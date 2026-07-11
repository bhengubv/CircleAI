// realestate_board.test.ts
// Verifies the CircleAI.RealEstate port: property/listing registration, close,
// active-in-suburb (case-insensitive, listed-desc), suburb average, PropertyKind.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryRealEstateBoard,
  RealEstateDomainContext,
  PropertyKind,
  property,
  listing,
  valuation,
  viewing,
} from "../src/realestate/index";

const D = (s: string) => new Date(s);

describe("PropertyKind enum", () => {
  it("exposes the C# members", () => {
    assert.equal(PropertyKind.Apartment, "Apartment");
    assert.equal(PropertyKind.Land, "Land");
  });
});

describe("InMemoryRealEstateBoard", () => {
  it("lists active properties in a suburb ordered by listed date desc", () => {
    const b = new InMemoryRealEstateBoard();
    b.registerProperty(property("p1", "Sandton", PropertyKind.Apartment, 2, 2, 90));
    b.registerProperty(property("p2", "Sandton", PropertyKind.House, 4, 3, 250));
    b.registerProperty(property("p3", "Rosebank", PropertyKind.Townhouse, 3, 2, 150));
    b.list(listing("l1", "p1", 2_000_000, "ZAR", D("2026-01-01T00:00:00Z"), true));
    b.list(listing("l2", "p2", 6_000_000, "ZAR", D("2026-03-01T00:00:00Z"), true));
    b.list(listing("l3", "p3", 3_000_000, "ZAR", D("2026-02-01T00:00:00Z"), true));
    const sandton = b.activeInSuburb("SANDTON"); // case-insensitive
    assert.deepEqual(
      sandton.map((l) => l.listingId),
      ["l2", "l1"],
    );
    assert.equal(b.suburbAverage("Sandton"), 4_000_000);
  });

  it("close() deactivates and excludes from active + average; empty suburb → null", () => {
    const b = new InMemoryRealEstateBoard();
    b.registerProperty(property("p1", "Sandton", PropertyKind.Apartment, 2, 2, 90));
    b.list(listing("l1", "p1", 2_000_000, "ZAR", D("2026-01-01T00:00:00Z"), true));
    b.close("l1");
    assert.deepEqual(b.activeInSuburb("Sandton"), []);
    assert.equal(b.suburbAverage("Sandton"), null);
    assert.equal(b.suburbAverage("Nowhere"), null);
    assert.throws(() => b.close("ghost"), /Unknown listing ghost/);
    assert.throws(() => b.activeInSuburb(" "), /suburb required/);
  });

  it("records valuations and viewings without error", () => {
    const b = new InMemoryRealEstateBoard();
    assert.doesNotThrow(() => b.value(valuation("p1", 1_800_000, "AVM", D("2026-01-01T00:00:00Z"))));
    assert.doesNotThrow(() => b.scheduleViewing(viewing("v1", "l1", "Buyer", D("2026-01-05T00:00:00Z"))));
    assert.throws(() => b.registerProperty(null as never));
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(RealEstateDomainContext.systemPromptSnippet.includes("[DOMAIN: RealEstate]"));
    assert.deepEqual(RealEstateDomainContext.complianceFlags, [
      "Alienation_of_Land_Act",
      "Rental_Housing_Act",
      "PPRA",
      "FICA",
      "POPIA",
    ]);
    assert.deepEqual(RealEstateDomainContext.suggestedTools, ["property_listings", "document_editor", "map", "analytics"]);
  });
});
