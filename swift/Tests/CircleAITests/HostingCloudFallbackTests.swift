// HostingCloudFallbackTests.swift
//
// Verifies CloudFallbackChain fall-through (skips unconfigured / faulting
// generators) and BackupBrainOrchestrator between-turn failover with
// degraded → cool-down (half-open) recovery.

import XCTest
@testable import CircleAI

final class HostingCloudFallbackTests: XCTestCase {

    private let msgs = [ChatMessage(role: "user", content: "q")]

    // ── CloudFallbackChain ──────────────────────────────────────────────────

    func testChainUsesFirstConfiguredGenerator() async throws {
        let a = LocalDeterministicChatGenerator(engineLabel: "A", reply: "from-A")
        let b = LocalDeterministicChatGenerator(engineLabel: "B", reply: "from-B")
        let chain = CloudFallbackChain([a, b])
        let out = try await chain.generate(messages: msgs, options: nil)
        XCTAssertEqual(out, "from-A")
    }

    func testChainSkipsUnconfiguredGenerator() async throws {
        let a = LocalDeterministicChatGenerator(engineLabel: "A", isConfigured: false)
        let b = LocalDeterministicChatGenerator(engineLabel: "B", reply: "from-B")
        let chain = CloudFallbackChain([a, b])
        let out = try await chain.generate(messages: msgs, options: nil)
        XCTAssertEqual(out, "from-B", "unconfigured generator is skipped")
    }

    func testChainSkipsThrowingGenerator() async throws {
        let a = LocalDeterministicChatGenerator(engineLabel: "A", throwsOnCall: true)
        let b = LocalDeterministicChatGenerator(engineLabel: "B", reply: "from-B")
        let chain = CloudFallbackChain([a, b])
        let out = try await chain.generate(messages: msgs, options: nil)
        XCTAssertEqual(out, "from-B")
    }

    func testChainNoneReadyReturnsSentinel() async throws {
        let a = LocalDeterministicChatGenerator(engineLabel: "A", isConfigured: false)
        let chain = CloudFallbackChain([a])
        let out = try await chain.generate(messages: msgs, options: nil)
        XCTAssertTrue(out.contains("CloudFallbackChain"))
    }

    func testChainStreamSkipsFailSoftFrame() async throws {
        let a = LocalDeterministicChatGenerator(engineLabel: "A", isConfigured: false) // yields "[A: not configured]"
        let b = LocalDeterministicChatGenerator(engineLabel: "B", reply: "streamed-B")
        let chain = CloudFallbackChain([a, b])
        var chunks: [String] = []
        for await c in chain.stream(messages: msgs, options: nil) { chunks.append(c) }
        XCTAssertEqual(chunks, ["streamed-B"])
    }

    func testIsFailSoftFrameDetection() {
        XCTAssertTrue(CloudFallbackChain.isFailSoftFrame("[OpenAI not configured]"))
        XCTAssertTrue(CloudFallbackChain.isFailSoftFrame("[CloudFallbackChain: ...]"))
        XCTAssertFalse(CloudFallbackChain.isFailSoftFrame("normal token"))
    }

    // ── BackupBrainOrchestrator ─────────────────────────────────────────────

    func testOrchestratorUsesPrimaryWhenHealthy() async throws {
        let a = LocalDeterministicChatGenerator(engineLabel: "A", reply: "A")
        let b = LocalDeterministicChatGenerator(engineLabel: "B", reply: "B")
        let orch = BackupBrainOrchestrator([a, b])
        let out = try await orch.generate(messages: msgs, options: nil)
        XCTAssertEqual(out, "A")
    }

    func testOrchestratorFailsOverToBackup() async throws {
        let a = LocalDeterministicChatGenerator(engineLabel: "A", throwsOnCall: true)
        let b = LocalDeterministicChatGenerator(engineLabel: "B", reply: "B")
        let orch = BackupBrainOrchestrator([a, b], policy: BackupBrainPolicy(degradedAfterFailures: 1))
        let out = try await orch.generate(messages: msgs, options: nil)
        XCTAssertEqual(out, "B")
    }

    func testOrchestratorAllFailReturnsSentinel() async throws {
        let a = LocalDeterministicChatGenerator(engineLabel: "A", throwsOnCall: true)
        let b = LocalDeterministicChatGenerator(engineLabel: "B", throwsOnCall: true)
        let orch = BackupBrainOrchestrator([a, b], policy: BackupBrainPolicy(degradedAfterFailures: 1, maxRetriesPerTurn: 5))
        let out = try await orch.generate(messages: msgs, options: nil)
        XCTAssertEqual(out, "[All brains failed.]")
    }

    func testOrchestratorMarksDegradedAfterThreshold() async throws {
        let a = LocalDeterministicChatGenerator(engineLabel: "A", throwsOnCall: true)
        let b = LocalDeterministicChatGenerator(engineLabel: "B", reply: "B")
        let clockBox = ClockBox(start: Date(timeIntervalSince1970: 1_000_000))
        let orch = BackupBrainOrchestrator([a, b],
                                           policy: BackupBrainPolicy(degradedAfterFailures: 1, coolDownDuration: 30),
                                           clock: { clockBox.now() })
        _ = try await orch.generate(messages: msgs, options: nil) // A fails → degraded, B serves
        let statuses = orch.statuses
        XCTAssertEqual(statuses[0].label, "A")
        XCTAssertEqual(statuses[0].health, .degraded)
        XCTAssertEqual(statuses[1].health, .healthy)

        // Advance the clock past cool-down → A becomes CoolingDown (half-open).
        clockBox.advance(by: 31)
        let after = orch.statuses
        XCTAssertEqual(after[0].health, .coolingDown)
    }

    // ── LocalDeterministicChatGenerator fake ────────────────────────────────

    func testLocalFakeEchoesLastUser() async throws {
        let g = LocalDeterministicChatGenerator(engineLabel: "L")
        let out = try await g.generate(messages: [ChatMessage(role: "user", content: "ping")], options: nil)
        XCTAssertEqual(out, "L: ping")
        XCTAssertTrue(g.isConfigured)
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    final class ClockBox: @unchecked Sendable {
        private let lock = NSLock()
        private var current: Date
        init(start: Date) { current = start }
        func now() -> Date { lock.lock(); defer { lock.unlock() }; return current }
        func advance(by seconds: TimeInterval) { lock.lock(); current = current.addingTimeInterval(seconds); lock.unlock() }
    }
}
