// biosignals.test.ts
// Verifies the CircleAI.Wearable.Biosignals port: BiosignalKind ordinals, sample
// factory (guid shape + confidence clamp + float rounding), null + recorded
// sources, the windowed aggregator, and the deterministic affect mapper.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { AffectState } from "../src/memory/index";
import {
  BiosignalKind,
  BiosignalAffectMapper,
  BiosignalAggregator,
  NullBiosignalSource,
  RecordedBiosignalSource,
  createBiosignalSample,
  biosignalSample,
} from "../src/wearable/biosignals/index";

const fr = Math.fround;

async function drain<T>(it: AsyncIterable<T>): Promise<T[]> {
  const out: T[] = [];
  for await (const x of it) out.push(x);
  return out;
}

describe("BiosignalKind", () => {
  it("has stable ordinals matching the C# enum", () => {
    assert.equal(BiosignalKind.HeartRate, 0);
    assert.equal(BiosignalKind.HeartRateVariability, 1);
    assert.equal(BiosignalKind.OxygenSaturation, 2);
    assert.equal(BiosignalKind.SleepStage, 5);
    assert.equal(BiosignalKind.Steps, 6);
    assert.equal(BiosignalKind.Unknown, 8);
  });
});

describe("createBiosignalSample", () => {
  it("mints a 32-hex guid id, clamps confidence to [0,1], and frounds value", () => {
    const s = createBiosignalSample(BiosignalKind.HeartRate, 72.3, "bpm", 1.5, false);
    assert.match(s.id, /^[0-9a-f]{32}$/);
    assert.equal(s.confidence, 1); // clamped from 1.5
    assert.equal(s.value, fr(72.3));
    assert.equal(s.unit, "bpm");
    assert.equal(s.isCumulative, false);
    assert.ok(s.measuredAt instanceof Date);

    const low = createBiosignalSample(BiosignalKind.OxygenSaturation, 98, "%", -0.2);
    assert.equal(low.confidence, 0); // clamped from -0.2
  });

  it("positional factory frounds value and confidence", () => {
    const s = biosignalSample("id0", BiosignalKind.BodyTemperature, 36.6, "celsius", 0.9, false, new Date(0));
    assert.equal(s.value, fr(36.6));
    assert.equal(s.confidence, fr(0.9));
  });
});

describe("NullBiosignalSource", () => {
  it("supports nothing and streams nothing", async () => {
    const src = new NullBiosignalSource();
    assert.deepEqual(src.supportedKinds, []);
    assert.equal(await src.isSupportedAsync(BiosignalKind.HeartRate), false);
    assert.deepEqual(await drain(src.streamAsync()), []);
  });
});

describe("RecordedBiosignalSource", () => {
  it("reports its distinct kinds and replays its samples in order", async () => {
    const samples = [
      biosignalSample("s1", BiosignalKind.HeartRate, 60, "bpm", 1, false, new Date("2026-01-01T00:00:00Z")),
      biosignalSample("s2", BiosignalKind.HeartRate, 80, "bpm", 1, false, new Date("2026-01-01T00:00:01Z")),
      biosignalSample("s3", BiosignalKind.Steps, 100, "count", 1, true, new Date("2026-01-01T00:00:02Z")),
    ];
    const src = new RecordedBiosignalSource(samples);
    assert.deepEqual([...src.supportedKinds].sort(), [BiosignalKind.HeartRate, BiosignalKind.Steps].sort());
    assert.equal(await src.isSupportedAsync(BiosignalKind.HeartRate), true);
    assert.equal(await src.isSupportedAsync(BiosignalKind.OxygenSaturation), false);
    assert.deepEqual((await drain(src.streamAsync())).map((s) => s.id), ["s1", "s2", "s3"]);
  });
});

