// creative_board.test.ts
// Verifies the CircleAI.Creative port: works by tag, recent inspiration,
// average critique score (0 when none).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryCreativeBoard,
  CreativeDomainContext,
  creativeWork,
  inspiration,
  critique,
} from "../src/creative/index";

describe("InMemoryCreativeBoard", () => {
  it("finds works by tag case-insensitively", () => {
    const b = new InMemoryCreativeBoard();
    b.addWork(creativeWork("w1", "Dawn", "poem", "Ann", new Date("2026-01-01T00:00:00Z"), ["Nature", "hope"]));
    b.addWork(creativeWork("w2", "Dusk", "song", "Ben", new Date("2026-01-01T00:00:00Z"), ["night"]));
    assert.equal(b.getWork("w1")?.title, "Dawn");
    assert.deepEqual(
      b.worksByTag("NATURE").map((w) => w.workId),
      ["w1"],
    );
  });

  it("lists recent inspiration newest-first capped by limit", () => {
    const b = new InMemoryCreativeBoard();
    b.recordInspiration(inspiration("i1", "sea", "http://a", new Date("2026-01-01T00:00:00Z")));
    b.recordInspiration(inspiration("i2", "sky", "http://b", new Date("2026-01-03T00:00:00Z")));
    b.recordInspiration(inspiration("i3", "sun", "http://c", new Date("2026-01-02T00:00:00Z")));
    assert.deepEqual(
      b.recentInspiration().map((i) => i.inspirationId),
      ["i2", "i3", "i1"],
    );
    assert.deepEqual(
      b.recentInspiration(1).map((i) => i.inspirationId),
      ["i2"],
    );
  });

  it("averages critique scores, 0 when none", () => {
    const b = new InMemoryCreativeBoard();
    assert.equal(b.avgScore("w1"), 0);
    b.addCritique(critique("c1", "w1", "Ann", "good", 8));
    b.addCritique(critique("c2", "w1", "Ben", "great", 10));
    b.addCritique(critique("c3", "w2", "Cy", "meh", 4));
    assert.equal(b.avgScore("w1"), 9);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(CreativeDomainContext.systemPromptSnippet.includes("[DOMAIN: Creative]"));
    assert.deepEqual(CreativeDomainContext.complianceFlags, ["Copyright_Act_98_1978", "POPIA"]);
    assert.deepEqual(CreativeDomainContext.suggestedTools, ["writing_tools", "image_tools", "music_tools", "document_editor"]);
  });
});
