using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AgentLimits;

/// <summary>
/// Dispatcher for every IQuotaSource (builtin and plugins).
///
/// One cycle: BeginCycle() -> RefreshAsync(source) for each -> Apply(snapshot) into RowRegistry.
/// Timers are per-source, honoring each source's interval. Gates are per-source too, so a
/// hung plugin can't block a builtin source.
///
/// Disabled plugins are never polled. Approved=false plugins return an empty
/// snapshot with a marker — the UI shows "click to approve".
/// </summary>
public sealed class PluginHost
{
    private readonly RowRegistry _registry;
    private readonly AppConfig _cfg;
    private readonly Dispatcher _ui;
    private readonly List<IQuotaSource> _sources = new();
    private readonly Dictionary<string, DispatcherTimer> _timers = new();
    private bool _started;

    public IReadOnlyList<IQuotaSource> Sources => _sources;
    public event Action? AfterRefresh;

    public PluginHost(RowRegistry registry, AppConfig cfg, Dispatcher ui)
    {
        _registry = registry;
        _cfg = cfg;
        _ui = ui;
    }

    /// <summary>Register builtin sources. Call before Discover().</summary>
    public void RegisterBuiltin(IEnumerable<IQuotaSource> sources)
    {
        foreach (var s in sources) _sources.Add(s);
    }

    /// <summary>Scan the plugins folder and register external sources.</summary>
    public int DiscoverPlugins(string? folderName = null)
    {
        folderName ??= _cfg.PluginsDir;
        var dir = Path.Combine(AppConfig.Dir, folderName);
        if (!Directory.Exists(dir)) return 0;

        var disabled = new HashSet<string>(_cfg.DisabledPlugins, StringComparer.Ordinal);
        var approved = new HashSet<string>(_cfg.ApprovedPlugins, StringComparer.Ordinal);

        var added = 0;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var folderNameOnly = Path.GetFileName(sub);
            if (folderNameOnly.StartsWith("_") || folderNameOnly.StartsWith(".")) continue;

            if (disabled.Contains(folderNameOnly)) continue;

            PluginManifest? manifest = null;
            try { manifest = PluginManifest.Load(sub); }
            catch (Exception ex) { Log.Warn($"plugin '{folderNameOnly}': {ex.Message}"); continue; }

            if (!File.Exists(Path.Combine(sub, manifest.Script ?? "main.py")))
            {
                Log.Warn($"plugin '{manifest.Id}': script '{manifest.Script ?? "main.py"}' not found");
                continue;
            }

            var isApproved = approved.Contains(manifest.Id!);
            var timeout = TimeSpan.FromSeconds(Math.Max(5, _cfg.PluginTimeoutSec));
            var src = new ExternalQuotaSource(manifest, sub, timeout, isApproved);
            _sources.Add(src);
            added++;
            Log.Info($"plugin '{manifest.Id}' registered (approved={isApproved}, interval={src.Interval.TotalSeconds:0}s)");
        }
        return added;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        foreach (var src in _sources)
        {
            var timer = new DispatcherTimer { Interval = src.Interval };
            timer.Tick += (_, _) => _ = RefreshOneAsync(src);
            _timers[src.Id] = timer;
            timer.Start();

            // First poll fires immediately — don't wait for the first tick.
            _ = RefreshOneAsync(src);
        }
    }

    public void Stop()
    {
        foreach (var t in _timers.Values) t.Stop();
        _timers.Clear();
        _started = false;
    }

    /// <summary>Run one poll of a source. manual=true means a manual refresh (the user's
    /// click): it lets some sources make a costly real request
    /// instead of only reading a local cache (see IQuotaSource.RefreshAsync).</summary>
    public async Task RefreshOneAsync(IQuotaSource src, bool manual = false)
    {
        var snap = await RefreshCoreAsync(src, manual);
        if (manual && src is ExternalQuotaSource && HasError(snap)) _ = FollowUpAsync(src);
    }

    private static bool HasError(QuotaSnapshot? snap) =>
        snap is not null && (snap.SourceError is not null || snap.Blocks.Any(b => b.Error is not null));

    /// <summary>A plugin often answers a double-click with "opening the browser, sign in" and
    /// gets the real figure later. To bring the row back right after sign-in rather than on the
    /// next timer tick, keep re-polling it until the error clears.</summary>
    private async Task FollowUpAsync(IQuotaSource src)
    {
        lock (_followUps) { if (!_followUps.Add(src.Id)) return; }
        try
        {
            var until = DateTime.UtcNow + FollowUpWindow;
            while (_started && DateTime.UtcNow < until)
            {
                await Task.Delay(FollowUpStep);
                // null means the poll was skipped (a background tick is running) or failed: no reason to give up.
                var snap = await RefreshCoreAsync(src, manual: false);
                if (snap is not null && !HasError(snap)) return;
            }
        }
        finally
        {
            lock (_followUps) _followUps.Remove(src.Id);
        }
    }

    private readonly HashSet<string> _followUps = new();
    private static readonly TimeSpan FollowUpStep = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FollowUpWindow = TimeSpan.FromMinutes(5);

    private async Task<QuotaSnapshot?> RefreshCoreAsync(IQuotaSource src, bool manual)
    {
        // A background tick over a running poll is simply skipped. A manual one waits: otherwise
        // a double-click during a background poll would silently do nothing.
        if (!await src.Gate.WaitAsync(manual ? ManualGateWait : TimeSpan.Zero))
        {
            Log.Info($"{src.Id}: skipped, previous refresh still running");
            return null;
        }
        try
        {
            using var cts = new CancellationTokenSource();
            var snap = await src.RefreshAsync(cts.Token, manual);
            if (snap is null) return null;

            // Apply on the UI thread
            await _ui.InvokeAsync(() => _registry.Apply(snap));
            return snap;
        }
        catch (OperationCanceledException) { return null; /* expected on shutdown */ }
        catch (Exception ex) { Log.Error($"{src.Id}: refresh threw", ex); return null; }
        finally
        {
            src.Gate.Release();
            _ = _ui.BeginInvoke(() => AfterRefresh?.Invoke());
        }
    }

    private static readonly TimeSpan ManualGateWait = TimeSpan.FromSeconds(60);

    /// <summary>Run every source in parallel (the ↻ button, showing from the tray). Always without
    /// manual: paid source actions happen only on a double-click on their row.</summary>
    public async Task RefreshAllAsync()
    {
        await Task.WhenAll(_sources.Select(s => RefreshOneAsync(s)));
    }
}
