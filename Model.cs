using System.ComponentModel;
using System.Globalization;

namespace AgentLimits;

/// <summary>
/// One dashboard row: one quota window for a given agent.
/// Internally everything is tracked as "how much is REMAINING" (sources report
/// it their own way — agy gives remainingFraction, the others give used%), and
/// it's displayed in whichever form <see cref="ShowUsed"/> selects.
/// </summary>
public sealed class LimitRow : INotifyPropertyChanged
{
    /// <summary>
    /// A mode shared across all rows: sources label their quota differently
    /// (z.ai and Claude say "used", Codex says "left"), so we offer a toggle.
    /// </summary>
    public static bool ShowUsed { get; set; }

    public string Key { get; }

    /// <summary>The block heading this row sits under in the widget (agent/profile).</summary>
    public string Group { get; internal set; }

    public string Prefix { get; internal set; }

    /// <summary>Current suffix text (5h/7d/...), for debugging and for the MCP output.</summary>
    public string SuffixText => _suffix;

    private string _suffix;

    /// <summary>Plugin not yet approved — the UI shows "click to approve".</summary>
    public bool PendingApproval
    {
        get => _pendingApproval;
        set { if (_pendingApproval == value) return; _pendingApproval = value; Raise(nameof(PendingApproval)); Raise(nameof(Tooltip)); }
    }
    private bool _pendingApproval;

