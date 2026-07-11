// travel_board.test.ts
// Verifies the CircleAI.Travel port: add flight/stay overloads, trip cost
// (flights + nights, min 1 night, missing ids skipped), upcoming trips.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryTravelBoard,
  TravelDomainContext,
  flight,
  hotelStay,
  travelTrip,
} from "../src/travel/index";

describe("InMemoryTravelBoard", () => {
  it("computes trip cost from flight prices and hotel nights", () => {
    const b = new InMemoryTravelBoard();
    b.add(flight("f1", "JNB", "CPT", new Date("2026-02-01T08:00:00Z"), new Date("2026-02-01T10:00:00Z"), "CemAir", "Y", 1500, "ZAR"));
    b.add(hotelStay("s1", "Grand", "CPT", new Date("2026-02-01T00:00:00Z"), new Date("2026-02-04T00:00:00Z"), 1200, "ZAR"));
    b.plan(travelTrip("t1", "CT Trip", new Date("2026-02-01T00:00:00Z"), new Date("2026-02-04T00:00:00Z"), ["f1"], ["s1"]));
    // 1500 + 1200*3 nights = 5100
    assert.equal(b.tripCost("t1"), 5100);
    assert.equal(b.getFlight("f1")?.carrier, "CemAir");
    assert.equal(b.getStay("s1")?.hotel, "Grand");
  });

  it("charges a minimum of one night and skips missing flight/stay ids", () => {
    const b = new InMemoryTravelBoard();
    b.add(hotelStay("s1", "H", "X", new Date("2026-02-01T00:00:00Z"), new Date("2026-02-01T00:00:00Z"), 900, "ZAR")); // same-day → 1 night
    b.plan(travelTrip("t1", "T", new Date("2026-02-01T00:00:00Z"), new Date("2026-02-02T00:00:00Z"), ["missing"], ["s1"]));
    assert.equal(b.tripCost("t1"), 900);
  });

  it("tripCost throws on unknown trip", () => {
    const b = new InMemoryTravelBoard();
    assert.throws(() => b.tripCost("ghost"), /Unknown trip ghost/);
  });

  it("lists upcoming trips (StartDate >= now) ascending", () => {
    const b = new InMemoryTravelBoard();
    b.plan(travelTrip("t1", "Past", new Date("2026-01-01T00:00:00Z"), new Date("2026-01-02T00:00:00Z"), [], []));
    b.plan(travelTrip("t2", "Soon", new Date("2026-03-01T00:00:00Z"), new Date("2026-03-05T00:00:00Z"), [], []));
    b.plan(travelTrip("t3", "Later", new Date("2026-05-01T00:00:00Z"), new Date("2026-05-05T00:00:00Z"), [], []));
    assert.deepEqual(
      b.upcomingTrips(new Date("2026-02-01T00:00:00Z")).map((t) => t.tripId),
      ["t2", "t3"],
    );
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(TravelDomainContext.systemPromptSnippet.includes("[DOMAIN: Travel]"));
    assert.deepEqual(TravelDomainContext.complianceFlags, ["POPIA", "Consumer_Protection_Act", "IATA_aware"]);
    assert.deepEqual(TravelDomainContext.suggestedTools, ["flight_search", "mapping", "currency_converter", "web_search"]);
  });
});
