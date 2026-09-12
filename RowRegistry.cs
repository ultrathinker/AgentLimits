using System.Collections.ObjectModel;
using System.Windows;

namespace AgentLimits;

/// <summary>
/// A thread-aware row registry. Replaces the old MainWindow's hard-coded
/// AddRow-in-the-constructor: sources (builtin and plugins) create/update/
/// remove rows while the app is running.
///
/// All mutations must go through Dispatcher.Invoke — that's a WPF requirement,
/// not thread-safety of the registry itself.
/// </summary>
public sealed class RowRegistry
{
    private readonly ObservableCollection<LimitRow> _rows;
    private readonly Dictionary<string, LimitRow> _byKey = new(StringComparer.Ordinal);

    /// <summary>How many consecutive cycles a block can be missing from a source before removal.</summary>
    public const int RemoveAfterMisses = 5;

    /// <summary>How many recent source runs we keep for the cumulative-time check.</summary>
    public const int RollingWindow = 5;

    /// <summary>Map of &lt;globalKey, miss count&gt;. Removed once it reaches &gt;= RemoveAfterMisses.</summary>
    private readonly Dictionary<string, int> _misses = new(StringComparer.Ordinal);

    public RowRegistry(ObservableCollection<LimitRow> rows) => _rows = rows;

    public IEnumerable<LimitRow> Rows => _rows;
    public IReadOnlyDictionary<string, LimitRow> ByKey => _byKey;

    /// <summary>
    /// Apply one source's snapshot. Creates/updates/removes rows.
    /// </summary>
    public void Apply(QuotaSnapshot snap)
    {
        // 1. Collect this source's global block keys
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in snap.Blocks)
        {
            var globalKey = snap.SourceId + "/" + b.Key;
            seenKeys.Add(globalKey);
            Upsert(globalKey, b, snap.SourceError);
            _misses.Remove(globalKey);
        }

        // 2. Rows for this source that aren't in this snapshot.
        var prefix = snap.SourceId + "/";
        foreach (var existingKey in _byKey.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            if (seenKeys.Contains(existingKey)) continue;

            // For builtin sources, a missing block is a NETWORK / PARSING ERROR,
            // not "the model went away". The row must stay visible and dimmed
            // (stale), as it was before the refactor — otherwise we'd lose data
            // for no good reason. Only remove a block if the source is IsSoft
            // (an external plugin, where models really can disappear).
            if (!snap.IsSoft)
            {
                if (_byKey.TryGetValue(existingKey, out var builtinRow))
                {
                    builtinRow.Stale = true;
                    builtinRow.StaleReason = snap.SourceError ?? "source returned no data";
                    // Keep last RemainingPercent/ResetsAt — the stale visual is already set up
                }
                continue;
            }

            // Soft source (plugin): misses accumulate, and the block is removed after N cycles.
            var count = _misses.GetValueOrDefault(existingKey) + 1;
            _misses[existingKey] = count;
            if (count >= RemoveAfterMisses)
            {
                RemoveRow(existingKey);
                _misses.Remove(existingKey);
            }
            else
            {
                // Intermediate state: hide it for this cycle (Hidden=true)
                if (_byKey.TryGetValue(existingKey, out var row))
                    row.Hidden = true;
            }
        }
    }

    private void Upsert(string globalKey, BlockData b, string? sourceError)
    {
        var isNew = !_byKey.TryGetValue(globalKey, out var existing);
        // Don't add to _rows here: if this is the first item of a new group, WPF
        // synchronously creates the visual container right on CollectionChanged.Add,
        // and Error/RemainingPercent/... below only get set AFTER that point —
        // their PropertyChanged fires into the void, the container is already
        // rendered with defaults. So set up the row's full state first, and add
        // it to the collection last (at the end of this method).
        var row = existing ?? new LimitRow(globalKey, b.Group, b.Prefix ?? "", b.Suffix ?? "");
        if (!isNew)
        {
            // The group may have changed on a plugin (the user renamed the model) — update it
            if (!string.IsNullOrEmpty(b.Group) && row.Group != b.Group)
                row.Group = b.Group;
            if (!string.IsNullOrEmpty(b.Suffix) && row.SuffixText != b.Suffix)
                row.SetSuffix(b.Suffix);
            if (b.Prefix is not null && row.Prefix != b.Prefix)
                row.Prefix = b.Prefix;
        }

        row.Hidden = false;

        // Determine the block's error text: block.Error wins, else sourceError
        var errorText = b.Error ?? sourceError;

        if (errorText is not null && b.RemainingPercent is not null)
        {
            // Stale: there was a value, the source stumbled
            row.Stale = true;
            row.StaleReason = errorText;
            row.RemainingPercent = b.RemainingPercent;
            row.ResetsAt = b.ResetsAt;
            row.SampledAt = b.SampledAt ?? DateTime.Now;
            row.Error = null;
        }
        else if (errorText is not null)
        {
            // Stale: no data, the source stumbled
            row.Stale = true;
            row.StaleReason = errorText;
            row.RemainingPercent = null;
            row.ResetsAt = null;
            row.SampledAt = b.SampledAt ?? DateTime.Now;
            row.Error = errorText;
        }
        else
        {
            // OK
            row.Stale = false;
            row.StaleReason = null;
            row.RemainingPercent = b.RemainingPercent;
            row.ResetsAt = b.ResetsAt;
            row.SampledAt = b.SampledAt ?? DateTime.Now;
            row.Error = null;
        }

        if (isNew)
        {
            _rows.Add(row);
            _byKey[globalKey] = row;
        }
    }

    private void RemoveRow(string globalKey)
    {
        if (!_byKey.TryGetValue(globalKey, out var row)) return;
        _rows.Remove(row);
        _byKey.Remove(globalKey);
    }

    /// <summary>Clear all rows (e.g. on reload).</summary>
    public void Clear()
    {
        _rows.Clear();
        _byKey.Clear();
        _misses.Clear();
    }
}