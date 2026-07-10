// HostingMultiplayer.swift
//
// Port of the CircleAI.Hosting.Multiplayer surface:
//   - Contracts.cs        → IMultiplayerPeerIdentity, GuestPeerIdentity
//   - MultiplayerHub.cs   → PeerState, MultiplayerHub (per-document group,
//                           LWW-by-rev edits, live cursors, presence)
//
// SignalR has no portable Swift analogue, so the hub is expressed as an
// in-memory session model: `connect`/`disconnect`/`join`/`leave`/`sendCursor`/
// `sendEdit` return the outgoing events that SignalR would broadcast, and the
// LWW rev logic + colour hashing + presence bookkeeping are ported faithfully.

import Foundation

// =====================================================================
// Peer identity
// =====================================================================

/// Resolves the human-visible identity of a peer making a hub call. Ported from
/// `IMultiplayerPeerIdentity`.
public protocol IMultiplayerPeerIdentity: Sendable {
    /// Stable id (used to derive a colour).
    var peerId: String { get }
    /// Human-readable display name.
    var displayName: String { get }
}

/// Anonymous guest identity. Ported from `GuestPeerIdentity`.
public struct GuestPeerIdentity: IMultiplayerPeerIdentity {
    public let peerId: String
    public let displayName: String

    public init(peerId: String? = nil, displayName: String? = nil) {
        self.peerId = peerId ?? UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        self.displayName = displayName ?? "Guest"
    }
}

/// Snapshot of who is currently in a document. Ported from
/// `MultiplayerHub.PeerState`.
public struct PeerState: Sendable, Equatable {
    public let connectionId: String
    public let displayName: String
    public let color: String
    public let docId: String?

    public init(connectionId: String, displayName: String, color: String, docId: String?) {
        self.connectionId = connectionId
        self.displayName = displayName
        self.color = color
        self.docId = docId
    }
}

// =====================================================================
// Outgoing events (what SignalR would broadcast to OthersInGroup)
// =====================================================================

/// One broadcast the hub emits to peers in a document group. Mirrors the C#
/// `Clients.OthersInGroup(...).SendAsync(event, args...)` calls.
public enum MultiplayerEvent: Sendable, Equatable {
    /// `PeerJoined(docId, connectionId, displayName, color)`.
    case peerJoined(docId: String, connectionId: String, displayName: String, color: String)
    /// `PeerLeft(docId, connectionId, displayName)`.
    case peerLeft(docId: String, connectionId: String, displayName: String)
    /// `CursorChanged(connectionId, displayName, color, line, ch)`.
    case cursorChanged(connectionId: String, displayName: String, color: String, line: Int, ch: Int)
    /// `EditApplied(docId, content, rev, fromConnectionId)`.
    case editApplied(docId: String, content: String, rev: Int64, fromConnectionId: String)
}

// =====================================================================
// MultiplayerHub
// =====================================================================

/// In-memory multiplayer collaboration hub. Per-document groups, last-write-wins
/// edits keyed by rev, live cursors, presence. Ported from `MultiplayerHub`.
///
/// Unlike the SignalR original (static dictionaries shared process-wide), this
/// port keeps state per-hub-instance so tests are isolated; `resetStateForTesting`
/// is retained for parity. Methods return the events a peer's neighbours would
/// receive so callers can wire them to whatever transport they use.
public final class MultiplayerHub: @unchecked Sendable {
    private struct DocRevState { var rev: Int64; var updatedAt: Date }

    private let lock = NSLock()
    private var revByDoc: [String: DocRevState] = [:]
    private var peerByConn: [String: PeerState] = [:]

    public init() {}

    /// Register a new connection. Mirrors `OnConnectedAsync`.
    public func connect(connectionId: String, identity: IMultiplayerPeerIdentity) {
        let peer = PeerState(
            connectionId: connectionId,
            displayName: identity.displayName,
            color: Self.colourFor(identity.peerId),
            docId: nil)
        lock.lock(); peerByConn[connectionId] = peer; lock.unlock()
    }

    /// Drop a connection; returns the `peerLeft` event to broadcast (if the peer
    /// was in a document). Mirrors `OnDisconnectedAsync`.
    @discardableResult
    public func disconnect(connectionId: String) -> MultiplayerEvent? {
        lock.lock()
        let peer = peerByConn.removeValue(forKey: connectionId)
        lock.unlock()
        if let peer = peer, let doc = peer.docId, !doc.isEmpty {
            return .peerLeft(docId: doc, connectionId: peer.connectionId, displayName: peer.displayName)
        }
        return nil
    }

