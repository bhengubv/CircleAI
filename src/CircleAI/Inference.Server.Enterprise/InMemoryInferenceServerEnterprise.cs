// InMemoryInferenceServerEnterprise.cs
//
// (3.3.0) Real in-memory enterprise-tier inference primitives.
// Tenant router: round-robin over registered nodes per model. Batch
// scheduler: real reservation queue with deadline guarantees and
// release. Shard planner: even-bucket split across registered nodes.
// Cross-tier offload: a policy-based decision (offload if prompt too
// large for caller tier).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inference.Server.Enterprise;

public sealed class RoundRobinTenantRouter : ITenantRouter
{
    private readonly ConcurrentDictionary<string, TenantQuota>     _quotas = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<string>>    _nodesByModel = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int>             _rr = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public string BackendId => "round-robin";

    public void RegisterNode(string modelId, string nodeId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId required");
        if (string.IsNullOrWhiteSpace(nodeId))  throw new ArgumentException("nodeId required");
        lock (_lock)
        {
            var list = _nodesByModel.GetOrAdd(modelId, _ => new List<string>());
            if (!list.Contains(nodeId)) list.Add(nodeId);
        }
    }

    public ValueTask<string?> ChooseNodeAsync(TenantContext tenant, string modelId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId required");
        lock (_lock)
        {
            if (!_nodesByModel.TryGetValue(modelId, out var nodes) || nodes.Count == 0)
                return ValueTask.FromResult<string?>(null);
            var idx = _rr.GetOrAdd(modelId, 0);
            var pick = nodes[idx % nodes.Count];
            _rr[modelId] = idx + 1;
            return ValueTask.FromResult<string?>(pick);
        }
    }

    public ValueTask SetQuotaAsync(TenantQuota quota, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quota);
        _quotas[quota.TenantId] = quota;
        return ValueTask.CompletedTask;
    }

    public ValueTask<TenantQuota?> GetQuotaAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("tenantId required", nameof(tenantId));
        _quotas.TryGetValue(tenantId, out var q);
        return ValueTask.FromResult(q);
    }
}

public sealed class InMemoryBatchScheduler : IBatchScheduler
{
    private readonly ConcurrentDictionary<string, BatchSlot> _slots = new(StringComparer.Ordinal);
    private long _seq;

    public string BackendId => "in-memory";

    public ValueTask<BatchSlot> ReserveAsync(string modelId, int estimatedTokens, TimeSpan maxWait, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId required");
        if (estimatedTokens <= 0) throw new ArgumentOutOfRangeException(nameof(estimatedTokens));
        if (maxWait <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxWait));
        var slot = new BatchSlot(
            SlotId:      $"slot-{Interlocked.Increment(ref _seq)}",
            ModelId:     modelId,
            Tokens:      estimatedTokens,
            DeadlineUtc: DateTimeOffset.UtcNow + maxWait);
        _slots[slot.SlotId] = slot;
        return ValueTask.FromResult(slot);
    }

    public ValueTask ReleaseAsync(BatchSlot slot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        _slots.TryRemove(slot.SlotId, out _);
        return ValueTask.CompletedTask;
    }
}

public sealed class EvenSplitModelShardPlanner : IModelShardPlanner
{
    private readonly Func<string, IReadOnlyList<string>> _nodesFor;
    public EvenSplitModelShardPlanner(Func<string, IReadOnlyList<string>> nodesFor)
        => _nodesFor = nodesFor ?? throw new ArgumentNullException(nameof(nodesFor));

    public string BackendId => "even-split";

    public ValueTask<IReadOnlyList<ShardDescriptor>> PlanAsync(string modelId, int paramBytes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId required");
        if (paramBytes <= 0) throw new ArgumentOutOfRangeException(nameof(paramBytes));

        var nodes = _nodesFor(modelId);
        if (nodes is null || nodes.Count == 0) return ValueTask.FromResult<IReadOnlyList<ShardDescriptor>>(Array.Empty<ShardDescriptor>());

        var bucket = paramBytes / nodes.Count;
        var rem    = paramBytes % nodes.Count;
        var list   = new List<ShardDescriptor>(nodes.Count);
        var cursor = 0;
        for (var i = 0; i < nodes.Count; i++)
        {
            var size = bucket + (i < rem ? 1 : 0);
            list.Add(new ShardDescriptor($"shard-{modelId}-{i}", cursor, cursor + size, nodes[i]));
            cursor += size;
        }
        return ValueTask.FromResult<IReadOnlyList<ShardDescriptor>>(list);
    }
}

public sealed class PolicyCrossTierOffload : ICrossTierOffload
{
    private readonly int _localPromptCeiling;
    private readonly string? _farmTargetNode;

    public PolicyCrossTierOffload(int localPromptCeiling = 2048, string? farmTargetNode = null)
    {
        if (localPromptCeiling <= 0) throw new ArgumentOutOfRangeException(nameof(localPromptCeiling));
        _localPromptCeiling = localPromptCeiling;
        _farmTargetNode = farmTargetNode;
    }

    public string BackendId => "policy";

    public ValueTask<OffloadDecision> ShouldOffloadAsync(string modelId, int promptTokens, ServerTier callerTier, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId required");
        if (promptTokens < 0) throw new ArgumentOutOfRangeException(nameof(promptTokens));
        if (callerTier == ServerTier.ServerFarm)
            return ValueTask.FromResult(new OffloadDecision(false, null, "Caller is already top-tier"));
        if (promptTokens <= _localPromptCeiling)
            return ValueTask.FromResult(new OffloadDecision(false, null, "Prompt fits locally"));
        return ValueTask.FromResult(new OffloadDecision(true, _farmTargetNode, $"Prompt exceeds local ceiling ({_localPromptCeiling} tokens)"));
    }
}
