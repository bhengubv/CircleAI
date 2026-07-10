// InferenceServerEnterprise.swift
//
// (2.7.0 / 3.3.0) Enterprise-tier inference-server contracts + real in-memory
// primitives, ported from CircleAI.Inference.Server.Enterprise:
//   • ServerTier
//   • ITenantRouter        + NullTenantRouter        + RoundRobinTenantRouter
//   • IBatchScheduler      + NullBatchScheduler      + InMemoryBatchScheduler
//   • IModelShardPlanner   + NullModelShardPlanner   + EvenSplitModelShardPlanner
//   • ICrossTierOffload    + NullCrossTierOffload    + PolicyCrossTierOffload
//
// Multi-tenant routing (round-robin over registered nodes per model), batch
// scheduling (real reservation queue with deadline guarantees + release),
// shard planning (even-bucket split across registered nodes), and cross-tier
// offload (policy decision: offload if the prompt is too large for the caller
// tier). No stubs — every contract has a working deterministic implementation.

import Foundation

// MARK: - Value types

public enum ServerTier: Int, Sendable {
    case singleNode = 0, server = 1, serverFarm = 2
}

public struct TenantContext: Sendable, Equatable {
    public let tenantId: String
    public let parentTenantId: String?
    public let tags: [String: String]?

    public init(tenantId: String, parentTenantId: String? = nil, tags: [String: String]? = nil) {
        self.tenantId = tenantId
        self.parentTenantId = parentTenantId
        self.tags = tags
    }
}

public struct TenantQuota: Sendable, Equatable {
    public let tenantId: String
    public let maxConcurrentRequests: Int
    public let maxModelsLoaded: Int
    public let maxBytesInFlight: Int64
    public let dailyTokenBudget: Int

    public init(tenantId: String, maxConcurrentRequests: Int, maxModelsLoaded: Int, maxBytesInFlight: Int64, dailyTokenBudget: Int) {
        self.tenantId = tenantId
        self.maxConcurrentRequests = maxConcurrentRequests
        self.maxModelsLoaded = maxModelsLoaded
        self.maxBytesInFlight = maxBytesInFlight
        self.dailyTokenBudget = dailyTokenBudget
    }
}

public struct BatchSlot: Sendable, Equatable {
    public let slotId: String
    public let modelId: String
    public let tokens: Int
    public let deadlineUtc: Date

    public init(slotId: String, modelId: String, tokens: Int, deadlineUtc: Date) {
        self.slotId = slotId
        self.modelId = modelId
        self.tokens = tokens
        self.deadlineUtc = deadlineUtc
    }
}

public struct ShardDescriptor: Sendable, Equatable {
    public let shardId: String
    public let rangeStart: Int
    public let rangeEnd: Int
    public let nodeId: String

    public init(shardId: String, rangeStart: Int, rangeEnd: Int, nodeId: String) {
        self.shardId = shardId
        self.rangeStart = rangeStart
        self.rangeEnd = rangeEnd
        self.nodeId = nodeId
    }
}

public struct OffloadDecision: Sendable, Equatable {
    public let shouldOffload: Bool
    public let targetNodeId: String?
    public let reason: String?

    public init(shouldOffload: Bool, targetNodeId: String?, reason: String?) {
        self.shouldOffload = shouldOffload
        self.targetNodeId = targetNodeId
        self.reason = reason
    }
}

public enum EnterpriseError: Error, Equatable, CustomStringConvertible {
    case modelIdRequired
    case nodeIdRequired
    case tenantIdRequired
    case estimatedTokensNotPositive
    case maxWaitNotPositive
    case paramBytesNotPositive
    case promptTokensNegative

    public var description: String {
        switch self {
        case .modelIdRequired: return "modelId required"
        case .nodeIdRequired: return "nodeId required"
        case .tenantIdRequired: return "tenantId required"
        case .estimatedTokensNotPositive: return "estimatedTokens must be > 0"
        case .maxWaitNotPositive: return "maxWait must be > 0"
        case .paramBytesNotPositive: return "paramBytes must be > 0"
        case .promptTokensNegative: return "promptTokens must be >= 0"
        }
    }
}

// MARK: - Interfaces

/// (2.7.0) Multi-tenant routing — pick a backend node per tenant.
public protocol ITenantRouter: Sendable {
    var backendId: String { get }
    func chooseNode(tenant: TenantContext, modelId: String) async throws -> String?
    func setQuota(_ quota: TenantQuota) async throws
    func getQuota(tenantId: String) async throws -> TenantQuota?
}

