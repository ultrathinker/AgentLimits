using System.IO;
using System.Net;
using System.Text.Json;

namespace AgentLimits;

/// <summary>
/// Local MCP server (Model Context Protocol) over JSON-RPC 2.0 over HTTP.
/// The tray app already collects quota data — this exposes it through a
/// standard protocol so other CLI agents (Claude Code, other MCP clients)
/// can programmatically ask "what's the quota situation right now."
///
/// Transport: a plain POST /mcp with JSON-RPC 2.0 in the body. No SSE: the
/// response is returned synchronously. This is compatible with clients that
/// support the simplified HTTP mode; strict streamable HTTP just needs the
/// 'text/event-stream' content-type added — left for later.
///
/// Default URL: http://localhost:8765/mcp
/// </summary>
public sealed class LimitServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SnapshotProvider _snapshot;
    private readonly int _port;

    /// <summary>Server name and version, returned in MCP-initialize.</summary>
    private const string ServerName = "AgentLimits";
    private const string ServerVersion = "1.0.0";

    /// <summary>MCP protocol version we respond with.</summary>
    private const string ProtocolVersion = "2024-11-05";

    public string Endpoint => $"http://localhost:{_port}/mcp";

    public LimitServer(int port, SnapshotProvider snapshot)
    {
        _port = port;
        _snapshot = snapshot;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    /// <summary>Starts accepting requests on a background thread. If the port is taken, the exception is swallowed.</summary>
    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Log.Warn($"mcp server: bind {Endpoint} failed ({ex.Message}) — endpoint disabled");
            return;
        }
        catch (Exception ex)
        {
            Log.Warn($"mcp server: start failed ({ex.Message}) — endpoint disabled");
            return;
        }
        Log.Info($"mcp server: {Endpoint} ready");
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Root GET → a short page with the address and how to connect.</summary>
    private async Task ServeRootAsync(HttpListenerContext ctx)
    {
        try
        {
            var html =
                "<!doctype html>\n" +
                "<meta charset=\"utf-8\">\n" +
                "<title>AgentLimits MCP</title>\n" +
                "<pre style=\"font-family: Consolas, monospace; padding: 16px\">\n" +
                "AgentLimits MCP server\n\n" +
                "  Endpoint:    POST " + Endpoint + "\n" +
                "  Protocol:    MCP " + ProtocolVersion + " over JSON-RPC 2.0\n\n" +
                "Methods:\n" +
                "  initialize                       handshake\n" +
                "  notifications/initialized        client -> server, no response\n" +
                "  tools/list                       discover tools\n" +
                "  tools/call                       invoke tool " + ToolName + "\n" +
                "  resources/list                   discover resources\n" +
                "  resources/read                   read resource " + ResourceUri + "\n\n" +
                "Quick probe (PowerShell):\n" +
                "  $body = '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}'\n" +
                "  Invoke-RestMethod -Method Post -Uri " + Endpoint + " -ContentType \"application/json\" -Body $body\n" +
                "</pre>\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
        finally
        {
            ctx.Response.Close();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }   // listener was stopped

            try { _ = Task.Run(() => HandleAsync(ctx)); }
            catch { ctx.Response.Close(); }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/")
            {
                await ServeRootAsync(ctx);
                return;
            }
            if (req.HttpMethod != "POST" || req.Url?.AbsolutePath != "/mcp")
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            string body;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? System.Text.Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            var response = HandleRpc(body);

            // notifications (method starting with "notifications/") need no response — 202 No Content.
            if (response is null)
            {
                ctx.Response.StatusCode = 202;
                return;
            }

            var respBytes = JsonSerializer.SerializeToUtf8Bytes(response);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = respBytes.Length;
            await ctx.Response.OutputStream.WriteAsync(respBytes);
        }
        catch (Exception ex)
        {
            Log.Warn($"mcp server: handler error ({ex.Message})");
            try { ctx.Response.StatusCode = 500; } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    /// <summary>
    /// JSON-RPC dispatcher. Returns null for notifications (no id field) — the client gets 202 No Content.
    /// </summary>
    private object? HandleRpc(string body)
    {
        JsonElement req;
        try
        {
            using var doc = JsonDocument.Parse(body);
            req = doc.RootElement.Clone();
        }
        catch
        {
            return MakeError(null, -32700, "parse error");
        }

        if (!req.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
            return MakeError(null, -32600, "invalid request: missing method");

        var method = methodProp.GetString() ?? "";
        var id = req.TryGetProperty("id", out var idProp) && idProp.ValueKind != JsonValueKind.Null
            ? (object?)idProp
            : null;

        object? result;
        try
        {
            result = method switch
            {
                "initialize"              => HandleInitialize(),
                "tools/list"              => HandleToolsList(),
                "tools/call"              => HandleToolsCall(req),
                "resources/list"          => HandleResourcesList(),
                "resources/read"          => HandleResourcesRead(req),
                "ping"                    => new { },
                _                         => throw new RpcException(-32601, $"method not found: {method}")
            };
        }
        catch (RpcException rex)
        {
            return id is null ? null : MakeError(idProp, rex.Code, rex.Message);
        }
        catch (Exception ex)
        {
            return id is null ? null : MakeError(idProp, -32603, "internal error: " + ex.Message);
        }

        // Notifications (no id) — the client won't wait for a response, return 202.
        if (id is null) return null;

        return new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = CloneId(idProp),
            ["result"] = result
        };
    }

    // ---------- MCP method handlers ----------

    private static object HandleInitialize() => new
    {
        protocolVersion = ProtocolVersion,
        capabilities = new
        {
            tools = new { listChanged = false },
            resources = new { subscribe = false, listChanged = false }
        },
        serverInfo = new { name = ServerName, version = ServerVersion },
        instructions = "AgentLimits exposes a single tool 'get_limits' that returns the current " +
                       "remaining quota per window for each configured agent profile. Use it to " +
                       "decide which model has headroom before you run a long task."
    };

    private static object HandleToolsList() => new
    {
        tools = new object[]
        {
            new
            {
                name = ToolName,
                description = "Returns the current Claude Code, z.ai/GLM, Codex, Antigravity and " +
                              "MiniMax quota state collected by AgentLimits. Each row carries the " +
                              "agent label, window length (5h/7d), remaining percent and the time " +
                              "at which the value was sampled.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>(),
                    additionalProperties = false
                }
            }
        }
    };

    private object HandleToolsCall(JsonElement req)
    {
        if (!req.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object ||
            !p.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String)
            throw new RpcException(-32602, "tools/call: missing params.name");

        var name = n.GetString();
        if (name != ToolName)
            throw new RpcException(-32602, $"unknown tool: {name}");

        // Text representation — for the LLM agent; a structured one is added below.
        var snapshot = _snapshot.GetSnapshot();
        var summary  = FormatHuman(snapshot);
        return new
        {
            content = new object[]
            {
                new { type = "text", text = summary }
            },
            isError = false,
            _meta = new { source = "AgentLimits", sampledAt = snapshot.SampledAt }
        };
    }

    private static object HandleResourcesList() => new
    {
        resources = new object[]
        {
            new
            {
                uri = ResourceUri,
                name = "current limits",
                description = "Latest snapshot of all configured agents in JSON form.",
                mimeType = "application/json"
            }
        }
    };

    private object HandleResourcesRead(JsonElement req)
    {
        if (!req.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object ||
            !p.TryGetProperty("uri", out var u) || u.ValueKind != JsonValueKind.String)
            throw new RpcException(-32602, "resources/read: missing params.uri");

        var uri = u.GetString();
        if (uri != ResourceUri)
            throw new RpcException(-32602, $"unknown resource: {uri}");

        var snapshot = _snapshot.GetSnapshot();
        var json = JsonSerializer.Serialize(new
        {
            sampledAt = snapshot.SampledAt,
            sources = snapshot.Sources
        });
        return new
        {
            contents = new object[]
            {
                new
                {
                    uri = ResourceUri,
                    mimeType = "application/json",
                    text = json
                }
            }
        };
    }

    // ---------- formatting ----------

    private static string FormatHuman(Snapshot snap)
    {
        if (snap.Sources.Count == 0)
            return "No agent limits have been collected yet. Wait one refresh cycle or open the source CLI once.";

        var lines = new List<string>
        {
            $"Agent limits — sampled at {snap.SampledAt:yyyy-MM-dd HH:mm:ss}",
            ""
        };
        foreach (var s in snap.Sources)
        {
            var pct = s.Error is not null ? "—" : $"{s.RemainingPercent:0.#}%";
            var err = string.IsNullOrEmpty(s.Error) ? "" : $"  ({s.Error})";
            var reset = s.ResetsAt is null ? "" : $"  resets {s.ResetsAt:dd.MM HH:mm}";
            lines.Add($"  • {s.Agent,-22} {s.Window,-4} {pct}{reset}{err}");
        }
        return string.Join("\n", lines);
    }

    private static object MakeError(JsonElement? id, int code, string message) => new
    {
        jsonrpc = "2.0",
        id = id is { } v ? (object?)CloneId(v) : null,
        error = new { code, message }
    };

    /// <summary>Copies the id value (number or string) without reaching back into JsonElement.</summary>
    private static object? CloneId(JsonElement id)
    {
        return id.ValueKind switch
        {
            JsonValueKind.Number => id.TryGetInt64(out var l) ? (object)l : id.GetDouble(),
            JsonValueKind.String => id.GetString(),
            _ => null
        };
    }

    private const string ToolName = "get_limits";
    private const string ResourceUri = "limits://current";

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _cts.Dispose();
    }
}

