// parenting/index.ts
// Full-parity port of CircleAI.Parenting (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Parenting vertical: children,
// milestones, and per-child per-day-of-week routines, plus an age calculation.
// Plus the static ParentingDomainContext.
//
// NOTE: The C# ParentingCompanionAdapter (an ICompanionSession LLM-prompt
// wrapper) is intentionally NOT ported — consistent with the sibling
// domain-board ports (healthcare/education/legal/commerce).
//
// Type mappings (C# → TS):
//   record                          → readonly interface (+ positional factory)
//   System.DayOfWeek (Sunday=0..Sat=6) → const enum-like DayOfWeek + names table
//   DateTime DateOfBirth / at         → Date
//   DateTimeOffset AchievedAtUtc     → Date
//   TimeSpan (AgeAsOf)               → number of milliseconds (a TimeSpan is a
//                                      duration; ms is a faithful carrier)
//   IReadOnlyList<RoutineEntry>      → readonly RoutineEntry[]
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// ROUTINE KEY PARITY: C# keys routines by $"{childId}/{DayOfWeek}", where the
// enum interpolates to its NAME (e.g. "Monday"). We reproduce that with the
// DAY_NAMES table so keys are byte-identical.
//
// SEMANTICS PARITY:
//   Children        — ordered by Name ascending (default comparer / ordinal).
//   RecordMilestone — throws on blank ChildId.
//   MilestonesFor   — child's milestones, AchievedAtUtc descending.
//   AgeAsOf         — throws on unknown child; (at - DateOfBirth) in ms.

/** Day of week, matching C# `System.DayOfWeek` values (Sunday = 0). */
export type DayOfWeek = 0 | 1 | 2 | 3 | 4 | 5 | 6;
/** Frozen value object for {@link DayOfWeek} members (C# enum names → values). */
export const DayOfWeek = Object.freeze({
  Sunday: 0,
  Monday: 1,
  Tuesday: 2,
  Wednesday: 3,
  Thursday: 4,
  Friday: 5,
  Saturday: 6,
} as const) satisfies Record<string, DayOfWeek>;

/** Enum-name for each {@link DayOfWeek} value, matching C# `DayOfWeek.ToString()`. */
const DAY_NAMES: readonly string[] = [
  "Sunday",
  "Monday",
  "Tuesday",
  "Wednesday",
  "Thursday",
  "Friday",
  "Saturday",
];

/** A child. Mirrors C# `Child` record. */
export interface Child {
  readonly childId: string;
  readonly name: string;
  readonly dateOfBirth: Date;
  readonly gender: string | null;
}

/** Constructs a {@link Child}. */
export function child(childId: string, name: string, dateOfBirth: Date, gender: string | null): Child {
  return { childId, name, dateOfBirth, gender };
}

/** A developmental milestone. Mirrors C# `Milestone` record. */
export interface Milestone {
  readonly milestoneId: string;
  readonly childId: string;
  readonly category: string;
  readonly description: string;
  /** UTC instant the milestone was achieved (C# `DateTimeOffset AchievedAtUtc`). */
  readonly achievedAtUtc: Date;
}

/** Constructs a {@link Milestone}. */
export function milestone(
  milestoneId: string,
  childId: string,
  category: string,
  description: string,
  achievedAtUtc: Date,
): Milestone {
  return { milestoneId, childId, category, description, achievedAtUtc };
}

/** A single entry in a daily routine. Mirrors C# `RoutineEntry` record. */
export interface RoutineEntry {
  readonly time: string;
  readonly activity: string;
}

/** Constructs a {@link RoutineEntry}. */
export function routineEntry(time: string, activity: string): RoutineEntry {
  return { time, activity };
}

/** A child's routine for a given day of week. Mirrors C# `Routine` record. */
export interface Routine {
  readonly childId: string;
  readonly dayOfWeek: DayOfWeek;
  readonly entries: readonly RoutineEntry[];
}

/** Constructs a {@link Routine}. */
export function routine(childId: string, dayOfWeek: DayOfWeek, entries: readonly RoutineEntry[]): Routine {
  return { childId, dayOfWeek, entries };
}

/** The parenting board contract. Mirrors C# `IParentingBoard`. */
export interface IParentingBoard {
  addChild(c: Child): void;
  getChild(id: string): Child | undefined;
  readonly children: readonly Child[];
  recordMilestone(m: Milestone): void;
  milestonesFor(childId: string): readonly Milestone[];
  setRoutine(r: Routine): void;
  getRoutine(childId: string, dow: DayOfWeek): Routine | undefined;
  /** (at − DateOfBirth) as a duration in milliseconds (C# `TimeSpan`). */
  ageAsOf(childId: string, at: Date): number;
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Deterministic in-memory {@link IParentingBoard}. */
export class InMemoryParentingBoard implements IParentingBoard {
  private readonly childrenById = new Map<string, Child>();
  private readonly milestones = new Map<string, Milestone[]>();
  private readonly routines = new Map<string, Routine>();

  addChild(c: Child): void {
    if (c == null) throw new Error("c required");
    this.childrenById.set(c.childId, c);
  }

  getChild(id: string): Child | undefined {
    return this.childrenById.get(id);
  }

  get children(): readonly Child[] {
    return [...this.childrenById.values()].sort((a, b) => ordinalCompare(a.name, b.name));
  }

  recordMilestone(m: Milestone): void {
    if (m == null) throw new Error("m required");
    if (m.childId == null || m.childId.trim() === "") throw new Error("ChildId required");
    let list = this.milestones.get(m.childId);
    if (list === undefined) {
      list = [];
      this.milestones.set(m.childId, list);
    }
    list.push(m);
  }

  milestonesFor(childId: string): readonly Milestone[] {
    const list = this.milestones.get(childId);
    if (list === undefined) return [];
    return [...list].sort((a, b) => b.achievedAtUtc.getTime() - a.achievedAtUtc.getTime());
  }

  setRoutine(r: Routine): void {
    if (r == null) throw new Error("r required");
    this.routines.set(this.key(r.childId, r.dayOfWeek), r);
  }

  getRoutine(childId: string, dow: DayOfWeek): Routine | undefined {
    return this.routines.get(this.key(childId, dow));
  }

  ageAsOf(childId: string, at: Date): number {
    const c = this.childrenById.get(childId);
    if (c === undefined) throw new Error(`Unknown child ${childId}`);
    return at.getTime() - c.dateOfBirth.getTime();
  }

  private key(childId: string, d: DayOfWeek): string {
    return `${childId}/${DAY_NAMES[d]}`;
  }
}

/**
 * Static domain context for the Parenting vertical. Mirrors C#
 * `ParentingDomainContext`.
 */
export const ParentingDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Parenting] Supportive parenting companion. Offer evidence-based parenting strategies (positive discipline, attachment, development milestones), school communication guidance, and family wellbeing tips. Acknowledge the difficulty of parenting without judgment. Compliance: Children's Act 38/2005, POPIA.",
  complianceFlags: ["Childrens_Act_38_2005", "POPIA"] as readonly string[],
  suggestedTools: ["development_tracker", "document_editor", "web_search", "calendar"] as readonly string[],
} as const;