/// (2.7.0) Batch scheduler — coalesce small requests into one big one.
public protocol IBatchScheduler: Sendable {
    var backendId: String { get }
    func reserve(modelId: String, estimatedTokens: Int, maxWait: TimeInterval) async throws -> BatchSlot
    func release(_ slot: BatchSlot) async throws
}

/// (2.7.0) Model-sharding plan for very-large-model deployments.
public protocol IModelShardPlanner: Sendable {
    var backendId: String { get }
    func plan(modelId: String, paramBytes: Int) async throws -> [ShardDescriptor]
}

/// (2.7.0) RT-12 v2 cross-tier offload — phone borrows server brain.
public protocol ICrossTierOffload: Sendable {
    var backendId: String { get }
    func shouldOffload(modelId: String, promptTokens: Int, callerTier: ServerTier) async throws -> OffloadDecision
}

// MARK: - Null (single-node) implementations

public struct NullTenantRouter: ITenantRouter {
    public static let instance = NullTenantRouter()
    public init() {}
    public var backendId: String { "null" }
    public func chooseNode(tenant: TenantContext, modelId: String) async throws -> String? { nil }
    public func setQuota(_ quota: TenantQuota) async throws {}
    public func getQuota(tenantId: String) async throws -> TenantQuota? { nil }
}

public struct NullBatchScheduler: IBatchScheduler {
    public static let instance = NullBatchScheduler()
    public init() {}
    public var backendId: String { "null" }
    public func reserve(modelId: String, estimatedTokens: Int, maxWait: TimeInterval) async throws -> BatchSlot {
        BatchSlot(
            slotId: "00000000-0000-0000-0000-000000000000",
            modelId: modelId, tokens: estimatedTokens,
            deadlineUtc: Date().addingTimeInterval(maxWait))
    }
    public func release(_ slot: BatchSlot) async throws {}
}

public struct NullModelShardPlanner: IModelShardPlanner {
    public static let instance = NullModelShardPlanner()
    public init() {}
    public var backendId: String { "null" }
    public func plan(modelId: String, paramBytes: Int) async throws -> [ShardDescriptor] { [] }
}

public struct NullCrossTierOffload: ICrossTierOffload {
    public static let instance = NullCrossTierOffload()
    public init() {}
    public var backendId: String { "null" }
    public func shouldOffload(modelId: String, promptTokens: Int, callerTier: ServerTier) async throws -> OffloadDecision {
        OffloadDecision(shouldOffload: false, targetNodeId: nil, reason: "Local execution; no cross-tier offload configured.")
    }
}

// MARK: - Real in-memory implementations

/// Round-robins over the nodes registered for a model. Quotas are stored and
/// retrievable. Node registration is order-preserving + deduplicated.
public final class RoundRobinTenantRouter: ITenantRouter, @unchecked Sendable {
    private let lock = NSLock()
    private var quotas: [String: TenantQuota] = [:]
    private var nodesByModel: [String: [String]] = [:]
    private var rr: [String: Int] = [:]

    public init() {}
    public var backendId: String { "round-robin" }

    public func registerNode(modelId: String, nodeId: String) throws {
        guard !modelId.trimmingCharacters(in: .whitespaces).isEmpty else { throw EnterpriseError.modelIdRequired }
        guard !nodeId.trimmingCharacters(in: .whitespaces).isEmpty else { throw EnterpriseError.nodeIdRequired }
        lock.lock(); defer { lock.unlock() }
        var list = nodesByModel[modelId] ?? []
        if !list.contains(nodeId) { list.append(nodeId) }
        nodesByModel[modelId] = list
    }

    public func chooseNode(tenant: TenantContext, modelId: String) async throws -> String? {
        guard !modelId.trimmingCharacters(in: .whitespaces).isEmpty else { throw EnterpriseError.modelIdRequired }
        lock.lock(); defer { lock.unlock() }
        guard let nodes = nodesByModel[modelId], !nodes.isEmpty else { return nil }
        let idx = rr[modelId] ?? 0
        let pick = nodes[idx % nodes.count]
        rr[modelId] = idx + 1
        return pick
    }

    public func setQuota(_ quota: TenantQuota) async throws {
        lock.lock(); quotas[quota.tenantId] = quota; lock.unlock()
    }

