//! fitness — CircleAI fitness-board primitives.
//!
//! Full Rust port of `src/CircleAI.Fitness/FitnessPrimitives.cs`:
//!
//! - Records [`Workout`] / [`FitnessGoal`] / [`ExerciseSet`], the
//!   [`IFitnessBoard`] contract, and the deterministic in-memory
//!   [`InMemoryFitnessBoard`] (workout log + weekly filter + calorie sum +
//!   goals + exercise sets).
//!
//! Sync-only; `DateTimeOffset`/`DateTime` → [`chrono::DateTime<Utc>`].

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Datelike, Duration, Utc};

/// (Fitness) A logged workout.
///
/// Mirrors `sealed record Workout(string WorkoutId, string UserId, string Kind,
/// int DurationMinutes, double CaloriesBurned, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Workout {
    pub workout_id: String,
    pub user_id: String,
    pub kind: String,
    pub duration_minutes: i32,
    pub calories_burned: f64,
    pub at_utc: DateTime<Utc>,
}

impl Workout {
    /// Constructs a workout, mirroring the positional C# record constructor.
    pub fn new(
        workout_id: impl Into<String>,
        user_id: impl Into<String>,
        kind: impl Into<String>,
        duration_minutes: i32,
        calories_burned: f64,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            workout_id: workout_id.into(),
            user_id: user_id.into(),
            kind: kind.into(),
            duration_minutes,
            calories_burned,
            at_utc,
        }
    }
}

/// (Fitness) A fitness goal.
///
/// Mirrors `sealed record FitnessGoal(string GoalId, string UserId,
/// string Metric, double Target, DateTime DueOn)`.
#[derive(Debug, Clone, PartialEq)]
pub struct FitnessGoal {
    pub goal_id: String,
    pub user_id: String,
    pub metric: String,
    pub target: f64,
    pub due_on: DateTime<Utc>,
}

impl FitnessGoal {
    /// Constructs a goal, mirroring the positional C# record constructor.
    pub fn new(
        goal_id: impl Into<String>,
        user_id: impl Into<String>,
        metric: impl Into<String>,
        target: f64,
        due_on: DateTime<Utc>,
    ) -> Self {
        Self {
            goal_id: goal_id.into(),
            user_id: user_id.into(),
            metric: metric.into(),
            target,
            due_on,
        }
    }
}

/// (Fitness) A set within a workout.
///
/// Mirrors `sealed record ExerciseSet(string SetId, string WorkoutId,
/// string Exercise, int Reps, double WeightKg)`.
#[derive(Debug, Clone, PartialEq)]
pub struct ExerciseSet {
    pub set_id: String,
    pub workout_id: String,
    pub exercise: String,
    pub reps: i32,
    pub weight_kg: f64,
}

impl ExerciseSet {
    /// Constructs a set, mirroring the positional C# record constructor.
    pub fn new(
        set_id: impl Into<String>,
        workout_id: impl Into<String>,
        exercise: impl Into<String>,
        reps: i32,
        weight_kg: f64,
    ) -> Self {
        Self {
            set_id: set_id.into(),
            workout_id: workout_id.into(),
            exercise: exercise.into(),
            reps,
            weight_kg,
        }
    }
}

/// (Fitness) The fitness-board contract.
///
/// Mirrors `interface IFitnessBoard`.
pub trait IFitnessBoard {
    /// Logs a workout.
    fn log(&self, w: Workout);
    /// This calendar week's workouts for a user (week starts Sunday), earliest first.
    fn workouts_this_week(&self, user_id: &str, now: DateTime<Utc>) -> Vec<Workout>;
    /// Total calories burned since `since`.
    fn total_calories_since(&self, user_id: &str, since: DateTime<Utc>) -> f64;
    /// Sets (or overwrites) a goal.
    fn set_goal(&self, g: FitnessGoal);
    /// A user's goals.
    fn goals_for(&self, user_id: &str) -> Vec<FitnessGoal>;
    /// Adds a set.
    fn add_set(&self, s: ExerciseSet);
    /// The sets for a workout.
    fn sets_for(&self, workout_id: &str) -> Vec<ExerciseSet>;
}

/// (Fitness) In-memory [`IFitnessBoard`].
///
/// Mirrors `sealed class InMemoryFitnessBoard`.
pub struct InMemoryFitnessBoard {
    workouts: Mutex<Vec<Workout>>,
    goals: Mutex<HashMap<String, FitnessGoal>>,
    sets: Mutex<Vec<ExerciseSet>>,
}

impl InMemoryFitnessBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            workouts: Mutex::new(Vec::new()),
            goals: Mutex::new(HashMap::new()),
            sets: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryFitnessBoard {
    fn default() -> Self {
        Self::new()
    }
}

/// Week start: `now.Date.AddDays(-(int)now.DayOfWeek)` (Sunday = 0).
fn week_start(now: DateTime<Utc>) -> DateTime<Utc> {
    let dow = now.weekday().num_days_from_sunday() as i64;
    let date = now.date_naive() - Duration::days(dow);
    date.and_hms_opt(0, 0, 0).unwrap().and_utc()
}

impl IFitnessBoard for InMemoryFitnessBoard {
    fn log(&self, w: Workout) {
        self.workouts.lock().unwrap().push(w);
    }

    fn workouts_this_week(&self, user_id: &str, now: DateTime<Utc>) -> Vec<Workout> {
        let start = week_start(now);
        let mut hits: Vec<Workout> = self
            .workouts
            .lock()
            .unwrap()
            .iter()
            .filter(|w| w.user_id == user_id && w.at_utc >= start)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        hits
    }

    fn total_calories_since(&self, user_id: &str, since: DateTime<Utc>) -> f64 {
        self.workouts
            .lock()
            .unwrap()
            .iter()
            .filter(|w| w.user_id == user_id && w.at_utc >= since)
            .map(|w| w.calories_burned)
            .sum()
    }

    fn set_goal(&self, g: FitnessGoal) {
        self.goals.lock().unwrap().insert(g.goal_id.clone(), g);
    }

    fn goals_for(&self, user_id: &str) -> Vec<FitnessGoal> {
        self.goals
            .lock()
            .unwrap()
            .values()
            .filter(|g| g.user_id == user_id)
            .cloned()
            .collect()
    }

    fn add_set(&self, s: ExerciseSet) {
        self.sets.lock().unwrap().push(s);
    }

    fn sets_for(&self, workout_id: &str) -> Vec<ExerciseSet> {
        self.sets
            .lock()
            .unwrap()
            .iter()
            .filter(|s| s.workout_id == workout_id)
            .cloned()
            .collect()
    }
}
