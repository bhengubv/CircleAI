/**
 * hosting/triggers
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/triggers.ts
//
// Port of the CircleAI.Hosting proactive-trigger surface:
//   • ITriggerCondition.cs  — ITriggerCondition + ProactiveContext
//   • ScheduleTrigger.cs    — fires once/day in a 5-minute window at a local time
//   • IdleTrigger.cs        — fires when idle longer than a threshold
//
// C# ScheduleTrigger works in local time (NowUtc.LocalDateTime, TimeOnly,
// DateOnly). The TS port uses a wall-clock Date's local getHours/getMinutes/
// getFullYear/getMonth/getDate so behaviour matches DateTimeOffset.LocalDateTime
// on the host. Times are represented as minutes-of-day.

import type { AffectState, Goal } from "../memory/index";

/**
 * Context snapshot passed to trigger conditions. Mirrors
 * CircleAI.Hosting.ProactiveContext.
 */
export interface ProactiveContext {
  /** User being evaluated. */
  readonly userId: string;
  /** Current UTC time. */
  readonly nowUtc: Date;
  /** How long since the user last interacted, in milliseconds. */
  readonly timeSinceLastInteractionMs: number;
  /** Current affect state (may be null if no store is configured). */
  readonly affectState: AffectState | null;
  /** User's currently active goals. */
  readonly activeGoals: readonly Goal[];
}

/**
 * A condition that, when true, signals B! should check in proactively. Mirrors
 * CircleAI.Hosting.ITriggerCondition.
 */
export interface ITriggerCondition {
  /** Stable name used for logging and deduplication. */
  readonly name: string;
  /** Returns true when the condition is currently met. */
  isMetAsync(context: ProactiveContext): Promise<boolean>;
}

/**
 * Fires at a specific time of day, active for a 5-minute window and at most
 * once per calendar day. Mirrors CircleAI.Hosting.ScheduleTrigger.
 */
export class ScheduleTrigger implements ITriggerCondition {
  /** Trigger time as minutes-of-day (0..1439). */
  private readonly triggerMinutes: number;
  /** Last fire date as a local YYYY-MM-DD key, or null. */
  private lastFireDate: string | null = null;

  readonly name: string;

  /**
   * @param triggerHour Local hour of day (0..23).
   * @param triggerMinute Local minute (0..59).
   * @param name Optional stable name. Defaults to "schedule".
   */
  constructor(triggerHour: number, triggerMinute: number, name = "schedule") {
    this.triggerMinutes = triggerHour * 60 + triggerMinute;
    this.name = name;
  }

  /** Trigger time-of-day as minutes past local midnight. */
  get triggerTimeMinutes(): number {
    return this.triggerMinutes;
  }

  async isMetAsync(context: ProactiveContext): Promise<boolean> {
    if (!context) throw new Error("context required");

    // Convert nowUtc to local time for comparison (C# NowUtc.LocalDateTime).
    const local = context.nowUtc;
    const localDate = localDateKey(local);
    const localMinutes = local.getHours() * 60 + local.getMinutes();

    // Already fired today — don't fire again.
    if (this.lastFireDate !== null && this.lastFireDate === localDate)
      return false;

    // Within the 5-minute window after triggerTime?
    const windowStart = this.triggerMinutes;
    const windowEnd = this.triggerMinutes + 5;

    let inWindow: boolean;
    if (windowEnd < 24 * 60) {
      // Normal case — window doesn't wrap midnight.
      inWindow = localMinutes >= windowStart && localMinutes < windowEnd % (24 * 60);
    } else {
      // Window wraps midnight (e.g. 23:58 + 5 min = 00:03).
      inWindow =
        localMinutes >= windowStart || localMinutes < windowEnd % (24 * 60);
    }

    if (!inWindow) return false;

    // In the window — mark fired for today and return true.
    this.lastFireDate = localDate;
    return true;
  }
}

/**
 * Fires when {@link ProactiveContext.timeSinceLastInteractionMs} exceeds the
 * idle threshold. Mirrors CircleAI.Hosting.IdleTrigger.
 */
export class IdleTrigger implements ITriggerCondition {
  private readonly idleThresholdMs: number;

  readonly name = "idle";

  /**
   * @param idleThresholdMs How long the user must be idle before firing.
   *   Defaults to 4 hours.
   */
  constructor(idleThresholdMs: number = 4 * 60 * 60 * 1000) {
    this.idleThresholdMs = idleThresholdMs;
  }

  /** Idle threshold in milliseconds. */
  get idleThreshold(): number {
    return this.idleThresholdMs;
  }

  async isMetAsync(context: ProactiveContext): Promise<boolean> {
    if (!context) throw new Error("context required");
    return context.timeSinceLastInteractionMs > this.idleThresholdMs;
  }
}

function localDateKey(d: Date): string {
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}
