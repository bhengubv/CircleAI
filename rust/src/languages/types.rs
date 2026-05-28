//! types.rs
//!
//! WritingSystem, LanguageTag, DetectionResult, ScriptNormalisationResult.

use serde::{Deserialize, Serialize};

// ─────────────────────────────────────────────────────────────────────────────
// WritingSystem
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum WritingSystem {
    Latin,
    Arabic,
    Ethiopic,
    Geez,
    Devanagari,
    Han,
    Cyrillic,
    Hebrew,
    Greek,
    Other,
}

// ─────────────────────────────────────────────────────────────────────────────
// LanguageTag
// ─────────────────────────────────────────────────────────────────────────────

/// A BCP-47 language tag enriched with display metadata.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LanguageTag {
    pub bcp_tag: String,
    pub english_name: String,
    pub native_name: String,
    pub writing_system: WritingSystem,
    pub is_rtl: bool,
    pub primary_region: String,
}

impl LanguageTag {
    pub fn new(
        bcp_tag: impl Into<String>,
        english_name: impl Into<String>,
        native_name: impl Into<String>,
        writing_system: WritingSystem,
        is_rtl: bool,
        primary_region: impl Into<String>,
    ) -> Self {
        Self {
            bcp_tag: bcp_tag.into(),
            english_name: english_name.into(),
            native_name: native_name.into(),
            writing_system,
            is_rtl,
            primary_region: primary_region.into(),
        }
    }

    /// Sentinel value for unknown language.
    pub fn unknown() -> Self {
        Self::new("und", "Unknown", "Unknown", WritingSystem::Latin, false, "")
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DetectionResult
// ─────────────────────────────────────────────────────────────────────────────

/// Result of language detection.
#[derive(Debug, Clone)]
pub struct DetectionResult {
    pub language: LanguageTag,
    /// 0.0–1.0 confidence score.
    pub confidence: f32,
    pub is_reliable: bool,
}

impl DetectionResult {
    pub fn new(language: LanguageTag, confidence: f32, is_reliable: bool) -> Self {
        Self {
            language,
            confidence,
            is_reliable,
        }
    }

    /// Returns a failed detection with `LanguageTag::unknown()` and confidence 0.
    pub fn unknown() -> Self {
        Self::new(LanguageTag::unknown(), 0.0, false)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ScriptNormalisationResult
// ─────────────────────────────────────────────────────────────────────────────

/// Result of script normalisation.
#[derive(Debug, Clone)]
pub struct ScriptNormalisationResult {
    pub input: String,
    pub normalised: String,
    pub detected_language: LanguageTag,
}
