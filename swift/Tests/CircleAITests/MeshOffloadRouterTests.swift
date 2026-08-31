import XCTest
@testable import CircleAI

/// Peer selection and the routing decision.
final class MeshOffloadRouterTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    private func ad(_ peer: String, tier: DeviceTier = .phone, kv: Int = 1000,
                    latency: Int? = nil, model: String = "m", age: TimeInterval = 0)
        -> MeshCapabilityAdvertisement {
        MeshCapabilityAdvertisement(peerId: peer, modelId: model, freeKvTokens: kv, tier: tier,
                                    contextWindowTokens: 4096,
                                    // WALL CLOCK, not the frozen test clock: the
                                    // registry ages entries against the real one,
                                    // so a fixed 2026 stamp reads as months stale.
                                    advertisedAtUtc: Date().addingTimeInterval(-age),
                                    latencyHintMs: latency)
    }

    private func turn(prompt: String = "hello", out: Int = 100) -> OffloadTurn {
        OffloadTurn.create(modelId: "m", prompt: prompt, maxOutputTokens: out,
                           correlationId: "corr-1", now: now)!
    }

    // MARK: - Peer selection

    func testTheStrongestTierWins() {
        let best = MeshOffloadOptions.defaultSelectPeer([
            ad("weak", tier: .phone), ad("strong", tier: .desktop), ad("mid", tier: .tablet),
        ])
        XCTAssertEqual(best?.peerId, "strong")
    }

    func testWithinATierTheFastestWins() {
        let best = MeshOffloadOptions.defaultSelectPeer([
            ad("slow", tier: .tablet, latency: 200), ad("fast", tier: .tablet, latency: 20),
        ])
        XCTAssertEqual(best?.peerId, "fast")
    }

    // Unknown is NOT fast. A peer that reports no hint must not beat one that
    // measured itself.
    func testAPeerWithNoLatencyHintSortsLast() {
        let best = MeshOffloadOptions.defaultSelectPeer([
            ad("unknown", tier: .tablet, latency: nil), ad("measured", tier: .tablet, latency: 500),
        ])
        XCTAssertEqual(best?.peerId, "measured")
    }

    func testSpareBudgetBreaksAnOtherwiseExactTie() {
        let best = MeshOffloadOptions.defaultSelectPeer([
            ad("small", tier: .tablet, kv: 100, latency: 50),
            ad("roomy", tier: .tablet, kv: 9000, latency: 50),
        ])
        XCTAssertEqual(best?.peerId, "roomy")
    }

    func testNoCandidatesSelectsNobody() {
        XCTAssertNil(MeshOffloadOptions.defaultSelectPeer([]))
    }

    // MARK: - Budget estimation

    // Four characters to the token for the prompt; the output budget is exact
    // because the caller asked for it.
    func testTheDefaultEstimateCountsPromptAndOutput() {
        let o = MeshOffloadOptions()
        XCTAssertEqual(o.estimateKvTokens(turn(prompt: String(repeating: "x", count: 400), out: 50)), 150)
        XCTAssertEqual(o.estimateKvTokens(turn(prompt: "", out: 0)), 0)
    }

    // MARK: - Routing

    func testAWorkingPeerServesTheTurn() async throws {
        let registry = InMemoryMeshCapabilityRegistry()
        try await registry.upsert(ad("peer-1"))

        let router = MeshOffloadRouter(
            registry: registry,
            client: StubClient(answers: ["peer-1": .ok("from peer")]),
            localFallback: StubFallback(result: .ok("from local")),
            options: MeshOffloadOptions(staleAfter: 3600),
            clock: { self.now })

        let r = try await router.route(turn())
        XCTAssertTrue(r.success)
        XCTAssertEqual(r.outputText, "from peer")
        XCTAssertEqual(r.servedBy, .remotePeer)
    }

    // Nobody in range is the normal case, not an error.
    func testWithNoPeersItAnswersLocally() async throws {
        let router = MeshOffloadRouter(
            registry: InMemoryMeshCapabilityRegistry(),
            client: StubClient(answers: [:]),
            localFallback: StubFallback(result: .ok("from local")),
            options: MeshOffloadOptions(staleAfter: 3600),
            clock: { self.now })

        let r = try await router.route(turn())
        XCTAssertTrue(r.success)
        XCTAssertEqual(r.outputText, "from local")
        XCTAssertEqual(r.servedBy, .localFallback)
    }

    func testAFailingPeerIsRetriedOnTheNextOne() async throws {
        let registry = InMemoryMeshCapabilityRegistry()
        try await registry.upsert(ad("bad", tier: .desktop))
        try await registry.upsert(ad("good", tier: .tablet))

        let router = MeshOffloadRouter(
            registry: registry,
            client: StubClient(answers: ["bad": .failed("busy"), "good": .ok("second try")]),
            localFallback: StubFallback(result: .ok("from local")),
            options: MeshOffloadOptions(staleAfter: 3600, maxPeerAttempts: 2),
            clock: { self.now })

        let r = try await router.route(turn())
        XCTAssertEqual(r.outputText, "second try")
    }

    // The attempt budget is a budget: it must not try a third peer.
    func testItStopsAfterTheAttemptBudgetAndFallsBack() async throws {
        let registry = InMemoryMeshCapabilityRegistry()
        for (i, t) in [DeviceTier.workstation, .desktop, .tablet].enumerated() {
            try await registry.upsert(ad("p\(i)", tier: t))
        }
        let client = StubClient(answers: [:])   // every peer fails

        let router = MeshOffloadRouter(
            registry: registry, client: client,
            localFallback: StubFallback(result: .ok("from local")),
            options: MeshOffloadOptions(staleAfter: 3600, maxPeerAttempts: 2),
            clock: { self.now })

        let r = try await router.route(turn())
        XCTAssertEqual(r.outputText, "from local")
        let attempts = await client.attempts
        XCTAssertEqual(attempts, 2)
    }

    // MARK: - Helpers

    private actor StubClient: IMeshOffloadClient {
        enum Answer { case ok(String), failed(String) }
        private let answers: [String: Answer]
        private(set) var attempts = 0

        init(answers: [String: Answer]) { self.answers = answers }

        nonisolated var isReady: Bool { true }

        func request(peerId: String, turn: OffloadTurn, timeout: TimeInterval) async throws -> OffloadResult {
            attempts += 1
            switch answers[peerId] {
            case .ok(let text):
                return OffloadResult(success: true, outputText: text, servedBy: .remotePeer,
                                     servingPeerId: peerId, outputTokenCount: 1,
                                     elapsedMilliseconds: 1, failureReason: nil)
            case .failed(let why):
                return OffloadResult.fail(why, servedBy: .none)
            case nil:
                return OffloadResult.fail("no answer", servedBy: .none)
            }
        }
    }

    private struct StubFallback: ILocalInferenceFallback {
        enum R { case ok(String), boom }
        let result: R
        func complete(_ turn: OffloadTurn) async throws -> OffloadResult {
            switch result {
            case .ok(let text):
                // Deliberately reports .none so the router has to label it.
                return OffloadResult(success: true, outputText: text, servedBy: .none,
                                     servingPeerId: nil, outputTokenCount: 1,
                                     elapsedMilliseconds: 1, failureReason: nil)
            case .boom:
                throw TestFailure.boom
            }
        }
    }

    private enum TestFailure: Error { case boom }
}
