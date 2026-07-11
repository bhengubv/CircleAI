// business_board.test.ts
// Verifies the CircleAI.Business port: unit hierarchy, latest-KPI lookup with
// NaN fallback, quarterly target achievement ratio, and context.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryBusinessBoard,
  BusinessDomainContext,
  businessUnit,
  kpiSample,
  quarterTarget,
} from "../src/business/index";

const D = (s: string) => new Date(s);

describe("InMemoryBusinessBoard", () => {
  it("adds units and finds children", () => {
    const b = new InMemoryBusinessBoard();
    b.add(businessUnit("root", "Group", "", ["rev"]));
    b.add(businessUnit("u1", "Sales", "root", ["rev"]));
    b.add(businessUnit("u2", "Eng", "root", ["velocity"]));
    assert.equal(b.getUnit("u1")?.name, "Sales");
    assert.equal(b.getUnit("nope"), undefined);
    assert.deepEqual(
      b.childrenOf("root").map((u) => u.unitId),
      ["u1", "u2"],
    );
  });

  it("returns the newest KPI value, or NaN when none", () => {
    const b = new InMemoryBusinessBoard();
    assert.ok(Number.isNaN(b.latestKpi("u1", "rev")));
    b.record(kpiSample("u1", "rev", 10, D("2026-01-01T00:00:00Z")));
    b.record(kpiSample("u1", "rev", 30, D("2026-03-01T00:00:00Z")));
    b.record(kpiSample("u1", "cost", 5, D("2026-03-01T00:00:00Z")));
    assert.equal(b.latestKpi("u1", "rev"), 30);
    assert.equal(b.latestKpi("u1", "cost"), 5);
  });

  it("computes target achievement, NaN when missing or zero target", () => {
    const b = new InMemoryBusinessBoard();
    b.record(kpiSample("u1", "rev", 75, D("2026-03-01T00:00:00Z")));
    b.setTarget(quarterTarget("u1", "rev", 2026, 1, 100));
    assert.equal(b.targetAchievement("u1", "rev", 2026, 1), 0.75);
    assert.ok(Number.isNaN(b.targetAchievement("u1", "rev", 2026, 2))); // no target
    b.setTarget(quarterTarget("u1", "rev", 2026, 3, 0));
    assert.ok(Number.isNaN(b.targetAchievement("u1", "rev", 2026, 3))); // zero target
  });

  it("rejects null arguments", () => {
    const b = new InMemoryBusinessBoard();
    assert.throws(() => b.add(null as never));
    assert.throws(() => b.record(null as never));
    assert.throws(() => b.setTarget(null as never));
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(BusinessDomainContext.systemPromptSnippet.includes("[DOMAIN: Business]"));
    assert.deepEqual(BusinessDomainContext.complianceFlags, ["POPIA", "Commercial_Law", "GDPR_aware"]);
    assert.deepEqual(BusinessDomainContext.suggestedTools, ["calendar", "web_search", "document_editor", "task_manager"]);
  });
});
