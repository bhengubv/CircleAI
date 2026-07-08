// hosting/warmup.ts
//
// Port of the CircleAI.Hosting.Warmup surface (RT-07):
//   • IRequestPredictor.cs / ArrivalForecast
//   • HistogramRequestPredictor.cs — per-minute-of-day EWMA arrival histogram
//   • PredictiveWarmupOptions.cs
//   • PredictiveWarmupController.cs — polls the predictor, pre-warms on a spike
//
// All arithmetic is C# `double`; there are no `float` sites, so no Math.fround
// is required. The controller's background loop is driven by setInterval; a
// single-cycle tickAsync is exposed for tests.

import type { IAIService } from "./service.js";

/**
 * Forecast of inbound requests over a window. Mirrors
 * CircleAI.Hosting.Warmup.ArrivalForecast.
 */
export interface ArrivalForecast {
  /** 0.0 .. 1.0 — chance of at least one arrival in the window. */
  readonly probabilityOfArrival: number;
  /** Best estimate of how many arrivals to expect. */
  readonly expectedCount: number;
  /** 0.0 .. 1.0 — trustworthiness given sample size; ~0 at cold-start. */
  readonly confidence: number;
}

/**
 * Local-only predictor that learns request arrival timing. Mirrors
 * CircleAI.Hosting.Warmup.IRequestPredictor.
 */
export interface IRequestPredictor {
  /** Record one arrival at `utc`. */
  recordArrival(utc: Date): void;
  /** Forecast arrivals over `forecastWindowMs` starting at `utcNow`. */
  predict(utcNow: Date, forecastWindowMs: number): ArrivalForecast;
  /** Total arrivals observed since construction. */
  readonly observedArrivals: number;
}

const MINUTES_PER_DAY = 24 * 60;
const WARM_CONFIDENCE = 1.0;
const MIN_SAMPLES_FOR_FULL_CONFIDENCE = 25;

/**
 * Default {@link IRequestPredictor} — a rolling per-minute-of-day EWMA arrival
 * histogram. Mirrors CircleAI.Hosting.Warmup.HistogramRequestPredictor.
 */
export class HistogramRequestPredictor implements IRequestPredictor {
  private readonly historyDays: number;
  private readonly perMinuteRate: Float64Array;
  private readonly perMinuteCount: Int32Array;
  private observed = 0;

  constructor(historyDays = 7) {
    if (historyDays <= 0) throw new Error("historyDays must be positive.");
    this.historyDays = historyDays;
    this.perMinuteRate = new Float64Array(MINUTES_PER_DAY);
    this.perMinuteCount = new Int32Array(MINUTES_PER_DAY);
  }

  get observedArrivals(): number {
    return this.observed;
  }

  recordArrival(utc: Date): void {
    const minute = utc.getUTCHours() * 60 + utc.getUTCMinutes();
    const cnt = ++this.perMinuteCount[minute];
    // EWMA over the last `historyDays` observations at this slot.
    const alpha = 2.0 / (Math.min(cnt, this.historyDays) + 1);
    this.perMinuteRate[minute] =
      alpha * 1.0 + (1 - alpha) * this.perMinuteRate[minute];
    this.observed++;
  }

  predict(utcNow: Date, forecastWindowMs: number): ArrivalForecast {
    if (forecastWindowMs <= 0)
      return { probabilityOfArrival: 0, expectedCount: 0, confidence: 0 };
    if (this.observed === 0)
      return { probabilityOfArrival: 0, expectedCount: 0, confidence: 0 };

    const minute = utcNow.getUTCHours() * 60 + utcNow.getUTCMinutes();
    const minutes = Math.max(1, Math.ceil(forecastWindowMs / 60000));
    let expected = 0;
    let coveredSamples = 0;
    for (let i = 0; i < minutes; i++) {
      const idx = (minute + i) % MINUTES_PER_DAY;
      expected += this.perMinuteRate[idx];
      coveredSamples += this.perMinuteCount[idx];
    }

    // Poisson tail: P(>=1 arrival) = 1 - exp(-lambda).
    const probability = 1.0 - Math.exp(-expected);
    const confidence = Math.min(
      WARM_CONFIDENCE,
      coveredSamples / (MIN_SAMPLES_FOR_FULL_CONFIDENCE * minutes),
    );
    return {
      probabilityOfArrival: probability,
      expectedCount: expected,
      confidence,
    };
  }