    /// Join a document group; returns the `peerJoined` event. Mirrors
    /// `JoinDocument`.
    @discardableResult
    public func joinDocument(connectionId: String, docId: String) -> MultiplayerEvent? {
        if docId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return nil }
        lock.lock()
        guard var peer = peerByConn[connectionId] else { lock.unlock(); return nil }
        peer = PeerState(connectionId: peer.connectionId, displayName: peer.displayName,
                         color: peer.color, docId: docId)
        peerByConn[connectionId] = peer
        lock.unlock()
        return .peerJoined(docId: docId, connectionId: peer.connectionId,
                           displayName: peer.displayName, color: peer.color)
    }

    /// Leave a document group; returns the `peerLeft` event. Mirrors
    /// `LeaveDocument`.
    @discardableResult
    public func leaveDocument(connectionId: String, docId: String) -> MultiplayerEvent? {
        if docId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return nil }
        lock.lock()
        guard var peer = peerByConn[connectionId] else { lock.unlock(); return nil }
        let dn = peer.displayName; let cid = peer.connectionId
        peer = PeerState(connectionId: cid, displayName: dn, color: peer.color, docId: nil)
        peerByConn[connectionId] = peer
        lock.unlock()
        return .peerLeft(docId: docId, connectionId: cid, displayName: dn)
    }

    /// Broadcast a cursor position; returns the `cursorChanged` event. Mirrors
    /// `SendCursor`.
    @discardableResult
    public func sendCursor(connectionId: String, docId: String, line: Int, ch: Int) -> MultiplayerEvent? {
        lock.lock(); let peer = peerByConn[connectionId]; lock.unlock()
        guard let peer = peer else { return nil }
        return .cursorChanged(connectionId: peer.connectionId, displayName: peer.displayName,
                              color: peer.color, line: line, ch: ch)
    }

    /// Result of a `sendEdit` call: the accepted rev, and — when the edit was
    /// applied — the `editApplied` event to broadcast.
    public struct EditResult: Sendable, Equatable {
        public let acceptedRev: Int64
        public let event: MultiplayerEvent?
    }

    /// Apply an edit iff its rev is greater than the server's current rev.
    /// Returns the new rev (or the server's current rev if the client's rev was
    /// stale) plus the broadcast event when applied. Mirrors `SendEdit`.
    @discardableResult
    public func sendEdit(connectionId: String, docId: String, content: String, rev: Int64) -> EditResult {
        lock.lock()
        let newRev: DocRevState
        if let prev = revByDoc[docId] {
            if rev <= prev.rev {
                newRev = prev
            } else {
                newRev = DocRevState(rev: rev, updatedAt: Date())
            }
        } else {
            newRev = DocRevState(rev: max(rev, 1), updatedAt: Date())
        }
        revByDoc[docId] = newRev
        lock.unlock()

        if newRev.rev != rev {
            // Rejected — client gets current rev back and can rebase.
            return EditResult(acceptedRev: newRev.rev, event: nil)
        }
        return EditResult(acceptedRev: rev,
                          event: .editApplied(docId: docId, content: content, rev: rev, fromConnectionId: connectionId))
    }

    /// Snapshot of who is currently in a document. Mirrors `Peers`.
    public func peers(docId: String) -> [PeerState] {
        lock.lock(); let all = Array(peerByConn.values); lock.unlock()
        return all.filter { $0.docId == docId }
    }

    /// Current server-known rev for a document (0 if never touched). Mirrors
    /// `CurrentRev`.
    public func currentRev(docId: String) -> Int64 {
        lock.lock(); defer { lock.unlock() }
        return revByDoc[docId]?.rev ?? 0
    }

    /// Wipes state. Mirrors `ResetStateForTesting`.
    public func resetStateForTesting() {
        lock.lock(); revByDoc.removeAll(); peerByConn.removeAll(); lock.unlock()
    }

    /// Stable hash → HSL hue so each peer lands on a distinct cursor colour.
    /// Ported from `ColourFor` (unchecked 32-bit `h = h*31 + c` accumulation).
    static func colourFor(_ peerId: String) -> String {
        if peerId.isEmpty { return "#5a4fcf" }
        // Match C# `foreach (var c in peerId) h = h*31 + c` over UTF-16 code
        // units with 32-bit wrap-around (`unchecked`).
        var h: Int32 = 0
        for unit in peerId.utf16 {
            h = h &* 31 &+ Int32(unit)
        }
        let hue = ((Int(h) % 360) + 360) % 360
        return "hsl(\(hue), 70%, 55%)"
    }
}
