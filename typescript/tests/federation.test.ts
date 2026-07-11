// federation.test.ts
// Verifies the CircleAI.Federation port: little-endian float encode/decode round
// trips, sample-size-weighted averaging (with Math.fround at the float write
// site), and the InMemoryFederationAggregator round lifecycle (open → submit →
// commit with min-participants gating and signature filtering).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  FederatedAveraging,
  InMemoryFederationAggregator,
  RoundStatus,
  DeltaDispatchOutcome,
  modelDelta,
  type ModelDelta,
} from "../src/federation/index";

function delta(roundId: string, samples: number, floats: number[], id = crypto.randomUUID()): ModelDelta {
  return modelDelta(
    id,
    roundId,
    "uhid",
    "m",
    "1.0.0",
    FederatedAveraging.encodeFloats(floats),
    samples,
    new Uint8Array([1, 2, 3]),
    new Date(),
  );
}

describe("FederatedAveraging encode/decode", () => {
  it("round-trips float arrays through Math.fround", () => {
    const values = [1.5, -2.25, 0, 3.14159];
    const decoded = FederatedAveraging.decodeFloats(FederatedAveraging.encodeFloats(values));
    assert.equal(decoded.length, 4);
    assert.equal(decoded[0], 1.5);
    assert.equal(decoded[1], -2.25);
    assert.equal(decoded[3], Math.fround(3.14159));
  });

  it("rejects a payload whose length is not a multiple of 4", () => {
    assert.throws(() => FederatedAveraging.decodeFloats(new Uint8Array([1, 2, 3])));
  });
});

describe("FederatedAveraging.average", () => {
  it("computes a sample-size-weighted average", () => {
    // weights 1:3 over [0,0] and [4,8] → [3,6].
    const d1 = delta("r", 1, [0, 0]);
    const d2 = delta("r", 3, [4, 8]);
    const out = FederatedAveraging.decodeFloats(FederatedAveraging.average([d1, d2]));
    assert.equal(out[0], 3);
    assert.equal(out[1], 6);
  });

  it("throws on empty / mismatched / zero-weight inputs", () => {
    assert.throws(() => FederatedAveraging.average([]));
    assert.throws(() => FederatedAveraging.average([delta("r", 1, [1, 2]), delta("r", 1, [1])]));
    assert.throws(() => FederatedAveraging.average([delta("r", 0, [1, 2])]));
  });
});

describe("InMemoryFederationAggregator", () => {
  it("opens a round with validated parameters", async () => {
    const agg = new InMemoryFederationAggregator(() => true);
    const round = await agg.openRoundAsync("m", "1.0.0", "1.1.0", 2, 5);
    assert.equal(round.status, RoundStatus.Open);
    assert.equal(round.minParticipants, 2);
    await assert.rejects(() => agg.openRoundAsync("m", "1", "2", 0, 5));
    await assert.rejects(() => agg.openRoundAsync("m", "1", "2", 3, 2));
  });

  it("commits once min participants are met and averages valid deltas", async () => {
    const agg = new InMemoryFederationAggregator(() => true);
    const round = await agg.openRoundAsync("m", "1.0.0", "1.1.0", 2, 5);
    await agg.submitDeltaAsync(delta(round.id, 1, [0, 0]));
    // Below min → null.
    assert.equal(await agg.tryCommitAsync(round.id), null);
    await agg.submitDeltaAsync(delta(round.id, 3, [4, 8]));
    const committed = await agg.tryCommitAsync(round.id);
    assert.ok(committed);
    const out = FederatedAveraging.decodeFloats(committed as Uint8Array);
    assert.deepEqual(out, [3, 6]);
    assert.equal((await agg.getRoundAsync(round.id)).status, RoundStatus.Committed);
  });

  it("re-returns the committed payload idempotently", async () => {
    const agg = new InMemoryFederationAggregator(() => true);
    const round = await agg.openRoundAsync("m", "1", "2", 1, 5);
    await agg.submitDeltaAsync(delta(round.id, 1, [2, 2]));
    const first = await agg.tryCommitAsync(round.id);
    const second = await agg.tryCommitAsync(round.id);
    assert.deepEqual([...(first as Uint8Array)], [...(second as Uint8Array)]);
  });

  it("drops signature-invalid deltas at commit time", async () => {
    // Only the delta with samples===2 validates; min is 1, so it commits with just that.
    const agg = new InMemoryFederationAggregator((d) => d.sampleCount === 2);
    const round = await agg.openRoundAsync("m", "1", "2", 1, 5);
    await agg.submitDeltaAsync(delta(round.id, 1, [9, 9]));
    await agg.submitDeltaAsync(delta(round.id, 2, [4, 4]));
    const out = FederatedAveraging.decodeFloats((await agg.tryCommitAsync(round.id)) as Uint8Array);
    assert.deepEqual(out, [4, 4]);
  });

  it("rejects submissions to an unknown round and past MaxParticipants", async () => {
    const agg = new InMemoryFederationAggregator(() => true);
    await assert.rejects(() => agg.submitDeltaAsync(delta("nope", 1, [1, 1])));
    const round = await agg.openRoundAsync("m", "1", "2", 1, 1);
    await agg.submitDeltaAsync(delta(round.id, 1, [1, 1]));
    await assert.rejects(() => agg.submitDeltaAsync(delta(round.id, 1, [2, 2])));
  });

  it("ignores empty-payload deltas without counting them", async () => {
    const agg = new InMemoryFederationAggregator(() => true);
    const round = await agg.openRoundAsync("m", "1", "2", 1, 5);
    await agg.submitDeltaAsync(
      modelDelta(crypto.randomUUID(), round.id, "u", "m", "1", new Uint8Array(0), 5, new Uint8Array(), new Date()),
    );
    assert.equal((await agg.getRoundAsync(round.id)).currentParticipantCount, 0);
  });

  it("exposes DeltaDispatchOutcome enum values", () => {
    assert.equal(DeltaDispatchOutcome.Accepted, 0);
    assert.equal(DeltaDispatchOutcome.Duplicate, 2);
  });
});
