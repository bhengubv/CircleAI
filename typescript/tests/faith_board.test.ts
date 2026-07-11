// faith_board.test.ts
// Verifies the CircleAI.Faith port: services between, recent prayers, scripture
// lookup + by-tradition.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryFaithBoard,
  FaithDomainContext,
  faithService,
  prayerRequest,
  scriptureReference,
} from "../src/faith/index";

describe("InMemoryFaithBoard", () => {
  it("lists services within a window ascending", () => {
    const b = new InMemoryFaithBoard();
    b.schedule(faithService("s1", "Grace", "Sunday", new Date("2026-01-04T09:00:00Z"), "Main Hall"));
    b.schedule(faithService("s2", "Grace", "Midweek", new Date("2026-01-07T18:00:00Z"), "Room A"));
    b.schedule(faithService("s3", "Grace", "Later", new Date("2026-02-01T09:00:00Z"), "Main Hall"));
    assert.deepEqual(
      b
        .servicesBetween(new Date("2026-01-01T00:00:00Z"), new Date("2026-01-31T00:00:00Z"))
        .map((s) => s.serviceId),
      ["s1", "s2"],
    );
  });

  it("lists recent prayers newest-first capped by limit", () => {
    const b = new InMemoryFaithBoard();
    b.submitPrayer(prayerRequest("p1", "Ann", "one", new Date("2026-01-01T00:00:00Z"), false));
    b.submitPrayer(prayerRequest("p2", "", "anon", new Date("2026-01-03T00:00:00Z"), true));
    b.submitPrayer(prayerRequest("p3", "Cy", "three", new Date("2026-01-02T00:00:00Z"), false));
    assert.deepEqual(
      b.recentPrayers().map((p) => p.requestId),
      ["p2", "p3", "p1"],
    );
    assert.deepEqual(
      b.recentPrayers(1).map((p) => p.requestId),
      ["p2"],
    );
  });

  it("looks up scripture exactly and lists by tradition case-insensitively", () => {
    const b = new InMemoryFaithBoard();
    b.addScripture(scriptureReference("ref1", "Christian", "John", 3, 16, "For God so loved..."));
    b.addScripture(scriptureReference("ref2", "Christian", "Psalms", 23, 1, "The Lord is my shepherd..."));
    assert.equal(b.lookup("Christian", "John", 3, 16)?.referenceId, "ref1");
    assert.equal(b.lookup("Christian", "John", 3, 17), undefined);
    assert.deepEqual(
      b.byTradition("christian").map((r) => r.referenceId).sort(),
      ["ref1", "ref2"],
    );
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(FaithDomainContext.systemPromptSnippet.includes("[DOMAIN: Faith]"));
    assert.deepEqual(FaithDomainContext.complianceFlags, ["POPIA", "Non_Denominational_Respect"]);
    assert.deepEqual(FaithDomainContext.suggestedTools, ["scripture_tools", "document_editor", "calendar"]);
  });
});
