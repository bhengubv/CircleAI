// NetworkingWiFi.swift
//
// Port of CircleAI.Networking.WiFi (the C# reference) — the LAN UDP network
// transport and its UDP-beacon peer discovery. Collapses the C# folder's two
// files (WiFiNetworkTransport.cs / WiFiPeerDiscovery.cs) into this single Swift
// file per the tree's flat convention.
//
// Ported types (1:1 with the C# under src/CircleAI.Networking.WiFi/):
//   Transport — WiFiNetworkTransport (INetworkTransport) + IUdpSocket
//   Discovery — WiFiPeerDiscovery (IPeerDiscovery) + IUdpBeaconSocket
//   Constants — DiscoveryPort (47890), DataPort (47891)
//
// Injected-socket note — the C# WiFiNetworkTransport wraps concrete UdpClients
// (real sockets): a sender and a receiver bound to DataPort with broadcast
// enabled. SendAsync unicasts to (destinationIp, DataPort) when the destination
// parses as an IPAddress, else broadcasts to (IPAddress.Broadcast, DataPort);
// PumpAsync receives datagrams into an unbounded inbound Channel.
// WiFiPeerDiscovery broadcasts/listens beacons on DiscoveryPort, parsing the
// `CIRCLEAI:BEACON:` magic. This port follows the task rule "inject the socket
// behind an interface; every contract gets a working deterministic
// implementation": the UDP datagram plane is injected behind IUdpSocket (data)
// and IUdpBeaconSocket (discovery). The unicast-vs-broadcast decision, the ports,
// and the beacon magic/format are ported byte-for-byte so the wire behaviour is
// identical.
//
// Concurrency (same rules as Networking.swift):
//   • Snapshot continuations UNDER the NSLock and finish() OUTSIDE it — finish()
//     runs onTermination synchronously and re-enters the same non-reentrant lock.
//   • The inbound stream is single-consumer with UNBOUNDED buffering, so a
//     datagram the socket pushes before receive() is iterated is retained, not
//     lost (mirrors C#'s unbounded inbound Channel<NetworkPayload>).

import Foundation

// ──────────────────────────────────────────────────────────────────────────
// UdpDestination (the target address a WiFi send resolves to)
//
// C#'s SendAsync branches on IPAddress.TryParse(destinationId): a parse success
// yields a unicast IPEndPoint(ip, DataPort); a failure yields the broadcast
// IPEndPoint(IPAddress.Broadcast, DataPort). This value type captures that
// decision so the wire target is explicit and testable.
// ──────────────────────────────────────────────────────────────────────────

/// The resolved UDP destination for a WiFi send: either a unicast host or the
/// LAN broadcast address, always on `WiFiNetworkTransport.dataPort`.
public struct UdpDestination: Sendable, Equatable {
    /// The dotted/colon host text, or `"255.255.255.255"` for broadcast.
    public let host: String
    public let port: Int
    /// True when this is the LAN broadcast destination.
    public let isBroadcast: Bool

    public init(host: String, port: Int, isBroadcast: Bool) {
        self.host = host
        self.port = port
        self.isBroadcast = isBroadcast
    }
}

// ──────────────────────────────────────────────────────────────────────────
// IPAddressParsing (deterministic IPAddress.TryParse analogue)
//
// The only behaviour the C# transport keys off IPAddress.TryParse is the
// yes/no branch (unicast if it parses, broadcast if not). This helper reproduces
// that decision deterministically: accept a dotted-quad IPv4 (four 0–255 octets)
// or a plausible IPv6 literal (hex groups with ':' and optional '::'), which
// covers what a destinationId would carry. It does not need to build an actual
// address — only to decide the branch exactly as .NET would for these inputs.
// ──────────────────────────────────────────────────────────────────────────

