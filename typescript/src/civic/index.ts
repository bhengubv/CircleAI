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
