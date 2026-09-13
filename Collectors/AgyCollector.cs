using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AgentLimits.Collectors;

/// <summary>
/// Antigravity CLI (agy) quota. Two sources, in priority order.
///
/// 1. The statusline snapshot (primary, sanctioned). agy itself invokes the
///    command configured as statusLine.command in its settings.json and feeds
///    it a JSON payload on stdin that contains a quota object. Our own exe
///    plays the role of that command (see AgyStatusline); it saves the
///    payload, and we read it from there. The data keeps refreshing while an
///    interactive agy session is open — in print mode (-p) statusline is
///    never invoked at all, verified directly.
///
/// 2. The private RetrieveUserQuotaSummary RPC on localhost (legacy path).
///    The CLI used to bring up a language server without --csrf_token and let
///    anyone in. As of some agy update it now returns 401 "missing CSRF
///    token" — both for the user's own running session and for a self-spawned
///    `agy models` (verified with curl on both ports, https and http). There's
///    nowhere to get the token from externally, so this path is kept only for
///    older agy versions.
///
/// GetUserStatus doesn't work for quota: quotaInfo only carries the 5-hour
/// window for each model, there's no weekly window in there.
/// </summary>
public sealed class AgyCollector
{
    private const string RpcPath = "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        // The server presents a self-signed certificate on 127.0.0.1 — trust it only there.
        ServerCertificateCustomValidationCallback = (msg, _, _, _) =>
            msg.RequestUri?.IsLoopback == true
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    public string? ExePath { get; init; } = FindExe();

    /// <param name="Id">Stable window identifier: gemini-weekly, gemini-5h, 3p-weekly, 3p-5h.</param>
    public sealed record Bucket(string Id, LimitWindow Window);

    public sealed record Result(IReadOnlyList<Bucket> Buckets, string? Error);

    private static readonly Result Empty = new([], null);

    /// <summary>What to say when there's no statusline snapshot yet and the RPC is closed by CSRF (401 on
    /// a working endpoint; the token can't be obtained externally). We stop at the first 401 without
    /// trying other schemes and ports — otherwise we spam agy's own console.</summary>
    private const string NoSourceError = "no snapshot yet — open agy in a terminal once (RPC is closed by CSRF), or double-click to open one";

