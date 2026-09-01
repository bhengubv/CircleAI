/**
 * wearable/biosignals/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// wearable/biosignals/index.ts
// Full-parity port of CircleAI.Wearable.Biosignals (C#). C# is the exact spec.
//
// The wearable biosignal layer: a canonical BiosignalKind taxonomy, a
// BiosignalSample record, a streaming IBiosignalSource contract with a null and a
// recorded implementation, a sliding-window BiosignalAggregator, and a
// deterministic BiosignalAffectMapper that projects samples onto AffectState.
//
// Type mappings (C# → TS):
//   enum BiosignalKind (0..8)          → const enum-like BiosignalKind
//   record BiosignalSample             → readonly interface (+ createBiosignalSample)
//   Guid Id                            → string (Guid.NewGuid().ToString("n") shape)
//   float Value / Confidence / Min / … → number, produced through Math.fround so
//                                        single-precision C# values match exactly.
//   bool IsCumulative                  → boolean
//   DateTimeOffset MeasuredAt/GeneratedAt → Date
//   IAsyncEnumerable<BiosignalSample>  → AsyncIterable<BiosignalSample>
//   CancellationToken                  → optional AbortSignal
//   Task<bool>                         → Promise<boolean>
//   IReadOnlyDictionary<BiosignalKind, BiosignalStats> → ReadonlyMap<…>
//   TimeSpan window / replayDelay      → number of milliseconds
//
// FLOAT PARITY: BiosignalSample.value/confidence and BiosignalStats fields are C#
// `float`. We route them through Math.fround (`fr`) so they carry the exact
// single-precision value. The aggregator sums in double precision then casts the
// mean to float (fr), matching `(float)(_sum / _count)`.
//
// CONCURRENCY PARITY (aggregator): the C# SnapshotAsync time-bounds the stream
// read with CancelAfter(window). We reproduce this by cancelling an AbortController
// after `window` ms; the linked signal both honours the caller's signal and the
// window deadline. We subscribe to (iterate) the source stream directly and stop
// at the first sample past the deadline, matching the C# deadline break.

import { AffectState } from "../../memory/index";
import { newGuidN } from "../../companion/herjarvis/guid";

/** Single-precision rounding at C# `float` sites. */
const fr = Math.fround;

/**
 * Canonical kinds of biosignal samples Circle AI consumes from wearables.
 * Integer values are stable across language ports — do not renumber. Mirrors C#
 * `BiosignalKind`.
 */
export type BiosignalKind = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8;
/** Frozen value object for {@link BiosignalKind} members. */
export const BiosignalKind = Object.freeze({
  /** Heart rate, beats per minute. */
  HeartRate: 0,
  /** Heart rate variability, RMSSD in milliseconds. */
  HeartRateVariability: 1,
  /** Peripheral oxygen saturation, percent (0-100). */
  OxygenSaturation: 2,
  /** Accelerometer magnitude, m/s^2. */
  Accelerometer: 3,
  /** Body temperature, degrees Celsius. */
  BodyTemperature: 4,
  /** Sleep stage encoded as a float: 0=awake, 1=light, 2=deep, 3=REM. */
  SleepStage: 5,
  /** Step count (cumulative or delta — see {@link BiosignalSample.isCumulative}). */
  Steps: 6,
  /** Galvanic skin response, microsiemens. */
  GalvanicSkinResponse: 7,
  /** Catch-all for vendor-specific or future signals. */
  Unknown: 8,
} as const) satisfies Map<string, BiosignalKind>;

/** A single biosignal measurement. Mirrors C# `BiosignalSample` record. */
export interface BiosignalSample {
  /** Stable identifier for this sample (C# `Guid Id`, "n" format). */
  readonly id: string;
  readonly kind: BiosignalKind;
  /** Numeric value in the canonical unit for the kind (C# `float`). */
  readonly value: number;
  /** Canonical unit string ("bpm", "ms", "%", "m/s^2", "celsius", "stage", "count", "uS"). */
  readonly unit: string;
  /** Sensor-reported confidence in [0, 1] (C# `float`). */
  readonly confidence: number;
  /** True when {@link kind} is Steps and the value is total-since-epoch. */
  readonly isCumulative: boolean;
  /** UTC time the sample was captured (C# `DateTimeOffset MeasuredAt`). */
  readonly measuredAt: Date;
}

