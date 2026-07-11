// hospitality_board.test.ts
// Verifies the CircleAI.Hospitality port: availability (booked + clean),
// checkout marking a room dirty, front-desk notes newest-first.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryHospitalityBoard,
  HospitalityDomainContext,
  hotelRoom,
  guestReservation,
  frontDeskNote,
} from "../src/hospitality/index";

describe("InMemoryHospitalityBoard", () => {
  it("returns rooms that are clean and not booked over the date", () => {
    const b = new InMemoryHospitalityBoard();
    b.addRoom(hotelRoom("r1", "Std", 1000, "ZAR", true));
    b.addRoom(hotelRoom("r2", "Std", 1000, "ZAR", true));
    b.addRoom(hotelRoom("r3", "Dlx", 2000, "ZAR", false)); // dirty
    // r1 booked across the query date.
    b.reserve(guestReservation("res1", "Ann", "r1", new Date("2026-01-01T00:00:00Z"), new Date("2026-01-05T00:00:00Z")));
    const avail = b.availableOn(new Date("2026-01-03T00:00:00Z")).map((r) => r.roomId);
    assert.deepEqual(avail, ["r2"]);
  });

  it("treats checkout day as available (CheckOut is exclusive)", () => {
    const b = new InMemoryHospitalityBoard();
    b.addRoom(hotelRoom("r1", "Std", 1000, "ZAR", true));
    b.reserve(guestReservation("res1", "Ann", "r1", new Date("2026-01-01T00:00:00Z"), new Date("2026-01-05T00:00:00Z")));
    assert.deepEqual(
      b.availableOn(new Date("2026-01-05T00:00:00Z")).map((r) => r.roomId),
      ["r1"],
    );
  });

  it("checkout marks the room dirty only when cleaning is needed", () => {
    const b = new InMemoryHospitalityBoard();
    b.addRoom(hotelRoom("r1", "Std", 1000, "ZAR", true));
    b.reserve(guestReservation("res1", "Ann", "r1", new Date("2026-01-01T00:00:00Z"), new Date("2026-01-05T00:00:00Z")));
    b.checkOut("res1", true);
    assert.equal(b.getRoom("r1")?.isClean, false);
    assert.equal(b.getReservation("res1")?.guestName, "Ann");
  });

  it("checkout throws on unknown reservation", () => {
    const b = new InMemoryHospitalityBoard();
    assert.throws(() => b.checkOut("ghost", true), /Unknown reservation ghost/);
  });

  it("lists front-desk notes newest-first", () => {
    const b = new InMemoryHospitalityBoard();
    b.addNote(frontDeskNote("n1", "res1", "early", new Date("2026-01-01T08:00:00Z")));
    b.addNote(frontDeskNote("n2", "res1", "late", new Date("2026-01-01T20:00:00Z")));
    b.addNote(frontDeskNote("n3", "res2", "other", new Date("2026-01-01T20:00:00Z")));
    assert.deepEqual(
      b.notesFor("res1").map((n) => n.noteId),
      ["n2", "n1"],
    );
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(HospitalityDomainContext.systemPromptSnippet.includes("[DOMAIN: Hospitality]"));
    assert.deepEqual(HospitalityDomainContext.complianceFlags, [
      "Tourism_Act",
      "CATHSSETA",
      "Liquor_Act",
      "Health_Regs",
      "POPIA",
    ]);
    assert.deepEqual(HospitalityDomainContext.suggestedTools, [
      "pms_system",
      "analytics",
      "document_editor",
      "reservation_engine",
    ]);
  });
});
