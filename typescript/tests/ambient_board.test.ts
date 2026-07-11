// ambient_board.test.ts
// Verifies the CircleAI.Ambient port: latest/history newest-first, preferences,
// and the comfort test (temp/humidity tolerances + noise ceiling).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryAmbientBoard,
  ambientReading,
  ambientPreference,
} from "../src/ambient/index";

describe("InMemoryAmbientBoard", () => {
  it("returns the latest reading and history newest-first capped by limit", () => {
    const b = new InMemoryAmbientBoard();
    b.record(ambientReading("d1", 22, 50, 300, 40, new Date("2026-01-01T08:00:00Z")));
    b.record(ambientReading("d1", 23, 48, 320, 42, new Date("2026-01-01T12:00:00Z")));
    b.record(ambientReading("d2", 30, 60, 100, 55, new Date("2026-01-01T12:00:00Z")));
    assert.equal(b.latest("d1")?.temperatureC, 23);
    assert.deepEqual(
      b.history("d1").map((r) => r.atUtc.toISOString()),
      ["2026-01-01T12:00:00.000Z", "2026-01-01T08:00:00.000Z"],
    );
    assert.equal(b.history("d1", 1).length, 1);
  });

  it("latest is undefined for an unseen device", () => {
    const b = new InMemoryAmbientBoard();
    assert.equal(b.latest("nope"), undefined);
  });

  it("comfort requires a preference and a reading within tolerances", () => {
    const b = new InMemoryAmbientBoard();
    assert.equal(b.isComfortable("d1", "office"), false); // no pref, no reading
    b.setPreference(ambientPreference("office", 22, 50, 45));
    assert.equal(b.getPreference("office")?.targetTempC, 22);
    assert.equal(b.isComfortable("d1", "office"), false); // pref but no reading

    // Within tolerance: |23-22|<=2, |55-50|<=10, 44<=45.
    b.record(ambientReading("d1", 23, 55, 300, 44, new Date("2026-01-01T12:00:00Z")));
    assert.equal(b.isComfortable("d1", "office"), true);
  });

  it("comfort fails when any bound is exceeded", () => {
    const b = new InMemoryAmbientBoard();
    b.setPreference(ambientPreference("office", 22, 50, 45));
    // Noise too high.
    b.record(ambientReading("d1", 22, 50, 300, 46, new Date("2026-01-01T12:00:00Z")));
    assert.equal(b.isComfortable("d1", "office"), false);
    // Temp too far off.
    b.record(ambientReading("d1", 26, 50, 300, 40, new Date("2026-01-01T13:00:00Z")));
    assert.equal(b.isComfortable("d1", "office"), false);
  });
});
