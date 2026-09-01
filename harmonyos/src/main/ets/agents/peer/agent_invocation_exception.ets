/**
 * agents/peer/agent_invocation_exception
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// agents/peer/agent_invocation_exception.ts
//
// Raised by IAgentPeerProtocol.invokeAsync when a peer Declines the invocation
// or otherwise fails to return a Response envelope. Port of
// CircleAI.Agents.Peer.AgentInvocationException.

import type { AgentMessage } from "./index";

/**
 * Thrown when a peer declines an `Invoke` or returns an error response. Mirrors
 * `CircleAI.Agents.Peer.AgentInvocationException`.
 */
export class AgentInvocationException extends Error {
  /** The peer that declined or errored, if known. */
  readonly peerUhid?: string;

  /** The decline envelope returned by the peer, if any. */
  readonly declineMessage?: AgentMessage;

  constructor(
    message: string,
    peerUhid?: string,
    declineMessage?: AgentMessage,
  ) {
    super(message);
    this.name = "AgentInvocationException";
    this.peerUhid = peerUhid;
    this.declineMessage = declineMessage;
  }
}
