// MeshTransportClientTests.swift

import XCTest
@testable import CircleAI

/// A transport that records what was sent and lets a test feed what arrives.
private final class LoopTransport: INetworkTransport, @unchecked Sendable {
    let kind: TransportKind = .localStore
    var isAvailable = true

    private let lock = NSLock()
    private var sentPayloads: [NetworkPayload] = []
    private var continuation: AsyncStream<NetworkPayload>.Continuation?

    var sent: [NetworkPayload] { lock.lock(); defer { lock.unlock() }; return sentPayloads }

    var sendError: Error?

    func start() async throws { isAvailable = true }
    func stop() async throws { isAvailable = false }

    func send(_ payload: NetworkPayload) async throws {
        if let sendError { throw sendError }
        lock.lock(); sentPayloads.append(payload); lock.unlock()
    }

    func receive() -> AsyncStream<NetworkPayload> {
        AsyncStream { c in
            lock.lock(); continuation = c; lock.unlock()
        }
    }

    func deliver(_ payload: NetworkPayload) {
        lock.lock(); let c = continuation; lock.unlock()
        c?.yield(payload)
    }
}

private final class EchoFallback: ILocalInferenceFallback, @unchecked Sendable {
    var reply = "served locally"
    var failWith: Error?
    var delay: TimeInterval = 0

    func complete(_ turn: OffloadTurn) async throws -> OffloadResult {
        if delay > 0 { try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000)) }
        if let failWith { throw failWith }
        return OffloadResult(success: true, outputText: reply, servedBy: .localFallback,
                             servingPeerId: nil, outputTokenCount: 3,
                             elapsedMilliseconds: 1, failureReason: nil, reasoningText: nil)
    }
}

private final class RecordingRegistry: IMeshCapabilityRegistry, @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [MeshCapabilityAdvertisement] = []

    var upserted: [MeshCapabilityAdvertisement] {
        lock.lock(); defer { lock.unlock() }
        return stored
    }

    func upsert(_ ad: MeshCapabilityAdvertisement) async throws {
        lock.lock(); stored.append(ad); lock.unlock()
    }

    @discardableResult
    func remove(peerId: String) async throws -> Bool {
        lock.lock(); defer { lock.unlock() }
        let before = stored.count
        stored.removeAll { $0.peerId == peerId }
        return stored.count != before
    }

    func list(staleAfter: TimeInterval?) -> [MeshCapabilityAdvertisement] { upserted }

    func find(modelId: String, minFreeKvTokens: Int,
              staleAfter: TimeInterval?) -> [MeshCapabilityAdvertisement] {
        upserted.filter { $0.modelId == modelId && $0.freeKvTokens >= minFreeKvTokens }
    }
}

final class MeshTransportClientTests: XCTestCase {

    private let epoch = Date(timeIntervalSince1970: 1_700_000_000)

    private func options(_ nodeId: String = "me", serve: Bool = true,
                         maxServed: Int = 2) -> MeshOffloadOptions {
        MeshOffloadOptions(localNodeId: nodeId, staleAfter: 30, requestTimeout: 5,
                           serveInboundRequests: serve, maxConcurrentServed: maxServed,
                           startTransport: false, broadcastInterval: 15)
    }

    private func advert(peer: String = "them", model: String = "qwen") -> MeshCapabilityAdvertisement {
        MeshCapabilityAdvertisement(peerId: peer, modelId: model, freeKvTokens: 4096,
                                    tier: .desktop, contextWindowTokens: 8192,
                                    advertisedAtUtc: Date(timeIntervalSince1970: 0),
                                    latencyHintMs: 40)
    }

    // MARK: - Broadcasting

