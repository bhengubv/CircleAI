// pets_board.test.ts
// Verifies the CircleAI.Pets port: pets (name-ordered), vaccinations
// (administered-desc), weight history (asc), and upcoming vet appointments.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryPetsBoard,
  PetsDomainContext,
  pet,
  vaccination,
  weightSample,
  vetAppointment,
} from "../src/pets/index";

const D = (s: string) => new Date(s);

describe("InMemoryPetsBoard", () => {
  it("adds pets ordered by name", () => {
    const b = new InMemoryPetsBoard();
    b.add(pet("p2", "Rex", "Dog", "Lab", D("2019-01-01")));
    b.add(pet("p1", "Milo", "Cat", null, D("2021-01-01")));
    assert.deepEqual(
      b.pets.map((p) => p.name),
      ["Milo", "Rex"],
    );
    assert.equal(b.getPet("p1")?.species, "Cat");
    assert.equal(b.getPet("nope"), undefined);
  });

  it("lists vaccinations newest-first and weight history oldest-first", () => {
    const b = new InMemoryPetsBoard();
    b.recordVaccination(vaccination("p1", "Rabies", D("2025-01-01T00:00:00Z"), D("2026-01-01T00:00:00Z")));
    b.recordVaccination(vaccination("p1", "Distemper", D("2025-06-01T00:00:00Z"), null));
    assert.deepEqual(
      b.vaccinationsFor("p1").map((v) => v.vaccine),
      ["Distemper", "Rabies"],
    );
    b.recordWeight(weightSample("p1", 4.2, D("2026-03-01T00:00:00Z")));
    b.recordWeight(weightSample("p1", 4.0, D("2026-01-01T00:00:00Z")));
    assert.deepEqual(
      b.weightHistory("p1").map((w) => w.weightKg),
      [4.0, 4.2],
    );
  });

  it("returns only future appointments ordered ascending (now overridable)", () => {
    const b = new InMemoryPetsBoard();
    b.schedule(vetAppointment("a1", "p1", "Checkup", D("2026-06-01T00:00:00Z"), "Dr A"));
    b.schedule(vetAppointment("a2", "p1", "Booster", D("2026-04-01T00:00:00Z"), "Dr B"));
    b.schedule(vetAppointment("a3", "p1", "Past", D("2026-01-01T00:00:00Z"), "Dr C"));
    const upcoming = b.upcomingAppointments(D("2026-02-01T00:00:00Z"));
    assert.deepEqual(
      upcoming.map((a) => a.apptId),
      ["a2", "a1"],
    );
  });

  it("rejects null arguments", () => {
    const b = new InMemoryPetsBoard();
    assert.throws(() => b.add(null as never));
    assert.throws(() => b.recordVaccination(null as never));
    assert.throws(() => b.recordWeight(null as never));
    assert.throws(() => b.schedule(null as never));
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(PetsDomainContext.systemPromptSnippet.includes("[DOMAIN: Pets]"));
    assert.deepEqual(PetsDomainContext.complianceFlags, ["Animals_Protection_Act_71_1962", "POPIA", "Vet_Referral_Required"]);
    assert.deepEqual(PetsDomainContext.suggestedTools, ["vet_finder", "pet_health_db", "training_tools", "calendar"]);
  });
});
