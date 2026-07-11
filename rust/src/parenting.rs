//! parenting — CircleAI parenting-board primitives.
//!
//! Full Rust port of `src/CircleAI.Parenting/ParentingPrimitives.cs`:
//!
//! - Enum [`DayOfWeek`] (a faithful port of `System.DayOfWeek`) + records
//!   ([`Child`], [`Milestone`], [`RoutineEntry`], [`Routine`]) +
//!   [`IParentingBoard`] with the deterministic in-memory
//!   [`InMemoryParentingBoard`] (children, milestones, per-day routines, age).
//!
//! `DateTime` fields (offset-less in the C#) map to [`DateTime<Utc>`]; the C#
//! `TimeSpan` returned by `AgeAsOf` maps to [`chrono::Duration`]. Routine keys
//! use the C# `DayOfWeek.ToString()` spelling (`"Sunday"`..`"Saturday"`).

use std::collections::HashMap;
use std::fmt;
use std::sync::Mutex;

use chrono::{DateTime, Duration, Utc};

/// (Parenting) A day of the week.
///
/// Mirrors `System.DayOfWeek` (Sunday = 0 .. Saturday = 6). [`fmt::Display`]
/// reproduces the C# `ToString()` spelling used for routine keys.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum DayOfWeek {
    Sunday,
    Monday,
    Tuesday,
    Wednesday,
    Thursday,
    Friday,
    Saturday,
}

impl fmt::Display for DayOfWeek {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let name = match self {
            DayOfWeek::Sunday => "Sunday",
            DayOfWeek::Monday => "Monday",
            DayOfWeek::Tuesday => "Tuesday",
            DayOfWeek::Wednesday => "Wednesday",
            DayOfWeek::Thursday => "Thursday",
            DayOfWeek::Friday => "Friday",
            DayOfWeek::Saturday => "Saturday",
        };
        f.write_str(name)
    }
}

/// (Parenting) A child.
///
/// Mirrors `sealed record Child(string ChildId, string Name,
/// DateTime DateOfBirth, string? Gender)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Child {
    pub child_id: String,
    pub name: String,
    pub date_of_birth: DateTime<Utc>,
    pub gender: Option<String>,
}

impl Child {
    /// Constructs a child, mirroring the positional C# record constructor.
    pub fn new(
        child_id: impl Into<String>,
        name: impl Into<String>,
        date_of_birth: DateTime<Utc>,
        gender: Option<String>,
    ) -> Self {
        Self {
            child_id: child_id.into(),
            name: name.into(),
            date_of_birth,
            gender,
        }
    }
}

/// (Parenting) A developmental milestone.
///
/// Mirrors `sealed record Milestone(string MilestoneId, string ChildId,
/// string Category, string Description, DateTimeOffset AchievedAtUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Milestone {
    pub milestone_id: String,
    pub child_id: String,
    pub category: String,
    pub description: String,
    pub achieved_at_utc: DateTime<Utc>,
}

impl Milestone {
    /// Constructs a milestone, mirroring the positional C# record constructor.
    pub fn new(
        milestone_id: impl Into<String>,
        child_id: impl Into<String>,
        category: impl Into<String>,
        description: impl Into<String>,
        achieved_at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            milestone_id: milestone_id.into(),
            child_id: child_id.into(),
            category: category.into(),
            description: description.into(),
            achieved_at_utc,
        }
    }
}

/// (Parenting) A single routine entry.
///
/// Mirrors `sealed record RoutineEntry(string Time, string Activity)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RoutineEntry {
    pub time: String,
    pub activity: String,
}

impl RoutineEntry {
    /// Constructs a routine entry, mirroring the positional C# record constructor.
    pub fn new(time: impl Into<String>, activity: impl Into<String>) -> Self {
        Self {
            time: time.into(),
            activity: activity.into(),
        }
    }
}

/// (Parenting) A child's routine for one day of the week.
///
/// Mirrors `sealed record Routine(string ChildId, DayOfWeek DayOfWeek,
/// IReadOnlyList<RoutineEntry> Entries)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Routine {
    pub child_id: String,
    pub day_of_week: DayOfWeek,
    pub entries: Vec<RoutineEntry>,
}

