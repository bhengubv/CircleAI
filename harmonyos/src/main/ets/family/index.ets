/**
 * family/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// family/index.ts
// Full-parity port of CircleAI.Family (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Family vertical: members, shared
// events (per-member view), and shared expenses with per-payer and per-category
// spend rollups since a cutoff. Plus the static FamilyDomainContext.
//
// NOTE: The C# FamilyCompanionAdapter (an ICompanionSession LLM-prompt wrapper)
// is intentionally NOT ported — consistent with the sibling domain-board ports
// (healthcare/education/legal/commerce).
//
// Type mappings (C# → TS):
//   record                          → readonly interface (+ positional factory)
//   decimal Amount                   → number
//   DateTime DateOfBirth             → Date
//   DateTimeOffset AtUtc            → Date
//   IReadOnlyList<string> MemberIds  → readonly string[]
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   Members          — ordered by Name ascending (default comparer / ordinal).
//   EventsForMember  — events whose MemberIds contains the id, AtUtc ascending.
//   TotalPaidBy      — Σ Amount of expenses paid by member with AtUtc >= since.
//   SpendByCategory  — Σ Amount of expenses in category (case-insensitive) with
//                      AtUtc >= since.

/** A family member. Mirrors C# `FamilyMember` record. */
export interface FamilyMember {
  readonly memberId: string;
  readonly name: string;
  readonly role: string;
  readonly dateOfBirth: Date;
}

/** Constructs a {@link FamilyMember}. */
export function familyMember(memberId: string, name: string, role: string, dateOfBirth: Date): FamilyMember {
  return { memberId, name, role, dateOfBirth };
}

/** A shared family event. Mirrors C# `FamilyEvent` record. */
export interface FamilyEvent {
  readonly eventId: string;
  readonly title: string;
  /** UTC instant of the event (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
  readonly memberIds: readonly string[];
}

/** Constructs a {@link FamilyEvent}. */
export function familyEvent(eventId: string, title: string, atUtc: Date, memberIds: readonly string[]): FamilyEvent {
  return { eventId, title, atUtc, memberIds };
}

/** A shared expense. Mirrors C# `SharedExpense` record. */
export interface SharedExpense {
  readonly expenseId: string;
  readonly paidById: string;
  readonly amount: number;
  readonly currency: string;
  readonly category: string;
  /** UTC instant of the expense (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link SharedExpense}. */
export function sharedExpense(
  expenseId: string,
  paidById: string,
  amount: number,
  currency: string,
  category: string,
  atUtc: Date,
): SharedExpense {
  return { expenseId, paidById, amount, currency, category, atUtc };
}

/** The family board contract. Mirrors C# `IFamilyBoard`. */
export interface IFamilyBoard {
  add(m: FamilyMember): void;
  getMember(id: string): FamilyMember | undefined;
  readonly members: readonly FamilyMember[];
  schedule(e: FamilyEvent): void;
  eventsForMember(memberId: string): readonly FamilyEvent[];
  record(e: SharedExpense): void;
  totalPaidBy(memberId: string, since: Date): number;
  spendByCategory(category: string, since: Date): number;
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Deterministic in-memory {@link IFamilyBoard}. */
export class InMemoryFamilyBoard implements IFamilyBoard {
  private readonly membersById = new Map<string, FamilyMember>();
  private readonly events = new Map<string, FamilyEvent>();
  private readonly expenses: SharedExpense[] = [];

  add(m: FamilyMember): void {
    if (m == null) throw new Error("m required");
    this.membersById.set(m.memberId, m);
  }

  getMember(id: string): FamilyMember | undefined {
    return this.membersById.get(id);
  }

  get members(): readonly FamilyMember[] {
    return [...this.membersById.values()].sort((a, b) => ordinalCompare(a.name, b.name));
  }

  schedule(e: FamilyEvent): void {
    if (e == null) throw new Error("e required");
    this.events.set(e.eventId, e);
  }

  eventsForMember(memberId: string): readonly FamilyEvent[] {
    return [...this.events.values()]
      .filter((e) => e.memberIds.includes(memberId))
      .sort((a, b) => a.atUtc.getTime() - b.atUtc.getTime());
  }

  record(e: SharedExpense): void {
    if (e == null) throw new Error("e required");
    this.expenses.push(e);
  }

  totalPaidBy(memberId: string, since: Date): number {
    return this.expenses
      .filter((e) => e.paidById === memberId && e.atUtc.getTime() >= since.getTime())
      .reduce((sum, e) => sum + e.amount, 0);
  }

  spendByCategory(category: string, since: Date): number {
    const target = category.toUpperCase();
    return this.expenses
      .filter((e) => e.category.toUpperCase() === target && e.atUtc.getTime() >= since.getTime())
      .reduce((sum, e) => sum + e.amount, 0);
  }
}

/**
 * Static domain context for the Family vertical. Mirrors C#
 * `FamilyDomainContext`.
 */
export const FamilyDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Family] Warm family life assistant. Help with shared calendar management, family budget tracking, activity planning, milestone documentation, and family communication strategies. Respect privacy boundaries — each family member's data is their own. Compliance: POPIA, Children's Act.",
  complianceFlags: ["POPIA", "Childrens_Act_38_2005"] as readonly string[],
  suggestedTools: ["shared_calendar", "family_budget", "document_editor", "task_manager"] as readonly string[],
} as const;
