// memory/runtime/index.ts
//
// CompanionRuntime — the host orchestrator that ticks the consolidator on a
// schedule, keeps the sync engine running, and exposes a single ingestion
// entry point for multimodal artefacts. Ported from
// CircleAI.Memory/Runtime/ (C#) at full parity.
//
// C# implements IHostedService (StartAsync/StopAsync). TypeScript has no such
// abstraction, so — following the ProactiveSchedulerBackgroundService port —
// the lifecycle is modelled as startAsync()/stopAsync() driving periodic loops
// under an AbortController, and the ILogger is replaced by an injectable
// onEvent/onError callback pair (both default to no-ops → deterministic).

import { SleepKind } from "../consolidation.js";
import type { ConsolidationOutcome, IMemoryConsolidator } from "../consolidation.js";
import {
  MediaModality,
  type IngestionResult,
  type MultimodalMemoryIngester,
} from "../multimodal.js";
import type { ICompanionStateSyncEngine } from "../sync/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// CompanionRuntimeOptions (CompanionRuntimeOptions.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Configuration for {@link CompanionRuntime}. All intervals are in
 * milliseconds (C# uses TimeSpan). Every value has a sensible default so a host
 * can construct the runtime with no options and get a working pipeline.
 * Setting any interval to 0 disables that loop.
 */
export interface CompanionRuntimeOptions {
  /**
   * Cadence for the daily-tier consolidation pass. Default: every 6 hours.
   * 0 disables automatic daily ticks.
   */
  readonly dailyTickIntervalMs?: number;
  /** Cadence for the weekly-tier consolidation pass. Default: every 24 hours. */
  readonly weeklyTickIntervalMs?: number;
  /** Cadence for the monthly-tier (persona-delta) pass. Default: every 48 hours. */
  readonly monthlyTickIntervalMs?: number;
  /**
   * Cadence at which the runtime broadcasts its sync state vector to peers.
   * Default: every 5 minutes. 0 disables periodic sync (the engine still
   * responds to inbound envelopes; only the initiating Announce is suppressed).
   */
  readonly syncBroadcastIntervalMs?: number;
  /**
   * Initial delay before the first consolidator tick after startAsync.
   * Default: 30 seconds. Keeps startup quiet.
   */
  readonly initialDelayMs?: number;
  /**
   * When true, the runtime runs an OnDemand consolidation pass during
   * startAsync to catch up anything pending before the timer cadence kicks in.
   * Default: true.
   */
  readonly catchUpOnStart?: boolean;
}

/** Default option values — the resolved shape used internally. */
interface ResolvedRuntimeOptions {
  readonly dailyTickIntervalMs: number;
  readonly weeklyTickIntervalMs: number;
  readonly monthlyTickIntervalMs: number;
  readonly syncBroadcastIntervalMs: number;
  readonly initialDelayMs: number;
  readonly catchUpOnStart: boolean;
}

const HOUR_MS = 60 * 60 * 1000;
const MINUTE_MS = 60 * 1000;

/** The C# CompanionRuntimeOptions defaults, expressed in milliseconds. */
export const DEFAULT_COMPANION_RUNTIME_OPTIONS: ResolvedRuntimeOptions = {
  dailyTickIntervalMs: 6 * HOUR_MS,
  weeklyTickIntervalMs: 24 * HOUR_MS,
  monthlyTickIntervalMs: 48 * HOUR_MS,
  syncBroadcastIntervalMs: 5 * MINUTE_MS,
  initialDelayMs: 30 * 1000,
  catchUpOnStart: true,
};

function resolveOptions(o: CompanionRuntimeOptions | undefined): ResolvedRuntimeOptions {
  const d = DEFAULT_COMPANION_RUNTIME_OPTIONS;
  return {
    dailyTickIntervalMs: o?.dailyTickIntervalMs ?? d.dailyTickIntervalMs,
    weeklyTickIntervalMs: o?.weeklyTickIntervalMs ?? d.weeklyTickIntervalMs,
    monthlyTickIntervalMs: o?.monthlyTickIntervalMs ?? d.monthlyTickIntervalMs,
    syncBroadcastIntervalMs: o?.syncBroadcastIntervalMs ?? d.syncBroadcastIntervalMs,
    initialDelayMs: o?.initialDelayMs ?? d.initialDelayMs,
    catchUpOnStart: o?.catchUpOnStart ?? d.catchUpOnStart,
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// Injectable observability seam (replaces C# ILogger)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Optional callbacks the runtime uses in lieu of C#'s ILogger. Both default to
 * no-ops so the runtime is deterministic and silent unless a host opts in.
 */
export interface CompanionRuntimeObserver {
  /** Informational lifecycle event (start, stop, tick outcomes). */
  onEvent?(message: string): void;
  /** A non-fatal failure inside a background loop. */
  onError?(message: string, error: unknown): void;
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionRuntime dependencies
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Constructor dependencies for {@link CompanionRuntime}. The consolidator is
 * required; the sync engine and ingester are optional (the runtime gracefully
 * skips whichever subsystem is absent — a text-only host may wire neither).
 */
export interface CompanionRuntimeDeps {
  readonly consolidator: IMemoryConsolidator;
  readonly options?: CompanionRuntimeOptions;
  readonly syncEngine?: ICompanionStateSyncEngine | null;
  readonly ingester?: MultimodalMemoryIngester | null;
  readonly observer?: CompanionRuntimeObserver;
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionRuntime (CompanionRuntime.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Owns the lifecycle of the memory pipeline (consolidator, sync engine,
 * multimodal ingester) and ticks the consolidation passes on a configurable
 * schedule.
 */
export class CompanionRuntime {
  private readonly consolidator: IMemoryConsolidator;
  private readonly syncEngine: ICompanionStateSyncEngine | null;
  private readonly ingester: MultimodalMemoryIngester | null;
  private readonly options: ResolvedRuntimeOptions;
  private readonly observer: CompanionRuntimeObserver;

  private controller: AbortController | null = null;
  private dailyLoop: Promise<void> | null = null;
  private weeklyLoop: Promise<void> | null = null;
  private monthlyLoop: Promise<void> | null = null;
  private syncLoop: Promise<void> | null = null;

  constructor(deps: CompanionRuntimeDeps) {
    if (!deps || !deps.consolidator) throw new Error("consolidator required");
    this.consolidator = deps.consolidator;
    this.syncEngine = deps.syncEngine ?? null;
    this.ingester = deps.ingester ?? null;
    this.options = resolveOptions(deps.options);
    this.observer = deps.observer ?? {};
  }

  // ── Lifecycle (IHostedService equivalent) ───────────────────────────────────

  /** Starts the sync engine (if any), optionally catches up, and arms loops. */
  async startAsync(): Promise<void> {
    this.logEvent("CompanionRuntime starting.");
    this.controller = new AbortController();
    const signal = this.controller.signal;

    if (this.syncEngine !== null) {
      await this.syncEngine.startAsync();
      this.logEvent("Sync engine started.");
    }

    if (this.options.catchUpOnStart) {
      try {
        const outcome = await this.consolidator.tickAsync(SleepKind.OnDemand);
        this.logEvent(
          `Catch-up consolidation: daily=${outcome.dailySummariesProduced} ` +
            `weekly=${outcome.semanticClustersProduced} ` +
            `monthly=${outcome.personaDeltasProduced} core=${outcome.corePromotions}.`,
        );
      } catch (ex) {
        this.logError("Catch-up consolidation failed (non-fatal).", ex);
      }
    }

    if (this.options.dailyTickIntervalMs > 0) {
      this.dailyLoop = this.runPeriodic(SleepKind.Daily, this.options.dailyTickIntervalMs, signal);
    }
    if (this.options.weeklyTickIntervalMs > 0) {
      this.weeklyLoop = this.runPeriodic(SleepKind.Weekly, this.options.weeklyTickIntervalMs, signal);
    }
    if (this.options.monthlyTickIntervalMs > 0) {
      this.monthlyLoop = this.runPeriodic(SleepKind.Monthly, this.options.monthlyTickIntervalMs, signal);
    }
    if (this.syncEngine !== null && this.options.syncBroadcastIntervalMs > 0) {
      this.syncLoop = this.runSyncBroadcasts(this.options.syncBroadcastIntervalMs, signal);
    }

    this.logEvent("CompanionRuntime started.");
  }

  /** Cancels every loop, awaits their exit, then disposes the sync engine. */
  async stopAsync(): Promise<void> {
    this.logEvent("CompanionRuntime stopping.");
    if (this.controller !== null) {
      try {
        this.controller.abort();
      } catch {
        /* ignore */
      }
    }

    await safeAwait(this.dailyLoop);
    await safeAwait(this.weeklyLoop);
    await safeAwait(this.monthlyLoop);
    await safeAwait(this.syncLoop);
    this.dailyLoop = null;
    this.weeklyLoop = null;
    this.monthlyLoop = null;
    this.syncLoop = null;
    this.controller = null;

    if (this.syncEngine !== null) {
      await this.syncEngine.disposeAsync();
    }

    this.logEvent("CompanionRuntime stopped.");
  }

  // ── Public helpers ──────────────────────────────────────────────────────────

  /**
   * Triggers an OnDemand consolidation pass. Hosts call this after large chunks
   * of new activity (e.g. end of a long conversation) when they don't want to
   * wait for the timer.
   */
  consolidateNowAsync(): Promise<ConsolidationOutcome> {
    return this.consolidator.tickAsync(SleepKind.OnDemand);
  }

  /**
   * Forwards multimodal ingestion to the registered ingester. Throws when no
   * ingester was wired (the runtime can be wired without one for text-only
   * hosts).
   */
  ingestMediaAsync(
    modality: MediaModality,
    sourceBytes: Uint8Array,
    mimeType?: string | null,
    sourceUri?: string | null,
    tags?: Record<string, string> | null,
  ): Promise<IngestionResult> {
    if (this.ingester === null) {
      throw new Error("CompanionRuntime was constructed without a MultimodalMemoryIngester.");
    }
    return this.ingester.ingestAsync(modality, sourceBytes, {
      mimeType: mimeType ?? null,
      sourceUri: sourceUri ?? null,
      tags: tags ?? null,
    });
  }

  /** Forces an immediate sync broadcast. No-op when sync isn't wired. */
  syncNowAsync(): Promise<void> {
    return this.syncEngine?.syncNowAsync() ?? Promise.resolve();
  }

  // ── Internals ────────────────────────────────────────────────────────────────

  private async runPeriodic(kind: SleepKind, intervalMs: number, signal: AbortSignal): Promise<void> {
    try {
      await delay(this.options.initialDelayMs, signal);
      while (!signal.aborted) {
        try {
          const outcome = await this.consolidator.tickAsync(kind);
          if (
            outcome.dailySummariesProduced +
              outcome.semanticClustersProduced +
              outcome.personaDeltasProduced +
              outcome.corePromotions >
            0
          ) {
            this.logEvent(
              `Consolidation tick ${kind}: daily=${outcome.dailySummariesProduced} ` +
                `weekly=${outcome.semanticClustersProduced} ` +
                `monthly=${outcome.personaDeltasProduced} core=${outcome.corePromotions}.`,
            );
          }
        } catch (ex) {
          if (signal.aborted) return;
          this.logError(`Consolidation tick ${kind} failed.`, ex);
        }
        await delay(intervalMs, signal);
      }
    } catch {
      // Graceful — aborted delay throws, which we treat as a clean stop.
    }
  }

  private async runSyncBroadcasts(intervalMs: number, signal: AbortSignal): Promise<void> {
    try {
      await delay(this.options.initialDelayMs, signal);
      while (!signal.aborted) {
        try {
          await this.syncEngine!.syncNowAsync();
        } catch (ex) {
          if (signal.aborted) return;
          this.logError("Sync broadcast failed.", ex);
        }
        await delay(intervalMs, signal);
      }
    } catch {
      // Graceful.
    }
  }

  private logEvent(message: string): void {
    this.observer.onEvent?.(message);
  }

  private logError(message: string, error: unknown): void {
    this.observer.onError?.(message, error);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

async function safeAwait(t: Promise<void> | null): Promise<void> {
  if (t === null) return;
  try {
    await t;
  } catch {
    /* logged earlier / graceful */
  }
}

/**
 * Cancellable delay. Rejects (with an abort error) if the signal fires before
 * the timer elapses — the loops treat that rejection as a clean stop. The
 * timer is unref'd so a pending delay never keeps the process alive.
 */
function delay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal.aborted) {
      reject(new Error("aborted"));
      return;
    }
    const t = setTimeout(() => {
      cleanup();
      resolve();
    }, ms);
    if (typeof t.unref === "function") t.unref();
    const onAbort = (): void => {
      clearTimeout(t);
      cleanup();
      reject(new Error("aborted"));
    };
    const cleanup = (): void => signal.removeEventListener("abort", onAbort);
    signal.addEventListener("abort", onAbort, { once: true });
  });
}
