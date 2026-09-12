using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentLimits;

/// <summary>
/// Plugin manifest: lives at plugins/&lt;id&gt;/manifest.json.
/// </summary>
public sealed class PluginManifest
{
    /// <summary>Required. Must match the folder name.</summary>
    public string? Id { get; set; }

    /// <summary>Optional. Defaults to 120.</summary>
    [JsonPropertyName("interval_sec")]
    public int? IntervalSec { get; set; }

    /// <summary>Optional. Defaults to main.py.</summary>
    [JsonPropertyName("script")]
    public string? Script { get; set; }

    /// <summary>Optional. The host will set AGENTLIMITS_TOKEN_&lt;key&gt;=&lt;value&gt;.</summary>
    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>Read and validate the manifest. Throws InvalidDataException on error.</summary>
    public static PluginManifest Load(string folder)
    {
        var path = Path.Combine(folder, "manifest.json");
        var json = File.ReadAllText(path);
        var m = JsonSerializer.Deserialize<PluginManifest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        }) ?? throw new InvalidDataException("empty manifest");

        if (string.IsNullOrWhiteSpace(m.Id))
            throw new InvalidDataException("manifest.id is required");
        if (m.Id != Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            throw new InvalidDataException($"manifest.id='{m.Id}' must match folder name '{Path.GetFileName(folder)}'");

        if (m.IntervalSec is < 30)
            throw new InvalidDataException($"manifest.interval_sec={m.IntervalSec} below minimum 30");

        return m;
    }
}