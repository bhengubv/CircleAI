// hosting/cron_job_models.ts
//
// Port of CircleAI.Hosting.CronJobModels — DeliveryTarget, CronJobState, and
// the CronJob record. C# enums are numeric (declaration order); the TS port
// keeps that numeric encoding via const maps + string-literal union types so
// values compare identically to the C# ordinals.

/** Delivery channel for a scheduled job's output. Mirrors DeliveryTarget. */
export const DeliveryTargetValues = {
  /** Deliver via in-process IAIObserver callback. */
  Local: 0,
  /** Deliver via push notification (requires IPushNotificationSender). */
  Push: 1,
  /** Deliver as a Telegram message. */
  Telegram: 2,
  /** Deliver via email. */
  Email: 3,
  /** Caller handles delivery via custom callback. */
  Custom: 4,
} as const;
export type DeliveryTarget =
  (typeof DeliveryTargetValues)[keyof typeof DeliveryTargetValues];

/** State of a scheduled job's last execution. Mirrors CronJobState. */
export const CronJobStateValues = {
  /** Job has never run. */
  Pending: 0,
  /** Job is currently executing. */
  Running: 1,
  /** Last run completed without error. */
  Succeeded: 2,
  /** Last run threw an exception or the model returned an error. */
  Failed: 3,
  /** Job has been manually paused and will not fire until re-enabled. */
  Paused: 4,
} as const;
export type CronJobState =
  (typeof CronJobStateValues)[keyof typeof CronJobStateValues];

/**
 * A named, recurring B! task with a cron schedule. Mirrors
 * CircleAI.Hosting.CronJob.
 */
export interface CronJob {
  readonly id: string;
  readonly name: string;
  readonly prompt: string;
  /** Cron expression (5-field: min hour dom month dow). */
  readonly cronExpression: string;
  readonly delivery: DeliveryTarget;
  /** UTC time of last run. null = never run. */
  readonly lastRunUtc: Date | null;
  /** UTC time of next scheduled run. */
  readonly nextRunUtc: Date | null;
  readonly state: CronJobState;
  readonly isEnabled: boolean;
}

/**
 * Constructs a {@link CronJob} with the C# record's positional defaults
 * (lastRunUtc/nextRunUtc null, state Pending, isEnabled true).
 */
export function cronJob(
  id: string,
  name: string,
  prompt: string,
  cronExpression: string,
  delivery: DeliveryTarget,
  lastRunUtc: Date | null = null,
  nextRunUtc: Date | null = null,
  state: CronJobState = CronJobStateValues.Pending,
  isEnabled = true,
): CronJob {
  return {
    id,
    name,
    prompt,
    cronExpression,
    delivery,
    lastRunUtc,
    nextRunUtc,
    state,
    isEnabled,
  };
}