/// Deterministic analogue of .NET's `IPAddress.TryParse`, used only to decide
/// unicast-vs-broadcast. Returns true for a valid IPv4 dotted-quad or a plausible
/// IPv6 literal.
public enum IPAddressParsing {
    /// True when `s` is a valid IPv4 dotted-quad (four 0–255 octets) or an IPv6
    /// literal. Mirrors the branch decision C#'s `IPAddress.TryParse` makes for a
    /// destinationId.
    public static func isValid(_ s: String) -> Bool {
        isValidIPv4(s) || isValidIPv6(s)
    }

    static func isValidIPv4(_ s: String) -> Bool {
        let parts = s.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return false }
        for part in parts {
            // Each octet: 1–3 ASCII digits, value 0–255, no leading '+'/'-'/spaces.
            guard !part.isEmpty, part.count <= 3, part.allSatisfy({ $0.isASCII && $0.isNumber }) else {
                return false
            }
            guard let v = Int(part), v >= 0, v <= 255 else { return false }
        }
        return true
    }

    static func isValidIPv6(_ s: String) -> Bool {
        // Must contain a ':' to be IPv6; reject anything else here.
        guard s.contains(":") else { return false }
        // At most one "::" compression run.
        let doubleColonCount = countOccurrences(of: "::", in: s)
        if doubleColonCount > 1 { return false }
        // Split on ':' and validate each non-empty group is 1–4 hex digits.
        let groups = s.split(separator: ":", omittingEmptySubsequences: false)
        // A valid literal has at most 8 groups (fewer when "::" is present).
        guard groups.count <= 8 else { return false }
        var nonEmpty = 0
        for g in groups {
            if g.isEmpty { continue } // part of a "::" run
            guard g.count <= 4, g.allSatisfy({ $0.isHexDigit && $0.isASCII }) else { return false }
            nonEmpty += 1
        }
        // Need at least one real group, and if uncompressed, exactly 8.
        if doubleColonCount == 0 { return groups.count == 8 && nonEmpty == 8 }
        return nonEmpty >= 1
    }

    private static func countOccurrences(of needle: String, in haystack: String) -> Int {
        guard !needle.isEmpty else { return 0 }
        var count = 0
        var idx = haystack.startIndex
        while let r = haystack.range(of: needle, range: idx..<haystack.endIndex) {
            count += 1
            idx = r.upperBound
        }
        return count
    }
}

// ──────────────────────────────────────────────────────────────────────────
// IUdpInboundWriter / IUdpSocket (WiFiNetworkTransport.cs)
//
// The injected socket seam for the DATA plane (the Swift analogue of the sender
// + receiver UdpClients). On start the transport hands the socket an
// IUdpInboundWriter so received datagrams can be pushed inbound (the analogue of
// PumpAsync feeding the inbound Channel). SendAsync calls send() with the
// resolved UdpDestination.
// ──────────────────────────────────────────────────────────────────────────

/// The sink an `IUdpSocket` uses to push a received datagram payload into the
/// transport's inbound stream. The Swift analogue of C#'s `PumpAsync` writing the
/// received `result.Buffer` into the inbound Channel.
public protocol IUdpInboundWriter: AnyObject, Sendable {
    /// Deliver a received datagram payload into the transport's inbound stream.
    /// Returns false once the inbound stream has been completed (stopped).
    @discardableResult
    func push(_ payload: NetworkPayload) -> Bool
}

/// The injected UDP data socket — the Swift analogue of the C# sender/receiver
/// `UdpClient` pair. Implement per platform (or in tests). `isBound` backs the
/// transport's `isAvailable` (C#'s `_receiver is not null`).
public protocol IUdpSocket: AnyObject {
    /// True when the receiver is bound (C#'s `_receiver is not null`).
    var isBound: Bool { get }

    /// Bind the receiver on `DataPort` and enable the sender, retaining `inbound`
    /// so received datagrams can be pushed into the transport (C#'s two
    /// `UdpClient` constructions + starting the pump).
    func open(inbound: IUdpInboundWriter) async throws

