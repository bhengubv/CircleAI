/**
 * telephony/call_cost_calculator
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// telephony/call_cost_calculator.ts
//
// Track per-call cost across the four spend axes: carrier telephony minutes,
// STT seconds, TTS characters, LLM tokens (input + output). Faithful port of
// CallCostCalculator.cs. The caller pipeline emits usage events; the calculator
// turns them into a running cost figure the orchestrator can compare against a
// budget ceiling.
//
// The C# accumulates via `Interlocked.Add` on `long` fields; JS is single-
// threaded so plain numeric accumulation is the exact equivalent. `TimeSpan`
// arguments become milliseconds.

/** Per-unit prices (USD or any consistent currency). Mirrors `CallPricing`. */
export interface CallPricing {
  /** Cost per minute of carrier telephony. */
  readonly carrierPerMinute: number;
  /** Cost per second of STT. */
  readonly sttPerSecond: number;
  /** Cost per 1000 characters of TTS. */
  readonly ttsPerThousandChars: number;
  /** Cost per 1000 input tokens. */
  readonly llmInputPerKToken: number;
  /** Cost per 1000 output tokens. */
  readonly llmOutputPerKToken: number;
}

/** Constructs a {@link CallPricing}. */
export function callPricing(
  carrierPerMinute: number,
  sttPerSecond: number,
  ttsPerThousandChars: number,
  llmInputPerKToken: number,
  llmOutputPerKToken: number,
): CallPricing {
  return {
    carrierPerMinute,
    sttPerSecond,
    ttsPerThousandChars,
    llmInputPerKToken,
    llmOutputPerKToken,
  };
}

/** Breakdown of where the money went. Mirrors `CallCostBreakdown`. */
export interface CallCostBreakdown {
  readonly carrier: number;
  readonly stt: number;
  readonly tts: number;
  readonly llmInput: number;
  readonly llmOutput: number;
  readonly total: number;
}

/** Tracks cost for one call. Mirrors `CallCostCalculator`. */
export class CallCostCalculator {
  private readonly pricing: CallPricing;
  private carrierMs = 0;
  private sttMs = 0;
  private ttsChars = 0;
  private llmInputTokens = 0;
  private llmOutputTokens = 0;

  constructor(pricing: CallPricing) {
    if (pricing === null || pricing === undefined) {
      throw new Error("pricing is required");
    }
    this.pricing = pricing;
  }

  /** Add carrier telephony usage. `durationMs` in milliseconds. */
  addCarrierTime(durationMs: number): void {
    if (durationMs < 0) return;
    this.carrierMs += Math.trunc(durationMs);
  }

  /** Add STT usage. `durationMs` in milliseconds. */
  addSttTime(durationMs: number): void {
    if (durationMs < 0) return;
    this.sttMs += Math.trunc(durationMs);
  }

  /** Add TTS usage in characters. */
  addTtsCharacters(chars: number): void {
    if (chars <= 0) return;
    this.ttsChars += chars;
  }

  /** Add LLM tokens. */
  addLlmTokens(inputTokens: number, outputTokens: number): void {
    if (inputTokens > 0) this.llmInputTokens += inputTokens;
    if (outputTokens > 0) this.llmOutputTokens += outputTokens;
  }

  /** Snapshot the current total cost breakdown. */
  currentBreakdown(): CallCostBreakdown {
    const carrierMin = this.carrierMs / 60_000;
    const sttSec = this.sttMs / 1000;
    const ttsK = this.ttsChars / 1000;
    const llmInputK = this.llmInputTokens / 1000;
    const llmOutputK = this.llmOutputTokens / 1000;

    const carrier = carrierMin * this.pricing.carrierPerMinute;
    const stt = sttSec * this.pricing.sttPerSecond;
    const tts = ttsK * this.pricing.ttsPerThousandChars;
    const llmIn = llmInputK * this.pricing.llmInputPerKToken;
    const llmOut = llmOutputK * this.pricing.llmOutputPerKToken;
    const total = carrier + stt + tts + llmIn + llmOut;

    return { carrier, stt, tts, llmInput: llmIn, llmOutput: llmOut, total };
  }

  reset(): void {
    this.carrierMs = 0;
    this.sttMs = 0;
    this.ttsChars = 0;
    this.llmInputTokens = 0;
    this.llmOutputTokens = 0;
  }
}
