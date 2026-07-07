// companion/memory_encoder.ts
// Background writer: turn → knowledge graph + attributed beliefs, off the hot
// path. Ported from Circle.AI.Companion (CompanionMemoryEncoder) — the C#
// reference.
//
// After each turn the session hands the exchange here and moves on; encoding
// happens on a background queue so the reply is never delayed. A full queue drops
// rather than blocks. C# uses a bounded Channel with DropWrite + a drain Task;
// JavaScript is single-threaded, so here a bounded array + a wakeup promise plays
// the same role (the drain runs as microtasks, never blocking a turn).

import type { InMemoryKnowledgeGraph } from "../memory/graph.js";
import type { IKnowledgeGraphExtractor } from "../memory/extractor.js";
import type { IBeliefExtractor, SelfBeliefStore } from "./belief.js";

interface EncodeJob {
  readonly userText: string;
  readonly assistantText: string;
  readonly episodeId: string;
}

/** Background writer: turn → knowledge graph, off the hot path. */
export class CompanionMemoryEncoder {
  private readonly extractor: IKnowledgeGraphExtractor;
  private readonly graph: InMemoryKnowledgeGraph;
  private readonly beliefExtractor: IBeliefExtractor | null;
  private readonly beliefs: SelfBeliefStore | null;
  private readonly capacity: number;

  private readonly queue: EncodeJob[] = [];
  private closed = false;
  private wake: (() => void) | null = null;
  private readonly drain: Promise<void>;

  /** First error hit while draining, if any (diagnostics). */
  lastError: unknown = null;

  constructor(
    extractor: IKnowledgeGraphExtractor,
    graph: InMemoryKnowledgeGraph,
    beliefExtractor: IBeliefExtractor | null = null,
    beliefs: SelfBeliefStore | null = null,
    capacity = 256,
  ) {
    if (!extractor) throw new Error("extractor required");
    if (!graph) throw new Error("graph required");
    this.extractor = extractor;
    this.graph = graph;
    this.beliefExtractor = beliefExtractor;
    this.beliefs = beliefs;
    this.capacity = Math.max(1, capacity);
    this.drain = this.drainLoop();
  }

  /** Hand a turn to the encoder. Non-blocking; returns immediately. */
  enqueue(userText: string, assistantText: string, episodeId: string): void {
    if (!episodeId || episodeId.trim().length === 0) return;
    if (this.closed) return;
    if (this.queue.length >= this.capacity) return; // DropWrite: never block a turn
    this.queue.push({ userText: userText ?? "", assistantText: assistantText ?? "", episodeId });
    const wake = this.wake;
    this.wake = null;
    wake?.();
  }

  private async drainLoop(): Promise<void> {
    for (;;) {
      if (this.queue.length === 0) {
        if (this.closed) return;
        await new Promise<void>((resolve) => {
          this.wake = resolve;
        });
        continue;
      }

      const job = this.queue.shift()!;
      try {
        // Give the memory node a readable name so recall hands back the actual
        // exchange, not an opaque id.
        this.graph.upsertNode({ id: job.episodeId, kind: "memory", name: job.userText, properties: {} });

        const triples = await this.extractor.extractFromTurnAsync(
          job.userText,
          job.assistantText,
          job.episodeId,
        );
        for (const t of triples) {
          this.graph.addTriple(t.subject, t.predicate, t.object, t.source, t.confidence);
        }

        // Form attributed beliefs from this turn — a third party's fact never
        // becomes the user's. Happens here, off the turn, at the point the false
        // belief would otherwise be created.
        if (this.beliefExtractor && this.beliefs) {
          for (const b of await this.beliefExtractor.extractAsync(job.userText, job.episodeId)) {
            this.beliefs.record(b);
          }
        }
      } catch (ex) {
        if (this.lastError == null) this.lastError = ex;
      }
    }
  }

  /** Stops accepting work and waits for the queue to drain. */
  async closeAsync(): Promise<void> {
    this.closed = true;
    const wake = this.wake;
    this.wake = null;
    wake?.();
    await this.drain;
  }
}
