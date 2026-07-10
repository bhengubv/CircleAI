// aethernet_mesh_capability.test.ts
//
// Verifies the CircleAI.AetherNet mesh capability discovery port (RT-12 v1):
//   InMemoryMeshCapabilityRegistry — upsert/replace, remove (idempotent),
//     list (+ staleAfter filter), find (case-insensitive model match, minFreeKv,
//     staleAfter, sorted by free budget descending)
//   NullMeshCapabilityBroadcaster — no-op
//   RegistryBackedMeshCapabilityBroadcaster — loopback into a registry

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { DeviceTier } from "../src/device/index";
import {
  InMemoryMeshCapabilityRegistry,
  NullMeshCapabilityBroadcaster,
  RegistryBackedMeshCapabilityBroadcaster,
  meshCapabilityAdvertisement,
  type MeshCapabilityAdvertisement,
} from "../src/aethernet/index";

function ad(
  peerId: string,
  modelId: string,
  freeKv: number,
  at: Date,
  tier: DeviceTier = DeviceTier.Phone,
): MeshCapabilityAdvertisement {
  return meshCapabilityAdvertisement(peerId, modelId, freeKv, tier, 2048, at);
}

describe("InMemoryMeshCapabilityRegistry", () => {
  it("upsert stores and replaces per peer", async () => {
    const reg = new InMemoryMeshCapabilityRegistry();
    const t = new Date();
    await reg.upsertAsync(ad("p1", "Qwen3-1.7B-MNN", 1000, t));
    assert.equal(reg.count, 1);
    // Replace the same peer's advertisement.
    await reg.upsertAsync(ad("p1", "Qwen3-1.7B-MNN", 2000, t));
    assert.equal(reg.count, 1);
    assert.equal(reg.list()[0].freeKvTokens, 2000);
  });

  it("rejects a blank peer id", async () => {
    const reg = new InMemoryMeshCapabilityRegistry();
    // Validation throws synchronously (mirrors C# ArgumentException.ThrowIfNullOrWhiteSpace
    // before the ValueTask is produced); wrap in an async fn so the sync throw
    // becomes a rejection for assert.rejects.
    await assert.rejects(async () => reg.upsertAsync(ad("   ", "M", 1, new Date())));
  });

  it("remove is idempotent and reports whether a peer was removed", async () => {
    const reg = new InMemoryMeshCapabilityRegistry();
    await reg.upsertAsync(ad("p1", "M", 1, new Date()));
    assert.equal(await reg.removeAsync("p1"), true);
    assert.equal(await reg.removeAsync("p1"), false);
    assert.equal(reg.count, 0);
  });

  it("list without staleAfter returns everything; with staleAfter filters old entries", async () => {
    const now = new Date("2026-07-10T12:00:00Z");
    const reg = new InMemoryMeshCapabilityRegistry();
    reg.nowUtc = () => now;

    const fresh = new Date(now.getTime() - 10_000); // 10s ago
    const stale = new Date(now.getTime() - 120_000); // 2min ago
    await reg.upsertAsync(ad("fresh", "M", 1, fresh));
    await reg.upsertAsync(ad("stale", "M", 1, stale));

    assert.equal(reg.list().length, 2);
    // 60s staleness window drops the 2-min-old entry.
    const recent = reg.list(60_000);
    assert.equal(recent.length, 1);
    assert.equal(recent[0].peerId, "fresh");
  });

  it("find matches modelId case-insensitively, respects minFreeKv, sorts by free budget desc", async () => {
    const reg = new InMemoryMeshCapabilityRegistry();
    const t = new Date();
    await reg.upsertAsync(ad("p1", "Qwen3-1.7B-MNN", 500, t));
    await reg.upsertAsync(ad("p2", "qwen3-1.7b-mnn", 3000, t)); // different case, same model
    await reg.upsertAsync(ad("p3", "Qwen3-1.7B-MNN", 1500, t));
    await reg.upsertAsync(ad("p4", "SomethingElse", 9000, t)); // different model

    const hits = reg.find("QWEN3-1.7B-MNN", 1000);
    assert.deepEqual(hits.map((h) => h.peerId), ["p2", "p3"]); // p1 filtered (500 < 1000); sorted desc
    assert.equal(hits[0].freeKvTokens, 3000);
  });

  it("find with staleAfter filters stale peers", async () => {
    const now = new Date("2026-07-10T12:00:00Z");
    const reg = new InMemoryMeshCapabilityRegistry();
    reg.nowUtc = () => now;
    await reg.upsertAsync(ad("fresh", "M", 1000, new Date(now.getTime() - 5_000)));
    await reg.upsertAsync(ad("stale", "M", 5000, new Date(now.getTime() - 300_000)));

    const hits = reg.find("M", 0, 60_000);
    assert.deepEqual(hits.map((h) => h.peerId), ["fresh"]);
  });

  it("find rejects a blank modelId", () => {
    const reg = new InMemoryMeshCapabilityRegistry();
    assert.throws(() => reg.find("  "));
  });
});

describe("NullMeshCapabilityBroadcaster", () => {
  it("broadcast is a no-op that resolves", async () => {
    await NullMeshCapabilityBroadcaster.instance.broadcastAsync(ad("p1", "M", 1, new Date()));
    // Nothing to assert beyond "did not throw / resolved".
    assert.ok(NullMeshCapabilityBroadcaster.instance);
  });
});

describe("RegistryBackedMeshCapabilityBroadcaster", () => {
  it("loops the advertisement into the bound registry", async () => {
    const reg = new InMemoryMeshCapabilityRegistry();
    const caster = new RegistryBackedMeshCapabilityBroadcaster(reg);
    await caster.broadcastAsync(ad("me", "Qwen3-1.7B-MNN", 2048, new Date(), DeviceTier.Workstation));
    assert.equal(caster.broadcastCount, 1);
    const found = reg.find("Qwen3-1.7B-MNN");
    assert.equal(found.length, 1);
    assert.equal(found[0].peerId, "me");
    assert.equal(found[0].tier, DeviceTier.Workstation);
  });
});