    func testTheAdvertIsStampedWithOurIdAndTheMomentItWasSent() async throws {
        // A timestamp taken when the advertisement was BUILT makes us look
        // stale before we have said anything.
        let t = LoopTransport()
        let b = AetherMeshCapabilityBroadcaster(transport: t, options: options(),
                                                now: { self.epoch })
        try await b.broadcast(advert(peer: "wrong-id"))

        XCTAssertEqual(t.sent.count, 1)
        let env = MeshOffloadWire.decodeAdvert(t.sent[0])
        XCTAssertEqual(env?.peerId, "me", "our id, not whatever the caller passed")
        XCTAssertEqual(env?.advertisedAtUtc, epoch)
        XCTAssertEqual(env?.modelId, "qwen")
    }

    func testTheAdvertCarriesTheFreshnessWindowAsItsTtl() async throws {
        // A packet that outlives the window is only ever noise: a peer that
        // stops hearing us expires us anyway.
        let t = LoopTransport()
        try await AetherMeshCapabilityBroadcaster(transport: t, options: options())
            .broadcast(advert())
        XCTAssertEqual(t.sent[0].contentType, MeshOffloadWire.advertContentType)
    }

    func testAnUnavailableTransportIsSkippedNotThrown() async throws {
        let t = LoopTransport()
        t.isAvailable = false
        try await AetherMeshCapabilityBroadcaster(transport: t, options: options())
            .broadcast(advert())
        XCTAssertTrue(t.sent.isEmpty)
    }

    func testAFailedBroadcastDoesNotFailTheCaller() async throws {
        // The next beacon tick is seconds away and nothing downstream is
        // waiting on this.
        struct Radio: Error {}
        let t = LoopTransport()
        t.sendError = Radio()
        try await AetherMeshCapabilityBroadcaster(transport: t, options: options())
            .broadcast(advert())
    }

    // MARK: - The beacon

    func testTheBeaconBroadcastsWhatTheProviderReturnsNow() async {
        // Free KV budget changes minute to minute; an advertisement captured at
        // startup advertises a phone that no longer exists.
        let t = LoopTransport()
        let b = AetherMeshCapabilityBroadcaster(transport: t, options: options())
        let free = FreeBox(4096)

        let beacon = MeshAdvertisementBeacon(broadcaster: b, interval: 60, advertisement: {
            MeshCapabilityAdvertisement(peerId: "x", modelId: "qwen",
                                        freeKvTokens: free.value, tier: .phone,
                                        contextWindowTokens: 4096,
                                        advertisedAtUtc: Date(timeIntervalSince1970: 0))
        })

        await beacon.tick()
        free.value = 128
        await beacon.tick()

        let sent = t.sent.compactMap { MeshOffloadWire.decodeAdvert($0)?.freeKvTokens }
        XCTAssertEqual(sent, [4096, 128])
    }

    func testANodeThatAdvertisesNothingBorrowsOnlyAndIsNotAFailure() async {
        // A cheap phone with no model to share is a legitimate configuration.
        let t = LoopTransport()
        let beacon = MeshAdvertisementBeacon(
            broadcaster: AetherMeshCapabilityBroadcaster(transport: t, options: options()),
            interval: 60, advertisement: { nil })
        await beacon.tick()
        XCTAssertTrue(t.sent.isEmpty)
    }

    func testStartingAndStoppingTheBeaconIsIdempotent() {
        let beacon = MeshAdvertisementBeacon(
            broadcaster: NullMeshCapabilityBroadcaster.shared,
            interval: 3600, advertisement: { nil })
        XCTAssertFalse(beacon.isRunning)
        beacon.start(); beacon.start()
        XCTAssertTrue(beacon.isRunning)
        beacon.stop(); beacon.stop()
        XCTAssertFalse(beacon.isRunning)
    }

    // MARK: - Serving a peer

    private func requestPayload(from peer: String, correlation: String = "c1",
                                prompt: String = "hello") throws -> NetworkPayload {
        let turn = OffloadTurn(modelId: "qwen", prompt: prompt, maxOutputTokens: 64,
                               temperature: 0.7, topP: 0.9, stopSequences: [],
                               correlationId: correlation, createdAtUtc: epoch)
        let env = OffloadRequestEnvelope(turn: turn, replyToNodeId: peer)
        return try MeshOffloadWire.encodeRequest(sourceNodeId: peer,
                                                 destinationPeerId: "me", env, ttl: 5)
    }

