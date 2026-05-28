//! known_languages.rs
//!
//! Static registry of all 20 languages Circle AI ships support for.
//! Entry order matches fixtures/language_tags.json exactly.

#![allow(clippy::excessive_precision)]

use super::types::{LanguageTag, WritingSystem};

// ── Africa ────────────────────────────────────────────────────────────────────

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

// ── Middle East & North Africa ────────────────────────────────────────────────

pub fn arabic() -> LanguageTag {
    LanguageTag::new("ar", "Arabic", "العربية", WritingSystem::Arabic, true, "SA")
}

// ── Europe & Americas ─────────────────────────────────────────────────────────

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

// ── Asia ──────────────────────────────────────────────────────────────────────

pub fn mandarin() -> LanguageTag {
    LanguageTag::new("zh", "Mandarin", "中文", WritingSystem::Han, false, "CN")
}

pub fn hindi() -> LanguageTag {
    LanguageTag::new("hi", "Hindi", "हिन्दी", WritingSystem::Devanagari, false, "IN")
}

/// All 20 languages in fixture declaration order.
pub fn all() -> Vec<LanguageTag> {
    vec![
        isi_zulu(),
        sesotho(),
        afrikaans(),
        swahili(),
        hausa(),
        amharic(),
        yoruba(),
        igbo(),
        xhosa(),
        sepedi(),
        setswana(),
        somali(),
        oromo(),
        arabic(),
        english(),
        portuguese(),
        french(),
        spanish(),
        mandarin(),
        hindi(),
    ]
}
