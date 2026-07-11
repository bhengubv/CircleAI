// collaboration.test.ts
// Verifies the CircleAI.Collaboration port: team-indexed channels, per-channel
// newest-first messages with a limit, presence, and Null* defaults.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryChannelStore,
  InMemoryMessageStore,
  InMemoryPresence,
  NullChannelStore,
  NullPresence,
  channel,
  message,
  presenceState,
} from "../src/collaboration/index";

const D = (s: string): Date => new Date(s);

describe("InMemoryChannelStore", () => {
  it("gets a channel and lists a team's channels ordered by name", async () => {
    const store = new InMemoryChannelStore();
    store.upsert(channel("c1", "zebra", "t1"));
    store.upsert(channel("c2", "alpha", "t1"));
    store.upsert(channel("c3", "other", "t2"));
    assert.equal((await store.getAsync("c1"))?.name, "zebra");
    const forTeam = await store.listForTeamAsync("t1");
    assert.deepEqual(
      forTeam.map((c) => c.name),
      ["alpha", "zebra"],
    );
  });
});

describe("InMemoryMessageStore", () => {
  it("returns newest-first messages up to the limit", async () => {
    const store = new InMemoryMessageStore();
    await store.postAsync(message("m1", "c1", "u", "first", D("2026-01-01T00:00:00Z")));
    await store.postAsync(message("m2", "c1", "u", "second", D("2026-02-01T00:00:00Z")));
    await store.postAsync(message("m3", "c1", "u", "third", D("2026-03-01T00:00:00Z")));
    const recent = await store.readAsync("c1", 2);
    assert.deepEqual(
      recent.map((m) => m.body),
      ["third", "second"],
    );
    assert.equal((await store.readAsync("empty")).length, 0);
  });
});

describe("InMemoryPresence", () => {
  it("reads a set presence state", async () => {
    const p = new InMemoryPresence();
    p.set(presenceState("u1", true, D("2026-01-01T00:00:00Z")));
    assert.equal((await p.getAsync("u1"))?.online, true);
    assert.equal(await p.getAsync("u2"), null);
  });
});

describe("Null collaboration defaults", () => {
  it("NullChannelStore returns nothing", async () => {
    assert.equal(await NullChannelStore.instance.getAsync("c"), null);
    assert.equal((await NullChannelStore.instance.listForTeamAsync("t")).length, 0);
  });
  it("NullPresence returns null", async () => {
    assert.equal(await NullPresence.instance.getAsync("u"), null);
  });
});
