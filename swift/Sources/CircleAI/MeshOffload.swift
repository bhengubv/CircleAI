// MeshOffload.swift
//
// Borrowing a nearby phone to think with: route a turn to a peer that has the
// model loaded and spare KV budget, fall back to this device when none does.
//
// Ported from src/CircleAI.Mesh.

import Foundation

// MARK: - Contracts

/// Who actually answered.
public enum OffloadServedBy: Int, Sendable, Equatable, Codable {
    case remotePeer = 0
    case localFallback = 1
    case none = 2
}

/// One turn to be answered, here or elsewhere.
public struct OffloadTurn: Sendable, Equatable, Codable {
    public let modelId: String
    public let prompt: String
    public let maxOutputTokens: Int
    public let temperature: Float
    public let topP: Float
    public let stopSequences: [String]
    public let correlationId: String
    public let createdAtUtc: Date

    public init(modelId: String, prompt: String, maxOutputTokens: Int, temperature: Float,
                topP: Float, stopSequences: [String], correlationId: String, createdAtUtc: Date) {
        self.modelId = modelId
        self.prompt = prompt
        self.maxOutputTokens = maxOutputTokens
        self.temperature = temperature
        self.topP = topP
        self.stopSequences = stopSequences
        self.correlationId = correlationId
        self.createdAtUtc = createdAtUtc
    }

    public static func create(modelId: String, prompt: String, maxOutputTokens: Int = 256,
                              temperature: Float = 0.7, topP: Float = 0.95,
                              stopSequences: [String]? = nil,
                              correlationId: String = UUID().uuidString
                                  .replacingOccurrences(of: "-", with: "").lowercased(),
                              now: Date = Date()) -> OffloadTurn? {
        guard !modelId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        return OffloadTurn(modelId: modelId, prompt: prompt, maxOutputTokens: maxOutputTokens,
                           temperature: temperature, topP: topP,
                           stopSequences: stopSequences ?? [],
                           correlationId: correlationId, createdAtUtc: now)
    }
}

public struct OffloadResult: Sendable, Equatable, Codable {
    public let success: Bool
    public let outputText: String
    public let servedBy: OffloadServedBy
    public let servingPeerId: String?
    public let outputTokenCount: Int
    public let elapsedMilliseconds: Double
    public let failureReason: String?
    public let reasoningText: String?

    public init(success: Bool, outputText: String, servedBy: OffloadServedBy,
                servingPeerId: String?, outputTokenCount: Int, elapsedMilliseconds: Double,
                failureReason: String?, reasoningText: String? = nil) {
        self.success = success
        self.outputText = outputText
        self.servedBy = servedBy
        self.servingPeerId = servingPeerId
        self.outputTokenCount = outputTokenCount
        self.elapsedMilliseconds = elapsedMilliseconds
        self.failureReason = failureReason
        self.reasoningText = reasoningText
    }

    public static func fail(_ reason: String, servedBy: OffloadServedBy = .none,
                            elapsedMilliseconds: Double = 0) -> OffloadResult {
        OffloadResult(success: false, outputText: "", servedBy: servedBy, servingPeerId: nil,
                      outputTokenCount: 0, elapsedMilliseconds: elapsedMilliseconds,
                      failureReason: reason)
    }

    func with(servedBy: OffloadServedBy? = nil, failureReason: String?? = nil) -> OffloadResult {
        OffloadResult(success: success, outputText: outputText,
                      servedBy: servedBy ?? self.servedBy, servingPeerId: servingPeerId,
                      outputTokenCount: outputTokenCount, elapsedMilliseconds: elapsedMilliseconds,
                      failureReason: failureReason ?? self.failureReason,
                      reasoningText: reasoningText)
    }
}

// MARK: - Seams

public protocol IOffloadRouter: Sendable {
    func route(_ turn: OffloadTurn) async throws -> OffloadResult
}

public protocol ILocalInferenceFallback: Sendable {
    func complete(_ turn: OffloadTurn) async throws -> OffloadResult
}

/// This node can borrow a brain but has none of its own to lend - which is a
/// real configuration on a small phone, not an error.
public struct NullLocalInferenceFallback: ILocalInferenceFallback {
    public static let instance = NullLocalInferenceFallback()
    public init() {}
    public func complete(_ turn: OffloadTurn) async throws -> OffloadResult {
        OffloadResult.fail(
            "No local inference fallback is registered; this node can borrow a peer brain " +
            "but cannot serve locally.", servedBy: .none)
    }
}

