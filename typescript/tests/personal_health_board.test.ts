// personal_health_board.test.ts
// Verifies the CircleAI.Personal.Health port: vital recording, ReadSince
// (kind + since, ascending), Latest, allergies, and medication lifecycle
// (active = not ended, name-ordered).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryPersonalHealthBoard,
  PersonalHealthDomainContext,
  VitalKind,
  vitalReading,
  allergy,
  medication,
} from "../src/personal/health/index";

describe("VitalKind ordinals", () => {
  it("match the C# enum order", () => {
    assert.equal(VitalKind.BloodPressureSystolic, 0);
    assert.equal(VitalKind.BloodPressureDiastolic, 1);
    assert.equal(VitalKind.GlucoseMgDl, 2);
    assert.equal(VitalKind.WeightKg, 3);
    assert.equal(VitalKind.HeartRateBpm, 4);
    assert.equal(VitalKind.TemperatureC, 5);
    assert.equal(VitalKind.OxygenPct, 6);
    assert.equal(VitalKind.StepsCount, 7);
  });
});

describe("InMemoryPersonalHealthBoard — vitals", () => {
  it("ReadSince filters by kind + since and sorts ascending", () => {
    const b = new InMemoryPersonalHealthBoard();
    b.record(vitalReading(VitalKind.GlucoseMgDl, 5.5, new Date("2026-03-01T00:00:00Z"), null));
    b.record(vitalReading(VitalKind.GlucoseMgDl, 6.1, new Date("2026-03-03T00:00:00Z"), "post-meal"));
    b.record(vitalReading(VitalKind.GlucoseMgDl, 4.9, new Date("2026-02-01T00:00:00Z"), "old"));
    b.record(vitalReading(VitalKind.WeightKg, 70, new Date("2026-03-02T00:00:00Z"), null));
    const since = new Date("2026-02-15T00:00:00Z");
    const readings = b.readSince(VitalKind.GlucoseMgDl, since);
    assert.deepEqual(
      readings.map((r) => r.value),
      [5.5, 6.1],
    );
  });

  it("Latest returns the most recent reading of a kind, or undefined", () => {
    const b = new InMemoryPersonalHealthBoard();
    assert.equal(b.latest(VitalKind.HeartRateBpm), undefined);
    b.record(vitalReading(VitalKind.HeartRateBpm, 60, new Date("2026-03-01T00:00:00Z"), null));
    b.record(vitalReading(VitalKind.HeartRateBpm, 72, new Date("2026-03-05T00:00:00Z"), null));
    assert.equal(b.latest(VitalKind.HeartRateBpm)?.value, 72);
  });
});

describe("InMemoryPersonalHealthBoard — allergies + medications", () => {
  it("adds allergies", () => {
    const b = new InMemoryPersonalHealthBoard();
    b.addAllergy(allergy("al1", "Penicillin", "Severe"));
    assert.equal(b.allergies.length, 1);
    assert.equal(b.allergies[0].substance, "Penicillin");
  });

  it("active medications exclude ended ones and are name-ordered", () => {
    const b = new InMemoryPersonalHealthBoard();
    b.addMedication(medication("m1", "Zoloft", "50mg", "od", new Date("2026-01-01T00:00:00Z"), null));
    b.addMedication(medication("m2", "Aspirin", "100mg", "od", new Date("2026-01-01T00:00:00Z"), null));
    b.addMedication(medication("m3", "Metformin", "500mg", "bd", new Date("2026-01-01T00:00:00Z"), null));
    b.endMedication("m3", new Date("2026-06-01T00:00:00Z"));
    assert.deepEqual(
      b.activeMedications().map((m) => m.name),
      ["Aspirin", "Zoloft"],
    );
  });

  it("ending an unknown medication throws", () => {
    const b = new InMemoryPersonalHealthBoard();
    assert.throws(() => b.endMedication("ghost", new Date()), /Unknown medication ghost/);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(PersonalHealthDomainContext.systemPromptSnippet.includes("[DOMAIN: Personal.Health]"));
    assert.deepEqual(PersonalHealthDomainContext.complianceFlags, ["POPIA", "Health_Professions_Act", "Not_Medical_Advice"]);
    assert.deepEqual(PersonalHealthDomainContext.suggestedTools, [
      "health_tracker",
      "symptom_checker_ref",
      "calendar",
      "document_editor",
    ]);
  });
});
