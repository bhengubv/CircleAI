// MeshTransportClient.swift
//
// Borrowing a brain from a phone on the same mesh, over a transport the host
// already wired.
//
// THREE THINGS SHARE ONE TRANSPORT and the dispatch keeping them apart is the
// whole design: our own outbound requests waiting for replies, other people's
// inbound requests we serve, and capability adverts we fold into the registry.
// They are told apart by content type, and each is handled so it cannot stall
// the others — a slow inference serving a peer must not stop us receiving the
// reply to our own question.
//
// WE DO NOT DISCOVER PEERS. Zero-infrastructure BLE and Wi-Fi Direct discovery
// is AetherNet's job, in its own repository. This publishes over a transport
// that already exists and folds in whatever arrives.
//
// Ported from src/CircleAI.Mesh/{AetherMeshCapabilityBroadcaster,
// MeshOffloadClient}.cs.

import Foundation

// MARK: - Broadcasting what this node can do

/// Sends our advertisement, destination-less, so every reachable peer's ingest
/// loop folds it into their registry.
public final class AetherMeshCapabilityBroadcaster: IMeshCapabilityBroadcaster, @unchecked Sendable {

    private let transport: any INetworkTransport
    private let options: MeshOffloadOptions
    private let now: @Sendable () -> Date
    private let log: (@Sendable (String) -> Void)?

    public init(transport: any INetworkTransport,
                options: MeshOffloadOptions,
                now: @escaping @Sendable () -> Date = { Date() },
                log: (@Sendable (String) -> Void)? = nil) {
        self.transport = transport
        self.options = options
        self.now = now
        self.log = log
    }

    public func broadcast(_ ad: MeshCapabilityAdvertisement) async throws {
        guard transport.isAvailable else {
            log?("mesh advert: transport \(transport.kind) unavailable; skipping broadcast")
            return
        }

        // STAMPED HERE, not by the caller. Peers dedupe on peer id and measure
        // staleness from the moment we actually SENT it — a timestamp taken
        // when the advertisement was built makes us look stale before we have
        // said anything.
        let stamped = MeshCapabilityAdvertisement(
            peerId: options.localNodeId,
            modelId: ad.modelId,
            freeKvTokens: ad.freeKvTokens,
            tier: ad.tier,
            contextWindowTokens: ad.contextWindowTokens,
            advertisedAtUtc: now(),
            latencyHintMs: ad.latencyHintMs)

        let envelope = MeshAdvertEnvelope(
            peerId: stamped.peerId,
            modelId: stamped.modelId,
            freeKvTokens: stamped.freeKvTokens,
            tier: stamped.tier.rawValue,
            contextWindowTokens: stamped.contextWindowTokens,
            advertisedAtUtc: stamped.advertisedAtUtc,
            latencyHintMs: stamped.latencyHintMs)

        // TTL IS THE FRESHNESS WINDOW. A peer that stops hearing us expires us
        // anyway, so a packet that outlives that window is only ever noise.
        let payload = try MeshOffloadWire.encodeAdvert(sourceNodeId: options.localNodeId,
                                                       envelope, ttl: options.staleAfter)
        do {
            try await transport.send(payload)
            log?("mesh advert: broadcast \(stamped.modelId) over \(transport.kind)")
        } catch {
            // A failed broadcast is not worth failing a caller over: the next
            // beacon tick is seconds away and nothing downstream is waiting.
            log?("mesh advert: broadcast failed over \(transport.kind): \(error)")
        }
    }
}

/// Re-broadcasts on a cadence, so this node never ages out of a peer's
/// freshness window.
public final class MeshAdvertisementBeacon: @unchecked Sendable {

    private let broadcaster: any IMeshCapabilityBroadcaster
    private let interval: TimeInterval
    /// Returns what to advertise right now, or nil. A closure rather than a
    /// stored value because free KV budget changes minute to minute, and an
    /// advertisement captured at startup advertises a phone that no longer
    /// exists.
    private let advertisement: @Sendable () -> MeshCapabilityAdvertisement?
    private let log: (@Sendable (String) -> Void)?

