// gaming_board.test.ts
// Verifies the CircleAI.Gaming port: titles by genre, total play time,
// achievements newest-first, most-played ranking.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryGamingBoard,
  GamingDomainContext,
  gameTitle,
  playSession,
  achievementUnlock,
} from "../src/gaming/index";

describe("InMemoryGamingBoard", () => {
  it("adds titles and finds by genre case-insensitively", () => {
    const b = new InMemoryGamingBoard();
    b.addTitle(gameTitle("t1", "Speedster", "Racing", "PC"));
    b.addTitle(gameTitle("t2", "Puzzler", "Puzzle", "Switch"));
    assert.equal(b.getTitle("t1")?.name, "Speedster");
    assert.deepEqual(
      b.titlesByGenre("racing").map((t) => t.titleId),
      ["t1"],
    );
  });

  it("totals play time in milliseconds for a (user, title)", () => {
    const b = new InMemoryGamingBoard();
    b.recordSession(playSession("s1", "u1", "t1", 600_000, new Date("2026-01-01T00:00:00Z")));
    b.recordSession(playSession("s2", "u1", "t1", 300_000, new Date("2026-01-02T00:00:00Z")));
    b.recordSession(playSession("s3", "u1", "t2", 999_999, new Date("2026-01-02T00:00:00Z")));
    assert.equal(b.totalPlayTime("u1", "t1"), 900_000);
  });

  it("lists achievements newest-first", () => {
    const b = new InMemoryGamingBoard();
    b.unlock(achievementUnlock("x1", "u1", "t1", "First Win", new Date("2026-01-01T00:00:00Z")));
    b.unlock(achievementUnlock("x2", "u1", "t1", "Marathon", new Date("2026-01-05T00:00:00Z")));
    b.unlock(achievementUnlock("x3", "u2", "t1", "Other", new Date("2026-01-06T00:00:00Z")));
    assert.deepEqual(
      b.achievementsFor("u1").map((u) => u.unlockId),
      ["x2", "x1"],
    );
  });

  it("ranks most-played titles by total time, dropping unknown titles, capped by topK", () => {
    const b = new InMemoryGamingBoard();
    b.addTitle(gameTitle("t1", "A", "g", "PC"));
    b.addTitle(gameTitle("t2", "B", "g", "PC"));
    // t3 has sessions but no registered title → dropped.
    b.recordSession(playSession("s1", "u1", "t1", 100, new Date("2026-01-01T00:00:00Z")));
    b.recordSession(playSession("s2", "u1", "t2", 500, new Date("2026-01-01T00:00:00Z")));
    b.recordSession(playSession("s3", "u1", "t3", 9999, new Date("2026-01-01T00:00:00Z")));
    assert.deepEqual(
      b.mostPlayed("u1").map((t) => t.titleId),
      ["t2", "t1"],
    );
    assert.deepEqual(
      b.mostPlayed("u1", 1).map((t) => t.titleId),
      ["t2"],
    );
  });

  it("mostPlayed throws on non-positive topK", () => {
    const b = new InMemoryGamingBoard();
    assert.throws(() => b.mostPlayed("u1", 0), /topK/);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(GamingDomainContext.systemPromptSnippet.includes("[DOMAIN: Gaming]"));
    assert.deepEqual(GamingDomainContext.complianceFlags, ["POPIA", "WASPA", "Child_Protection"]);
    assert.deepEqual(GamingDomainContext.suggestedTools, ["game_db", "community_tools", "analytics", "web_search"]);
  });
});
