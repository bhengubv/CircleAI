// sports_board.test.ts
// Verifies the CircleAI.Sports port: activity log + history ordering, weekly km,
// best-over-distance, scheduling + completion + upcoming.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemorySportsBoard,
  SportsDomainContext,
  DistanceKind,
  activity,
  trainingSession,
} from "../src/sports/index";

describe("InMemorySportsBoard", () => {
  it("logs activities and returns history newest-first, capped by limit", () => {
    const b = new InMemorySportsBoard();
    b.log(activity("a1", "u1", DistanceKind.Run, 5, 1_500_000, new Date("2026-01-01T06:00:00Z")));
    b.log(activity("a2", "u1", DistanceKind.Run, 10, 3_000_000, new Date("2026-01-03T06:00:00Z")));
    b.log(activity("a3", "u1", DistanceKind.Run, 3, 900_000, new Date("2026-01-02T06:00:00Z")));
    b.log(activity("a4", "u2", DistanceKind.Run, 8, 2_400_000, new Date("2026-01-04T06:00:00Z")));
    assert.deepEqual(
      b.history("u1").map((a) => a.activityId),
      ["a2", "a3", "a1"],
    );
    assert.deepEqual(
      b.history("u1", 2).map((a) => a.activityId),
      ["a2", "a3"],
    );
  });

  it("history throws on non-positive limit", () => {
    const b = new InMemorySportsBoard();
    assert.throws(() => b.history("u1", 0), /limit/);
  });

  it("sums this week's km from the Sunday week-start", () => {
    const b = new InMemorySportsBoard();
    // now = Wed 2026-01-07. Week start = Sun 2026-01-04 00:00Z.
    const now = new Date("2026-01-07T12:00:00Z");
    b.log(activity("a1", "u1", DistanceKind.Run, 5, 1, new Date("2026-01-03T23:59:59Z"))); // before week start
    b.log(activity("a2", "u1", DistanceKind.Run, 10, 1, new Date("2026-01-04T00:00:00Z"))); // exactly week start
    b.log(activity("a3", "u1", DistanceKind.Run, 7, 1, new Date("2026-01-06T09:00:00Z")));
    b.log(activity("a4", "u1", DistanceKind.Bike, 100, 1, new Date("2026-01-06T09:00:00Z"))); // wrong kind
    assert.equal(b.totalKmThisWeek("u1", DistanceKind.Run, now), 17);
    assert.equal(b.totalKmThisWeek("u1", DistanceKind.Bike, now), 100);
  });

  it("best returns the fastest qualifying activity, projected to the requested distance", () => {
    const b = new InMemorySportsBoard();
    b.log(activity("a1", "u1", DistanceKind.Run, 10, 3_000_000, new Date("2026-01-01T06:00:00Z")));
    b.log(activity("a2", "u1", DistanceKind.Run, 12, 2_500_000, new Date("2026-01-02T06:00:00Z")));
    b.log(activity("a3", "u1", DistanceKind.Run, 5, 900_000, new Date("2026-01-03T06:00:00Z"))); // too short
    const best = b.best("u1", DistanceKind.Run, 10);
    assert.ok(best);
    assert.equal(best?.distanceKm, 10);
    assert.equal(best?.timeMs, 2_500_000);
    assert.equal(best?.achievedUtc.toISOString(), "2026-01-02T06:00:00.000Z");
    assert.equal(b.best("u1", DistanceKind.Run, 100), undefined);
  });

  it("schedules sessions, completes them, and lists only future incomplete ones", () => {
    const b = new InMemorySportsBoard();
    const future = new Date(Date.now() + 86_400_000);
    const past = new Date(Date.now() - 86_400_000);
    b.schedule(trainingSession("s1", "u1", "Long run", future, false));
    b.schedule(trainingSession("s2", "u1", "Tempo", new Date(Date.now() + 172_800_000), false));
    b.schedule(trainingSession("s3", "u1", "Old", past, false));
    b.complete("s2");
    assert.deepEqual(
      b.upcoming("u1").map((s) => s.sessionId),
      ["s1"],
    );
  });

  it("complete throws on unknown session", () => {
    const b = new InMemorySportsBoard();
    assert.throws(() => b.complete("ghost"), /Unknown session ghost/);
  });

  it("DistanceKind values match the C# enum ordinals", () => {
    assert.equal(DistanceKind.Run, 0);
    assert.equal(DistanceKind.Row, 4);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(SportsDomainContext.systemPromptSnippet.includes("[DOMAIN: Sports]"));
    assert.deepEqual(SportsDomainContext.complianceFlags, ["WADA", "SASCOC", "Sport_Recreation_SA", "POPIA"]);
    assert.deepEqual(SportsDomainContext.suggestedTools, [
      "performance_tracker",
      "analytics",
      "schedule_manager",
      "document_editor",
    ]);
  });
});
