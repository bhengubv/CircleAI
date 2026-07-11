// elderly_board.test.ts
// Verifies the CircleAI.Elderly port: per-resident care plans, medication
// reminders (activate/deactivate), latest check-in, and missed-check-in test.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryElderlyCareBoard,
  ElderlyDomainContext,
  carePlan,
  medReminder,
  checkIn,
} from "../src/elderly/index";

const D = (s: string) => new Date(s);

describe("InMemoryElderlyCareBoard", () => {
  it("stores a care plan keyed by resident name", () => {
    const b = new InMemoryElderlyCareBoard();
    b.setPlan(carePlan("cp1", "Gogo", ["Hypertension"], ["Penicillin"], "Prefers morning walks"));
    assert.equal(b.getPlan("Gogo")?.carerNotes, "Prefers morning walks");
    assert.deepEqual(b.getPlan("Gogo")?.medicalConditions, ["Hypertension"]);
    assert.equal(b.getPlan("Nobody"), undefined);
  });

  it("filters active reminders per resident and deactivates by id", () => {
    const b = new InMemoryElderlyCareBoard();
    b.addReminder(medReminder("r1", "Gogo", "Amlodipine", 8 * 3600_000, true));
    b.addReminder(medReminder("r2", "Gogo", "Aspirin", 20 * 3600_000, true));
    b.addReminder(medReminder("r3", "Mkhulu", "Metformin", 8 * 3600_000, true));
    assert.deepEqual(
      b.activeRemindersFor("Gogo").map((r) => r.reminderId),
      ["r1", "r2"],
    );
    b.deactivateReminder("r1");
    assert.deepEqual(
      b.activeRemindersFor("Gogo").map((r) => r.reminderId),
      ["r2"],
    );
    assert.throws(() => b.deactivateReminder("ghost"), /Unknown reminder ghost/);
  });

  it("returns the latest check-in and detects missed check-ins", () => {
    const b = new InMemoryElderlyCareBoard();
    assert.equal(b.latestCheckIn("Gogo"), undefined);
    assert.equal(b.missedCheckIn("Gogo", D("2026-05-01T00:00:00Z")), true); // no check-in at all
    b.recordCheckIn(checkIn("k1", "Gogo", D("2026-05-01T08:00:00Z"), "OK", null));
    b.recordCheckIn(checkIn("k2", "Gogo", D("2026-05-02T08:00:00Z"), "OK", "cheerful"));
    assert.equal(b.latestCheckIn("Gogo")?.checkInId, "k2");
    assert.equal(b.missedCheckIn("Gogo", D("2026-05-02T00:00:00Z")), false); // latest after cutoff
    assert.equal(b.missedCheckIn("Gogo", D("2026-05-03T00:00:00Z")), true); // latest before cutoff
  });

  it("rejects null arguments", () => {
    const b = new InMemoryElderlyCareBoard();
    assert.throws(() => b.setPlan(null as never));
    assert.throws(() => b.addReminder(null as never));
    assert.throws(() => b.recordCheckIn(null as never));
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(ElderlyDomainContext.systemPromptSnippet.includes("[DOMAIN: Elderly]"));
    assert.deepEqual(ElderlyDomainContext.complianceFlags, ["Older_Persons_Act_13_2006", "Social_Assistance_Act", "POPIA"]);
    assert.deepEqual(ElderlyDomainContext.suggestedTools, ["medication_reminder", "calendar", "web_search", "document_editor"]);
  });
});
