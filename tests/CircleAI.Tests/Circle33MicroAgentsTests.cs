// Circle33MicroAgentsTests.cs

using System;
using System.Threading.Tasks;
using CircleAI.MicroAgents;
using Xunit;

namespace CircleAI.Tests;

public class Circle33MicroAgentsTests
{
    [Fact]
    public async Task FuncAgent_InvokesDelegate()
    {
        var a = new FuncMicroAgent("greeter", "greets", null,
            (input, _) => ValueTask.FromResult(new MicroAgentResponse("greeter", $"hello {input}")));
        var r = await a.InvokeAsync("world");
        Assert.Equal("hello world", r.Output);
    }

    [Fact]
    public void FuncAgent_EmptyId_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new FuncMicroAgent("", "x", null, (_, _) => ValueTask.FromResult(new MicroAgentResponse("x", ""))));
    }

    [Fact]
    public async Task Host_RegisterAndInvoke_RoundTrips()
    {
        var host = new InMemoryMicroAgentHost();
        host.Register(new FuncMicroAgent("calc", "adds", null,
            (i, _) => ValueTask.FromResult(new MicroAgentResponse("calc", i + i))));
        var r = await host.InvokeAsync("calc", "42");
        Assert.Equal("4242", r!.Output);
    }

    [Fact]
    public async Task Host_InvokeUnknown_ReturnsNull()
    {
        var host = new InMemoryMicroAgentHost();
        Assert.Null(await host.InvokeAsync("ghost", "x"));
    }

    [Fact]
    public void Host_List_ReturnsRegisteredDescriptors()
    {
        var host = new InMemoryMicroAgentHost();
        host.Register(new FuncMicroAgent("a1", "d1", new[] { "math" },
            (_, _) => ValueTask.FromResult(new MicroAgentResponse("a1", ""))));
        host.Register(new FuncMicroAgent("a2", "d2", null,
            (_, _) => ValueTask.FromResult(new MicroAgentResponse("a2", ""))));

        var list = host.List();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, d => d.AgentId == "a1");
    }
}