    /// Send `data` to `destination` (C#'s `_sender.SendAsync(data, endpoint)`).
    func send(_ data: Data, to destination: UdpDestination) async throws

    /// Close both UDP clients (C#'s `_receiver/_sender` close).
    func close() async throws
}

// ──────────────────────────────────────────────────────────────────────────
// WiFiNetworkTransport (WiFiNetworkTransport.cs)
// ──────────────────────────────────────────────────────────────────────────

/// `INetworkTransport` using LAN UDP broadcast / unicast via an injected UDP
/// socket. `start` opens the socket; `send` unicasts to
/// `(destinationIp, dataPort)` when the destination parses as an IP address, else
/// broadcasts to `(255.255.255.255, dataPort)`; `receive` drains datagrams the
/// socket pushes inbound; `stop` closes the socket then completes the inbound
/// stream. Mirrors the C# `WiFiNetworkTransport` exactly.
public final class WiFiNetworkTransport: INetworkTransport, @unchecked Sendable {
    /// Discovery beacon port (C#'s `DiscoveryPort = 47890`).
    public static let discoveryPort = 47890
    /// Data plane port (C#'s `DataPort = 47891`).
    public static let dataPort = 47891
    /// The LAN broadcast address text (C#'s `IPAddress.Broadcast`).
    public static let broadcastAddress = "255.255.255.255"

    /// The inbound sink handed to the socket. Buffers datagrams pushed before
    /// `receive()` is iterated (unbounded) so none are lost.
    private final class InboundWriter: IUdpInboundWriter, @unchecked Sendable {
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

    private let socket: IUdpSocket
    private let inbound = InboundWriter()

    /// - Parameter socket: the injected UDP data socket (the socket seam).
    public init(socket: IUdpSocket) {
        self.socket = socket
    }

    public var kind: TransportKind { .wiFi }

    /// Mirrors C#'s `IsAvailable => _receiver is not null`.
    public var isAvailable: Bool { socket.isBound }

    /// Resolve the UDP destination for a payload exactly as C#'s `SendAsync`:
    /// unicast to `(destinationId, dataPort)` when the destination is a non-empty
    /// parseable IP address, else broadcast to `(255.255.255.255, dataPort)`.
    public static func resolveDestination(for payload: NetworkPayload) -> UdpDestination {
        if let dest = payload.destinationId, !dest.isEmpty, IPAddressParsing.isValid(dest) {
            return UdpDestination(host: dest, port: dataPort, isBroadcast: false)
        }
        return UdpDestination(host: broadcastAddress, port: dataPort, isBroadcast: true)
    }

    /// Open the socket and hand it the inbound writer (C#'s two `UdpClient`
    /// constructions + starting `PumpAsync`).
    public func start() async throws {
        try await socket.open(inbound: inbound)
    }

    /// Close the socket, then complete the inbound stream (C#'s close order +
    /// `_inbound.Writer.TryComplete()`).
    public func stop() async throws {
        try await socket.close()
        inbound.complete()
    }

    /// Send the payload to its resolved destination (unicast or broadcast).
    /// Mirrors C#'s `SendAsync` branch.
    public func send(_ payload: NetworkPayload) async throws {
        let destination = Self.resolveDestination(for: payload)
        try await socket.send(payload.data, to: destination)
    }

