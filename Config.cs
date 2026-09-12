using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentLimits;

public sealed class ZaiConfig
{
    /// <summary>JWT, DPAPI-encrypted under the current account (base64). Never touches disk in plaintext.</summary>
    public string? TokenProtected { get; set; }
    public string? Organization { get; set; }
    public string? Project { get; set; }
}

public sealed class MinimaxConfig
{
    /// <summary>MiniMax API/subscription token, DPAPI-encrypted under the current account (base64).</summary>
    public string? TokenProtected { get; set; }
}

public sealed class AppConfig
{
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// <summary>false — show remaining (default), true — show used, matching the web dashboards.</summary>
    public bool ShowUsed { get; set; }

    public ZaiConfig Zai { get; set; } = new();
    public MinimaxConfig Minimax { get; set; } = new();

    /// <summary>Poll intervals, in seconds. agy is more expensive than the rest — it can spawn a process.</summary>
    public int AgyIntervalSec { get; set; } = 300;
    public int CodexIntervalSec { get; set; } = 60;
    /// <summary>Claude is polled rarely: /api/oauth/usage returns 429 quickly.</summary>
    public int ClaudeIntervalSec { get; set; } = 300;
    public int ZaiIntervalSec { get; set; } = 180;
    public int MinimaxIntervalSec { get; set; } = 180;

    /// <summary>localhost port for the MCP server (JSON-RPC over HTTP). 0 — disable.</summary>
    public int McpPort { get; set; } = 8765;

    // ---------- plugins (new section) ----------

    /// <summary>Plugins folder name, relative to %LOCALAPPDATA%\AgentLimits\.</summary>
    public string PluginsDir { get; set; } = "plugins";

    /// <summary>Ids of plugins the user has disabled.</summary>
    public List<string> DisabledPlugins { get; set; } = new();

    /// <summary>Ids of plugins that passed one-click approve.</summary>
    public List<string> ApprovedPlugins { get; set; } = new();

    /// <summary>Plugin launch timeout, in seconds.</summary>
    public int PluginTimeoutSec { get; set; } = 30;

    /// <summary>Path to the Python interpreter. Defaults to "py" (the Windows launcher);
    /// if that's not found, python / python3 / python.exe are tried.</summary>
    public string PythonPath { get; set; } = "py";

    /// <summary>
    /// Block order in the widget (top to bottom). Names match LimitRow.Group
    /// ("Claude · personal", "Claude · work", "z.ai · GLM", "Codex", "Antigravity", "MiniMax").
    /// The user changes this by dragging a block's header; the app saves it here.
    /// </summary>
    public List<string> GroupOrder { get; set; } = new()
    {
        "Claude · personal",
        "Claude · work",
        "z.ai · GLM",
        "Codex",
        "Antigravity",
        "MiniMax"
    };

    [JsonIgnore]
    public string? ZaiToken
    {
        get => TokenProtection.Unprotect(Zai.TokenProtected);
        set => Zai.TokenProtected = TokenProtection.Protect(value);
    }

    [JsonIgnore]
    public string? MinimaxToken
    {
        get => TokenProtection.Unprotect(Minimax.TokenProtected);
        set => Minimax.TokenProtected = TokenProtection.Protect(value);
    }

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentLimits");

    public static string Path_ { get; } = Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // The config may be edited by hand or by a script — don't be picky about key casing.
        PropertyNameCaseInsensitive = true
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path_), JsonOpts) ?? new AppConfig();
        }
        catch { /* a broken config must not block startup — just fall back to defaults */ }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path_, JsonSerializer.Serialize(this, JsonOpts));
    }
}

/// <summary>
/// DPAPI called directly through crypt32 — to avoid pulling in a NuGet package
/// for just two calls. CurrentUser scope: only this account on this machine can decrypt it.
/// </summary>
internal static class TokenProtection
{
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        return Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(plain), protect: true));
    }

    public static string? Unprotect(string? protectedB64)
    {
        if (string.IsNullOrEmpty(protectedB64)) return null;
        try { return Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(protectedB64), protect: false)); }
        catch { return null; }
    }

    private static byte[] Crypt(byte[] input, bool protect)
    {
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        try
        {
            inBlob.cbData = input.Length;
            inBlob.pbData = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);

            var ok = protect
                ? CryptProtectData(ref inBlob, "AgentLimits", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok) throw new InvalidOperationException("DPAPI failed: " + Marshal.GetLastWin32Error());

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
