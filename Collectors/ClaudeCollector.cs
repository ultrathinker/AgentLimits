using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentLimits.Collectors;

/// <summary>
/// Claude Code quota, per profile (personal ~/.claude, work ~/.claude-work).
///
/// Reads the OAuth token from the profile's .credentials.json and calls
/// GET https://api.anthropic.com/api/oauth/usage — the same numbers the
/// Usage screen in the Claude app shows.
///
/// Why not statusline: it does carry rate_limits, but the file is written by
/// whichever profile's statusline happens to be configured, and with several
/// sessions running, one account's data easily ends up attributed to another.
/// The token, by contrast, is unambiguously tied to its profile.
///
/// The endpoint returns 429 easily under frequent polling, so the interval is
/// large, and the last successful response is kept in memory and shown until
/// it's replaced.
/// </summary>
public sealed class ClaudeCollector
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly Dictionary<string, Result> _lastGood = [];

    public sealed record Result(LimitWindow? FiveHour, LimitWindow? SevenDay, string? Plan, string? Error);

    /// <summary>Profile directory: personal → ~/.claude, work → ~/.claude-work.</summary>
    public static string ProfileDir(string profile) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        profile == "work" ? ".claude-work" : ".claude");

    public async Task<Result> CollectAsync(string profile, CancellationToken ct = default)
    {
        var (token, plan) = ReadCredentials(profile);
        if (token is null)
            return Fallback(profile, "no token for profile");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await Http.SendAsync(req, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return Fallback(profile, "rate limited, retrying");
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return Fallback(profile, "token expired - run claude");
            if (!resp.IsSuccessStatusCode)
                return Fallback(profile, $"HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            var result = new Result(
                ReadWindow(root, "five_hour", "5h"),
                ReadWindow(root, "seven_day", "7d"),
                plan,
                null);

            if (result.FiveHour is not null || result.SevenDay is not null)
                _lastGood[profile] = result;
            return result;
        }
        catch (Exception ex)
        {
            return Fallback(profile, ex.Message);
        }
    }

    /// <summary>On failure, show the last successful response (instead of nothing),
    /// but keep Error set — otherwise the user loses the stale indicator.</summary>
    private Result Fallback(string profile, string error) =>
        _lastGood.TryGetValue(profile, out var last)
            ? last with { Error = error }
            : new Result(null, null, null, error);

    private static (string? Token, string? Plan) ReadCredentials(string profile)
    {
        var path = Path.Combine(ProfileDir(profile), ".credentials.json");
        try
        {
            if (!File.Exists(path)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return (null, null);

            var token = oauth.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
            var plan = oauth.TryGetProperty("subscriptionType", out var s) ? s.GetString() : null;
            return (token, plan);
        }
        catch
        {
            return (null, null);
        }
    }

    private static LimitWindow? ReadWindow(JsonElement root, string name, string label)
    {
        if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) return null;

        DateTime? reset = null;
        if (w.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(ra.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
            reset = dt.ToLocalTime();

        return new LimitWindow(Math.Clamp(100 - u.GetDouble(), 0, 100), reset, DateTime.Now, label);
    }
}
