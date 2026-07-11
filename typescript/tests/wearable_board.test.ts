// wearable_board.test.ts
// Verifies the CircleAI.Wearable port: devices sorted by vendor, record guard,
// read-since, latest value, average (NaN when empty), WearableContext record.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryWearableBoard,
  WearableKind,
  WearableTelemetryKind,
  wearableDevice,
  wearableSample,
  wearableContext,
} from "../src/wearable/index";

describe("InMemoryWearableBoard", () => {
  it("lists devices ordered by vendor and enforces enum ordinals", () => {
    const b = new InMemoryWearableBoard();
    b.add(wearableDevice("d1", WearableKind.Smartwatch, "Zephyr", "1.0", 80));
    b.add(wearableDevice("d2", WearableKind.FitnessBand, "Acme", "2.1", 55));
    assert.equal(b.getDevice("d1")?.vendor, "Zephyr");
    assert.deepEqual(
      b.devices.map((d) => d.deviceId),
      ["d2", "d1"], // "Acme" < "Zephyr"
    );
    assert.equal(WearableKind.Smartwatch, 0);
    assert.equal(WearableKind.Headset, 4);
    assert.equal(WearableTelemetryKind.HeartRate, 0);
    assert.equal(WearableTelemetryKind.OxygenPct, 6);
  });

  it("record throws for unknown device", () => {
    const b = new InMemoryWearableBoard();
    assert.throws(
      () => b.record(wearableSample("ghost", WearableTelemetryKind.HeartRate, 70, new Date())),
      /Unknown device ghost/,
    );
  });

  it("reads samples since a cutoff ascending, latest value, and average", () => {
    const b = new InMemoryWearableBoard();
    b.add(wearableDevice("d1", WearableKind.Smartwatch, "Z", "1", 80));
    b.record(wearableSample("d1", WearableTelemetryKind.HeartRate, 60, new Date("2026-01-01T00:00:00Z")));
    b.record(wearableSample("d1", WearableTelemetryKind.HeartRate, 80, new Date("2026-01-03T00:00:00Z")));
    b.record(wearableSample("d1", WearableTelemetryKind.HeartRate, 50, new Date("2025-12-30T00:00:00Z"))); // before cutoff
    const since = new Date("2026-01-01T00:00:00Z");
    assert.deepEqual(
      b.readSince("d1", WearableTelemetryKind.HeartRate, since).map((s) => s.value),
      [60, 80],
    );
    assert.equal(b.latestValue("d1", WearableTelemetryKind.HeartRate), 80);
    assert.equal(b.averageValue("d1", WearableTelemetryKind.HeartRate, since), 70);
  });

  it("latestValue is undefined and averageValue is NaN when there are no samples", () => {
    const b = new InMemoryWearableBoard();
    b.add(wearableDevice("d1", WearableKind.Smartwatch, "Z", "1", 80));
    assert.equal(b.latestValue("d1", WearableTelemetryKind.Steps), undefined);
    assert.ok(Number.isNaN(b.averageValue("d1", WearableTelemetryKind.Steps, new Date("2026-01-01T00:00:00Z"))));
  });

  it("WearableContext carries the biometric snapshot", () => {
    const ctx = wearableContext(72, 4200, 98, 36.5, true, new Date("2026-01-01T00:00:00Z"));
    assert.equal(ctx.heartRateBpm, 72);
    assert.equal(ctx.stepCountToday, 4200);
    assert.equal(ctx.isWorkoutActive, true);
    const empty = wearableContext(null, null, null, null, false, new Date("2026-01-01T00:00:00Z"));
    assert.equal(empty.spO2Percent, null);
  });
});