    private func client(_ t: LoopTransport, _ r: RecordingRegistry, _ f: EchoFallback,
                        opts: MeshOffloadOptions? = nil) -> MeshOffloadClient {
        MeshOffloadClient(transport: t, registry: r, localFallback: f,
                          options: opts ?? options(), now: { self.epoch })
    }

    func testAnInboundRequestIsServedAndRepliedTo() async throws {
        let t = LoopTransport(), r = RecordingRegistry(), f = EchoFallback()
        let c = client(t, r, f)

        await c.serve(try requestPayload(from: "them"))

        XCTAssertEqual(t.sent.count, 1)
        let reply = MeshOffloadWire.decodeReply(t.sent[0])
        XCTAssertEqual(reply?.correlationId, "c1")
        XCTAssertTrue(reply?.success ?? false)
        XCTAssertEqual(reply?.outputText, "served locally")
    }

    func testANodeThatDoesNotServeStaysSilent() async throws {
        let t = LoopTransport(), r = RecordingRegistry(), f = EchoFallback()
        let c = client(t, r, f, opts: options(serve: false))
        await c.serve(try requestPayload(from: "them"))
        XCTAssertTrue(t.sent.isEmpty)
    }

    func testAtCapacityIsAnAnswerNotSilence() async throws {
        // A peer told it is busy tries somebody else; a peer told nothing waits
        // out its whole timeout.
        let t = LoopTransport(), r = RecordingRegistry()
        let f = EchoFallback()
        f.delay = 0.4
        let c = client(t, r, f, opts: options(maxServed: 1))

        let firstPayload = try requestPayload(from: "them", correlation: "a")
        async let first: Void = c.serve(firstPayload)
        try await Task.sleep(nanoseconds: 60_000_000)
        await c.serve(try requestPayload(from: "them", correlation: "b"))
        _ = try await first

        let replies = t.sent.compactMap { MeshOffloadWire.decodeReply($0) }
        let busy = replies.first { $0.correlationId == "b" }
        XCTAssertNotNil(busy)
        XCTAssertFalse(busy!.success)
        XCTAssertEqual(busy?.failureReason, "Serving peer is at capacity.")
    }

    func testAFallbackThatThrowsStillProducesAReply() async throws {
        // A peer left waiting because we crashed mid-inference burns its whole
        // timeout for nothing.
        struct Boom: Error {}
        let t = LoopTransport(), r = RecordingRegistry(), f = EchoFallback()
        f.failWith = Boom()

        await client(t, r, f).serve(try requestPayload(from: "them"))

        let reply = MeshOffloadWire.decodeReply(t.sent[0])
        XCTAssertFalse(reply?.success ?? true)
        XCTAssertTrue(reply?.failureReason?.contains("raised an error") ?? false)
    }

    func testARequestWithNoReturnAddressIsDropped() async throws {
        let t = LoopTransport(), r = RecordingRegistry(), f = EchoFallback()
        await client(t, r, f).serve(try requestPayload(from: "   "))
        XCTAssertTrue(t.sent.isEmpty)
    }

    func testAnUndecodableRequestIsDroppedNotACrash() async {
        let t = LoopTransport(), r = RecordingRegistry(), f = EchoFallback()
        let junk = NetworkPayload.create(
            data: Data("not json".utf8), destinationId: "me",
            contentType: MeshOffloadWire.requestContentType)
        await client(t, r, f).serve(junk)
        XCTAssertTrue(t.sent.isEmpty)
    }

    // MARK: - Advert ingest