impl Routine {
    /// Constructs a routine, mirroring the positional C# record constructor.
    pub fn new(child_id: impl Into<String>, day_of_week: DayOfWeek, entries: Vec<RoutineEntry>) -> Self {
        Self {
            child_id: child_id.into(),
            day_of_week,
            entries,
        }
    }
}

/// (Parenting) The parenting board contract.
///
/// Mirrors `interface IParentingBoard`.
pub trait IParentingBoard {
    /// Adds (or overwrites) a child.
    fn add_child(&self, c: Child);
    /// Looks up a child by id.
    fn get_child(&self, id: &str) -> Option<Child>;
    /// All children, ordered by name ascending.
    fn children(&self) -> Vec<Child>;
    /// Records a milestone. Panics on an empty child id.
    fn record_milestone(&self, m: Milestone);
    /// Milestones for a child, newest-first.
    fn milestones_for(&self, child_id: &str) -> Vec<Milestone>;
    /// Sets (or overwrites) the routine for a child + day.
    fn set_routine(&self, r: Routine);
    /// The routine for a child + day, if any.
    fn get_routine(&self, child_id: &str, dow: DayOfWeek) -> Option<Routine>;
    /// The child's age at `at` (`at - date_of_birth`). Panics on an unknown
    /// child id (mirrors the C# `InvalidOperationException`).
    fn age_as_of(&self, child_id: &str, at: DateTime<Utc>) -> Duration;
}

/// (Parenting) In-memory [`IParentingBoard`].
///
/// Mirrors `sealed class InMemoryParentingBoard`.
pub struct InMemoryParentingBoard {
    children: Mutex<HashMap<String, Child>>,
    milestones: Mutex<HashMap<String, Vec<Milestone>>>,
    routines: Mutex<HashMap<String, Routine>>,
}

impl InMemoryParentingBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            children: Mutex::new(HashMap::new()),
            milestones: Mutex::new(HashMap::new()),
            routines: Mutex::new(HashMap::new()),
        }
    }

    fn key(child_id: &str, d: DayOfWeek) -> String {
        format!("{child_id}/{d}")
    }
}

impl Default for InMemoryParentingBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IParentingBoard for InMemoryParentingBoard {
    fn add_child(&self, c: Child) {
        self.children.lock().unwrap().insert(c.child_id.clone(), c);
    }

    fn get_child(&self, id: &str) -> Option<Child> {
        self.children.lock().unwrap().get(id).cloned()
    }

    fn children(&self) -> Vec<Child> {
        let mut out: Vec<Child> = self.children.lock().unwrap().values().cloned().collect();
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out
    }

    fn record_milestone(&self, m: Milestone) {
        if m.child_id.trim().is_empty() {
            panic!("ChildId required");
        }
        self.milestones
            .lock()
            .unwrap()
            .entry(m.child_id.clone())
            .or_default()
            .push(m);
    }

    fn milestones_for(&self, child_id: &str) -> Vec<Milestone> {
        let milestones = self.milestones.lock().unwrap();
        let Some(list) = milestones.get(child_id) else {
            return Vec::new();
        };
        // OrderByDescending(AchievedAtUtc).
        let mut out: Vec<Milestone> = list.clone();
        out.sort_by(|a, b| b.achieved_at_utc.cmp(&a.achieved_at_utc));
        out
    }

    fn set_routine(&self, r: Routine) {
        let key = Self::key(&r.child_id, r.day_of_week);
        self.routines.lock().unwrap().insert(key, r);
    }

    fn get_routine(&self, child_id: &str, dow: DayOfWeek) -> Option<Routine> {
        self.routines.lock().unwrap().get(&Self::key(child_id, dow)).cloned()
    }

    fn age_as_of(&self, child_id: &str, at: DateTime<Utc>) -> Duration {
        let children = self.children.lock().unwrap();
        match children.get(child_id) {
            Some(c) => at - c.date_of_birth,
            None => panic!("Unknown child {child_id}"),
        }
    }
}
