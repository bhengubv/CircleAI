//! identity_test.rs
//!
//! Enum variant checks, struct construction, and fixture example checks
//! for CircleIdentity, RegisteredDevice, and IdentityTier.

use circle_ai::identity::{CircleIdentity, IdentityTier, RegisteredDevice};
use chrono::DateTime;
use serde::Deserialize;

// ─────────────────────────────────────────────────────────────────────────────
// Fixture deserialization helpers
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct IdentityFixture {
    identity_id: String,
    display_name: String,
    preferred_language: Option<String>,
    tier: String,
    device_ids: Vec<String>,
    created_at: String,
    last_seen_at: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DeviceFixture {
    device_id: String,
    identity_id: String,
    platform: String,
    device_name: Option<String>,
    registered_at: String,
    last_active_at: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ExampleFixture {
    id: String,
    identity: IdentityFixture,
    devices: Vec<DeviceFixture>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Fixture {
    identity_tiers: Vec<String>,
    platforms: Vec<String>,
    examples: Vec<ExampleFixture>,
}

fn load_fixture() -> Fixture {
    let fixtures_dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("fixtures");
    let path = fixtures_dir.join("identity.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("Failed to read {:?}: {}", path, e));
    serde_json::from_str(&text).expect("Failed to parse identity.json")
}

fn tier_from_str(s: &str) -> IdentityTier {
    match s {
        "Anonymous" => IdentityTier::Anonymous,
        "Pseudonymous" => IdentityTier::Pseudonymous,
        "Verified" => IdentityTier::Verified,
        other => panic!("Unknown tier: {}", other),
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Enum tests
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_identity_tier_variants() {
    // All three variants exist and are distinct
    let tiers = [
        IdentityTier::Anonymous,
        IdentityTier::Pseudonymous,
        IdentityTier::Verified,
    ];
    assert_eq!(tiers[0], IdentityTier::Anonymous);
    assert_eq!(tiers[1], IdentityTier::Pseudonymous);
    assert_eq!(tiers[2], IdentityTier::Verified);
    assert_ne!(tiers[0], tiers[1]);
    assert_ne!(tiers[1], tiers[2]);
}

#[test]
fn test_identity_tier_ordering() {
    // Tier order: Anonymous < Pseudonymous < Verified
    assert!(IdentityTier::Anonymous < IdentityTier::Pseudonymous);
    assert!(IdentityTier::Pseudonymous < IdentityTier::Verified);
    assert!(IdentityTier::Anonymous < IdentityTier::Verified);
}

#[test]
fn test_fixture_tier_order() {
    let fixture = load_fixture();
    assert_eq!(fixture.identity_tiers, vec!["Anonymous", "Pseudonymous", "Verified"]);
}

// ─────────────────────────────────────────────────────────────────────────────
// Fixture example checks
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_fixture_has_three_examples() {
    let fixture = load_fixture();
    assert_eq!(fixture.examples.len(), 3);
}

#[test]
fn test_verified_multi_device_example() {
    let fixture = load_fixture();
    let example = fixture
        .examples
        .iter()
        .find(|e| e.id == "verified_multi_device")
        .expect("verified_multi_device example missing");

    let id_fix = &example.identity;
    assert_eq!(id_fix.identity_id, "a1b2c3d4-e5f6-7890-abcd-ef1234567890");
    assert_eq!(id_fix.display_name, "Sipho Dlamini");
    assert_eq!(id_fix.preferred_language.as_deref(), Some("zu"));
    assert_eq!(id_fix.tier, "Verified");
    assert_eq!(id_fix.device_ids.len(), 3);

    assert_eq!(example.devices.len(), 3);
    assert_eq!(example.devices[0].platform, "android");
    assert_eq!(example.devices[1].platform, "watch");
    assert_eq!(example.devices[2].platform, "windows");
}

#[test]
fn test_pseudonymous_single_device_example() {
    let fixture = load_fixture();
    let example = fixture
        .examples
        .iter()
        .find(|e| e.id == "pseudonymous_single_device")
        .expect("pseudonymous_single_device example missing");

    assert_eq!(example.identity.tier, "Pseudonymous");
    assert_eq!(example.devices.len(), 1);
    assert_eq!(example.devices[0].platform, "ios");
}

#[test]
fn test_anonymous_iot_example() {
    let fixture = load_fixture();
    let example = fixture
        .examples
        .iter()
        .find(|e| e.id == "anonymous_iot")
        .expect("anonymous_iot example missing");

    assert_eq!(example.identity.tier, "Anonymous");
    assert_eq!(example.identity.display_name, "Guest");
    assert!(example.identity.preferred_language.is_none());
    assert_eq!(example.devices.len(), 1);
    assert_eq!(example.devices[0].platform, "iot");
    assert!(example.devices[0].device_name.is_none());
}

// ─────────────────────────────────────────────────────────────────────────────
// Struct construction tests
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_circle_identity_construction() {
    let now = chrono::Utc::now();
    let identity = CircleIdentity::new(
        "test-id-001",
        "Test User",
        Some("en".to_string()),
        IdentityTier::Pseudonymous,
        vec!["device-001".to_string()],
        now,
        now,
    );

    assert_eq!(identity.identity_id, "test-id-001");
    assert_eq!(identity.display_name, "Test User");
    assert_eq!(identity.preferred_language.as_deref(), Some("en"));
    assert_eq!(identity.tier, IdentityTier::Pseudonymous);
    assert_eq!(identity.device_ids.len(), 1);
}

#[test]
fn test_registered_device_construction() {
    let now = chrono::Utc::now();
    let device = RegisteredDevice::new(
        "device-001",
        "identity-001",
        "android",
        Some("My Phone".to_string()),
        now,
        now,
    );

    assert_eq!(device.device_id, "device-001");
    assert_eq!(device.identity_id, "identity-001");
    assert_eq!(device.platform, "android");
    assert_eq!(device.device_name.as_deref(), Some("My Phone"));
}

#[test]
fn test_fixture_platforms() {
    let fixture = load_fixture();
    let expected_platforms = vec![
        "android", "ios", "windows", "macos", "linux", "web", "watch", "iot",
    ];
    assert_eq!(fixture.platforms, expected_platforms);
}

#[test]
fn test_all_fixture_examples_build_structs() {
    let fixture = load_fixture();
    for example in &fixture.examples {
        let id_fix = &example.identity;
        let tier = tier_from_str(&id_fix.tier);

        let created = DateTime::parse_from_rfc3339(&id_fix.created_at)
            .expect("invalid created_at")
            .with_timezone(&chrono::Utc);
        let last_seen = DateTime::parse_from_rfc3339(&id_fix.last_seen_at)
            .expect("invalid last_seen_at")
            .with_timezone(&chrono::Utc);

        let identity = CircleIdentity::new(
            &id_fix.identity_id,
            &id_fix.display_name,
            id_fix.preferred_language.clone(),
            tier,
            id_fix.device_ids.clone(),
            created,
            last_seen,
        );
        assert_eq!(identity.identity_id, id_fix.identity_id);

        for dev_fix in &example.devices {
            let reg_at = DateTime::parse_from_rfc3339(&dev_fix.registered_at)
                .expect("invalid registered_at")
                .with_timezone(&chrono::Utc);
            let last_active = DateTime::parse_from_rfc3339(&dev_fix.last_active_at)
                .expect("invalid last_active_at")
                .with_timezone(&chrono::Utc);

            let device = RegisteredDevice::new(
                &dev_fix.device_id,
                &dev_fix.identity_id,
                &dev_fix.platform,
                dev_fix.device_name.clone(),
                reg_at,
                last_active,
            );
            assert_eq!(device.device_id, dev_fix.device_id);
        }
    }
}
