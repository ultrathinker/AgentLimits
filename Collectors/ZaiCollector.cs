using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentLimits.Collectors;

/// <summary>
/// GLM quota (z.ai coding plan) — what a coding agent like Kilo draws down.
/// Exactly the request the /manage-apikey/coding-plan/personal/usage page makes
/// on Refresh. The JWT copied from the browser lives until logout (no exp claim).
///
/// In the response, percentage is how much is USED (the page itself labels it "Used").
/// </summary>
public sealed class ZaiCollector
{
    private const string Url = "https://api.z.ai/api/monitor/usage/quota/limit";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed record Result(LimitWindow? FiveHour, LimitWindow? Weekly, string? Level, string? Error);

    public async Task<Result> CollectAsync(AppConfig cfg, CancellationToken ct = default)
    {
        var token = cfg.ZaiToken;
        if (string.IsNullOrWhiteSpace(token))
            return new Result(null, null, null, "no token (see config.json)");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrWhiteSpace(cfg.Zai.Organization))
                req.Headers.TryAddWithoutValidation("Bigmodel-Organization", cfg.Zai.Organization);
            if (!string.IsNullOrWhiteSpace(cfg.Zai.Project))
                req.Headers.TryAddWithoutValidation("Bigmodel-Project", cfg.Zai.Project);
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.Referrer = new Uri("https://z.ai/");

            using var resp = await Http.SendAsync(req, ct);
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new Result(null, null, null, "token expired - refresh it");
            if (!resp.IsSuccessStatusCode)
                return new Result(null, null, null, $"HTTP {(int)resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return new Result(null, null, null, "unexpected response");

            var level = data.TryGetProperty("level", out var lv) ? lv.GetString() : null;

            LimitWindow? fiveHour = null;
            LimitWindow? weekly = null;

            if (data.TryGetProperty("limits", out var limits))
            {
                foreach (var lim in limits.EnumerateArray())
                {
                    // TIME_LIMIT is a monthly search/web-reader call counter, not tokens. Skip it.
                    if (lim.TryGetProperty("type", out var t) && t.GetString() != "TOKENS_LIMIT") continue;
                    if (!lim.TryGetProperty("percentage", out var pct)) continue;

                    DateTime? reset = null;
                    if (lim.TryGetProperty("nextResetTime", out var nrt) && nrt.ValueKind == JsonValueKind.Number)
                    {
                        var ms = nrt.GetInt64();
                        if (ms > 0)
                            reset = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                    }

                    var used = pct.GetDouble();
                    var remaining = Math.Clamp(100 - used, 0, 100);
                    var window = new LimitWindow(remaining, reset, DateTime.Now);

                    int unit = lim.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 0;
                    int number = lim.TryGetProperty("number", out var num) && num.ValueKind == JsonValueKind.Number ? num.GetInt32() : 0;

                    // unit 3 (hours, number 5) = 5-hour window.
                    // unit 6 (weeks, number 1) = weekly window.
                    // At zero usage (0%) z.ai omits nextResetTime, so sorting by reset date used to break.
                    if (unit == 3 || number == 5)
                    {
                        fiveHour = window with { WindowLabel = "5h" };
                    }
                    else if (unit == 6 || unit == 4 || number == 1 || number == 7)
                    {
                        weekly = window with { WindowLabel = "7d" };
                    }
                    else
                    {
                        if (reset.HasValue && (reset.Value - DateTime.Now).TotalHours > 12)
                            weekly = window with { WindowLabel = "7d" };
                        else
                            fiveHour = window with { WindowLabel = "5h" };
                    }
                }
            }

            if (fiveHour is null && weekly is null)
                return new Result(null, null, level, "no windows in response");

            return new Result(fiveHour, weekly, level, null);
        }
        catch (Exception ex)
        {
            return new Result(null, null, null, ex.Message);
        }
    }
}
