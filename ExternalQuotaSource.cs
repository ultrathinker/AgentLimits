using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AgentLimits;

/// <summary>
/// Polls an external plugin: spawns `py main.py`, reads JSON from stdout,
/// validates it, wraps it in a QuotaSnapshot. Enforces a timeout and kills
/// the process along with any subprocesses it spawned.
///
/// Cumulative-time check: if the average of the last 5 runs exceeds the
/// interval, the next run is skipped (no Process.Start at all), so we don't
/// hammer the source.
/// </summary>
internal sealed class ExternalQuotaSource : IQuotaSource
{
    private readonly PluginManifest _manifest;
    private readonly string _folder;
    private readonly TimeSpan _timeout;

    /// <summary>Ring buffer of the last 5 run durations.</summary>
    private readonly Queue<double> _recentSeconds = new();

    public string Id => _manifest.Id!;
    public string DisplayName => $"plugin: {_manifest.Id}";
    public TimeSpan Interval { get; }
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public bool IsBuiltin => false;
    public bool Approved { get; internal set; }

    /// <summary>Next time a run is allowed (UTC). Default = right away.</summary>
    public DateTime NextAllowedUtc { get; private set; } = DateTime.UtcNow;

    public ExternalQuotaSource(PluginManifest manifest, string folder, TimeSpan timeout, bool approved)
    {
        _manifest = manifest;
        _folder = folder;
        _timeout = timeout;
        Approved = approved;
        var sec = manifest.IntervalSec ?? 120;
        Interval = TimeSpan.FromSeconds(sec);
    }

    public async Task<QuotaSnapshot?> RefreshAsync(CancellationToken ct, bool manual = false)
    {
        if (!Approved)
        {
            // Return an empty snapshot with a marker — the UI will show "click to approve".
            return new QuotaSnapshot(
                Id,
                Array.Empty<BlockData>(),
                $"plugin '{Id}' is awaiting approval",
                IsSoft: true);
        }

        // Cumulative-time skip: if past runs average longer than the interval,
        // skip the next one.
        if (DateTime.UtcNow < NextAllowedUtc)
        {
            var wait = NextAllowedUtc - DateTime.UtcNow;
            Log.Info($"{Id}: skipped (cumulative-time throttle, {wait.TotalSeconds:0}s left)");
            return new QuotaSnapshot(
                Id,
                Array.Empty<BlockData>(),
                $"throttled ({wait.TotalSeconds:0}s)",
                IsSoft: true);
        }

        var scriptPath = Path.Combine(_folder, _manifest.Script ?? "main.py");
        if (!File.Exists(scriptPath))
        {
            Log.Warn($"{Id}: script not found: {scriptPath}");
            return new QuotaSnapshot(Id, Array.Empty<BlockData>(), "script not found", IsSoft: true);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            return await RunAsync(scriptPath, ct);
        }
        finally
        {
            sw.Stop();
            var secs = sw.Elapsed.TotalSeconds;
            TrackDuration(secs);
            // If a single run took longer than half the interval, throttle the next one
            if (secs > Interval.TotalSeconds / 2.0)
                NextAllowedUtc = DateTime.UtcNow.AddSeconds(secs);
        }
    }

