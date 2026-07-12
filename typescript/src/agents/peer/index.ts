// agents/peer/index.ts
//
// AgentMessage with correlation_id — port of CircleAI.Agents.Peer.AgentMessage.

import { randomUUID } from "node:crypto";

export enum AgentMessageKind {
  Discover = 0,
  Greet = 1,
  CapabilityQuery = 2,
  Invoke = 3,
  Response = 4,
  Decline = 5,
  Heartbeat = 6,
}

export interface AgentMessage {
  readonly id: string;
  readonly kind: AgentMessageKind;
  readonly fromUhid: string;
  readonly toUhid: string;
  readonly contentType: string;
  readonly payload: Uint8Array;
  readonly signature: Uint8Array;
  readonly sentAt: string; // ISO 8601
  readonly correlationId: string;
}

export interface CreateAgentMessageInput {
  readonly kind: AgentMessageKind;
  readonly fromUhid: string;
  readonly toUhid: string;
  readonly contentType: string;
  readonly payload: Uint8Array;
  readonly signature: Uint8Array;
  readonly correlationId?: string;
}

/**
 * Create a new envelope. When `correlationId` is omitted, a 32-char
 * UUID hex is synthesised so every outbound envelope carries SOME
 * trace anchor — distributed traces always stitch.
 */
export function createAgentMessage(
  input: CreateAgentMessageInput,
): AgentMessage {
  return {
    id: randomUUID(),
    kind: input.kind,
    fromUhid: input.fromUhid,
    toUhid: input.toUhid,
    contentType: input.contentType,
    payload: input.payload,
    signature: input.signature,
    sentAt: new Date().toISOString(),
    correlationId: input.correlationId ?? randomUUID().replace(/-/g, ""),
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// CircleAI.Agents.Peer — the agent-to-agent Aether-mesh protocol. Identity
// records (PeerAgent / AgentCapability), the IAgentPeerProtocol contract, the
// AgentInvocationException, the in-process AgentBus transport, and the
// InMemoryAgentPeerProtocol reference implementation (discovery window, invoke
// timeout, payload-prefix correlation, and the background inbox pump). Faithful
// ports of the CircleAI.Agents.Peer C# project (AgentMessage above is the
// pre-existing entry).
// ─────────────────────────────────────────────────────────────────────────────

export {
  agentCapability,
  peerAgent,
  withLastSeen,
  newPeerHandle,
  type AgentCapability,
  type PeerAgent,
} from "./peer_agent.js";
export type { IAgentPeerProtocol } from "./contracts.js";
export { AgentInvocationException } from "./agent_invocation_exception.js";
export { guidToBytes, bytesToGuid } from "./guid_bytes.js";
export { AgentBus } from "./agent_bus.js";
export {
  InMemoryAgentPeerProtocol,
  type InMemoryAgentPeerProtocolOptions,
  type AgentPayloadSigner,
  type AgentCapabilityHandler,
} from "./in_memory_agent_peer_protocol.js";
