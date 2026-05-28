//! languages — WritingSystem, LanguageTag, DetectionResult, ScriptNormalisationResult,
//! KnownLanguages static registry, and ILanguageDetector / ILanguageRegistry traits.

pub mod known_languages;
pub mod types;

pub use types::{DetectionResult, LanguageTag, ScriptNormalisationResult, WritingSystem};

// ─────────────────────────────────────────────────────────────────────────────
// KnownLanguages — thin wrapper so callers can write KnownLanguages::all(), etc.
// ─────────────────────────────────────────────────────────────────────────────

/// Static registry of every language Circle AI ships support for.
pub struct KnownLanguages;

impl KnownLanguages {
    pub fn isi_zulu() -> LanguageTag   { known_languages::isi_zulu() }
    pub fn sesotho() -> LanguageTag    { known_languages::sesotho() }
    pub fn afrikaans() -> LanguageTag  { known_languages::afrikaans() }
    pub fn swahili() -> LanguageTag    { known_languages::swahili() }
    pub fn hausa() -> LanguageTag      { known_languages::hausa() }
    pub fn amharic() -> LanguageTag    { known_languages::amharic() }
    pub fn yoruba() -> LanguageTag     { known_languages::yoruba() }
    pub fn igbo() -> LanguageTag       { known_languages::igbo() }
    pub fn xhosa() -> LanguageTag      { known_languages::xhosa() }
    pub fn sepedi() -> LanguageTag     { known_languages::sepedi() }
    pub fn setswana() -> LanguageTag   { known_languages::setswana() }
    pub fn somali() -> LanguageTag     { known_languages::somali() }
    pub fn oromo() -> LanguageTag      { known_languages::oromo() }
    pub fn arabic() -> LanguageTag     { known_languages::arabic() }
    pub fn english() -> LanguageTag    { known_languages::english() }
    pub fn portuguese() -> LanguageTag { known_languages::portuguese() }
    pub fn french() -> LanguageTag     { known_languages::french() }
    pub fn spanish() -> LanguageTag    { known_languages::spanish() }
    pub fn mandarin() -> LanguageTag   { known_languages::mandarin() }
    pub fn hindi() -> LanguageTag      { known_languages::hindi() }

    /// All languages shipped with Circle AI (20 entries, in declaration order).
    pub fn all() -> Vec<LanguageTag> {
        known_languages::all()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Traits
// ─────────────────────────────────────────────────────────────────────────────

/// Detects the BCP-47 language of a piece of text.
pub trait ILanguageDetector {
    type Error: std::error::Error;

    /// Detects the most likely language.
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