    private let lock = NSLock()
    private var loop: Task<Void, Never>?

    public init(broadcaster: any IMeshCapabilityBroadcaster,
                interval: TimeInterval,
                advertisement: @escaping @Sendable () -> MeshCapabilityAdvertisement?,
                log: (@Sendable (String) -> Void)? = nil) {
        self.broadcaster = broadcaster
        self.interval = interval
        self.advertisement = advertisement
        self.log = log
    }

    public var isRunning: Bool {
        lock.lock(); defer { lock.unlock() }
        return loop != nil
    }

    public func start() {
        lock.lock()
        guard loop == nil else { lock.unlock(); return }
        let task: Task<Void, Never> = Task { [weak self] in
            guard let self else { return }
            await self.run()
        }
        loop = task
        lock.unlock()
    }

    public func stop() {
        lock.lock()
        let task = loop
        loop = nil
        lock.unlock()
        task?.cancel()
    }

    private func run() async {
        while !Task.isCancelled {
            await tick()
            do {
                try await Task.sleep(nanoseconds: UInt64(max(0.1, interval) * 1_000_000_000))
            } catch {
                break
            }
        }
    }

    /// One beacon tick. Internal so a test can drive the cadence rather than
    /// waiting for it.
    func tick() async {
        guard let ad = advertisement() else {
            // A node that advertises nothing BORROWS ONLY. That is a legitimate
            // configuration — a cheap phone with no model to share — and it must
            // not look like a failure.
            return
        }
        do {
            try await broadcaster.broadcast(ad)
        } catch {
            log?("mesh advert beacon: tick failed: \(error)")
        }
    }
}

// MARK: - The offload client

/// Sends turns to peers, serves turns for peers, and ingests their adverts.
public final class MeshOffloadClient: IMeshOffloadClient, @unchecked Sendable {

    private let transport: any INetworkTransport
    private let registry: any IMeshCapabilityRegistry
    private let localFallback: any ILocalInferenceFallback
    private let options: MeshOffloadOptions
    private let now: @Sendable () -> Date
    private let log: (@Sendable (String) -> Void)?

    private let lock = NSLock()
    private var pending: [String: CheckedContinuation<OffloadReplyEnvelope, Error>] = [:]
    private var pump: Task<Void, Never>?
    private var serving = 0

    public init(transport: any INetworkTransport,
                registry: any IMeshCapabilityRegistry,
                localFallback: any ILocalInferenceFallback,
                options: MeshOffloadOptions,
                now: @escaping @Sendable () -> Date = { Date() },
                log: (@Sendable (String) -> Void)? = nil) {
        self.transport = transport
        self.registry = registry
        self.localFallback = localFallback
        self.options = options
        self.now = now
        self.log = log
    }

    public var isReady: Bool {
        lock.lock(); defer { lock.unlock() }
        return transport.isAvailable && pump != nil
    }

    // MARK: Lifecycle

    public func start() async throws {
        lock.lock()
        guard pump == nil else { lock.unlock(); return }
        lock.unlock()

        if options.startTransport && !transport.isAvailable {
            try? await transport.start()
        }

        let task: Task<Void, Never> = Task { [weak self] in
            guard let self else { return }
            await self.runPump()
        }
        lock.lock(); pump = task; lock.unlock()
    }

    public func stop() async {
        lock.lock()
        let task = pump
        pump = nil
        // RELEASE EVERYONE STILL WAITING. A continuation abandoned at shutdown
        // is a task suspended forever, and in Swift that is a leak the runtime
        // will not clean up.
        let waiting = pending
        pending.removeAll()
        lock.unlock()

        task?.cancel()
        // AWAIT it. Cancelling asks the pump to stop; it does not witness that
        // it has. Returning while it is still suspended leaves a task holding a
        // thread of the cooperative pool, and enough of those starve the pool
        // so completely that nothing runs at all.
        await task?.value
        for (_, c) in waiting { c.resume(throwing: CancellationError()) }
    }