  /** Test-only — wipe state. */
  resetForTests(): void {
    this.perMinuteRate.fill(0);
    this.perMinuteCount.fill(0);
    this.observed = 0;
  }
}

/**
 * Configuration for {@link PredictiveWarmupController}. Mirrors
 * CircleAI.Hosting.Warmup.PredictiveWarmupOptions. Durations are milliseconds.
 */
export interface PredictiveWarmupOptions {
  /** Opt-in to pre-warming. Default false. */
  readonly enabled?: boolean;
  /** How often to poll the predictor, ms. Default 30 000. */
  readonly pollIntervalMs?: number;
  /** How far ahead to forecast, ms. Default 60 000. */
  readonly forecastWindowMs?: number;
  /** Pre-warm when probability × confidence ≥ this. Default 0.5. */
  readonly warmupThreshold?: number;
  /** Minimum delay between consecutive pre-warm calls, ms. Default 300 000. */
  readonly minTimeBetweenWarmupsMs?: number;
}

/** Resolved defaults for {@link PredictiveWarmupOptions}. */
export const DEFAULT_PREDICTIVE_WARMUP_OPTIONS: Required<PredictiveWarmupOptions> = {
  enabled: false,
  pollIntervalMs: 30_000,
  forecastWindowMs: 60_000,
  warmupThreshold: 0.5,
  minTimeBetweenWarmupsMs: 5 * 60_000,
};

/**
 * Background loop that polls an {@link IRequestPredictor} and triggers
 * {@link IAIService.prewarmAsync} before predicted spikes. Mirrors
 * CircleAI.Hosting.Warmup.PredictiveWarmupController.
 */
export class PredictiveWarmupController {
  private readonly service: IAIService;
  private readonly predictor: IRequestPredictor;
  private readonly options: Required<PredictiveWarmupOptions>;
  private readonly clock: () => Date;

  private timer: ReturnType<typeof setInterval> | null = null;
  private started = false;
  private disposed = false;
  private lastWarmupMs = Number.NEGATIVE_INFINITY;

  constructor(
    service: IAIService,
    predictor: IRequestPredictor,
    options: PredictiveWarmupOptions = {},
    clock?: () => Date,
  ) {
    if (!service) throw new Error("service required");
    if (!predictor) throw new Error("predictor required");
    this.service = service;
    this.predictor = predictor;
    this.options = { ...DEFAULT_PREDICTIVE_WARMUP_OPTIONS, ...options };
    this.clock = clock ?? (() => new Date());
  }

  /** Begin polling. No-op when disabled or already started. */
  async startAsync(): Promise<void> {
    if (this.disposed) throw new Error("PredictiveWarmupController is disposed.");
    if (!this.options.enabled || this.started) return;
    this.started = true;
    // Run one tick immediately (mirrors the C# do/while loop).
    await this.tickAsync();
    this.timer = setInterval(() => {
      void this.tickAsync();
    }, this.options.pollIntervalMs);
    (this.timer as { unref?: () => void }).unref?.();
  }

  /** Record a request arrival on the predictor at "now". */
  notifyArrival(): void {
    this.predictor.recordArrival(this.clock());
  }

  /**
   * One prediction + decide-and-maybe-warm cycle. Returns true when warmup
   * fired. Public for tests and manual poking.
   */
  async tickAsync(): Promise<boolean> {
    const now = this.clock();
    const forecast = this.predictor.predict(now, this.options.forecastWindowMs);
    const score = forecast.probabilityOfArrival * forecast.confidence;
    if (score < this.options.warmupThreshold) return false;
    if (now.getTime() - this.lastWarmupMs < this.options.minTimeBetweenWarmupsMs)
      return false;

    try {
      this.lastWarmupMs = now.getTime();
      await this.service.prewarmAsync();
      return true;
    } catch {
      return false;
    }
  }

  async disposeAsync(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }
}
