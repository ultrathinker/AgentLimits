using System.Text;
using AgentLimits;
using AgentLimits.Collectors;

// Console check of the sources: prints the same thing the widget would show.
Console.OutputEncoding = Encoding.UTF8;

var cfg = AppConfig.Load();
var sw = System.Diagnostics.Stopwatch.StartNew();

var agy = await new AgyCollector().CollectAsync();
foreach (var b in agy.Buckets)
    Print($"agy · {b.Id}", b.Window, agy.Error);
if (agy.Buckets.Count == 0) Print("agy", null, agy.Error);
Console.WriteLine($"   ({sw.ElapsedMilliseconds} ms)");

var codex = await new CodexCollector().CollectAsync();
Print("codex · 5h", codex.FiveHour, codex.Error);
Print("codex · weekly", codex.Weekly, codex.Error);
Console.WriteLine($"   codex plan: {codex.Plan ?? "?"}");

var claude = new ClaudeCollector();
foreach (var profile in new[] { "personal", "work" })
{
    var r = await claude.CollectAsync(profile);
    Print($"claude/{profile} · 5h", r.FiveHour, r.Error);
    Print($"claude/{profile} · weekly", r.SevenDay, r.Error);
}

var zai = await new ZaiCollector().CollectAsync(cfg);
Print("GLM · 5h", zai.FiveHour, zai.Error);
Print("GLM · weekly", zai.Weekly, zai.Error);
Console.WriteLine($"   z.ai level: {zai.Level ?? "?"}");

var minimax = await new MinimaxCollector().CollectAsync(cfg);
Print("MiniMax · 5h", minimax.FiveHour, minimax.Error);
Print("MiniMax · weekly", minimax.Weekly, minimax.Error);

static void Print(string label, LimitWindow? w, string? error)
{
    if (w is LimitWindow v)
    {
        var reset = v.ResetsAt is null ? "?" : v.ResetsAt.Value.ToString("dd.MM HH:mm");
        var sampled = v.SampledAt is null ? "" : $", sampled {v.SampledAt:dd.MM HH:mm}";
        Console.WriteLine($"{label,-22} remaining {v.RemainingPercent,6:0.##}%   resets {reset}{sampled}");
    }
    else
    {
        Console.WriteLine($"{label,-22} — {error ?? "no data"}");
    }
}
