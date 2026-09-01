/**
 * agents/peer/in_memory_agent_peer_protocol
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// agents/peer/in_memory_agent_peer_protocol.ts
//
// Reference implementation of IAgentPeerProtocol that uses an in-process
// AgentBus as its transport. Multiple instances sharing one bus simulate a
// small mesh of CircleAI devices. Port of
// CircleAI.Agents.Peer.InMemoryAgentPeerProtocol.
//
// Real implementations (BLE, Wi-Fi Direct, Aether router) live elsewhere and
// follow the same contract.
//
// Async mappings (C# → TS):
//   TaskCompletionSource<T>          → a Deferred<T> (resolve/reject captured)
//   CancellationTokenSource.CancelAfter + linked token → setTimeout + AbortController
//   Task.Run(PumpInboxAsync)         → a detached async loop over an AbortController
//   IDisposable.Dispose              → dispose() (abort pump, unregister, complete inbox)

import { AsyncQueue } from "../../companion/herjarvis/async_queue";
import { AgentBus } from "./agent_bus";
import { AgentInvocationException } from "./agent_invocation_exception";
import { bytesToGuid, guidToBytes } from "./guid_bytes";
import type { IAgentPeerProtocol } from "./contracts";
import {
  peerAgent,
  withLastSeen,
  newPeerHandle,
  type AgentCapability,
  type PeerAgent,
} from "./peer_agent";
import { AgentMessageKind, createAgentMessage, type AgentMessage } from "./index";

const DEFAULT_DISCOVERY_WINDOW_MS = 50;
const DEFAULT_INVOKE_TIMEOUT_MS = 5_000;

/** A signer over an outbound payload (wired to the UhidKeyRing in production). */
export type AgentPayloadSigner = (data: Uint8Array) => Uint8Array;

/**
 * Invoked when a peer sends an `Invoke`. Returning a non-null array sends a
 * `Response`; returning `null` sends a `Decline`.
 */
export type AgentCapabilityHandler = (
  capability: AgentCapability,
  requestPayload: Uint8Array,
) => Uint8Array | null;

/** Options for {@link InMemoryAgentPeerProtocol}. */
export interface InMemoryAgentPeerProtocolOptions {
  /** Signs outbound payloads. When omitted, signatures are empty. */
  readonly signer?: AgentPayloadSigner;
  /** Handles inbound `Invoke` messages. When omitted, invocations are ignored. */
  readonly capabilityHandler?: AgentCapabilityHandler;
}

interface Deferred<T> {
  readonly promise: Promise<T>;
  resolve(value: T): void;
  reject(reason: Object): void;
}

function defer<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason: Object) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const EMPTY = new Uint8Array(0);

/**
 * In-memory reference implementation of {@link IAgentPeerProtocol}. Backed by an
 * {@link AgentBus} so multiple instances can simulate a mesh of CircleAI peers.
 * Mirrors `CircleAI.Agents.Peer.InMemoryAgentPeerProtocol`.
 */
export class InMemoryAgentPeerProtocol implements IAgentPeerProtocol {
  private readonly ownUhidValue: string;
  private readonly bus: AgentBus;
  private readonly ownCapabilities: readonly AgentCapability[];
  private readonly ownPublicKey: Uint8Array;
  private readonly signer?: AgentPayloadSigner;
  private readonly capabilityHandler?: AgentCapabilityHandler;

  private readonly lastSeen = new Map<string, Date>();
  private readonly pendingInvocations = new Map<string, Deferred<AgentMessage>>();

  private readonly runController = new AbortController();
  private readonly pumpTask: Promise<void>;
  private readonly externalInbox = new AsyncQueue<AgentMessage>();

  private disposed = false;

  constructor(
    ownUhid: string,
    bus: AgentBus,
    ownCapabilities: readonly AgentCapability[],
    ownPublicKey: Uint8Array,
    options: InMemoryAgentPeerProtocolOptions = {},
  ) {
    if (ownUhid === null || ownUhid === undefined || ownUhid.trim().length === 0) {
      throw new Error("ownUhid must not be null or whitespace.");
    }
    if (bus === null || bus === undefined) throw new Error("bus is required.");
    if (ownCapabilities === null || ownCapabilities === undefined) {
      throw new Error("ownCapabilities is required.");
    }
    if (ownPublicKey === null || ownPublicKey === undefined) {
      throw new Error("ownPublicKey is required.");
    }

    this.ownUhidValue = ownUhid;
    this.bus = bus;
    this.ownCapabilities = ownCapabilities;
    this.ownPublicKey = ownPublicKey;
    this.signer = options.signer;
    this.capabilityHandler = options.capabilityHandler;

    this.bus.register(
      peerAgent(
        newPeerHandle(),
        ownUhid,
        ownUhid,
        this.ownCapabilities,
        this.ownPublicKey,
        "in-memory",
        new Date(),
      ),
    );

    this.pumpTask = this.pumpInboxAsync();
  }