/** Constructs a {@link BiosignalSample} from explicit fields (positional record ctor). */
export function biosignalSample(
  id: string,
  kind: BiosignalKind,
  value: number,
  unit: string,
  confidence: number,
  isCumulative: boolean,
  measuredAt: Date,
): BiosignalSample {
  return { id, kind, value: fr(value), unit, confidence: fr(confidence), isCumulative, measuredAt };
}

/**
 * Creates a fresh sample with a new Guid id, current UTC timestamp, and
 * confidence clamped to [0, 1]. Mirrors C# `BiosignalSample.Create`.
 */
export function createBiosignalSample(
  kind: BiosignalKind,
  value: number,
  unit: string,
  confidence = 1.0,
  isCumulative = false,
): BiosignalSample {
  return {
    id: newGuidN(),
    kind,
    value: fr(value),
    unit,
    confidence: fr(Math.max(0, Math.min(1, confidence))),
    isCumulative,
    measuredAt: new Date(),
  };
}

/**
 * A streaming source of biosignal samples — typically backed by a wearable
 * device, a platform health API, or a simulator for tests. Mirrors C#
 * `IBiosignalSource`.
 */
export interface IBiosignalSource {
  /** The kinds of signals this source can emit. May be empty for the null source. */
  readonly supportedKinds: readonly BiosignalKind[];
  /** Streams biosignal samples until the signal aborts or the source completes. */
  streamAsync(signal?: AbortSignal): AsyncIterable<BiosignalSample>;
  /** Reports whether this source can produce samples of the given kind. */
  isSupportedAsync(kind: BiosignalKind, signal?: AbortSignal): Promise<boolean>;
}

/**
 * A biosignal source that supports nothing and emits nothing. Use for tests and
 * as the default when no wearable is connected. Mirrors C# `NullBiosignalSource`.
 */
export class NullBiosignalSource implements IBiosignalSource {
  get supportedKinds(): readonly BiosignalKind[] {
    return [];
  }

  isSupportedAsync(_kind: BiosignalKind, _signal?: AbortSignal): Promise<boolean> {
    return Promise.resolve(false);
  }

  // eslint-disable-next-line @typescript-eslint/no-unused-vars, require-yield
  async *streamAsync(_signal?: AbortSignal): AsyncIterable<BiosignalSample> {
    // Yield nothing. The await keeps the method genuinely async.
    await Promise.resolve();
    return;
  }
}

/** A short cancellable delay honouring an AbortSignal (C# Task.Delay(ct)). */
function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException("Aborted", "AbortError"));
      return;
    }
    const t = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    const onAbort = (): void => {
      clearTimeout(t);
      reject(new DOMException("Aborted", "AbortError"));
    };
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

/**
 * Replays a recorded biosignal stream. Useful for tests, training data, and host
 * integration when no live wearable is connected. Mirrors C#
 * `RecordedBiosignalSource`.
 */
export class RecordedBiosignalSource implements IBiosignalSource {
  private readonly samples: readonly BiosignalSample[];
  private readonly kinds: readonly BiosignalKind[];
  private readonly replayDelayMs: number;

  constructor(samples: readonly BiosignalSample[], replayDelayMs = 0) {
    if (samples == null) throw new Error("samples required");
    this.samples = samples;
    this.replayDelayMs = replayDelayMs;
    const seen = new Set<BiosignalKind>();
    for (const s of samples) seen.add(s.kind);
    this.kinds = [...seen];
  }

  get supportedKinds(): readonly BiosignalKind[] {
    return this.kinds;
  }

  isSupportedAsync(kind: BiosignalKind, _signal?: AbortSignal): Promise<boolean> {
    return Promise.resolve(this.kinds.includes(kind));
  }

