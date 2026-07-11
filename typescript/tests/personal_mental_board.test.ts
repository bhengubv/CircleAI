// personal_mental_board.test.ts
// Verifies the CircleAI.Personal.Mental port: mood logging + 7-day window and
// average (NaN when empty), journal entries (blank id throws, newest-first),
// and coping strategies by tag. The 7-day clock is injected for determinism.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryMentalHealthBoard,
  PersonalMentalDomainContext,
  Mood,
  moodLog,
  journalEntry,
  copingStrategy,
} from "../src/personal/mental/index";

const NOW = new Date("2026-07-10T12:00:00Z");
function board(): InMemoryMentalHealthBoard {
  const b = new InMemoryMentalHealthBoard();
  b.nowUtc = () => NOW;
  return b;
}

describe("Mood ordinals", () => {
  it("VeryLow=0 .. Great=4", () => {
    assert.equal(Mood.VeryLow, 0);
    assert.equal(Mood.Low, 1);
    assert.equal(Mood.Neutral, 2);
    assert.equal(Mood.Good, 3);
    assert.equal(Mood.Great, 4);
  });
});

describe("InMemoryMentalHealthBoard — mood", () => {
  it("Last7Days keeps entries within the window, sorted ascending", () => {
    const b = board();
    const withinA = new Date(NOW.getTime() - 6 * 24 * 3600 * 1000);
    const withinB = new Date(NOW.getTime() - 1 * 24 * 3600 * 1000);
    const old = new Date(NOW.getTime() - 8 * 24 * 3600 * 1000);
    b.logMood(moodLog(Mood.Good, withinB, null));
    b.logMood(moodLog(Mood.Low, withinA, "rough"));
    b.logMood(moodLog(Mood.Great, old, "too old"));
    assert.deepEqual(
      b.last7Days().map((m) => m.mood),
      [Mood.Low, Mood.Good],
    );
  });

  it("AvgMood7Day averages the ordinals; NaN when there are none", () => {
    const b = board();
    assert.ok(Number.isNaN(b.avgMood7Day()));
    b.logMood(moodLog(Mood.Neutral, new Date(NOW.getTime() - 3600 * 1000), null)); // 2
    b.logMood(moodLog(Mood.Great, new Date(NOW.getTime() - 7200 * 1000), null)); // 4
    assert.equal(b.avgMood7Day(), 3);
  });
});

describe("InMemoryMentalHealthBoard — journal", () => {
  it("entries list newest-first; blank id throws", () => {
    const b = board();
    b.addEntry(journalEntry("e1", "First", "…", new Date("2026-07-01T00:00:00Z")));
    b.addEntry(journalEntry("e2", "Second", "…", new Date("2026-07-05T00:00:00Z")));
    assert.deepEqual(
      b.entries.map((e) => e.entryId),
      ["e2", "e1"],
    );
    assert.throws(() => b.addEntry(journalEntry("   ", "X", "Y", new Date())), /EntryId required/);
  });
});

describe("InMemoryMentalHealthBoard — coping strategies", () => {
  it("StrategiesByTag matches case-insensitively; blank tag throws", () => {
    const b = board();
    b.registerStrategy(copingStrategy("s1", "Box Breathing", "…", ["Anxiety", "Grounding"]));
    b.registerStrategy(copingStrategy("s2", "5-4-3-2-1", "…", ["grounding"]));
    b.registerStrategy(copingStrategy("s3", "Gratitude", "…", ["Mood"]));
    assert.deepEqual(
      b.strategiesByTag("GROUNDING").map((s) => s.strategyId).sort(),
      ["s1", "s2"],
    );
    assert.throws(() => b.strategiesByTag(" "), /tag required/);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(PersonalMentalDomainContext.systemPromptSnippet.includes("[DOMAIN: Personal.Mental]"));
    assert.deepEqual(PersonalMentalDomainContext.complianceFlags, [
      "POPIA",
      "Mental_Health_Care_Act_17_2002",
      "Not_Therapy",
      "Crisis_Protocol",
    ]);
    assert.deepEqual(PersonalMentalDomainContext.suggestedTools, ["journal", "breathing_tools", "mood_tracker", "web_search"]);
  });
});
