// kids_board.test.ts
// Verifies the CircleAI.Kids port: age-banded content sorted by title, used-today
// (same UTC day) + over-limit for screen/reading (and no cap for other kinds).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryKidsBoard,
  KidsDomainContext,
  AgeAppropriateness,
  kidsContent,
  dailyTime,
  timeLog,
} from "../src/kids/index";

const MIN = 60_000; // one minute in ms

describe("InMemoryKidsBoard", () => {
  it("lists content in a band sorted by title", () => {
    const b = new InMemoryKidsBoard();
    b.addContent(kidsContent("c1", "Zebra", AgeAppropriateness.Toddler, "video", ["animals"]));
    b.addContent(kidsContent("c2", "Apple", AgeAppropriateness.Toddler, "book", ["food"]));
    b.addContent(kidsContent("c3", "Space", AgeAppropriateness.Teen, "game", ["science"]));
    assert.deepEqual(
      b.contentFor(AgeAppropriateness.Toddler).map((c) => c.contentId),
      ["c2", "c1"],
    );
    assert.equal(AgeAppropriateness.Toddler, 0);
    assert.equal(AgeAppropriateness.Teen, 5);
  });

  it("sums used-today only for the same UTC calendar day and matching kind", () => {
    const b = new InMemoryKidsBoard();
    const now = new Date("2026-01-10T20:00:00Z");
    b.recordTime(timeLog("Kai", "screen", 30 * MIN, new Date("2026-01-10T08:00:00Z")));
    b.recordTime(timeLog("Kai", "screen", 15 * MIN, new Date("2026-01-10T18:00:00Z")));
    b.recordTime(timeLog("Kai", "screen", 60 * MIN, new Date("2026-01-09T18:00:00Z"))); // yesterday
    b.recordTime(timeLog("Kai", "reading", 10 * MIN, new Date("2026-01-10T09:00:00Z")));
    assert.equal(b.usedToday("Kai", "screen", now), 45 * MIN);
    assert.equal(b.usedToday("Kai", "reading", now), 10 * MIN);
  });

  it("flags over-limit for screen/reading, false when no limits, no cap for other kinds", () => {
    const b = new InMemoryKidsBoard();
    const now = new Date("2026-01-10T20:00:00Z");
    // No limits set yet.
    b.recordTime(timeLog("Kai", "screen", 120 * MIN, new Date("2026-01-10T08:00:00Z")));
    assert.equal(b.overLimit("Kai", "screen", now), false);

    b.setLimits(dailyTime("Kai", 60 * MIN, 30 * MIN));
    assert.equal(b.limitsFor("Kai")?.screenLimitMs, 60 * MIN);
    assert.equal(b.overLimit("Kai", "screen", now), true); // 120 > 60
    // C# OverLimit selects the cap case-insensitively, but the usage total comes
    // from UsedToday, which matches Kind with an ordinal (case-sensitive) `==`
    // (KidsPrimitives.cs line 43). "SCREEN" therefore matches no "screen" logs →
    // used = 0 → 0 > 60min is false. The kind casing only affects the cap, not
    // which logs are summed.
    assert.equal(b.overLimit("Kai", "SCREEN", now), false);

    b.recordTime(timeLog("Kai", "reading", 10 * MIN, new Date("2026-01-10T09:00:00Z")));
    assert.equal(b.overLimit("Kai", "reading", now), false); // 10 <= 30

    // Unknown kind → no cap (TimeSpan.MaxValue), never over.
    b.recordTime(timeLog("Kai", "outdoor", 999 * MIN, new Date("2026-01-10T09:00:00Z")));
    assert.equal(b.overLimit("Kai", "outdoor", now), false);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(KidsDomainContext.systemPromptSnippet.includes("[DOMAIN: Kids]"));
    assert.deepEqual(KidsDomainContext.complianceFlags, [
      "POPIA_Childrens_Data",
      "COPPA_principles",
      "Childrens_Act",
      "CAPS_curriculum",
    ]);
    assert.deepEqual(KidsDomainContext.suggestedTools, ["educational_content", "story_tools", "quiz_tools"]);
  });
});
