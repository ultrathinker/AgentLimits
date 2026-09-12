namespace AgentLimits;

/// <summary>
/// The unified shape of what one source poll returns (a builtin collector or an
/// external plugin). Replaces five different per-collector Result types.
/// </summary>
/// <param name="IsSoft">True for external plugins: their blocks can disappear (e.g.
/// the user changed their model list in OpenRouter), and the row is then removed
/// after N missed cycles. False for builtin sources: a network error must NOT hide
/// the row — it becomes stale instead, as it was before the refactor.</param>
public sealed record QuotaSnapshot(
    string SourceId,
    IReadOnlyList<BlockData> Blocks,
    string? SourceError = null,
    bool IsSoft = false);

/// <summary>
/// One quota block. Corresponds to one row in the widget.
/// </summary>
/// <param name="Key">Unique within SourceId. Global key = &lt;SourceId&gt;/&lt;Key&gt;.</param>
/// <param name="Group">The block's heading in the UI.</param>
/// <param name="Prefix">Subheading (model name etc.).</param>
/// <param name="Suffix">Window label (5h/7d/daily/...). Real text, not a binary guess.</param>
/// <param name="RemainingPercent">0..100. null = "no data, but the block exists".</param>
/// <param name="ResetsAt">When it resets. null = unknown.</param>
/// <param name="SampledAt">When it was measured. null = the host fills in the receive time.</param>
/// <param name="Error">When set, the block is marked stale with this reason.</param>
public sealed record BlockData(
    string Key,
    string Group,
    string? Prefix,
    string? Suffix,
    double? RemainingPercent,
    DateTime? ResetsAt,
    DateTime? SampledAt,
    string? Error);
