using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentLimits.Collectors;

/// <summary>
/// MiniMax quota (Token Plan: 5-hour and weekly windows).
/// Polls the official API https://api.minimax.io/v1/token_plan/remains
/// using the API / Subscription key, DPAPI-encrypted in config.json.
/// </summary>
public sealed class MinimaxCollector
{
    private const string ApiUrl = "https://api.minimax.io/v1/token_plan/remains";
    private const string BackendUrl = "https://platform.minimax.io/backend/account/token_plan/remains_percent";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed record Result(LimitWindow? FiveHour, LimitWindow? Weekly, string? Error);

    public async Task<Result> CollectAsync(AppConfig cfg, CancellationToken ct = default)
    {
        var token = cfg.MinimaxToken ?? Environment.GetEnvironmentVariable("MINIMAX_API_KEY");
        if (string.IsNullOrWhiteSpace(token))
            return new Result(null, null, "no token (see config.json)");

        try
        {
            // 1. Try the official v1 token_plan endpoint
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

            using var resp = await Http.SendAsync(req, ct);
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new Result(null, null, "token expired or invalid - refresh it");

            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var parsed = ParseResponse(body);
                if (parsed.Error is null) return parsed;
            }

            // 2. Fall back to the platform's backend endpoint
            using var req2 = new HttpRequestMessage(HttpMethod.Get, BackendUrl);
            req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req2.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req2.Headers.Referrer = new Uri("https://platform.minimax.io/");

            using var resp2 = await Http.SendAsync(req2, ct);
            if (resp2.IsSuccessStatusCode)
            {
                var body2 = await resp2.Content.ReadAsStringAsync(ct);
                var parsed2 = ParseResponse(body2);
                if (parsed2.Error is null) return parsed2;
            }

            return new Result(null, null, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return new Result(null, null, ex.Message);
        }
    }

    public static Result ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("base_resp", out var baseResp))
            {
                if (baseResp.TryGetProperty("status_code", out var code) && code.GetInt32() != 0)
                {
                    var msg = baseResp.TryGetProperty("status_msg", out var m) ? m.GetString() : "error";
                    return new Result(null, null, msg ?? $"code {code.GetInt32()}");
                }
            }

            if (!root.TryGetProperty("model_remains", out var modelRemains) || modelRemains.GetArrayLength() == 0)
                return new Result(null, null, "no model_remains in response");

            // Look for the "general" entry (the plan's main model), else take the first
            JsonElement? targetElem = null;
            foreach (var item in modelRemains.EnumerateArray())
            {
                if (item.TryGetProperty("model_name", out var mn) && mn.GetString() == "general")
                {
                    targetElem = item;
                    break;
                }
            }
            targetElem ??= modelRemains[0];
            var elem = targetElem.Value;

            // 5-hour window
            double? fiveUsedPct = ParsePercent(elem, "current_interval_used_percent", "current_interval_remains_count", "current_interval_total_count");
            DateTime? fiveReset = ParseResetTime(elem, "end_time", "remains_time");
            LimitWindow? fiveWindow = fiveUsedPct is not null
                ? new LimitWindow(Math.Clamp(100 - fiveUsedPct.Value, 0, 100), fiveReset, DateTime.Now, "5h")
                : null;

            // Weekly window
            double? weekUsedPct = ParsePercent(elem, "current_weekly_used_percent", "current_weekly_remains_count", "current_weekly_total_count");
            DateTime? weekReset = ParseResetTime(elem, "weekly_end_time", "weekly_remains_time");
            LimitWindow? weekWindow = weekUsedPct is not null
                ? new LimitWindow(Math.Clamp(100 - weekUsedPct.Value, 0, 100), weekReset, DateTime.Now, "7d")
                : null;

            if (fiveWindow is null && weekWindow is null)
                return new Result(null, null, "no valid limit windows found");

            return new Result(fiveWindow, weekWindow, null);
        }
        catch (Exception ex)
        {
            return new Result(null, null, ex.Message);
        }
    }

    private static double? ParsePercent(JsonElement elem, string pctProp, string remainsCountProp, string totalCountProp)
    {
        if (elem.TryGetProperty(pctProp, out var p))
        {
            if (p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString()?.Trim().TrimEnd('%');
                if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var val))
                    return val;
            }
            else if (p.ValueKind == JsonValueKind.Number)
            {
                var val = p.GetDouble();
                return val <= 1.0 && val > 0 ? val * 100 : val;
            }
        }

        if (elem.TryGetProperty(remainsCountProp, out var rem) &&
            elem.TryGetProperty(totalCountProp, out var tot) &&
            rem.ValueKind == JsonValueKind.Number &&
            tot.ValueKind == JsonValueKind.Number)
        {
            var r = rem.GetDouble();
            var t = tot.GetDouble();
            if (t > 0 && r >= 0)
                return Math.Clamp((1.0 - (r / t)) * 100, 0, 100);
        }

        return null;
    }

    private static DateTime? ParseResetTime(JsonElement elem, string endProp, string remainsMsProp)
    {
        if (elem.TryGetProperty(endProp, out var e) && e.ValueKind == JsonValueKind.Number)
        {
            var ms = e.GetInt64();
            if (ms > 0)
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        }

        if (elem.TryGetProperty(remainsMsProp, out var r) && r.ValueKind == JsonValueKind.Number)
        {
            var ms = r.GetInt64();
            if (ms > 0)
                return DateTime.Now.AddMilliseconds(ms);
        }

        return null;
    }
}
