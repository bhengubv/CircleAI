// civic/index.ts
// Full-parity port of CircleAI.Civic (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Civic vertical: reported issues,
// representatives, civic events. Plus the static CivicDomainContext.
//
// NOTE: The C# CivicCompanionAdapter (an ICompanionSession LLM-prompt wrapper) is
// intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   double Lat / Lon                 → number
//   string? District                 → string | null
//   DateTimeOffset ReportedUtc/AtUtc → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   Resolve         — throws on unknown issue; sets Status.
//   OpenIssues      — issues whose Status != "Resolved" (ordinal case-insensitive).
//   RepsForDistrict — reps whose District matches (ordinal case-insensitive).
//   UpcomingEvents  — events with AtUtc >= now(UTC), AtUtc ascending.

/** A reported civic issue. Mirrors C# `CivicIssue` record. */
export interface CivicIssue {
  readonly issueId: string;
  readonly category: string;
  readonly description: string;
  readonly lat: number;
  readonly lon: number;
  /** UTC instant the issue was reported (C# `DateTimeOffset ReportedUtc`). */
  readonly reportedUtc: Date;
  readonly status: string;
}

/** Constructs a {@link CivicIssue}. */
export function civicIssue(
  issueId: string,
  category: string,
  description: string,
  lat: number,
  lon: number,
  reportedUtc: Date,
  status: string,
): CivicIssue {
  return { issueId, category, description, lat, lon, reportedUtc, status };
}

/** An elected/appointed representative. Mirrors C# `Representative` record. */
export interface Representative {
  readonly repId: string;
  readonly name: string;
  readonly office: string;
  readonly contactEmail: string;
  readonly district: string | null;
}

/** Constructs a {@link Representative}. */
export function representative(
  repId: string,
  name: string,
  office: string,
  contactEmail: string,
  district: string | null,
): Representative {
  return { repId, name, office, contactEmail, district };
}

