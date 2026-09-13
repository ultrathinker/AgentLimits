namespace AgentLimits;

/// <summary>
/// A quota source. Implemented either by a builtin wrapper around a collector or
/// by an external plugin. PluginHost keeps the registry of sources, runs them on
/// a timer, and applies their results to the rows.
/// </summary>
public interface IQuotaSource
{
    /// <summary>Stable id; matches the folder name for plugins.</summary>
    string Id { get; }

    /// <summary>Display name for logs.</summary>
    string DisplayName { get; }

    /// <summary>Interval between polls.</summary>
    TimeSpan Interval { get; }

    /// <summary>Serializes polls within one source, but not across sources.</summary>
    SemaphoreSlim Gate { get; }

    /// <summary>Whether the source is builtin (for tray logic and disabling).</summary>
    bool IsBuiltin { get; }

    /// <summary>Whether the plugin has passed one-click approve. Always true for builtin.</summary>
    bool Approved { get; }

    /// <summary>One poll. Should return a QuotaSnapshot, or null on error.
    /// manual=true means a manual refresh (the user's click/double-click), not the background
    /// timer. Sources that need a real, costly request to recover their data
    /// (Codex) make it only when manual=true — background polling always stays
    /// free.</summary>
    Task<QuotaSnapshot?> RefreshAsync(CancellationToken ct, bool manual = false);
}
