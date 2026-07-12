// telephony/eval_session.ts
//
// Drive an end-to-end voice-pipeline test against a real LLM without needing a
// carrier minute — faithful port of EvalSession.cs. The harness feeds a scripted
// conversation (user utterances) through the same pipeline production uses, then
// collects everything the AI said back for assertion.
//
// C# measures latency with `DateTime.UtcNow` deltas → `Date.now()` deltas
// (milliseconds). The per-turn handler is injected (the model call is a seam).

/** One scripted turn from a fake caller. Mirrors `EvalTurn`. */
export interface EvalTurn {
  /** What the caller said (already-transcribed). */
  readonly userTranscript: string;
  /** Optional keywords the AI's response should include. */
  readonly expectedKeywords?: readonly string[];
}

/** Constructs an {@link EvalTurn}. */
export function evalTurn(userTranscript: string, expectedKeywords?: readonly string[]): EvalTurn {
  return { userTranscript, expectedKeywords };
}

/** Outcome of one eval turn. Mirrors `EvalTurnResult`. */
export interface EvalTurnResult {
  readonly assistantResponse: string;
  readonly missingKeywords: readonly string[];
  /** Latency in milliseconds. */
  readonly latencyMs: number;
}

/** Overall eval result. Mirrors `EvalRunResult`. */
export interface EvalRunResult {
  readonly turns: readonly EvalTurnResult[];
  readonly allKeywordsHit: boolean;
  /** Total latency in milliseconds. */
  readonly totalLatencyMs: number;
}

/** Function that runs one turn through the AI under test. Mirrors the `EvalTurnHandler` delegate. */
export type EvalTurnHandler = (userTranscript: string, signal?: AbortSignal) => Promise<string>;

/** Drives an EvalSession against a real LLM-based handler. Mirrors `EvalSession`. */
export class EvalSession {
  private readonly handler: EvalTurnHandler;

  constructor(handler: EvalTurnHandler) {
    if (handler === null || handler === undefined) throw new Error("handler is required");
    this.handler = handler;
  }

  /** Run the script and assemble results. */
  async runAsync(script: readonly EvalTurn[], signal?: AbortSignal): Promise<EvalRunResult> {
    if (script === null || script === undefined) throw new Error("script is required");
    const results: EvalTurnResult[] = [];
    let totalMs = 0;
    let allHit = true;
    for (const turn of script) {
      const started = Date.now();
      const response = await this.handler(turn.userTranscript, signal);
      const elapsedMs = Date.now() - started;
      totalMs += elapsedMs;

      const missing: string[] = [];
      if (turn.expectedKeywords !== undefined) {
        for (const kw of turn.expectedKeywords) {
          if (response.toLowerCase().indexOf(kw.toLowerCase()) < 0) {
            missing.push(kw);
          }
        }
      }
      if (missing.length > 0) allHit = false;
      results.push({ assistantResponse: response, missingKeywords: missing, latencyMs: elapsedMs });
    }
    return { turns: results, allKeywordsHit: allHit, totalLatencyMs: totalMs };
  }
}