/** A civic event. Mirrors C# `CivicEvent` record. */
export interface CivicEvent {
  readonly eventId: string;
  readonly title: string;
  /** UTC instant of the event (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
  readonly location: string;
  readonly audience: string;
}

/** Constructs a {@link CivicEvent}. */
export function civicEvent(eventId: string, title: string, atUtc: Date, location: string, audience: string): CivicEvent {
  return { eventId, title, atUtc, location, audience };
}

/** The civic board contract. Mirrors C# `ICivicBoard`. */
export interface ICivicBoard {
  report(i: CivicIssue): void;
  resolve(issueId: string, status: string): void;
  openIssues(): readonly CivicIssue[];
  addRep(r: Representative): void;
  repsForDistrict(district: string): readonly Representative[];
  schedule(e: CivicEvent): void;
  upcomingEvents(): readonly CivicEvent[];
  /** Number of issues not yet resolved. */
  readonly openIssueCount: number;
  /** Issues filed under a given category (case-insensitive), newest first. */
  issuesByCategory(category: string): readonly CivicIssue[];
  /** Remove a representative by id. Returns whether one was removed. */
  removeRep(repId: string): boolean;
  /** Representatives holding a given office (case-insensitive), ordered by name. */
  repsForOffice(office: string): readonly Representative[];
  /** Events targeting a given audience (case-insensitive), earliest first. */
  eventsForAudience(audience: string): readonly CivicEvent[];
  /** Open-issue counts grouped by category, most-common first. */
  openIssueBreakdown(): readonly CategoryCount[];
}

/**
 * A `(category, count)` pair, mirroring the C# named tuple
 * `(string Category, int Count)` returned by `OpenIssueBreakdown`.
 */
export type CategoryCount = readonly [category: string, count: number];

/**
 * Ordinal-case-insensitive compare, matching C# `StringComparer.OrdinalIgnoreCase`
 * ordering. Ties fall back to the ordinal comparison of the originals.
 */
function ordinalIgnoreCaseCompare(a: string, b: string): number {
  const la = a.toUpperCase();
  const lb = b.toUpperCase();
  if (la < lb) return -1;
  if (la > lb) return 1;
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Deterministic in-memory {@link ICivicBoard}. */
export class InMemoryCivicBoard implements ICivicBoard {
  private readonly issues = new Map<string, CivicIssue>();
  private readonly reps = new Map<string, Representative>();
  private readonly events = new Map<string, CivicEvent>();

  report(i: CivicIssue): void {
    if (i == null) throw new Error("i required");
    this.issues.set(i.issueId, i);
  }

  resolve(issueId: string, status: string): void {
    const i = this.issues.get(issueId);
    if (i === undefined) throw new Error(`Unknown issue ${issueId}`);
    this.issues.set(issueId, { ...i, status });
  }

  openIssues(): readonly CivicIssue[] {
    return [...this.issues.values()].filter((i) => i.status.toLowerCase() !== "resolved");
  }

  addRep(r: Representative): void {
    if (r == null) throw new Error("r required");
    this.reps.set(r.repId, r);
  }

  repsForDistrict(district: string): readonly Representative[] {
    const d = district.toLowerCase();
    return [...this.reps.values()].filter((r) => r.district !== null && r.district.toLowerCase() === d);
  }

  schedule(e: CivicEvent): void {
    if (e == null) throw new Error("e required");
    this.events.set(e.eventId, e);
  }

  upcomingEvents(): readonly CivicEvent[] {
    const nowMs = Date.now();
    return [...this.events.values()]
      .filter((e) => e.atUtc.getTime() >= nowMs)
      .sort((a, b) => a.atUtc.getTime() - b.atUtc.getTime());
  }

  /** Number of issues not yet resolved. Mirrors C# `OpenIssueCount`. */
  get openIssueCount(): number {
    return this.openIssues().length;
  }

  /**
   * Issues filed under a given category (case-insensitive), newest first.
   * Mirrors C# `IssuesByCategory`.
   */
  issuesByCategory(category: string): readonly CivicIssue[] {
    const target = category.toLowerCase();
    return [...this.issues.values()]
      .filter((i) => i.category.toLowerCase() === target)
      .sort((a, b) => b.reportedUtc.getTime() - a.reportedUtc.getTime());
  }

  /** Remove a representative by id. Returns whether one was removed. Mirrors C# `RemoveRep`. */
  removeRep(repId: string): boolean {
    return this.reps.delete(repId);
  }

  /**
   * Representatives holding a given office (case-insensitive), ordered by name
   * (OrdinalIgnoreCase). Mirrors C# `RepsForOffice`.
   */
  repsForOffice(office: string): readonly Representative[] {
    const target = office.toLowerCase();
    return [...this.reps.values()]
      .filter((r) => r.office.toLowerCase() === target)
      .sort((a, b) => ordinalIgnoreCaseCompare(a.name, b.name));
  }

  /**
   * Events targeting a given audience (case-insensitive), earliest first.
   * Mirrors C# `EventsForAudience`.
   */
  eventsForAudience(audience: string): readonly CivicEvent[] {
    const target = audience.toLowerCase();
    return [...this.events.values()]
      .filter((e) => e.audience.toLowerCase() === target)
      .sort((a, b) => a.atUtc.getTime() - b.atUtc.getTime());
  }

  /**
   * Open-issue counts grouped by category (case-insensitive; first-seen casing
   * wins), most-common first. Ties keep first-seen order. Mirrors C#
   * `OpenIssueBreakdown`.
   */
  openIssueBreakdown(): readonly CategoryCount[] {
    const groups = new Map<string, { category: string; count: number }>();
    for (const i of this.openIssues()) {
      const key = i.category.toLowerCase();
      const g = groups.get(key);
      if (g === undefined) groups.set(key, { category: i.category, count: 1 });
      else g.count += 1;
    }
    return [...groups.values()]
      .sort((a, b) => b.count - a.count)
      .map((g): CategoryCount => [g.category, g.count]);
  }
}

/**
 * Static domain context for the Civic vertical. Mirrors C#
 * `CivicDomainContext`.
 */
export const CivicDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Civic] Expert in civic rights and government services. Help citizens navigate municipal processes, permit applications, public participation, service delivery queries, and constitutional rights. Explain bureaucratic processes in plain language. Compliance: PAJA, PAIA, Constitution of SA, Municipal Systems Act.",
  complianceFlags: ["PAJA", "PAIA", "Constitution_RSA", "Municipal_Systems_Act", "POPIA"] as readonly string[],
  suggestedTools: ["government_portals", "document_editor", "map", "web_search"] as readonly string[],
} as const;
