// Circle33PacaMcpTests.cs
//
// (3.3.0) Tests for Paca MCP server tooling.

using System.Threading.Tasks;
using CircleAI.Workflows;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PacaMcpTests
{
    [Fact]
    public void RegisterTool_ShowsInList()
    {
        var s = new PacaMcpServer();
        s.RegisterTool(PacaCoreMcpTools.CreateTask, (_, _) => ValueTask.FromResult("{}"));
        Assert.Single(s.Tools);
    }

    [Fact]
    public async Task InvokeAsync_DelegatesToHandler()
    {
        var s = new PacaMcpServer();
        s.RegisterTool(PacaCoreMcpTools.CreateTask, (args, _) => ValueTask.FromResult($"got:{args}"));
        var r = await s.InvokeAsync("agent1", "create_task", """{"project_id":"p","title":"x"}""");
        Assert.StartsWith("got:", r);
    }

    [Fact]
    public async Task InvokeAsync_UnknownTool_ReturnsError()
    {
        var s = new PacaMcpServer();
        var r = await s.InvokeAsync("agent1", "ghost", "{}");
        Assert.Contains("Unknown tool", r);
    }

    [Fact]
    public async Task ConfigureAgent_RestrictsToolset()
    {
        var s = new PacaMcpServer();
        s.RegisterTool(PacaCoreMcpTools.CreateTask, (_, _) => ValueTask.FromResult("{}"));
        s.RegisterTool(PacaCoreMcpTools.ListTasks,  (_, _) => ValueTask.FromResult("{}"));
        s.ConfigureAgent(new AgentMcpConfig("agent1",
            Transports:    new[] { McpTransportKind.Stdio, McpTransportKind.Http },
            EnabledTools:  new[] { "list_tasks" },
            ToolSettings:  new System.Collections.Generic.Dictionary<string, string>()));

        var blocked = await s.InvokeAsync("agent1", "create_task", "{}");
        Assert.Contains("not enabled", blocked);

        var ok = await s.InvokeAsync("agent1", "list_tasks", "{}");
        Assert.DoesNotContain("not enabled", ok);
    }

    [Fact]
    public void ToolsListJson_IncludesAllSchemas()
    {
        var s = new PacaMcpServer();
        s.RegisterTool(PacaCoreMcpTools.CreateTask, (_, _) => ValueTask.FromResult("{}"));
        s.RegisterTool(PacaCoreMcpTools.ListTasks,  (_, _) => ValueTask.FromResult("{}"));
        var json = s.ToolsListJson();
        Assert.Contains("create_task", json);
        Assert.Contains("list_tasks",  json);
    }

    [Fact]
    public async Task InvokeAsync_HandlerThrows_WrappedAsError()
    {
        var s = new PacaMcpServer();
        s.RegisterTool(PacaCoreMcpTools.CreateTask, (_, _) => throw new System.InvalidOperationException("bad input"));
        var r = await s.InvokeAsync("agent1", "create_task", "{}");
        Assert.Contains("bad input", r);
    }

    [Fact]
    public void GetAgentConfig_ReturnsConfigured()
    {
        var s = new PacaMcpServer();
        s.ConfigureAgent(new AgentMcpConfig("agent1",
            new[] { McpTransportKind.Stdio },
            new[] { "list_tasks" },
            new System.Collections.Generic.Dictionary<string, string>()));
        var cfg = s.GetAgentConfig("agent1");
        Assert.NotNull(cfg);
        Assert.Contains(McpTransportKind.Stdio, cfg!.Transports);
    }

    [Fact]
    public void GetAgentConfig_Unknown_ReturnsNull()
    {
        var s = new PacaMcpServer();
        Assert.Null(s.GetAgentConfig("ghost"));
    }
}
