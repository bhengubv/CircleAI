/**
 * hosting/thermal
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

import * as __platform from "../platform";
// hosting/thermal.ts
//
// Port of CircleAI.Hosting.IThermalThrottleService + ThermalState +
// ThermalThrottleService. The C# implementation samples an OS-specific thermal
// API behind #if platform guards (Android PowerManager, iOS NSProcessInfo,
// Windows WMI, Linux sysfs). The TS port keeps the coarse state machine,
// polling loop, and StateChanged event, and makes the platform read a pluggable
// sampler (default: best-effort Linux sysfs) so it stays deterministic under
// test.

/**
 * Coarse thermal state, ordered coolest → hottest so numeric comparisons are
 * meaningful. Mirrors CircleAI.Hosting.ThermalState.
 */
export enum ThermalState {
  /** State could not be determined. */
  Unknown = 0,
  /** Device is within normal operating temperature. */
  Normal = 1,
  /** Device is slightly warm; performance may be lightly throttled. */
  Fair = 2,
  /** Device is hot; OS may have begun throttling CPU/GPU. */
  Serious = 3,
  /** Device is critically hot; aggressive throttling or shutdown imminent. */
  Critical = 4,
}

/** Handler for {@link IThermalThrottleService.onStateChanged}. */
export type ThermalStateHandler = (state: ThermalState) => void;

/**
 * Polls platform thermal APIs and exposes the current device temperature
 * state. Mirrors CircleAI.Hosting.IThermalThrottleService.
 */
export interface IThermalThrottleService {
  /** Most-recently sampled thermal state. */
  readonly currentState: ThermalState;
  /**
   * True when currentState is Serious or Critical. Inference workers should
   * pause when this returns true.
   */
  readonly shouldPauseInference: boolean;
  /** Subscribe to state transitions. Returns an unsubscribe function. */
  onStateChanged(handler: ThermalStateHandler): () => void;
  /** Starts the background polling loop. No-op while already running. */
  startMonitoring(): void;
  /** Stops the polling loop. The current state is retained. */
  stopMonitoring(): void;
  /** Dispose — stops monitoring and releases the timer. */
  dispose(): void;
}

/** A platform temperature sampler returning a coarse {@link ThermalState}. */
export type ThermalSampler = () => ThermalState;

const POLL_INTERVAL_MS = 10_000;

// Millidegrees-Celsius thresholds used for Linux sysfs readings.
const MILLI_CELSIUS_SERIOUS = 75_000;
const MILLI_CELSIUS_CRITICAL = 90_000;
const LINUX_THERMAL_PATH = "/sys/class/thermal/thermal_zone0/temp";

/**
 * Cross-platform thermal state poller. Mirrors
 * CircleAI.Hosting.ThermalThrottleService. Sampling is injectable; the default
 * best-effort sampler reads Linux sysfs and returns Unknown elsewhere.
 */
export class ThermalThrottleService implements IThermalThrottleService {
  private currentStateRaw: ThermalState = ThermalState.Unknown;
  private timer: ReturnType<typeof setInterval> | null = null;
  private disposed = false;
  private running = false;

  private readonly handlers = new Set<ThermalStateHandler>();
  private readonly sampler: ThermalSampler;

  /**
   * @param sampler Optional platform sampler. Defaults to a Linux sysfs reader
   *   that returns Unknown on non-Linux hosts.
   */
  constructor(sampler?: ThermalSampler) {
    this.sampler = sampler ?? defaultLinuxSampler;
  }

  get currentState(): ThermalState {
    return this.currentStateRaw;
  }

  get shouldPauseInference(): boolean {
    return this.currentState >= ThermalState.Serious;
  }

  onStateChanged(handler: ThermalStateHandler): () => void {
    this.handlers.add(handler);
    return () => this.handlers.delete(handler);
  }

  startMonitoring(): void {
    if (this.disposed) throw new Error("ThermalThrottleService is disposed.");
    // Ensure only one polling loop runs at a time.
    if (this.running) return;
    this.running = true;

    // Sample immediately so callers get a valid state before the first tick.
    this.applyNewState(this.sampleThermalState());

    this.timer = setInterval(() => {
      this.applyNewState(this.sampleThermalState());
    }, POLL_INTERVAL_MS);
    (this.timer as { unref?: () => void }).unref?.();
  }

  stopMonitoring(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
    this.running = false;
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.stopMonitoring();
  }

  private applyNewState(newState: ThermalState): void {
    const previous = this.currentStateRaw;
    this.currentStateRaw = newState;
    if (previous !== newState) {
      for (const h of this.handlers) {
        try {
          h(newState);
        } catch {
          /* handler threw; non-fatal */
        }
      }
    }
  }

  private sampleThermalState(): ThermalState {
    try {
      return this.sampler();
    } catch {
      return ThermalState.Unknown;
    }
  }
}

/** Best-effort Linux sysfs sampler (millidegrees Celsius). */
function defaultLinuxSampler(): ThermalState {
  // Lazy require so bundlers targeting the browser don't choke on node:fs.
  let fs: typeof import("../platform");
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    fs = __platform;
  } catch {
    return ThermalState.Unknown;
  }
  if (!fs.existsSync(LINUX_THERMAL_PATH)) return ThermalState.Unknown;
  try {
    const text = fs.readFileSync(LINUX_THERMAL_PATH, "utf8").trim();
    if (!/^[+-]?\d+$/.test(text)) return ThermalState.Unknown;
    const milliCelsius = Number.parseInt(text, 10);
    if (milliCelsius > MILLI_CELSIUS_CRITICAL) return ThermalState.Critical;
    if (milliCelsius > MILLI_CELSIUS_SERIOUS) return ThermalState.Serious;
    return ThermalState.Normal;
  } catch {
    return ThermalState.Unknown;
  }
}
