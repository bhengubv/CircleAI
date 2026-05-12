//! languages.rs
//!
//! WritingSystem, LanguageTag, DetectionResult, ScriptNormalisationResult,
//! KnownLanguages static registry, and ILanguageDetector / ILanguageRegistry traits.

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

// ─────────────────────────────────────────────────────────────────────────────
// KnownLanguages
// ─────────────────────────────────────────────────────────────────────────────

/// Static registry of every language Circle AI ships support for.
pub struct KnownLanguages;

impl KnownLanguages {
    // ── Africa ────────────────────────────────────────────────────────────────

    pub fn isi_zulu() -> LanguageTag {
        LanguageTag::new("zu", "isiZulu", "isiZulu", WritingSystem::Latin, false, "ZA")
    }

    pub fn sesotho() -> LanguageTag {
        LanguageTag::new("st", "Sesotho", "Sesotho", WritingSystem::Latin, false, "ZA")
    }

    pub fn afrikaans() -> LanguageTag {
        LanguageTag::new("af", "Afrikaans", "Afrikaans", WritingSystem::Latin, false, "ZA")
    }

    pub fn swahili() -> LanguageTag {
        LanguageTag::new("sw", "Swahili", "Kiswahili", WritingSystem::Latin, false, "KE")
    }

    pub fn hausa() -> LanguageTag {
        LanguageTag::new("ha", "Hausa", "Hausa", WritingSystem::Latin, false, "NG")
    }

    pub fn amharic() -> LanguageTag {
        LanguageTag::new("am", "Amharic", "አማርኛ", WritingSystem::Ethiopic, false, "ET")
    }

    pub fn yoruba() -> LanguageTag {
        LanguageTag::new("yo", "Yoruba", "Yorùbá", WritingSystem::Latin, false, "NG")
    }

    pub fn igbo() -> LanguageTag {
        LanguageTag::new("ig", "Igbo", "Igbo", WritingSystem::Latin, false, "NG")
    }

    pub fn xhosa() -> LanguageTag {
        LanguageTag::new("xh", "isiXhosa", "isiXhosa", WritingSystem::Latin, false, "ZA")
    }

    pub fn sepedi() -> LanguageTag {
        LanguageTag::new("nso", "Sepedi", "Sepedi", WritingSystem::Latin, false, "ZA")
    }

    pub fn setswana() -> LanguageTag {
        LanguageTag::new("tn", "Setswana", "Setswana", WritingSystem::Latin, false, "ZA")
    }

    pub fn somali() -> LanguageTag {
        LanguageTag::new("so", "Somali", "Soomaali", WritingSystem::Latin, false, "SO")
    }

    pub fn oromo() -> LanguageTag {
        LanguageTag::new("om", "Oromo", "Afaan Oromoo", WritingSystem::Latin, false, "ET")
    }

    // ── Middle East & North Africa ────────────────────────────────────────────

    pub fn arabic() -> LanguageTag {
        LanguageTag::new("ar", "Arabic", "العربية", WritingSystem::Arabic, true, "SA")
    }

    // ── Europe & Americas ─────────────────────────────────────────────────────

    pub fn english() -> LanguageTag {
        LanguageTag::new("en", "English", "English", WritingSystem::Latin, false, "GB")
    }

    pub fn portuguese() -> LanguageTag {
        LanguageTag::new("pt", "Portuguese", "Português", WritingSystem::Latin, false, "PT")
    }

    pub fn french() -> LanguageTag {
        LanguageTag::new("fr", "French", "Français", WritingSystem::Latin, false, "FR")
    }

    pub fn spanish() -> LanguageTag {
        LanguageTag::new("es", "Spanish", "Español", WritingSystem::Latin, false, "ES")
    }

    // ── Asia ──────────────────────────────────────────────────────────────────

    pub fn mandarin() -> LanguageTag {
        LanguageTag::new("zh", "Mandarin", "中文", WritingSystem::Han, false, "CN")
    }

    pub fn hindi() -> LanguageTag {
        LanguageTag::new("hi", "Hindi", "हिन्दी", WritingSystem::Devanagari, false, "IN")
    }

    /// All languages shipped with Circle AI (20 entries, in declaration order).
    pub fn all() -> Vec<LanguageTag> {
        vec![
            Self::isi_zulu(),
            Self::sesotho(),
            Self::afrikaans(),
            Self::swahili(),
            Self::hausa(),
            Self::amharic(),
            Self::yoruba(),
            Self::igbo(),
            Self::xhosa(),
            Self::sepedi(),
            Self::setswana(),
            Self::somali(),
            Self::oromo(),
            Self::arabic(),
            Self::english(),
            Self::portuguese(),
            Self::french(),
            Self::spanish(),
            Self::mandarin(),
            Self::hindi(),
        ]
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Traits
// ─────────────────────────────────────────────────────────────────────────────

/// Detects the BCP-47 language of a piece of text.
pub trait ILanguageDetector {
    type Error: std::error::Error;

    /// Detects the most likely language.
    /// Returns `LanguageTag::unknown()` with confidence 0 when detection fails.
    fn detect(&self, text: &str) -> Result<DetectionResult, Self::Error>;

    /// Returns up to `max_results` candidates ranked by confidence.
    fn detect_multiple(
        &self,
        text: &str,
        max_results: usize,
    ) -> Result<Vec<DetectionResult>, Self::Error>;
}

/// Registry of all BCP-47 language tags that Circle AI understands.
pub trait ILanguageRegistry {
    fn get_by_bcp_tag(&self, bcp_tag: &str) -> Option<LanguageTag>;
    fn get_all(&self) -> Vec<LanguageTag>;
    fn get_for_region(&self, iso_region: &str) -> Vec<LanguageTag>;
    fn is_supported(&self, bcp_tag: &str) -> bool;
}
