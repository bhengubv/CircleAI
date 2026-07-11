// construction_board.test.ts
// Verifies the CircleAI.Construction port: open tasks by due date, complete,
// spend + remaining budget.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryConstructionBoard,
  ConstructionDomainContext,
  project,
  constructionTask,
  costEntry,
} from "../src/construction/index";

describe("InMemoryConstructionBoard", () => {
  it("lists open tasks for a project ordered by due date; complete closes them", () => {
    const b = new InMemoryConstructionBoard();
    b.create(project("p1", "House", new Date("2026-01-01T00:00:00Z"), null, 1_000_000, "ZAR"));
    b.add(constructionTask("t1", "p1", "Foundation", new Date("2026-02-01T00:00:00Z"), false));
    b.add(constructionTask("t2", "p1", "Roof", new Date("2026-01-15T00:00:00Z"), false));
    b.add(constructionTask("t3", "p1", "Paint", new Date("2026-03-01T00:00:00Z"), true)); // already done
    assert.equal(b.getProject("p1")?.name, "House");
    assert.deepEqual(
      b.openConstructionTasksFor("p1").map((t) => t.constructionTaskId),
      ["t2", "t1"],
    );
    b.complete("t2");
    assert.deepEqual(
      b.openConstructionTasksFor("p1").map((t) => t.constructionTaskId),
      ["t1"],
    );
  });

  it("complete throws on unknown task", () => {
    const b = new InMemoryConstructionBoard();
    assert.throws(() => b.complete("ghost"), /Unknown task ghost/);
  });

  it("sums spend and computes remaining budget; unknown project throws", () => {
    const b = new InMemoryConstructionBoard();
    b.create(project("p1", "House", new Date("2026-01-01T00:00:00Z"), null, 1000, "ZAR"));
    b.recordCost(costEntry("c1", "p1", "Materials", 300, new Date("2026-01-05T00:00:00Z")));
    b.recordCost(costEntry("c2", "p1", "Labour", 200, new Date("2026-01-06T00:00:00Z")));
    b.recordCost(costEntry("c3", "p2", "Other", 999, new Date("2026-01-06T00:00:00Z")));
    assert.equal(b.spendFor("p1"), 500);
    assert.equal(b.remainingBudget("p1"), 500);
    assert.throws(() => b.remainingBudget("ghost"), /Unknown project ghost/);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(ConstructionDomainContext.systemPromptSnippet.includes("[DOMAIN: Construction]"));
    assert.deepEqual(ConstructionDomainContext.complianceFlags, [
      "OHS_Act",
      "NHBRC_Act",
      "CIDB_Act",
      "National_Building_Regs",
      "POPIA",
    ]);
    assert.deepEqual(ConstructionDomainContext.suggestedTools, ["project_scheduler", "document_editor", "map", "analytics"]);
  });
});
