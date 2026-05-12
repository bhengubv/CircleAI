//! language_registry_test.rs
//!
//! Tests for KnownLanguages: 20 entries, per-entry field match from fixture.

use circle_ai::languages::{KnownLanguages, WritingSystem};
use serde::Deserialize;

// ─────────────────────────────────────────────────────────────────────────────
// Fixture deserialization helpers
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct LanguageFixture {
    bcp_tag: String,
    english_name: String,
    native_name: String,
    writing_system: String,
    is_rtl: bool,
    primary_region: String,
}

#[derive(Debug, Deserialize)]
struct Fixture {
    languages: Vec<LanguageFixture>,
}

fn load_fixture() -> Fixture {
    let fixtures_dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("fixtures");
    let path = fixtures_dir.join("language_tags.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("Failed to read {:?}: {}", path, e));
    serde_json::from_str(&text).expect("Failed to parse language_tags.json")
}

fn writing_system_from_str(s: &str) -> WritingSystem {
    match s {
        "Latin" => WritingSystem::Latin,
        "Arabic" => WritingSystem::Arabic,
        "Ethiopic" => WritingSystem::Ethiopic,
        "Geez" => WritingSystem::Geez,
        "Devanagari" => WritingSystem::Devanagari,
        "Han" => WritingSystem::Han,
        "Cyrillic" => WritingSystem::Cyrillic,
        "Hebrew" => WritingSystem::Hebrew,
        "Greek" => WritingSystem::Greek,
        other => panic!("Unknown writing system in fixture: {}", other),
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_all_count_is_20() {
    assert_eq!(KnownLanguages::all().len(), 20);
}

#[test]
fn test_all_entries_match_fixture() {
    let fixture = load_fixture();
    let all = KnownLanguages::all();

    assert_eq!(
        all.len(),
        fixture.languages.len(),
        "KnownLanguages::all() length {} != fixture length {}",
        all.len(),
        fixture.languages.len()
    );

    for (i, (lang, fix)) in all.iter().zip(fixture.languages.iter()).enumerate() {
        assert_eq!(
            lang.bcp_tag, fix.bcp_tag,
            "[{}] bcp_tag mismatch",
            i
        );
        assert_eq!(
            lang.english_name, fix.english_name,
            "[{}] english_name mismatch for {}",
            i, fix.bcp_tag
        );
        assert_eq!(
            lang.native_name, fix.native_name,
            "[{}] native_name mismatch for {}",
            i, fix.bcp_tag
        );
        assert_eq!(
            lang.writing_system,
            writing_system_from_str(&fix.writing_system),
            "[{}] writing_system mismatch for {}",
            i,
            fix.bcp_tag
        );
        assert_eq!(
            lang.is_rtl, fix.is_rtl,
            "[{}] is_rtl mismatch for {}",
            i, fix.bcp_tag
        );
        assert_eq!(
            lang.primary_region, fix.primary_region,
            "[{}] primary_region mismatch for {}",
            i, fix.bcp_tag
        );
    }
}

#[test]
fn test_arabic_is_rtl() {
    let ar = KnownLanguages::arabic();
    assert!(ar.is_rtl, "Arabic must be RTL");
    assert_eq!(ar.bcp_tag, "ar");
    assert_eq!(ar.writing_system, WritingSystem::Arabic);
}

#[test]
fn test_no_other_rtl_language() {
    let rtl_count = KnownLanguages::all().iter().filter(|l| l.is_rtl).count();
    assert_eq!(rtl_count, 1, "Exactly one RTL language (Arabic) expected");
}

#[test]
fn test_amharic_writing_system() {
    let am = KnownLanguages::amharic();
    assert_eq!(am.writing_system, WritingSystem::Ethiopic);
    assert_eq!(am.bcp_tag, "am");
    assert_eq!(am.primary_region, "ET");
}

#[test]
fn test_mandarin_writing_system() {
    let zh = KnownLanguages::mandarin();
    assert_eq!(zh.writing_system, WritingSystem::Han);
    assert_eq!(zh.bcp_tag, "zh");
    assert_eq!(zh.primary_region, "CN");
}

#[test]
fn test_hindi_writing_system() {
    let hi = KnownLanguages::hindi();
    assert_eq!(hi.writing_system, WritingSystem::Devanagari);
    assert_eq!(hi.bcp_tag, "hi");
    assert_eq!(hi.primary_region, "IN");
}

#[test]
fn test_african_language_count() {
    // IsiZulu, Sesotho, Afrikaans, Swahili, Hausa, Amharic, Yoruba, Igbo, Xhosa,
    // Sepedi, Setswana, Somali, Oromo = 13
    let african_regions = ["ZA", "KE", "NG", "ET", "SO"];
    let count = KnownLanguages::all()
        .iter()
        .filter(|l| african_regions.contains(&l.primary_region.as_str()))
        .count();
    assert_eq!(count, 13, "Expected 13 African languages");
}

#[test]
fn test_bcp_tags_are_unique() {
    let all = KnownLanguages::all();
    let mut seen = std::collections::HashSet::new();
    for lang in &all {
        assert!(
            seen.insert(&lang.bcp_tag),
            "Duplicate BCP tag: {}",
            lang.bcp_tag
        );
    }
}

#[test]
fn test_isi_zulu_fields() {
    let zu = KnownLanguages::isi_zulu();
    assert_eq!(zu.bcp_tag, "zu");
    assert_eq!(zu.english_name, "isiZulu");
    assert_eq!(zu.native_name, "isiZulu");
    assert_eq!(zu.writing_system, WritingSystem::Latin);
    assert!(!zu.is_rtl);
    assert_eq!(zu.primary_region, "ZA");
}

#[test]
fn test_declaration_order() {
    let all = KnownLanguages::all();
    // First entry must be IsiZulu, last must be Hindi
    assert_eq!(all[0].bcp_tag, "zu");
    assert_eq!(all[19].bcp_tag, "hi");
    // Arabic must be at index 13
    assert_eq!(all[13].bcp_tag, "ar");
}
