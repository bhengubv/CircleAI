// faith/index.ts
// Full-parity port of CircleAI.Faith (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Faith vertical: services, prayer
// requests, scripture references. Plus the static FaithDomainContext.
//
// NOTE: The C# FaithCompanionAdapter (an ICompanionSession LLM-prompt wrapper) is
// intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   int Chapter / Verse / limit      → number
//   bool IsAnonymous                 → boolean
//   DateTimeOffset StartUtc/SubmittedUtc → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   ServicesBetween — services with start <= StartUtc <= end, StartUtc ascending.
//   RecentPrayers   — all prayers, SubmittedUtc descending, take limit.
//   Lookup          — first scripture matching (Tradition, Book, Chapter, Verse)
//                     exactly (ordinal), or undefined. Map values order.
//   ByTradition     — scriptures whose Tradition matches (ordinal case-insensitive).

/** A scheduled faith service. Mirrors C# `FaithService` record. */
export interface FaithService {
  readonly serviceId: string;
  readonly communityName: string;
  readonly title: string;
  /** UTC start instant (C# `DateTimeOffset StartUtc`). */
  readonly startUtc: Date;
  readonly location: string;
}

/** Constructs a {@link FaithService}. */
export function faithService(
  serviceId: string,
  communityName: string,
  title: string,
  startUtc: Date,
  location: string,
): FaithService {
  return { serviceId, communityName, title, startUtc, location };
}

/** A prayer request. Mirrors C# `PrayerRequest` record. */
export interface PrayerRequest {
  readonly requestId: string;
  readonly author: string;
  readonly body: string;
  /** UTC instant the request was submitted (C# `DateTimeOffset SubmittedUtc`). */
  readonly submittedUtc: Date;
  readonly isAnonymous: boolean;
}

/** Constructs a {@link PrayerRequest}. */
export function prayerRequest(
  requestId: string,
  author: string,
  body: string,
  submittedUtc: Date,
  isAnonymous: boolean,
): PrayerRequest {
  return { requestId, author, body, submittedUtc, isAnonymous };
}

/** A scripture reference. Mirrors C# `ScriptureReference` record. */
export interface ScriptureReference {
  readonly referenceId: string;
  readonly tradition: string;
  readonly book: string;
  readonly chapter: number;
  readonly verse: number;
  readonly text: string;
}

/** Constructs a {@link ScriptureReference}. */
export function scriptureReference(
  referenceId: string,
  tradition: string,
  book: string,
  chapter: number,
  verse: number,
  text: string,
): ScriptureReference {
  return { referenceId, tradition, book, chapter, verse, text };
}

/** The faith board contract. Mirrors C# `IFaithBoard`. */
export interface IFaithBoard {
  schedule(s: FaithService): void;
  servicesBetween(start: Date, end: Date): readonly FaithService[];
  submitPrayer(r: PrayerRequest): void;
  recentPrayers(limit?: number): readonly PrayerRequest[];
  addScripture(r: ScriptureReference): void;
  lookup(tradition: string, book: string, chapter: number, verse: number): ScriptureReference | undefined;
  byTradition(tradition: string): readonly ScriptureReference[];
  /** Number of services scheduled. */
  readonly serviceCount: number;
  /** Remove a service by id. Returns whether one was removed. */
  removeService(serviceId: string): boolean;
  /** Services at a given location (case-insensitive), earliest first. */
  servicesAt(location: string): readonly FaithService[];
  /** Non-anonymous prayers by a given author (case-insensitive), newest first. */
  prayersByAuthor(author: string): readonly PrayerRequest[];
  /** Count of prayers submitted anonymously. */
  anonymousPrayerCount(): number;
  /** Verses of a given tradition/book/chapter (case-insensitive), ordered by verse. */
  chapterVerses(tradition: string, book: string, chapter: number): readonly ScriptureReference[];
}

/** Deterministic in-memory {@link IFaithBoard}. */
export class InMemoryFaithBoard implements IFaithBoard {
  private readonly services = new Map<string, FaithService>();
  private readonly prayers: PrayerRequest[] = [];
  private readonly scripture = new Map<string, ScriptureReference>();