    /// <summary>A manual refresh of this very row is running (it was double-clicked) — the UI
    /// spins a small spinner over the bar instead of blurring the whole window.</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { if (_isRefreshing == value) return; _isRefreshing = value; Raise(nameof(IsRefreshing)); }
    }
    private bool _isRefreshing;
    private double? _remainingPercent;
    private DateTime? _resetsAt;
    private string? _error;
    private DateTime? _sampledAt;
    private bool _hidden;
    private bool _stale;
    private string? _staleReason;

    public LimitRow(string key, string group, string prefix, string suffix)
    {
        Key = key;
        Group = group;
        Prefix = prefix;
        _suffix = suffix;
    }

    public string Label => string.IsNullOrEmpty(Prefix) ? _suffix : $"{Prefix} · {_suffix}";

    /// <summary>A short window — anything shorter than a day: labels like 5h/45m vs. 7d.</summary>
    public bool IsShortWindow => !_suffix.EndsWith("d", StringComparison.Ordinal);

    /// <summary>
    /// The window's length is supplied by the source: for Codex it depends on
    /// the plan (on the business plan, for instance, only the weekly window exists).
    /// </summary>
    public void SetSuffix(string? suffix)
    {
        if (string.IsNullOrEmpty(suffix) || _suffix == suffix) return;
        _suffix = suffix;
        Raise(nameof(Label));
        Raise(nameof(IsShortWindow));
    }

    /// <summary>How much of the quota is still available (0..100). null — no data.</summary>
    public double? RemainingPercent
    {
        get => _remainingPercent;
        set { _remainingPercent = value; RaiseValueChanged(); }
    }

    /// <summary>
    /// The source didn't respond, but a past value exists — show it dimmed
    /// instead of blanking the row: a stale number is more useful than emptiness.
    /// </summary>
    public bool Stale
    {
        get => _stale;
        set { if (_stale == value) return; _stale = value; Raise(nameof(Stale)); Raise(nameof(RowOpacity)); Raise(nameof(Tooltip)); }
    }

    public string? StaleReason
    {
        get => _staleReason;
        set { _staleReason = value; Raise(nameof(Tooltip)); }
    }

    public double RowOpacity => Stale ? 0.5 : 1.0;

    /// <summary>The row is hidden when the plan simply doesn't have this window (5h on Codex Business).</summary>
    public bool Hidden
    {
        get => _hidden;
        set { if (_hidden == value) return; _hidden = value; Raise(nameof(Hidden)); }
    }

    public DateTime? ResetsAt
    {
        get => _resetsAt;
        set { _resetsAt = value; Raise(nameof(ResetsAt)); Raise(nameof(ResetText)); }
    }

    /// <summary>The source's error text; when set, the row is drawn gray.</summary>
    public string? Error
    {
        get => _error;
        set { _error = value; Raise(nameof(Error)); Raise(nameof(RemainingText)); Raise(nameof(Tone)); Raise(nameof(Tooltip)); }
    }

    /// <summary>When the data was actually measured (for cached sources — Codex, statusline).</summary>
    public DateTime? SampledAt
    {
        get => _sampledAt;
        set { _sampledAt = value; Raise(nameof(SampledAt)); Raise(nameof(Tooltip)); }
    }

    /// <summary>The number the user sees: remaining or used, depending on the current mode.</summary>
    public double? DisplayPercent =>
        RemainingPercent is null ? null : (ShowUsed ? 100 - RemainingPercent.Value : RemainingPercent.Value);

    /// <summary>The window should already have reset (ResetsAt is in the past), but no fresh data
    /// has arrived yet — the source is silent; it doesn't mean the limit really is still the old one.</summary>
    public bool ResetOverdue => ResetsAt is not null && ResetsAt.Value <= DateTime.Now;

    public string RemainingText
    {
        get
        {
            if (Error is not null) return "—";
            if (DisplayPercent is null) return "…";

            var v = DisplayPercent.Value;
            // An overdue reset isn't a time in itself but a flag: "this is an old number,
            // the source hasn't confirmed the new window yet". It's marked in brackets next to
            // the percent, not in the time column, where the word was mistaken for a clock reading.
            var flag = ResetOverdue ? " [reset]" : "";
            if (v >= 99.5) return "100%" + flag;
            // Fractional percents are shown only below 1% — that's where they matter.
            // Very tiny amounts aren't rounded down to zero: "0%" would read as "all gone".
            if (v > 0 && v < 1)
                return (v < 0.05 ? "<0.1%" : v.ToString("0.#", CultureInfo.InvariantCulture) + "%") + flag;
            return Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + "%" + flag;
        }
    }

    public string ResetText
    {
        get
        {
            if (ResetsAt is null) return "";
            var left = ResetsAt.Value - DateTime.Now;
            // "Reset" doesn't go here — this is the time column, not a status; see RemainingText.
            if (left <= TimeSpan.Zero) return "";
            if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d {left.Hours}h";
            if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes:00}m";
            return $"{(int)left.TotalMinutes}m";
        }
    }

    /// <summary>The bar reflects the same number as the text; its color is always based on the remaining amount.</summary>
    public double BarWidthFactor => Math.Clamp((DisplayPercent ?? 0) / 100.0, 0, 1);

    /// <summary>ok / warn / crit / dead — the template picks its color off this field.</summary>
    public string Tone
    {
        get
        {
            if (Error is not null || RemainingPercent is null) return "dead";
            var v = RemainingPercent.Value;
            if (v <= 10) return "crit";
            if (v <= 30) return "warn";
            return "ok";
        }
    }

    public string Tooltip
    {
        get
        {
            if (Error is not null) return $"{Label}: {Error}";
            var stale = Stale ? $"\nstale: {StaleReason ?? "source unavailable"}" : "";
            var both = RemainingPercent is null
                ? ""
                : $"\n{RemainingPercent.Value:0.#}% remaining, {100 - RemainingPercent.Value:0.#}% used";
            var reset = ResetsAt is null ? "" : $"\nresets: {ResetsAt:dd.MM HH:mm}";
            var age = SampledAt is null ? "" : $"\nsampled: {SampledAt:HH:mm:ss}";
            return $"{Label}{both}{reset}{age}{stale}";
        }
    }

    /// <summary>Peak (expensive) hours info for the group.</summary>
    public string? PeakScheduleText => Group switch
    {
        "z.ai · GLM" => "Mon–Fri 08:00–12:00 (3×)",
        "MiniMax"    => "Mon–Fri 09:00–11:30",
        _ => null
    };

    public bool IsPeakActive
    {
        get
        {
            var now = DateTime.Now;
            if (now.DayOfWeek is < DayOfWeek.Monday or > DayOfWeek.Friday)
                return false;

            var time = now.TimeOfDay;
            return Group switch
            {
                "z.ai · GLM" => time >= new TimeSpan(8, 0, 0) && time < new TimeSpan(12, 0, 0),
                "MiniMax"    => time >= new TimeSpan(9, 0, 0) && time < new TimeSpan(11, 30, 0),
                _ => false
            };
        }
    }

    public string PeakTooltip => Group switch
    {
        "z.ai · GLM" => IsPeakActive
            ? "Peak hours ACTIVE right now! Quota multiplier is 3× (Mon–Fri 08:00–12:00 local time)"
            : "Peak hours: Mon–Fri 08:00–12:00 (3× multiplier). Normal rate is 1×.",
        "MiniMax" => IsPeakActive
            ? "Peak hours ACTIVE right now! Traffic control & accelerated usage (Mon–Fri 09:00–11:30 local time)"
            : "Peak hours: Mon–Fri 09:00–11:30 (traffic control / dynamic rate limiting)",
        _ => ""
    };

    /// <summary>Ticks once a minute: recompute "resets in Xh Ym" and peak-hours status without hitting the source.</summary>
    public void RefreshCountdown()
    {
        Raise(nameof(ResetText));
        Raise(nameof(RemainingText));
        Raise(nameof(IsPeakActive));
        Raise(nameof(PeakTooltip));
    }

    /// <summary>Repaint after the remaining/used mode changes.</summary>
    public void RefreshDisplayMode() => RaiseValueChanged();

    private void RaiseValueChanged()
    {
        Raise(nameof(RemainingPercent));
        Raise(nameof(DisplayPercent));
        Raise(nameof(RemainingText));
        Raise(nameof(BarWidthFactor));
        Raise(nameof(Tone));
        Raise(nameof(Tooltip));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Result of one limit window, as returned by a collector.</summary>
/// <param name="WindowLabel">Short label for the window's length ("5h", "7d") — when the source knows it.</param>
public readonly record struct LimitWindow(
    double RemainingPercent,
    DateTime? ResetsAt,
    DateTime? SampledAt = null,
    string? WindowLabel = null);
