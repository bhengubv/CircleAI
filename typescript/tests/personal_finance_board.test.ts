// personal_finance_board.test.ts
// Verifies the CircleAI.Personal.Finance port: account upsert, transaction
// recording (moves balance; unknown account throws), month listing, budgets
// (case-insensitive key, ordinal-sorted), and Summarise.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryPersonalFinanceBoard,
  PersonalFinanceDomainContext,
  account,
  financeTransaction,
  budgetLine,
} from "../src/personal/finance/index";

describe("InMemoryPersonalFinanceBoard — accounts + transactions", () => {
  it("record moves the balance and rejects unknown accounts", () => {
    const b = new InMemoryPersonalFinanceBoard();
    b.upsert(account("a1", "Cheque", 1000, "ZAR"));
    b.record(financeTransaction("t1", "a1", -200, "Groceries", null, new Date("2026-03-01T00:00:00Z")));
    b.record(financeTransaction("t2", "a1", 500, "Salary", "March", new Date("2026-03-02T00:00:00Z")));
    assert.equal(b.getAccount("a1")?.balance, 1300);
    assert.throws(
      () => b.record(financeTransaction("t3", "ghost", 1, "X", null, new Date())),
      /Unknown account ghost/,
    );
  });

  it("listForMonth filters by account and UTC year/month", () => {
    const b = new InMemoryPersonalFinanceBoard();
    b.upsert(account("a1", "Cheque", 0, "ZAR"));
    b.record(financeTransaction("t1", "a1", 10, "C", null, new Date("2026-03-15T00:00:00Z")));
    b.record(financeTransaction("t2", "a1", 20, "C", null, new Date("2026-04-15T00:00:00Z")));
    assert.deepEqual(
      b.listForMonth("a1", 2026, 3).map((t) => t.txId),
      ["t1"],
    );
  });
});

describe("InMemoryPersonalFinanceBoard — budgets", () => {
  it("budget keys are case-insensitive (last write wins) and listed ordinal-sorted", () => {
    const b = new InMemoryPersonalFinanceBoard();
    b.setBudget(budgetLine("Groceries", 3000));
    b.setBudget(budgetLine("groceries", 3500)); // same key (case-insensitive) → replaces value
    b.setBudget(budgetLine("Airtime", 500));
    const list = b.budgets;
    // Verified against C#: the retained value is the LAST write, so its Category
    // casing is "groceries" (lowercase). Order under the default comparer is
    // [Airtime, groceries] (matches ordinal for these ASCII labels).
    assert.deepEqual(
      list.map((x) => x.category),
      ["Airtime", "groceries"],
    );
    assert.equal(list.find((x) => x.category.toLowerCase() === "groceries")?.monthlyLimit, 3500);
  });
});

describe("InMemoryPersonalFinanceBoard — summarise", () => {
  it("computes TotalIn, TotalOut, and per-category totals", () => {
    const b = new InMemoryPersonalFinanceBoard();
    b.upsert(account("a1", "Cheque", 0, "ZAR"));
    b.record(financeTransaction("t1", "a1", 1000, "Salary", null, new Date("2026-03-01T00:00:00Z")));
    b.record(financeTransaction("t2", "a1", -200, "Groceries", null, new Date("2026-03-02T00:00:00Z")));
    b.record(financeTransaction("t3", "a1", -50, "Groceries", null, new Date("2026-03-03T00:00:00Z")));
    b.record(financeTransaction("t4", "a1", -100, "Transport", null, new Date("2026-03-04T00:00:00Z")));
    const s = b.summarise("a1", 2026, 3);
    assert.equal(s.totalIn, 1000);
    assert.equal(s.totalOut, 350);
    assert.equal(s.byCategory.get("Salary"), 1000);
    assert.equal(s.byCategory.get("Groceries"), -250);
    assert.equal(s.byCategory.get("Transport"), -100);
  });

  it("summarise of an empty month is all-zero with an empty map", () => {
    const b = new InMemoryPersonalFinanceBoard();
    b.upsert(account("a1", "Cheque", 0, "ZAR"));
    const s = b.summarise("a1", 2026, 1);
    assert.equal(s.totalIn, 0);
    assert.equal(s.totalOut, 0);
    assert.equal(s.byCategory.size, 0);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(PersonalFinanceDomainContext.systemPromptSnippet.includes("[DOMAIN: Personal.Finance]"));
    assert.deepEqual(PersonalFinanceDomainContext.complianceFlags, ["FAIS_Act_37_2002", "NCA", "POPIA", "Not_Financial_Advice"]);
    assert.deepEqual(PersonalFinanceDomainContext.suggestedTools, ["budget_tracker", "spreadsheet", "calculator", "web_search"]);
  });
});