  async *streamAsync(signal?: AbortSignal): AsyncIterable<BiosignalSample> {
    for (const s of this.samples) {
      if (signal?.aborted) throw new DOMException("Aborted", "AbortError");
      if (this.replayDelayMs > 0) await delay(this.replayDelayMs, signal);
      yield s;
    }
  }
}

/** Per-kind aggregate statistics over a sliding window. Mirrors C# `BiosignalStats`. */
export interface BiosignalStats {
  readonly sampleCount: number;
  /** Minimum value observed (C# `float`). */
  readonly min: number;
  /** Maximum value observed (C# `float`). */
  readonly max: number;
  /** Arithmetic mean of observed values (C# `float`). */
  readonly mean: number;
}

/** Constructs a {@link BiosignalStats} (float fields routed through Math.fround). */
export function biosignalStats(sampleCount: number, min: number, max: number, mean: number): BiosignalStats {
  return { sampleCount, min: fr(min), max: fr(max), mean: fr(mean) };
}

/**
 * A snapshot of biosignal aggregates across all observed kinds at a point in
 * time. Kinds with no samples in the window are absent. Mirrors C#
 * `BiosignalSnapshot`.
 */
export interface BiosignalSnapshot {
  readonly stats: ReadonlyMap<BiosignalKind, BiosignalStats>;
  /** UTC timestamp at which the snapshot was generated (C# `DateTimeOffset GeneratedAt`). */
  readonly generatedAt: Date;
}

/** Double-precision accumulator matching the C# private Accumulator. */
class Accumulator {
  private count = 0;
  private minV = Number.POSITIVE_INFINITY;
  private maxV = Number.NEGATIVE_INFINITY;
  private sum = 0;

  add(v: number): void {
    this.count++;
    if (v < this.minV) this.minV = v;
    if (v > this.maxV) this.maxV = v;
    this.sum += v;
  }

  toStats(): BiosignalStats {
    // Min/Max preserve the (already float) sample values; mean casts double→float.
    return {
      sampleCount: this.count,
      min: fr(this.minV),
      max: fr(this.maxV),
      mean: this.count === 0 ? fr(0) : fr(this.sum / this.count),
    };
  }
}

/**
 * Sliding-window aggregator over an {@link IBiosignalSource}. Mirrors C#
 * `BiosignalAggregator`.
 */
export class BiosignalAggregator {
  private readonly source: IBiosignalSource;

  constructor(source: IBiosignalSource) {
    if (source == null) throw new Error("source required");
    this.source = source;
  }

  /**
   * Consumes samples until the source completes or `windowMs` elapses, then
   * returns a snapshot over the samples that fell within the window (relative to
   * UTC now at call time). Single-shot, not continuous. Mirrors C#
   * `SnapshotAsync`.
   */
  async snapshotAsync(windowMs: number, signal?: AbortSignal): Promise<BiosignalSnapshot> {
    if (windowMs <= 0) throw new Error("Window must be positive.");

    const generatedAt = new Date();
    const cutoff = generatedAt.getTime() - windowMs;
    const deadline = generatedAt.getTime() + windowMs;
    const accumulator = new Map<BiosignalKind, Accumulator>();

    // Time-bound the read so a never-completing source still yields a snapshot.
    const controller = new AbortController();
    const linkedAbort = (): void => controller.abort();
    if (signal !== undefined) {
      if (signal.aborted) controller.abort();
      else signal.addEventListener("abort", linkedAbort, { once: true });
    }
    const timer = setTimeout(() => controller.abort(), windowMs);
    if (typeof (timer as { unref?: () => void }).unref === "function") {
      (timer as { unref: () => void }).unref();
    }

    try {
      for await (const sample of this.source.streamAsync(controller.signal)) {
        if (sample.measuredAt.getTime() < cutoff) continue;

        let acc = accumulator.get(sample.kind);
        if (acc === undefined) {
          acc = new Accumulator();
          accumulator.set(sample.kind, acc);
        }
        acc.add(sample.value);

        if (Date.now() >= deadline) break;
      }
    } catch (ex) {
      // Window elapsed / abort before the source completed. Rethrow only if the
      // caller's own signal was the cause (C# rethrows unless it was the window).
      if (signal?.aborted) throw ex;
      if (!isAbortError(ex)) throw ex;
      // else: window elapsed — expected; fall through.
    } finally {
      clearTimeout(timer);
      signal?.removeEventListener("abort", linkedAbort);
    }

    const stats = new Map<BiosignalKind, BiosignalStats>();
    for (const [kind, acc] of accumulator) stats.set(kind, acc.toStats());
    return { stats, generatedAt };
  }
}

