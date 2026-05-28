//! goal.rs
//!
//! Goal tracking — user goals B! tracks and proactively helps with.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

/// Lifecycle state of a [`Goal`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum GoalStatus {
    /// Goal is currently being pursued.
    Active,
    /// Goal has been achieved.
    Completed,
    /// Goal has been abandoned without completion.
    Abandoned,
}

/// Relative importance of a [`Goal`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum GoalPriority {
    /// Nice-to-have; may be deferred.
    Low,
    /// Standard importance.
    Normal,
    /// Urgent or critical to the user.
    High,
}

/// A user goal that B! tracks and proactively helps with.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Goal {
    /// Unique stable identifier for this goal.
    pub id: String,
    /// Owner of this goal.
    pub user_id: String,
    /// Short, human-readable title.
    pub title: String,
    /// Full description of what the user wants to achieve.
    pub description: String,
    /// Current lifecycle state.
    pub status: GoalStatus,
    /// Relative importance.
    pub priority: GoalPriority,
    /// When this goal was first recorded (UTC).
    pub created_utc: DateTime<Utc>,
    /// Optional deadline (UTC).
    pub due_utc: Option<DateTime<Utc>>,
    /// When the goal was completed or abandoned (UTC).
    pub completed_utc: Option<DateTime<Utc>>,
    /// Freeform notes B! or the user has attached to this goal.
    pub notes: Option<String>,
    /// Progress toward completion: 0.0..=1.0.
    pub progress: f32,
}

impl Goal {
    pub fn new(
        id: impl Into<String>,
        user_id: impl Into<String>,
        title: impl Into<String>,
        description: impl Into<String>,
        priority: GoalPriority,
    ) -> Self {
        Self {
            id: id.into(),
            user_id: user_id.into(),
            title: title.into(),
            description: description.into(),
            status: GoalStatus::Active,
            priority,
            created_utc: Utc::now(),
            due_utc: None,
            completed_utc: None,
            notes: None,
            progress: 0.0,
        }
    }

    /// Returns a new `Goal` with `progress` advanced by `delta`, clamped to [0.0, 1.0].
    ///
    /// Does not mutate `self`; returns a clone with the new progress value.
    pub fn advance_progress(&self, delta: f32) -> Self {
        let mut g = self.clone();
        g.progress = (g.progress + delta).clamp(0.0, 1.0);
        g
    }
}
