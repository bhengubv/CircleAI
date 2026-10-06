// InMemoryMicroAgents.cs
//
// (3.3.0) Real IMicroAgentHost that keeps a registry of agents and
// routes Invoke calls to them. A helper FuncMicroAgent lets callers
// build agents from a lambda without authoring a new type per agent.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.MicroAgents;

/// <summary>(3.3.0) Wrap a delegate in an IMicroAgent so callers can register lambdas.</summary>
public sealed class FuncMicroAgent : IMicroAgent
{
    private readonly Func<string, CancellationToken, ValueTask<MicroAgentResponse>> _impl;

    public FuncMicroAgent(string agentId, string description, IReadOnlyList<string>? capabilities,
        Func<string, CancellationToken, ValueTask<MicroAgentResponse>> impl)
    {
        if (string.IsNullOrWhiteSpace(agentId)) throw new ArgumentException("agentId required", nameof(agentId));
        AgentId    = agentId;
        Descriptor = new MicroAgentDescriptor(agentId, description ?? "", capabilities ?? Array.Empty<string>());
        _impl      = impl ?? throw new ArgumentNullException(nameof(impl));
    }

    public string                 AgentId    { get; }
    public string                 BackendId  => "func";
    public MicroAgentDescriptor   Descriptor { get; }

    public ValueTask<MicroAgentResponse> InvokeAsync(string input, CancellationToken ct = default)
        => _impl(input, ct);
}

// (3.3.0) Note: InMemoryMicroAgentHost lives in NullImplementations.cs as a
// real (not null) implementation — kept there for backward-compat. This file
// only adds the FuncMicroAgent helper.
