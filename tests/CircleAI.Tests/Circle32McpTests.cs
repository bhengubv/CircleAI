// Circle32McpTests.cs
//
// (3.2.0) Tests for CircleAI.Hosting.Mcp — JSON-RPC 2.0 dispatcher
// behaviour via the testable DispatchAsync entry point: initialize,
// tools/list, tools/call (success + tool-level error + unknown tool),
// resources/list, resources/read (success + unknown scheme + missing
// uri), and notifications (no response).

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle32McpTests
{
    private sealed class EchoTool : IMcpTool
    {
        public string Name        => "echo";
        public string Description => "Returns the message you sent.";
        public object InputSchema => new
        {
            type       = "object",
            properties = new { message = new { type = "string" } },
            required   = new[] { "message" },
        };

        public Task<object> ExecuteAsync(JsonObject arguments, CancellationToken ct = default)
        {
            var msg = arguments["message"]?.GetValue<string>() ?? "";
            return Task.FromResult<object>(new { echoed = msg });
        }
    }

    private sealed class FailingTool : IMcpTool
    {
        public string Name        => "fail";
        public string Description => "Always raises a tool error.";
        public object InputSchema => new { type = "object", properties = new { } };
        public Task<object> ExecuteAsync(JsonObject arguments, CancellationToken ct = default)
            => throw new McpToolException("bad thing happened");
    }

    private sealed class StaticResourceProvider : IMcpResourceProvider
    {
        public string UriScheme => "test://";
        public Task<IReadOnlyList<McpResource>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpResource>>(new[]
            {
                new McpResource("test://one", "One", "First", "text/plain"),
            });
        public Task<McpResourceContent?> ReadAsync(string uri, CancellationToken ct = default)
            => Task.FromResult<McpResourceContent?>(
                uri == "test://one" ? new McpResourceContent(uri, "text/plain", "hello") : null);
    }

    private static IServiceProvider BuildServices(params Action<ServiceCollection>[] configure)
    {
        var s = new ServiceCollection();
        foreach (var c in configure) c(s);
        return s.BuildServiceProvider();
    }

    private static JsonNode Req(int id, string method, object? @params = null)
    {
        var node = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = id,
            ["method"]  = method,
        };
        if (@params is not null)
            node["params"] = JsonNode.Parse(JsonSerializer.Serialize(@params));
        return node;
    }

    private static string SerializeResponse(object? response) => JsonSerializer.Serialize(response);

    // ── initialize / notifications ────────────────────────────────────

    [Fact]
    public async Task Initialize_ReturnsProtocolVersionAndServerInfo()
    {
        var sp = BuildServices();
        var info = new McpEndpoints.McpServerInfo { Name = "test-mcp", Version = "9.9.9" };
        var resp = await McpEndpoints.DispatchAsync(Req(1, "initialize"), sp, info);
        var json = SerializeResponse(resp);
        Assert.Contains("\"protocolVersion\":\"2024-11-05\"", json);
        Assert.Contains("\"name\":\"test-mcp\"", json);
        Assert.Contains("\"version\":\"9.9.9\"", json);
    }

    [Fact]
    public async Task NotificationsInitialized_ReturnsNull()
    {
        var sp = BuildServices();
        var resp = await McpEndpoints.DispatchAsync(Req(1, "notifications/initialized"), sp, new McpEndpoints.McpServerInfo());
        Assert.Null(resp);
    }

    // ── tools/list ────────────────────────────────────────────────────

    [Fact]
    public async Task ToolsList_ReturnsRegisteredTools()
    {
        var sp = BuildServices(
            s => s.AddMcpTool<EchoTool>(),
            s => s.AddMcpTool<FailingTool>());

        var resp = await McpEndpoints.DispatchAsync(Req(2, "tools/list"), sp, new McpEndpoints.McpServerInfo());
        var json = SerializeResponse(resp);
        Assert.Contains("\"name\":\"echo\"", json);
        Assert.Contains("\"name\":\"fail\"", json);
    }

    // ── tools/call ────────────────────────────────────────────────────

    [Fact]
    public async Task ToolsCall_RoundTripsResult()
    {
        var sp = BuildServices(s => s.AddMcpTool<EchoTool>());
        var resp = await McpEndpoints.DispatchAsync(
            Req(3, "tools/call", new { name = "echo", arguments = new { message = "hi" } }),
            sp, new McpEndpoints.McpServerInfo());
        var json = SerializeResponse(resp);
        Assert.Contains("\"isError\":false", json);
        Assert.Contains("echoed", json);
        Assert.Contains("hi", json);
    }

    [Fact]
    public async Task ToolsCall_McpToolException_ReturnsIsErrorTrue()
    {
        var sp = BuildServices(s => s.AddMcpTool<FailingTool>());
        var resp = await McpEndpoints.DispatchAsync(
            Req(4, "tools/call", new { name = "fail", arguments = new { } }),
            sp, new McpEndpoints.McpServerInfo());
        var json = SerializeResponse(resp);
        Assert.Contains("\"isError\":true", json);
        Assert.Contains("bad thing happened", json);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsProtocolError()
    {
        var sp = BuildServices(s => s.AddMcpTool<EchoTool>());
        var resp = await McpEndpoints.DispatchAsync(
            Req(5, "tools/call", new { name = "nope" }),
            sp, new McpEndpoints.McpServerInfo());
        var json = SerializeResponse(resp);
        Assert.Contains("\"code\":-32602", json);
        Assert.Contains("Unknown tool", json);
    }

    [Fact]
    public async Task ToolsCall_MissingName_ReturnsProtocolError()
    {
        var sp = BuildServices(s => s.AddMcpTool<EchoTool>());
        var resp = await McpEndpoints.DispatchAsync(
            Req(6, "tools/call", new { arguments = new { } }),
            sp, new McpEndpoints.McpServerInfo());
        var json = SerializeResponse(resp);
        Assert.Contains("\"code\":-32602", json);
        Assert.Contains("name", json);
        Assert.Contains("is required", json);
    }

    // ── resources/list and resources/read ─────────────────────────────

    [Fact]
    public async Task ResourcesList_AggregatesProviders()
    {
        var sp = BuildServices(s => s.AddMcpResourceProvider<StaticResourceProvider>());
        var resp = await McpEndpoints.DispatchAsync(Req(7, "resources/list"), sp, new McpEndpoints.McpServerInfo());
        var json = SerializeResponse(resp);
        Assert.Contains("\"uri\":\"test://one\"", json);
        Assert.Contains("\"mimeType\":\"text/plain\"", json);
    }

    [Fact]
    public async Task ResourcesRead_RoundTripsContent()
    {
        var sp = BuildServices(s => s.AddMcpResourceProvider<StaticResourceProvider>());
        var resp = await McpEndpoints.DispatchAsync(
            Req(8, "resources/read", new { uri = "test://one" }),
            sp, new McpEndpoints.McpServerInfo());
        var json = SerializeResponse(resp);
        Assert.Contains("\"text\":\"hello\"", json);
    }

    [Fact]
    public async Task ResourcesRead_UnknownScheme_Errors()
    {
        var sp = BuildServices(s => s.AddMcpResourceProvider<StaticResourceProvider>());
        var resp = await McpEndpoints.DispatchAsync(
            Req(9, "resources/read", new { uri = "weird://thing" }),
            sp, new McpEndpoints.McpServerInfo());
        var json = SerializeResponse(resp);
        Assert.Contains("No provider for URI scheme", json);
    }

    [Fact]
    public async Task ResourcesRead_MissingUri_Errors()
    {
        var sp = BuildServices(s => s.AddMcpResourceProvider<StaticResourceProvider>());
        var resp = await McpEndpoints.DispatchAsync(
            Req(10, "resources/read", new { }),
            sp, new McpEndpoints.McpServerInfo());
        var json = SerializeResponse(resp);
        Assert.Contains("uri", json);
        Assert.Contains("is required", json);
    }

    [Fact]
    public async Task ResourcesRead_NotFound_Errors()
    {
        var sp = BuildServices(s => s.AddMcpResourceProvider<StaticResourceProvider>());
        var resp = await McpEndpoints.DispatchAsync(
            Req(11, "resources/read", new { uri = "test://missing" }),
            sp, new McpEndpoints.McpServerInfo());
        var json = SerializeResponse(resp);
        Assert.Contains("Resource not found", json);
    }

    // ── unknown method / malformed ────────────────────────────────────

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var sp = BuildServices();
        var resp = await McpEndpoints.DispatchAsync(Req(12, "what/is/this"), sp, new McpEndpoints.McpServerInfo());
        var json = SerializeResponse(resp);
        Assert.Contains("\"code\":-32601", json);
    }

    [Fact]
    public async Task MissingJsonRpcVersion_ReturnsInvalidRequest()
    {
        var sp = BuildServices();
        var bad = new JsonObject { ["id"] = 1, ["method"] = "initialize" }; // no jsonrpc
        var resp = await McpEndpoints.DispatchAsync(bad, sp, new McpEndpoints.McpServerInfo());
        var json = SerializeResponse(resp);
        Assert.Contains("\"code\":-32600", json);
    }

    [Fact]
    public async Task NullRequest_ReturnsInvalidRequest()
    {
        var sp = BuildServices();
        var resp = await McpEndpoints.DispatchAsync(null, sp, new McpEndpoints.McpServerInfo());
        var json = SerializeResponse(resp);
        Assert.Contains("\"code\":-32600", json);
    }
}