    private void TrackDuration(double seconds)
    {
        _recentSeconds.Enqueue(seconds);
        while (_recentSeconds.Count > RowRegistry.RollingWindow)
            _recentSeconds.Dequeue();

        if (_recentSeconds.Count == RowRegistry.RollingWindow)
        {
            var avg = _recentSeconds.Average();
            if (avg > Interval.TotalSeconds)
            {
                // Average exceeds the interval — push the next run back by (avg - interval)
                NextAllowedUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, avg - Interval.TotalSeconds));
                Log.Warn($"{Id}: cumulative-time throttle, avg={avg:0.0}s > interval={Interval.TotalSeconds:0}s");
            }
        }
    }

    private async Task<QuotaSnapshot?> RunAsync(string scriptPath, CancellationToken ct)
    {
        var interpreter = ResolveInterpreter();
        if (interpreter is null)
        {
            Log.Warn($"{Id}: python not found (tried py/python/python3)");
            return new QuotaSnapshot(Id, Array.Empty<BlockData>(), "python not found (set PythonPath in config)");
        }

        var psi = new ProcessStartInfo(interpreter, $"\"{scriptPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _folder,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Standard environment variables
        psi.Environment["AGENTLIMITS_VERSION"] = "1.0.0";
        psi.Environment["AGENTLIMITS_PLUGIN_ID"] = Id;
        psi.Environment["AGENTLIMITS_INTERVAL_SEC"] = ((int)Interval.TotalSeconds).ToString();

        // Builtin source tokens
        var cfg = AppConfig.Load();
        if (!string.IsNullOrEmpty(cfg.ZaiToken))
            psi.Environment["AGENTLIMITS_TOKEN_ZAI"] = cfg.ZaiToken;
        if (!string.IsNullOrEmpty(cfg.MinimaxToken))
            psi.Environment["AGENTLIMITS_TOKEN_MINIMAX"] = cfg.MinimaxToken;

        // User-supplied env from the manifest
        if (_manifest.Env is { } env)
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
            {
                Log.Warn($"{Id}: process start returned null");
                return new QuotaSnapshot(Id, Array.Empty<BlockData>(), "could not start plugin", IsSoft: true);
            }

            using var ctsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ctsTimeout.CancelAfter(_timeout);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ctsTimeout.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(ctsTimeout.Token);

            bool exited;
            try
            {
                exited = await Task.Run(() => proc.WaitForExit(_timeout), ctsTimeout.Token);
            }
            catch (OperationCanceledException) { exited = false; }

            if (!exited)
            {
                TryKill(proc);
                Log.Warn($"{Id}: timeout after {_timeout.TotalSeconds:0}s");
                return new QuotaSnapshot(Id, Array.Empty<BlockData>(), $"timeout after {(int)_timeout.TotalSeconds}s", IsSoft: true);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            // stderr goes into the log
            if (!string.IsNullOrWhiteSpace(stderr))
                foreach (var line in stderr.Split('\n'))
                    Log.Info($"{Id}: {line.TrimEnd()}");

            if (proc.ExitCode != 0)
            {
                var first = stderr.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
                return new QuotaSnapshot(Id, Array.Empty<BlockData>(), $"exit {proc.ExitCode}: {first}", IsSoft: true);
            }

            return ParseStdout(stdout);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(proc);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(proc);
            Log.Warn($"{Id}: refresh failed ({ex.Message})");
            return new QuotaSnapshot(Id, Array.Empty<BlockData>(), ex.Message, IsSoft: true);
        }
    }

    private QuotaSnapshot? ParseStdout(string stdout)
    {
        var trimmed = stdout.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return new QuotaSnapshot(Id, Array.Empty<BlockData>(), "empty stdout", IsSoft: true);

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            if (!root.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.Number || ver.GetInt32() != 1)
                return new QuotaSnapshot(Id, Array.Empty<BlockData>(), "unsupported version", IsSoft: true);

            if (!root.TryGetProperty("blocks", out var blocksEl) || blocksEl.ValueKind != JsonValueKind.Array)
                return new QuotaSnapshot(Id, Array.Empty<BlockData>(), "missing blocks", IsSoft: true);

            var blocks = new List<BlockData>();
            foreach (var b in blocksEl.EnumerateArray())
            {
                if (!b.TryGetProperty("key", out var keyEl) || keyEl.ValueKind != JsonValueKind.String)
                    continue;
                if (!b.TryGetProperty("group", out var groupEl) || groupEl.ValueKind != JsonValueKind.String)
                    continue;

                var key = keyEl.GetString()!;
                var group = groupEl.GetString()!;

                var prefix = b.TryGetProperty("prefix", out var pEl) && pEl.ValueKind == JsonValueKind.String
                    ? pEl.GetString() : null;
                var suffix = b.TryGetProperty("suffix", out var sEl) && sEl.ValueKind == JsonValueKind.String
                    ? sEl.GetString() : null;
                var error = b.TryGetProperty("error", out var eEl) && eEl.ValueKind == JsonValueKind.String
                    ? eEl.GetString() : null;

                double? pct = null;
                if (b.TryGetProperty("remaining_percent", out var pctEl) && pctEl.ValueKind == JsonValueKind.Number)
                {
                    var d = pctEl.GetDouble();
                    if (!double.IsNaN(d) && !double.IsInfinity(d))
                        pct = Math.Clamp(d, 0, 100);
                }

                DateTime? resets = null, sampled = null;
                if (b.TryGetProperty("resets_at", out var rEl) && rEl.ValueKind == JsonValueKind.String)
                    resets = ParseIsoLocal(rEl.GetString());
                if (b.TryGetProperty("sampled_at", out var sdEl) && sdEl.ValueKind == JsonValueKind.String)
                    sampled = ParseIsoLocal(sdEl.GetString());

                blocks.Add(new BlockData(key, group, prefix, suffix, pct, resets, sampled, error));
            }

            return new QuotaSnapshot(Id, blocks, null, IsSoft: true);
        }
        catch (JsonException ex)
        {
            Log.Warn($"{Id}: invalid JSON ({ex.Message})");
            return new QuotaSnapshot(Id, Array.Empty<BlockData>(), "invalid JSON", IsSoft: true);
        }
    }

    private static DateTime? ParseIsoLocal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
            return dt;
        return null;
    }

    /// <summary>Find a working python: first the one from config, then fall back through a list.
    /// On Windows, miniconda/Anaconda install python.exe but not the py-launcher.</summary>
    private static string? _interpreter;
    private static string? _interpreterFor;

    private static string? ResolveInterpreter()
    {
        var cfg = AppConfig.Load();
        var configured = cfg.PythonPath ?? "";
        // The answer doesn't change between polls — without a cache every poll of every plugin
        // spawned up to four `--version` processes. Re-check only when PythonPath changes.
        if (_interpreter is not null && _interpreterFor == configured) return _interpreter;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(cfg.PythonPath)) candidates.Add(cfg.PythonPath);
        candidates.AddRange(new[] { "py", "python", "python3", "python.exe" });

        foreach (var c in candidates)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = c,
                    Arguments = "--version",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (p is null) continue;
                if (p.WaitForExit(2000) && p.ExitCode == 0)
                {
                    _interpreterFor = configured;
                    return _interpreter = c;
                }
            }
            catch { /* not found, keep trying */ }
        }
        return null;
    }

    private static void TryKill(Process? p)
    {
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
    }
}