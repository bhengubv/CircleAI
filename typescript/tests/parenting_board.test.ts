// parenting_board.test.ts
// Verifies the CircleAI.Parenting port: children (name-ordered), milestones
// (achieved-desc), per-day routines keyed by DayOfWeek name, and age calc.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryParentingBoard,
  ParentingDomainContext,
  DayOfWeek,
  child,
  milestone,
  routine,
  routineEntry,
} from "../src/parenting/index";

const D = (s: string) => new Date(s);

describe("DayOfWeek enum", () => {
  it("matches C# System.DayOfWeek values (Sunday = 0)", () => {
    assert.equal(DayOfWeek.Sunday, 0);
    assert.equal(DayOfWeek.Monday, 1);
    assert.equal(DayOfWeek.Saturday, 6);
  });
});

describe("InMemoryParentingBoard", () => {
  it("adds children ordered by name", () => {
    const b = new InMemoryParentingBoard();
    b.addChild(child("c2", "Zara", D("2018-01-01"), "F"));
    b.addChild(child("c1", "Ali", D("2020-06-15"), "M"));
    assert.deepEqual(
      b.children.map((c) => c.name),
      ["Ali", "Zara"],
    );
    assert.equal(b.getChild("c1")?.gender, "M");
    assert.equal(b.getChild("nope"), undefined);
  });

  it("records milestones newest-first; blank childId throws; empty is []", () => {
    const b = new InMemoryParentingBoard();
    assert.deepEqual(b.milestonesFor("c1"), []);
    b.recordMilestone(milestone("m1", "c1", "Motor", "First steps", D("2021-06-01T00:00:00Z")));
    b.recordMilestone(milestone("m2", "c1", "Speech", "First word", D("2021-09-01T00:00:00Z")));
    assert.deepEqual(
      b.milestonesFor("c1").map((m) => m.milestoneId),
      ["m2", "m1"],
    );
    assert.throws(() => b.recordMilestone(milestone("m", "", "c", "d", D("2021-01-01T00:00:00Z"))), /ChildId required/);
  });

  it("stores and retrieves routines keyed by child + day", () => {
    const b = new InMemoryParentingBoard();
    const mon = routine("c1", DayOfWeek.Monday, [routineEntry("07:00", "Wake"), routineEntry("08:00", "School")]);
    b.setRoutine(mon);
    assert.deepEqual(b.getRoutine("c1", DayOfWeek.Monday)?.entries.length, 2);
    assert.equal(b.getRoutine("c1", DayOfWeek.Tuesday), undefined);
  });

  it("computes age as a duration in ms; unknown child throws", () => {
    const b = new InMemoryParentingBoard();
    b.addChild(child("c1", "Ali", D("2020-01-01T00:00:00Z"), "M"));
    const oneYearMs = D("2021-01-01T00:00:00Z").getTime() - D("2020-01-01T00:00:00Z").getTime();
    assert.equal(b.ageAsOf("c1", D("2021-01-01T00:00:00Z")), oneYearMs);
    assert.throws(() => b.ageAsOf("ghost", D("2021-01-01T00:00:00Z")), /Unknown child ghost/);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(ParentingDomainContext.systemPromptSnippet.includes("[DOMAIN: Parenting]"));
    assert.deepEqual(ParentingDomainContext.complianceFlags, ["Childrens_Act_38_2005", "POPIA"]);
    assert.deepEqual(ParentingDomainContext.suggestedTools, ["development_tracker", "document_editor", "web_search", "calendar"]);
  });
});
