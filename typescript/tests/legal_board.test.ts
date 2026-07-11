// legal_board.test.ts
// Verifies the CircleAI.Legal port: matters (open/close/active ordering),
// contracts expiring before a date, upcoming deadlines, and clause-by-tag.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryLegalBoard,
  LegalDomainContext,
  matter,
  contract,
  legalDeadline,
  clause,
} from "../src/legal/index";

describe("InMemoryLegalBoard — matters", () => {
  it("opens, closes, and lists active matters newest-first", () => {
    const b = new InMemoryLegalBoard();
    b.open(matter("m1", "Older", "ZA", "C1", new Date("2026-01-01T00:00:00Z"), true));
    b.open(matter("m2", "Newer", "ZA", "C2", new Date("2026-06-01T00:00:00Z"), true));
    b.open(matter("m3", "Closed", "ZA", "C3", new Date("2026-05-01T00:00:00Z"), true));
    b.close("m3");
    assert.equal(b.getMatter("m3")?.open, false);
    assert.deepEqual(
      b.activeMatters.map((m) => m.matterId),
      ["m2", "m1"],
    );
  });

  it("closing an unknown matter throws", () => {
    const b = new InMemoryLegalBoard();
    assert.throws(() => b.close("ghost"), /Unknown matter ghost/);
  });
});

describe("InMemoryLegalBoard — contracts", () => {
  it("contractsExpiringBefore includes only dated contracts <= date, sorted asc", () => {
    const b = new InMemoryLegalBoard();
    b.addContract(contract("c1", "m1", "A", new Date("2026-01-01"), new Date("2026-12-31"), ["X"]));
    b.addContract(contract("c2", "m1", "B", new Date("2026-01-01"), new Date("2026-03-31"), ["Y"]));
    b.addContract(contract("c3", "m1", "Perp", new Date("2026-01-01"), null, ["Z"]));
    b.addContract(contract("c4", "m1", "Late", new Date("2026-01-01"), new Date("2027-06-30"), ["W"]));
    const hits = b.contractsExpiringBefore(new Date("2026-12-31T23:59:59Z"));
    assert.deepEqual(
      hits.map((c) => c.contractId),
      ["c2", "c1"],
    );
  });
});

describe("InMemoryLegalBoard — deadlines", () => {
  it("upcomingDeadlines returns DueOn >= now, sorted ascending", () => {
    const b = new InMemoryLegalBoard();
    b.add(legalDeadline("d1", "m1", "past", new Date("2026-01-01")));
    b.add(legalDeadline("d2", "m1", "soon", new Date("2026-08-01")));
    b.add(legalDeadline("d3", "m1", "later", new Date("2026-12-01")));
    const up = b.upcomingDeadlines(new Date("2026-07-01"));
    assert.deepEqual(
      up.map((d) => d.deadlineId),
      ["d2", "d3"],
    );
  });
});

describe("InMemoryLegalBoard — clauses", () => {
  it("clausesByTag matches case-insensitively", () => {
    const b = new InMemoryLegalBoard();
    b.addClause(clause("cl1", "Indemnity", "…", ["Liability", "Risk"]));
    b.addClause(clause("cl2", "Warranty", "…", ["risk"]));
    b.addClause(clause("cl3", "Term", "…", ["Duration"]));
    assert.deepEqual(
      b.clausesByTag("RISK").map((c) => c.clauseId).sort(),
      ["cl1", "cl2"],
    );
  });

  it("a blank tag throws", () => {
    const b = new InMemoryLegalBoard();
    assert.throws(() => b.clausesByTag("   "), /tag required/);
  });
});

describe("LegalDomainContext", () => {
  it("exposes prompt + compliance + tools", () => {
    assert.ok(LegalDomainContext.systemPromptSnippet.includes("[DOMAIN: Legal]"));
    assert.deepEqual(LegalDomainContext.complianceFlags, [
      "Legal_Practice_Act_28_2014",
      "Attorneys_Act",
      "POPIA",
      "Professional_Legal_Privilege",
    ]);
    assert.deepEqual(LegalDomainContext.suggestedTools, ["legal_research", "document_editor", "contract_analyser"]);
  });
});
