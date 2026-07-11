// energy_board.test.ts
// Verifies the CircleAI.Energy port: readings-since, total kWh (last-first),
// cost estimate, active outages.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryEnergyBoard,
  EnergyDomainContext,
  meterReading,
  energyTariff,
  outage,
} from "../src/energy/index";

describe("InMemoryEnergyBoard", () => {
  it("returns readings since a cutoff ascending", () => {
    const b = new InMemoryEnergyBoard();
    b.record(meterReading("m1", 100, new Date("2026-01-01T00:00:00Z")));
    b.record(meterReading("m1", 150, new Date("2026-01-03T00:00:00Z")));
    b.record(meterReading("m1", 90, new Date("2025-12-30T00:00:00Z"))); // before cutoff
    assert.deepEqual(
      b.readingsFor("m1", new Date("2026-01-01T00:00:00Z")).map((r) => r.kwh),
      [100, 150],
    );
  });

  it("totals kWh as last minus first, 0 when fewer than 2", () => {
    const b = new InMemoryEnergyBoard();
    b.record(meterReading("m1", 100, new Date("2026-01-01T00:00:00Z")));
    assert.equal(b.totalKwhSince("m1", new Date("2026-01-01T00:00:00Z")), 0);
    b.record(meterReading("m1", 175, new Date("2026-01-05T00:00:00Z")));
    assert.equal(b.totalKwhSince("m1", new Date("2026-01-01T00:00:00Z")), 75);
  });

  it("estimates cost at the peak rate; unknown tariff throws", () => {
    const b = new InMemoryEnergyBoard();
    b.record(meterReading("m1", 100, new Date("2026-01-01T00:00:00Z")));
    b.record(meterReading("m1", 200, new Date("2026-01-05T00:00:00Z")));
    b.setTariff(energyTariff("t1", "Standard", 2.5, 1.2, "ZAR"));
    assert.equal(b.getTariff("t1")?.name, "Standard");
    assert.equal(b.estimateCost("m1", "t1", new Date("2026-01-01T00:00:00Z")), 250); // 100 kWh * 2.5
    assert.throws(() => b.estimateCost("m1", "ghost", new Date("2026-01-01T00:00:00Z")), /Unknown tariff ghost/);
  });

  it("lists only ongoing outages (EndUtc == null)", () => {
    const b = new InMemoryEnergyBoard();
    b.logOutage(outage("o1", "North", new Date("2026-01-01T00:00:00Z"), null, "Storm"));
    b.logOutage(outage("o2", "South", new Date("2026-01-01T00:00:00Z"), new Date("2026-01-01T03:00:00Z"), "Fixed"));
    assert.deepEqual(
      b.activeOutages().map((o) => o.outageId),
      ["o1"],
    );
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(EnergyDomainContext.systemPromptSnippet.includes("[DOMAIN: Energy]"));
    assert.deepEqual(EnergyDomainContext.complianceFlags, [
      "Electricity_Act",
      "NERSA",
      "SABS",
      "Municipal_Energy_By_laws",
      "POPIA",
    ]);
    assert.deepEqual(EnergyDomainContext.suggestedTools, ["energy_model", "analytics", "document_editor", "web_search"]);
  });
});
