// telephony/false_interruption_tracker.ts
//
// Counts how often the barge-in controller paused and then resumed (false
// alarm) versus cancelled (real interruption) — faithful port of
// FalseInterruptionTracker.cs. High false-alarm rates suggest the VAD threshold
// is too sensitive.
//
// `Interlocked` counters → plain numeric fields (single-threaded). The float
// `FalseAlarmRate` uses Math.fround to match C# `float`.

import type { BargeInTransition } from "./barge_in_controller.js";
import { BargeInState } from "./barge_in_controller.js";

/** Counters for false-interruption monitoring. Mirrors `InterruptionStats`. */
export interface InterruptionStats {
  readonly totalPauseEvents: number;
  readonly confirmedBargeIns: number;
  readonly falseAlarms: number;
  readonly falseAlarmRate: number;
}

/** Tracks barge-in transitions and surfaces a false-alarm rate. Mirrors `IFalseInterruptionTracker`. */
export interface IFalseInterruptionTracker {
  /** Record one transition emitted by {@link BargeInController}. */
  record(transition: BargeInTransition): void;
  /** Current cumulative stats. */
  getStats(): InterruptionStats;
  /** Reset all counters. */
  reset(): void;
}

/** Default in-memory tracker. Mirrors `InMemoryFalseInterruptionTracker`. */
export class InMemoryFalseInterruptionTracker implements IFalseInterruptionTracker {
  private totalPauses = 0;
  private confirmed = 0;
  private falseAlarmsCount = 0;

  record(transition: BargeInTransition): void {
    if (transition === null || transition === undefined) throw new Error("transition is required");
    switch (transition.to) {
      case BargeInState.Paused:
        this.totalPauses += 1;
        break;
      case BargeInState.Cancelled:
        this.confirmed += 1;
        break;
      case BargeInState.Resumed:
        this.falseAlarmsCount += 1;
        break;
    }
  }

  getStats(): InterruptionStats {
    const rate = this.totalPauses > 0 ? Math.fround(this.falseAlarmsCount / this.totalPauses) : 0;
    return {
      totalPauseEvents: this.totalPauses,
      confirmedBargeIns: this.confirmed,
      falseAlarms: this.falseAlarmsCount,
      falseAlarmRate: rate,
    };
  }

  reset(): void {
    this.totalPauses = 0;
    this.confirmed = 0;
    this.falseAlarmsCount = 0;
  }
}
