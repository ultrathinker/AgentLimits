using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AgentLimits.Collectors;

/// <summary>
/// Codex CLI quota. There's no API of its own, but the server sends a rate_limits
/// snapshot with every response, and the CLI writes it into the session's rollout
/// log. We take the most recent snapshot.
///
/// Important: this is the state AS OF the last Codex call, not "right now".
/// That's why the UI shows the sample time — an old one means stale numbers.
/// </summary>
public sealed class CodexCollector
{
    private static readonly string SessionsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    public sealed record Result(LimitWindow? FiveHour, LimitWindow? Weekly, string? Plan, string? Error);

    /// <summary>Sent when a manual refresh can't help otherwise — a short, explicit instruction:
    /// one quick answer with no thinking, so we don't sit through long reasoning on a team plan.</summary>
    private const string PingPrompt =
        "Reply with exactly the single word hi and nothing else. " +
        "Do not think, do not explain, do not call any tools. Answer immediately.";

    /// <summary>
    /// manual=true (manual refresh, double-click): if the local cache has no window,
    /// allow one real (paid, if tiny) request to Codex, which
    /// makes it send fresh rate_limits. Background polling (manual=false) never
    /// does this — otherwise the "polling costs nothing" principle would break.
    /// </summary>
    public async Task<Result> CollectAsync(CancellationToken ct = default, bool manual = false)
    {
        var result = await Task.Run(() => Collect(ct), ct);

        var pinged = false;
        if (manual && result.Error is not null)
        {
            pinged = true;
            if (await TryPingAsync(ct))
                result = await Task.Run(() => Collect(ct), ct);
        }

        if (result.Error is null) return result;
        return result with
        {
            Error = result.Error + (pinged
                ? " — just pinged Codex, it still reports no window; try again later"
                : " — double-click to ping Codex and check again (real request, small cost)")
        };
    }

    /// <summary>Make Codex answer once so it writes fresh rate_limits
    /// into a new rollout file. cmd.exe is needed because codex on Windows is a .cmd shim (see AgyStatusline).</summary>
    private static async Task<bool> TryPingAsync(CancellationToken ct)
    {
        var command = "codex exec -s read-only --skip-git-repo-check -c model_reasoning_effort=low \""
                      + PingPrompt + "\"";

        Process? p = null;
        try
        {
            p = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c " + command)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath(),
            });
            if (p is null) return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(35));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
        finally
        {
            try { if (p is not null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p?.Dispose(); } catch { }
        }
    }

    private static Result Collect(CancellationToken ct)
    {
        if (!Directory.Exists(SessionsDir))
            return new Result(null, null, null, "no ~/.codex/sessions");

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(SessionsDir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(30)
                .ToList();
        }
        catch (Exception ex) { return new Result(null, null, null, ex.Message); }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var snap = LastRateLimitsIn(file.FullName);
            if (snap is null) continue;

            var sampled = file.LastWriteTime;

            // primary/secondary are NOT "5 hours/weekly": on the team plan, primary
            // carries the weekly window (window_minutes 10080) and secondary is empty.
            // So we sort by the window's actual length instead.
            LimitWindow? shortWin = null, longWin = null;
            foreach (var name in new[] { "primary", "secondary" })
            {
                var parsed = ReadWindow(snap.Value, name, sampled);
                if (parsed is null) continue;
                var (w, minutes) = parsed.Value;
                if (minutes is > 0 and <= 1440) shortWin ??= w;
                else longWin ??= w;
            }

            var plan = snap.Value.TryGetProperty("plan_type", out var pt) ? pt.GetString() : null;

            // Codex sent rate_limits but with no window in it — that is NOT "no data",
            // it's a real state (e.g. the team's shared workspace credits ran out,
            // rate_limit_reached_type = workspace_member_credits_depleted). Returning
            // Error: null here was a bug: for a builtin source, no Error and no blocks leaves
            // an already existing row "silently as is", and a row that was
            // never created yet (e.g. right after a restart) is not created
            // at all, although it must become something visible (at least a dimmed "—").
            if (shortWin is null && longWin is null)
            {
                var reason = snap.Value.TryGetProperty("rate_limit_reached_type", out var rrt) &&
                             rrt.ValueKind == JsonValueKind.String ? rrt.GetString() : null;
                var msg = reason switch
                {
                    "workspace_member_credits_depleted" =>
                        "your team's shared Codex credits ran out — no personal quota to fall back on, so Codex reports no percent at all",
                    not null => "Codex stopped reporting a window (" + reason.Replace('_', ' ') + ")",
                    null => "Codex's rate-limit report has no window data — try again after your next Codex request"
                };
                return new Result(null, null, plan, msg);
            }

            return new Result(shortWin, longWin, plan, null);
        }
        return new Result(null, null, null, "no rate-limit snapshot found");
    }

    private static JsonElement? LastRateLimitsIn(string path)
    {
        JsonElement? best = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (line.IndexOf("rate_limit", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    var doc = JsonDocument.Parse(line);
                    var found = FindRateLimits(doc.RootElement);
                    // The last one in the file is the most recent; clone since doc gets disposed.
                    if (found is not null) best = found.Value.Clone();
                }
                catch { /* line isn't JSON — skip it */ }
            }
        }
        catch { return null; }
        return best;
    }

    private static JsonElement? FindRateLimits(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.NameEquals("rate_limits") && p.Value.ValueKind == JsonValueKind.Object)
                        return p.Value;
                    var r = FindRateLimits(p.Value);
                    if (r is not null) return r;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var r = FindRateLimits(item);
                    if (r is not null) return r;
                }
                break;
        }
        return null;
    }

    private static (LimitWindow Window, int? Minutes)? ReadWindow(JsonElement rl, string name, DateTime sampled)
    {
        if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("used_percent", out var up)) return null;

        var used = up.ValueKind == JsonValueKind.Number ? up.GetDouble() : 0;

        DateTime? reset = null;
        if (w.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
            reset = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()).LocalDateTime;

        int? minutes = w.TryGetProperty("window_minutes", out var wm) && wm.ValueKind == JsonValueKind.Number
            ? wm.GetInt32()
            : null;

        return (new LimitWindow(Math.Clamp(100 - used, 0, 100), reset, sampled, FormatWindow(minutes)), minutes);
    }

    private static string? FormatWindow(int? minutes) => minutes switch
    {
        null or <= 0 => null,
        < 60 => $"{minutes}m",
        < 1440 => $"{minutes / 60}h",
        10080 => "7d",           // labeled the same way as the other sources
        _ => $"{minutes / 1440}d"
    };
}