/** True for an AbortError (the cancellation surfaced by an aborted stream). */
function isAbortError(ex: Object): boolean {
  return ex instanceof DOMException && ex.name === "AbortError";
}

/**
 * Maps biosignal samples to {@link AffectState} mutations using deterministic,
 * fixture-validated rules. Mirrors C# `BiosignalAffectMapper`.
 *
 * Rule sheet (all mutations clamped to [0, 1], float arithmetic):
 *   HeartRate > 130 bpm (conf ≥ 0.5): Energy += 0.10, Uncertainty += 0.05.
 *   HeartRate > 100 bpm (conf ≥ 0.5): Energy += 0.05.
 *   HeartRate < 50 bpm  (conf ≥ 0.5): Energy -= 0.05.
 *   HRV < 20 ms (conf ≥ 0.5): Uncertainty += 0.05, Rapport -= 0.02.
 *   HRV > 60 ms (conf ≥ 0.5): Engagement += 0.02.
 *   SpO2 < 90 % (conf ≥ 0.5): Uncertainty += 0.10.
 *   SleepStage / low-confidence / other kinds: no mutation.
 */
export const BiosignalAffectMapper = {
  /** C# `MinConfidence` (0.5f). */
  minConfidence: fr(0.5),

  /**
   * Applies the rule for `sample` to `affect`, mutating it in place. Safe to call
   * repeatedly — resulting field values are clamped to [0, 1].
   */
  apply(sample: BiosignalSample, affect: AffectState): void {
    if (sample == null) throw new Error("sample required");
    if (affect == null) throw new Error("affect required");

    // Confidence gate — low-confidence samples never mutate state.
    if (sample.confidence < this.minConfidence) return;

    switch (sample.kind) {
      case BiosignalKind.HeartRate:
        applyHeartRate(sample.value, affect);
        break;
      case BiosignalKind.HeartRateVariability:
        applyHrv(sample.value, affect);
        break;
      case BiosignalKind.OxygenSaturation:
        applySpO2(sample.value, affect);
        break;
      // SleepStage + remaining kinds do not drive affect.
      default:
        break;
    }

    affect.lastUpdatedUtc = new Date();
  },
};

/** float Clamp to [0, 1] (C# `Math.Clamp(v, 0f, 1f)` on a float). */
function clamp01(v: number): number {
  return fr(Math.max(0, Math.min(1, v)));
}

function applyHeartRate(bpm: number, a: AffectState): void {
  if (bpm > 130) {
    a.energy = clamp01(fr(a.energy + fr(0.1)));
    a.uncertainty = clamp01(fr(a.uncertainty + fr(0.05)));
  } else if (bpm > 100) {
    a.energy = clamp01(fr(a.energy + fr(0.05)));
  } else if (bpm < 50) {
    a.energy = clamp01(fr(a.energy - fr(0.05)));
  }
}

function applyHrv(rmssdMs: number, a: AffectState): void {
  if (rmssdMs < 20) {
    a.uncertainty = clamp01(fr(a.uncertainty + fr(0.05)));
    a.rapport = clamp01(fr(a.rapport - fr(0.02)));
  } else if (rmssdMs > 60) {
    a.engagement = clamp01(fr(a.engagement + fr(0.02)));
  }
}

function applySpO2(percent: number, a: AffectState): void {
  if (percent < 90) {
    a.uncertainty = clamp01(fr(a.uncertainty + fr(0.1)));
  }
}