  schedule(s: FaithService): void {
    if (s == null) throw new Error("s required");
    this.services.set(s.serviceId, s);
  }

  servicesBetween(start: Date, end: Date): readonly FaithService[] {
    const s = start.getTime();
    const e = end.getTime();
    return [...this.services.values()]
      .filter((x) => x.startUtc.getTime() >= s && x.startUtc.getTime() <= e)
      .sort((a, b) => a.startUtc.getTime() - b.startUtc.getTime());
  }

  submitPrayer(r: PrayerRequest): void {
    if (r == null) throw new Error("r required");
    this.prayers.push(r);
  }

  recentPrayers(limit = 20): readonly PrayerRequest[] {
    return [...this.prayers]
      .sort((a, b) => b.submittedUtc.getTime() - a.submittedUtc.getTime())
      .slice(0, limit);
  }

  addScripture(r: ScriptureReference): void {
    if (r == null) throw new Error("r required");
    this.scripture.set(r.referenceId, r);
  }

  lookup(tradition: string, book: string, chapter: number, verse: number): ScriptureReference | undefined {
    for (const r of this.scripture.values()) {
      if (r.tradition === tradition && r.book === book && r.chapter === chapter && r.verse === verse) return r;
    }
    return undefined;
  }

  byTradition(tradition: string): readonly ScriptureReference[] {
    const t = tradition.toLowerCase();
    return [...this.scripture.values()].filter((r) => r.tradition.toLowerCase() === t);
  }

  /** Number of services scheduled. Mirrors C# `ServiceCount`. */
  get serviceCount(): number {
    return this.services.size;
  }

  /** Remove a service by id. Returns whether one was removed. Mirrors C# `RemoveService`. */
  removeService(serviceId: string): boolean {
    return this.services.delete(serviceId);
  }

  /** Services at a given location (case-insensitive), earliest first. Mirrors C# `ServicesAt`. */
  servicesAt(location: string): readonly FaithService[] {
    const target = location.toLowerCase();
    return [...this.services.values()]
      .filter((s) => s.location.toLowerCase() === target)
      .sort((a, b) => a.startUtc.getTime() - b.startUtc.getTime());
  }

  /**
   * Non-anonymous prayers by a given author (case-insensitive), newest first.
   * Anonymous requests are excluded regardless of author, mirroring the
   * privacy-aware C# `PrayersByAuthor`.
   */
  prayersByAuthor(author: string): readonly PrayerRequest[] {
    const target = author.toLowerCase();
    return this.prayers
      .filter((p) => !p.isAnonymous && p.author.toLowerCase() === target)
      .sort((a, b) => b.submittedUtc.getTime() - a.submittedUtc.getTime());
  }

  /** Count of prayers submitted anonymously. Mirrors C# `AnonymousPrayerCount`. */
  anonymousPrayerCount(): number {
    return this.prayers.reduce((n, p) => n + (p.isAnonymous ? 1 : 0), 0);
  }

  /**
   * Verses of a given tradition/book/chapter (tradition + book matched
   * case-insensitively), ordered by verse. Mirrors C# `ChapterVerses`.
   */
  chapterVerses(tradition: string, book: string, chapter: number): readonly ScriptureReference[] {
    const t = tradition.toLowerCase();
    const b = book.toLowerCase();
    return [...this.scripture.values()]
      .filter((r) => r.tradition.toLowerCase() === t && r.book.toLowerCase() === b && r.chapter === chapter)
      .sort((x, y) => x.verse - y.verse);
  }
}

/**
 * Static domain context for the Faith vertical. Mirrors C#
 * `FaithDomainContext`.
 */
export const FaithDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Faith] Respectful, non-denominational spiritual companion. Help with scripture study, prayer composition, devotional content, faith community planning, and spiritual reflection prompts. Respect all faith traditions equally. Never impose one tradition on another. Compliance: POPIA.",
  complianceFlags: ["POPIA", "Non_Denominational_Respect"] as readonly string[],
  suggestedTools: ["scripture_tools", "document_editor", "calendar"] as readonly string[],
} as const;
