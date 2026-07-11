// commerce/finance/index.ts
// Full-parity port of CircleAI.Commerce.Finance (C#). C# is the exact spec.
//
// Invoice board: issue invoices with tax-bearing lines, record payments, mark
// overdue, compute the remaining balance on an invoice and total outstanding,
// and list overdue invoices. Plus the static CommerceFinanceDomainContext.
//
// Type mappings (C# → TS):
//   record                  → readonly interface (+ positional factory)
//   decimal                 → number
//   double TaxPct           → number
//   DateTime / DateTimeOffset→ Date
//   IReadOnlyList<InvoiceLine> → readonly InvoiceLine[]
//   ConcurrentDictionary (Ordinal) + List under a lock → Map + array
//
// PARITY NOTES:
//   MarkOverdue(asOf) — every invoice with DueDate < asOf whose Status is not
//                       "Paid" (case-insensitive) becomes "Overdue"
//   RemainingOn(id)   — billed = Σ line.Amount × (1 + line.TaxPct/100); minus the
//                       sum of payments recorded against the invoice. Unknown
//                       invoice → 0. (C# computes (decimal)(1 + TaxPct/100.0);
//                       with JS doubles the expression is identical arithmetic.)
//   TotalOutstanding  — Σ RemainingOn over every known invoice id
//   Overdue()         — invoices whose Status == "Overdue" (case-insensitive)

/** A single tax-bearing line on an invoice. Mirrors C# `InvoiceLine` record. */
export interface InvoiceLine {
  readonly description: string;
  readonly amount: number;
  readonly taxPct: number;
}

/** Constructs an {@link InvoiceLine}. */
export function invoiceLine(description: string, amount: number, taxPct: number): InvoiceLine {
  return { description, amount, taxPct };
}

/** An invoice. Mirrors C# `Invoice` record. */
export interface Invoice {
  readonly invoiceId: string;
  readonly customerId: string;
  readonly issueDate: Date;
  readonly dueDate: Date;
  readonly lines: readonly InvoiceLine[];
  readonly currency: string;
  readonly status: string;
}

/** Constructs an {@link Invoice}. */
export function invoice(
  invoiceId: string,
  customerId: string,
  issueDate: Date,
  dueDate: Date,
  lines: readonly InvoiceLine[],
  currency: string,
  status: string,
): Invoice {
  return { invoiceId, customerId, issueDate, dueDate, lines, currency, status };
}

/** A payment recorded against an invoice. Mirrors C# `FinancePayment` record. */
export interface FinancePayment {
  readonly paymentId: string;
  readonly invoiceId: string;
  readonly amount: number;
  readonly atUtc: Date;
}

/** Constructs a {@link FinancePayment}. */
export function financePayment(paymentId: string, invoiceId: string, amount: number, atUtc: Date): FinancePayment {
  return { paymentId, invoiceId, amount, atUtc };
}

/** The invoice board contract. */
export interface IInvoiceBoard {
  issue(i: Invoice): void;
  get(invoiceId: string): Invoice | undefined;
  recordPayment(p: FinancePayment): void;
  markOverdue(asOf: Date): void;
  remainingOn(invoiceId: string): number;
  totalOutstanding(): number;
  overdue(): readonly Invoice[];
}

/** Deterministic in-memory {@link IInvoiceBoard}. */
export class InMemoryInvoiceBoard implements IInvoiceBoard {
  private readonly invoices = new Map<string, Invoice>();
  private readonly payments: FinancePayment[] = [];

  issue(i: Invoice): void {
    if (i == null) throw new Error("i required");
    this.invoices.set(i.invoiceId, i);
  }

  get(invoiceId: string): Invoice | undefined {
    return this.invoices.get(invoiceId);
  }

  recordPayment(p: FinancePayment): void {
    if (p == null) throw new Error("p required");
    this.payments.push(p);
  }

  markOverdue(asOf: Date): void {
    const cutoff = asOf.getTime();
    for (const i of [...this.invoices.values()]) {
      if (i.dueDate.getTime() < cutoff && i.status.toLowerCase() !== "paid") {
        this.invoices.set(i.invoiceId, { ...i, status: "Overdue" });
      }
    }
  }

  remainingOn(invoiceId: string): number {
    const inv = this.invoices.get(invoiceId);
    if (inv === undefined) return 0;
    const billed = inv.lines.reduce((acc, l) => acc + l.amount * (1 + l.taxPct / 100.0), 0);
    const paid = this.payments.filter((p) => p.invoiceId === invoiceId).reduce((acc, p) => acc + p.amount, 0);
    return billed - paid;
  }

  totalOutstanding(): number {
    return [...this.invoices.keys()].reduce((acc, id) => acc + this.remainingOn(id), 0);
  }

  overdue(): readonly Invoice[] {
    return [...this.invoices.values()].filter((i) => i.status.toLowerCase() === "overdue");
  }
}

/**
 * Static domain context for the Commerce.Finance vertical. Mirrors C#
 * `CommerceFinanceDomainContext`.
 */
export const CommerceFinanceDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Commerce.Finance] You are a commercial finance expert. Help with working capital " +
    "optimisation, cash flow forecasting, business credit applications, debt structuring, and " +
    "treasury policy. Ground advice in the cash conversion cycle and credit profile. " +
    "Compliance: NCA (National Credit Act 34 of 2005), SARB prudential rules, POPIA.",
  complianceFlags: ["NCA_34_2005", "SARB_aware", "POPIA", "IFRS"] as readonly string[],
  suggestedTools: ["cash_flow_model", "spreadsheet", "web_search"] as readonly string[],
} as const;