  /** The UHID identity owned by this agent. */
  get ownUhid(): string {
    return this.ownUhidValue;
  }

  async discoverPeersAsync(signal?: AbortSignal): Promise<readonly PeerAgent[]> {
    throwIfAborted(signal);

    // Broadcast a Discover so peers can refresh their view of us.
    const announcement = createAgentMessage({
      kind: AgentMessageKind.Discover,
      fromUhid: this.ownUhidValue,
      toUhid: "*",
      contentType: "application/json",
      payload: EMPTY,
      signature: this.sign(EMPTY),
    });
    this.bus.send(announcement);

    // Brief listen window so any registered peer's responses can land.
    await delay(DEFAULT_DISCOVERY_WINDOW_MS, signal);

    return this.bus.registeredPeers
      .filter((p) => p.uhidIdentityId !== this.ownUhidValue)
      .map((p) => this.withLastSeen(p));
  }

  async greetAsync(targetUhid: string, signal?: AbortSignal): Promise<PeerAgent | null> {
    requireNonBlank(targetUhid, "targetUhid");
    throwIfAborted(signal);

    const peer = this.bus.tryGetPeer(targetUhid);
    if (peer === null) return null;

    const greet = createAgentMessage({
      kind: AgentMessageKind.Greet,
      fromUhid: this.ownUhidValue,
      toUhid: targetUhid,
      contentType: "application/json",
      payload: EMPTY,
      signature: this.sign(EMPTY),
    });
    this.bus.send(greet);

    return this.withLastSeen(peer);
  }

  async queryCapabilitiesAsync(
    targetUhid: string,
    signal?: AbortSignal,
  ): Promise<readonly AgentCapability[]> {
    requireNonBlank(targetUhid, "targetUhid");
    throwIfAborted(signal);

    const peer = this.bus.tryGetPeer(targetUhid);
    if (peer === null) return [];
    return peer.capabilities;
  }

  async invokeAsync(
    targetUhid: string,
    capability: AgentCapability,
    requestPayload: Uint8Array,
    signal?: AbortSignal,
  ): Promise<AgentMessage> {
    requireNonBlank(targetUhid, "targetUhid");
    if (capability === null || capability === undefined) throw new Error("capability is required.");
    if (requestPayload === null || requestPayload === undefined) {
      throw new Error("requestPayload is required.");
    }
    throwIfAborted(signal);

    if (this.bus.tryGetPeer(targetUhid) === null) {
      throw new AgentInvocationException(
        `Peer '${targetUhid}' is not reachable on the current transport.`,
        targetUhid,
      );
    }

    const invoke = createAgentMessage({
      kind: AgentMessageKind.Invoke,
      fromUhid: this.ownUhidValue,
      toUhid: targetUhid,
      contentType: "application/octet-stream",
      payload: requestPayload,
      signature: this.sign(requestPayload),
    });

    const tcs = defer<AgentMessage>();
    this.pendingInvocations.set(invoke.id, tcs);

    this.bus.send(invoke);

    // Timeout / caller-cancellation race against the reply.
    let timeoutHandle: ReturnType<typeof setTimeout> | undefined;
    let onAbort: (() => void) | undefined;
    const timedOutSentinel: unique symbol = Symbol("timeout") as never;

    const cancellation = new Promise<never>((_resolve, reject) => {
      timeoutHandle = setTimeout(() => reject(timedOutSentinel), DEFAULT_INVOKE_TIMEOUT_MS);
      if (signal !== undefined) {
        onAbort = () => reject(new AbortError());
        if (signal.aborted) onAbort();
        else signal.addEventListener("abort", onAbort, { once: true });
      }
    });

    let reply: AgentMessage;
    try {
      reply = await Promise.race([tcs.promise, cancellation]);
    } catch (err) {
      this.pendingInvocations.delete(invoke.id);
      if (err instanceof AbortError) {
        throwIfAborted(signal);
      }
      // Timeout sentinel OR abort-without-flagged-signal → invocation timeout.
      throw new AgentInvocationException(
        `Invocation of '${capability.name}' on peer '${targetUhid}' timed out.`,
        targetUhid,
      );
    } finally {
      if (timeoutHandle !== undefined) clearTimeout(timeoutHandle);
      if (signal !== undefined && onAbort !== undefined) {
        signal.removeEventListener("abort", onAbort);
      }
    }

    this.pendingInvocations.delete(invoke.id);

    if (reply.kind === AgentMessageKind.Decline) {
      throw new AgentInvocationException(
        `Peer '${targetUhid}' declined '${capability.name}'.`,
        targetUhid,
        reply,
      );
    }

    return reply;
  }

  async *streamInboxAsync(signal?: AbortSignal): AsyncIterable<AgentMessage> {
    yield* this.externalInbox.drain(signal);
  }

