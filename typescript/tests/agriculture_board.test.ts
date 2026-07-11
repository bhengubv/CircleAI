// agriculture_board.test.ts
// Verifies the CircleAI.Agriculture port: fields, crops ordered by planting date,
// average-yield-of-variety rollup.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { InMemoryFarmBoard, AgricultureDomainContext, field, crop, yieldRecord } from "../src/agriculture/index";

describe("InMemoryFarmBoard", () => {
  it("adds and retrieves fields", () => {
    const b = new InMemoryFarmBoard();
    b.addField(field("f1", 12.5, "loam", "drip"));
    assert.equal(b.getField("f1")?.soilType, "loam");
    assert.equal(b.getField("nope"), undefined);
  });

  it("lists a field's crops earliest-planted first", () => {
    const b = new InMemoryFarmBoard();
    b.plant(crop("c3", "f1", "Maize", new Date("2026-03-01T00:00:00Z"), null));
    b.plant(crop("c1", "f1", "Maize", new Date("2026-01-01T00:00:00Z"), null));
    b.plant(crop("c2", "f1", "Wheat", new Date("2026-02-01T00:00:00Z"), null));
    b.plant(crop("cx", "f2", "Soy", new Date("2026-01-15T00:00:00Z"), null));
    assert.deepEqual(
      b.cropsForField("f1").map((c) => c.cropId),
      ["c1", "c2", "c3"],
    );
  });

  it("averages yield across a variety (case-insensitive), 0 when none", () => {
    const b = new InMemoryFarmBoard();
    b.plant(crop("c1", "f1", "Maize", new Date("2026-01-01T00:00:00Z"), null));
    b.plant(crop("c2", "f1", "maize", new Date("2026-01-01T00:00:00Z"), null));
    b.plant(crop("c3", "f1", "Wheat", new Date("2026-01-01T00:00:00Z"), null));
    b.recordYield(yieldRecord("c1", 6, new Date("2026-06-01T00:00:00Z")));
    b.recordYield(yieldRecord("c2", 8, new Date("2026-06-01T00:00:00Z")));
    b.recordYield(yieldRecord("c3", 4, new Date("2026-06-01T00:00:00Z")));
    assert.equal(b.avgYieldOfVariety("MAIZE"), 7);
    assert.equal(b.avgYieldOfVariety("Barley"), 0);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(AgricultureDomainContext.systemPromptSnippet.includes("[DOMAIN: Agriculture]"));
    assert.deepEqual(AgricultureDomainContext.complianceFlags, ["DAFF_regs", "CARA", "Fertilizer_Act", "POPIA"]);
    assert.deepEqual(AgricultureDomainContext.suggestedTools, [
      "weather_api",
      "market_prices",
      "soil_data",
      "document_editor",
    ]);
  });
});
