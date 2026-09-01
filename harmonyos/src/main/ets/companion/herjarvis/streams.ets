/**
 * companion/herjarvis/streams
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/herjarvis/streams.ts
//
// The channel/stream-based HerJarvis implementations, ported from
// HerJarvisRealImplementations.cs:
//
//   1.  HeartbeatAlwaysOnPresence — IAlwaysOnPresence
//   2.  ChannelFusedPerception    — IFusedPerception
//   16. TopicLiveWorldKnowledge   — ILiveWorldKnowledge
//   17. ChannelBioSignalStream    — IBioSignalStream
//   19. MailboxAgentPeerNetwork   — IAgentPeerNetwork
//
// C# uses System.Threading.Channels (unbounded) + IAsyncEnumerable with
// WaitToReadAsync/TryRead. TypeScript has no Channel primitive, so each of these
// uses a small in-memory async queue (AsyncQueue) that supports the exact
// producer/consumer shape: TryWrite = enqueue, the stream = an async iterator
// that drains the queue and then awaits the next item. Cancellation is via an
// optional AbortSignal (the C# CancellationToken).

import { AsyncQueue } from "./async_queue";
import type {
  IAlwaysOnPresence,
  IFusedPerception,
  FusedPercept,
  ILiveWorldKnowledge,
  WorldFact,
  IBioSignalStream,
  BioSignal,
  IAgentPeerNetwork,
  AgentToAgentMessage,
} from "./contracts";

// =====================================================================
// 1. AlwaysOnPresence — timer-driven heartbeat with start/stop.
// =====================================================================

/**
 * A heartbeat presence: `startAsync` arms a repeating timer that increments a
 * tick counter (first tick fires immediately, matching the C# `TimeSpan.Zero`
 * due time); `stopAsync` clears it. Idempotent start (a second start is a
 * no-op while running), mirroring `if (_timer is not null) return`.
 */
export class HeartbeatAlwaysOnPresence implements IAlwaysOnPresence {
  private timer: ReturnType<typeof setInterval> | null = null;
  private ticks = 0;
  private readonly intervalMs: number;
  // Fire the first heartbeat immediately (C# due time TimeSpan.Zero).
  private immediate: ReturnType<typeof setTimeout> | null = null;

  constructor(heartbeatIntervalMs = 30_000) {
    this.intervalMs = heartbeatIntervalMs;
  }

  get isRunning(): boolean {
    return this.timer !== null;
  }

  get heartbeats(): number {
    return this.ticks;
  }

  startAsync(): Promise<void> {
    if (this.timer !== null) return Promise.resolve();
    // C# Timer with dueTime=0 fires once immediately, then every period.
    this.immediate = setTimeout(() => {
      this.ticks++;
      this.immediate = null;
    }, 0);
    if (typeof this.immediate.unref === "function") this.immediate.unref();
    this.timer = setInterval(() => {
      this.ticks++;
    }, this.intervalMs);
    if (typeof this.timer.unref === "function") this.timer.unref();
    return Promise.resolve();
  }

  stopAsync(): Promise<void> {
    if (this.immediate !== null) {
      clearTimeout(this.immediate);
      this.immediate = null;
    }
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
    return Promise.resolve();
  }

  /** Alias for symmetry with the C# IDisposable.Dispose(). */
  dispose(): void {
    void this.stopAsync();
  }
}

// =====================================================================
// 2. FusedPerception — Channel-based pub/sub with Publish hook.
// =====================================================================

/**
 * A publish/subscribe fused-perception stream. `publish` enqueues a percept;
 * `streamAsync` yields percepts as they arrive until `complete()` is called
 * (or the AbortSignal fires).
 */
export class ChannelFusedPerception implements IFusedPerception {
  private readonly queue = new AsyncQueue<FusedPercept>();

  publish(p: FusedPercept): void {
    if (p == null) throw new Error("percept required");
    this.queue.enqueue(p);
  }

  complete(): void {
    this.queue.complete();
  }

  async *streamAsync(signal?: AbortSignal): AsyncGenerator<FusedPercept> {
    yield* this.queue.drain(signal);
  }
}

