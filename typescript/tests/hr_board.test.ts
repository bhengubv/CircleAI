// hr_board.test.ts
// Verifies the CircleAI.HR port: employee registry (name-ordered), leave
// requests + decisions, performance reviews with average rating, and context.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryHRBoard,
  HRDomainContext,
  employee,
  leaveRequest,
  performanceReview,
} from "../src/hr/index";

const D = (s: string) => new Date(s);

describe("InMemoryHRBoard", () => {
  it("hires and lists employees ordered by name; unknown id undefined", () => {
    const b = new InMemoryHRBoard();
    b.hire(employee("e2", "Zoe", "Dev", D("2020-01-01"), 100, "ZAR"));
    b.hire(employee("e1", "Ada", "Lead", D("2019-01-01"), 200, "ZAR"));
    assert.deepEqual(
      b.employees.map((e) => e.name),
      ["Ada", "Zoe"],
    );
    assert.equal(b.getEmployee("e1")?.role, "Lead");
    assert.equal(b.getEmployee("nope"), undefined);
  });

  it("tracks pending leave and applies decisions; unknown throws", () => {
    const b = new InMemoryHRBoard();
    b.request(leaveRequest("l1", "e1", "Annual", D("2026-01-01"), D("2026-01-05"), "Pending"));
    b.request(leaveRequest("l2", "e2", "Sick", D("2026-02-01"), D("2026-02-02"), "Approved"));
    assert.deepEqual(
      b.pendingLeaves().map((l) => l.requestId),
      ["l1"],
    );
    b.decideLeave("l1", "Approved");
    assert.deepEqual(b.pendingLeaves(), []);
    assert.throws(() => b.decideLeave("ghost", "X"), /Unknown leave request ghost/);
  });

  it("averages ratings; empty is 0", () => {
    const b = new InMemoryHRBoard();
    assert.equal(b.avgRatingFor("e1"), 0);
    b.review(performanceReview("r1", "e1", D("2026-01-01"), 4, ""));
    b.review(performanceReview("r2", "e1", D("2026-06-01"), 2, ""));
    b.review(performanceReview("r3", "e2", D("2026-01-01"), 5, ""));
    assert.equal(b.avgRatingFor("e1"), 3);
    assert.equal(b.avgRatingFor("e2"), 5);
  });

  it("rejects null arguments", () => {
    const b = new InMemoryHRBoard();
    assert.throws(() => b.hire(null as never));
    assert.throws(() => b.request(null as never));
    assert.throws(() => b.review(null as never));
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(HRDomainContext.systemPromptSnippet.includes("[DOMAIN: HR]"));
    assert.deepEqual(HRDomainContext.complianceFlags, ["LRA_66_1995", "BCEA", "EEA", "Skills_Development_Act", "POPIA"]);
    assert.deepEqual(HRDomainContext.suggestedTools, ["hris", "document_editor", "analytics", "job_boards"]);
  });
});
