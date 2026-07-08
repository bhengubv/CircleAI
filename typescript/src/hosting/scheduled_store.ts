// hosting/scheduled_store.ts
//
// Port of CircleAI.Hosting.IScheduledTaskStore + InMemoryScheduledTaskStore.
// The C# implementation is backed by a ConcurrentDictionary keyed on job id;
// the TS port uses a Map. GetDueJobs returns enabled jobs whose nextRunUtc is
// at or before "now".

import type { CronJob } from "./cron_job_models.js";

/**
 * Persistence abstraction for {@link CronJob} records. Mirrors
 * CircleAI.Hosting.IScheduledTaskStore.
 */
export interface IScheduledTaskStore {
  /** Returns every registered job, regardless of enabled state. */
  listAsync(): Promise<readonly CronJob[]>;
  /** Returns the job with the given id, or null if not found. */
  getAsync(id: string): Promise<CronJob | null>;
  /** Inserts or replaces the job identified by CronJob.id. Returns the stored record. */
  upsertAsync(job: CronJob): Promise<CronJob>;
  /** Removes the job with the given id. No-op if it does not exist. */
  deleteAsync(id: string): Promise<void>;
  /** Returns all enabled jobs whose nextRunUtc is in the past (<= now). */
  getDueJobsAsync(): Promise<readonly CronJob[]>;
}

/**
 * In-memory {@link IScheduledTaskStore}. All state is lost on process exit.
 * Mirrors CircleAI.Hosting.InMemoryScheduledTaskStore.
 */
export class InMemoryScheduledTaskStore implements IScheduledTaskStore {
  private readonly store = new Map<string, CronJob>();

  async listAsync(): Promise<readonly CronJob[]> {
    return [...this.store.values()];
  }

  async getAsync(id: string): Promise<CronJob | null> {
    if (id == null || id.trim().length === 0) throw new Error("id required");
    return this.store.get(id) ?? null;
  }

  async upsertAsync(job: CronJob): Promise<CronJob> {
    if (!job) throw new Error("job required");
    this.store.set(job.id, job);
    return job;
  }

  async deleteAsync(id: string): Promise<void> {
    if (id == null || id.trim().length === 0) throw new Error("id required");
    this.store.delete(id);
  }

  async getDueJobsAsync(): Promise<readonly CronJob[]> {
    const now = Date.now();
    return [...this.store.values()].filter(
      (j) => j.isEnabled && j.nextRunUtc != null && j.nextRunUtc.getTime() <= now,
    );
  }
}
