using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AgentLimits;

/// <summary>
/// "Statusline bridge for Antigravity CLI" mode.
///
/// Why: agy's private language-server RPC (RetrieveUserQuotaSummary) started
/// returning 401 "missing CSRF token" as of some update — both for the user's
/// own running session and for a self-spawned `agy models` (verified manually).
/// There's nowhere to get the token from externally. The sanctioned channel for
/// the same numbers is statusline: agy itself invokes the command configured in
/// its settings.json and feeds it a JSON payload on stdin containing a quota object.
///
/// So AgentLimits.exe can be launched as that command:
///   AgentLimits.exe --agy-statusline ["the user's original command"]
/// It saves the payload to agy-statusline.json (read by AgyCollector) and, if a
/// second argument is given, transparently forwards the same stdin into it and
/// prints its stdout as its own — so the user's own statusline keeps working.
/// </summary>
internal static class AgyStatusline
{
    public const string Flag = "--agy-statusline";

    /// <summary>Freshness window: agy refreshes the quota itself while a session stays open.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(30);

    public static string CachePath => Path.Combine(AppConfig.Dir, "agy-statusline.json");

    /// <summary>Run statusline mode if requested. true means the process should exit now.</summary>
    public static bool TryRun(string[] args)
    {
        var idx = Array.FindIndex(args, a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;

        var payload = "";
        try { payload = Console.In.ReadToEnd(); } catch { }

        try { Save(payload); } catch { }

        // Anything after the flag is the user's command (agy passes it as one string).
        var chain = idx + 1 < args.Length ? args[idx + 1] : null;
        try { Console.Out.Write(Chain(chain, payload) ?? ""); } catch { }
        return true;
    }

    private static void Save(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return;

        // Only ever save a real JSON object: if some other tool's payload ends up
        // here by accident, keeping the previous snapshot beats corrupting it.
        using (var doc = JsonDocument.Parse(payload))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            // agy fires the statusline several times in a row, and not every render carries
            // the quota: on "initializing" the quota object is there but every window is zero; on
            // "authenticating" (session re-auth) there is no quota object at all. Both
            // mean "quota unavailable right now", and neither may be confused with "quota = 0":
            // such a render must never overwrite live numbers — better to keep showing the previous ones.
            if (!HasUsableQuota(doc.RootElement) && File.Exists(CachePath))
            {
                try
                {
                    using var old = JsonDocument.Parse(File.ReadAllText(CachePath));
                    if (HasUsableQuota(old.RootElement)) return;
                }
                catch { /* previous snapshot doesn't parse — nothing to lose */ }
            }
        }

        Directory.CreateDirectory(AppConfig.Dir);
        var tmp = CachePath + ".tmp";
        File.WriteAllText(tmp, payload, new UTF8Encoding(false));
        File.Move(tmp, CachePath, overwrite: true);
    }

    /// <summary>
    /// At least one quota window is non-zero. false both when there is no quota object
    /// at all (agy hasn't loaded it yet or is re-authenticating) and when it is there
    /// but every remaining_fraction is zero (the first, "initializing" render).
    /// </summary>
    private static bool HasUsableQuota(JsonElement root)
    {
        if (!root.TryGetProperty("quota", out var quota) || quota.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var b in quota.EnumerateObject())
        {
            if (b.Value.ValueKind != JsonValueKind.Object) continue;
            if (!b.Value.TryGetProperty("remaining_fraction", out var f) || f.ValueKind != JsonValueKind.Number)
                continue;
            if (f.GetDouble() > 0) return true;
        }
        return false;
    }

    /// <summary>Run the user's command, feeding it the same stdin, and return its stdout.</summary>
    private static string? Chain(string? command, string payload)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        Process? p = null;
        try
        {
            p = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c " + command)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            });
            if (p is null) return null;

            p.StandardInput.Write(payload);
            p.StandardInput.Close();

            var stdout = p.StandardOutput.ReadToEndAsync();
            // Drain stderr too: otherwise a chatty user command fills the pipe buffer,
            // hangs, and gets killed by the timeout.
            _ = p.StandardError.ReadToEndAsync();
            // agy's own statusline budget is ~5-10s; we budget for less so we don't
            // get killed ourselves as a hung script.
            if (!p.WaitForExit(4000)) { try { p.Kill(entireProcessTree: true); } catch { } return null; }
            return stdout.Wait(1000) ? stdout.Result : null;
        }
        catch { return null; }
        finally { try { p?.Dispose(); } catch { } }
    }
}