    func testAPeerAdvertIsFoldedIntoTheRegistry() async throws {
        let t = LoopTransport(), r = RecordingRegistry(), f = EchoFallback()
        let env = MeshAdvertEnvelope(peerId: "them", modelId: "qwen", freeKvTokens: 2048,
                                     tier: DeviceTier.desktop.rawValue,
                                     contextWindowTokens: 8192, advertisedAtUtc: epoch,
                                     latencyHintMs: 30)
        let payload = try MeshOffloadWire.encodeAdvert(sourceNodeId: "them", env)

        await client(t, r, f).ingestAdvert(payload)

        XCTAssertEqual(r.upserted.count, 1)
        XCTAssertEqual(r.upserted[0].peerId, "them")
        XCTAssertEqual(r.upserted[0].tier, .desktop)
    }

    func testOurOwnAdvertEchoedBackIsIgnored() async throws {
        // Folding it in makes this device its own best peer, and it then tries
        // to offload to itself.
        let t = LoopTransport(), r = RecordingRegistry(), f = EchoFallback()
        let env = MeshAdvertEnvelope(peerId: "me", modelId: "qwen", freeKvTokens: 1,
                                     tier: DeviceTier.phone.rawValue,
                                     contextWindowTokens: 1, advertisedAtUtc: epoch,
                                     latencyHintMs: nil)
        await client(t, r, f).ingestAdvert(
            try MeshOffloadWire.encodeAdvert(sourceNodeId: "me", env))
        XCTAssertTrue(r.upserted.isEmpty)
    }

    func testAnAdvertWithNoPeerIdIsIgnored() async throws {
        let t = LoopTransport(), r = RecordingRegistry(), f = EchoFallback()
        let env = MeshAdvertEnvelope(peerId: "  ", modelId: "q", freeKvTokens: 1,
                                     tier: 0, contextWindowTokens: 1,
                                     advertisedAtUtc: epoch, latencyHintMs: nil)
        await client(t, r, f).ingestAdvert(
            try MeshOffloadWire.encodeAdvert(sourceNodeId: "x", env))
        XCTAssertTrue(r.upserted.isEmpty)
    }

    // MARK: - Dispatch

    func testTrafficThatIsNotOursIsIgnored() {
        let t = LoopTransport(), r = RecordingRegistry(), f = EchoFallback()
        let other = NetworkPayload.create(
            data: Data("{}".utf8), destinationId: "me", contentType: "application/other")
        client(t, r, f).dispatch(other)      // must not crash or reply
        XCTAssertTrue(t.sent.isEmpty)
    }

    // MARK: - Requesting from a peer

    func testAnUnavailableTransportFailsFastRatherThanWaiting() async throws {
        let t = LoopTransport(); t.isAvailable = false
        let c = client(t, RecordingRegistry(), EchoFallback())

        let turn = OffloadTurn.create(modelId: "qwen", prompt: "hi")!
        let result = try await c.request(peerId: "them", turn: turn, timeout: 5)

        XCTAssertFalse(result.success)
        XCTAssertEqual(result.servedBy, OffloadServedBy.none)
        XCTAssertTrue(result.failureReason?.contains("not available") ?? false)
    }

    func testABlankPeerIdIsRefused() async throws {
        let c = client(LoopTransport(), RecordingRegistry(), EchoFallback())
        let result = try await c.request(peerId: "  ",
                                         turn: OffloadTurn.create(modelId: "q", prompt: "p")!,
                                         timeout: 1)
        XCTAssertFalse(result.success)
    }

    func testAPeerThatNeverRepliesTimesOutWithItsNameInTheMessage() async throws {
        let t = LoopTransport()
        let c = client(t, RecordingRegistry(), EchoFallback())

        let result = try await c.request(peerId: "silent-phone",
                                         turn: OffloadTurn.create(modelId: "q", prompt: "p")!,
                                         timeout: 0.15)
        XCTAssertFalse(result.success)
        XCTAssertTrue(result.failureReason?.contains("silent-phone") ?? false)
        XCTAssertEqual(t.sent.count, 1, "the request did go out")
    }

