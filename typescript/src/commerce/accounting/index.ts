// commerce/accounting/index.ts
// Full-parity port of CircleAI.Commerce.Accounting (C#). C# is the exact spec.
//
// Double-entry accounting board: post debit/credit entries, define tax rates,
// compute account balances and per-period sums, and a net-profit rollup. Plus
// the static CommerceAccountingDomainContext.
//
// Type mappings (C# → TS):
//   record            → readonly interface (+ positional factory)
//   decimal           → number
//   double Percentage → number
//   DateTime AtUtc    → Date (period matching uses UTC Year/Month, matching the
//                       UTC instants stored on entries)
//   List<AccountingEntry> under a lock → plain array (JS is single-threaded)
//
// PARITY NOTES:
//   Post           — rejects negative debit OR credit ("amounts must be non-negative")
//   AccountBalance — Σ (Debit − Credit) over the account
//   Sum(code, p)   — Σ (Debit − Credit) filtered to the account AND period
//   ForAccount     — same filter, sorted AtUtc ascending
//   NetProfit      — Sum(revenue,p) − Sum(expense,p)
//
// PERIOD MATCHING: C# compares `e.AtUtc.Year` / `.Month`. `AtUtc` is a UTC
// instant, so we read `getUTCFullYear()` and `getUTCMonth()+1` (Date months are
// 0-based) to stay faithful regardless of the host time zone.

/** A single ledger posting. Mirrors C# `AccountingEntry` record. */
export interface AccountingEntry {
  readonly entryId: string;
  readonly atUtc: Date;
  readonly accountCode: string;
  readonly debitAmount: number;
  readonly creditAmount: number;
  readonly memo: string;
}

/** Constructs an {@link AccountingEntry}. */
export function accountingEntry(
  entryId: string,
  atUtc: Date,
  accountCode: string,
  debitAmount: number,
  creditAmount: number,
  memo: string,
): AccountingEntry {
  return { entryId, atUtc, accountCode, debitAmount, creditAmount, memo };
}

/** A named tax rate. Mirrors C# `TaxRate` record. */
export interface TaxRate {
  readonly code: string;
  readonly percentage: number;
}

/** Constructs a {@link TaxRate}. */
export function taxRate(code: string, percentage: number): TaxRate {
  return { code, percentage };
}

/** A calendar period (year + 1-based month). Mirrors C# `Period` record. */
export interface Period {
  readonly year: number;
  readonly month: number;
}

/** Constructs a {@link Period}. */
export function period(year: number, month: number): Period {
  return { year, month };
}

/** The accounting board contract. */
export interface IAccountingBoard {
  post(e: AccountingEntry): void;
  defineTax(r: TaxRate): void;
  getTax(code: string): TaxRate | undefined;
  accountBalance(accountCode: string): number;
  sum(accountCode: string, p: Period): number;
  forAccount(accountCode: string, p: Period): readonly AccountingEntry[];
  netProfit(p: Period, revenueAccount: string, expenseAccount: string): number;
}

/** Deterministic in-memory {@link IAccountingBoard}. */
export class InMemoryAccountingBoard implements IAccountingBoard {
  private readonly entries: AccountingEntry[] = [];
  private readonly tax = new Map<string, TaxRate>();

  post(e: AccountingEntry): void {
    if (e == null) throw new Error("e required");
    if (e.debitAmount < 0 || e.creditAmount < 0) throw new Error("amounts must be non-negative");
    this.entries.push(e);
  }

  defineTax(r: TaxRate): void {
    if (r == null) throw new Error("r required");
    this.tax.set(r.code, r);
  }

  getTax(code: string): TaxRate | undefined {
    return this.tax.get(code);
  }

  accountBalance(accountCode: string): number {
    return this.entries
      .filter((e) => e.accountCode === accountCode)
      .reduce((acc, e) => acc + (e.debitAmount - e.creditAmount), 0);
  }

  sum(accountCode: string, p: Period): number {
    return this.entries
      .filter((e) => e.accountCode === accountCode && inPeriod(e.atUtc, p))
      .reduce((acc, e) => acc + (e.debitAmount - e.creditAmount), 0);
  }

  forAccount(accountCode: string, p: Period): readonly AccountingEntry[] {
    return this.entries
      .filter((e) => e.accountCode === accountCode && inPeriod(e.atUtc, p))
      .sort((a, b) => a.atUtc.getTime() - b.atUtc.getTime());
  }

  netProfit(p: Period, revenueAccount: string, expenseAccount: string): number {
    return this.sum(revenueAccount, p) - this.sum(expenseAccount, p);
  }
}

/** True when the UTC year+month of `at` match the period. */
function inPeriod(at: Date, p: Period): boolean {
  return at.getUTCFullYear() === p.year && at.getUTCMonth() + 1 === p.month;
}

/**
 * Static domain context for the Commerce.Accounting vertical. Mirrors C#
 * `CommerceAccountingDomainContext`.
 */
export const CommerceAccountingDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Commerce.Accounting] You are an expert accounting assistant. Help with bookkeeping, " +
    "bank reconciliation, VAT calculations (SA 15% standard rate), financial statement preparation, " +
    "cash flow analysis, and audit trail documentation. Cite relevant IFRS or GAAP standards. " +
    "Compliance: Companies Act 71 of 2008, SARS regulations, IFRS for SMEs.",
  complianceFlags: ["IFRS", "SARS", "Companies_Act_71_2008", "VAT_Act"] as readonly string[],
  suggestedTools: ["accounting_software", "spreadsheet", "document_editor"] as readonly string[],
} as const;
