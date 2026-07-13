//! accessibility — CircleAI accessibility-board primitives.
//!
//! Full Rust port of `src/CircleAI.Accessibility/AccessibilityPrimitives.cs`:
//!
//! - [`AccessibilityNeed`] enum, records [`UserAccessibilityProfile`] /
//!   [`AdaptationHint`], the [`IAccessibilityBoard`] contract, and the
//!   deterministic in-memory [`InMemoryAccessibilityBoard`] (profile store +
//!   adaptation-hint derivation).
//!
//! Sync-only. Hint order and formatting mirror the C# exactly: contrast →
//! motion → aria → text-scale (`"F2"`, i.e. two decimals) → one `need` hint per
//! declared need (using the C# enum member name).

use std::collections::HashMap;
use std::sync::Mutex;

/// (Accessibility) A category of accessibility need.
///
/// Mirrors `enum AccessibilityNeed { Visual, Hearing, Motor, Cognitive, Speech }`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AccessibilityNeed {
    Visual,
    Hearing,
    Motor,
    Cognitive,
    Speech,
}

impl AccessibilityNeed {
    /// The C# enum member name (used verbatim in the `need` adaptation hint).
    pub fn name(&self) -> &'static str {
        match self {
            AccessibilityNeed::Visual => "Visual",
            AccessibilityNeed::Hearing => "Hearing",
            AccessibilityNeed::Motor => "Motor",
            AccessibilityNeed::Cognitive => "Cognitive",
            AccessibilityNeed::Speech => "Speech",
        }
    }
}

/// (Accessibility) A user's accessibility profile.
///
/// Mirrors `sealed record UserAccessibilityProfile(string UserId,
/// IReadOnlyList<AccessibilityNeed> Needs, double TextScale, bool HighContrast,
/// bool ReducedMotion, bool ScreenReader)`.
#[derive(Debug, Clone, PartialEq)]
pub struct UserAccessibilityProfile {
    pub user_id: String,
    pub needs: Vec<AccessibilityNeed>,
    pub text_scale: f64,
    pub high_contrast: bool,
    pub reduced_motion: bool,
    pub screen_reader: bool,
}

impl UserAccessibilityProfile {
    /// Constructs a profile, mirroring the positional C# record constructor.
    pub fn new(
        user_id: impl Into<String>,
        needs: Vec<AccessibilityNeed>,
        text_scale: f64,
        high_contrast: bool,
        reduced_motion: bool,
        screen_reader: bool,
    ) -> Self {
        Self {
            user_id: user_id.into(),
            needs,
            text_scale,
            high_contrast,
            reduced_motion,
            screen_reader,
        }
    }
}

/// (Accessibility) A single UI adaptation hint.
///
/// Mirrors `sealed record AdaptationHint(string Kind, string Value)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AdaptationHint {
    pub kind: String,
    pub value: String,
}

impl AdaptationHint {
    /// Constructs a hint, mirroring the positional C# record constructor.
    pub fn new(kind: impl Into<String>, value: impl Into<String>) -> Self {
        Self {
            kind: kind.into(),
            value: value.into(),
        }
    }
}

/// (Accessibility) The accessibility-board contract.
///
/// Mirrors `interface IAccessibilityBoard`.
pub trait IAccessibilityBoard {
    /// Sets (or overwrites) a user's profile.
    fn set_profile(&self, p: UserAccessibilityProfile);
    /// A user's profile, if any.
    fn get_profile(&self, user_id: &str) -> Option<UserAccessibilityProfile>;
    /// Derives UI adaptation hints from a user's profile. Empty when the user has
    /// no profile.
    fn hints_for(&self, user_id: &str) -> Vec<AdaptationHint>;
}

/// (Accessibility) In-memory [`IAccessibilityBoard`].
///
/// Mirrors `sealed class InMemoryAccessibilityBoard`.
pub struct InMemoryAccessibilityBoard {
    profiles: Mutex<HashMap<String, UserAccessibilityProfile>>,
}

