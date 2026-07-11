//! fitness_test.rs
//!
//! Ports the behaviour of `CircleAI.Fitness`: workout log + weekly filter +
//! calorie sum + goals + exercise sets.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::fitness::{ExerciseSet, FitnessGoal, IFitnessBoard, InMemoryFitnessBoard, Workout};

#[test]
fn workouts_this_week_filtered_and_ordered() {
    let board = InMemoryFitnessBoard::new();
    // now = Wednesday 2026-01-07; week starts Sunday 2026-01-04.
    let now = Utc.with_ymd_and_hms(2026, 1, 7, 12, 0, 0).unwrap();
    board.log(Workout::new("w2", "u", "Run", 30, 300.0, now));
    board.log(Workout::new("w1", "u", "Bike", 45, 400.0, Utc.with_ymd_and_hms(2026, 1, 5, 8, 0, 0).unwrap()));
    board.log(Workout::new("w0", "u", "Old", 60, 999.0, Utc.with_ymd_and_hms(2026, 1, 2, 8, 0, 0).unwrap()));

    let wk = board.workouts_this_week("u", now);
    assert_eq!(wk.len(), 2);
    assert_eq!(wk[0].workout_id, "w1"); // earliest first
    assert_eq!(wk[1].workout_id, "w2");
}

#[test]
fn total_calories_since() {
    let board = InMemoryFitnessBoard::new();
    let base = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.log(Workout::new("w1", "u", "Run", 30, 300.0, base));
    board.log(Workout::new("w2", "u", "Run", 30, 250.0, base + Duration::hours(2)));
    board.log(Workout::new("w3", "other", "Run", 30, 900.0, base + Duration::hours(2)));

    assert!((board.total_calories_since("u", base) - 550.0).abs() < 1e-9);
    assert!((board.total_calories_since("u", base + Duration::hours(1)) - 250.0).abs() < 1e-9);
}

#[test]
fn goals_are_per_user_and_overwrite_by_id() {
    let board = InMemoryFitnessBoard::new();
    let due = Utc.with_ymd_and_hms(2026, 6, 1, 0, 0, 0).unwrap();
    board.set_goal(FitnessGoal::new("g1", "u", "weight", 80.0, due));
    board.set_goal(FitnessGoal::new("g1", "u", "weight", 78.0, due)); // overwrite
    board.set_goal(FitnessGoal::new("g2", "other", "steps", 10000.0, due));

    let goals = board.goals_for("u");
    assert_eq!(goals.len(), 1);
    assert!((goals[0].target - 78.0).abs() < 1e-9);
}

#[test]
fn sets_for_workout() {
    let board = InMemoryFitnessBoard::new();
    board.add_set(ExerciseSet::new("s1", "w1", "Squat", 5, 100.0));
    board.add_set(ExerciseSet::new("s2", "w1", "Bench", 5, 80.0));
    board.add_set(ExerciseSet::new("s3", "w2", "Deadlift", 3, 140.0));

    assert_eq!(board.sets_for("w1").len(), 2);
    assert_eq!(board.sets_for("w2").len(), 1);
    assert_eq!(board.sets_for("w3").len(), 0);
}
