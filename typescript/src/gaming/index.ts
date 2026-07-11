// gaming/index.ts
// Full-parity port of CircleAI.Gaming (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Gaming vertical: game titles, play
// sessions, achievement unlocks, total-play-time + most-played rollups. Plus the
// static GamingDomainContext.
//
// NOTE: The C# GamingCompanionAdapter (an ICompanionSession LLM-prompt wrapper)
// is intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   TimeSpan Duration                → number of milliseconds (a TimeSpan is a
//                                      duration; ms is a faithful carrier).
//   TimeSpan TotalPlayTime (return)  → number of milliseconds.
//   DateTimeOffset AtUtc             → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   TitlesByGenre  — titles whose Genre matches (ordinal case-insensitive).
//   TotalPlayTime  — sum of Duration (ms) over (user,title) sessions.
//   AchievementsFor— user's unlocks, AtUtc descending.
//   MostPlayed     — user's sessions grouped by TitleId, ordered by total ms
//                    descending, take topK, resolved to GameTitle (unknown titles
//                    dropped). topK<=0 throws.

/** A game title. Mirrors C# `GameTitle` record. */
export interface GameTitle {
  readonly titleId: string;
  readonly name: string;
  readonly genre: string;
  readonly platform: string;
}

/** Constructs a {@link GameTitle}. */
export function gameTitle(titleId: string, name: string, genre: string, platform: string): GameTitle {
  return { titleId, name, genre, platform };
}

/** A play session. Mirrors C# `PlaySession` record. */
export interface PlaySession {
  readonly sessionId: string;
  readonly userId: string;
  readonly titleId: string;
  /** Session length as a value in milliseconds (C# `TimeSpan Duration`). */
  readonly durationMs: number;
  /** UTC instant of the session (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link PlaySession}. */
export function playSession(
  sessionId: string,
  userId: string,
  titleId: string,
  durationMs: number,
  atUtc: Date,
): PlaySession {
  return { sessionId, userId, titleId, durationMs, atUtc };
}

/** An achievement unlock. Mirrors C# `AchievementUnlock` record. */
export interface AchievementUnlock {
  readonly unlockId: string;
  readonly userId: string;
  readonly titleId: string;
  readonly achievement: string;
  /** UTC instant of the unlock (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs an {@link AchievementUnlock}. */
export function achievementUnlock(
  unlockId: string,
  userId: string,
  titleId: string,
  achievement: string,
  atUtc: Date,
): AchievementUnlock {
  return { unlockId, userId, titleId, achievement, atUtc };
}

/** The gaming board contract. Mirrors C# `IGamingBoard`. */
export interface IGamingBoard {
  addTitle(t: GameTitle): void;
  getTitle(id: string): GameTitle | undefined;
  titlesByGenre(genre: string): readonly GameTitle[];
  recordSession(s: PlaySession): void;
  /** Total play time in milliseconds (C# `TimeSpan`). */
  totalPlayTime(userId: string, titleId: string): number;
  unlock(u: AchievementUnlock): void;
  achievementsFor(userId: string): readonly AchievementUnlock[];
  mostPlayed(userId: string, topK?: number): readonly GameTitle[];
}

/** Deterministic in-memory {@link IGamingBoard}. */
export class InMemoryGamingBoard implements IGamingBoard {
  private readonly titles = new Map<string, GameTitle>();
  private readonly sessions: PlaySession[] = [];
  private readonly unlocks: AchievementUnlock[] = [];

  addTitle(t: GameTitle): void {
    if (t == null) throw new Error("t required");
    this.titles.set(t.titleId, t);
  }

  getTitle(id: string): GameTitle | undefined {
    return this.titles.get(id);
  }

  titlesByGenre(genre: string): readonly GameTitle[] {
    const g = genre.toLowerCase();
    return [...this.titles.values()].filter((t) => t.genre.toLowerCase() === g);
  }

  recordSession(s: PlaySession): void {
    if (s == null) throw new Error("s required");
    this.sessions.push(s);
  }

  totalPlayTime(userId: string, titleId: string): number {
    return this.sessions
      .filter((s) => s.userId === userId && s.titleId === titleId)
      .reduce((sum, s) => sum + s.durationMs, 0);
  }

  unlock(u: AchievementUnlock): void {
    if (u == null) throw new Error("u required");
    this.unlocks.push(u);
  }

  achievementsFor(userId: string): readonly AchievementUnlock[] {
    return this.unlocks
      .filter((u) => u.userId === userId)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime());
  }

  mostPlayed(userId: string, topK = 5): readonly GameTitle[] {
    if (topK <= 0) throw new Error("topK");
    // Group by titleId, preserving first-seen order (LINQ GroupBy semantics).
    const totals = new Map<string, number>();
    for (const s of this.sessions) {
      if (s.userId === userId) {
        totals.set(s.titleId, (totals.get(s.titleId) ?? 0) + s.durationMs);
      }
    }
    return [...totals.entries()]
      .sort((a, b) => b[1] - a[1])
      .slice(0, topK)
      .map(([titleId]) => this.titles.get(titleId))
      .filter((t): t is GameTitle => t !== undefined);
  }
}

/**
 * Static domain context for the Gaming vertical. Mirrors C#
 * `GamingDomainContext`.
 */
export const GamingDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Gaming] Expert gaming companion. Help with game strategy guides, build optimisation, community event planning, game review writing, speedrun technique research, and gaming health (screen time, ergonomics). Compliance: POPIA, WASPA (in-game purchases), child protection where applicable.",
  complianceFlags: ["POPIA", "WASPA", "Child_Protection"] as readonly string[],
  suggestedTools: ["game_db", "community_tools", "analytics", "web_search"] as readonly string[],
} as const;
