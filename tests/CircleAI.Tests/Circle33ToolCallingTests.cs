// Circle33ToolCallingTests.cs
//
// (3.3.0) Tests for tool-call registry (local + webhook).

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

public class Circle33ToolCallingTests
{
    private static readonly ToolDefinition GetOrderDef = new(
        Name: "get_order",
        Description: "Look up an order by id.",
        ArgumentsJsonSchema: """{"type":"object","properties":{"order_id":{"type":"string"}}}""");

    [Fact]
    public async Task Local_Handler_IsInvokedWithArguments()
    {
        var reg = new DefaultToolCallRegistry(new HttpClient());
        var seen = "";
        reg.RegisterLocal(GetOrderDef, (args, ct) =>
        {
            seen = args;
            return ValueTask.FromResult("""{"order_id":"42","status":"shipped"}""");
        });

        var r = await reg.InvokeAsync(new ToolInvocation("c1", "get_order", """{"order_id":"42"}"""));

        Assert.True(r.Succeeded);
        Assert.Equal("""{"order_id":"42"}""", seen);
        Assert.Contains("shipped", r.ResultJson);
    }

    [Fact]
    public async Task Local_Handler_Throws_ReportsFailure()
    {
        var reg = new DefaultToolCallRegistry(new HttpClient());
        reg.RegisterLocal(GetOrderDef, (_, _) => throw new InvalidOperationException("boom"));

        var r = await reg.InvokeAsync(new ToolInvocation("c1", "get_order", "{}"));

        Assert.False(r.Succeeded);
        Assert.Contains("boom", r.Error);
    }

    [Fact]
    public async Task Webhook_PostsArgumentsAndReturnsBody()
    {
        var handler = new ToolHandler((_ => true,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"answer":42}""", Encoding.UTF8, "application/json"),
            }));
        var reg = new DefaultToolCallRegistry(new HttpClient(handler));
        reg.RegisterWebhook(GetOrderDef, new Uri("https://example.com/tools/get_order"));

        var r = await reg.InvokeAsync(new ToolInvocation("c1", "get_order", """{"order_id":"42"}"""));

        Assert.True(r.Succeeded);
        Assert.Contains("42", r.ResultJson);
        Assert.Single(handler.Requests);
        Assert.Equal("https://example.com/tools/get_order", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Webhook_Non200_ReportsError()
    {
        var handler = new ToolHandler((_ => true, new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("bad json"),
        }));
        var reg = new DefaultToolCallRegistry(new HttpClient(handler));
        reg.RegisterWebhook(GetOrderDef, new Uri("https://example.com/tools/get_order"));

        var r = await reg.InvokeAsync(new ToolInvocation("c1", "get_order", "{}"));

        Assert.False(r.Succeeded);
        Assert.Contains("400", r.Error);
    }

    [Fact]
    public async Task UnknownTool_ReportsFailure()
    {
        var reg = new DefaultToolCallRegistry(new HttpClient());
        var r = await reg.InvokeAsync(new ToolInvocation("c1", "ghost", "{}"));
        Assert.False(r.Succeeded);
        Assert.Contains("not registered", r.Error);
    }

    [Fact]
    public void Register_NullDef_Throws()
    {
        var reg = new DefaultToolCallRegistry(new HttpClient());
        Assert.Throws<ArgumentNullException>(() => reg.RegisterLocal(null!, (_, _) => ValueTask.FromResult("")));
    }

    [Fact]
    public void Register_RelativeUri_Throws()
    {
        var reg = new DefaultToolCallRegistry(new HttpClient());
        Assert.Throws<ArgumentException>(() =>
            reg.RegisterWebhook(GetOrderDef, new Uri("/relative", UriKind.Relative)));
    }

    [Fact]
    public void Definitions_ListsRegistered()
    {
        var reg = new DefaultToolCallRegistry(new HttpClient());
        reg.RegisterLocal(GetOrderDef, (_, _) => ValueTask.FromResult("{}"));
        reg.RegisterWebhook(new ToolDefinition("ping", "p", "{}"), new Uri("https://example.com/ping"));

        Assert.Equal(2, reg.Definitions.Count);
        Assert.Contains(reg.Definitions, d => d.Name == "get_order");
        Assert.Contains(reg.Definitions, d => d.Name == "ping");
    }

    private sealed class ToolHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public ToolHandler(params (Func<HttpRequestMessage, bool>, HttpResponseMessage)[] responses)
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