public protocol IMeshOffloadClient: Sendable {
    var isReady: Bool { get }
    func request(peerId: String, turn: OffloadTurn, timeout: TimeInterval) async throws -> OffloadResult
}

// MARK: - Options

/// Everything the router is allowed to decide, in one place.
public struct MeshOffloadOptions: Sendable {
    public var localNodeId: String
    public var staleAfter: TimeInterval
    public var requestTimeout: TimeInterval
    public var maxPeerAttempts: Int
    public var kvHeadroomFactor: Double
    public var estimateKvTokens: @Sendable (OffloadTurn) -> Int
    public var selectPeer: @Sendable ([MeshCapabilityAdvertisement]) -> MeshCapabilityAdvertisement?
    public var serveInboundRequests: Bool
    public var maxConcurrentServed: Int
    public var startTransport: Bool
    public var broadcastInterval: TimeInterval

    public init(
        localNodeId: String = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased(),
        staleAfter: TimeInterval = 30,
        requestTimeout: TimeInterval = 30,
        maxPeerAttempts: Int = 2,
        kvHeadroomFactor: Double = 1.0,
        estimateKvTokens: (@Sendable (OffloadTurn) -> Int)? = nil,
        selectPeer: (@Sendable ([MeshCapabilityAdvertisement]) -> MeshCapabilityAdvertisement?)? = nil,
        serveInboundRequests: Bool = true,
        maxConcurrentServed: Int = 2,
        startTransport: Bool = true,
        broadcastInterval: TimeInterval = 15
    ) {
        self.localNodeId = localNodeId
        self.staleAfter = staleAfter
        self.requestTimeout = requestTimeout
        self.maxPeerAttempts = maxPeerAttempts
        self.kvHeadroomFactor = kvHeadroomFactor
        // Four characters to the token is the rough English ratio; the output
        // budget is exact because the caller asked for it.
        self.estimateKvTokens = estimateKvTokens ?? { t in (t.prompt.count / 4) + t.maxOutputTokens }
        self.selectPeer = selectPeer ?? { MeshOffloadOptions.defaultSelectPeer($0) }
        self.serveInboundRequests = serveInboundRequests
        self.maxConcurrentServed = maxConcurrentServed
        self.startTransport = startTransport
        self.broadcastInterval = broadcastInterval
    }

    /// Best tier first, then lowest latency, then most spare budget. A peer
    /// that reports NO latency hint sorts last on that key rather than first -
    /// unknown is not fast.
    public static func defaultSelectPeer(
        _ candidates: [MeshCapabilityAdvertisement]) -> MeshCapabilityAdvertisement? {
        var best: MeshCapabilityAdvertisement?
        for c in candidates {
            guard let b = best else { best = c; continue }

            if c.tier.rawValue > b.tier.rawValue { best = c; continue }
            if c.tier.rawValue < b.tier.rawValue { continue }

            let cl = c.latencyHintMs ?? Int.max
            let bl = b.latencyHintMs ?? Int.max
            if cl < bl { best = c; continue }
            if cl > bl { continue }

            if c.freeKvTokens > b.freeKvTokens { best = c }
        }
        return best
    }
}

// MARK: - The router

/// Picks a peer, tries it, tries the next, and falls back to this device.
/// Every path returns a result: a mesh that throws when no peer answers is a
/// mesh that takes the whole app down when somebody walks out of range.
public struct MeshOffloadRouter: IOffloadRouter {
    private let registry: any IMeshCapabilityRegistry
    private let client: any IMeshOffloadClient
    private let localFallback: any ILocalInferenceFallback
    private let options: MeshOffloadOptions
    private let clock: @Sendable () -> Date

    public init(registry: any IMeshCapabilityRegistry,
                client: any IMeshOffloadClient,
                localFallback: any ILocalInferenceFallback,
                options: MeshOffloadOptions = MeshOffloadOptions(),
                clock: @escaping @Sendable () -> Date = { Date() }) {
        self.registry = registry
        self.client = client
        self.localFallback = localFallback
        self.options = options
        self.clock = clock
    }

