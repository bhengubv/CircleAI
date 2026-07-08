// proactive/background_service.ts
//
// ProactiveSchedulerBackgroundService.cs — refreshes the scheduler once at
// start, then loops on a one-minute timer calling tickAsync, re-snapshotting the
// source every RefreshInterval. Modelled here as a start/stop service (there is
// no IHostedService in TypeScript): `start()` kicks off the ExecuteAsync loop
// under an AbortController; `stop()` aborts it and awaits the loop.

import type { IProactiveScheduler } from "./contracts.js";

/** Tunable knobs for the background tick loop. Mirrors ProactiveSchedulerOptions. */
export interface ProactiveSchedulerOptions {
  /** How often the scheduler ticks (ms). Default 1 minute. */
  readonly tickIntervalMs?: number;
  /** How often the source is re-snapshotted (ms). Default 5 minutes. */
  readonly refreshIntervalMs?: number;
}

const DEFAULT_TICK_MS = 60_000;
const DEFAULT_REFRESH_MS = 5 * 60_000;

/**
 * Hosted-service equivalent that drives the scheduler. Mirrors
 * ProactiveSchedulerBackgroundService.
 */
export class ProactiveSchedulerBackgroundService {
  private readonly scheduler: IProactiveScheduler;
  private readonly tickIntervalMs: number;
  private readonly refreshIntervalMs: number;
  private readonly onError?: (message: string, error: unknown) => void;

  private controller: AbortController | null = null;
  private loop: Promise<void> | null = null;

  constructor(
    scheduler: IProactiveScheduler,
    options: ProactiveSchedulerOptions = {},
    onError?: (message: string, error: unknown) => void,
  ) {
    if (scheduler == null) throw new Error("scheduler required");
    this.scheduler = scheduler;
    this.tickIntervalMs = options.tickIntervalMs ?? DEFAULT_TICK_MS;
    this.refreshIntervalMs = options.refreshIntervalMs ?? DEFAULT_REFRESH_MS;
    this.onError = onError;
  }

  /** Begin the refresh/tick loop. Idempotent while running. */
  start(): void {
    if (this.controller !== null) return;
    this.controller = new AbortController();
    this.loop = this.executeAsync(this.controller.signal);
  }

  /** Signal cancellation and await the loop's exit. */
  async stop(): Promise<void> {
    if (this.controller === null) return;
    this.controller.abort();
    const loop = this.loop;
    this.controller = null;
    this.loop = null;
    if (loop) {
      try {
        await loop;
      } catch {
        /* expected */
      }
    }
  }

  private async executeAsync(signal: AbortSignal): Promise<void> {
    // Initial refresh — populate the scheduler before the first tick.
    try {
      await this.scheduler.refreshAsync(signal);
    } catch (ex) {
      if (signal.aborted) return;
      this.onError?.("Initial proactive scheduler refresh failed.", ex);
    }

    let lastRefresh = Date.now();

    while (!signal.aborted) {
      try {
        await delay(this.tickIntervalMs, signal);
      } catch {
        return;
      }

      const now = Date.now();
      try {
        if (now - lastRefresh >= this.refreshIntervalMs) {
          await this.scheduler.refreshAsync(signal);
          lastRefresh = now;
        }
        await this.scheduler.tickAsync(new Date(now), signal);
      } catch (ex) {
        if (signal.aborted) return;
        this.onError?.("Proactive scheduler tick failed; will retry on next interval.", ex);
      }
    }
  }
}

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
