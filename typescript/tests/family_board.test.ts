// family_board.test.ts
// Verifies the CircleAI.Family port: members (name-ordered), per-member events,
// shared expense rollups by payer and by category since a cutoff.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryFamilyBoard,
  FamilyDomainContext,
  familyMember,
  familyEvent,
  sharedExpense,
} from "../src/family/index";

const D = (s: string) => new Date(s);

describe("InMemoryFamilyBoard", () => {
  it("adds members ordered by name", () => {
    const b = new InMemoryFamilyBoard();
    b.add(familyMember("m2", "Bob", "Parent", D("1980-01-01")));
    b.add(familyMember("m1", "Amy", "Parent", D("1982-01-01")));
    assert.deepEqual(
      b.members.map((m) => m.name),
      ["Amy", "Bob"],
    );
    assert.equal(b.getMember("m1")?.role, "Parent");
    assert.equal(b.getMember("nope"), undefined);
  });

  it("returns events containing a member ordered by time", () => {
    const b = new InMemoryFamilyBoard();
    b.schedule(familyEvent("e1", "Dinner", D("2026-03-01T18:00:00Z"), ["m1", "m2"]));
    b.schedule(familyEvent("e2", "Recital", D("2026-01-01T18:00:00Z"), ["m1"]));
    b.schedule(familyEvent("e3", "Golf", D("2026-02-01T18:00:00Z"), ["m2"]));
    assert.deepEqual(
      b.eventsForMember("m1").map((e) => e.eventId),
      ["e2", "e1"],
    );
  });

  it("sums shared expenses by payer and by category since a cutoff", () => {
    const b = new InMemoryFamilyBoard();
    b.record(sharedExpense("x1", "m1", 100, "ZAR", "Groceries", D("2026-05-01T00:00:00Z")));
    b.record(sharedExpense("x2", "m1", 50, "ZAR", "Fuel", D("2026-05-10T00:00:00Z")));
    b.record(sharedExpense("x3", "m2", 200, "ZAR", "groceries", D("2026-05-15T00:00:00Z")));
    b.record(sharedExpense("x4", "m1", 999, "ZAR", "Groceries", D("2026-04-01T00:00:00Z"))); // before cutoff
    const since = D("2026-05-01T00:00:00Z");
    assert.equal(b.totalPaidBy("m1", since), 150);
    assert.equal(b.spendByCategory("GROCERIES", since), 300); // case-insensitive: 100 + 200
  });

  it("rejects null arguments", () => {
    const b = new InMemoryFamilyBoard();
    assert.throws(() => b.add(null as never));
    assert.throws(() => b.schedule(null as never));
    assert.throws(() => b.record(null as never));
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(FamilyDomainContext.systemPromptSnippet.includes("[DOMAIN: Family]"));
    assert.deepEqual(FamilyDomainContext.complianceFlags, ["POPIA", "Childrens_Act_38_2005"]);
    assert.deepEqual(FamilyDomainContext.suggestedTools, ["shared_calendar", "family_budget", "document_editor", "task_manager"]);
  });
});