    // MARK: Consumer side

    public func request(peerId: String, turn: OffloadTurn,
                        timeout: TimeInterval) async throws -> OffloadResult {
        guard !peerId.trimmingCharacters(in: .whitespaces).isEmpty else {
            return OffloadResult.fail("A peer id is required.", servedBy: .none)
        }
        guard transport.isAvailable else {
            return OffloadResult.fail("Transport \(transport.kind) is not available.",
                                      servedBy: .none)
        }

        let started = now()
        let envelope = OffloadRequestEnvelope(turn: turn, replyToNodeId: options.localNodeId)

        do {
            let payload = try MeshOffloadWire.encodeRequest(
                sourceNodeId: options.localNodeId, destinationPeerId: peerId,
                envelope, ttl: timeout)

            let reply = try await withReply(correlationId: turn.correlationId,
                                            timeout: timeout) {
                try await self.transport.send(payload)
            }

            let elapsed = now().timeIntervalSince(started) * 1000
            return OffloadResult(
                success: reply.success,
                outputText: reply.outputText,
                servedBy: .remotePeer,
                servingPeerId: peerId,
                outputTokenCount: reply.outputTokenCount,
                elapsedMilliseconds: elapsed,
                failureReason: reply.success ? nil
                    : (reply.failureReason ?? "Remote peer reported failure."),
                reasoningText: reply.reasoningText)

        } catch is DuplicateCorrelation {
            return OffloadResult.fail("Duplicate correlation id already in flight.",
                                      servedBy: .none)
        } catch is OffloadTimeout {
            return OffloadResult.fail(
                String(format: "Peer %@ did not reply within %.1fs.", peerId, timeout),
                servedBy: .none,
                elapsedMilliseconds: now().timeIntervalSince(started) * 1000)
        } catch {
            return OffloadResult.fail("Offload to peer \(peerId) failed: \(error)",
                                      servedBy: .none,
                                      elapsedMilliseconds: now().timeIntervalSince(started) * 1000)
        }
    }

    private struct DuplicateCorrelation: Error {}
    private struct OffloadTimeout: Error {}

    /// Registers the correlation id, runs `send`, and waits for the matching
    /// reply or the timeout.
    private func withReply(correlationId: String, timeout: TimeInterval,
                           send: @escaping @Sendable () async throws -> Void
    ) async throws -> OffloadReplyEnvelope {

        lock.lock()
        if pending[correlationId] != nil {
            lock.unlock()
            throw DuplicateCorrelation()
        }
        lock.unlock()

        defer {
            lock.lock(); pending.removeValue(forKey: correlationId); lock.unlock()
        }

        return try await withThrowingTaskGroup(of: OffloadReplyEnvelope.self) { group in
            group.addTask {
                try await withCheckedThrowingContinuation { c in
                    self.lock.lock()
                    self.pending[correlationId] = c
                    self.lock.unlock()
                }
            }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(max(0, timeout) * 1_000_000_000))
                throw OffloadTimeout()
            }

            try await send()

