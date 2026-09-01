/**
 * fitness/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// fitness/index.ts
// Full-parity port of CircleAI.Fitness (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Fitness vertical: workouts, goals,
// exercise sets, weekly workouts + calorie rollups. Plus the static
// FitnessDomainContext.
//
// NOTE: The C# FitnessCompanionAdapter (an ICompanionSession LLM-prompt wrapper)
// is intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   int DurationMinutes / Reps       → number
//   double CaloriesBurned / *        → number
//   DateTimeOffset AtUtc             → Date
//   DateTime DueOn                   → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   WorkoutsThisWeek — user's workouts with AtUtc >= weekStart (Sunday-based,
//                      C# now.Date - now.DayOfWeek), AtUtc ascending.
//   TotalCaloriesSince — sum CaloriesBurned for user's workouts with AtUtc >= since.
//   GoalsFor         — goals for the user (insertion order over the map values).
//   SetsFor          — sets for the workout (insertion order).

/** A logged workout. Mirrors C# `Workout` record. */
export interface Workout {
  readonly workoutId: string;
  readonly userId: string;
  readonly kind: string;
  readonly durationMinutes: number;
  readonly caloriesBurned: number;
  /** UTC instant of the workout (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link Workout}. */
export function workout(
  workoutId: string,
  userId: string,
  kind: string,
  durationMinutes: number,
  caloriesBurned: number,
  atUtc: Date,
): Workout {
  return { workoutId, userId, kind, durationMinutes, caloriesBurned, atUtc };
}

/** A fitness goal. Mirrors C# `FitnessGoal` record. */
export interface FitnessGoal {
  readonly goalId: string;
  readonly userId: string;
  readonly metric: string;
  readonly target: number;
  /** Due date (C# `DateTime DueOn`). */
  readonly dueOn: Date;
}

/** Constructs a {@link FitnessGoal}. */
export function fitnessGoal(
  goalId: string,
  userId: string,
  metric: string,
  target: number,
  dueOn: Date,
): FitnessGoal {
  return { goalId, userId, metric, target, dueOn };
}

/** A single set within a workout. Mirrors C# `ExerciseSet` record. */
export interface ExerciseSet {
  readonly setId: string;
  readonly workoutId: string;
  readonly exercise: string;
  readonly reps: number;
  readonly weightKg: number;
}

/** Constructs an {@link ExerciseSet}. */
export function exerciseSet(
  setId: string,
  workoutId: string,
  exercise: string,
  reps: number,
  weightKg: number,
): ExerciseSet {
  return { setId, workoutId, exercise, reps, weightKg };
}

/** The fitness board contract. Mirrors C# `IFitnessBoard`. */
export interface IFitnessBoard {
  log(w: Workout): void;
  workoutsThisWeek(userId: string, now: Date): readonly Workout[];
  totalCaloriesSince(userId: string, since: Date): number;
  setGoal(g: FitnessGoal): void;
  goalsFor(userId: string): readonly FitnessGoal[];
  addSet(s: ExerciseSet): void;
  setsFor(workoutId: string): readonly ExerciseSet[];
  /** Total number of workouts logged. */
  readonly workoutCount: number;
  /** A user's workouts of a given kind (case-insensitive), newest first. */
  workoutsByKind(userId: string, kind: string): readonly Workout[];
  /** Remove a goal by id. Returns whether one was removed. */
  removeGoal(goalId: string): boolean;
  /** The user's soonest-due goal for a given metric (case-insensitive), or undefined. */
  goalByMetric(userId: string, metric: string): FitnessGoal | undefined;
  /** Average workout duration (minutes) for a user since `since`, or 0 when none. */
  avgDurationSince(userId: string, since: Date): number;
  /** Total lifted volume (reps × weightKg) across a workout's sets. */
  totalVolumeKg(workoutId: string): number;
}

/**
 * Start-of-week (Sunday) at UTC midnight for `now`, matching C#
 * `now.Date.AddDays(-(int)now.DayOfWeek)` (DayOfWeek: Sunday = 0).
 */
function weekStartUtc(now: Date): Date {
  const midnight = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  return new Date(midnight - now.getUTCDay() * 86_400_000);
}

/** Deterministic in-memory {@link IFitnessBoard}. */
export class InMemoryFitnessBoard implements IFitnessBoard {
  private readonly workouts: Workout[] = [];
  private readonly goals = new Map<string, FitnessGoal>();
  private readonly sets: ExerciseSet[] = [];

