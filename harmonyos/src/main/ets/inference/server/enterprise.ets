/**
 * inference/server/enterprise
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/server/enterprise.ts
//
// Port of CircleAI.Inference.Server.Enterprise (2.7.0 / 3.3.0):
//   Contracts.cs                     — ServerTier, TenantContext, TenantQuota,
//                                      ITenantRouter, BatchSlot, IBatchScheduler,
//                                      ShardDescriptor, IModelShardPlanner,
//                                      OffloadDecision, ICrossTierOffload
//   InMemoryInferenceServerEnterprise.cs — RoundRobinTenantRouter,
//                                      InMemoryBatchScheduler,
//                                      EvenSplitModelShardPlanner,
//                                      PolicyCrossTierOffload
//   NullImplementations.cs           — Null* single-node defaults
//
// Multi-tenant routing + batch scheduling + sharding + cross-tier offload. Real
// in-memory backends; the algorithms (round-robin, even-bucket split, policy
// offload) are byte-faithful.

// ── enums / records ────────────────────────────────────────────────────────────

/** Ported from ServerTier. */
export enum ServerTier {
  SingleNode = 0,
  Server = 1,
  ServerFarm = 2,
}

/** Ported from TenantContext. */
export interface TenantContext {
  readonly tenantId: string;
  readonly parentTenantId?: string | null;
  readonly tags?: Readonly<Map<string, string>> | null;
}

/** Ported from TenantQuota. */
export interface TenantQuota {
  readonly tenantId: string;
  readonly maxConcurrentRequests: number;
  readonly maxModelsLoaded: number;
  readonly maxBytesInFlight: number;
  readonly dailyTokenBudget: number;
}

/** Ported from BatchSlot. */
export interface BatchSlot {
  readonly slotId: string;
  readonly modelId: string;
  readonly tokens: number;
  /** ISO 8601 UTC deadline. */
  readonly deadlineUtc: string;
}

/** Ported from ShardDescriptor. */
export interface ShardDescriptor {
  readonly shardId: string;
  readonly rangeStart: number;
  readonly rangeEnd: number;
  readonly nodeId: string;
}

/** Ported from OffloadDecision. */
export interface OffloadDecision {
  readonly shouldOffload: boolean;
  readonly targetNodeId: string | null;
  readonly reason: string | null;
}

// ── contracts ──────────────────────────────────────────────────────────────────

/** Multi-tenant routing — pick a backend node per tenant. Ported from ITenantRouter. */
export interface ITenantRouter {
  readonly backendId: string;
  chooseNode(tenant: TenantContext, modelId: string, signal?: AbortSignal): Promise<string | null>;
  setQuota(quota: TenantQuota, signal?: AbortSignal): Promise<void>;
  getQuota(tenantId: string, signal?: AbortSignal): Promise<TenantQuota | null>;
}

/** Batch scheduler — coalesce small requests. Ported from IBatchScheduler. */
export interface IBatchScheduler {
  readonly backendId: string;
  reserve(modelId: string, estimatedTokens: number, maxWaitMs: number, signal?: AbortSignal): Promise<BatchSlot>;
  release(slot: BatchSlot, signal?: AbortSignal): Promise<void>;
}

/** Model-sharding plan for very-large-model deployments. Ported from IModelShardPlanner. */
export interface IModelShardPlanner {
  readonly backendId: string;
  plan(modelId: string, paramBytes: number, signal?: AbortSignal): Promise<readonly ShardDescriptor[]>;
}

/** RT-12 v2 cross-tier offload — phone borrows server brain. Ported from ICrossTierOffload. */
export interface ICrossTierOffload {
  readonly backendId: string;
  shouldOffload(
    modelId: string,
    promptTokens: number,
    callerTier: ServerTier,
    signal?: AbortSignal,
  ): Promise<OffloadDecision>;
}

// ── real in-memory implementations ──────────────────────────────────────────────

