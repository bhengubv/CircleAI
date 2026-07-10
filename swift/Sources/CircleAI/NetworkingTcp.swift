// NetworkingTcp.swift
//
// Port of CircleAI.Networking.Tcp (the C# reference) — the raw-TCP network
// transport. Collapses the C# folder's two files (TcpNetworkTransport.cs /
// TcpTransportCommons.cs) into this single Swift file per the tree's flat
// convention.
//
// Ported types (1:1 with the C# under src/CircleAI.Networking.Tcp/):
//   Enum      — TcpConnectionState
//   DTOs      — TcpEndpointDescriptor, TcpThroughputSample
//   Constants — TcpKnownPorts
//   Registry  — InMemoryTcpConnectionRegistry
//   Transport — TcpNetworkTransport (INetworkTransport) + ITcpStreamSocket
//
// Injected-socket note — the C# TcpNetworkTransport wraps a concrete TcpClient /
// TcpListener / NetworkStream (a real socket). It frames each payload as a
// 4-byte little-endian length prefix (BitConverter.GetBytes(data.Length))
// followed by the bytes, and its pump reads a 4-byte length then exactly that
// many bytes (ReadExactlyAsync). This port follows the task rule "inject the
// socket behind an interface; every contract gets a working deterministic
// implementation": the byte stream is injected behind ITcpStreamSocket. The
// length-prefix framing is ported byte-for-byte (4-byte little-endian, matching
// BitConverter on a little-endian host) so the wire format is identical, and the
// pump drains the injected socket exactly as the C# `PumpAsync` drains the
// NetworkStream.
//
// Concurrency (same rules as Networking.swift):
//   • Snapshot continuations UNDER the NSLock and finish() OUTSIDE it — finish()
//     runs onTermination synchronously and re-enters the same non-reentrant lock.
//   • The inbound stream is single-consumer with UNBOUNDED buffering, so a frame
//     read before receive() is iterated is retained, not lost (mirrors C#'s
//     unbounded inbound Channel<NetworkPayload>).

import Foundation

// ──────────────────────────────────────────────────────────────────────────
// TcpConnectionState (TcpTransportCommons.cs)
//
// Int-raw + Codable; ordinals follow the C# declaration order.
// ──────────────────────────────────────────────────────────────────────────

/// The connection lifecycle state of a TCP endpoint. Ordinals mirror the C#
/// `TcpConnectionState` declaration order.
public enum TcpConnectionState: Int, Codable, Sendable, CaseIterable {
    case disconnected = 0
    case connecting = 1
    case connected = 2
    case closing = 3
    case failed = 4
}

// ──────────────────────────────────────────────────────────────────────────
// TcpEndpointDescriptor / TcpThroughputSample (records)
// ──────────────────────────────────────────────────────────────────────────

/// Describes a TCP endpoint + socket options. Ported from the C#
/// `TcpEndpointDescriptor` record. `connectTimeout` is seconds (C#'s TimeSpan).
public struct TcpEndpointDescriptor: Sendable, Equatable, Codable {
    public let host: String
    public let port: Int
    public let noDelay: Bool
    public let keepAlive: Bool
    public let connectTimeout: TimeInterval

    public init(
        host: String,
        port: Int,
        noDelay: Bool,
        keepAlive: Bool,
        connectTimeout: TimeInterval
    ) {
        self.host = host
        self.port = port
        self.noDelay = noDelay
        self.keepAlive = keepAlive
        self.connectTimeout = connectTimeout
    }
}

/// A single TCP throughput measurement. Ported from the C#
/// `TcpThroughputSample` record.
public struct TcpThroughputSample: Sendable, Equatable, Codable {
    public let endpointId: String
    public let bytesSent: Int64
    public let bytesReceived: Int64
    public let atUtc: Date

    public init(
        endpointId: String,
        bytesSent: Int64,
        bytesReceived: Int64,
        atUtc: Date
    ) {
        self.endpointId = endpointId
        self.bytesSent = bytesSent
        self.bytesReceived = bytesReceived
        self.atUtc = atUtc
    }
}

// ──────────────────────────────────────────────────────────────────────────
// TcpKnownPorts (static constants)
// ──────────────────────────────────────────────────────────────────────────