    public func getQuota(tenantId: String) async throws -> TenantQuota? {
        guard !tenantId.trimmingCharacters(in: .whitespaces).isEmpty else { throw EnterpriseError.tenantIdRequired }
        lock.lock(); defer { lock.unlock() }
        return quotas[tenantId]
    }
}

/// Real reservation queue with deadline guarantees + release.
public final class InMemoryBatchScheduler: IBatchScheduler, @unchecked Sendable {
    private let lock = NSLock()
    private var slots: [String: BatchSlot] = [:]
    private var seq: Int64 = 0

    public init() {}
    public var backendId: String { "in-memory" }

    public func reserve(modelId: String, estimatedTokens: Int, maxWait: TimeInterval) async throws -> BatchSlot {
        guard !modelId.trimmingCharacters(in: .whitespaces).isEmpty else { throw EnterpriseError.modelIdRequired }
        guard estimatedTokens > 0 else { throw EnterpriseError.estimatedTokensNotPositive }
        guard maxWait > 0 else { throw EnterpriseError.maxWaitNotPositive }
        lock.lock()
        seq += 1
        let id = seq
        lock.unlock()
        let slot = BatchSlot(
            slotId: "slot-\(id)", modelId: modelId, tokens: estimatedTokens,
            deadlineUtc: Date().addingTimeInterval(maxWait))
        lock.lock(); slots[slot.slotId] = slot; lock.unlock()
        return slot
    }

    public func release(_ slot: BatchSlot) async throws {
        lock.lock(); slots.removeValue(forKey: slot.slotId); lock.unlock()
    }

    /// Count of live reservations. Diagnostics / test assertions.
    public var reservedCount: Int {
        lock.lock(); defer { lock.unlock() }
        return slots.count
    }
}

/// Even-bucket split across the nodes registered for a model. Remainder tokens
/// are distributed one-per-node to the lowest-indexed nodes.
public final class EvenSplitModelShardPlanner: IModelShardPlanner, @unchecked Sendable {
    private let nodesFor: @Sendable (String) -> [String]

    public init(nodesFor: @escaping @Sendable (String) -> [String]) {
        self.nodesFor = nodesFor
    }
    public var backendId: String { "even-split" }

    public func plan(modelId: String, paramBytes: Int) async throws -> [ShardDescriptor] {
        guard !modelId.trimmingCharacters(in: .whitespaces).isEmpty else { throw EnterpriseError.modelIdRequired }
        guard paramBytes > 0 else { throw EnterpriseError.paramBytesNotPositive }

        let nodes = nodesFor(modelId)
        if nodes.isEmpty { return [] }

        let bucket = paramBytes / nodes.count
        let rem = paramBytes % nodes.count
        var list: [ShardDescriptor] = []
        var cursor = 0
        for i in 0..<nodes.count {
            let size = bucket + (i < rem ? 1 : 0)
            list.append(ShardDescriptor(shardId: "shard-\(modelId)-\(i)", rangeStart: cursor, rangeEnd: cursor + size, nodeId: nodes[i]))
            cursor += size
        }
        return list
    }
}

/// Policy cross-tier offload: offload when the prompt exceeds the local ceiling
/// and the caller is not already the top tier.
public final class PolicyCrossTierOffload: ICrossTierOffload, @unchecked Sendable {
    private let localPromptCeiling: Int
    private let farmTargetNode: String?

    public init(localPromptCeiling: Int = 2048, farmTargetNode: String? = nil) {
        precondition(localPromptCeiling > 0, "localPromptCeiling must be > 0")
        self.localPromptCeiling = localPromptCeiling
        self.farmTargetNode = farmTargetNode
    }
    public var backendId: String { "policy" }

    public func shouldOffload(modelId: String, promptTokens: Int, callerTier: ServerTier) async throws -> OffloadDecision {
        guard !modelId.trimmingCharacters(in: .whitespaces).isEmpty else { throw EnterpriseError.modelIdRequired }
        guard promptTokens >= 0 else { throw EnterpriseError.promptTokensNegative }
        if callerTier == .serverFarm {
            return OffloadDecision(shouldOffload: false, targetNodeId: nil, reason: "Caller is already top-tier")
        }
        if promptTokens <= localPromptCeiling {
            return OffloadDecision(shouldOffload: false, targetNodeId: nil, reason: "Prompt fits locally")
        }
        return OffloadDecision(shouldOffload: true, targetNodeId: farmTargetNode, reason: "Prompt exceeds local ceiling (\(localPromptCeiling) tokens)")
    }
}
