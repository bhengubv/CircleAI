// tourism_board.test.ts
// Verifies the CircleAI.Tourism port: attractions in city / by tag (ordered by
// name), itineraries, and the bookings snapshot.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryTourismBoard,
  TourismDomainContext,
  attraction,
  itinerary,
  itineraryItem,
  tourismBooking,
} from "../src/tourism/index";

describe("InMemoryTourismBoard", () => {
  it("lists attractions in a city (case-insensitive) ordered by name", () => {
    const b = new InMemoryTourismBoard();
    b.add(attraction("a1", "Table Mountain", "Cape Town", "ZA", -33.9, 18.4, ["nature"]));
    b.add(attraction("a2", "Bo-Kaap", "cape town", "ZA", -33.9, 18.4, ["culture", "history"]));
    b.add(attraction("a3", "Gold Reef", "Johannesburg", "ZA", -26.2, 28.0, ["theme-park"]));
    assert.deepEqual(
      b.attractionsInCity("CAPE TOWN").map((a) => a.attractionId),
      ["a2", "a1"], // "Bo-Kaap" < "Table Mountain"
    );
  });

  it("lists attractions by tag (case-insensitive) ordered by name", () => {
    const b = new InMemoryTourismBoard();
    b.add(attraction("a1", "Zoo", "X", "ZA", 0, 0, ["Family"]));
    b.add(attraction("a2", "Aquarium", "X", "ZA", 0, 0, ["family", "water"]));
    assert.deepEqual(
      b.byTag("FAMILY").map((a) => a.attractionId),
      ["a2", "a1"],
    );
  });

  it("throws on blank city/tag", () => {
    const b = new InMemoryTourismBoard();
    assert.throws(() => b.attractionsInCity("  "), /city required/);
    assert.throws(() => b.byTag(""), /tag required/);
  });

  it("stores itineraries and snapshots bookings", () => {
    const b = new InMemoryTourismBoard();
    b.plan(itinerary("i1", "Weekend", [itineraryItem(0, 32_400_000, 43_200_000, "a1", "morning")]));
    assert.equal(b.getItinerary("i1")?.title, "Weekend");
    b.book(tourismBooking("bk1", "i1", new Date("2026-02-01T00:00:00Z"), 2, 5000, "ZAR"));
    const snap = b.bookings;
    assert.equal(snap.length, 1);
    assert.equal(snap[0].bookingId, "bk1");
    // Snapshot is a copy — booking more does not mutate the earlier snapshot.
    b.book(tourismBooking("bk2", "i1", new Date("2026-03-01T00:00:00Z"), 1, 2500, "ZAR"));
    assert.equal(snap.length, 1);
    assert.equal(b.bookings.length, 2);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(TourismDomainContext.systemPromptSnippet.includes("[DOMAIN: Tourism]"));
    assert.deepEqual(TourismDomainContext.complianceFlags, ["Tourism_Act_3_2014", "SABS_Tour_Ops", "SATSA", "POPIA"]);
    assert.deepEqual(TourismDomainContext.suggestedTools, ["mapping", "booking_system", "document_editor", "weather_api"]);
  });
});