/// Well-known TCP port constants. Ported from the C# static `TcpKnownPorts`
/// (values match exactly).
public enum TcpKnownPorts {
    public static let http = 80
    public static let https = 443
    public static let ssh = 22
    public static let smtp = 25
    public static let imap = 143
    public static let imapSsl = 993
    public static let pop3 = 110
    public static let pop3Ssl = 995
    public static let mqtt = 1883
    public static let mqttSsl = 8883
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryTcpConnectionRegistry (TcpTransportCommons.cs)
//
// C# uses two ConcurrentDictionaries (endpoints + states) and a lock-guarded List
// for throughput. Here a single NSLock guards all of it; default state is
// Disconnected and TotalBytesSent sums the per-endpoint samples exactly.
// ──────────────────────────────────────────────────────────────────────────

/// In-memory registry of TCP endpoints, connection states, and throughput
/// samples. Ported from the C# `InMemoryTcpConnectionRegistry`.
public final class InMemoryTcpConnectionRegistry: @unchecked Sendable {
    private let lock = NSLock()
    private var endpoints: [String: TcpEndpointDescriptor] = [:]
    private var states: [String: TcpConnectionState] = [:]
    private var throughput: [TcpThroughputSample] = []

    public init() {}

    /// Register (or replace) an endpoint descriptor keyed by `id`.
    public func register(_ id: String, _ d: TcpEndpointDescriptor) {
        lock.lock(); endpoints[id] = d; lock.unlock()
    }

    /// The endpoint descriptor for `id`, or nil (matches C#'s `GetValueOrDefault`).
    public func get(_ id: String) -> TcpEndpointDescriptor? {
        lock.lock(); defer { lock.unlock() }
        return endpoints[id]
    }

    /// Set the connection state for an endpoint.
    public func setState(_ id: String, _ s: TcpConnectionState) {
        lock.lock(); states[id] = s; lock.unlock()
    }

    /// The connection state for an endpoint, or `.disconnected` (matches C#'s
    /// default).
    public func state(_ id: String) -> TcpConnectionState {
        lock.lock(); defer { lock.unlock() }
        return states[id] ?? .disconnected
    }

    /// Record a throughput sample.
    public func recordSample(_ s: TcpThroughputSample) {
        lock.lock(); throughput.append(s); lock.unlock()
    }

    /// Total bytes sent across all samples for `id` (matches C#'s
    /// `Where(...).Sum(t => t.BytesSent)`).
    public func totalBytesSent(_ id: String) -> Int64 {
        lock.lock(); defer { lock.unlock() }
        return throughput.filter { $0.endpointId == id }.reduce(0) { $0 + $1.bytesSent }
    }
}

// ──────────────────────────────────────────────────────────────────────────
// TcpFraming (byte-exact port of the C# length-prefix wire format)
//
// C# SendAsync: write BitConverter.GetBytes(data.Length) (4 bytes) then data.
// On a little-endian host BitConverter writes the length little-endian; this
// helper does the same, so the framed bytes are identical.
// ──────────────────────────────────────────────────────────────────────────

/// The 4-byte-little-endian length-prefix framing the C# TCP transport uses on
/// the wire. Exposed so the framing is testable and byte-exact.
public enum TcpFraming {
    /// Frame `data` as a 4-byte little-endian length prefix followed by the bytes.
    /// Mirrors C#'s `BitConverter.GetBytes(data.Length)` + the data (little-endian
    /// host).
    public static func frame(_ data: Data) -> Data {
        let len = UInt32(truncatingIfNeeded: data.count)
        var out = Data(capacity: 4 + data.count)
        out.append(UInt8(truncatingIfNeeded: len & 0xFF))
        out.append(UInt8(truncatingIfNeeded: (len >> 8) & 0xFF))
        out.append(UInt8(truncatingIfNeeded: (len >> 16) & 0xFF))
        out.append(UInt8(truncatingIfNeeded: (len >> 24) & 0xFF))
        out.append(data)
        return out
    }

