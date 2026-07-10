// InferenceServerEnterpriseTests.swift

import XCTest
@testable import CircleAI

final class InferenceServerEnterpriseTests: XCTestCase {

    private let tenant = TenantContext(tenantId: "t1")

    // MARK: - RoundRobinTenantRouter

    func testRoundRobinCyclesNodes() async throws {
        let r = RoundRobinTenantRouter()
        try r.registerNode(modelId: "m", nodeId: "n1")
        try r.registerNode(modelId: "m", nodeId: "n2")
        try r.registerNode(modelId: "m", nodeId: "n1") // dedup
        var picks: [String] = []
        for _ in 0..<4 { picks.append(try await r.chooseNode(tenant: tenant, modelId: "m")!) }
        XCTAssertEqual(picks, ["n1", "n2", "n1", "n2"])
    }

    func testRoundRobinUnknownModelReturnsNil() async throws {
        let r = RoundRobinTenantRouter()
        let node = try await r.chooseNode(tenant: tenant, modelId: "absent")
        XCTAssertNil(node)
    }

    func testRoundRobinQuotaStoreAndFetch() async throws {
        let r = RoundRobinTenantRouter()
        let quota = TenantQuota(tenantId: "t1", maxConcurrentRequests: 4, maxModelsLoaded: 2, maxBytesInFlight: 1024, dailyTokenBudget: 100_000)
        try await r.setQuota(quota)
        let got = try await r.getQuota(tenantId: "t1")
        XCTAssertEqual(got, quota)
        let unknownQuota = try await r.getQuota(tenantId: "unknown")
        XCTAssertNil(unknownQuota)
    }

    func testRoundRobinRegisterValidatesArgs() {
        let r = RoundRobinTenantRouter()
        XCTAssertThrowsError(try r.registerNode(modelId: "", nodeId: "n"))
        XCTAssertThrowsError(try r.registerNode(modelId: "m", nodeId: ""))
    }

    func testNullTenantRouterAlwaysNil() async throws {
        let r = NullTenantRouter.instance
        XCTAssertEqual(r.backendId, "null")
        let node = try await r.chooseNode(tenant: tenant, modelId: "m")
        XCTAssertNil(node)
        let quota = try await r.getQuota(tenantId: "t1")
        XCTAssertNil(quota)
    }

    // MARK: - InMemoryBatchScheduler

    func testBatchReserveIssuesUniqueSlotsWithDeadline() async throws {
        let s = InMemoryBatchScheduler()
        let before = Date()
        let slot1 = try await s.reserve(modelId: "m", estimatedTokens: 100, maxWait: 2.0)
        let slot2 = try await s.reserve(modelId: "m", estimatedTokens: 50, maxWait: 2.0)
        XCTAssertNotEqual(slot1.slotId, slot2.slotId)
        XCTAssertTrue(slot1.slotId.hasPrefix("slot-"))
        XCTAssertEqual(slot1.tokens, 100)
        XCTAssertGreaterThanOrEqual(slot1.deadlineUtc, before.addingTimeInterval(2.0))
        XCTAssertEqual(s.reservedCount, 2)
    }

    func testBatchReleaseRemovesSlot() async throws {
        let s = InMemoryBatchScheduler()
        let slot = try await s.reserve(modelId: "m", estimatedTokens: 10, maxWait: 1.0)
        try await s.release(slot)
        XCTAssertEqual(s.reservedCount, 0)
    }

    func testBatchReserveValidatesArgs() async {
        let s = InMemoryBatchScheduler()
        do { _ = try await s.reserve(modelId: "", estimatedTokens: 1, maxWait: 1); XCTFail() } catch { XCTAssertEqual(error as? EnterpriseError, .modelIdRequired) }
        do { _ = try await s.reserve(modelId: "m", estimatedTokens: 0, maxWait: 1); XCTFail() } catch { XCTAssertEqual(error as? EnterpriseError, .estimatedTokensNotPositive) }
        do { _ = try await s.reserve(modelId: "m", estimatedTokens: 1, maxWait: 0); XCTFail() } catch { XCTAssertEqual(error as? EnterpriseError, .maxWaitNotPositive) }
    }

    func testNullBatchSchedulerEmptyGuid() async throws {
        let s = NullBatchScheduler.instance
        let slot = try await s.reserve(modelId: "m", estimatedTokens: 5, maxWait: 1)
        XCTAssertEqual(slot.slotId, "00000000-0000-0000-0000-000000000000")
    }

