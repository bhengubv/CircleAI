// NullImplementations.cs
//
// (2.7.0) Single-node defaults — fall back to local execution.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inference.Server.Enterprise;

public sealed class NullTenantRouter : ITenantRouter
{
    public static readonly NullTenantRouter Instance = new();
    public string BackendId => "null";
    public ValueTask<string?> ChooseNodeAsync(TenantContext tenant, string modelId, CancellationToken ct = default)
        => ValueTask.FromResult<string?>(null);
    public ValueTask SetQuotaAsync(TenantQuota q, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<TenantQuota?> GetQuotaAsync(string tenantId, CancellationToken ct = default)
        => ValueTask.FromResult<TenantQuota?>(null);
}

public sealed class NullBatchScheduler : IBatchScheduler
{
    public static readonly NullBatchScheduler Instance = new();
    public string BackendId => "null";
    public ValueTask<BatchSlot> ReserveAsync(string modelId, int est, TimeSpan maxWait, CancellationToken ct = default)
        => ValueTask.FromResult(new BatchSlot(
            SlotId:      Guid.Empty.ToString(),
            ModelId:     modelId,
            Tokens:      est,
            DeadlineUtc: DateTimeOffset.UtcNow.Add(maxWait)));
    public ValueTask ReleaseAsync(BatchSlot slot, CancellationToken ct = default) => ValueTask.CompletedTask;
}

public sealed class NullModelShardPlanner : IModelShardPlanner
{
    public static readonly NullModelShardPlanner Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<ShardDescriptor>> PlanAsync(string modelId, int paramBytes, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ShardDescriptor>>(Array.Empty<ShardDescriptor>());
}

public sealed class NullCrossTierOffload : ICrossTierOffload
{
    public static readonly NullCrossTierOffload Instance = new();
    public string BackendId => "null";
    public ValueTask<OffloadDecision> ShouldOffloadAsync(string modelId, int promptTokens, ServerTier tier, CancellationToken ct = default)
        => ValueTask.FromResult(new OffloadDecision(
            ShouldOffload: false,
            TargetNodeId:  null,
            Reason:        "Local execution; no cross-tier offload configured."));
}
