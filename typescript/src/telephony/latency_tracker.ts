// telephony/latency_tracker.ts
//
// Per-stage latency tracking for the voice loop — faithful port of
// LatencyTracker.cs. Records observations into a fixed-size sliding window per
// stage and surfaces p50/p95/p99 + max via a snapshot API.
//
// `TimeSpan` observations are stored as milliseconds (integer, matching the C#
// `(long)latency.TotalMilliseconds` truncation). The `ConcurrentDictionary` +
// per-queue lock collapses to plain map/array access on JS's single thread.

/** One stage we track latency on. Mirrors static `LatencyStage`. */
export const LatencyStage = {
  AsrFirstWord: "asr.first_word",
  AsrFinal: "asr.final",
  LlmFirstToken: "llm.first_token",
  LlmFullResponse: "llm.full_response",
  TtsFirstAudio: "tts.first_audio",
  TtsFullAudio: "tts.full_audio",
  EndToEnd: "voice_loop.end_to_end",
} as const;

/** Snapshot of latency for one stage. Mirrors `LatencySnapshot`. All times in ms. */
export interface LatencySnapshot {
  readonly stage: string;
  readonly samples: number;
  readonly minMs: number;
  readonly p50Ms: number;
  readonly p95Ms: number;
  readonly p99Ms: number;
  readonly maxMs: number;
}

/** Records latency observations and produces percentiles. Mirrors `LatencyTracker`. */
export class LatencyTracker {
  private readonly windowSize: number;
  private readonly observations = new Map<string, number[]>();

  constructor(windowSize = 256) {
    if (windowSize <= 0) throw new RangeError("windowSize");
    this.windowSize = windowSize;
  }

  /** Record one observation. `latencyMs` in milliseconds. */
  record(stage: string, latencyMs: number): void {
    if (!stage || stage.trim().length === 0) throw new Error("stage required");
    if (latencyMs < 0) return;

    let queue = this.observations.get(stage);
    if (queue === undefined) {
      queue = [];
      this.observations.set(stage, queue);
    }
    queue.push(Math.trunc(latencyMs));
    while (queue.length > this.windowSize) queue.shift();
  }

  /** Snapshot percentiles for one stage. */
  snapshot(stage: string): LatencySnapshot | null {
    const queue = this.observations.get(stage);
    if (queue === undefined || queue.length === 0) return null;
    const sorted = [...queue].sort((a, b) => a - b);

    const percentile = (p: number): number => {
      if (sorted.length === 0) return 0;
      let idx = Math.ceil(p * sorted.length) - 1;
      if (idx < 0) idx = 0;
      if (idx >= sorted.length) idx = sorted.length - 1;
      return sorted[idx]!;
    };

    return {
      stage,
      samples: sorted.length,
      minMs: sorted[0]!,
      p50Ms: percentile(0.5),
      p95Ms: percentile(0.95),
      p99Ms: percentile(0.99),
      maxMs: sorted[sorted.length - 1]!,
    };
  }

  /** Snapshot every tracked stage. */
  snapshotAll(): readonly LatencySnapshot[] {
    const list: LatencySnapshot[] = [];
    for (const stage of [...this.observations.keys()]) {
      const snap = this.snapshot(stage);
      if (snap !== null) list.push(snap);
    }
    return list;
  }

  reset(stage: string): void {
    const queue = this.observations.get(stage);
    if (queue === undefined) return;
    queue.length = 0;
  }

  resetAll(): void {
    this.observations.clear();
  }
}
