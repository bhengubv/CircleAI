// community/index.ts
// Full-parity port of CircleAI.Community (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Community vertical: groups,
// announcements, volunteer opportunities. Plus the static CommunityDomainContext.
//
// NOTE: The C# CommunityCompanionAdapter (an ICompanionSession LLM-prompt
// wrapper) is intentionally NOT ported — consistent with the sibling
// domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   IReadOnlyList<string> MemberIds  → readonly string[]
//   int VolunteersNeeded / limit     → number
//   DateTimeOffset AtUtc / WhenUtc   → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   GroupsForMember  — groups whose MemberIds contain the member (ordinal eq).
//   AnnouncementsFor — group's announcements, AtUtc descending, take limit.
//   Opportunities    — opps with WhenUtc >= now(UTC), WhenUtc ascending.

/** A community group. Mirrors C# `CommunityGroup` record. */
export interface CommunityGroup {
  readonly groupId: string;
  readonly name: string;
  readonly purpose: string;
  readonly memberIds: readonly string[];
}

/** Constructs a {@link CommunityGroup}. */
export function communityGroup(
  groupId: string,
  name: string,
  purpose: string,
  memberIds: readonly string[],
): CommunityGroup {
  return { groupId, name, purpose, memberIds };
}

/** A group announcement. Mirrors C# `Announcement` record. */
export interface Announcement {
  readonly announcementId: string;
  readonly groupId: string;
  readonly title: string;
  readonly body: string;
  /** UTC instant of the announcement (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs an {@link Announcement}. */
export function announcement(
  announcementId: string,
  groupId: string,
  title: string,
  body: string,
  atUtc: Date,
): Announcement {
  return { announcementId, groupId, title, body, atUtc };
}

/** A volunteer opportunity. Mirrors C# `VolunteerOpportunity` record. */
export interface VolunteerOpportunity {
  readonly oppId: string;
  readonly groupId: string;
  readonly description: string;
  readonly volunteersNeeded: number;
  /** UTC instant the opportunity is scheduled for (C# `DateTimeOffset WhenUtc`). */
  readonly whenUtc: Date;
}

/** Constructs a {@link VolunteerOpportunity}. */
export function volunteerOpportunity(
  oppId: string,
  groupId: string,
  description: string,
  volunteersNeeded: number,
  whenUtc: Date,
): VolunteerOpportunity {
  return { oppId, groupId, description, volunteersNeeded, whenUtc };
}

/** The community board contract. Mirrors C# `ICommunityBoard`. */
export interface ICommunityBoard {
  create(g: CommunityGroup): void;
  getGroup(id: string): CommunityGroup | undefined;
  groupsForMember(memberId: string): readonly CommunityGroup[];
  post(a: Announcement): void;
  announcementsFor(groupId: string, limit?: number): readonly Announcement[];
  list(o: VolunteerOpportunity): void;
  opportunities(): readonly VolunteerOpportunity[];
}

/** Deterministic in-memory {@link ICommunityBoard}. */
export class InMemoryCommunityBoard implements ICommunityBoard {
  private readonly groups = new Map<string, CommunityGroup>();
  private readonly annc: Announcement[] = [];
  private readonly opps = new Map<string, VolunteerOpportunity>();

  create(g: CommunityGroup): void {
    if (g == null) throw new Error("g required");
    this.groups.set(g.groupId, g);
  }

  getGroup(id: string): CommunityGroup | undefined {
    return this.groups.get(id);
  }

  groupsForMember(memberId: string): readonly CommunityGroup[] {
    return [...this.groups.values()].filter((g) => g.memberIds.includes(memberId));
  }

  post(a: Announcement): void {
    if (a == null) throw new Error("a required");
    this.annc.push(a);
  }

  announcementsFor(groupId: string, limit = 20): readonly Announcement[] {
    return this.annc
      .filter((a) => a.groupId === groupId)
      .sort((x, y) => y.atUtc.getTime() - x.atUtc.getTime())
      .slice(0, limit);
  }

  list(o: VolunteerOpportunity): void {
    if (o == null) throw new Error("o required");
    this.opps.set(o.oppId, o);
  }

  opportunities(): readonly VolunteerOpportunity[] {
    const nowMs = Date.now();
    return [...this.opps.values()]
      .filter((o) => o.whenUtc.getTime() >= nowMs)
      .sort((a, b) => a.whenUtc.getTime() - b.whenUtc.getTime());
  }
}

/**
 * Static domain context for the Community vertical. Mirrors C#
 * `CommunityDomainContext`.
 */
export const CommunityDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Community] Community organising and engagement assistant. Help with community event planning, volunteer coordination, advocacy letter writing, fundraising strategies, and neighbourhood communication. Empower grassroots action. Compliance: NPO Act, POPIA, Fundraising Act.",
  complianceFlags: ["NPO_Act", "Fundraising_Act", "POPIA"] as readonly string[],
  suggestedTools: ["event_manager", "document_editor", "communication_tools", "volunteer_tracker"] as readonly string[],
} as const;
