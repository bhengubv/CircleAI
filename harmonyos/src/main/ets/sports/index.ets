/**
 * sports/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// sports/index.ts
// Full-parity port of CircleAI.Sports (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Sports vertical: activities, personal
// bests, training sessions, weekly volume. Plus the static SportsDomainContext.
//
// NOTE: The C# SportsCompanionAdapter (an ICompanionSession LLM-prompt wrapper)
// is intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   enum DistanceKind                → const enum-like DistanceKind (Run=0..Row=4)
//   record                           → readonly interface (+ positional factory)
//   double DistanceKm                → number
//   TimeSpan Duration / Time         → number of milliseconds (a TimeSpan is a
//                                      duration; ms is a faithful carrier and the
//                                      ordering key used by Best()).
//   DateTimeOffset AtUtc / *Utc      → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//   List<Activity> (+lock)           → Activity[] (single-threaded; lock is a no-op)
//
// SEMANTICS PARITY:
//   Log            — throws on null.
//   History        — user's activities, AtUtc descending, take limit (limit<=0 throws).
//   TotalKmThisWeek— sum DistanceKm where AtUtc >= weekStart; weekStart =
//                    now.Date - now.DayOfWeek days (Sunday-based week, C# .Date).
//   Best           — earliest-Duration activity of >= distanceKm for (user,kind);
//                    projected to a PersonalBest at the *requested* distanceKm.
//   Schedule       — throws on null.
//   Complete       — throws on unknown session; sets Completed=true.
//   Upcoming       — user's incomplete sessions with ScheduledUtc >= now(UTC),
//                    ScheduledUtc ascending.

/** Distance-based activity kind. Mirrors C# `DistanceKind` (Run = 0). */
export type DistanceKind = 0 | 1 | 2 | 3 | 4;
/** Frozen value object for {@link DistanceKind} members. */
export const DistanceKind = Object.freeze({
  Run: 0,
  Bike: 1,
  Swim: 2,
  Walk: 3,
  Row: 4,
} as const) satisfies Map<string, DistanceKind>;

/** A logged endurance activity. Mirrors C# `Activity` record. */
export interface Activity {
  readonly activityId: string;
  readonly userId: string;
  readonly kind: DistanceKind;
  readonly distanceKm: number;
  /** Duration as a value in milliseconds (C# `TimeSpan Duration`). */
  readonly durationMs: number;
  /** UTC instant of the activity (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs an {@link Activity}. */
export function activity(
  activityId: string,
  userId: string,
  kind: DistanceKind,
  distanceKm: number,
  durationMs: number,
  atUtc: Date,
): Activity {
  return { activityId, userId, kind, distanceKm, durationMs, atUtc };
}

/** A personal best over a distance. Mirrors C# `PersonalBest` record. */
export interface PersonalBest {
  readonly userId: string;
  readonly kind: DistanceKind;
  readonly distanceKm: number;
  /** Best time as a value in milliseconds (C# `TimeSpan Time`). */
  readonly timeMs: number;
  /** UTC instant the best was achieved (C# `DateTimeOffset AchievedUtc`). */
  readonly achievedUtc: Date;
}

/** Constructs a {@link PersonalBest}. */
export function personalBest(
  userId: string,
  kind: DistanceKind,
  distanceKm: number,
  timeMs: number,
  achievedUtc: Date,
): PersonalBest {
  return { userId, kind, distanceKm, timeMs, achievedUtc };
}

/** A scheduled training session. Mirrors C# `TrainingSession` record. */
export interface TrainingSession {
  readonly sessionId: string;
  readonly userId: string;
  readonly plan: string;
  /** UTC instant the session is scheduled for (C# `DateTimeOffset ScheduledUtc`). */
  readonly scheduledUtc: Date;
  readonly completed: boolean;
}

/** Constructs a {@link TrainingSession}. */
export function trainingSession(
  sessionId: string,
  userId: string,
  plan: string,
  scheduledUtc: Date,
  completed: boolean,
): TrainingSession {
  return { sessionId, userId, plan, scheduledUtc, completed };
}

/** The sports board contract. Mirrors C# `ISportsBoard`. */
export interface ISportsBoard {
  log(a: Activity): void;
  history(userId: string, limit?: number): readonly Activity[];
  totalKmThisWeek(userId: string, kind: DistanceKind, now: Date): number;
  best(userId: string, kind: DistanceKind, distanceKm: number): PersonalBest | undefined;
  schedule(s: TrainingSession): void;
  complete(sessionId: string): void;
  upcoming(userId: string): readonly TrainingSession[];
}

/**
 * Start-of-week (Sunday) at UTC midnight for `now`, matching C#
 * `now.Date.AddDays(-(int)now.DayOfWeek)` (DayOfWeek: Sunday = 0).
 */
function weekStartUtc(now: Date): Date {
  const midnight = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  return new Date(midnight - now.getUTCDay() * 86_400_000);
}

/** Deterministic in-memory {@link ISportsBoard}. */
export class InMemorySportsBoard implements ISportsBoard {
  private readonly activities: Activity[] = [];
  private readonly sessions = new Map<string, TrainingSession>();

  log(a: Activity): void {
    if (a == null) throw new Error("a required");
    this.activities.push(a);
  }

  history(userId: string, limit = 50): readonly Activity[] {
    if (limit <= 0) throw new Error("limit");
    return this.activities
      .filter((a) => a.userId === userId)
      .sort((x, y) => y.atUtc.getTime() - x.atUtc.getTime())
      .slice(0, limit);
  }

  totalKmThisWeek(userId: string, kind: DistanceKind, now: Date): number {
    const weekStart = weekStartUtc(now).getTime();
    return this.activities
      .filter((a) => a.userId === userId && a.kind === kind && a.atUtc.getTime() >= weekStart)
      .reduce((sum, a) => sum + a.distanceKm, 0);
  }

  best(userId: string, kind: DistanceKind, distanceKm: number): PersonalBest | undefined {
    const hit = this.activities
      .filter((a) => a.userId === userId && a.kind === kind && a.distanceKm >= distanceKm)
      .sort((x, y) => x.durationMs - y.durationMs)[0];
    if (hit === undefined) return undefined;
    return { userId, kind, distanceKm, timeMs: hit.durationMs, achievedUtc: hit.atUtc };
  }

  schedule(s: TrainingSession): void {
    if (s == null) throw new Error("s required");
    this.sessions.set(s.sessionId, s);
  }

  complete(sessionId: string): void {
    const s = this.sessions.get(sessionId);
    if (s === undefined) throw new Error(`Unknown session ${sessionId}`);
    this.sessions.set(sessionId, { ...s, completed: true });
  }

  upcoming(userId: string): readonly TrainingSession[] {
    const nowMs = Date.now();
    return [...this.sessions.values()]
      .filter((s) => s.userId === userId && !s.completed && s.scheduledUtc.getTime() >= nowMs)
      .sort((a, b) => a.scheduledUtc.getTime() - b.scheduledUtc.getTime());
  }
}

/**
 * Static domain context for the Sports vertical. Mirrors C#
 * `SportsDomainContext`.
 */
export const SportsDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Sports] Expert sports management and performance assistant. Help with training programme design, athlete nutrition guidance, club administration, fixture scheduling, performance data analysis, and sports event management. Apply periodisation and load management principles. Compliance: WADA anti-doping rules, SASCOC, Sport and Recreation SA, POPIA.",
  complianceFlags: ["WADA", "SASCOC", "Sport_Recreation_SA", "POPIA"] as readonly string[],
  suggestedTools: ["performance_tracker", "analytics", "schedule_manager", "document_editor"] as readonly string[],
} as const;
