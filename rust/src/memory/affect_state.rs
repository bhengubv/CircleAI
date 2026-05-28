//! affect_state.rs
//!
//! AffectState — B!'s current emotional/engagement state ("HER affect layer").
//! Five float dimensions, all 0.0–1.0.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

/// B!'s current emotional/engagement state — the "HER affect layer".
///
/// Five float dimensions, all 0.0–1.0. Persisted per-user and injected
/// into the system prompt to shape response tone and initiative.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AffectState {
    /// Opaque user identifier (device ID or hashed phone number).
    /// Never contains PII in plaintext.
    pub user_id: String,

    /// UTC time of the last update to this affect state.
    pub last_updated_at: DateTime<Utc>,

    /// 0 = bored, 1 = fascinated. Drives proactive questions.
    pub curiosity: f32,

    /// 0 = disengaged, 1 = fully engaged. Rises with frequent quality interactions.
    pub engagement: f32,

    /// 0 = confident, 1 = confused. High → ask clarifying questions.
    pub uncertainty: f32,

    /// 0 = stranger, 1 = deep rapport. Grows slowly over many sessions.
    pub rapport: f32,

    /// 0 = subdued, 1 = energetic. Mirrors time-of-day and interaction pace.
    pub energy: f32,
}

impl Default for AffectState {
    fn default() -> Self {
        Self {
            user_id: "default".to_string(),
            last_updated_at: Utc::now(),
            curiosity: 0.5,
            engagement: 0.5,
            uncertainty: 0.2,
            rapport: 0.0,
            energy: 0.5,
        }
    }
}

impl AffectState {
    /// Create a new default `AffectState` for the given user.
    pub fn new(user_id: impl Into<String>) -> Self {
        Self {
            user_id: user_id.into(),
            ..Default::default()
        }
    }

    /// Apply a positive interaction: nudge `engagement` and `rapport` up, `uncertainty` down.
    ///
    /// Deltas: engagement += 0.02, rapport += 0.01, uncertainty -= 0.02
    pub fn apply_positive_signal(&mut self) {
        self.engagement = (self.engagement + 0.02).clamp(0.0, 1.0);
        self.rapport = (self.rapport + 0.01).clamp(0.0, 1.0);
        self.uncertainty = (self.uncertainty - 0.02).clamp(0.0, 1.0);
        self.last_updated_at = Utc::now();
    }

    /// Apply a negative interaction: nudge `engagement` down, `uncertainty` up.
    ///
    /// Deltas: engagement -= 0.03, uncertainty += 0.03
    pub fn apply_negative_signal(&mut self) {
        self.engagement = (self.engagement - 0.03).clamp(0.0, 1.0);
        self.uncertainty = (self.uncertainty + 0.03).clamp(0.0, 1.0);
        self.last_updated_at = Utc::now();
    }

    /// Apply idle time decay: `engagement` and `energy` drift back toward 0.5.
    ///
    /// decay = min(0.3, hours * 0.02)
    /// lerp(a, b, t) = a + (b - a) * t.clamp(0, 1)
    pub fn apply_idle_decay(&mut self, idle_hours: f32) {
        let decay = (idle_hours * 0.02_f32).min(0.3_f32);
        self.engagement = Self::lerp(self.engagement, 0.5, decay);
        self.energy = Self::lerp(self.energy, 0.5, decay);
        self.last_updated_at = Utc::now();
    }

    fn lerp(a: f32, b: f32, t: f32) -> f32 {
        a + (b - a) * t.clamp(0.0, 1.0)
    }

    /// Builds a compact affect hint for injection into the system prompt.
    ///
    /// Only emits lines that deviate meaningfully from neutral (0.5).
    pub fn to_system_prompt_hint(&self) -> String {
        let mut hints: Vec<&str> = Vec::new();

        if self.curiosity > 0.7 {
            hints.push("You are deeply curious about this topic — ask a follow-up question.");
        }
        if self.engagement > 0.7 {
            hints.push("You are fully engaged — be enthusiastic and thorough.");
        }
        if self.engagement < 0.3 {
            hints.push("Keep your response brief and to the point.");
        }
        if self.uncertainty > 0.6 {
            hints.push("You are uncertain — ask a clarifying question before answering.");
        }
        if self.rapport > 0.7 {
            hints.push("You know this user well — use a warm, familiar tone.");
        }
        if self.energy < 0.3 {
            hints.push("Keep your response calm and measured.");
        }
        if self.energy > 0.8 {
            hints.push("You are energetic — be upbeat and concise.");
        }

        if hints.is_empty() {
            return String::new();
        }
        format!("[Affect state]\n{}\n", hints.join("\n"))
    }
}
