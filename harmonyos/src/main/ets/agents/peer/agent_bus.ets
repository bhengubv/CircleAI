/**
 * agents/peer/agent_bus
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// agents/peer/agent_bus.ts
//
// In-process coordinator that lets several InMemoryAgentPeerProtocol instances
// behave like devices on a mesh, for tests and samples. Port of
// CircleAI.Agents.Peer.AgentBus.
//
// AgentBus owns the peer registry and an unbounded channel per registered peer
// (reusing companion/herjarvis AsyncQueue as the Channel.CreateUnbounded
// analogue). Send routes a message to the right channel (or fans out on
// broadcast). Receive yields envelopes as they arrive.
//
// AgentBus is NOT a production transport. It exists so the protocol contract
// can be exercised without a real Aether router on the wire.

import { AsyncQueue } from "../../companion/herjarvis/async_queue";
import type { AgentMessage } from "./index";
import type { PeerAgent } from "./peer_agent";

/**
 * In-process bus used to simulate a mesh of CircleAI peers for tests and
 * samples. Not a production transport. Mirrors `CircleAI.Agents.Peer.AgentBus`.
 */
export class AgentBus {
  // Ordinal string keys — Map preserves insertion order and uses SameValueZero
  // key equality, matching the C# ConcurrentDictionary<string,...>(Ordinal).
  private readonly peers = new Map<string, PeerAgent>();
  private readonly inboxes = new Map<string, AsyncQueue<AgentMessage>>();

  /** Snapshot of every peer currently registered on the bus. */
  get registeredPeers(): readonly PeerAgent[] {
    return [...this.peers.values()];
  }

  /**
   * Registers `peer` on the bus. A subsequent {@link send} targeted at the
   * peer's UHID will deliver to its inbox. Re-registering with the same UHID
   * replaces the prior record.
   */
  register(peer: PeerAgent): void {
    if (peer === null || peer === undefined) throw new Error("peer is required.");
    this.peers.set(peer.uhidIdentityId, peer);
    if (!this.inboxes.has(peer.uhidIdentityId)) {
      this.inboxes.set(peer.uhidIdentityId, new AsyncQueue<AgentMessage>());
    }
  }

  /**
   * Removes `uhid` from the bus and completes its inbox so any active
   * {@link receive} enumerator terminates cleanly.
   */
  unregister(uhid: string): void {
    requireNonBlank(uhid, "uhid");
    this.peers.delete(uhid);
    const channel = this.inboxes.get(uhid);
    if (channel !== undefined) {
      channel.complete();
      this.inboxes.delete(uhid);
    }
  }

  /** Tries to read the latest record for `uhid`. Returns `null` when absent. */
  tryGetPeer(uhid: string): PeerAgent | null {
    requireNonBlank(uhid, "uhid");
    return this.peers.get(uhid) ?? null;
  }

  /**
   * Routes `message` to its recipient(s). When `toUhid` is `"*"` the envelope is
   * delivered to every registered inbox except the sender's own. Messages for an
   * unknown UHID are dropped silently — the simulated peer is considered offline.
   */
  send(message: AgentMessage): void {
    if (message === null || message === undefined) throw new Error("message is required.");
    if (message.toUhid === "*") {
      for (const [key, channel] of this.inboxes) {
        if (key === message.fromUhid) continue;
        channel.enqueue(message);
      }
      return;
    }

    const inbox = this.inboxes.get(message.toUhid);
    if (inbox !== undefined) {
      inbox.enqueue(message);
    }
  }

  /**
   * Streams every envelope delivered to `uhid`'s inbox. The sequence terminates
   * when the inbox is completed (via {@link unregister}) or when `signal` fires.
   */
  async *receive(uhid: string, signal?: AbortSignal): AsyncIterable<AgentMessage> {
    requireNonBlank(uhid, "uhid");
    let inbox = this.inboxes.get(uhid);
    if (inbox === undefined) {
      inbox = new AsyncQueue<AgentMessage>();
      this.inboxes.set(uhid, inbox);
    }
    yield* inbox.drain(signal);
  }
}

function requireNonBlank(value: string, name: string): void {
  if (value === null || value === undefined || value.trim().length === 0) {
    throw new Error(`${name} must not be null or whitespace.`);
  }
}
