// beauty_board.test.ts
// Verifies the CircleAI.Beauty port: treatments, appointments-between,
// profiles, concern-based recommender.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryBeautyBoard,
  BeautyDomainContext,
  treatment,
  appointment,
  skinProfile,
} from "../src/beauty/index";

describe("InMemoryBeautyBoard", () => {
  it("adds treatments and returns appointments within a window, ordered", () => {
    const b = new InMemoryBeautyBoard();
    b.addTreatment(treatment("t1", "Facial", 60, 500, "ZAR"));
    assert.equal(b.getTreatment("t1")?.name, "Facial");
    b.book(appointment("a1", "Ann", "t1", new Date("2026-01-05T10:00:00Z"), null));
    b.book(appointment("a2", "Bea", "t1", new Date("2026-01-01T10:00:00Z"), "note"));
    b.book(appointment("a3", "Cy", "t1", new Date("2026-01-20T10:00:00Z"), null)); // out of window
    assert.deepEqual(
      b
        .appointmentsBetween(new Date("2026-01-01T00:00:00Z"), new Date("2026-01-10T00:00:00Z"))
        .map((a) => a.apptId),
      ["a2", "a1"],
    );
  });

  it("recommends treatments matching profile concerns, [] when no profile", () => {
    const b = new InMemoryBeautyBoard();
    b.addTreatment(treatment("t1", "Acne Clearing", 45, 400, "ZAR"));
    b.addTreatment(treatment("t2", "Anti-Ageing Serum", 30, 600, "ZAR"));
    b.addTreatment(treatment("t3", "Relaxing Massage", 60, 700, "ZAR"));
    assert.equal(b.recommendFor("nobody").length, 0);
    b.saveProfile(skinProfile("Ann", "oily", ["acne", "ageing"]));
    assert.deepEqual(
      b.recommendFor("Ann").map((t) => t.treatmentId).sort(),
      ["t1", "t2"],
    );
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(BeautyDomainContext.systemPromptSnippet.includes("[DOMAIN: Beauty]"));
    assert.deepEqual(BeautyDomainContext.complianceFlags, ["POPIA", "Medicines_Act_cosmetic_claims"]);
    assert.deepEqual(BeautyDomainContext.suggestedTools, ["product_db", "ingredient_checker", "web_search"]);
  });
});
