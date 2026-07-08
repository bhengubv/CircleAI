//! capability_registry_test.rs
//!
//! Verifies ExternalCapabilityRegistry: the full absorption catalogue, id lookup,
//! and by-package filtering. Mirrors the C# ExternalCapabilityRegistry.

use circle_ai::companion::capability_registry::ExternalCapabilityRegistry;

#[test]
fn all_has_the_full_catalogue() {
    let all = ExternalCapabilityRegistry::all();
    // The C# registry has 30 entries.
    assert_eq!(all.len(), 30);
    // Every entry has a non-empty id, license, strategy, target package, and at
    // least one value bullet.
    for c in &all {
        assert!(!c.id.is_empty());
        assert!(!c.license.is_empty());
        assert!(!c.strategy.is_empty());
        assert!(!c.target_package.is_empty());
        assert!(!c.value_bullets.is_empty());
    }
}

#[test]
fn find_is_case_insensitive() {
    let hit = ExternalCapabilityRegistry::find("HippoRAG").unwrap();
    assert_eq!(hit.id, "HippoRAG");
    assert_eq!(hit.repo.as_deref(), Some("OSU-NLP-Group/HippoRAG"));
    assert_eq!(hit.target_package, "CircleAI.Memory.HippoRAG");
    // Different casing still matches.
    assert!(ExternalCapabilityRegistry::find("hipporag").is_some());
    assert!(ExternalCapabilityRegistry::find("no-such-cap").is_none());
}

#[test]
fn by_package_filters_correctly() {
    // Two capabilities target CircleAI.Speech (Amphion + yapsnap).
    let speech = ExternalCapabilityRegistry::by_package("CircleAI.Speech");
    assert_eq!(speech.len(), 2);
    let ids: Vec<&str> = speech.iter().map(|c| c.id.as_str()).collect();
    assert!(ids.contains(&"Amphion"));
    assert!(ids.contains(&"yapsnap"));
    // Two target CircleAI.Inference (airllm + shard).
    let inference = ExternalCapabilityRegistry::by_package("CircleAI.Inference");
    assert_eq!(inference.len(), 2);
    // Case-insensitive package match.
    assert_eq!(
        ExternalCapabilityRegistry::by_package("circleai.speech").len(),
        2
    );
    // Unknown package → empty.
    assert!(ExternalCapabilityRegistry::by_package("CircleAI.Nope").is_empty());
}

#[test]
fn claude_mem_entry_is_intact() {
    let c = ExternalCapabilityRegistry::find("claude-mem").unwrap();
    assert_eq!(c.license, "MIT");
    assert_eq!(c.strategy, "pattern-port");
    assert_eq!(c.target_package, "CircleAI.Memory");
    assert_eq!(c.value_bullets.len(), 10);
    assert_eq!(c.value_bullets[0], "Multi-platform memory adapter");
}
