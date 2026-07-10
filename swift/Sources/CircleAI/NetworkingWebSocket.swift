// NetworkingWebSocket.swift
//
// Port of CircleAI.Networking.WebSocket (the C# reference) — the full-duplex
// WebSocket network transport. Collapses the C# folder's two files
// (WebSocketTransport.cs / WebSocketTransportCommons.cs) into this single Swift
// file per the tree's flat convention.
//
// Ported types (1:1 with the C# under src/CircleAI.Networking.WebSocket/):
//   Enums     — WebSocketLinkState, WebSocketMessageType
//   DTOs      — WebSocketEndpointDescriptor, WebSocketFrameSummary
//   Registry  — InMemoryWebSocketSessionRegistry
//   Transport — WebSocketTransport (INetworkTransport) + IWebSocketSocket
//
// Injected-socket note — the C# WebSocketTransport wraps a concrete
// ClientWebSocket (a real socket): ConnectAsync opens it, SendAsync sends the
// payload as a single binary end-of-message frame, and PumpAsync receives frames
// into an unbounded inbound Channel (breaking on a Close frame). This port
// follows the task rule "inject the socket behind an interface; every contract
// gets a working deterministic implementation": the WebSocket is injected behind
// IWebSocketSocket, handed an IWebSocketInboundWriter on connect so it can push
// received binary frames. The send-as-binary-EOM behaviour and the open-state
// availability check are ported faithfully.
//
// Concurrency (same rules as Networking.swift):
//   • Snapshot continuations UNDER the NSLock and finish() OUTSIDE it — finish()
//     runs onTermination synchronously and re-enters the same non-reentrant lock.
//   • The inbound stream is single-consumer with UNBOUNDED buffering, so a frame
//     the socket pushes before receive() is iterated is retained, not lost
//     (mirrors C#'s unbounded inbound Channel<NetworkPayload>).

import Foundation

// ──────────────────────────────────────────────────────────────────────────
// WebSocketLinkState / WebSocketMessageType (WebSocketTransportCommons.cs)
//
// Int-raw + Codable; ordinals follow the C# declaration order. The C# enum's
// last member is `Closed_Error` — its ordinal (5) is preserved; the Swift case
// is spelled `closedError` (idiomatic camelCase), the raw value is what matters
// for the cross-language wire contract.
// ──────────────────────────────────────────────────────────────────────────

/// The link-layer state of a WebSocket session. Ordinals mirror the C#
/// `WebSocketLinkState` declaration order (`Closed`=0 … `Closed_Error`=5).
public enum WebSocketLinkState: Int, Codable, Sendable, CaseIterable {
    case closed = 0
    case connecting = 1
    case open = 2
    case closeSent = 3
    case closeReceived = 4
    /// The C# `Closed_Error` member (ordinal 5): closed due to an error.
    case closedError = 5
}

/// A WebSocket frame's message type. Ordinals mirror the C#
/// `WebSocketMessageType` declaration order.
public enum WebSocketMessageType: Int, Codable, Sendable, CaseIterable {
    case text = 0
    case binary = 1
    case ping = 2
    case pong = 3
    case close = 4
}

// ──────────────────────────────────────────────────────────────────────────
// WebSocketEndpointDescriptor / WebSocketFrameSummary (records)
// ──────────────────────────────────────────────────────────────────────────

/// Describes a WebSocket endpoint. Ported from the C#
/// `WebSocketEndpointDescriptor` record. `uri` is stored as a `String` (the URL
/// text) so the descriptor is `Codable`/`Equatable` without depending on
/// `Foundation.URL`'s normalisation; `headers` is optional (C#'s nullable
/// dictionary); `pingInterval` is seconds (C#'s TimeSpan).
public struct WebSocketEndpointDescriptor: Sendable, Equatable, Codable {
    public let uri: String
    public let headers: [String: String]?
    public let pingInterval: TimeInterval
    public let subprotocols: [String]

    public init(
        uri: String,
        headers: [String: String]?,
        pingInterval: TimeInterval,
        subprotocols: [String]
    ) {
        self.uri = uri
        self.headers = headers
        self.pingInterval = pingInterval
        self.subprotocols = subprotocols
    }
}

/// A summary of a single WebSocket frame. Ported from the C#
/// `WebSocketFrameSummary` record.
public struct WebSocketFrameSummary: Sendable, Equatable, Codable {
    public let sessionId: String
    public let type: WebSocketMessageType
    public let bytes: Int
    public let atUtc: Date

