/**
 * kids/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// kids/index.ts
// Full-parity port of CircleAI.Kids (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Kids vertical: age-banded content,
// daily time limits, time logs, and screen/reading-limit checks. Plus the static
// KidsDomainContext.
//
// NOTE: The C# KidsCompanionAdapter (an ICompanionSession LLM-prompt wrapper) is
// intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   enum AgeAppropriateness          → const enum-like (Toddler=0..Teen=5)
//   record                           → readonly interface (+ positional factory)
//   IReadOnlyList<string> Tags       → readonly string[]
//   TimeSpan ScreenLimit/ReadingLimit/Duration → number of milliseconds
//   TimeSpan UsedToday (return)      → number of milliseconds
//   DateTimeOffset AtUtc             → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   ContentFor — content in the band, Title ascending.
//   UsedToday  — sum of Duration (ms) for (kid, kind) logs whose AtUtc is the same
//                UTC calendar day as now (C# AtUtc.Date == now.Date).
//   OverLimit  — false when no limits set; else UsedToday > cap where cap is
//                ScreenLimit for "screen", ReadingLimit for "reading" (both ordinal
//                case-insensitive), else no cap (C# TimeSpan.MaxValue → +Infinity).

/** Age-appropriateness band. Mirrors C# `AgeAppropriateness` (Toddler = 0). */
export type AgeAppropriateness = 0 | 1 | 2 | 3 | 4 | 5;
/** Frozen value object for {@link AgeAppropriateness} members. */
export const AgeAppropriateness = Object.freeze({
  Toddler: 0,
  Preschool: 1,
  EarlyPrimary: 2,
  LatePrimary: 3,
  PreTeen: 4,
  Teen: 5,
} as const) satisfies Record<string, AgeAppropriateness>;

/** A piece of kids content. Mirrors C# `KidsContent` record. */
export interface KidsContent {
  readonly contentId: string;
  readonly title: string;
  readonly ageBand: AgeAppropriateness;
  readonly kind: string;
  readonly tags: readonly string[];
}

/** Constructs a {@link KidsContent}. */
export function kidsContent(
  contentId: string,
  title: string,
  ageBand: AgeAppropriateness,
  kind: string,
  tags: readonly string[],
): KidsContent {
  return { contentId, title, ageBand, kind, tags };
}

/** A child's daily time limits. Mirrors C# `DailyTime` record. */
export interface DailyTime {
  readonly kidName: string;
  /** Screen-time limit in milliseconds (C# `TimeSpan ScreenLimit`). */
  readonly screenLimitMs: number;
  /** Reading-time limit in milliseconds (C# `TimeSpan ReadingLimit`). */
  readonly readingLimitMs: number;
}

/** Constructs a {@link DailyTime}. */
export function dailyTime(kidName: string, screenLimitMs: number, readingLimitMs: number): DailyTime {
  return { kidName, screenLimitMs, readingLimitMs };
}

/** A logged time span for a child. Mirrors C# `TimeLog` record. */
export interface TimeLog {
  readonly kidName: string;
  readonly kind: string;
  /** Duration in milliseconds (C# `TimeSpan Duration`). */
  readonly durationMs: number;
  /** UTC instant of the log (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link TimeLog}. */
export function timeLog(kidName: string, kind: string, durationMs: number, atUtc: Date): TimeLog {
  return { kidName, kind, durationMs, atUtc };
}

/** The kids board contract. Mirrors C# `IKidsBoard`. */
export interface IKidsBoard {
  addContent(c: KidsContent): void;
  contentFor(band: AgeAppropriateness): readonly KidsContent[];
  setLimits(d: DailyTime): void;
  limitsFor(kidName: string): DailyTime | undefined;
  recordTime(t: TimeLog): void;
  /** Time used today in milliseconds (C# `TimeSpan`). */
  usedToday(kidName: string, kind: string, now: Date): number;
  overLimit(kidName: string, kind: string, now: Date): boolean;
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** True when two instants fall on the same UTC calendar date (C# `.Date` eq). */
function sameUtcDate(a: Date, b: Date): boolean {
  return (
    a.getUTCFullYear() === b.getUTCFullYear() &&
    a.getUTCMonth() === b.getUTCMonth() &&
    a.getUTCDate() === b.getUTCDate()
  );
}

/** Deterministic in-memory {@link IKidsBoard}. */
export class InMemoryKidsBoard implements IKidsBoard {
  private readonly content = new Map<string, KidsContent>();
  private readonly limits = new Map<string, DailyTime>();
  private readonly logs: TimeLog[] = [];

  addContent(c: KidsContent): void {
    if (c == null) throw new Error("c required");
    this.content.set(c.contentId, c);
  }

  contentFor(band: AgeAppropriateness): readonly KidsContent[] {
    return [...this.content.values()]
      .filter((c) => c.ageBand === band)
      .sort((a, b) => ordinalCompare(a.title, b.title));
  }

  setLimits(d: DailyTime): void {
    if (d == null) throw new Error("d required");
    this.limits.set(d.kidName, d);
  }

  limitsFor(kidName: string): DailyTime | undefined {
    return this.limits.get(kidName);
  }

  recordTime(t: TimeLog): void {
    if (t == null) throw new Error("t required");
    this.logs.push(t);
  }

  usedToday(kidName: string, kind: string, now: Date): number {
    return this.logs
      .filter((l) => l.kidName === kidName && l.kind === kind && sameUtcDate(l.atUtc, now))
      .reduce((sum, l) => sum + l.durationMs, 0);
  }

  overLimit(kidName: string, kind: string, now: Date): boolean {
    const limits = this.limits.get(kidName);
    if (limits === undefined) return false;
    const used = this.usedToday(kidName, kind, now);
    const cap =
      kind.toLowerCase() === "screen"
        ? limits.screenLimitMs
        : kind.toLowerCase() === "reading"
          ? limits.readingLimitMs
          : Number.POSITIVE_INFINITY; // C# TimeSpan.MaxValue — never exceeded
    return used > cap;
  }
}

/**
 * Static domain context for the Kids vertical. Mirrors C# `KidsDomainContext`.
 */
export const KidsDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Kids] Safe, age-appropriate learning companion for children. Use simple, encouraging language. Help with homework, creative storytelling, educational games, and curiosity questions. Never generate inappropriate content. Validate effort, not just results. Compliance: POPIA (children's data), COPPA-principles, Children's Act, CAPS curriculum.",
  complianceFlags: ["POPIA_Childrens_Data", "COPPA_principles", "Childrens_Act", "CAPS_curriculum"] as readonly string[],
  suggestedTools: ["educational_content", "story_tools", "quiz_tools"] as readonly string[],
} as const;
