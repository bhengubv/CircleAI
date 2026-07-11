// social_board.test.ts
// Verifies the CircleAI.Social port: reaction counts, follow/unfollow guards,
// follow-graph feed newest-first + limit, followers.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemorySocialBoard,
  SocialDomainContext,
  socialPost,
  reaction,
  follow,
} from "../src/social/index";

describe("InMemorySocialBoard", () => {
  it("counts reactions by kind case-insensitively", () => {
    const b = new InMemorySocialBoard();
    b.post(socialPost("p1", "u1", "hi", new Date("2026-01-01T00:00:00Z"), []));
    b.react(reaction("p1", "u2", "Like", new Date("2026-01-01T00:00:00Z")));
    b.react(reaction("p1", "u3", "like", new Date("2026-01-01T00:00:00Z")));
    b.react(reaction("p1", "u4", "love", new Date("2026-01-01T00:00:00Z")));
    assert.equal(b.reactionCount("p1", "LIKE"), 2);
    assert.equal(b.reactionCount("p1", "love"), 1);
    assert.equal(b.getPost("p1")?.body, "hi");
  });

  it("prevents following yourself and supports unfollow", () => {
    const b = new InMemorySocialBoard();
    assert.throws(() => b.follow(follow("u1", "u1", new Date())), /Cannot follow yourself/);
    b.follow(follow("u1", "u2", new Date("2026-01-01T00:00:00Z")));
    b.follow(follow("u1", "u3", new Date("2026-01-01T00:00:00Z")));
    b.unfollow("u1", "u2");
    assert.deepEqual(b.followers("u3"), ["u1"]);
    assert.deepEqual(b.followers("u2"), []);
  });

  it("builds a feed of followed authors' posts, newest-first, capped by limit", () => {
    const b = new InMemorySocialBoard();
    b.follow(follow("u1", "a", new Date("2026-01-01T00:00:00Z")));
    b.follow(follow("u1", "b", new Date("2026-01-01T00:00:00Z")));
    b.post(socialPost("p1", "a", "1", new Date("2026-01-01T00:00:00Z"), []));
    b.post(socialPost("p2", "b", "2", new Date("2026-01-03T00:00:00Z"), []));
    b.post(socialPost("p3", "c", "3", new Date("2026-01-04T00:00:00Z"), [])); // not followed
    assert.deepEqual(
      b.feedFor("u1").map((p) => p.postId),
      ["p2", "p1"],
    );
    assert.deepEqual(
      b.feedFor("u1", 1).map((p) => p.postId),
      ["p2"],
    );
  });

  it("feedFor throws on non-positive limit", () => {
    const b = new InMemorySocialBoard();
    assert.throws(() => b.feedFor("u1", 0), /limit/);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(SocialDomainContext.systemPromptSnippet.includes("[DOMAIN: Social]"));
    assert.deepEqual(SocialDomainContext.complianceFlags, [
      "POPIA",
      "ASA_Advertising_Code",
      "Platform_Community_Standards",
    ]);
    assert.deepEqual(SocialDomainContext.suggestedTools, ["social_media_api", "analytics", "content_planner", "image_tools"]);
  });
});
