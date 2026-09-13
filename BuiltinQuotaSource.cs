using AgentLimits.Collectors;

namespace AgentLimits;

/// <summary>
/// Wraps the existing collectors (Claude, Codex, z.ai, Antigravity, MiniMax).
/// Implements IQuotaSource so PluginHost doesn't need to know it's a C# class under
/// the hood rather than a Python script.
///
/// Each builtin source is its own IQuotaSource. This lets the user disable an
/// individual source through the approved_plugins/disable mechanism, and each
/// one gets its own timer/gate/timeout.
/// </summary>
internal sealed class BuiltinQuotaSource : IQuotaSource
{
    private readonly Func<AppConfig, CancellationToken, bool, Task<QuotaSnapshot?>> _refresh;

    public string Id { get; }
    public string DisplayName { get; }
    public TimeSpan Interval { get; }
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public bool IsBuiltin => true;
    public bool Approved => true;

    private BuiltinQuotaSource(
        string id, string displayName, TimeSpan interval,
        Func<AppConfig, CancellationToken, bool, Task<QuotaSnapshot?>> refresh)
    {
        Id = id;
        DisplayName = displayName;
        Interval = interval;
        _refresh = refresh;
    }

    public async Task<QuotaSnapshot?> RefreshAsync(CancellationToken ct, bool manual = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var snap = await _refresh(AppConfig.Load(), ct, manual);
            var status = snap?.SourceError is null ? "ok" : $"error {snap.SourceError}";
            Log.Info($"{Id}: {status}, {snap?.Blocks.Count ?? 0} blocks in {sw.Elapsed.TotalMilliseconds:0} ms");
            return snap;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"{Id}: refresh threw {ex.Message}"); return null; }
    }

    // ---------- factories for each source ----------

    /// <summary>Claude Code, two profiles (personal and work).</summary>
    public static IEnumerable<BuiltinQuotaSource> Claude(AppConfig cfg)
    {
        var claude = new ClaudeCollector();

        yield return MakeAggregate("claude", "Claude Code",
            TimeSpan.FromSeconds(Math.Max(120, cfg.ClaudeIntervalSec)),
            async (_, ct, _) =>
            {
                var personal = await claude.CollectAsync("personal", ct);
                var work = await claude.CollectAsync("work", ct);
                var blocks = new List<BlockData>();

                Append(blocks, "personal", "Claude · personal", personal.FiveHour, personal.Error);
                Append(blocks, "personal.7d", "Claude · personal", personal.SevenDay, personal.Error);

                Append(blocks, "work", "Claude · work", work.FiveHour, work.Error);
                Append(blocks, "work.7d", "Claude · work", work.SevenDay, work.Error);

                var err = personal.Error ?? work.Error;
                return new QuotaSnapshot("claude", blocks, err);
            });
    }

    /// <summary>z.ai / GLM.</summary>
    public static BuiltinQuotaSource Zai(AppConfig cfg)
    {
        var zai = new ZaiCollector();
        return new BuiltinQuotaSource("zai", "z.ai / GLM",
            TimeSpan.FromSeconds(Math.Max(60, cfg.ZaiIntervalSec)),
            async (c, ct, _) =>
            {
                var r = await zai.CollectAsync(c, ct);
                var blocks = new List<BlockData>();
                Append(blocks, "5h", "z.ai · GLM", r.FiveHour, r.Error);
                Append(blocks, "7d", "z.ai · GLM", r.Weekly, r.Error);
                return new QuotaSnapshot("zai", blocks, r.Error);
            });
    }

    /// <summary>Codex CLI.</summary>
    public static BuiltinQuotaSource Codex(AppConfig cfg)
    {
        var codex = new CodexCollector();
        return new BuiltinQuotaSource("codex", "Codex CLI",
            TimeSpan.FromSeconds(Math.Max(10, cfg.CodexIntervalSec)),
            async (_, ct, manual) =>
            {
                var r = await codex.CollectAsync(ct, manual);
                var blocks = new List<BlockData>();
                Append(blocks, "5h", "Codex", r.FiveHour, r.Error);
                Append(blocks, "7d", "Codex", r.Weekly, r.Error);
                return new QuotaSnapshot("codex", blocks, r.Error);
            });
    }

    /// <summary>Known Antigravity windows in a fixed display order (Gemini before
    /// Cld/GPT, 5h before weekly). The API returns buckets in a different order between
    /// calls — without an explicit order here, rows get created in whatever order their
    /// keys first appeared and never get reordered afterward: 5h/7d could
    /// "swap places" from one run to the next.</summary>
    private static readonly (string Id, string Prefix)[] AgyKnownBuckets =
    {
        ("gemini-5h", "Gemini"), ("gemini-weekly", "Gemini"),
        ("3p-5h", "Cld/GPT"), ("3p-weekly", "Cld/GPT"),
    };

    /// <summary>Antigravity (4 windows: gemini 5h/7d and 3p 5h/7d).</summary>
    public static BuiltinQuotaSource Agy(AppConfig cfg)
    {
        var agy = new AgyCollector();
        return new BuiltinQuotaSource("agy", "Antigravity",
            TimeSpan.FromSeconds(Math.Max(30, cfg.AgyIntervalSec)),
            async (_, ct, manual) =>
            {
                var r = await agy.CollectAsync(ct);
                if (manual && !string.IsNullOrEmpty(r.Error))
                    AgyCollector.OpenInteractiveTerminal();
                var byId = r.Buckets.ToDictionary(b => b.Id);
                var blocks = new List<BlockData>();

                foreach (var (id, prefix) in AgyKnownBuckets)
                {
                    if (byId.TryGetValue(id, out var b))
                    {
                        var win = b.Window;
                        blocks.Add(new BlockData(id, "Antigravity", prefix, win.WindowLabel,
                            win.RemainingPercent, win.ResetsAt, win.SampledAt, null));
                    }
                    else if (!string.IsNullOrEmpty(r.Error))
                    {
                        // The source failed entirely (network, CSRF, etc.) — show every
                        // known window as stale/error rather than staying silent: otherwise,
                        // on the very first poll after startup (before any success has ever
                        // happened), the whole Antigravity group wouldn't appear at all, the
                        // way Codex/z.ai used to behave without a token
                        // (see Append() below — same principle).
                        blocks.Add(new BlockData(id, "Antigravity", prefix, null,
                            null, null, null, r.Error));
                    }
                    // otherwise — the source answered successfully, but this particular
                    // window just wasn't in the response; RowRegistry decides what to do
                    // (for a builtin source that means Stale, not disappearing).
                }

                // Buckets with an id not in AgyKnownBuckets (Antigravity added a new
                // window) — don't drop them, just append at the end with no order guarantee.
                foreach (var b in r.Buckets.Where(b => AgyKnownBuckets.All(k => k.Id != b.Id)))
                {
                    var win = b.Window;
                    blocks.Add(new BlockData(b.Id, "Antigravity",
                        b.Id.StartsWith("3p-") ? "Cld/GPT" : "Gemini", win.WindowLabel,
                        win.RemainingPercent, win.ResetsAt, win.SampledAt, null));
                }

                return new QuotaSnapshot("agy", blocks, r.Error);
            });
    }

    /// <summary>MiniMax.</summary>
    public static BuiltinQuotaSource Minimax(AppConfig cfg)
    {
        var mm = new MinimaxCollector();
        return new BuiltinQuotaSource("minimax", "MiniMax",
            TimeSpan.FromSeconds(Math.Max(60, cfg.MinimaxIntervalSec)),
            async (c, ct, _) =>
            {
                var r = await mm.CollectAsync(c, ct);
                var blocks = new List<BlockData>();
                Append(blocks, "5h", "MiniMax", r.FiveHour, r.Error);
                Append(blocks, "7d", "MiniMax", r.Weekly, r.Error);
                return new QuotaSnapshot("minimax", blocks, r.Error);
            });
    }

    private static void Append(List<BlockData> blocks, string key, string group, LimitWindow? w, string? srcError)
    {
        if (w is null)
        {
            // No window in the plan AND no error — genuinely nothing to show.
            // If there is an error, add the block as stale so the user sees the reason
            // even if the source has never once answered successfully (token not set up,
            // the CLI never launched, etc.).
            if (string.IsNullOrEmpty(srcError)) return;
            blocks.Add(new BlockData(
                Key: key,
                Group: group,
                Prefix: "",
                // Suffix = key ("5h"/"7d"): without it Label is empty and the tooltip on this
                // row reads as ": <error>" — no way to tell which window it belongs to.
                Suffix: key,
                RemainingPercent: null,
                ResetsAt: null,
                SampledAt: null,
                Error: srcError));
            return;
        }
        var v = w.Value;

        blocks.Add(new BlockData(
            Key: key,
            Group: group,
            Prefix: "",
            Suffix: v.WindowLabel,
            RemainingPercent: v.RemainingPercent,
            ResetsAt: v.ResetsAt,
            SampledAt: v.SampledAt,
            Error: srcError));
    }

    /// <summary>Aggregator for Claude: one source covering both profiles, since historically
    /// this was one poll in the code. We keep it simple: one IQuotaSource = one group of rows.</summary>
    private static BuiltinQuotaSource MakeAggregate(string id, string name, TimeSpan interval,
        Func<AppConfig, CancellationToken, bool, Task<QuotaSnapshot?>> refresh)
        => new(id, name, interval, refresh);
}