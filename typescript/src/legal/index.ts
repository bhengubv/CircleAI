// legal/index.ts
// Full-parity port of CircleAI.Legal (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Legal vertical: matters, contracts,
// deadlines, and a clause library. Plus the static LegalDomainContext.
//
// Type mappings (C# → TS):
//   record                        → readonly interface (+ positional factory)
//   DateTime / DateTimeOffset     → Date
//   DateTime? ExpiryDate          → Date | null
//   IReadOnlyList<string>         → readonly string[]
//   ConcurrentDictionary (Ordinal)→ Map<string,T>
//
// ORDERING / FILTER PARITY:
//   ActiveMatters           — Open only, sorted OpenedAtUtc descending
//   ContractsExpiringBefore — ExpiryDate present AND <= date, sorted asc
//   UpcomingDeadlines(now)  — DueOn >= now, sorted asc
//   ClausesByTag            — case-insensitive tag membership; blank tag throws

/** A legal matter. Mirrors C# `Matter` record. */
export interface Matter {
  readonly matterId: string;
  readonly title: string;
  readonly jurisdiction: string;
  readonly client: string;
  readonly openedAtUtc: Date;
  readonly open: boolean;
}

/** Constructs a {@link Matter}. */
export function matter(
  matterId: string,
  title: string,
  jurisdiction: string,
  client: string,
  openedAtUtc: Date,
  open: boolean,
): Matter {
  return { matterId, title, jurisdiction, client, openedAtUtc, open };
}

/** A contract attached to a matter. Mirrors C# `Contract` record. */
export interface Contract {
  readonly contractId: string;
  readonly matterId: string;
  readonly title: string;
  readonly effectiveDate: Date;
  readonly expiryDate: Date | null;
  readonly counterparties: readonly string[];
}

/** Constructs a {@link Contract}. */
export function contract(
  contractId: string,
  matterId: string,
  title: string,
  effectiveDate: Date,
  expiryDate: Date | null,
  counterparties: readonly string[],
): Contract {
  return { contractId, matterId, title, effectiveDate, expiryDate, counterparties };
}

/** A tracked legal deadline. Mirrors C# `LegalDeadline` record. */
export interface LegalDeadline {
  readonly deadlineId: string;
  readonly matterId: string;
  readonly description: string;
  readonly dueOn: Date;
}

/** Constructs a {@link LegalDeadline}. */
export function legalDeadline(deadlineId: string, matterId: string, description: string, dueOn: Date): LegalDeadline {
  return { deadlineId, matterId, description, dueOn };
}

/** A reusable clause in the clause library. Mirrors C# `Clause` record. */
export interface Clause {
  readonly clauseId: string;
  readonly title: string;
  readonly body: string;
  readonly tags: readonly string[];
}

/** Constructs a {@link Clause}. */
export function clause(clauseId: string, title: string, body: string, tags: readonly string[]): Clause {
  return { clauseId, title, body, tags };
}

/** The legal board contract. */
export interface ILegalBoard {
  open(m: Matter): void;
  close(matterId: string): void;
  getMatter(id: string): Matter | undefined;
  readonly activeMatters: readonly Matter[];
  addContract(c: Contract): void;
  contractsExpiringBefore(date: Date): readonly Contract[];
  add(d: LegalDeadline): void;
  upcomingDeadlines(now: Date): readonly LegalDeadline[];
  addClause(c: Clause): void;
  clausesByTag(tag: string): readonly Clause[];
}

/** Deterministic in-memory {@link ILegalBoard}. */
export class InMemoryLegalBoard implements ILegalBoard {
  private readonly matters = new Map<string, Matter>();
  private readonly contracts = new Map<string, Contract>();
  private readonly deadlines = new Map<string, LegalDeadline>();
  private readonly clauses = new Map<string, Clause>();

  open(m: Matter): void {
    if (m == null) throw new Error("m required");
    this.matters.set(m.matterId, m);
  }

  close(matterId: string): void {
    const m = this.matters.get(matterId);
    if (m === undefined) throw new Error(`Unknown matter ${matterId}`);
    this.matters.set(matterId, { ...m, open: false });
  }

  getMatter(id: string): Matter | undefined {
    return this.matters.get(id);
  }

  get activeMatters(): readonly Matter[] {
    return [...this.matters.values()]
      .filter((m) => m.open)
      .sort((a, b) => b.openedAtUtc.getTime() - a.openedAtUtc.getTime());
  }

  addContract(c: Contract): void {
    if (c == null) throw new Error("c required");
    this.contracts.set(c.contractId, c);
  }

  contractsExpiringBefore(date: Date): readonly Contract[] {
    const cutoff = date.getTime();
    return [...this.contracts.values()]
      .filter((c) => c.expiryDate !== null && c.expiryDate.getTime() <= cutoff)
      .sort((a, b) => a.expiryDate!.getTime() - b.expiryDate!.getTime());
  }

  add(d: LegalDeadline): void {
    if (d == null) throw new Error("d required");
    this.deadlines.set(d.deadlineId, d);
  }

  upcomingDeadlines(now: Date): readonly LegalDeadline[] {
    const from = now.getTime();
    return [...this.deadlines.values()]
      .filter((d) => d.dueOn.getTime() >= from)
      .sort((a, b) => a.dueOn.getTime() - b.dueOn.getTime());
  }

  addClause(c: Clause): void {
    if (c == null) throw new Error("c required");
    this.clauses.set(c.clauseId, c);
  }

  clausesByTag(tag: string): readonly Clause[] {
    if (tag == null || tag.trim().length === 0) throw new Error("tag required");
    const lower = tag.toLowerCase();
    return [...this.clauses.values()].filter((c) => c.tags.some((t) => t.toLowerCase() === lower));
  }
}

/**
 * Static domain context for the Legal vertical. Mirrors C# `LegalDomainContext`.
 */
export const LegalDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Legal] You are a legal knowledge and compliance assistant. Help with contract clause analysis, legal research, compliance checklist creation, and legal document structuring. IMPORTANT: This is not legal advice. Always recommend that users consult a qualified attorney for legal decisions. Compliance: Legal Practice Act, LPA 28/2014, Attorneys Act, POPIA.",
  complianceFlags: [
    "Legal_Practice_Act_28_2014",
    "Attorneys_Act",
    "POPIA",
    "Professional_Legal_Privilege",
  ] as readonly string[],
  suggestedTools: ["legal_research", "document_editor", "contract_analyser"] as readonly string[],
} as const;
