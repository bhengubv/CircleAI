// healthcare_board.test.ts
// Verifies the CircleAI.Healthcare port: patient registry, appointment
// scheduling + status update, prescriptions, and the ordering guarantees.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryHealthcareBoard,
  HealthcareDomainContext,
  patient,
  healthAppointment,
  prescription,
} from "../src/healthcare/index";

describe("InMemoryHealthcareBoard", () => {
  it("registers and retrieves patients; unknown id is undefined", () => {
    const b = new InMemoryHealthcareBoard();
    b.register(patient("p1", "Ada", new Date("1990-01-01T00:00:00Z")));
    assert.equal(b.getPatient("p1")?.name, "Ada");
    assert.equal(b.getPatient("nope"), undefined);
  });

  it("schedules appointments and lists them ascending by AtUtc", () => {
    const b = new InMemoryHealthcareBoard();
    b.schedule(healthAppointment("a2", "p1", "Dr B", new Date("2026-03-02T09:00:00Z"), "Booked"));
    b.schedule(healthAppointment("a1", "p1", "Dr A", new Date("2026-03-01T09:00:00Z"), "Booked"));
    b.schedule(healthAppointment("a3", "p2", "Dr C", new Date("2026-01-01T09:00:00Z"), "Booked"));
    const forP1 = b.appointmentsFor("p1");
    assert.deepEqual(
      forP1.map((a) => a.apptId),
      ["a1", "a2"],
    );
  });

  it("updateStatus mutates the stored appointment; unknown throws", () => {
    const b = new InMemoryHealthcareBoard();
    b.schedule(healthAppointment("a1", "p1", "Dr A", new Date("2026-03-01T09:00:00Z"), "Booked"));
    b.updateStatus("a1", "Completed");
    assert.equal(b.appointmentsFor("p1")[0].status, "Completed");
    assert.throws(() => b.updateStatus("ghost", "X"), /Unknown appointment ghost/);
  });

  it("prescriptions list newest-first (PrescribedUtc descending)", () => {
    const b = new InMemoryHealthcareBoard();
    b.prescribe(prescription("r1", "p1", "Med", "1", "od", new Date("2026-01-01T00:00:00Z")));
    b.prescribe(prescription("r2", "p1", "Med2", "2", "bd", new Date("2026-06-01T00:00:00Z")));
    b.prescribe(prescription("r3", "p2", "Med3", "3", "tds", new Date("2026-05-01T00:00:00Z")));
    assert.deepEqual(
      b.prescriptionsFor("p1").map((r) => r.rxId),
      ["r2", "r1"],
    );
  });

  it("null arguments are rejected", () => {
    const b = new InMemoryHealthcareBoard();
    assert.throws(() => b.register(null as never));
    assert.throws(() => b.schedule(null as never));
    assert.throws(() => b.prescribe(null as never));
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(HealthcareDomainContext.systemPromptSnippet.includes("[DOMAIN: Healthcare]"));
    assert.deepEqual(HealthcareDomainContext.complianceFlags, [
      "HIPAA",
      "POPIA",
      "Health_Professions_Act_56_1974",
      "NHA_61_2003",
      "ICD10",
    ]);
    assert.deepEqual(HealthcareDomainContext.suggestedTools, [
      "ehr_system",
      "appointment_scheduler",
      "document_editor",
      "icd10_lookup",
    ]);
  });
});