  /**
   * Tears down the protocol, unregisters from the bus, and stops the inbox pump.
   * (C# `IDisposable.Dispose`.)
   */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;

    this.runController.abort();
    this.bus.unregister(this.ownUhidValue);
    this.externalInbox.complete();
    // Reject any still-pending invocations so awaiters don't hang forever.
    for (const [, tcs] of this.pendingInvocations) {
      tcs.reject(new AgentInvocationException("Protocol disposed before reply.", this.ownUhidValue));
    }
    this.pendingInvocations.clear();
  }

  // ── Private helpers ───────────────────────────────────────────────────────

  private async pumpInboxAsync(): Promise<void> {
    try {
      for await (const message of this.bus.receive(this.ownUhidValue, this.runController.signal)) {
        // AgentMessage.sentAt is an ISO-8601 string; PeerAgent.lastSeenAt is a
        // Date, so parse the instant on the way into the last-seen index.
        this.lastSeen.set(message.fromUhid, new Date(message.sentAt));
        this.handleIncoming(message);
      }
    } catch {
      // Shutdown path — abort ends the receive loop.
    }
  }

  private handleIncoming(message: AgentMessage): void {
    switch (message.kind) {
      case AgentMessageKind.Response:
      case AgentMessageKind.Decline:
        this.completePending(message);
        break;
      case AgentMessageKind.Invoke:
        this.routeInvoke(message);
        break;
      default:
        break;
    }

    // Every inbound message is also surfaced to external consumers.
    this.externalInbox.enqueue(message);
  }

  private completePending(message: AgentMessage): void {
    // Convention: Response/Decline carry the original Invoke's id in the first
    // 16 bytes of the payload when generated by routeInvoke.
    if (message.payload.length < 16) return;
    const correlationId = bytesToGuid(message.payload, 0);
    const tcs = this.pendingInvocations.get(correlationId);
    if (tcs !== undefined) {
      tcs.resolve(message);
    }
  }

  private routeInvoke(invoke: AgentMessage): void {
    if (this.capabilityHandler === undefined) return;

    // Best-effort: a real implementation negotiates which capability is being
    // invoked by carrying its name in the payload. The in-memory mock simply
    // hands the first advertised capability to the handler.
    const capability: AgentCapability =
      this.ownCapabilities.length > 0
        ? this.ownCapabilities[0]
        : { name: "unknown", version: "0.0.0", costPerInvocation: 0, costCurrency: "SDPKT" };

    let result: Uint8Array | null;
    try {
      result = this.capabilityHandler(capability, invoke.payload);
    } catch {
      result = null;
    }

    const correlationPrefix = guidToBytes(invoke.id);

    if (result === null) {
      const decline = createAgentMessage({
        kind: AgentMessageKind.Decline,
        fromUhid: this.ownUhidValue,
        toUhid: invoke.fromUhid,
        contentType: "application/octet-stream",
        payload: correlationPrefix,
        signature: this.sign(correlationPrefix),
      });
      this.bus.send(decline);
      return;
    }

    const responsePayload = new Uint8Array(correlationPrefix.length + result.length);
    responsePayload.set(correlationPrefix, 0);
    responsePayload.set(result, correlationPrefix.length);

    const response = createAgentMessage({
      kind: AgentMessageKind.Response,
      fromUhid: this.ownUhidValue,
      toUhid: invoke.fromUhid,
      contentType: "application/octet-stream",
      payload: responsePayload,
      signature: this.sign(responsePayload),
    });
    this.bus.send(response);
  }

  private sign(data: Uint8Array): Uint8Array {
    return this.signer === undefined ? EMPTY : this.signer(data);
  }

  private withLastSeen(peer: PeerAgent): PeerAgent {
    const seen = this.lastSeen.get(peer.uhidIdentityId);
    return withLastSeen(peer, seen ?? peer.lastSeenAt);
  }
}

class AbortError extends Error {
  constructor() {
    super("The operation was aborted.");
    this.name = "AbortError";
  }
}

function requireNonBlank(value: string, name: string): void {
  if (value === null || value === undefined || value.trim().length === 0) {
    throw new Error(`${name} must not be null or whitespace.`);
  }
}

function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw new AbortError();
}

/**
 * Awaits `ms` milliseconds, resolving early (not throwing) if `signal` fires —
 * mirrors the C# discovery window that swallows the OperationCanceledException
 * ("window elapsed or caller cancelled — both fine").
 */
function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise<void>((resolve) => {
    if (signal?.aborted) {
      resolve();
      return;
    }
    let done = false;
    const finish = () => {
      if (done) return;
      done = true;
      clearTimeout(handle);
      signal?.removeEventListener("abort", onAbort);
      resolve();
    };
    const onAbort = () => finish();
    const handle = setTimeout(finish, ms);
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}