    public async Task<Result> CollectAsync(CancellationToken ct = default)
    {
        // 0. The snapshot from agy's statusline — the sanctioned channel now that the
        //    private RPC is closed by CSRF. Written by AgyStatusline (our own exe,
        //    configured in agy's settings.json as statusLine.command).
        var snapshot = ReadStatuslineCache();
        // No hidden `agy models` here: it does render the statusline, but without
        // a quota object (verified), so it can't refresh the snapshot and
        // only held the poll for up to 12 s. Only a real chat in agy brings the quota.
        if (snapshot is not null) return snapshot;

        // 1. An already-running agy (the user's interactive session) — a free source.
        foreach (var proc in SafeProcesses("agy"))
        {
            foreach (var port in TcpTable.ListeningPorts(proc.Id))
            {
                var (json, authRejected) = await TryPortAsync(port, ct);
                if (json is not null) return Parse(json);
                if (authRejected) return Empty with { Error = NoSourceError };
            }
        }

        // 2. Otherwise, spawn our own for a couple of seconds.
        if (ExePath is null || !File.Exists(ExePath))
            return Empty with { Error = "agy.exe not found" };

        Process? spawned = null;
        try
        {
            spawned = Process.Start(new ProcessStartInfo(ExePath, "models")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath()
            });
            if (spawned is null) return Empty with { Error = "could not start agy" };

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                foreach (var port in TcpTable.ListeningPorts(spawned.Id))
                {
                    var (json, authRejected) = await TryPortAsync(port, ct);
                    if (json is not null) return Parse(json);
                    if (authRejected) return Empty with { Error = NoSourceError };
                }
                if (spawned.HasExited) break;
                await Task.Delay(200, ct);
            }
            return Empty with { Error = "agy did not open a port in time" };
        }
        catch (Exception ex)
        {
            return Empty with { Error = ex.Message };
        }
        finally
        {
            TryKill(spawned);
        }
    }

    /// <summary>A snapshot exists and isn't marked stale (see Error on Result).</summary>
    private static bool IsFresh(Result? r) => r is { Error: null, Buckets.Count: > 0 };

    /// <summary>
    /// Open a real, visible terminal window with an interactive agy session — on a
    /// double-click on an Antigravity row. Unlike Codex (which has a clean
    /// one-shot exec mode), agy has no safe headless way to reliably render
    /// the statusline: `-p` never triggers it, and `-i` keeps the session
    /// open. So we simply hand control to the user in a real terminal,
    /// with no hidden PTY automation.
    /// </summary>
    public static void OpenInteractiveTerminal()
    {
        var exe = FindExe();
        if (exe is null)
        {
            // Otherwise cmd /k would sit on screen with "'agy' is not recognized".
            Log.Warn("agy: not opening a terminal, agy.exe not found");
            return;
        }
        // cmd.exe /k takes the rest of the command line as is and, if it starts
        // and ends with a quote, strips exactly one quote from each
        // end (assuming it's an "extra" outer layer) — with our own two
        // pairs of quotes (exe path and prompt) that breaks the command. We wrap
        // everything in an extra outer pair so that this layer is what gets stripped.
        var inner = $"\"{exe}\" -i \"just say 'hi', that's all\"";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"{inner}\"")
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
            Log.Info($"agy: opened interactive terminal (exe={exe})");
        }
        catch (Exception ex) { Log.Warn($"agy: could not open terminal ({ex.Message})"); }
    }

    /// <summary>
    /// Parse the statusline payload snapshot. The quota object's keys vary between
    /// agy versions — sometimes a bucketId (gemini-5h), sometimes a model's display
    /// name; fields are sometimes snake_case, sometimes camelCase — so parsing is
    /// tolerant: the family is inferred from the key name, the window length from
    /// the name too, and if that's not said, from the reset horizon.
    /// null means there's no snapshot (let the caller fall back to the RPC).
    /// </summary>
    private static Result? ReadStatuslineCache()
    {
        string json;
        DateTime sampledAt;
        try
        {
            var path = AgyStatusline.CachePath;
            if (!File.Exists(path)) return null;
            sampledAt = File.GetLastWriteTime(path);
            json = File.ReadAllText(path);
        }
        catch { return null; }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("quota", out var quota) || quota.ValueKind != JsonValueKind.Object)
                return Empty with { Error = "agy hasn't reported quota yet — chat in agy once (agy models / idle renders don't carry it), or double-click to open one" };

            var buckets = new List<Bucket>();
            foreach (var entry in quota.EnumerateObject())
            {
                var fraction = Num(entry.Value, "remaining_fraction", "remainingFraction", "remaining");
                if (fraction is null) continue;

                var resetIn = Num(entry.Value, "reset_in_seconds", "resetInSeconds");
                var reset = Time(entry.Value, "reset_time", "resetTime")
                            ?? (resetIn is not null ? sampledAt.AddSeconds(resetIn.Value) : null);

                var name = (entry.Name + " " + (Str(entry.Value, "display_name", "displayName", "model") ?? ""))
                    .ToLowerInvariant();

                var weekly = name.Contains("week") || name.Contains("7d")
                             || (!name.Contains("5h") && !name.Contains("five")
                                 && (reset - sampledAt) > TimeSpan.FromHours(36));

                var family = name.Contains("gemini") ? "gemini" : "3p";
                var id = $"{family}-{(weekly ? "weekly" : "5h")}";
                if (buckets.Any(b => b.Id == id)) continue;   // first one wins, no duplicates

                buckets.Add(new Bucket(id,
                    new LimitWindow(Math.Clamp(fraction.Value * 100.0, 0, 100), reset, sampledAt,
                        weekly ? "7d" : "5h")));
            }

            if (buckets.Count == 0)
                return Empty with { Error = "statusline quota is empty" };

            // While an agy session stays open, the quota in the payload keeps refreshing
            // itself. If the snapshot is old, we still show the numbers, but dimmed and with a reason.
            var age = DateTime.Now - sampledAt;
            var stale = age > AgyStatusline.FreshFor
                ? $"statusline snapshot is {Age(age)} old — open agy to refresh, or double-click to open one"
                : null;
            return new Result(buckets, stale);
        }
        catch (Exception ex)
        {
            return Empty with { Error = "bad statusline snapshot: " + ex.Message };
        }
    }

    private static double? Num(JsonElement o, params string[] names)
    {
        foreach (var n in names)
            if (o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
        return null;
    }

    private static string? Str(JsonElement o, params string[] names)
    {
        foreach (var n in names)
            if (o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static DateTime? Time(JsonElement o, params string[] names)
    {
        var s = Str(o, names);
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt.ToLocalTime()
            : null;
    }

    private static string Age(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalMinutes}m";

    private static async Task<(string? Json, bool AuthRejected)> TryPortAsync(int port, CancellationToken ct)
    {
        // agy (LanguageServerService) always listens over HTTPS — try https first,
        // so we don't send plaintext HTTP to a TLS server and spam its console with handshake errors.
        foreach (var scheme in new[] { "https", "http" })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{scheme}://127.0.0.1:{port}{RpcPath}")
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                using var resp = await Http.SendAsync(req, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (null, true);
                if (!resp.IsSuccessStatusCode) continue;
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (body.Contains("remainingFraction")) return (body, false);
            }
            catch { /* wrong port/scheme — move on */ }
        }
        return (null, false);
    }

    private static Result Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var resp) ||
                !resp.TryGetProperty("groups", out var groups))
                return Empty with { Error = "unexpected response" };

            var buckets = new List<Bucket>();
            foreach (var group in groups.EnumerateArray())
            {
                if (!group.TryGetProperty("buckets", out var list)) continue;
                foreach (var b in list.EnumerateArray())
                {
                    if (!b.TryGetProperty("bucketId", out var idEl)) continue;
                    if (!b.TryGetProperty("remainingFraction", out var rf)) continue;

                    var id = idEl.GetString();
                    if (string.IsNullOrEmpty(id) || buckets.Any(x => x.Id == id)) continue;

                    DateTime? reset = b.TryGetProperty("resetTime", out var rt) && rt.GetString() is string s &&
                                      DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
                        ? dt.ToLocalTime()
                        : null;

                    var label = b.TryGetProperty("window", out var w) && w.GetString() == "weekly" ? "7d" : "5h";
                    buckets.Add(new Bucket(id, new LimitWindow(rf.GetDouble() * 100.0, reset, DateTime.Now, label)));
                }
            }

            return buckets.Count == 0
                ? Empty with { Error = "no quota in response" }
                : new Result(buckets, null);
        }
        catch (Exception ex)
        {
            return Empty with { Error = "parse error: " + ex.Message };
        }
    }

    private static IEnumerable<Process> SafeProcesses(string name)
    {
        try { return Process.GetProcessesByName(name); }
        catch { return []; }
    }

    private static void TryKill(Process? p)
    {
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
    }

    private static string? FindExe()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");
        if (File.Exists(local)) return local;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), "agy.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }
}
