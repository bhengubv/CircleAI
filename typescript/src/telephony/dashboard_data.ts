// telephony/dashboard_data.ts
//
// Data model for the built-in voice-loop dashboard — faithful port of
// DashboardData.cs. Hosts can render this via any UI stack (Blazor, ASP.NET MVC,
// the existing BigBruh! console). The contracts here describe what the dashboard
// needs to show: live calls, recent calls, agent health, cost spend, percentile
// latency.
//
// The C# composes the snapshot from injected `Func<...>` feeds → injected
// zero-arg supplier functions here. Float `PauseFalseAlarmRate` uses Math.fround.

import type { CallStatus } from "./primitives.js";
import type { LatencySnapshot } from "./latency_tracker.js";

/** One row in the live-calls panel. Mirrors `LiveCallRow`. */
export interface LiveCallRow {
  readonly callId: string;
  readonly carrier: string;
  readonly from: string;
  readonly to: string;
  readonly status: CallStatus;
  readonly startedAtUtc: Date;
  /** Duration in milliseconds. */
  readonly durationMs: number;
  readonly costSoFar: number;
}

/** One row in the recent-calls panel. Mirrors `RecentCallRow`. */
export interface RecentCallRow {
  readonly callId: string;
  readonly carrier: string;
  readonly from: string;
  readonly to: string;
  readonly finalStatus: CallStatus;
  readonly endedAtUtc: Date;
  /** Duration in milliseconds. */
  readonly durationMs: number;
  readonly totalCost: number;
}

/** Agent health summary row. Mirrors `AgentHealthRow`. */
export interface AgentHealthRow {
  readonly agentLabel: string;
  /** "Healthy" / "Degraded" / "CoolingDown". */
  readonly health: string;
  readonly consecutiveFailures: number;
}

/** Top-of-page summary card. Mirrors `DashboardSummary`. */
export interface DashboardSummary {
  readonly liveCallCount: number;
  readonly currentSpendUsd: number;
  readonly callsLast24h: number;
  readonly pauseFalseAlarmRate: number;
}

/** Constructs a {@link DashboardSummary} (narrows `pauseFalseAlarmRate` to float32). */
export function dashboardSummary(
  liveCallCount: number,
  currentSpendUsd: number,
  callsLast24h: number,
  pauseFalseAlarmRate: number,
): DashboardSummary {
  return {
    liveCallCount,
    currentSpendUsd,
    callsLast24h,
    pauseFalseAlarmRate: Math.fround(pauseFalseAlarmRate),
  };
}

/** Full dashboard snapshot. Mirrors `DashboardSnapshot`. */
export interface DashboardSnapshot {
  readonly summary: DashboardSummary;
  readonly liveCalls: readonly LiveCallRow[];
  readonly recentCalls: readonly RecentCallRow[];
  readonly agentHealth: readonly AgentHealthRow[];
  readonly latencyByStage: readonly LatencySnapshot[];
}

/** Dashboard data source: hosts compose live + recent + health + latency feeds. Mirrors `IDashboardDataSource`. */
export interface IDashboardDataSource {
  snapshotAsync(signal?: AbortSignal): Promise<DashboardSnapshot>;
}

/** Default composed data source — pulls from supplied stores/services. Mirrors `DefaultDashboardDataSource`. */
export class DefaultDashboardDataSource implements IDashboardDataSource {
  private readonly liveCalls: () => readonly LiveCallRow[];
  private readonly recentCalls: () => readonly RecentCallRow[];
  private readonly agentHealth: () => readonly AgentHealthRow[];
  private readonly latency: () => readonly LatencySnapshot[];
  private readonly summary: () => DashboardSummary;

  constructor(
    liveCalls: () => readonly LiveCallRow[],
    recentCalls: () => readonly RecentCallRow[],
    agentHealth: () => readonly AgentHealthRow[],
    latency: () => readonly LatencySnapshot[],
    summary: () => DashboardSummary,
  ) {
    if (liveCalls === null || liveCalls === undefined) throw new Error("liveCalls is required");
    if (recentCalls === null || recentCalls === undefined) throw new Error("recentCalls is required");
    if (agentHealth === null || agentHealth === undefined) throw new Error("agentHealth is required");
    if (latency === null || latency === undefined) throw new Error("latency is required");
    if (summary === null || summary === undefined) throw new Error("summary is required");
    this.liveCalls = liveCalls;
    this.recentCalls = recentCalls;
    this.agentHealth = agentHealth;
    this.latency = latency;
    this.summary = summary;
  }

  snapshotAsync(_signal?: AbortSignal): Promise<DashboardSnapshot> {
    const snap: DashboardSnapshot = {
      summary: this.summary(),
      liveCalls: this.liveCalls(),
      recentCalls: this.recentCalls(),
      agentHealth: this.agentHealth(),
      latencyByStage: this.latency(),
    };
    return Promise.resolve(snap);
  }
}