            let first = try await group.next()!
            group.cancelAll()
            return first
        }
    }

    // MARK: The pump

    private func runPump() async {
        while !Task.isCancelled {
            for await payload in transport.receive() {
                if Task.isCancelled { return }
                dispatch(payload)
            }
            // The stream ended without cancellation, so the transport closed it.
            // Pause and re-subscribe — reconnecting is the transport's concern,
            // not ours, but noticing it stopped IS.
            if Task.isCancelled { return }
            try? await Task.sleep(nanoseconds: 1_000_000_000)
        }
    }

    func dispatch(_ payload: NetworkPayload) {
        switch payload.contentType {
        case MeshOffloadWire.replyContentType:
            handleReply(payload)

        case MeshOffloadWire.requestContentType:
            // Served on its OWN task: a slow inference must not stall the pump,
            // or we could not receive replies to our own outbound turns.
            Task { await serve(payload) }

        case MeshOffloadWire.advertContentType:
            Task { await ingestAdvert(payload) }

        default:
            // A shared transport carries other CircleAI traffic. Not ours.
            break
        }
    }

    private func handleReply(_ payload: NetworkPayload) {
        guard let reply = MeshOffloadWire.decodeReply(payload) else { return }

        lock.lock()
        let waiter = pending.removeValue(forKey: reply.correlationId)
        lock.unlock()

        // A missing waiter is a LATE reply — the requester already gave up and
        // moved on. Dropping it is correct; logging it as an error is noise.
        waiter?.resume(returning: reply)
    }

    // MARK: Provider side

    func serve(_ payload: NetworkPayload) async {
        guard options.serveInboundRequests else { return }
        guard let request = MeshOffloadWire.decodeRequest(payload),
              !request.replyToNodeId.trimmingCharacters(in: .whitespaces).isEmpty
        else { return }

        let reply: OffloadReplyEnvelope

        if !claimServeSlot() {
            // AT CAPACITY IS AN ANSWER, not silence. A peer told it is busy
            // tries somebody else; a peer told nothing waits out its timeout.
            reply = OffloadReplyEnvelope(
                correlationId: request.correlationId, success: false, outputText: "",
                outputTokenCount: 0, failureReason: "Serving peer is at capacity.",
                reasoningText: nil, completedAtUtc: now())
        } else {
            defer { releaseServeSlot() }
            let turn = OffloadTurn(
                modelId: request.modelId, prompt: request.prompt,
                maxOutputTokens: request.maxOutputTokens, temperature: request.temperature,
                topP: request.topP, stopSequences: request.stopSequences,
                correlationId: request.correlationId, createdAtUtc: request.createdAtUtc)
            do {
                let result = try await localFallback.complete(turn)
                reply = OffloadReplyEnvelope(
                    correlationId: request.correlationId, success: result.success,
                    outputText: result.outputText, outputTokenCount: result.outputTokenCount,
                    failureReason: result.failureReason, reasoningText: result.reasoningText,
                    completedAtUtc: now())
            } catch {
                reply = OffloadReplyEnvelope(
                    correlationId: request.correlationId, success: false, outputText: "",
                    outputTokenCount: 0,
                    failureReason: "Serving peer raised an error: \(error)",
                    reasoningText: nil, completedAtUtc: now())
            }
        }

        do {
            let out = try MeshOffloadWire.encodeReply(
                sourceNodeId: options.localNodeId, destinationNodeId: request.replyToNodeId,
                reply, ttl: options.requestTimeout)
            try await transport.send(out)
        } catch {
            log?("mesh offload: failed to send reply \(request.correlationId): \(error)")
        }
    }

    private func claimServeSlot() -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard serving < max(1, options.maxConcurrentServed) else { return false }
        serving += 1
        return true
    }

    private func releaseServeSlot() {
        lock.lock(); serving = max(0, serving - 1); lock.unlock()
    }

    // MARK: Advertisement ingest

    func ingestAdvert(_ payload: NetworkPayload) async {
        guard let env = MeshOffloadWire.decodeAdvert(payload),
              !env.peerId.trimmingCharacters(in: .whitespaces).isEmpty
        else { return }

        // OUR OWN ADVERT, ECHOED BACK by a broadcast transport. Folding it into
        // the registry makes this device its own best peer, and it then tries to
        // offload to itself.
        guard env.peerId != options.localNodeId else { return }

        let ad = MeshCapabilityAdvertisement(
            peerId: env.peerId,
            modelId: env.modelId,
            freeKvTokens: env.freeKvTokens,
            tier: DeviceTier(rawValue: env.tier) ?? .phone,
            contextWindowTokens: env.contextWindowTokens,
            advertisedAtUtc: env.advertisedAtUtc,
            latencyHintMs: env.latencyHintMs)

        try? await registry.upsert(ad)
    }
}
