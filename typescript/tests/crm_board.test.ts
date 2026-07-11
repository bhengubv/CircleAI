// crm_board.test.ts
// Verifies the CircleAI.CRM port: contact substring search, stage-indexed deal
// pipeline, per-contact activity log, ordering guarantees, and Null* defaults.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryContactStore,
  InMemoryDealPipeline,
  InMemoryActivityLog,
  NullContactStore,
  NullDealPipeline,
  NullActivityLog,
  contact,
  deal,
  activity,
  company,
} from "../src/crm/index";

describe("InMemoryContactStore", () => {
  it("upserts and gets; unknown id is null; blank id throws", async () => {
    const s = new InMemoryContactStore();
    assert.equal(s.backendId, "in-memory");
    await s.upsertAsync(contact("c1", "Ada Lovelace", "ada@x.io", null, null));
    assert.equal((await s.getAsync("c1"))?.fullName, "Ada Lovelace");
    assert.equal(await s.getAsync("nope"), null);
    await assert.rejects(async () => s.upsertAsync(contact("", "X", null, null, null)));
  });

  it("searches name and email case-insensitively, ordered by full name, take topK", async () => {
    const s = new InMemoryContactStore();
    await s.upsertAsync(contact("c1", "Ada Lovelace", "ada@x.io", null, null));
    await s.upsertAsync(contact("c2", "Grace Hopper", "grace@x.io", null, null));
    await s.upsertAsync(contact("c3", "Zed", "ada.fan@x.io", null, null)); // matches by email
    const byAda = await s.searchAsync("ADA");
    assert.deepEqual(
      byAda.map((c) => c.contactId),
      ["c1", "c3"], // "Ada Lovelace" < "Zed" ordinally
    );
    assert.equal((await s.searchAsync("a", 1)).length, 1);
    await assert.rejects(async () => s.searchAsync("a", 0));
  });
});

describe("InMemoryDealPipeline", () => {
  it("lists by stage case-insensitively, ordered by value descending", async () => {
    const p = new InMemoryDealPipeline();
    await p.upsertAsync(deal("d1", "co", "Small", 10, "ZAR", "Won"));
    await p.upsertAsync(deal("d2", "co", "Big", 100, "ZAR", "won"));
    await p.upsertAsync(deal("d3", "co", "Mid", 50, "ZAR", "Lost"));
    const won = await p.listByStageAsync("WON");
    assert.deepEqual(
      won.map((d) => d.dealId),
      ["d2", "d1"],
    );
    assert.equal((await p.getAsync("d3"))?.name, "Mid");
    await assert.rejects(async () => p.upsertAsync(deal("", "co", "X", 1, "ZAR", "s")));
    await assert.rejects(async () => p.listByStageAsync(" "));
  });
});

describe("InMemoryActivityLog", () => {
  it("appends per contact, reads newest-first up to limit", async () => {
    const log = new InMemoryActivityLog();
    await log.appendAsync(activity("a1", "c1", "call", "old", new Date("2026-01-01T00:00:00Z")));
    await log.appendAsync(activity("a2", "c1", "email", "new", new Date("2026-06-01T00:00:00Z")));
    await log.appendAsync(activity("a3", "c2", "note", "other", new Date("2026-03-01T00:00:00Z")));
    const forC1 = await log.readForContactAsync("c1");
    assert.deepEqual(
      forC1.map((a) => a.activityId),
      ["a2", "a1"],
    );
    assert.equal((await log.readForContactAsync("c1", 1)).length, 1);
    assert.deepEqual(await log.readForContactAsync("ghost"), []);
    await assert.rejects(async () => log.appendAsync(activity("x", "", "k", "b", new Date())));
  });
});

describe("CRM factories + Null* defaults", () => {
  it("company factory maps positional fields", () => {
    const c = company("co1", "Acme", "Tech");
    assert.deepEqual(c, { companyId: "co1", name: "Acme", industry: "Tech" });
  });

  it("Null* stores nothing and returns fail-closed values", async () => {
    assert.equal(NullContactStore.instance.backendId, "null");
    await NullContactStore.instance.upsertAsync(contact("c", "n", null, null, null));
    assert.equal(await NullContactStore.instance.getAsync("c"), null);
    assert.deepEqual(await NullContactStore.instance.searchAsync("x"), []);
    assert.equal(await NullDealPipeline.instance.getAsync("d"), null);
    assert.deepEqual(await NullDealPipeline.instance.listByStageAsync("s"), []);
    assert.deepEqual(await NullActivityLog.instance.readForContactAsync("c"), []);
  });
});