impl InMemoryAccessibilityBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            profiles: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryAccessibilityBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IAccessibilityBoard for InMemoryAccessibilityBoard {
    fn set_profile(&self, p: UserAccessibilityProfile) {
        self.profiles.lock().unwrap().insert(p.user_id.clone(), p);
    }

    fn get_profile(&self, user_id: &str) -> Option<UserAccessibilityProfile> {
        self.profiles.lock().unwrap().get(user_id).cloned()
    }

    fn hints_for(&self, user_id: &str) -> Vec<AdaptationHint> {
        let profiles = self.profiles.lock().unwrap();
        let p = match profiles.get(user_id) {
            Some(p) => p.clone(),
            None => return Vec::new(),
        };
        drop(profiles);
        let mut hints: Vec<AdaptationHint> = Vec::new();
        if p.high_contrast {
            hints.push(AdaptationHint::new("contrast", "high"));
        }
        if p.reduced_motion {
            hints.push(AdaptationHint::new("motion", "reduced"));
        }
        if p.screen_reader {
            hints.push(AdaptationHint::new("aria", "verbose"));
        }
        if p.text_scale > 1.0 {
            // C# TextScale.ToString("F2") — fixed-point, two decimals.
            hints.push(AdaptationHint::new("text-scale", format!("{:.2}", p.text_scale)));
        }
        for n in &p.needs {
            hints.push(AdaptationHint::new("need", n.name()));
        }
        hints
    }
}

/// Default `threshold` for [`InMemoryAccessibilityBoard::needs_large_text`]
/// (C# `threshold = 1.3`).
pub const DEFAULT_LARGE_TEXT_THRESHOLD: f64 = 1.3;

/// StubGuard parity additions — concrete-only helpers on the in-memory board
/// (mirroring the C# members added to `InMemoryAccessibilityBoard`/`IAccessibilityBoard`).
impl InMemoryAccessibilityBoard {
    /// Number of stored profiles. Mirrors `Count`.
    pub fn count(&self) -> usize {
        self.profiles.lock().unwrap().len()
    }

    /// Removes a user's profile. Returns `true` if present. Mirrors `Remove`.
    pub fn remove(&self, user_id: &str) -> bool {
        self.profiles.lock().unwrap().remove(user_id).is_some()
    }

    /// Profiles declaring `need`, ordered by user id (case-insensitive). Mirrors
    /// `WithNeed`.
    pub fn with_need(&self, need: AccessibilityNeed) -> Vec<UserAccessibilityProfile> {
        let mut hits: Vec<UserAccessibilityProfile> = self
            .profiles
            .lock()
            .unwrap()
            .values()
            .filter(|p| p.needs.contains(&need))
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.user_id.to_lowercase().cmp(&b.user_id.to_lowercase()));
        hits
    }

    /// Profiles with a screen reader enabled, ordered by user id (case-insensitive).
    /// Mirrors `ScreenReaderUsers`.
    pub fn screen_reader_users(&self) -> Vec<UserAccessibilityProfile> {
        let mut hits: Vec<UserAccessibilityProfile> = self
            .profiles
            .lock()
            .unwrap()
            .values()
            .filter(|p| p.screen_reader)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.user_id.to_lowercase().cmp(&b.user_id.to_lowercase()));
        hits
    }

    /// Average text scale across all profiles; `1.0` when there are none (the C#
    /// `DefaultIfEmpty(1.0).Average()`). Mirrors `AverageTextScale`.
    pub fn average_text_scale(&self) -> f64 {
        let profiles = self.profiles.lock().unwrap();
        let scales: Vec<f64> = profiles.values().map(|p| p.text_scale).collect();
        if scales.is_empty() {
            1.0
        } else {
            scales.iter().sum::<f64>() / scales.len() as f64
        }
    }

    /// Whether a user's text scale is at or above `threshold` (see
    /// [`DEFAULT_LARGE_TEXT_THRESHOLD`]). `false` for an unknown user. Mirrors
    /// `NeedsLargeText`.
    pub fn needs_large_text(&self, user_id: &str, threshold: f64) -> bool {
        self.profiles
            .lock()
            .unwrap()
            .get(user_id)
            .is_some_and(|p| p.text_scale >= threshold)
    }
}
