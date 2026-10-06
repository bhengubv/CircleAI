// MicroAgentHelpers.cs
//
// (3.3.0) Top-up: capability search + invoke-history helpers around
// the existing InMemoryMicroAgentHost (which lives in
// NullImplementations.cs and is a real implementation).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.MicroAgents;

public sealed record MicroAgentInvocation(string AgentId, string Input, string ResponseText, DateTimeOffset AtUtc);

/// <summary>(3.3.0) Capability filter — find agents whose descriptor advertises a capability tag.</summary>
public static class MicroAgentSearch
{
    public static IReadOnlyList<MicroAgentDescriptor> ByCapability(IEnumerable<MicroAgentDescriptor> all, string capability)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (string.IsNullOrWhiteSpace(capability)) throw new ArgumentException("capability required", nameof(capability));
        return all.Where(d => d.Capabilities.Any(c => string.Equals(c, capability, StringComparison.OrdinalIgnoreCase)))
                  .OrderBy(d => d.AgentId).ToArray();
    }

    public static IReadOnlyList<MicroAgentDescriptor> Search(IEnumerable<MicroAgentDescriptor> all, string query, int topK = 10)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        return all.Where(d =>
                d.AgentId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.Capabilities.Any(c => c.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(topK).ToArray();
    }
}

/// <summary>(3.3.0) Keep an in-memory invocation log.</summary>
public sealed class MicroAgentInvocationLog
{
    private readonly List<MicroAgentInvocation> _items = new();
    private readonly object _lock = new();

    public void Append(MicroAgentInvocation i) { ArgumentNullException.ThrowIfNull(i); lock (_lock) _items.Add(i); }
    public IReadOnlyList<MicroAgentInvocation> ForAgent(string agentId, int limit = 50)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_lock) return _items.Where(i => i.AgentId == agentId).OrderByDescending(i => i.AtUtc).Take(limit).ToArray();
    }
    public int TotalInvocations { get { lock (_lock) return _items.Count; } }
}
