// personal/mental/index.ts
// Full-parity port of CircleAI.Personal.Mental (C#). C# is the exact spec.
//
// Domain types + in-memory store for the mental-health vertical: mood logs,
// journal entries, a coping-strategy library, and a 7-day trend. Plus the
// static PersonalMentalDomainContext. Privacy: per-user instance only.
//
// Type mappings (C# → TS):
//   enum Mood             → TS enum (ordinals ARE the trend values: VeryLow=0..Great=4)
//   record                → readonly interface (+ positional factory)
//   DateTimeOffset AtUtc  → Date; string? Note → string | null
//   IReadOnlyList<string> → readonly string[]
//   ConcurrentDictionary (Ordinal) + List under a lock → Map + array
//
// PARITY NOTES:
//   Last7Days()      — AtUtc >= (now − 7 days), sorted AtUtc ascending
//   AddEntry(e)      — blank EntryId throws ("EntryId required")
//   Entries          — sorted AtUtc descending
//   StrategiesByTag  — case-insensitive tag membership; blank tag throws
//   AvgMood7Day()    — mean of the (int)Mood ordinals over the last 7 days;
//                      returns NaN when there are none (C# double.NaN)

/** Discrete mood scale. Ordinals are the numeric trend values. */
export enum Mood {
  VeryLow = 0,
  Low = 1,
  Neutral = 2,
  Good = 3,
  Great = 4,
}

/** A single mood log. Mirrors C# `MoodLog` record. */
export interface MoodLog {
  readonly mood: Mood;
  readonly atUtc: Date;
  readonly note: string | null;
}

/** Constructs a {@link MoodLog}. */
export function moodLog(mood: Mood, atUtc: Date, note: string | null): MoodLog {
  return { mood, atUtc, note };
}

/** A journal entry. Mirrors C# `JournalEntry` record. */
export interface JournalEntry {
  readonly entryId: string;
  readonly title: string;
  readonly body: string;
  readonly atUtc: Date;
}

/** Constructs a {@link JournalEntry}. */
export function journalEntry(entryId: string, title: string, body: string, atUtc: Date): JournalEntry {
  return { entryId, title, body, atUtc };
}

/** A reusable coping strategy. Mirrors C# `CopingStrategy` record. */
export interface CopingStrategy {
  readonly strategyId: string;
  readonly title: string;
  readonly description: string;
  readonly tags: readonly string[];
}

/** Constructs a {@link CopingStrategy}. */
export function copingStrategy(
  strategyId: string,
  title: string,
  description: string,
  tags: readonly string[],
): CopingStrategy {
  return { strategyId, title, description, tags };
}

/** The mental-health board contract. */
export interface IMentalHealthBoard {
  logMood(m: MoodLog): void;
  last7Days(): readonly MoodLog[];
  addEntry(e: JournalEntry): void;
  readonly entries: readonly JournalEntry[];
  registerStrategy(s: CopingStrategy): void;
  strategiesByTag(tag: string): readonly CopingStrategy[];
  avgMood7Day(): number;
}

/**
 * Deterministic in-memory {@link IMentalHealthBoard}.
 *
 * `nowUtc` is injectable so the 7-day window is testable without wall-clock
 * flakiness; it defaults to `() => new Date()` (mirrors C# `DateTimeOffset.UtcNow`).
 */
export class InMemoryMentalHealthBoard implements IMentalHealthBoard {
  private readonly moods: MoodLog[] = [];
  private readonly entryMap = new Map<string, JournalEntry>();
  private readonly strats = new Map<string, CopingStrategy>();

  /** Clock source for the 7-day window. Override in tests for determinism. */
  nowUtc: () => Date = () => new Date();

  logMood(m: MoodLog): void {
    if (m == null) throw new Error("m required");
    this.moods.push(m);
  }

  last7Days(): readonly MoodLog[] {
    const cutoff = this.nowUtc().getTime() - 7 * 24 * 60 * 60 * 1000;
    return this.moods
      .filter((m) => m.atUtc.getTime() >= cutoff)
      .sort((a, b) => a.atUtc.getTime() - b.atUtc.getTime());
  }

  addEntry(e: JournalEntry): void {
    if (e == null) throw new Error("e required");
    if (e.entryId == null || e.entryId.trim().length === 0) throw new Error("EntryId required");
    this.entryMap.set(e.entryId, e);
  }

  get entries(): readonly JournalEntry[] {
    return [...this.entryMap.values()].sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime());
  }

  registerStrategy(s: CopingStrategy): void {
    if (s == null) throw new Error("s required");
    this.strats.set(s.strategyId, s);
  }

  strategiesByTag(tag: string): readonly CopingStrategy[] {
    if (tag == null || tag.trim().length === 0) throw new Error("tag required");
    const lower = tag.toLowerCase();
    return [...this.strats.values()].filter((s) => s.tags.some((t) => t.toLowerCase() === lower));
  }

  avgMood7Day(): number {
    const items = this.last7Days();
    if (items.length === 0) return Number.NaN;
    return items.reduce((sum, m) => sum + (m.mood as number), 0) / items.length;
  }
}

/**
 * Static domain context for the Personal.Mental vertical. Mirrors C#
 * `PersonalMentalDomainContext`.
 */
export const PersonalMentalDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Personal.Mental] Warm, empathetic mental wellness companion. Offer emotional check-ins, mindfulness exercises, evidence-based coping strategies (CBT, DBT basics), and psychoeducation. Never diagnose. Always validate feelings before offering tools. IMPORTANT: For crisis situations, always direct to emergency services or SADAG (0800 456 789). Not a substitute for professional therapy. Compliance: POPIA, Mental Health Care Act.",
  complianceFlags: ["POPIA", "Mental_Health_Care_Act_17_2002", "Not_Therapy", "Crisis_Protocol"] as readonly string[],
  suggestedTools: ["journal", "breathing_tools", "mood_tracker", "web_search"] as readonly string[],
} as const;