/// <summary>
/// Holds the current snapshot for the server to serve. Created in MainWindow, updated
/// on every Done(); the server reads it on each request without blocking.
/// </summary>
public sealed class SnapshotProvider
{
    private Snapshot _snap = new(DateTime.Now, Array.Empty<SourceRow>());

    public void Update(IEnumerable<LimitRow> rows)
    {
        var list = rows
            .Where(r => !r.Hidden)
            .Select(r =>
            {
                var agent = string.IsNullOrEmpty(r.Prefix) ? r.Group : $"{r.Group} · {r.Prefix}";
                // The real suffix text (5h/7d/daily/...), not a binary guess.
                var window = string.IsNullOrEmpty(r.SuffixText) ? "?" : r.SuffixText;
                return new SourceRow(
                    Agent: agent,
                    Window: window,
                    RemainingPercent: r.RemainingPercent ?? 0,
                    ResetsAt: r.ResetsAt,
                    Error: r.Error,
                    Hidden: r.Hidden);
            })
            .ToList();
        _snap = new Snapshot(DateTime.Now, list);
    }

    public Snapshot GetSnapshot() => _snap;
}

public sealed record Snapshot(DateTime SampledAt, IReadOnlyList<SourceRow> Sources);
public sealed record SourceRow(string Agent, string Window, double RemainingPercent, DateTime? ResetsAt, string? Error, bool Hidden);

internal sealed class RpcException : Exception
{
    public int Code { get; }
    public RpcException(int code, string message) : base(message) { Code = code; }
}