describe("BiosignalAggregator", () => {
  it("aggregates per-kind min/max/mean/count over the window", async () => {
    const now = Date.now();
    const samples = [
      biosignalSample("s1", BiosignalKind.HeartRate, 60, "bpm", 1, false, new Date(now)),
      biosignalSample("s2", BiosignalKind.HeartRate, 80, "bpm", 1, false, new Date(now)),
      biosignalSample("s3", BiosignalKind.HeartRate, 100, "bpm", 1, false, new Date(now)),
      biosignalSample("s4", BiosignalKind.OxygenSaturation, 97, "%", 1, false, new Date(now)),
    ];
    const agg = new BiosignalAggregator(new RecordedBiosignalSource(samples));
    const snap = await agg.snapshotAsync(60_000);
    const hr = snap.stats.get(BiosignalKind.HeartRate);
    assert.ok(hr);
    assert.equal(hr?.sampleCount, 3);
    assert.equal(hr?.min, fr(60));
    assert.equal(hr?.max, fr(100));
    assert.equal(hr?.mean, fr(80)); // (60+80+100)/3
    const spo2 = snap.stats.get(BiosignalKind.OxygenSaturation);
    assert.equal(spo2?.sampleCount, 1);
    assert.equal(spo2?.mean, fr(97));
  });

  it("drops samples older than the window", async () => {
    const now = Date.now();
    const old = biosignalSample("old", BiosignalKind.HeartRate, 999, "bpm", 1, false, new Date(now - 10 * 60_000));
    const fresh = biosignalSample("new", BiosignalKind.HeartRate, 70, "bpm", 1, false, new Date(now));
    const agg = new BiosignalAggregator(new RecordedBiosignalSource([old, fresh]));
    const snap = await agg.snapshotAsync(60_000);
    const hr = snap.stats.get(BiosignalKind.HeartRate);
    assert.equal(hr?.sampleCount, 1);
    assert.equal(hr?.mean, fr(70));
  });

  it("rejects a non-positive window", async () => {
    const agg = new BiosignalAggregator(new NullBiosignalSource());
    await assert.rejects(() => agg.snapshotAsync(0), /Window must be positive/);
  });
});

describe("BiosignalAffectMapper", () => {
  it("ignores low-confidence samples", () => {
    const a = new AffectState();
    const before = a.energy;
    BiosignalAffectMapper.apply(
      biosignalSample("s", BiosignalKind.HeartRate, 140, "bpm", 0.4, false, new Date()),
      a,
    );
    assert.equal(a.energy, before);
  });

  it("applies the high heart-rate rule with float parity", () => {
    const a = new AffectState();
    BiosignalAffectMapper.apply(biosignalSample("s", BiosignalKind.HeartRate, 140, "bpm", 1, false, new Date()), a);
    assert.equal(a.energy, 0.6000000238418579);
    assert.equal(a.uncertainty, 0.25);
  });

  it("applies the elevated and low heart-rate rules", () => {
    const a1 = new AffectState();
    BiosignalAffectMapper.apply(biosignalSample("s", BiosignalKind.HeartRate, 110, "bpm", 1, false, new Date()), a1);
    assert.equal(a1.energy, 0.550000011920929);

    const a2 = new AffectState();
    BiosignalAffectMapper.apply(biosignalSample("s", BiosignalKind.HeartRate, 45, "bpm", 1, false, new Date()), a2);
    assert.equal(a2.energy, 0.44999998807907104);
  });

  it("applies HRV rules (low: uncertainty up + rapport clamped at 0; high: engagement up)", () => {
    const low = new AffectState();
    BiosignalAffectMapper.apply(
      biosignalSample("s", BiosignalKind.HeartRateVariability, 15, "ms", 1, false, new Date()),
      low,
    );
    assert.equal(low.uncertainty, 0.25);
    assert.equal(low.rapport, 0); // 0 - 0.02 clamped to 0

    const high = new AffectState();
    BiosignalAffectMapper.apply(
      biosignalSample("s", BiosignalKind.HeartRateVariability, 70, "ms", 1, false, new Date()),
      high,
    );
    assert.equal(high.engagement, 0.5199999809265137);
  });

  it("applies the low SpO2 rule and leaves other kinds untouched", () => {
    const a = new AffectState();
    BiosignalAffectMapper.apply(
      biosignalSample("s", BiosignalKind.OxygenSaturation, 85, "%", 1, false, new Date()),
      a,
    );
    assert.equal(a.uncertainty, 0.30000001192092896);

    const b = new AffectState();
    const engBefore = b.engagement;
    BiosignalAffectMapper.apply(biosignalSample("s", BiosignalKind.SleepStage, 2, "stage", 1, false, new Date()), b);
    assert.equal(b.engagement, engBefore); // sleep stage does not mutate affect
  });
});
