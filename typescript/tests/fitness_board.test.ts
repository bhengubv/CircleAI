// fitness_board.test.ts
// Verifies the CircleAI.Fitness port: weekly workouts, calories-since, goals,
// exercise sets.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryFitnessBoard,
  FitnessDomainContext,
  workout,
  fitnessGoal,
  exerciseSet,
} from "../src/fitness/index";

describe("InMemoryFitnessBoard", () => {
  it("lists this week's workouts oldest-first from the Sunday week-start", () => {
    const b = new InMemoryFitnessBoard();
    const now = new Date("2026-01-07T12:00:00Z"); // Wed; week start Sun 2026-01-04
    b.log(workout("w0", "u1", "run", 30, 300, new Date("2026-01-03T23:00:00Z"))); // before
    b.log(workout("w1", "u1", "run", 30, 300, new Date("2026-01-06T09:00:00Z")));
    b.log(workout("w2", "u1", "lift", 45, 200, new Date("2026-01-05T09:00:00Z")));
    assert.deepEqual(
      b.workoutsThisWeek("u1", now).map((w) => w.workoutId),
      ["w2", "w1"],
    );
  });

  it("sums calories since a cutoff for one user", () => {
    const b = new InMemoryFitnessBoard();
    b.log(workout("w1", "u1", "run", 30, 300, new Date("2026-01-01T09:00:00Z")));
    b.log(workout("w2", "u1", "run", 30, 250, new Date("2026-01-05T09:00:00Z")));
    b.log(workout("w3", "u2", "run", 30, 999, new Date("2026-01-05T09:00:00Z")));
    assert.equal(b.totalCaloriesSince("u1", new Date("2026-01-02T00:00:00Z")), 250);
  });

  it("stores goals per user and sets per workout", () => {
    const b = new InMemoryFitnessBoard();
    b.setGoal(fitnessGoal("g1", "u1", "weight", 75, new Date("2026-06-01T00:00:00Z")));
    b.setGoal(fitnessGoal("g2", "u2", "5k", 22, new Date("2026-06-01T00:00:00Z")));
    assert.deepEqual(
      b.goalsFor("u1").map((g) => g.goalId),
      ["g1"],
    );
    b.addSet(exerciseSet("s1", "w1", "squat", 5, 100));
    b.addSet(exerciseSet("s2", "w1", "bench", 5, 80));
    b.addSet(exerciseSet("s3", "w2", "row", 8, 60));
    assert.deepEqual(
      b.setsFor("w1").map((s) => s.setId),
      ["s1", "s2"],
    );
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(FitnessDomainContext.systemPromptSnippet.includes("[DOMAIN: Fitness]"));
    assert.deepEqual(FitnessDomainContext.complianceFlags, ["HPCSA_Fitness", "POPIA", "Not_Medical_Advice"]);
    assert.deepEqual(FitnessDomainContext.suggestedTools, [
      "fitness_tracker",
      "exercise_db",
      "nutrition_tools",
      "analytics",
    ]);
  });
});