  log(w: Workout): void {
    if (w == null) throw new Error("w required");
    this.workouts.push(w);
  }

  workoutsThisWeek(userId: string, now: Date): readonly Workout[] {
    const weekStart = weekStartUtc(now).getTime();
    return this.workouts
      .filter((w) => w.userId === userId && w.atUtc.getTime() >= weekStart)
      .sort((a, b) => a.atUtc.getTime() - b.atUtc.getTime());
  }

  totalCaloriesSince(userId: string, since: Date): number {
    const sinceMs = since.getTime();
    return this.workouts
      .filter((w) => w.userId === userId && w.atUtc.getTime() >= sinceMs)
      .reduce((sum, w) => sum + w.caloriesBurned, 0);
  }

  setGoal(g: FitnessGoal): void {
    if (g == null) throw new Error("g required");
    this.goals.set(g.goalId, g);
  }

  goalsFor(userId: string): readonly FitnessGoal[] {
    return [...this.goals.values()].filter((g) => g.userId === userId);
  }

  addSet(s: ExerciseSet): void {
    if (s == null) throw new Error("s required");
    this.sets.push(s);
  }

  setsFor(workoutId: string): readonly ExerciseSet[] {
    return this.sets.filter((s) => s.workoutId === workoutId);
  }

  /** Total number of workouts logged. Mirrors C# `WorkoutCount`. */
  get workoutCount(): number {
    return this.workouts.length;
  }

  /**
   * A user's workouts of a given kind (kind matched case-insensitively; user id
   * matched ordinally), newest first. Mirrors C# `WorkoutsByKind`.
   */
  workoutsByKind(userId: string, kind: string): readonly Workout[] {
    const target = kind.toLowerCase();
    return this.workouts
      .filter((w) => w.userId === userId && w.kind.toLowerCase() === target)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime());
  }

  /** Remove a goal by id. Returns whether one was removed. Mirrors C# `RemoveGoal`. */
  removeGoal(goalId: string): boolean {
    return this.goals.delete(goalId);
  }

  /**
   * The user's soonest-due goal for a given metric (metric matched
   * case-insensitively), or undefined. Mirrors C# `GoalByMetric`.
   */
  goalByMetric(userId: string, metric: string): FitnessGoal | undefined {
    const target = metric.toLowerCase();
    return [...this.goals.values()]
      .filter((g) => g.userId === userId && g.metric.toLowerCase() === target)
      .sort((a, b) => a.dueOn.getTime() - b.dueOn.getTime())[0];
  }

  /**
   * Average workout duration (minutes) for a user since `since`, or 0 when the
   * user has no qualifying workouts (C# `DefaultIfEmpty(0).Average()`).
   * Mirrors C# `AvgDurationSince`.
   */
  avgDurationSince(userId: string, since: Date): number {
    const sinceMs = since.getTime();
    const durations = this.workouts
      .filter((w) => w.userId === userId && w.atUtc.getTime() >= sinceMs)
      .map((w) => w.durationMinutes);
    if (durations.length === 0) return 0;
    return durations.reduce((sum, d) => sum + d, 0) / durations.length;
  }

  /** Total lifted volume (reps × weightKg) across a workout's sets. Mirrors C# `TotalVolumeKg`. */
  totalVolumeKg(workoutId: string): number {
    return this.sets
      .filter((s) => s.workoutId === workoutId)
      .reduce((sum, s) => sum + s.reps * s.weightKg, 0);
  }
}

/**
 * Static domain context for the Fitness vertical. Mirrors C#
 * `FitnessDomainContext`.
 */
export const FitnessDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Fitness] Personal fitness coach companion. Help with training programme design, workout planning, recovery protocols, nutritional timing, and progress analysis. Apply evidence-based exercise science principles. Not a medical service. Compliance: HPCSA fitness guidelines, POPIA.",
  complianceFlags: ["HPCSA_Fitness", "POPIA", "Not_Medical_Advice"] as readonly string[],
  suggestedTools: ["fitness_tracker", "exercise_db", "nutrition_tools", "analytics"] as readonly string[],
} as const;
