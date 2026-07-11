// FitnessBoardTests.swift
//
// Exercises the Fitness records' Codable round-trips and the deterministic
// behaviour of InMemoryFitnessBoard — workouts this week (asc), calorie totals,
// goals, and exercise sets. Also checks the FitnessDomainContext constants.
// Mirrors CircleAI.Fitness/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class FitnessBoardTests: XCTestCase {

    func testWorkoutCodableRoundTrip() throws {
        let w = Workout(workoutId: "w1", userId: "u1", kind: "run", durationMinutes: 30, caloriesBurned: 300, atUtc: Date(timeIntervalSince1970: 5))
        XCTAssertEqual(try JSONDecoder().decode(Workout.self, from: try JSONEncoder().encode(w)), w)
    }

    func testWorkoutsThisWeekAscending() {
        let b = InMemoryFitnessBoard()
        var cal = Calendar(identifier: .gregorian); cal.timeZone = TimeZone(identifier: "UTC")!
        let now = cal.date(from: DateComponents(year: 2021, month: 1, day: 8, hour: 12))! // Fri
        let d1 = cal.date(from: DateComponents(year: 2021, month: 1, day: 4, hour: 9))!
        let d2 = cal.date(from: DateComponents(year: 2021, month: 1, day: 6, hour: 9))!
        let last = cal.date(from: DateComponents(year: 2021, month: 1, day: 1, hour: 9))!
        b.log(Workout(workoutId: "w2", userId: "u1", kind: "run", durationMinutes: 10, caloriesBurned: 100, atUtc: d2))
        b.log(Workout(workoutId: "w1", userId: "u1", kind: "run", durationMinutes: 10, caloriesBurned: 100, atUtc: d1))
        b.log(Workout(workoutId: "w0", userId: "u1", kind: "run", durationMinutes: 10, caloriesBurned: 100, atUtc: last))
        XCTAssertEqual(b.workoutsThisWeek(userId: "u1", now: now).map { $0.workoutId }, ["w1", "w2"])
    }

    func testTotalCaloriesSince() {
        let b = InMemoryFitnessBoard()
        let base = Date(timeIntervalSince1970: 1000)
        b.log(Workout(workoutId: "w1", userId: "u1", kind: "a", durationMinutes: 1, caloriesBurned: 100, atUtc: base.addingTimeInterval(10)))
        b.log(Workout(workoutId: "w2", userId: "u1", kind: "a", durationMinutes: 1, caloriesBurned: 250, atUtc: base.addingTimeInterval(20)))
        b.log(Workout(workoutId: "w3", userId: "u1", kind: "a", durationMinutes: 1, caloriesBurned: 999, atUtc: base.addingTimeInterval(-5))) // before
        b.log(Workout(workoutId: "w4", userId: "other", kind: "a", durationMinutes: 1, caloriesBurned: 500, atUtc: base.addingTimeInterval(30)))
        XCTAssertEqual(b.totalCaloriesSince(userId: "u1", since: base), 350, accuracy: 1e-9)
    }

    func testGoalsAndSets() {
        let b = InMemoryFitnessBoard()
        b.setGoal(FitnessGoal(goalId: "g1", userId: "u1", metric: "5k", target: 25, dueOn: Date(timeIntervalSince1970: 1)))
        b.setGoal(FitnessGoal(goalId: "g2", userId: "other", metric: "10k", target: 55, dueOn: Date(timeIntervalSince1970: 1)))
        XCTAssertEqual(b.goalsFor(userId: "u1").map { $0.goalId }, ["g1"])
        b.addSet(ExerciseSet(setId: "s1", workoutId: "w1", exercise: "squat", reps: 5, weightKg: 100))
        b.addSet(ExerciseSet(setId: "s2", workoutId: "w1", exercise: "squat", reps: 5, weightKg: 105))
        b.addSet(ExerciseSet(setId: "s3", workoutId: "w2", exercise: "bench", reps: 5, weightKg: 80))
        XCTAssertEqual(Set(b.setsFor(workoutId: "w1").map { $0.setId }), ["s1", "s2"])
    }

    func testDomainContext() {
        XCTAssertTrue(FitnessDomainContext.systemPromptSnippet.contains("[DOMAIN: Fitness]"))
        XCTAssertEqual(FitnessDomainContext.complianceFlags, ["HPCSA_Fitness", "POPIA", "Not_Medical_Advice"])
        XCTAssertEqual(FitnessDomainContext.suggestedTools, ["fitness_tracker", "exercise_db", "nutrition_tools", "analytics"])
    }
}