    func testAReplyResolvesTheWaitingRequest() async throws {
        let t = LoopTransport()
        let c = client(t, RecordingRegistry(), EchoFallback())
        let turn = OffloadTurn.create(modelId: "qwen", prompt: "hi")!

        async let pending = c.request(peerId: "them", turn: turn, timeout: 5)

        // Wait for the request to actually be sent, then answer it.
        var tries = 0
        while t.sent.isEmpty && tries < 200 {
            try await Task.sleep(nanoseconds: 2_000_000); tries += 1
        }
        let reply = OffloadReplyEnvelope(correlationId: turn.correlationId, success: true,
                                         outputText: "from the peer", outputTokenCount: 4,
                                         failureReason: nil, reasoningText: nil,
                                         completedAtUtc: epoch)
        c.dispatch(try MeshOffloadWire.encodeReply(sourceNodeId: "them",
                                                   destinationNodeId: "me", reply))

        let result = try await pending
        XCTAssertTrue(result.success)
        XCTAssertEqual(result.outputText, "from the peer")
        XCTAssertEqual(result.servedBy, .remotePeer)
        XCTAssertEqual(result.servingPeerId, "them")
    }

    func testALateReplyForAnAbandonedRequestIsDroppedQuietly() throws {
        // The requester already gave up and moved on.
        let t = LoopTransport()
        let c = client(t, RecordingRegistry(), EchoFallback())
        let reply = OffloadReplyEnvelope(correlationId: "nobody-waiting", success: true,
                                         outputText: "too late", outputTokenCount: 1,
                                         failureReason: nil, reasoningText: nil,
                                         completedAtUtc: epoch)
        c.dispatch(try MeshOffloadWire.encodeReply(sourceNodeId: "them",
                                                   destinationNodeId: "me", reply))
    }

    func testAFailedRemoteReplyIsReportedAsAFailureNotAnEmptyAnswer() async throws {
        let t = LoopTransport()
        let c = client(t, RecordingRegistry(), EchoFallback())
        let turn = OffloadTurn.create(modelId: "qwen", prompt: "hi")!

        async let pending = c.request(peerId: "them", turn: turn, timeout: 5)
        var tries = 0
        while t.sent.isEmpty && tries < 200 {
            try await Task.sleep(nanoseconds: 2_000_000); tries += 1
        }
        let reply = OffloadReplyEnvelope(correlationId: turn.correlationId, success: false,
                                         outputText: "", outputTokenCount: 0,
                                         failureReason: "out of memory", reasoningText: nil,
                                         completedAtUtc: epoch)
        c.dispatch(try MeshOffloadWire.encodeReply(sourceNodeId: "them",
                                                   destinationNodeId: "me", reply))

        let result = try await pending
        XCTAssertFalse(result.success)
        XCTAssertEqual(result.failureReason, "out of memory")
    }

    func testStoppingReleasesAnyoneStillWaiting() async throws {
        // A continuation abandoned at shutdown is a task suspended forever, and
        // the Swift runtime will not clean it up.
        let t = LoopTransport()
        let c = client(t, RecordingRegistry(), EchoFallback())
        try await c.start()

        let turn = OffloadTurn.create(modelId: "qwen", prompt: "hi")!
        async let pending = c.request(peerId: "them", turn: turn, timeout: 30)

        var tries = 0
        while t.sent.isEmpty && tries < 200 {
            try await Task.sleep(nanoseconds: 2_000_000); tries += 1
        }
        await c.stop()

        let result = try await pending
        XCTAssertFalse(result.success, "a stopped client answers rather than hanging")
    }

    func testIsReadyTracksTheTransportAndThePump() async throws {
        let t = LoopTransport()
        let c = client(t, RecordingRegistry(), EchoFallback())
        XCTAssertFalse(c.isReady, "not started")

        try await c.start()
        XCTAssertTrue(c.isReady)

        t.isAvailable = false
        XCTAssertFalse(c.isReady, "a dead transport is not ready even while pumping")

        await c.stop()
    }
}

private final class FreeBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: Int
    init(_ v: Int) { stored = v }
    var value: Int {
        get { lock.lock(); defer { lock.unlock() }; return stored }
        set { lock.lock(); stored = newValue; lock.unlock() }
    }
}