    public func route(_ turn: OffloadTurn) async throws -> OffloadResult {
        let estimate = max(0, options.estimateKvTokens(turn))
        let minFreeKv = max(0, Int(ceil(Double(estimate) * options.kvHeadroomFactor)))

        let candidates = registry.find(modelId: turn.modelId,
                                       minFreeKvTokens: minFreeKv,
                                       staleAfter: options.staleAfter)
        if candidates.isEmpty {
            return await fallBackLocal(turn, why: "No capable peer advertised.")
        }

        var pool = candidates
        var tried = Set<String>()
        var reasons: [String] = []
        let attempts = max(1, options.maxPeerAttempts)

        for _ in 0..<attempts {
            if pool.isEmpty { break }
            let pick = options.selectPeer(pool) ?? pool[0]
            // Removed from the pool whether or not it is tried, so a selector
            // that keeps returning the same peer cannot spin.
            pool.removeAll { $0.peerId == pick.peerId }
            if !tried.insert(pick.peerId).inserted { continue }

            do {
                let remote = try await client.request(peerId: pick.peerId, turn: turn,
                                                      timeout: options.requestTimeout)
                if remote.success { return remote }
                reasons.append("\(pick.peerId): \(remote.failureReason ?? "unknown")")
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                reasons.append("\(pick.peerId): \(error.localizedDescription)")
            }
        }

        return await fallBackLocal(turn, why: "All peer attempts failed: " + reasons.joined(separator: "; "))
    }

    private func fallBackLocal(_ turn: OffloadTurn, why: String) async -> OffloadResult {
        let started = clock()
        do {
            var local = try await localFallback.complete(turn)

            // A fallback that answered but did not say who served it gets
            // labelled here, so the caller can always tell where words came from.
            if local.success && local.servedBy == .none {
                local = local.with(servedBy: .localFallback)
            }
            // And a bare failure inherits WHY the mesh gave up, which is the
            // part that explains itself to a person.
            if !local.success && (local.failureReason ?? "").isEmpty {
                local = local.with(failureReason: .some(why))
            }
            return local
        } catch {
            let elapsed = clock().timeIntervalSince(started) * 1000
            return OffloadResult.fail("\(why) Local fallback also failed: \(error.localizedDescription)",
                                      servedBy: .none, elapsedMilliseconds: elapsed)
        }
    }
}

// MARK: - The wire
//
// Three envelope shapes and the content types that tell them apart. Kept
// separate from the transport so the encoding is testable without a radio.

public struct OffloadRequestEnvelope: Sendable, Equatable, Codable {
    public let correlationId: String
    public let replyToNodeId: String
    public let modelId: String
    public let prompt: String
    public let maxOutputTokens: Int
    public let temperature: Float
    public let topP: Float
    public let stopSequences: [String]
    public let createdAtUtc: Date

    public init(correlationId: String, replyToNodeId: String, modelId: String, prompt: String,
                maxOutputTokens: Int, temperature: Float, topP: Float,
                stopSequences: [String], createdAtUtc: Date) {
        self.correlationId = correlationId
        self.replyToNodeId = replyToNodeId
        self.modelId = modelId
        self.prompt = prompt
        self.maxOutputTokens = maxOutputTokens
        self.temperature = temperature
        self.topP = topP
        self.stopSequences = stopSequences
        self.createdAtUtc = createdAtUtc
    }

    public init(turn: OffloadTurn, replyToNodeId: String) {
        self.init(correlationId: turn.correlationId, replyToNodeId: replyToNodeId,
                  modelId: turn.modelId, prompt: turn.prompt,
                  maxOutputTokens: turn.maxOutputTokens, temperature: turn.temperature,
                  topP: turn.topP, stopSequences: turn.stopSequences,
                  createdAtUtc: turn.createdAtUtc)
    }
}

public struct OffloadReplyEnvelope: Sendable, Equatable, Codable {
    public let correlationId: String
    public let success: Bool
    public let outputText: String
    public let outputTokenCount: Int
    public let failureReason: String?
    public let reasoningText: String?
    public let completedAtUtc: Date

    public init(correlationId: String, success: Bool, outputText: String, outputTokenCount: Int,
                failureReason: String?, reasoningText: String?, completedAtUtc: Date) {
        self.correlationId = correlationId
        self.success = success
        self.outputText = outputText
        self.outputTokenCount = outputTokenCount
        self.failureReason = failureReason
        self.reasoningText = reasoningText
        self.completedAtUtc = completedAtUtc
    }
}

public struct MeshAdvertEnvelope: Sendable, Equatable, Codable {
    public let peerId: String
    public let modelId: String
    public let freeKvTokens: Int
    public let tier: Int
    public let contextWindowTokens: Int
    public let advertisedAtUtc: Date
    public let latencyHintMs: Int?

    public init(peerId: String, modelId: String, freeKvTokens: Int, tier: Int,
                contextWindowTokens: Int, advertisedAtUtc: Date, latencyHintMs: Int?) {
        self.peerId = peerId
        self.modelId = modelId
        self.freeKvTokens = freeKvTokens
        self.tier = tier
        self.contextWindowTokens = contextWindowTokens
        self.advertisedAtUtc = advertisedAtUtc
        self.latencyHintMs = latencyHintMs
    }

