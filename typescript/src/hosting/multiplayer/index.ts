// hosting/multiplayer/index.ts
//
// Port of CircleAI.Hosting.Multiplayer:
//   • Contracts.cs — IMultiplayerPeerIdentity, GuestPeerIdentity
//   • MultiplayerHub.cs — per-document collaboration hub: join/leave, live
//     cursors, LWW-by-rev edits, presence, colour-per-peer
//
// SignalR's Hub has no TS analogue, so the hub's transport (Clients.OthersInGroup
// broadcasts) is injected behind {@link IMultiplayerBroadcaster}. Connection
// lifecycle (OnConnected/OnDisconnected) and the SignalR-static shared state
// (RevByDoc / PeerByConn) are modelled as hub-instance state. The deterministic
// bits — ColourFor's stable hash, SendEdit's rev arbitration, Peers/CurrentRev
// — are ported verbatim.

import { randomUUID } from "node:crypto";

// ─────────────────────────────────────────────────────────────────────────────
// Peer identity contracts
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Resolves the human-visible identity of the peer making a hub call. Mirrors
 * CircleAI.Hosting.Multiplayer.IMultiplayerPeerIdentity.
 */
export interface IMultiplayerPeerIdentity {
  /** Stable id (used to derive a colour). */
  readonly peerId: string;
  /** Human-readable display name. */
  readonly displayName: string;
}

/** Anonymous guest identity. Mirrors GuestPeerIdentity. */
export class GuestPeerIdentity implements IMultiplayerPeerIdentity {
  readonly peerId: string;
  readonly displayName: string;