    // MARK: - EvenSplitModelShardPlanner

    func testEvenSplitDistributesRemainderToLowIndexNodes() async throws {
        // 10 bytes over 3 nodes → [4, 3, 3], contiguous ranges.
        let planner = EvenSplitModelShardPlanner(nodesFor: { _ in ["n0", "n1", "n2"] })
        let shards = try await planner.plan(modelId: "m", paramBytes: 10)
        XCTAssertEqual(shards.count, 3)
        XCTAssertEqual(shards[0].rangeStart, 0); XCTAssertEqual(shards[0].rangeEnd, 4)
        XCTAssertEqual(shards[1].rangeStart, 4); XCTAssertEqual(shards[1].rangeEnd, 7)
        XCTAssertEqual(shards[2].rangeStart, 7); XCTAssertEqual(shards[2].rangeEnd, 10)
        XCTAssertEqual(shards.map { $0.nodeId }, ["n0", "n1", "n2"])
        XCTAssertEqual(shards[0].shardId, "shard-m-0")
    }

    func testEvenSplitNoNodesReturnsEmpty() async throws {
        let planner = EvenSplitModelShardPlanner(nodesFor: { _ in [] })
        let shards = try await planner.plan(modelId: "m", paramBytes: 100)
        XCTAssertTrue(shards.isEmpty)
    }

    func testEvenSplitValidatesArgs() async {
        let planner = EvenSplitModelShardPlanner(nodesFor: { _ in ["n0"] })
        do { _ = try await planner.plan(modelId: "", paramBytes: 1); XCTFail() } catch { XCTAssertEqual(error as? EnterpriseError, .modelIdRequired) }
        do { _ = try await planner.plan(modelId: "m", paramBytes: 0); XCTFail() } catch { XCTAssertEqual(error as? EnterpriseError, .paramBytesNotPositive) }
    }

    // MARK: - PolicyCrossTierOffload

    func testOffloadWhenPromptExceedsCeiling() async throws {
        let policy = PolicyCrossTierOffload(localPromptCeiling: 2048, farmTargetNode: "farm-1")
        let decision = try await policy.shouldOffload(modelId: "m", promptTokens: 4096, callerTier: .singleNode)
        XCTAssertTrue(decision.shouldOffload)
        XCTAssertEqual(decision.targetNodeId, "farm-1")
    }

    func testNoOffloadWhenPromptFits() async throws {
        let policy = PolicyCrossTierOffload(localPromptCeiling: 2048)
        let decision = try await policy.shouldOffload(modelId: "m", promptTokens: 100, callerTier: .server)
        XCTAssertFalse(decision.shouldOffload)
        XCTAssertEqual(decision.reason, "Prompt fits locally")
    }

    func testNoOffloadWhenCallerIsTopTier() async throws {
        let policy = PolicyCrossTierOffload(localPromptCeiling: 10)
        let decision = try await policy.shouldOffload(modelId: "m", promptTokens: 99_999, callerTier: .serverFarm)
        XCTAssertFalse(decision.shouldOffload)
        XCTAssertEqual(decision.reason, "Caller is already top-tier")
    }

    func testOffloadValidatesArgs() async {
        let policy = PolicyCrossTierOffload()
        do { _ = try await policy.shouldOffload(modelId: "", promptTokens: 1, callerTier: .server); XCTFail() } catch { XCTAssertEqual(error as? EnterpriseError, .modelIdRequired) }
        do { _ = try await policy.shouldOffload(modelId: "m", promptTokens: -1, callerTier: .server); XCTFail() } catch { XCTAssertEqual(error as? EnterpriseError, .promptTokensNegative) }
    }

    func testNullCrossTierOffloadNeverOffloads() async throws {
        let n = NullCrossTierOffload.instance
        let d = try await n.shouldOffload(modelId: "m", promptTokens: 100_000, callerTier: .singleNode)
        XCTAssertFalse(d.shouldOffload)
    }

    func testBackendIdsAreStable() {
        XCTAssertEqual(RoundRobinTenantRouter().backendId, "round-robin")
        XCTAssertEqual(InMemoryBatchScheduler().backendId, "in-memory")
        XCTAssertEqual(EvenSplitModelShardPlanner(nodesFor: { _ in [] }).backendId, "even-split")
        XCTAssertEqual(PolicyCrossTierOffload().backendId, "policy")
    }
}