    public init(_ ad: MeshCapabilityAdvertisement) {
        self.init(peerId: ad.peerId, modelId: ad.modelId, freeKvTokens: ad.freeKvTokens,
                  tier: ad.tier.rawValue, contextWindowTokens: ad.contextWindowTokens,
                  advertisedAtUtc: ad.advertisedAtUtc, latencyHintMs: ad.latencyHintMs)
    }

    /// The tier travels as a number, so an unknown value from a newer build
    /// lands on phone rather than failing the whole advert.
    public func toAdvertisement() -> MeshCapabilityAdvertisement {
        MeshCapabilityAdvertisement(
            peerId: peerId, modelId: modelId, freeKvTokens: freeKvTokens,
            tier: DeviceTier(rawValue: tier) ?? .phone,
            contextWindowTokens: contextWindowTokens,
            advertisedAtUtc: advertisedAtUtc, latencyHintMs: latencyHintMs)
    }
}

public enum MeshOffloadWire {
    public static let requestContentType = "application/x-circleai-offload-request+json"
    public static let replyContentType = "application/x-circleai-offload-reply+json"
    public static let advertContentType = "application/x-circleai-mesh-advert+json"
    public static let correlationMetaKey = "circleai-offload-corr"

    /// ISO-8601 with fractional seconds, which is what the C# writes and what
    /// the other language bases already agreed on.
    static let iso: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    static func encoder() -> JSONEncoder {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .custom { date, enc in
            var c = enc.singleValueContainer()
            try c.encode(iso.string(from: date))
        }
        return e
    }

    static func decoder() -> JSONDecoder {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .custom { dec in
            let s = try dec.singleValueContainer().decode(String.self)
            if let parsed = iso.date(from: s) { return parsed }
            // Some encoders drop the fraction when it is zero.
            let plain = ISO8601DateFormatter()
            plain.formatOptions = [.withInternetDateTime]
            if let parsed = plain.date(from: s) { return parsed }
            throw DecodingError.dataCorrupted(
                .init(codingPath: dec.codingPath, debugDescription: "bad date: \(s)"))
        }
        return d
    }

    public static func encodeRequest(sourceNodeId: String, destinationPeerId: String,
                                     _ env: OffloadRequestEnvelope,
                                     ttl: TimeInterval? = nil) throws -> NetworkPayload {
        try build(sourceId: sourceNodeId, destinationId: destinationPeerId,
                  body: encoder().encode(env), contentType: requestContentType,
                  correlation: env.correlationId, priority: .high, ttl: ttl)
    }

    public static func encodeReply(sourceNodeId: String, destinationNodeId: String,
                                   _ env: OffloadReplyEnvelope,
                                   ttl: TimeInterval? = nil) throws -> NetworkPayload {
        try build(sourceId: sourceNodeId, destinationId: destinationNodeId,
                  body: encoder().encode(env), contentType: replyContentType,
                  correlation: env.correlationId, priority: .high, ttl: ttl)
    }

    /// An advert has NO destination - it is for whoever is listening.
    public static func encodeAdvert(sourceNodeId: String, _ env: MeshAdvertEnvelope,
                                    ttl: TimeInterval? = nil) throws -> NetworkPayload {
        try build(sourceId: sourceNodeId, destinationId: nil,
                  body: encoder().encode(env), contentType: advertContentType,
                  correlation: env.peerId, priority: .normal, ttl: ttl)
    }

    public static func decodeRequest(_ payload: NetworkPayload) -> OffloadRequestEnvelope? {
        try? decoder().decode(OffloadRequestEnvelope.self, from: payload.data)
    }

    public static func decodeReply(_ payload: NetworkPayload) -> OffloadReplyEnvelope? {
        try? decoder().decode(OffloadReplyEnvelope.self, from: payload.data)
    }

    public static func decodeAdvert(_ payload: NetworkPayload) -> MeshAdvertEnvelope? {
        try? decoder().decode(MeshAdvertEnvelope.self, from: payload.data)
    }

    private static func build(sourceId: String?, destinationId: String?, body: Data,
                              contentType: String, correlation: String,
                              priority: MessagePriority, ttl: TimeInterval?) -> NetworkPayload {
        NetworkPayload(
            id: UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased(),
            sourceId: sourceId,
            destinationId: destinationId,
            data: body,
            priority: priority,
            ttl: ttl,
            contentType: contentType,
            metadata: [correlationMetaKey: correlation],
            createdAt: Date())
    }
}
