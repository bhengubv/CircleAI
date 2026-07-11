// commerce_accounting_board.test.ts
// Verifies the CircleAI.Commerce.Accounting port: postings with debit/credit,
// balance = Σ(debit − credit), per-period sums, ForAccount ordering, net profit,
// and tax-rate storage.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryAccountingBoard,
  CommerceAccountingDomainContext,
  accountingEntry,
  taxRate,
  period,
} from "../src/commerce/accounting/index";

describe("InMemoryAccountingBoard", () => {
  it("balance is Σ(debit − credit) over the account", () => {
    const b = new InMemoryAccountingBoard();
    b.post(accountingEntry("e1", new Date("2026-03-01T00:00:00Z"), "4000", 100, 0, "sale"));
    b.post(accountingEntry("e2", new Date("2026-03-05T00:00:00Z"), "4000", 0, 30, "refund"));
    b.post(accountingEntry("e3", new Date("2026-03-05T00:00:00Z"), "5000", 20, 0, "expense"));
    assert.equal(b.accountBalance("4000"), 70);
    assert.equal(b.accountBalance("5000"), 20);
  });

  it("post rejects negative debit or credit", () => {
    const b = new InMemoryAccountingBoard();
    assert.throws(() => b.post(accountingEntry("x", new Date(), "4000", -1, 0, "m")), /non-negative/);
    assert.throws(() => b.post(accountingEntry("x", new Date(), "4000", 0, -1, "m")), /non-negative/);
  });

  it("sum + ForAccount filter to the account AND UTC period; ForAccount is time-ordered", () => {
    const b = new InMemoryAccountingBoard();
    b.post(accountingEntry("m2", new Date("2026-03-15T00:00:00Z"), "4000", 50, 0, "mid"));
    b.post(accountingEntry("m1", new Date("2026-03-01T00:00:00Z"), "4000", 10, 0, "early"));
    b.post(accountingEntry("apr", new Date("2026-04-01T00:00:00Z"), "4000", 999, 0, "other-month"));
    const p = period(2026, 3);
    assert.equal(b.sum("4000", p), 60);
    assert.deepEqual(
      b.forAccount("4000", p).map((e) => e.entryId),
      ["m1", "m2"],
    );
  });

  it("netProfit = revenue period sum − expense period sum", () => {
    const b = new InMemoryAccountingBoard();
    b.post(accountingEntry("r", new Date("2026-03-10T00:00:00Z"), "4000", 500, 0, "rev"));
    b.post(accountingEntry("x", new Date("2026-03-10T00:00:00Z"), "5000", 200, 0, "exp"));
    assert.equal(b.netProfit(period(2026, 3), "4000", "5000"), 300);
  });

  it("tax rates are stored and retrieved by code", () => {
    const b = new InMemoryAccountingBoard();
    b.defineTax(taxRate("VAT", 15));
    assert.equal(b.getTax("VAT")?.percentage, 15);
    assert.equal(b.getTax("ZERO"), undefined);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(CommerceAccountingDomainContext.systemPromptSnippet.includes("[DOMAIN: Commerce.Accounting]"));
    assert.deepEqual(CommerceAccountingDomainContext.complianceFlags, ["IFRS", "SARS", "Companies_Act_71_2008", "VAT_Act"]);
    assert.deepEqual(CommerceAccountingDomainContext.suggestedTools, ["accounting_software", "spreadsheet", "document_editor"]);
  });
});