/** Round-robin over registered nodes per model. Ported from RoundRobinTenantRouter. */
export class RoundRobinTenantRouter implements ITenantRouter {
  private readonly quotas = new Map<string, TenantQuota>();
  private readonly nodesByModel = new Map<string, string[]>();
  private readonly rr = new Map<string, number>();

  readonly backendId = "round-robin";

  registerNode(modelId: string, nodeId: string): void {
    if (!modelId || modelId.trim().length === 0) throw new Error("modelId required");
    if (!nodeId || nodeId.trim().length === 0) throw new Error("nodeId required");
    let list = this.nodesByModel.get(modelId);
    if (!list) {
      list = [];
      this.nodesByModel.set(modelId, list);
    }
    if (!list.includes(nodeId)) list.push(nodeId);
  }

  async chooseNode(tenant: TenantContext, modelId: string, _signal?: AbortSignal): Promise<string | null> {
    if (!tenant) throw new Error("tenant required");
    if (!modelId || modelId.trim().length === 0) throw new Error("modelId required");
    const nodes = this.nodesByModel.get(modelId);
    if (!nodes || nodes.length === 0) return null;
    const idx = this.rr.get(modelId) ?? 0;
    const pick = nodes[idx % nodes.length]!;
    this.rr.set(modelId, idx + 1);
    return pick;
  }

  async setQuota(quota: TenantQuota, _signal?: AbortSignal): Promise<void> {
    if (!quota) throw new Error("quota required");
    this.quotas.set(quota.tenantId, quota);
  }

  async getQuota(tenantId: string, _signal?: AbortSignal): Promise<TenantQuota | null> {
    if (!tenantId || tenantId.trim().length === 0) throw new Error("tenantId required");
    return this.quotas.get(tenantId) ?? null;
  }
}

/** Real reservation queue with deadline guarantees. Ported from InMemoryBatchScheduler. */
export class InMemoryBatchScheduler implements IBatchScheduler {
  private readonly slots = new Map<string, BatchSlot>();
  private seq = 0;

  readonly backendId = "in-memory";

  async reserve(
    modelId: string,
    estimatedTokens: number,
    maxWaitMs: number,
    _signal?: AbortSignal,
  ): Promise<BatchSlot> {
    if (!modelId || modelId.trim().length === 0) throw new Error("modelId required");
    if (estimatedTokens <= 0) throw new RangeError("estimatedTokens must be > 0");
    if (maxWaitMs <= 0) throw new RangeError("maxWait must be > 0");
    const slot: BatchSlot = {
      slotId: `slot-${++this.seq}`,
      modelId,
      tokens: estimatedTokens,
      deadlineUtc: new Date(Date.now() + maxWaitMs).toISOString(),
    };
    this.slots.set(slot.slotId, slot);
    return slot;
  }

  async release(slot: BatchSlot, _signal?: AbortSignal): Promise<void> {
    if (!slot) throw new Error("slot required");
    this.slots.delete(slot.slotId);
  }
}

/** Even-bucket split across registered nodes. Ported from EvenSplitModelShardPlanner. */
export class EvenSplitModelShardPlanner implements IModelShardPlanner {
  private readonly nodesFor: (modelId: string) => readonly string[];

  constructor(nodesFor: (modelId: string) => readonly string[]) {
    if (!nodesFor) throw new Error("nodesFor required");
    this.nodesFor = nodesFor;
  }

  readonly backendId = "even-split";

  async plan(modelId: string, paramBytes: number, _signal?: AbortSignal): Promise<readonly ShardDescriptor[]> {
    if (!modelId || modelId.trim().length === 0) throw new Error("modelId required");
    if (paramBytes <= 0) throw new RangeError("paramBytes must be > 0");

    const nodes = this.nodesFor(modelId);
    if (!nodes || nodes.length === 0) return [];

    const bucket = Math.trunc(paramBytes / nodes.length);
    const rem = paramBytes % nodes.length;
    const list: ShardDescriptor[] = [];
    let cursor = 0;
    for (let i = 0; i < nodes.length; i++) {
      const size = bucket + (i < rem ? 1 : 0);
      list.push({ shardId: `shard-${modelId}-${i}`, rangeStart: cursor, rangeEnd: cursor + size, nodeId: nodes[i]! });
      cursor += size;
    }
    return list;
  }
}

