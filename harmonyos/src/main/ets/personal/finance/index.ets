/**
 * personal/finance/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// personal/finance/index.ts
// Full-parity port of CircleAI.Personal.Finance (C#). C# is the exact spec.
//
// Domain types + in-memory store for personal finance: accounts, transactions
// (which move the account balance), budget lines, and a monthly summary. Plus
// the static PersonalFinanceDomainContext.
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ factory)
//   decimal                               → number
//   string? Note                          → string | null
//   DateTimeOffset AtUtc                  → Date (UTC instant)
//   IReadOnlyDictionary<string,decimal>   → ReadonlyMap<string, number>
//   accounts: ConcurrentDictionary (Ordinal)          → Map<string,Account>
//   budgets:  ConcurrentDictionary (OrdinalIgnoreCase) → case-insensitive map
//   List<FinanceTransaction> under a lock              → plain array
//
// PARITY NOTES:
//   Record(t)     — throws for an unknown account; else appends AND moves the
//                   account balance by t.Amount (positive credits, negative debits)
//   ListForMonth  — account + UTC Year/Month match (insertion order preserved)
//   Budgets       — case-insensitive keyed; listed sorted by Category (ordinal)
//   Summarise     — ByCategory = Σ Amount grouped by Category (group order = first
//                   appearance); TotalIn = Σ positive amounts; TotalOut = −Σ negatives

/** A personal account. Mirrors C# `Account` record. */
export interface Account {
  readonly accountId: string;
  readonly name: string;
  readonly balance: number;
  readonly currency: string;
}

/** Constructs an {@link Account}. */
export function account(accountId: string, name: string, balance: number, currency: string): Account {
  return { accountId, name, balance, currency };
}

/** A personal-finance transaction. Mirrors C# `FinanceTransaction` record. */
export interface FinanceTransaction {
  readonly txId: string;
  readonly accountId: string;
  readonly amount: number;
  readonly category: string;
  readonly note: string | null;
  readonly atUtc: Date;
}

/** Constructs a {@link FinanceTransaction}. */
export function financeTransaction(
  txId: string,
  accountId: string,
  amount: number,
  category: string,
  note: string | null,
  atUtc: Date,
): FinanceTransaction {
  return { txId, accountId, amount, category, note, atUtc };
}

/** A monthly budget cap for a category. Mirrors C# `BudgetLine` record. */
export interface BudgetLine {
  readonly category: string;
  readonly monthlyLimit: number;
}

/** Constructs a {@link BudgetLine}. */
export function budgetLine(category: string, monthlyLimit: number): BudgetLine {
  return { category, monthlyLimit };
}

/** A month rollup. Mirrors C# `MonthSummary` record. */
export interface MonthSummary {
  readonly year: number;
  readonly month: number;
  readonly totalIn: number;
  readonly totalOut: number;
  readonly byCategory: ReadonlyMap<string, number>;
}

/** Constructs a {@link MonthSummary}. */
export function monthSummary(
  year: number,
  month: number,
  totalIn: number,
  totalOut: number,
  byCategory: ReadonlyMap<string, number>,
): MonthSummary {
  return { year, month, totalIn, totalOut, byCategory };
}

/** The personal-finance board contract. */
export interface IPersonalFinanceBoard {
  upsert(a: Account): void;
  getAccount(id: string): Account | undefined;
  record(t: FinanceTransaction): void;
  listForMonth(accountId: string, year: number, month: number): readonly FinanceTransaction[];
  setBudget(b: BudgetLine): void;
  readonly budgets: readonly BudgetLine[];
  summarise(accountId: string, year: number, month: number): MonthSummary;
}

/** Deterministic in-memory {@link IPersonalFinanceBoard}. */
export class InMemoryPersonalFinanceBoard implements IPersonalFinanceBoard {
  private readonly accounts = new Map<string, Account>();
  /** Budget lines keyed case-insensitively (C# StringComparer.OrdinalIgnoreCase). */
  private readonly budgetsByKey = new Map<string, BudgetLine>();
  private readonly txns: FinanceTransaction[] = [];

  upsert(a: Account): void {
    if (a == null) throw new Error("a required");
    this.accounts.set(a.accountId, a);
  }

  getAccount(id: string): Account | undefined {
    return this.accounts.get(id);
  }

  record(t: FinanceTransaction): void {
    if (t == null) throw new Error("t required");
    const a = this.accounts.get(t.accountId);
    if (a === undefined) throw new Error(`Unknown account ${t.accountId}`);
    this.txns.push(t);
    this.accounts.set(t.accountId, { ...a, balance: a.balance + t.amount });
  }

  listForMonth(accountId: string, year: number, month: number): readonly FinanceTransaction[] {
    return this.txns.filter(
      (t) => t.accountId === accountId && t.atUtc.getUTCFullYear() === year && t.atUtc.getUTCMonth() + 1 === month,
    );
  }

  setBudget(b: BudgetLine): void {
    if (b == null) throw new Error("b required");
    this.budgetsByKey.set(b.category.toLowerCase(), b);
  }

  get budgets(): readonly BudgetLine[] {
    // C# `OrderBy(b => b.Category)` uses Comparer<string>.Default; for ASCII
    // category labels that coincides with ordinal code-unit ordering, which we
    // use here for deterministic, locale-independent results. The stored value
    // (and thus its Category casing) is the LAST write for a case-insensitive
    // key, exactly like the C# ConcurrentDictionary(OrdinalIgnoreCase) indexer.
    return [...this.budgetsByKey.values()].sort((a, b) => ordinalCompare(a.category, b.category));
  }

  summarise(accountId: string, year: number, month: number): MonthSummary {
    const rows = this.listForMonth(accountId, year, month);
    const byCat = new Map<string, number>();
    for (const t of rows) {
      byCat.set(t.category, (byCat.get(t.category) ?? 0) + t.amount);
    }
    const inSum = rows.filter((t) => t.amount > 0).reduce((acc, t) => acc + t.amount, 0);
    // C# negates a decimal sum; decimal has no negative zero, so normalise the
    // JS `-0` that `-(0)` would produce back to `+0` (adding 0 does this).
    const outSum = -rows.filter((t) => t.amount < 0).reduce((acc, t) => acc + t.amount, 0) + 0;
    return { year, month, totalIn: inSum, totalOut: outSum, byCategory: byCat };
  }
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * Static domain context for the Personal.Finance vertical. Mirrors C#
 * `PersonalFinanceDomainContext`.
 */
export const PersonalFinanceDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Personal.Finance] Personal finance coach. Help with monthly budgeting, emergency fund planning, debt snowball/avalanche strategy, savings goals, retirement planning basics, and investment options education. IMPORTANT: This is financial education, not advice. Recommend a registered financial planner for personalised investment advice. Compliance: FAIS Act, NCA, POPIA.",
  complianceFlags: ["FAIS_Act_37_2002", "NCA", "POPIA", "Not_Financial_Advice"] as readonly string[],
  suggestedTools: ["budget_tracker", "spreadsheet", "calculator", "web_search"] as readonly string[],
} as const;
