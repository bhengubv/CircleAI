// agents/peer/contracts.ts
//
// PeerAgent + AgentCapability identity records and the IAgentPeerProtocol
// contract — ports of CircleAI.Agents.Peer.PeerAgent / AgentCapability /
// IAgentPeerProtocol. AgentMessage / AgentMessageKind live in ./index.ts
// (the pre-existing barrel entry) and are imported from there.
//
// Type mappings (C# → TS):
//   sealed record              → readonly interface (+ positional factory)
//   Guid                       → string (UUID) — see message.ts for the
//                                .NET-layout 16-byte round-trip used by the
//                                Invoke↔Response correlation prefix.
//   decimal                    → number
//   byte[]                     → Uint8Array
//   DateTimeOffset             → Date (UTC instant)
//   IReadOnlyList<T>           → readonly T[]
//   CancellationToken          → AbortSignal (optional)
//   IAsyncEnumerable<T>        → AsyncIterable<T>

import type { AgentCapability, PeerAgent } from "./peer_agent.js";
import type { AgentMessage } from "./index.js";

/**
 * Agent-to-agent protocol over the Aether mesh. Mirrors
 * `CircleAI.Agents.Peer.IAgentPeerProtocol`. Every method MUST be safe to call
 * from any thread / concurrently.
 */
export interface IAgentPeerProtocol {
  /**
   * Listens for {@link AgentMessage} `Discover` broadcasts and any
   * already-registered peers for a short discovery window, returning every peer
   * observed.
   */
  discoverPeersAsync(signal?: AbortSignal): Promise<readonly PeerAgent[]>;

  /**
   * Initiates a handshake with `targetUhid`. Returns the peer's identity record
   * on a successful greet, or `null` if the peer is unreachable.
   */
  greetAsync(targetUhid: string, signal?: AbortSignal): Promise<PeerAgent | null>;

  /** Queries `targetUhid` for the capabilities it currently advertises. */
  queryCapabilitiesAsync(
    targetUhid: string,
    signal?: AbortSignal,
  ): Promise<readonly AgentCapability[]>;

  /**
   * Invokes `capability` on `targetUhid` with `requestPayload`. Awaits a single
   * `Response` envelope.
   *
   * @throws AgentInvocationException when the peer returns a `Decline` or when
   *   invocation otherwise fails / times out.
   */
  invokeAsync(
    targetUhid: string,
    capability: AgentCapability,
    requestPayload: Uint8Array,
    signal?: AbortSignal,
  ): Promise<AgentMessage>;

  /**
   * Streams every inbound {@link AgentMessage} addressed to this agent
   * (including broadcasts where `toUhid` is `"*"`). The sequence terminates when
   * `signal` is aborted.
   */
  streamInboxAsync(signal?: AbortSignal): AsyncIterable<AgentMessage>;
}
