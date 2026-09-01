/**
 * agents/peer/peer_agent
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// agents/peer/peer_agent.ts
//
// Identity records for a remote agent reachable over the Aether peer mesh —
// ports of CircleAI.Agents.Peer.PeerAgent + AgentCapability. PeerAgent
// describes WHO another CircleAI is and HOW to reach them; it does not own the
// connection (connections live on the protocol implementation).

import { randomUUID } from "../../platform";

/**
 * A capability advertised by a {@link PeerAgent}. Mirrors
 * `CircleAI.Agents.Peer.AgentCapability`.
 */
export interface AgentCapability {
  /**
   * Canonical capability name — e.g. `"translate"`, `"summarise"`,
   * `"navigate"`, `"diagnose"`.
   */
  readonly name: string;
  /** Semantic version of the capability contract. */
  readonly version: string;
  /** Cost in {@link costCurrency}. `0` means free. (C# `decimal` → number.) */
  readonly costPerInvocation: number;
  /**
   * Currency code. Defaults to `"SDPKT"` within the CircleAI ecosystem; other
   * codes are allowed for interoperability with external agents.
   */
  readonly costCurrency: string;
}

/** Constructs an {@link AgentCapability}. */
export function agentCapability(
  name: string,
  version: string,
  costPerInvocation: number,
  costCurrency: string,
): AgentCapability {
  return { name, version, costPerInvocation, costCurrency };
}

/**
 * A peer Circle AI agent discoverable over the Aether mesh. Mirrors
 * `CircleAI.Agents.Peer.PeerAgent`.
 */
export interface PeerAgent {
  /** Local handle for this peer (stable per discovery session). C# `Guid`. */
  readonly id: string;
  /**
   * Hashed UHID identity reference — never raw PII. Used as the routing key in
   * `AgentMessage.toUhid`.
   */
  readonly uhidIdentityId: string;
  /** User-chosen display label (e.g. "Sipho's Circle"). */
  readonly displayName: string;
  /** Capabilities this peer advertises. */
  readonly capabilities: readonly AgentCapability[];
  /** DER-encoded P-256 public key from the peer's UhidKeyRing. */
  readonly publicKeyDer: Uint8Array;
  /**
   * Transport currently carrying this peer — `"aether"`, `"wifi-direct"`,
   * `"ble"`, `"https-relay"`, or `null` when the peer is offline.
   */
  readonly currentTransportId: string | null;
  /** UTC timestamp of the last message or heartbeat from this peer. */
  readonly lastSeenAt: Date;
}

/** Constructs a {@link PeerAgent}. */
export function peerAgent(
  id: string,
  uhidIdentityId: string,
  displayName: string,
  capabilities: readonly AgentCapability[],
  publicKeyDer: Uint8Array,
  currentTransportId: string | null,
  lastSeenAt: Date,
): PeerAgent {
  return {
    id,
    uhidIdentityId,
    displayName,
    capabilities,
    publicKeyDer,
    currentTransportId,
    lastSeenAt,
  };
}

/**
 * Returns a copy of `peer` with `lastSeenAt` replaced — the analogue of the C#
 * `peer with { LastSeenAt = ... }` record `with`-expression used by the
 * in-memory protocol's `WithLastSeen`.
 */
export function withLastSeen(peer: PeerAgent, lastSeenAt: Date): PeerAgent {
  return { ...peer, lastSeenAt };
}

/** Mints a fresh handle id for a newly-registered peer (C# `Guid.NewGuid()`). */
export function newPeerHandle(): string {
  return randomUUID();
}
