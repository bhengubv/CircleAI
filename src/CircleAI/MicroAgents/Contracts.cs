// Contracts.cs — (2.9.0) Micro-agent contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.MicroAgents;

public sealed record MicroAgentDescriptor(string AgentId, string Description, IReadOnlyList<string> Capabilities);
public sealed record MicroAgentResponse(string AgentId, string Output, IReadOnlyDictionary<string, string>? Metadata = null);

public interface IMicroAgent
{
    string AgentId { get; }
    string BackendId { get; }
    MicroAgentDescriptor Descriptor { get; }
    ValueTask<MicroAgentResponse> InvokeAsync(string input, CancellationToken ct = default);
}

public interface IMicroAgentHost
{
    string BackendId { get; }
    void Register(IMicroAgent agent);
    IReadOnlyList<MicroAgentDescriptor> List();
    ValueTask<MicroAgentResponse?> InvokeAsync(string agentId, string input, CancellationToken ct = default);
}
