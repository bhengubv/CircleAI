// Circle33McpImporterTests.cs
//
// (3.3.0) Tests for MCP tool importer.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33McpImporterTests
{
    [Fact]
    public async Task Import_ParsesToolsList_RegistersEachTool()
    {
        var handler = new McpHandler((_ => true, Json("""
        {
          "jsonrpc": "2.0",
          "id": 1,
          "result": {
            "tools": [
              { "name": "get_weather",   "description": "Look up weather.",  "inputSchema": { "type": "object" } },
              { "name": "send_email",    "description": "Send email.",        "inputSchema": { "type": "object" } }
            ]
          }
        }
        """)));
        var importer = new HttpMcpToolImporter(new HttpClient(handler));
        var registry = new DefaultToolCallRegistry(new HttpClient());

        var imported = await importer.ImportAsync(registry,
            new McpServerConfig(new Uri("https://mcp.example.com/jsonrpc")));

        Assert.Equal(2, imported.Count);
        Assert.Contains(imported, t => t.Name == "get_weather");
        Assert.Contains(imported, t => t.Name == "send_email");
        Assert.Equal(2, registry.Definitions.Count);
    }

    [Fact]
    public async Task Import_AppliesPrefix()
    {
        var handler = new McpHandler((_ => true, Json("""
        {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"x","description":"d","inputSchema":{}}]}}
        """)));
        var importer = new HttpMcpToolImporter(new HttpClient(handler));
        var registry = new DefaultToolCallRegistry(new HttpClient());

        var imported = await importer.ImportAsync(registry,
            new McpServerConfig(new Uri("https://mcp.example.com/jsonrpc"), ToolNamePrefix: "remote_"));

        Assert.Single(imported);
        Assert.Equal("remote_x", imported[0].Name);
    }

    [Fact]
    public async Task Import_AttachesAuthorizationHeader()
    {
        var handler = new McpHandler((_ => true, Json("""{"jsonrpc":"2.0","id":1,"result":{"tools":[]}}""")));
        var importer = new HttpMcpToolImporter(new HttpClient(handler));
        var registry = new DefaultToolCallRegistry(new HttpClient());

        await importer.ImportAsync(registry,
            new McpServerConfig(new Uri("https://mcp.example.com/jsonrpc"),
                AuthorizationHeader: "Bearer test"));

        Assert.Equal("Bearer test", handler.Requests[0].Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Import_ServerError_ReturnsEmpty()
    {
        var handler = new McpHandler((_ => true, new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var importer = new HttpMcpToolImporter(new HttpClient(handler));
        var registry = new DefaultToolCallRegistry(new HttpClient());

        var imported = await importer.ImportAsync(registry,
            new McpServerConfig(new Uri("https://mcp.example.com/jsonrpc")));

        Assert.Empty(imported);
    }

    [Fact]
    public async Task Import_MalformedBody_ReturnsEmpty()
    {
        var handler = new McpHandler((_ => true, Json("""{"jsonrpc":"2.0"}""")));
        var importer = new HttpMcpToolImporter(new HttpClient(handler));
        var registry = new DefaultToolCallRegistry(new HttpClient());

        var imported = await importer.ImportAsync(registry,
            new McpServerConfig(new Uri("https://mcp.example.com/jsonrpc")));

        Assert.Empty(imported);
    }

    [Fact]
    public async Task Import_EmptyToolName_IsSkipped()
    {
        var handler = new McpHandler((_ => true, Json("""
        {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"","description":"d","inputSchema":{}}]}}
        """)));
        var importer = new HttpMcpToolImporter(new HttpClient(handler));
        var registry = new DefaultToolCallRegistry(new HttpClient());

        var imported = await importer.ImportAsync(registry,
            new McpServerConfig(new Uri("https://mcp.example.com/jsonrpc")));

        Assert.Empty(imported);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class McpHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public McpHandler(params (Func<HttpRequestMessage, bool>, HttpResponseMessage)[] responses)
        {
            _responses = responses.ToList();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            for (int i = 0; i < _responses.Count; i++)
            {
                if (_responses[i].Match(request))
                {
                    var resp = _responses[i].Response;
                    _responses.RemoveAt(i);
                    return Task.FromResult(resp);
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
