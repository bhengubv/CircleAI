/**
 * hosting/background_worker
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/background_worker.ts
//
// Port of CircleAI.Hosting.BackgroundInferenceWorker — the IHostedService
// adapter that starts/stops the butler with the host lifecycle and honours an
// optional IThermalThrottleService, exposing isPaused while the device is
// thermally throttled (Serious/Critical). The .NET Generic Host lifecycle maps
// to startAsync/stopAsync + disposeAsync here.

import type { IAIService } from "./service";
import {
  type IThermalThrottleService,
  ThermalState,
} from "./thermal";

/**
 * Wraps a {@link IAIService} so it participates in a host lifecycle, honouring
 * an optional thermal throttle service. Mirrors
 * CircleAI.Hosting.BackgroundInferenceWorker.
 */
export class BackgroundInferenceWorker {
  private readonly butler: IAIService;
  private readonly thermal: IThermalThrottleService | null;

  private paused = false;
  private stopped = false;
  private thermalUnsub: (() => void) | null = null;

  constructor(butler: IAIService, thermal: IThermalThrottleService | null = null) {
    if (!butler) throw new Error("butler required");
    this.butler = butler;
    this.thermal = thermal;
  }

  /**
   * True while the device is thermally throttled (Serious/Critical). Callers
   * that queue inference work should check this before submitting.
   */
  get isPaused(): boolean {
    return this.paused;
  }

  async startAsync(): Promise<void> {
    if (this.thermal !== null) {
      this.thermalUnsub = this.thermal.onStateChanged((s) => this.onThermalStateChanged(s));
      this.thermal.startMonitoring();
    }
    await this.butler.startAsync();
  }

  async stopAsync(): Promise<void> {
    if (this.stopped) return;
    this.stopped = true;

    if (this.thermal !== null) {
      this.thermalUnsub?.();
      this.thermalUnsub = null;
      this.thermal.stopMonitoring();
    }

    await this.butler.stopAsync();
  }

  async disposeAsync(): Promise<void> {
    await this.stopAsync();
    await this.butler.disposeAsync();
  }

  private onThermalStateChanged(newState: ThermalState): void {
    const shouldPause = newState >= ThermalState.Serious;
    if (shouldPause && !this.paused) {
      this.paused = true;
    } else if (!shouldPause && this.paused) {
      this.paused = false;
    }
  }
}