    /// Yields inbound datagram payloads the socket pushed. Mirrors C#'s
    /// `_inbound.Reader.ReadAllAsync(ct)`.
    public func receive() -> AsyncStream<NetworkPayload> {
        inbound.stream()
    }
}

// ──────────────────────────────────────────────────────────────────────────
// IUdpBeaconSocket / WiFiPeerDiscovery (WiFiPeerDiscovery.cs)
//
// The injected socket seam for the DISCOVERY plane (the Swift analogue of the
// UdpClient bound to DiscoveryPort). DiscoverAsync listens for datagrams,
// yielding a PeerInfo for each `CIRCLEAI:BEACON:{nodeId}` beacon; AnnounceAsync
// broadcasts `CIRCLEAI:BEACON:{nodeId}` to DiscoveryPort. The beacon magic + wire
// format are ported byte-for-byte.
// ──────────────────────────────────────────────────────────────────────────

/// A received discovery datagram: its UTF-8 message text and the sender's address
/// text. The Swift analogue of C#'s `UdpReceiveResult` (Buffer decoded +
/// RemoteEndPoint.Address).
public struct UdpBeaconDatagram: Sendable, Equatable {
    /// The datagram bytes decoded as UTF-8 (C#'s `Encoding.UTF8.GetString(Buffer)`).
    public let message: String
    /// The sender's address text (C#'s `RemoteEndPoint.Address`).
    public let senderAddress: String

    public init(message: String, senderAddress: String) {
        self.message = message
        self.senderAddress = senderAddress
    }
}

/// The injected UDP beacon socket — the Swift analogue of the C# `UdpClient`
/// bound to `DiscoveryPort` for beacons. Implement per platform (or in tests).
public protocol IUdpBeaconSocket: AnyObject {
    /// Yields received beacon datagrams (C#'s `udp.ReceiveAsync()` loop). The
    /// stream finishes when the socket is closed / cancelled (C#'s `yield break`
    /// on a receive failure).
    func receiveBeacons() -> AsyncStream<UdpBeaconDatagram>

    /// Broadcast `payload` to the LAN on `DiscoveryPort` (C#'s
    /// `udp.SendAsync(beacon, IPEndPoint(IPAddress.Broadcast, DiscoveryPort))`).
    func broadcast(_ payload: Data) async throws
}

/// Discovers nearby Circle AI devices on the same LAN via UDP broadcast beacons.
/// Ported from the C# `WiFiPeerDiscovery`. No Aether, no cloud, no infrastructure
/// required.
public final class WiFiPeerDiscovery: IPeerDiscovery, @unchecked Sendable {
    /// The beacon prefix (C#'s `BeaconMagic = "CIRCLEAI:BEACON:"`).
    public static let beaconMagic = "CIRCLEAI:BEACON:"

    private let socket: IUdpBeaconSocket

    /// - Parameter socket: the injected UDP beacon socket (the socket seam).
    public init(socket: IUdpBeaconSocket) {
        self.socket = socket
    }

    /// Yields a `PeerInfo` for each received `CIRCLEAI:BEACON:{nodeId}` datagram.
    /// The nodeId is the text after the magic; the display name is
    /// `"WiFi/{senderAddress}"`; the peer supports only `.wiFi`, role `.peer`, no
    /// signal strength, `lastSeen` = now. Ported from C#'s `DiscoverAsync`.
    public func discover() -> AsyncStream<PeerInfo> {
        let magic = Self.beaconMagic
        let beacons = socket.receiveBeacons()
        return AsyncStream(bufferingPolicy: .unbounded) { continuation in
            // Subscribe synchronously by capturing the already-created beacon
            // stream, then consume in the task — a beacon arriving right after
            // discover() returns is not lost (the beacon stream is buffered).
            let task = Task {
                for await datagram in beacons {
                    guard datagram.message.hasPrefix(magic) else { continue }
                    let nodeId = String(datagram.message.dropFirst(magic.count))
                    let peer = PeerInfo(
                        nodeId: nodeId,
                        displayName: "WiFi/\(datagram.senderAddress)",
                        supportedTransports: [.wiFi],
                        role: .peer,
                        signalStrengthDbm: nil,
                        lastSeen: Date())
                    continuation.yield(peer)
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    /// Broadcast this device's presence as `CIRCLEAI:BEACON:{nodeId}` on
    /// `DiscoveryPort`. Ported from C#'s `AnnounceAsync`.
    public func announce(localInfo: PeerInfo) async throws {
        let beacon = Data("\(Self.beaconMagic)\(localInfo.nodeId)".utf8)
        try await socket.broadcast(beacon)
    }
}
