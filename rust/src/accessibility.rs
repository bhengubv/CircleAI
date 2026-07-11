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
