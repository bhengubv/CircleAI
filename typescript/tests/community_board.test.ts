// community_board.test.ts
// Verifies the CircleAI.Community port: groups for member, announcements
// newest-first + limit, upcoming volunteer opportunities.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryCommunityBoard,
  CommunityDomainContext,
  communityGroup,
  announcement,
  volunteerOpportunity,
} from "../src/community/index";

describe("InMemoryCommunityBoard", () => {
  it("creates groups and finds groups for a member", () => {
    const b = new InMemoryCommunityBoard();
    b.create(communityGroup("g1", "Gardeners", "Grow food", ["u1", "u2"]));
    b.create(communityGroup("g2", "Runners", "Jog", ["u2", "u3"]));
    assert.equal(b.getGroup("g1")?.name, "Gardeners");
    assert.deepEqual(
      b.groupsForMember("u1").map((g) => g.groupId),
      ["g1"],
    );
    assert.deepEqual(
      b.groupsForMember("u2").map((g) => g.groupId).sort(),
      ["g1", "g2"],
    );
  });

  it("lists announcements newest-first capped by limit", () => {
    const b = new InMemoryCommunityBoard();
    b.post(announcement("a1", "g1", "One", "b", new Date("2026-01-01T00:00:00Z")));
    b.post(announcement("a2", "g1", "Two", "b", new Date("2026-01-03T00:00:00Z")));
    b.post(announcement("a3", "g1", "Three", "b", new Date("2026-01-02T00:00:00Z")));
    b.post(announcement("a4", "g2", "Other", "b", new Date("2026-01-05T00:00:00Z")));
    assert.deepEqual(
      b.announcementsFor("g1").map((a) => a.announcementId),
      ["a2", "a3", "a1"],
    );
    assert.deepEqual(
      b.announcementsFor("g1", 2).map((a) => a.announcementId),
      ["a2", "a3"],
    );
  });

  it("lists future volunteer opportunities ascending", () => {
    const b = new InMemoryCommunityBoard();
    const soon = new Date(Date.now() + 86_400_000);
    const later = new Date(Date.now() + 172_800_000);
    b.list(volunteerOpportunity("o1", "g1", "Later", 5, later));
    b.list(volunteerOpportunity("o2", "g1", "Soon", 3, soon));
    b.list(volunteerOpportunity("o3", "g1", "Past", 3, new Date(Date.now() - 86_400_000)));
    assert.deepEqual(
      b.opportunities().map((o) => o.oppId),
      ["o2", "o1"],
    );
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(CommunityDomainContext.systemPromptSnippet.includes("[DOMAIN: Community]"));
    assert.deepEqual(CommunityDomainContext.complianceFlags, ["NPO_Act", "Fundraising_Act", "POPIA"]);
    assert.deepEqual(CommunityDomainContext.suggestedTools, [
      "event_manager",
      "document_editor",
      "communication_tools",
      "volunteer_tracker",
    ]);
  });
});
