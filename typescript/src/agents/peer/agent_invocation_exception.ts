// agents/peer/agent_invocation_exception.ts
//
// Raised by IAgentPeerProtocol.invokeAsync when a peer Declines the invocation
// or otherwise fails to return a Response envelope. Port of
// CircleAI.Agents.Peer.AgentInvocationException.

import type { AgentMessage } from "./index.js";

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
