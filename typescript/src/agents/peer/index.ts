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