    /// Decode a little-endian Int32 length from the first 4 bytes of `prefix`.
    /// Mirrors C#'s `BitConverter.ToInt32(lenBuf)` (little-endian host).
    public static func decodeLength(_ prefix: Data) -> Int {
        precondition(prefix.count >= 4, "length prefix must be 4 bytes")
        let b = [UInt8](prefix.prefix(4))
        let v = UInt32(b[0]) | (UInt32(b[1]) << 8) | (UInt32(b[2]) << 16) | (UInt32(b[3]) << 24)
        return Int(Int32(bitPattern: v))
    }
}

// ──────────────────────────────────────────────────────────────────────────
// ITcpInboundWriter / ITcpStreamSocket (TcpNetworkTransport.cs)
//
// The injected socket seam (the Swift analogue of NetworkStream). The transport
// frames a payload and calls write(); the socket delivers received framed bytes
// back by pushing decoded payloads through the ITcpInboundWriter (the analogue
// of the C# PumpAsync loop feeding the inbound Channel). Keeping the framing at
// the transport boundary makes the 4-byte length-prefix format byte-exact and
// testable without a real NetworkStream.
// ──────────────────────────────────────────────────────────────────────────

/// The sink an `ITcpStreamSocket` uses to push a received (already de-framed)
/// payload into the transport's inbound stream. The Swift analogue of C#'s
/// `PumpAsync` writing into the inbound Channel.
public protocol ITcpInboundWriter: AnyObject, Sendable {
    /// Deliver a received payload into the transport's inbound stream. Returns
    /// false once the inbound stream has been completed (stopped).
    @discardableResult
    func push(_ payload: NetworkPayload) -> Bool
}

/// The injected TCP byte stream — the Swift analogue of C#'s `NetworkStream`.
/// Implement per platform (or in tests). `isConnected` backs the transport's
/// `isAvailable` (C#'s `_client?.Connected`).
public protocol ITcpStreamSocket: AnyObject {
    /// True when the underlying client is connected (C#'s `TcpClient.Connected`).
    var isConnected: Bool { get }

    /// Open the stream, retaining `inbound` so de-framed payloads read off the
    /// wire can be pushed into the transport (C#'s `ConnectAsync` + starting the
    /// pump).
    func open(inbound: ITcpInboundWriter) async throws

    /// Write already-framed bytes to the wire (C#'s two `WriteAsync` calls: the
    /// length prefix then the data). The transport hands the fully framed buffer.
    func write(_ framed: Data) async throws

    /// Close the stream / client / listener (C#'s `_stream/_client/_listener`
    /// close).
    func close() async throws
}

// ──────────────────────────────────────────────────────────────────────────
// TcpNetworkTransport (TcpNetworkTransport.cs)
// ──────────────────────────────────────────────────────────────────────────

/// `INetworkTransport` over raw TCP via an injected byte stream. `start` opens
/// the stream (marking the transport running); `send` frames the payload as a
/// 4-byte little-endian length prefix + data and writes it (throwing when not
/// connected, C#'s `InvalidOperationException("Not connected.")`); `receive`
/// drains de-framed payloads the socket pushes inbound; `stop` closes the stream
/// then completes the inbound stream. Framing is ported byte-for-byte from the
/// C# `TcpNetworkTransport`.
public final class TcpNetworkTransport: INetworkTransport, @unchecked Sendable {
    /// The inbound sink handed to the socket. Buffers frames pushed before
    /// `receive()` is iterated (unbounded) so none are lost.
    private final class InboundWriter: ITcpInboundWriter, @unchecked Sendable {
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

    private let socket: ITcpStreamSocket
    private let inbound = InboundWriter()

    private let lock = NSLock()
    private var opened = false

    /// - Parameter socket: the injected TCP byte stream (the socket seam). The
    ///   remote endpoint / listen port that the C# constructor takes belong to the
    ///   socket's construction in this port (the socket owns the address), so the
    ///   transport only depends on the stream contract.
    public init(socket: ITcpStreamSocket) {
        self.socket = socket
    }

    public var kind: TransportKind { .tcp }

    /// Mirrors C#'s `IsAvailable => _client?.Connected ?? false`.
    public var isAvailable: Bool { socket.isConnected }

    /// Open the stream and hand the socket the inbound writer (C#'s `ConnectAsync`
    /// + starting `PumpAsync`).
    public func start() async throws {
        try await socket.open(inbound: inbound)
        lock.lock(); opened = true; lock.unlock()
    }

    /// Close the stream, then complete the inbound stream (C#'s close order +
    /// `_inbound.Writer.TryComplete()`).
    public func stop() async throws {
        lock.lock(); opened = false; lock.unlock()
        try await socket.close()
        inbound.complete()
    }

    /// Frame the payload (4-byte little-endian length prefix + data) and write it.
    /// Throws `NetworkError.notConnected` when the transport was never opened,
    /// mirroring C#'s `InvalidOperationException("Not connected.")` on a nil
    /// stream.
    public func send(_ payload: NetworkPayload) async throws {
        lock.lock()
        let up = opened
        lock.unlock()
        guard up else { throw NetworkError.notConnected }
        let framed = TcpFraming.frame(payload.data)
        try await socket.write(framed)
    }

    /// Yields de-framed inbound payloads the socket pushed. Mirrors C#'s
    /// `_inbound.Reader.ReadAllAsync(ct)`.
    public func receive() -> AsyncStream<NetworkPayload> {
        inbound.stream()
    }
}
