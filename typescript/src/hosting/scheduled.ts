// hosting/scheduled.ts
//
// Port of the CircleAI.Hosting scheduled-task surface:
//   • CronJobModels.cs   — DeliveryTarget, CronJobState, CronJob
//   • IScheduledTaskStore.cs / InMemoryScheduledTaskStore.cs
//   • ScheduledAIService.cs — background polling loop + JobCompleted event
//
// The C# background loop uses Task.Run + Task.Delay(30s); the TS port drives
// the loop with setInterval so start/stop are deterministic and testable. A
// single tick (processDueJobsAsync) is exposed so tests can drive one cycle
// without waiting on the timer.

import type { IAIService } from "./service.js";
import type { IScheduledTaskStore } from "./scheduled_store.js";
import { getNextOccurrence } from "./cron_schedule_parser.js";

export type {
  IScheduledTaskStore,
} from "./scheduled_store.js";
export { InMemoryScheduledTaskStore } from "./scheduled_store.js";
export type {
  DeliveryTarget,
  CronJobState,
  CronJob,
} from "./cron_job_models.js";
export {
  DeliveryTargetValues,
  CronJobStateValues,
  cronJob,
} from "./cron_job_models.js";

import type { CronJob } from "./cron_job_models.js";
import { CronJobStateValues } from "./cron_job_models.js";

/**
 * Event data emitted when a scheduled job finishes (success or failure).
 * Mirrors CircleAI.Hosting.JobCompletedEventArgs.
 */
export interface JobCompletedEventArgs {
  /** The job that was executed (with updated state fields). */
  readonly job: CronJob;
  /** The AI response text, or an empty string on failure. */
  readonly response: string;
  /** Non-null when execution failed. */
  readonly error: Error | null;
}

/** Handler for {@link ScheduledAIService.onJobCompleted}. */
export type JobCompletedHandler = (args: JobCompletedEventArgs) => void;

const POLL_INTERVAL_MS = 30_000;

/**
 * Polls {@link IScheduledTaskStore} for due {@link CronJob} records every 30 s,
 * executes each via {@link IAIService.askAsync}, and raises onJobCompleted.
 * Mirrors CircleAI.Hosting.ScheduledAIService.
 */
export class ScheduledAIService {
  private readonly butler: IAIService;
  private readonly store: IScheduledTaskStore;

  private timer: ReturnType<typeof setInterval> | null = null;
  private running = false;
  private ticking = false;

  private readonly handlers = new Set<JobCompletedHandler>();

  constructor(butler: IAIService, store: IScheduledTaskStore) {
    if (!butler) throw new Error("butler required");
    if (!store) throw new Error("store required");
    this.butler = butler;
    this.store = store;
  }

  /**
   * Subscribe to job-completion events. Mirrors the C# `OnJobCompleted` event.
   * Returns an unsubscribe function.
   */
  onJobCompleted(handler: JobCompletedHandler): () => void {
    this.handlers.add(handler);
    return () => this.handlers.delete(handler);
  }

  /** Starts the background polling loop. No-op when already running. */
  async startAsync(): Promise<void> {
    if (this.running) return;
    this.running = true;
    this.timer = setInterval(() => {
      void this.runOnceGuarded();
    }, POLL_INTERVAL_MS);
    // Do not keep the Node event loop alive purely for the poll timer.
    (this.timer as { unref?: () => void }).unref?.();
  }

  /** Signals the polling loop to stop. */
  async stopAsync(): Promise<void> {
    this.running = false;
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  /** Async-dispose equivalent (C# IAsyncDisposable). */
  async disposeAsync(): Promise<void> {
    await this.stopAsync();
  }

  private async runOnceGuarded(): Promise<void> {
    if (this.ticking) return;
    this.ticking = true;
    try {
      await this.processDueJobsAsync();
    } catch {
      /* poll cycle errors are logged in C#; swallow here (best-effort) */
    } finally {
      this.ticking = false;
    }
  }

  /**
   * Runs one poll cycle: fetch due jobs and execute each. Public so tests can
   * drive a single cycle without the 30-second timer.
   */
  async processDueJobsAsync(): Promise<void> {
    const dueJobs = await this.store.getDueJobsAsync();
    if (dueJobs.length === 0) return;

    for (const job of dueJobs) {
      await this.executeJobAsync(job);
    }
  }

  private async executeJobAsync(job: CronJob): Promise<void> {
    const now = new Date();

    // Mark as Running.
    const running: CronJob = { ...job, state: CronJobStateValues.Running };
    await this.store.upsertAsync(running);

    let response = "";
    let error: Error | null = null;

    try {
      response = await this.butler.askAsync(job.prompt);
    } catch (ex) {
      error = ex instanceof Error ? ex : new Error(String(ex));
    }

    const nextRun = computeNextRun(job.cronExpression, now);
    const updatedState = error === null
      ? CronJobStateValues.Succeeded
      : CronJobStateValues.Failed;

    const updated: CronJob = {
      ...job,
      lastRunUtc: now,
      nextRunUtc: nextRun,
      state: updatedState,
    };

    try {
      await this.store.upsertAsync(updated);
    } catch {
      /* persistence failure is logged in C#; non-fatal */
    }

    // Fire event best-effort — subscriber errors must not crash the loop.
    const args: JobCompletedEventArgs = { job: updated, response, error };
    for (const h of this.handlers) {
      try {
        h(args);
      } catch {
        /* subscriber threw; non-fatal */
      }
    }
  }
}

function computeNextRun(cronExpression: string, after: Date): Date | null {
  try {
    return getNextOccurrence(cronExpression, after);
  } catch {
    return null;
  }
}
