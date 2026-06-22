// McpEndpoints.cs
//
// (3.2.0) ASP.NET Core route mapping for the MCP JSON-RPC 2.0 endpoint.
// Direct lift of CircleUp's MapMcpApi protocol layer — vault-specific
// tool surface replaced with a DI-driven IMcpTool collection +
// IMcpResourceProvider collection. POST /mcp handles single requests
// AND batches. GET /mcp/manifest is kept for backwards compatibility.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Hosting.Mcp;

public static class McpEndpoints
{
    public sealed class McpServerInfo
    {
        public string Name        { get; init; } = "circleai-mcp";
        public string Version     { get; init; } = "3.2.0";
        public string Description { get; init; } = "CircleAI MCP endpoint";
    }

    /// <summary>
    /// (3.2.0) Map the MCP JSON-RPC 2.0 endpoint onto a route group.
    /// Hosts register <see cref="IMcpTool"/> + <see cref="IMcpResourceProvider"/>
    /// in DI; the dispatcher routes from there.
    /// </summary>
    public static RouteGroupBuilder MapMcpApi(this RouteGroupBuilder group, McpServerInfo? serverInfo = null)
    {
        var info = serverInfo ?? new McpServerInfo();

        // GET /mcp/manifest — legacy, but useful for ad-hoc curl exploration.
        group.MapGet("/mcp/manifest", (HttpContext ctx) =>
        {
            var tools = ctx.RequestServices.GetServices<IMcpTool>().ToArray();
            return Results.Ok(new
            {
                name        = info.Name,
                version     = info.Version,
                description = info.Description,
                deprecated  = true,
                deprecationNotice = "Use POST /mcp with JSON-RPC 2.0 instead.",
                tools = tools.Select(t => new
                {
                    name        = t.Name,
                    description = t.Description,
                    inputSchema = t.InputSchema,
                }),
            });
        });

        // POST /mcp — JSON-RPC 2.0.
        group.MapPost("/mcp", async (HttpContext ctx) =>
        {
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body).ConfigureAwait(false); }
            catch { return McpError(null, -32700, "Parse error"); }

            if (body is null) return McpError(null, -32600, "Invalid Request");

            if (body is JsonArray batch)
            {
                var responses = new List<object?>();
                foreach (var item in batch)
                {
                    responses.Add(await HandleRequestAsync(item, ctx, info).ConfigureAwait(false));
                }
                return Results.Ok(responses.Where(r => r is not null).ToArray());
            }

            var result = await HandleRequestAsync(body, ctx, info).ConfigureAwait(false);
            return result is null ? Results.NoContent() : Results.Ok(result);
        });

        return group;
    }

    // ─────────────────────────────────────────────────────────────────
    // JSON-RPC dispatcher
    // ─────────────────────────────────────────────────────────────────

    internal static Task<object?> HandleRequestAsync(JsonNode? req, HttpContext ctx, McpServerInfo info) =>
        DispatchAsync(req, ctx.RequestServices, info, ctx.RequestAborted);

    /// <summary>
    /// (3.2.0) Pure-DI dispatcher entry point — testable without a
    /// HttpContext. Returns null for notifications.
    /// </summary>
    public static async Task<object?> DispatchAsync(
        JsonNode?              req,
        IServiceProvider       services,
        McpServerInfo          info,
        System.Threading.CancellationToken ct = default)
    {
        if (req is null) return McpErrorObj(null, -32600, "Invalid Request");

        var id     = req["id"];
        var method = req["jsonrpc"]?.GetValue<string>() == "2.0"
                     ? req["method"]?.GetValue<string>()
                     : null;
        if (method is null) return McpErrorObj(id, -32600, "Invalid Request: missing jsonrpc or method");

        var @params = req["params"];
        try
        {
            return method switch
            {
                "initialize"                => HandleInitialize(id, info),
                "notifications/initialized" => null,
                "tools/list"                => HandleToolsList(id, services),
                "tools/call"                => await HandleToolsCallAsync(id, @params, services, ct).ConfigureAwait(false),
                "resources/list"            => await HandleResourcesListAsync(id, services, ct).ConfigureAwait(false),
                "resources/read"            => await HandleResourcesReadAsync(id, @params, services, ct).ConfigureAwait(false),
                _                           => McpErrorObj(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception ex)
        {
            return McpErrorObj(id, -32603, $"Internal error: {ex.Message}");
        }
    }

    private static object HandleInitialize(JsonNode? id, McpServerInfo info) =>
        McpResult(id, new
        {
            protocolVersion = "2024-11-05",
            serverInfo      = new { name = info.Name, version = info.Version },
            capabilities    = new
            {
                tools     = new { listChanged = false },
                resources = new { listChanged = false, subscribe = false },
            },
        });

    private static object HandleToolsList(JsonNode? id, IServiceProvider services)
    {
        var tools = services.GetServices<IMcpTool>()
            .Select(t => new
            {
                name        = t.Name,
                description = t.Description,
                inputSchema = t.InputSchema,
            })
            .ToArray();
        return McpResult(id, new { tools });
    }

    private static async Task<object> HandleToolsCallAsync(
        JsonNode? id, JsonNode? @params, IServiceProvider services,
        System.Threading.CancellationToken ct)
    {
        var toolName = @params?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(toolName))
            return McpErrorObj(id, -32602, "Invalid params: 'name' is required")!;

        var tool = services.GetServices<IMcpTool>()
            .FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
        if (tool is null) return McpErrorObj(id, -32602, $"Unknown tool: {toolName}")!;

        var args = @params?["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            var result = await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            return McpToolResult(id, result);
        }
        catch (McpToolException ex)
        {
            return McpToolError(id, ex.Message);
        }
    }

    private static async Task<object> HandleResourcesListAsync(
        JsonNode? id, IServiceProvider services,
        System.Threading.CancellationToken ct)
    {
        var providers = services.GetServices<IMcpResourceProvider>().ToArray();
        var resources = new List<McpResource>();
        foreach (var p in providers)
        {
            var page = await p.ListAsync(ct).ConfigureAwait(false);
            resources.AddRange(page);
        }
        return McpResult(id, new
        {
            resources = resources.Select(r => new
            {
                uri         = r.Uri,
                name        = r.Name,
                description = r.Description ?? r.Name,
                mimeType    = r.MimeType,
            }).ToArray(),
        });
    }

    private static async Task<object> HandleResourcesReadAsync(
        JsonNode? id, JsonNode? @params, IServiceProvider services,
        System.Threading.CancellationToken ct)
    {
        var uri = @params?["uri"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(uri)) return McpErrorObj(id, -32602, "Invalid params: 'uri' is required")!;

        var providers = services.GetServices<IMcpResourceProvider>();
        var provider = providers.FirstOrDefault(p => uri.StartsWith(p.UriScheme, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return McpErrorObj(id, -32602, $"No provider for URI scheme: {uri}")!;

        var content = await provider.ReadAsync(uri, ct).ConfigureAwait(false);
        if (content is null) return McpErrorObj(id, -32602, $"Resource not found: {uri}")!;

        return McpResult(id, new
        {
            contents = new[]
            {
                new { uri = content.Uri, mimeType = content.MimeType, text = content.Text },
            },
        });
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static object McpResult(JsonNode? id, object result) =>
        new { jsonrpc = "2.0", id = id?.ToJsonString(), result };

    private static object McpToolResult(JsonNode? id, object data) =>
        McpResult(id, new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(data) } },
            isError = false,
        });

    private static object McpToolError(JsonNode? id, string message) =>
        McpResult(id, new
        {
            content = new[] { new { type = "text", text = message } },
            isError = true,
        });

    private static IResult McpError(JsonNode? id, int code, string message) =>
        Results.Ok(McpErrorObj(id, code, message));

    private static object McpErrorObj(JsonNode? id, int code, string message) =>
        new { jsonrpc = "2.0", id = id?.ToJsonString(), error = new { code, message } };
}
