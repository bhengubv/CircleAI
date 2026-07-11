// commerce_finance_board.test.ts
// Verifies the CircleAI.Commerce.Finance port: invoice issue, payment recording,
// billed = Σ line.Amount × (1 + TaxPct/100), remaining/outstanding, overdue.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryInvoiceBoard,
  CommerceFinanceDomainContext,
  invoice,
  invoiceLine,
  financePayment,
} from "../src/commerce/finance/index";

describe("InMemoryInvoiceBoard", () => {
  it("remainingOn = billed (with tax) − payments; unknown invoice is 0", () => {
    const b = new InMemoryInvoiceBoard();
    b.issue(
      invoice(
        "i1",
        "c1",
        new Date("2026-01-01"),
        new Date("2026-02-01"),
        [invoiceLine("Item", 100, 15), invoiceLine("Item2", 50, 0)],
        "ZAR",
        "Issued",
      ),
    );
    // billed = 100*1.15 + 50*1.0 = 115 + 50 = 165
    assert.equal(b.remainingOn("i1"), 165);
    b.recordPayment(financePayment("p1", "i1", 65, new Date("2026-01-15T00:00:00Z")));
    assert.equal(b.remainingOn("i1"), 100);
    assert.equal(b.remainingOn("ghost"), 0);
  });

  it("totalOutstanding sums remaining across all invoices", () => {
    const b = new InMemoryInvoiceBoard();
    b.issue(invoice("i1", "c1", new Date("2026-01-01"), new Date("2026-02-01"), [invoiceLine("A", 100, 0)], "ZAR", "Issued"));
    b.issue(invoice("i2", "c1", new Date("2026-01-01"), new Date("2026-02-01"), [invoiceLine("B", 200, 0)], "ZAR", "Issued"));
    b.recordPayment(financePayment("p1", "i1", 40, new Date()));
    assert.equal(b.totalOutstanding(), 60 + 200);
  });

  it("markOverdue flips DueDate<asOf non-paid invoices to Overdue; Overdue() lists them", () => {
    const b = new InMemoryInvoiceBoard();
    b.issue(invoice("i1", "c1", new Date("2026-01-01"), new Date("2026-02-01"), [invoiceLine("A", 100, 0)], "ZAR", "Issued"));
    b.issue(invoice("i2", "c1", new Date("2026-01-01"), new Date("2026-02-01"), [invoiceLine("B", 100, 0)], "ZAR", "Paid"));
    b.issue(invoice("i3", "c1", new Date("2026-06-01"), new Date("2026-12-01"), [invoiceLine("C", 100, 0)], "ZAR", "Issued"));
    b.markOverdue(new Date("2026-07-01"));
    assert.deepEqual(
      b.overdue().map((i) => i.invoiceId),
      ["i1"],
    );
    // "Paid" (case-insensitive) is never marked overdue; future-due stays Issued.
    assert.equal(b.get("i2")?.status, "Paid");
    assert.equal(b.get("i3")?.status, "Issued");
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(CommerceFinanceDomainContext.systemPromptSnippet.includes("[DOMAIN: Commerce.Finance]"));
    assert.deepEqual(CommerceFinanceDomainContext.complianceFlags, ["NCA_34_2005", "SARB_aware", "POPIA", "IFRS"]);
    assert.deepEqual(CommerceFinanceDomainContext.suggestedTools, ["cash_flow_model", "spreadsheet", "web_search"]);
  });
});