/** Policy-based offload decision. Ported from PolicyCrossTierOffload. */
export class PolicyCrossTierOffload implements ICrossTierOffload {
  private readonly localPromptCeiling: number;
  private readonly farmTargetNode: string | null;

  constructor(localPromptCeiling = 2048, farmTargetNode: string | null = null) {
    if (localPromptCeiling <= 0) throw new RangeError("localPromptCeiling must be > 0");
    this.localPromptCeiling = localPromptCeiling;
    this.farmTargetNode = farmTargetNode;
  }

  readonly backendId = "policy";

  async shouldOffload(
    modelId: string,
    promptTokens: number,
    callerTier: ServerTier,
    _signal?: AbortSignal,
  ): Promise<OffloadDecision> {
    if (!modelId || modelId.trim().length === 0) throw new Error("modelId required");
    if (promptTokens < 0) throw new RangeError("promptTokens must be >= 0");
    if (callerTier === ServerTier.ServerFarm) {
      return { shouldOffload: false, targetNodeId: null, reason: "Caller is already top-tier" };
    }
    if (promptTokens <= this.localPromptCeiling) {
      return { shouldOffload: false, targetNodeId: null, reason: "Prompt fits locally" };
    }
    return {
      shouldOffload: true,
      targetNodeId: this.farmTargetNode,
      reason: `Prompt exceeds local ceiling (${this.localPromptCeiling} tokens)`,
    };
  }
}

// ── null single-node defaults ────────────────────────────────────────────────

/** Ported from NullTenantRouter. */
export class NullTenantRouter implements ITenantRouter {
  static readonly instance = new NullTenantRouter();
  readonly backendId = "null";
  async chooseNode(_tenant: TenantContext, _modelId: string, _signal?: AbortSignal): Promise<string | null> {
    return null;
  }
  async setQuota(_quota: TenantQuota, _signal?: AbortSignal): Promise<void> {
    /* no-op */
  }
  async getQuota(_tenantId: string, _signal?: AbortSignal): Promise<TenantQuota | null> {
    return null;
  }
}

/** Ported from NullBatchScheduler. */
export class NullBatchScheduler implements IBatchScheduler {
  static readonly instance = new NullBatchScheduler();
  readonly backendId = "null";
  async reserve(modelId: string, est: number, maxWaitMs: number, _signal?: AbortSignal): Promise<BatchSlot> {
    return {
      slotId: "00000000-0000-0000-0000-000000000000",
      modelId,
      tokens: est,
      deadlineUtc: new Date(Date.now() + maxWaitMs).toISOString(),
    };
  }
  async release(_slot: BatchSlot, _signal?: AbortSignal): Promise<void> {
    /* no-op */
  }
}

/** Ported from NullModelShardPlanner. */
export class NullModelShardPlanner implements IModelShardPlanner {
  static readonly instance = new NullModelShardPlanner();
  readonly backendId = "null";
  async plan(_modelId: string, _paramBytes: number, _signal?: AbortSignal): Promise<readonly ShardDescriptor[]> {
    return [];
  }
}

/** Ported from NullCrossTierOffload. */
export class NullCrossTierOffload implements ICrossTierOffload {
  static readonly instance = new NullCrossTierOffload();
  readonly backendId = "null";
  async shouldOffload(
    _modelId: string,
    _promptTokens: number,
    _tier: ServerTier,
    _signal?: AbortSignal,
  ): Promise<OffloadDecision> {
    return {
      shouldOffload: false,
      targetNodeId: null,
      reason: "Local execution; no cross-tier offload configured.",
    };
  }
}
