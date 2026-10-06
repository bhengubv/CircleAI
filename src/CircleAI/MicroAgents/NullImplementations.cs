// NullImplementations.cs — (2.9.0)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.MicroAgents;

public sealed class NullMicroAgent : IMicroAgent
{
    public string AgentId => "null";
    public string BackendId => "null";
    public MicroAgentDescriptor Descriptor { get; } = new("null", "No-op micro agent", Array.Empty<string>());
    public ValueTask<MicroAgentResponse> InvokeAsync(string input, CancellationToken ct = default)
        => ValueTask.FromResult(new MicroAgentResponse(AgentId, ""));
}

public sealed class InMemoryMicroAgentHost : IMicroAgentHost
{
    public string BackendId => "in-memory";
    private readonly ConcurrentDictionary<string, IMicroAgent> _agents = new();

    public void Register(IMicroAgent agent) => _agents[agent.AgentId] = agent;

    public IReadOnlyList<MicroAgentDescriptor> List()
    {
        var l = new List<MicroAgentDescriptor>(_agents.Count);
        foreach (var kv in _agents) l.Add(kv.Value.Descriptor);
        return l;
    }

    public async ValueTask<MicroAgentResponse?> InvokeAsync(string agentId, string input, CancellationToken ct = default)
    {
        if (_agents.TryGetValue(agentId, out var a))
            return await a.InvokeAsync(input, ct);
        return null;
    }
}
