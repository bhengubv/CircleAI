// autonomousbiz.test.ts
// Verifies the CircleAI.AutonomousBiz port: the fan-out revenue loop with kept
// history, the currency-matched running-balance treasury, the append-only
// decision log, and Null* defaults.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryRevenueLoop,
  InMemoryTreasury,
  InMemoryDecisionLog,
  NullTreasury,
  NullRevenueLoop,
  revenueEvent,
  autonomousDecision,
  type RevenueEvent,
} from "../src/autonomousbiz/index";

const D = (s: string): Date => new Date(s);

describe("InMemoryRevenueLoop", () => {
  it("fans out to subscribers and keeps history filtered by since", async () => {
    const loop = new InMemoryRevenueLoop();
    const seen: RevenueEvent[] = [];
    const sub = loop.subscribe(async (e) => {
      seen.push(e);
    });
    loop.publish(revenueEvent("e1", 10, "ZAR", "app", D("2026-01-01T00:00:00Z")));
    loop.publish(revenueEvent("e2", 20, "ZAR", "app", D("2026-03-01T00:00:00Z")));
    sub.dispose();
    loop.publish(revenueEvent("e3", 5, "ZAR", "app", D("2026-04-01T00:00:00Z")));
    // Give async subscribers a tick.
    await new Promise((r) => setTimeout(r, 5));
    assert.equal(seen.length, 2); // e3 arrived after dispose
    const since = await loop.readAsync(D("2026-02-01T00:00:00Z"));
    assert.deepEqual(
      since.map((e) => e.eventId),
      ["e2", "e3"],
    );
  });

  it("does not let a throwing subscriber break publish", () => {
    const loop = new InMemoryRevenueLoop();
    loop.subscribe(async () => {
      throw new Error("bad");
    });
    assert.doesNotThrow(() => loop.publish(revenueEvent("e", 1, "ZAR", "s", new Date())));
  });
});

describe("InMemoryTreasury", () => {
  it("sums currency-matched events into a running balance", async () => {
    const loop = new InMemoryRevenueLoop();
    const treasury = new InMemoryTreasury(loop, "ZAR");
    loop.publish(revenueEvent("e1", 100, "ZAR", "app", new Date()));
    loop.publish(revenueEvent("e2", 50, "zar", "app", new Date())); // case-insensitive currency
    loop.publish(revenueEvent("e3", 999, "USD", "app", new Date())); // wrong currency, excluded
    const snap = await treasury.getSnapshotAsync();
    assert.equal(snap.balance, 150);
    assert.equal(snap.currency, "ZAR");
  });
});

describe("InMemoryDecisionLog", () => {
  it("appends and reads newest-first with a limit", async () => {
    const log = new InMemoryDecisionLog();
    await log.appendAsync(autonomousDecision("d1", "r", "a", D("2026-01-01T00:00:00Z")));
    await log.appendAsync(autonomousDecision("d2", "r", "a", D("2026-02-01T00:00:00Z")));
    const recent = await log.readAsync(1);
    assert.equal(recent.length, 1);
    assert.equal(recent[0].decisionId, "d2");
    await assert.rejects(() => log.readAsync(0));
  });
});

describe("Null autonomous-biz defaults", () => {
  it("NullTreasury returns a zero balance", async () => {
    assert.equal((await NullTreasury.instance.getSnapshotAsync()).balance, 0);
  });
  it("NullRevenueLoop reads empty", async () => {
    assert.equal((await NullRevenueLoop.instance.readAsync(new Date())).length, 0);
  });
});