  constructor(peerId?: string | null, displayName?: string | null) {
    this.peerId = peerId ?? randomUUID().replace(/-/g, "");
    this.displayName = displayName ?? "Guest";
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// PeerState
// ─────────────────────────────────────────────────────────────────────────────

/** Snapshot of one connected peer. Mirrors MultiplayerHub.PeerState. */
export interface PeerState {
  readonly connectionId: string;
  readonly displayName: string;
  readonly color: string;
  readonly docId: string | null;
}

interface DocRevState {
  readonly rev: number;
  readonly updatedAt: number;
}

// ─────────────────────────────────────────────────────────────────────────────
// Broadcaster seam (SignalR Clients.OthersInGroup)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Outbound event sink. `sendToOthersInGroup` mirrors
 * `Clients.OthersInGroup(group).SendAsync(event, ...args)` — the sender's own
 * connection is excluded by the hub before calling.
 */
export interface IMultiplayerBroadcaster {
  sendToOthersInGroup(
    group: string,
    fromConnectionId: string,
    event: string,
    args: readonly unknown[],
  ): Promise<void>;
}

/** Recording broadcaster for tests — captures every outbound event. */
export class RecordingBroadcaster implements IMultiplayerBroadcaster {
  readonly events: {
    group: string;
    from: string;
    event: string;
    args: readonly unknown[];
  }[] = [];

  async sendToOthersInGroup(
    group: string,
    fromConnectionId: string,
    event: string,
    args: readonly unknown[],
  ): Promise<void> {
    this.events.push({ group, from: fromConnectionId, event, args });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// MultiplayerHub
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Multiplayer collaboration hub. Mirrors
 * CircleAI.Hosting.Multiplayer.MultiplayerHub. Connections are driven
 * explicitly (onConnected/onDisconnected + a connectionId per call) since there
 * is no ambient SignalR context in TS. Shared per-document state matches the
 * C# static dictionaries.
 */
export class MultiplayerHub {
  private readonly broadcaster: IMultiplayerBroadcaster;

  // C# statics: shared across all connections of one hub host.
  private readonly revByDoc = new Map<string, DocRevState>();
  private readonly peerByConn = new Map<string, PeerState>();

  constructor(broadcaster: IMultiplayerBroadcaster) {
    if (!broadcaster) throw new Error("broadcaster required");
    this.broadcaster = broadcaster;
  }

  /** Mirrors OnConnectedAsync — registers a peer keyed by connectionId. */
  async onConnected(connectionId: string, identity: IMultiplayerPeerIdentity): Promise<void> {
    if (!identity) throw new Error("identity required");
    this.peerByConn.set(connectionId, {
      connectionId,
      displayName: identity.displayName,
      color: MultiplayerHub.colourFor(identity.peerId),
      docId: null,
    });
  }

  /** Mirrors OnDisconnectedAsync — removes the peer and notifies its doc group. */
  async onDisconnected(connectionId: string): Promise<void> {
    const peer = this.peerByConn.get(connectionId);
    if (peer !== undefined) {
      this.peerByConn.delete(connectionId);
      if (peer.docId != null && peer.docId.length > 0) {
        await this.broadcaster.sendToOthersInGroup(
          docGroup(peer.docId),
          connectionId,
          "PeerLeft",
          [peer.docId, peer.connectionId, peer.displayName],
        );
      }
    }
  }

  async joinDocument(connectionId: string, docId: string): Promise<void> {
    if (docId == null || docId.trim().length === 0) return;
    const peer = this.peerByConn.get(connectionId);
    if (peer !== undefined) {
      this.peerByConn.set(connectionId, { ...peer, docId });
      await this.broadcaster.sendToOthersInGroup(docGroup(docId), connectionId, "PeerJoined", [
        docId,
        peer.connectionId,
        peer.displayName,
        peer.color,
      ]);
    }
  }

  async leaveDocument(connectionId: string, docId: string): Promise<void> {
    if (docId == null || docId.trim().length === 0) return;
    const peer = this.peerByConn.get(connectionId);
    if (peer !== undefined) {
      this.peerByConn.set(connectionId, { ...peer, docId: null });
      await this.broadcaster.sendToOthersInGroup(docGroup(docId), connectionId, "PeerLeft", [
        docId,
        peer.connectionId,
        peer.displayName,
      ]);
    }
  }

  async sendCursor(
    connectionId: string,
    docId: string,
    line: number,
    ch: number,
  ): Promise<void> {
    const peer = this.peerByConn.get(connectionId);
    if (peer === undefined) return;
    await this.broadcaster.sendToOthersInGroup(docGroup(docId), connectionId, "CursorChanged", [
      peer.connectionId,
      peer.displayName,
      peer.color,
      line,
      ch,
    ]);
  }

  /**
   * Apply an edit if its rev is greater than the server's current rev. Returns
   * the new rev (or the server's current rev if the client's was stale).
   * Mirrors SendEdit's AddOrUpdate arbitration.
   */
  async sendEdit(
    connectionId: string,
    docId: string,
    content: string,
    rev: number,
  ): Promise<number> {
    const prev = this.revByDoc.get(docId);
    let newRev: DocRevState;
    if (prev === undefined) {
      newRev = { rev: Math.max(rev, 1), updatedAt: Date.now() };
    } else if (rev <= prev.rev) {
      newRev = prev;
    } else {
      newRev = { rev, updatedAt: Date.now() };
    }
    this.revByDoc.set(docId, newRev);

    if (newRev.rev !== rev) {
      // Rejected — client gets current rev back and can rebase.
      return newRev.rev;
    }

    await this.broadcaster.sendToOthersInGroup(docGroup(docId), connectionId, "EditApplied", [
      docId,
      content,
      rev,
      connectionId,
    ]);
    return rev;
  }

  /** Snapshot of who is currently in a document. Mirrors Peers. */
  peers(docId: string): readonly PeerState[] {
    return [...this.peerByConn.values()].filter((p) => p.docId === docId);
  }

  /** Current server-known rev for a document (0 if never touched). Mirrors CurrentRev. */
  currentRev(docId: string): number {
    return this.revByDoc.get(docId)?.rev ?? 0;
  }

  /** Test/admin hook — wipes state. Mirrors ResetStateForTesting. */
  resetStateForTesting(): void {
    this.revByDoc.clear();
    this.peerByConn.clear();
  }

  /**
   * Stable hash → HSL hue so each peer lands on a different cursor colour.
   * Mirrors MultiplayerHub.ColourFor (int32 wraparound hash).
   */
  static colourFor(peerId: string): string {
    if (peerId == null || peerId.length === 0) return "#5a4fcf";
    let h = 0;
    for (let i = 0; i < peerId.length; i++) {
      // Emulate C# `unchecked` int32 arithmetic: h = h * 31 + c.
      h = Math.imul(h, 31) + peerId.charCodeAt(i);
      h |= 0; // wrap to int32
    }
    const hue = ((h % 360) + 360) % 360;
    return `hsl(${hue}, 70%, 55%)`;
  }
}

function docGroup(docId: string): string {
  return `doc:${docId}`;
}