    public init(
        sessionId: String,
        type: WebSocketMessageType,
        bytes: Int,
        atUtc: Date
    ) {
        self.sessionId = sessionId
        self.type = type
        self.bytes = bytes
        self.atUtc = atUtc
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryWebSocketSessionRegistry (WebSocketTransportCommons.cs)
//
// C# uses two ConcurrentDictionaries (endpoints + states) and a lock-guarded List
// for frames. Here a single NSLock guards all of it; default state is Closed,
// TotalBytes sums a session's frame bytes, and FrameCount counts a session's
// frames of a given type — all matching exactly.
// ──────────────────────────────────────────────────────────────────────────

/// In-memory registry of WebSocket sessions, link states, and frame summaries.
/// Ported from the C# `InMemoryWebSocketSessionRegistry`.
public final class InMemoryWebSocketSessionRegistry: @unchecked Sendable {
    private let lock = NSLock()
    private var endpoints: [String: WebSocketEndpointDescriptor] = [:]
    private var states: [String: WebSocketLinkState] = [:]
    private var frames: [WebSocketFrameSummary] = []

    public init() {}

    /// Register (or replace) an endpoint descriptor keyed by `sessionId`.
    public func register(_ sessionId: String, _ d: WebSocketEndpointDescriptor) {
        lock.lock(); endpoints[sessionId] = d; lock.unlock()
    }

    /// The endpoint descriptor for `sessionId`, or nil (matches C#'s
    /// `GetValueOrDefault`).
    public func get(_ sessionId: String) -> WebSocketEndpointDescriptor? {
        lock.lock(); defer { lock.unlock() }
        return endpoints[sessionId]
    }

    /// Set the link state for a session.
    public func setState(_ sessionId: String, _ s: WebSocketLinkState) {
        lock.lock(); states[sessionId] = s; lock.unlock()
    }

    /// The link state for a session, or `.closed` (matches C#'s default).
    public func state(_ sessionId: String) -> WebSocketLinkState {
        lock.lock(); defer { lock.unlock() }
        return states[sessionId] ?? .closed
    }

    /// Record a frame summary.
    public func recordFrame(_ f: WebSocketFrameSummary) {
        lock.lock(); frames.append(f); lock.unlock()
    }

    /// Total bytes across all frames for `sessionId` (matches C#'s
    /// `Sum(f => (long)f.Bytes)`).
    public func totalBytes(_ sessionId: String) -> Int64 {
        lock.lock(); defer { lock.unlock() }
        return frames.filter { $0.sessionId == sessionId }.reduce(0) { $0 + Int64($1.bytes) }
    }

    /// Count of frames for `sessionId` of the given `type` (matches C#'s
    /// `Count(f => f.SessionId == sessionId && f.Type == type)`).
    public func frameCount(_ sessionId: String, _ type: WebSocketMessageType) -> Int {
        lock.lock(); defer { lock.unlock() }
        return frames.filter { $0.sessionId == sessionId && $0.type == type }.count
    }
}

// ──────────────────────────────────────────────────────────────────────────
// IWebSocketInboundWriter / IWebSocketSocket (WebSocketTransport.cs)
//
// The injected socket seam (the Swift analogue of ClientWebSocket). On connect
// the transport hands the socket an IWebSocketInboundWriter so the socket can
// push received binary frames (the analogue of the C# PumpAsync loop feeding the
// inbound Channel). The transport sends the payload as a single binary
// end-of-message frame via send().
// ──────────────────────────────────────────────────────────────────────────

/// The sink an `IWebSocketSocket` uses to push a received binary-frame payload
/// into the transport's inbound stream. The Swift analogue of C#'s `PumpAsync`
/// writing into the inbound Channel.
public protocol IWebSocketInboundWriter: AnyObject, Sendable {
    /// Deliver a received payload into the transport's inbound stream. Returns
    /// false once the inbound stream has been completed (stopped).
    @discardableResult
    func push(_ payload: NetworkPayload) -> Bool
}

/// The injected WebSocket — the Swift analogue of C#'s `ClientWebSocket`.
/// Implement per platform (or in tests). `isOpen` backs the transport's
/// `isAvailable` (C#'s `_ws?.State == WebSocketState.Open`).
public protocol IWebSocketSocket: AnyObject {
    /// True when the socket is open (C#'s `State == WebSocketState.Open`).
    var isOpen: Bool { get }

    /// Connect the socket, retaining `inbound` so received frames can be pushed
    /// into the transport (C#'s `ConnectAsync` + starting the pump).
    func connect(inbound: IWebSocketInboundWriter) async throws

    /// Send `data` as a single binary end-of-message frame (C#'s `SendAsync` with
    /// `WebSocketMessageType.Binary, endOfMessage: true`).
    func send(_ data: Data) async throws

    /// Close the socket with the normal-closure status (C#'s `CloseAsync(
    /// WebSocketCloseStatus.NormalClosure, "stop", ct)`).
    func close() async throws
}

// ──────────────────────────────────────────────────────────────────────────
// WebSocketTransport (WebSocketTransport.cs)
// ──────────────────────────────────────────────────────────────────────────

/// Full-duplex `INetworkTransport` backed by an injected WebSocket. `start`
/// connects; `send` transmits the payload as one binary end-of-message frame
/// (throwing when the socket is nil, C#'s `ArgumentNullException.ThrowIfNull(_ws)`);
/// `receive` drains binary frames the socket pushes inbound; `stop` closes the
/// socket then completes the inbound stream. Mirrors the C# `WebSocketTransport`.
public final class WebSocketTransport: INetworkTransport, @unchecked Sendable {
    /// The inbound sink handed to the socket. Buffers frames pushed before
    /// `receive()` is iterated (unbounded) so none are lost.
    private final class InboundWriter: IWebSocketInboundWriter, @unchecked Sendable {
        private let lock = NSLock()
        private var completed = false
        private var pending: [NetworkPayload] = []
        private var continuation: AsyncStream<NetworkPayload>.Continuation?

        @discardableResult
        func push(_ payload: NetworkPayload) -> Bool {
            lock.lock()
            if completed { lock.unlock(); return false }
            if let cont = continuation {
                cont.yield(payload)
            } else {
                pending.append(payload)
            }
            lock.unlock()
            return true
        }

        func stream() -> AsyncStream<NetworkPayload> {
            AsyncStream(bufferingPolicy: .unbounded) { continuation in
                lock.lock()
                if completed {
                    lock.unlock()
                    continuation.finish()
                    return
                }
                for p in pending { continuation.yield(p) }
                pending.removeAll()
                self.continuation = continuation
                lock.unlock()

                continuation.onTermination = { [weak self] _ in
                    guard let self else { return }
                    self.lock.lock(); self.continuation = nil; self.lock.unlock()
                }
            }
        }

        func complete() {
            lock.lock()
            completed = true
            let cont = continuation
            continuation = nil
            pending.removeAll()
            lock.unlock()
            cont?.finish()
        }
    }

    private let socket: IWebSocketSocket
    private let endpoint: String
    private let inbound = InboundWriter()

    /// - Parameters:
    ///   - socket: the injected WebSocket (the socket seam).
    ///   - endpoint: the endpoint URL text (the C# constructor's `endpoint`, used
    ///     to build the `Uri`). Retained for parity/inspection; the injected
    ///     socket owns the actual connection target.
    public init(socket: IWebSocketSocket, endpoint: String) {
        self.socket = socket
        self.endpoint = endpoint
    }

    public var kind: TransportKind { .webSocket }

    /// Mirrors C#'s `IsAvailable => _ws?.State == WebSocketState.Open`.
    public var isAvailable: Bool { socket.isOpen }

    /// The endpoint URL text this transport was constructed with.
    public var endpointUri: String { endpoint }

    /// Connect the socket, handing it the inbound writer (C#'s `ConnectAsync` +
    /// starting `PumpAsync`).
    public func start() async throws {
        try await socket.connect(inbound: inbound)
    }

    /// Close the socket, then complete the inbound stream (C#'s `CloseAsync` then
    /// `_inbound.Writer.TryComplete()`).
    public func stop() async throws {
        try await socket.close()
        inbound.complete()
    }

    /// Send the payload as a single binary end-of-message frame (C#'s `SendAsync`
    /// with `WebSocketMessageType.Binary, endOfMessage: true`).
    public func send(_ payload: NetworkPayload) async throws {
        try await socket.send(payload.data)
    }

    /// Yields inbound payloads the socket pushed. Mirrors C#'s
    /// `_inbound.Reader.ReadAllAsync(ct)`.
    public func receive() -> AsyncStream<NetworkPayload> {
        inbound.stream()
    }
}