// =====================================================================
// 16. LiveWorldKnowledge — topic-pub/sub broker.
// =====================================================================

/**
 * A topic broker: `publish` routes a fact to the queue for its topic (only if
 * that topic already has subscribers, matching the C# `TryGetValue` guard);
 * `subscribeAsync` interleaves the queues for the requested topics, polling
 * ~every 50ms as the C# does.
 */
export class TopicLiveWorldKnowledge implements ILiveWorldKnowledge {
  private readonly byTopic = new Map<string, AsyncQueue<WorldFact>>();

  /** Publish a fact to subscribers of the matching topic. */
  publish(fact: WorldFact): void {
    if (fact == null) throw new Error("fact required");
    const q = this.byTopic.get(fact.topic);
    if (q !== undefined) q.enqueue(fact);
  }

  async *subscribeAsync(
    topics: readonly string[],
    signal?: AbortSignal,
  ): AsyncGenerator<WorldFact> {
    if (topics == null) throw new Error("topics required");
    const channels = topics.map((t) => {
      let q = this.byTopic.get(t);
      if (q === undefined) {
        q = new AsyncQueue<WorldFact>();
        this.byTopic.set(t, q);
      }
      return q;
    });

    // Mirror the C# loop: drain all channels non-blocking, then Task.Delay(50).
    while (!(signal?.aborted ?? false)) {
      for (const c of channels) {
        let f: WorldFact | undefined;
        while ((f = c.tryDequeue()) !== undefined) yield f;
      }
      try {
        await delay(50, signal);
      } catch {
        return; // OperationCanceledException -> yield break
      }
    }
  }
}

// =====================================================================
// 17. BioSignalStream — fan-in channel with Publish hook.
// =====================================================================

/** A fan-in bio-signal stream — same shape as ChannelFusedPerception. */
export class ChannelBioSignalStream implements IBioSignalStream {
  private readonly queue = new AsyncQueue<BioSignal>();

  publish(s: BioSignal): void {
    if (s == null) throw new Error("signal required");
    this.queue.enqueue(s);
  }

  complete(): void {
    this.queue.complete();
  }

  async *streamAsync(signal?: AbortSignal): AsyncGenerator<BioSignal> {
    yield* this.queue.drain(signal);
  }
}

// =====================================================================
// 19. AgentPeerNetwork — in-memory mailbox per agent id.
// =====================================================================

/**
 * An in-memory per-agent mailbox. `sendAsync` enqueues to the recipient's
 * mailbox (created on demand); `receiveAsync` drains one agent's mailbox as an
 * async stream. Ordinal (case-sensitive) agent ids, matching the C#
 * StringComparer.Ordinal keying.
 */
export class MailboxAgentPeerNetwork implements IAgentPeerNetwork {
  private readonly mailboxes = new Map<string, AsyncQueue<AgentToAgentMessage>>();

  private box(id: string): AsyncQueue<AgentToAgentMessage> {
    let b = this.mailboxes.get(id);
    if (b === undefined) {
      b = new AsyncQueue<AgentToAgentMessage>();
      this.mailboxes.set(id, b);
    }
    return b;
  }

  async sendAsync(message: AgentToAgentMessage): Promise<void> {
    if (message == null) throw new Error("message required");
    this.box(message.toAgentId).enqueue(message);
  }

  async *receiveAsync(
    forAgentId: string,
    signal?: AbortSignal,
  ): AsyncGenerator<AgentToAgentMessage> {
    if (!forAgentId || forAgentId.trim().length === 0) throw new Error("forAgentId required");
    yield* this.box(forAgentId).drain(signal);
  }
}

/** Promise-based delay that rejects when the AbortSignal fires. */
function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal?.aborted) {
      reject(new Error("aborted"));
      return;
    }
    const t = setTimeout(() => {
      cleanup();
      resolve();
    }, ms);
    if (typeof t.unref === "function") t.unref();
    const onAbort = () => {
      clearTimeout(t);
      cleanup();
      reject(new Error("aborted"));
    };
    const cleanup = () => signal?.removeEventListener("abort", onAbort);
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}
