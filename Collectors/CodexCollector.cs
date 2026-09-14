using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
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

    private const int CandidateFiles = 30;
    private const long FirstReadTailBytes = 1 << 20;
    private const long SameWindowToleranceSec = 600;

    /// <summary>What has already been read from a session file: files are append-only, so each
    /// poll reads only the new tail instead of megabytes all over again.</summary>
    private sealed class FileState
    {
        public bool Seen;
        public long Offset;
        public DateTimeOffset LastTs = DateTimeOffset.MinValue;
        public JsonElement? Last;
    }

    /// <summary>The highest usage seen for a specific window (length + reset time).</summary>
    private readonly record struct Peak(int Minutes, long ResetsAt, double Used);

    private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Peak> _peaks = [];

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

    private Result Collect(CancellationToken ct)
    {
        if (!Directory.Exists(SessionsDir))
            return new Result(null, null, null, "no ~/.codex/sessions");

        List<FileInfo> files;
        try
        {
            // The file's modified date can't be trusted: while Codex keeps a session open, Windows
            // doesn't update LastWriteTime for hours (seen: 08:15 while it was written at 10:16). So
            // we take candidates with a margin and decide freshness by the events' own timestamps.
            files = new DirectoryInfo(SessionsDir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc > f.CreationTimeUtc ? f.LastWriteTimeUtc : f.CreationTimeUtc)
                .Take(CandidateFiles)
                .ToList();
        }
        catch (Exception ex) { return new Result(null, null, null, ex.Message); }

        var current = new HashSet<string>(files.Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _files.Keys.Where(k => !current.Contains(k)).ToList())
            _files.Remove(gone);
        _peaks.RemoveAll(p => p.ResetsAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 86400);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!_files.TryGetValue(file.FullName, out var state))
                _files[file.FullName] = state = new FileState();
            ReadNewLines(file.FullName, state);
        }

        var latest = _files.Values.Where(s => s.Last is not null).MaxBy(s => s.LastTs);
        if (latest is null)
            return new Result(null, null, null, "no rate-limit snapshot found");

        {
            var snap = latest.Last;
            var sampled = latest.LastTs.LocalDateTime;

            // primary/secondary are NOT "5 hours/weekly": on the team plan, primary
            // carries the weekly window (window_minutes 10080) and secondary is empty.
            // So we sort by the window's actual length instead.
            LimitWindow? shortWin = null, longWin = null;
            foreach (var name in new[] { "primary", "secondary" })
            {
                var parsed = ReadWindow(snap!.Value, name, sampled);
                if (parsed is null) continue;
                var (w, minutes) = parsed.Value;
                if (minutes is > 0 and <= 1440) shortWin ??= w;
                else longWin ??= w;
            }

            var plan = snap!.Value.TryGetProperty("plan_type", out var pt) ? pt.GetString() : null;

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
    }

    /// <summary>Read a session file from where we stopped. On first contact, only
    /// the last megabyte: we only need the latest snapshot, and sessions can reach 20 MB.
    /// An incomplete last line (Codex is still writing it) is left for the next poll.</summary>
    private void ReadNewLines(string path, FileState state)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            if (state.Seen && length < state.Offset)
            {
                state.Seen = false;
                state.Last = null;
                state.LastTs = DateTimeOffset.MinValue;
            }

            var first = !state.Seen;
            var start = first ? Math.Max(0, length - FirstReadTailBytes) : state.Offset;
            state.Seen = true;
            if (length <= start) { state.Offset = start; return; }

            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[length - start];
            var read = 0;
            while (read < buf.Length)
            {
                var n = fs.Read(buf, read, buf.Length - read);
                if (n == 0) break;
                read += n;
            }

            var end = read == 0 ? -1 : Array.LastIndexOf(buf, (byte)'\n', read - 1);
            if (end < 0) { state.Offset = start; return; }

            var pos = 0;
            if (first && start > 0) pos = Array.IndexOf(buf, (byte)'\n', 0, end + 1) + 1;
            while (pos <= end)
            {
                var nl = Array.IndexOf(buf, (byte)'\n', pos, end - pos + 1);
                ProcessLine(Encoding.UTF8.GetString(buf, pos, nl - pos), state);
                pos = nl + 1;
            }
            state.Offset = start + end + 1;
        }
        catch { /* file busy or deleted — try again on the next poll */ }
    }

    private void ProcessLine(string line, FileState state)
    {
        if (line.IndexOf("rate_limit", StringComparison.OrdinalIgnoreCase) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var found = FindRateLimits(doc.RootElement);
            if (found is null) return;
            var rl = found.Value.Clone();

            var ts = doc.RootElement.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String &&
                     DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : state.LastTs;
            if (ts >= state.LastTs)
            {
                state.Last = rl;
                state.LastTs = ts;
            }
            RecordPeaks(rl);
        }
        catch { /* line isn't JSON — skip it */ }
    }

    /// <summary>Within one window usage only grows. Yet Codex sometimes writes a snapshot with
    /// used_percent 0.0 for the same window (at the start of a new session, rarely mid-session) —
    /// on top of 59 % that produced "100 % left". We remember the maximum per window and don't let
    /// such a snapshot lower it; with a new resets_at the window is new and the maximum starts over.</summary>
    private void RecordPeaks(JsonElement rl)
    {
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            if (!TryWindowKey(w, out var minutes, out var resets)) continue;
            if (!w.TryGetProperty("used_percent", out var up) || up.ValueKind != JsonValueKind.Number) continue;

            var used = up.GetDouble();
            var i = _peaks.FindIndex(p => p.Minutes == minutes && Math.Abs(p.ResetsAt - resets) <= SameWindowToleranceSec);
            if (i < 0) _peaks.Add(new Peak(minutes, resets, used));
            else if (used > _peaks[i].Used) _peaks[i] = _peaks[i] with { Used = used };
        }
    }

    private double PeakUsed(int minutes, long resets) =>
        _peaks.Where(p => p.Minutes == minutes && Math.Abs(p.ResetsAt - resets) <= SameWindowToleranceSec)
              .Select(p => p.Used)
              .DefaultIfEmpty(0)
              .Max();

    private static bool TryWindowKey(JsonElement w, out int minutes, out long resets)
    {
        minutes = 0;
        resets = 0;
        if (!w.TryGetProperty("window_minutes", out var wm) || wm.ValueKind != JsonValueKind.Number) return false;
        if (!w.TryGetProperty("resets_at", out var ra) || ra.ValueKind != JsonValueKind.Number) return false;
        minutes = wm.GetInt32();
        resets = ra.GetInt64();
        return true;
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

    private (LimitWindow Window, int? Minutes)? ReadWindow(JsonElement rl, string name, DateTime sampled)
    {
        if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("used_percent", out var up)) return null;

        var used = up.ValueKind == JsonValueKind.Number ? up.GetDouble() : 0;
        if (TryWindowKey(w, out var windowMinutes, out var windowResets))
            used = Math.Max(used, PeakUsed(windowMinutes, windowResets));

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
