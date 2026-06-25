// McpToolImporter.cs
//
// (3.3.0) Pull tool definitions from an MCP (Model Context Protocol)
// server at call start. Each remote tool registers into the local
// IToolCallRegistry as a webhook-style tool that forwards calls back
// to the MCP server.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Description of one MCP tool returned from <c>tools/list</c>.</summary>
public sealed record McpToolDescriptor(string Name, string Description, string InputJsonSchema);

/// <summary>(3.3.0) MCP server descriptor.</summary>
/// <param name="ServerEndpoint">HTTP endpoint of the MCP server.</param>
/// <param name="AuthorizationHeader">Optional <c>Authorization</c> header to attach (e.g. <c>Bearer ...</c>).</param>
/// <param name="ToolNamePrefix">Optional prefix applied to imported tool names to avoid collisions.</param>
public sealed record McpServerConfig(
    Uri     ServerEndpoint,
    string? AuthorizationHeader = null,
    string? ToolNamePrefix      = null);

/// <summary>(3.3.0) Imports tools from MCP servers into a tool registry.</summary>
public interface IMcpToolImporter
{
    ValueTask<IReadOnlyList<ToolDefinition>> ImportAsync(
        IToolCallRegistry registry,
        McpServerConfig   server,
        CancellationToken ct = default);
}

/// <summary>(3.3.0) HTTP-backed importer (tools list + invoke via JSON-RPC over HTTP).</summary>
public sealed class HttpMcpToolImporter : IMcpToolImporter
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public HttpMcpToolImporter(HttpClient http, ILogger<HttpMcpToolImporter>? logger = null)
    {
        _http   = http ?? throw new ArgumentNullException(nameof(http));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async ValueTask<IReadOnlyList<ToolDefinition>> ImportAsync(
        IToolCallRegistry registry,
        McpServerConfig   server,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(server);

        var listRequest = new
        {
            jsonrpc = "2.0",
            id      = 1,
            method  = "tools/list",
            @params = new { },
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, server.ServerEndpoint)
        {
            Content = JsonContent.Create(listRequest),
        };
        if (!string.IsNullOrWhiteSpace(server.AuthorizationHeader))
        {
            msg.Headers.Add("Authorization", server.AuthorizationHeader);
        }

        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("MCP server {Endpoint} returned {Status}", server.ServerEndpoint, resp.StatusCode);
            return Array.Empty<ToolDefinition>();
        }

        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("result", out var result)) return Array.Empty<ToolDefinition>();
        if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ToolDefinition>();
        }

        var imported = new List<ToolDefinition>();
        foreach (var entry in tools.EnumerateArray())
        {
            var name        = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
            var description = entry.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var schema      = entry.TryGetProperty("inputSchema", out var s) ? s.GetRawText() : "{}";
            if (string.IsNullOrWhiteSpace(name)) continue;

            var localName = string.IsNullOrWhiteSpace(server.ToolNamePrefix) ? name : $"{server.ToolNamePrefix}{name}";
            var def       = new ToolDefinition(localName, description, schema);

            // Register a webhook-style entry whose invocation forwards back to the MCP server's tools/call method.
            var invokeUrl = AppendQuery(server.ServerEndpoint, "remote_tool", name);
            registry.RegisterWebhook(def, invokeUrl);
            imported.Add(def);
        }

        return imported;
    }

    private static Uri AppendQuery(Uri baseUri, string key, string value)
    {
        var ub = new UriBuilder(baseUri);
        var existing = ub.Query?.TrimStart('?') ?? "";
        var separator = string.IsNullOrEmpty(existing) ? "" : "&";
        ub.Query = existing + separator + key + "=" + Uri.EscapeDataString(value);
        return ub.Uri;
    }
}
