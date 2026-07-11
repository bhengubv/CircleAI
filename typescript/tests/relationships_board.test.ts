// relationships_board.test.ts
// Verifies the CircleAI.Relationships port: contacts sorted by name,
// upcoming-this-month by day, last-contact, not-contacted-since.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryRelationshipsBoard,
  RelationshipsDomainContext,
  personContact,
  importantDate,
  contactEvent,
} from "../src/relationships/index";

describe("InMemoryRelationshipsBoard", () => {
  it("lists contacts sorted by name (ordinal)", () => {
    const b = new InMemoryRelationshipsBoard();
    b.addContact(personContact("c1", "Zara", "friend", null));
    b.addContact(personContact("c2", "Ann", "sister", "note"));
    assert.equal(b.getContact("c2")?.relationship, "sister");
    assert.deepEqual(
      b.contacts.map((c) => c.name),
      ["Ann", "Zara"],
    );
  });

  it("lists important dates in the current UTC month ordered by day", () => {
    const b = new InMemoryRelationshipsBoard();
    const now = new Date();
    const y = now.getUTCFullYear();
    const m = now.getUTCMonth();
    const mkUtc = (day: number): Date => new Date(Date.UTC(y, m, day));
    b.addImportantDate(importantDate("d1", "c1", "Birthday", mkUtc(20)));
    b.addImportantDate(importantDate("d2", "c2", "Anniversary", mkUtc(5)));
    // A date in a different month must be excluded.
    b.addImportantDate(importantDate("d3", "c3", "Other", new Date(Date.UTC(y, (m + 1) % 12, 15))));
    assert.deepEqual(
      b.upcomingThisMonth().map((d) => d.dateId),
      ["d2", "d1"],
    );
  });

  it("tracks last contact and flags contacts not reached since a cutoff", () => {
    const b = new InMemoryRelationshipsBoard();
    b.addContact(personContact("c1", "Ann", "friend", null));
    b.addContact(personContact("c2", "Ben", "friend", null)); // never contacted
    b.recordTouchpoint(contactEvent("c1", "call", new Date("2026-01-01T00:00:00Z"), null));
    b.recordTouchpoint(contactEvent("c1", "text", new Date("2026-03-01T00:00:00Z"), "hi"));
    assert.equal(b.lastContact("c1")?.toISOString(), "2026-03-01T00:00:00.000Z");
    assert.equal(b.lastContact("c2"), undefined);
    // Cutoff after c1's last touch → both flagged.
    assert.deepEqual(
      b.notContactedSince(new Date("2026-04-01T00:00:00Z")).map((c) => c.contactId).sort(),
      ["c1", "c2"],
    );
    // Cutoff before c1's last touch → only c2 flagged.
    assert.deepEqual(
      b.notContactedSince(new Date("2026-02-01T00:00:00Z")).map((c) => c.contactId),
      ["c2"],
    );
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(RelationshipsDomainContext.systemPromptSnippet.includes("[DOMAIN: Relationships]"));
    assert.deepEqual(RelationshipsDomainContext.complianceFlags, ["POPIA", "Not_Therapy"]);
    assert.deepEqual(RelationshipsDomainContext.suggestedTools, ["journal", "mood_tracker", "calendar"]);
  });
});
