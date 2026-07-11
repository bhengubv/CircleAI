// relationships/index.ts
// Full-parity port of CircleAI.Relationships (C#). C# is the exact spec.
//
// Domain types + in-memory CRM-lite for personal relationships: contacts,
// important dates, and a last-contact tracker. Plus the static
// RelationshipsDomainContext.
//
// NOTE: The C# RelationshipsCompanionAdapter (an ICompanionSession LLM-prompt
// wrapper) is intentionally NOT ported — consistent with the sibling
// domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   string? Notes / Note             → string | null
//   DateTime Date                    → Date
//   DateTimeOffset AtUtc / return    → Date  (nullable return → Date | undefined)
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   Contacts          — all contacts, Name ascending (default comparer / ordinal).
//   UpcomingThisMonth — important dates whose Date.Month == now(UTC).Month,
//                       ordered by Date.Day ascending.
//   LastContact       — most-recent touchpoint AtUtc for the contact, or undefined.
//   NotContactedSince — contacts whose LastContact is undefined OR < cutoff.

/** A personal contact. Mirrors C# `PersonContact` record. */
export interface PersonContact {
  readonly contactId: string;
  readonly name: string;
  readonly relationship: string;
  readonly notes: string | null;
}

/** Constructs a {@link PersonContact}. */
export function personContact(
  contactId: string,
  name: string,
  relationship: string,
  notes: string | null,
): PersonContact {
  return { contactId, name, relationship, notes };
}

/** An important date for a contact. Mirrors C# `ImportantDate` record. */
export interface ImportantDate {
  readonly dateId: string;
  readonly contactId: string;
  readonly kind: string;
  /** The date (C# `DateTime Date`). */
  readonly date: Date;
}

/** Constructs an {@link ImportantDate}. */
export function importantDate(dateId: string, contactId: string, kind: string, date: Date): ImportantDate {
  return { dateId, contactId, kind, date };
}

/** A recorded touchpoint with a contact. Mirrors C# `ContactEvent` record. */
export interface ContactEvent {
  readonly contactId: string;
  readonly kind: string;
  /** UTC instant of the touchpoint (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
  readonly note: string | null;
}

/** Constructs a {@link ContactEvent}. */
export function contactEvent(contactId: string, kind: string, atUtc: Date, note: string | null): ContactEvent {
  return { contactId, kind, atUtc, note };
}

/** The relationships board contract. Mirrors C# `IRelationshipsBoard`. */
export interface IRelationshipsBoard {
  addContact(c: PersonContact): void;
  getContact(id: string): PersonContact | undefined;
  readonly contacts: readonly PersonContact[];
  addImportantDate(d: ImportantDate): void;
  upcomingThisMonth(): readonly ImportantDate[];
  recordTouchpoint(e: ContactEvent): void;
  /** Most-recent touchpoint instant for the contact, or undefined. */
  lastContact(contactId: string): Date | undefined;
  notContactedSince(cutoff: Date): readonly PersonContact[];
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Deterministic in-memory {@link IRelationshipsBoard}. */
export class InMemoryRelationshipsBoard implements IRelationshipsBoard {
  private readonly contactsById = new Map<string, PersonContact>();
  private readonly dates = new Map<string, ImportantDate>();
  private readonly events: ContactEvent[] = [];

  addContact(c: PersonContact): void {
    if (c == null) throw new Error("c required");
    this.contactsById.set(c.contactId, c);
  }

  getContact(id: string): PersonContact | undefined {
    return this.contactsById.get(id);
  }

  get contacts(): readonly PersonContact[] {
    return [...this.contactsById.values()].sort((a, b) => ordinalCompare(a.name, b.name));
  }

  addImportantDate(d: ImportantDate): void {
    if (d == null) throw new Error("d required");
    this.dates.set(d.dateId, d);
  }

  upcomingThisMonth(): readonly ImportantDate[] {
    const month = new Date().getUTCMonth();
    return [...this.dates.values()]
      .filter((d) => d.date.getUTCMonth() === month)
      .sort((a, b) => a.date.getUTCDate() - b.date.getUTCDate());
  }

  recordTouchpoint(e: ContactEvent): void {
    if (e == null) throw new Error("e required");
    this.events.push(e);
  }

  lastContact(contactId: string): Date | undefined {
    const hit = this.events
      .filter((e) => e.contactId === contactId)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime())[0];
    return hit?.atUtc;
  }

  notContactedSince(cutoff: Date): readonly PersonContact[] {
    const cutoffMs = cutoff.getTime();
    return [...this.contactsById.values()].filter((c) => {
      const last = this.lastContact(c.contactId);
      return last === undefined || last.getTime() < cutoffMs;
    });
  }
}

/**
 * Static domain context for the Relationships vertical. Mirrors C#
 * `RelationshipsDomainContext`.
 */
export const RelationshipsDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Relationships] Empathetic relationship support companion. Help with communication strategies, conflict resolution (NVC principles), relationship goal-setting, and self-reflection prompts. Non-judgmental, no-advice-without-consent approach. Not a therapy service. Compliance: POPIA.",
  complianceFlags: ["POPIA", "Not_Therapy"] as readonly string[],
  suggestedTools: ["journal", "mood_tracker", "calendar"] as readonly string[],
} as const;
