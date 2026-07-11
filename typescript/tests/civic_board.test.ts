// civic_board.test.ts
// Verifies the CircleAI.Civic port: report + resolve, open issues, reps by
// district, upcoming events.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryCivicBoard,
  CivicDomainContext,
  civicIssue,
  representative,
  civicEvent,
} from "../src/civic/index";

describe("InMemoryCivicBoard", () => {
  it("reports issues and lists those not resolved", () => {
    const b = new InMemoryCivicBoard();
    b.report(civicIssue("i1", "Water", "Burst pipe", -26, 28, new Date("2026-01-01T00:00:00Z"), "Open"));
    b.report(civicIssue("i2", "Roads", "Pothole", -26, 28, new Date("2026-01-01T00:00:00Z"), "Resolved"));
    b.report(civicIssue("i3", "Power", "Outage", -26, 28, new Date("2026-01-01T00:00:00Z"), "InProgress"));
    assert.deepEqual(
      b.openIssues().map((i) => i.issueId).sort(),
      ["i1", "i3"],
    );
  });

  it("resolve updates status (and hides from open) or throws", () => {
    const b = new InMemoryCivicBoard();
    b.report(civicIssue("i1", "Water", "Burst pipe", -26, 28, new Date("2026-01-01T00:00:00Z"), "Open"));
    b.resolve("i1", "Resolved");
    assert.equal(b.openIssues().length, 0);
    assert.throws(() => b.resolve("ghost", "Resolved"), /Unknown issue ghost/);
  });

  it("finds reps for a district case-insensitively", () => {
    const b = new InMemoryCivicBoard();
    b.addRep(representative("r1", "Ann", "Councillor", "ann@x", "Ward 5"));
    b.addRep(representative("r2", "Ben", "MP", "ben@x", null));
    b.addRep(representative("r3", "Cy", "Councillor", "cy@x", "ward 5"));
    assert.deepEqual(
      b.repsForDistrict("WARD 5").map((r) => r.repId).sort(),
      ["r1", "r3"],
    );
  });

  it("lists upcoming events ascending", () => {
    const b = new InMemoryCivicBoard();
    const future1 = new Date(Date.now() + 86_400_000);
    const future2 = new Date(Date.now() + 172_800_000);
    b.schedule(civicEvent("e1", "Meeting", future2, "Hall", "Public"));
    b.schedule(civicEvent("e2", "Cleanup", future1, "Park", "Public"));
    b.schedule(civicEvent("e3", "Old", new Date(Date.now() - 86_400_000), "Hall", "Public"));
    assert.deepEqual(
      b.upcomingEvents().map((e) => e.eventId),
      ["e2", "e1"],
    );
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(CivicDomainContext.systemPromptSnippet.includes("[DOMAIN: Civic]"));
    assert.deepEqual(CivicDomainContext.complianceFlags, [
      "PAJA",
      "PAIA",
      "Constitution_RSA",
      "Municipal_Systems_Act",
      "POPIA",
    ]);
    assert.deepEqual(CivicDomainContext.suggestedTools, ["government_portals", "document_editor", "map", "web_search"]);
  });
});
