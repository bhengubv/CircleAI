// Contracts.cs
//
// (2.7.0) Enterprise-tier inference-server contracts. Multi-tenant
// routing + gRPC streaming + batch scheduling + sharding + cross-tier
// offload (RT-12 v2). Real backends in 2.7.1.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inference.Server.Enterprise;

public enum ServerTier { SingleNode, Server, ServerFarm }

public sealed record TenantContext(string TenantId, string? ParentTenantId, IReadOnlyDictionary<string, string>? Tags = null);

public sealed record TenantQuota(
    string TenantId,
    int    MaxConcurrentRequests,
    int    MaxModelsLoaded,
    long   MaxBytesInFlight,
    int    DailyTokenBudget);

/// <summary>(2.7.0) Multi-tenant routing — pick a backend node per tenant.</summary>
public interface ITenantRouter
{
    string BackendId { get; }

    ValueTask<string?> ChooseNodeAsync(TenantContext tenant, string modelId, CancellationToken ct = default);

    ValueTask SetQuotaAsync(TenantQuota quota, CancellationToken ct = default);
    ValueTask<TenantQuota?> GetQuotaAsync(string tenantId, CancellationToken ct = default);
}

public sealed record BatchSlot(string SlotId, string ModelId, int Tokens, DateTimeOffset DeadlineUtc);

/// <summary>(2.7.0) Batch scheduler — coalesce small requests into one big one.</summary>
public interface IBatchScheduler
{
    string BackendId { get; }

    ValueTask<BatchSlot> ReserveAsync(string modelId, int estimatedTokens, TimeSpan maxWait, CancellationToken ct = default);
    ValueTask ReleaseAsync(BatchSlot slot, CancellationToken ct = default);
}

public sealed record ShardDescriptor(string ShardId, int RangeStart, int RangeEnd, string NodeId);

/// <summary>(2.7.0) Model-sharding plan for very-large-model deployments.</summary>
public interface IModelShardPlanner
{
    string BackendId { get; }

    ValueTask<IReadOnlyList<ShardDescriptor>> PlanAsync(string modelId, int paramBytes, CancellationToken ct = default);
}

public sealed record OffloadDecision(bool ShouldOffload, string? TargetNodeId, string? Reason);

/// <summary>(2.7.0) RT-12 v2 cross-tier offload — phone borrows server brain.</summary>
public interface ICrossTierOffload
{
    string BackendId { get; }

    ValueTask<OffloadDecision> ShouldOffloadAsync(
        string            modelId,
        int               promptTokens,
        ServerTier        callerTier,
        CancellationToken ct = default);
}
