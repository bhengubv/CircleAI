//! security_primitives_test.rs
//!
//! Ports `SecurityCheckpoint.cs`, `UhidKeyRing.cs`, and
//! `RedactedEvidenceJsonConverter.cs`.

use std::collections::HashMap;

use circle_ai::security::{
    hash_redacted, redact_evidence, to_redacted_json, KeyRingError, SecurityCheckpoint,
    UhidKeyRing,
};

// ── SecurityCheckpoint ──────────────────────────────────────────────────────

#[test]
fn checkpoint_verifies_untampered_payload() {
    let cp = SecurityCheckpoint::create("uhid-1", "CircleAI.Memory", b"state".to_vec());
    assert!(cp.verify());
    assert_eq!(cp.uhid_identity_id, "uhid-1");
    assert_eq!(cp.module_label, "CircleAI.Memory");
    assert_eq!(cp.payload_hash.len(), 32);
}

#[test]
fn checkpoint_detects_tampering() {
    let mut cp = SecurityCheckpoint::create("uhid-1", "Mod", b"good".to_vec());
    cp.payload = b"evil".to_vec();
    assert!(!cp.verify());
}

#[test]
fn checkpoint_hash_is_sha256_of_payload() {
    // Empty payload SHA-256 = e3b0c442... (well-known vector).
    let cp = SecurityCheckpoint::create("uhid", "Mod", Vec::new());
    let hex: String = cp.payload_hash.iter().map(|b| format!("{b:02x}")).collect();
    assert_eq!(
        hex,
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    );
}

#[test]
fn checkpoint_display_hides_payload() {
    let cp = SecurityCheckpoint::create("uhid-9", "CircleAI.Companion", b"secret-bytes".to_vec());
    let s = cp.to_string();
    assert!(s.contains("SecurityCheckpoint(Id="));
    assert!(s.contains("Module=CircleAI.Companion"));
    assert!(s.contains("Uhid=uhid-9"));
    assert!(s.contains("PayloadBytes=12"));
    // Raw payload must never appear.
    assert!(!s.contains("secret-bytes"));
    // Hash prefix present (upper hex, 16 chars for first 8 bytes).
    assert!(s.contains("PayloadSha256="));
}

#[test]
#[should_panic(expected = "uhidIdentityId required")]
fn checkpoint_rejects_blank_uhid() {
    let _ = SecurityCheckpoint::create("   ", "Mod", b"x".to_vec());
}

#[test]
#[should_panic(expected = "moduleLabel required")]
fn checkpoint_rejects_blank_module() {
    let _ = SecurityCheckpoint::create("uhid", "", b"x".to_vec());
}

// ── UhidKeyRing ─────────────────────────────────────────────────────────────

#[test]
fn keyring_signs_and_verifies() {
    let ring = UhidKeyRing::generate_fresh("uhid-1");
    assert_eq!(ring.uhid_identity_id(), "uhid-1");
    assert!(!ring.is_revoked());
    assert!(ring.revoked_at().is_none());
    assert!(!ring.public_key_der().is_empty());

    let data = b"payload";
    let sig = ring.sign(data).unwrap();
    assert!(ring.verify(data, &sig));
    assert!(!ring.verify(b"tampered", &sig));
}

#[test]
fn keyring_revoke_blocks_sign_but_allows_verify() {
    let ring = UhidKeyRing::generate_fresh("uhid-1");
    let data = b"payload";
    let sig = ring.sign(data).unwrap();

    ring.revoke();
    assert!(ring.is_revoked());
    assert!(ring.revoked_at().is_some());

    // Sign now errors.
    match ring.sign(data) {
        Err(KeyRingError::Revoked(id)) => assert_eq!(id, ring.ring_id()),
        other => panic!("expected Revoked, got {other:?}"),
    }
    // Historical verify still works.
    assert!(ring.verify(data, &sig));
}

#[test]
fn keyring_revoke_is_idempotent() {
    let ring = UhidKeyRing::generate_fresh("uhid-1");
    ring.revoke();
    let first = ring.revoked_at();
    ring.revoke();
    assert_eq!(ring.revoked_at(), first);
}

#[test]
fn keyring_rotate_returns_fresh_ring_and_revokes_old() {
    let ring = UhidKeyRing::generate_fresh("uhid-1");
    let old_id = ring.ring_id();
    let fresh = ring.rotate();

    assert!(ring.is_revoked(), "old ring revoked");
    assert!(!fresh.is_revoked(), "new ring active");
    assert_ne!(fresh.ring_id(), old_id, "new ring id differs");
    assert_eq!(fresh.uhid_identity_id(), "uhid-1", "identity preserved");

    // New ring signs; a signature from the new ring must not verify on old key.
    let data = b"payload";
    let sig = fresh.sign(data).unwrap();
    assert!(fresh.verify(data, &sig));
    assert!(!ring.verify(data, &sig), "distinct keys");
}

#[test]
fn keyring_dispose_disables_sign_and_verify() {
    let ring = UhidKeyRing::generate_fresh("uhid-1");
    let data = b"payload";
    let sig = ring.sign(data).unwrap();
    ring.dispose();
    assert!(matches!(ring.sign(data), Err(KeyRingError::Disposed)));
    assert!(!ring.verify(data, &sig), "verify fails after dispose");
}

#[test]
#[should_panic(expected = "uhidIdentityId required")]
fn keyring_rejects_blank_identity() {
    let _ = UhidKeyRing::generate_fresh("  ");
}

#[test]
fn keyring_fresh_rings_have_distinct_ids() {
    let a = UhidKeyRing::generate_fresh("uhid");
    let b = UhidKeyRing::generate_fresh("uhid");
    assert_ne!(a.ring_id(), b.ring_id());
}

// ── RedactedEvidenceJsonConverter ───────────────────────────────────────────

#[test]
fn hash_redacted_empty_is_bare_prefix() {
    assert_eq!(hash_redacted(""), "sha256:");
}

#[test]
fn hash_redacted_matches_sha256_hex_lower() {
    // "token" SHA-256 lower hex.
    let h = hash_redacted("token");
    assert_eq!(
        h,
        "sha256:3c469e9d6c5875d37a43f353d4f88e61fcf812c66eee3457465a40b0da4153e0"
    );
}

#[test]
fn redact_evidence_preserves_keys_and_redacts_values() {
    let mut ev = HashMap::new();
    ev.insert("session".to_string(), "secret-token".to_string());
    ev.insert("payload".to_string(), "raw-bytes".to_string());
    let red = redact_evidence(&ev);
    assert_eq!(red.len(), 2);
    assert!(red.contains_key("session"));
    assert!(red.contains_key("payload"));
    // Values are redacted, never raw.
    for v in red.values() {
        assert!(v.starts_with("sha256:"));
        assert!(!v.contains("secret-token"));
        assert!(!v.contains("raw-bytes"));
    }
}

#[test]
fn redacted_json_is_sorted_and_redacted() {
    let mut ev = HashMap::new();
    ev.insert("b".to_string(), "one".to_string());
    ev.insert("a".to_string(), "two".to_string());
    let json = to_redacted_json(&ev);
    // BTreeMap ordering -> "a" before "b".
    let a_pos = json.find("\"a\"").unwrap();
    let b_pos = json.find("\"b\"").unwrap();
    assert!(a_pos < b_pos, "keys sorted: {json}");
    assert!(json.contains("sha256:"));
    assert!(!json.contains("one"));
    assert!(!json.contains("two"));
}

#[test]
fn redacted_json_empty_map_is_empty_object() {
    let ev: HashMap<String, String> = HashMap::new();
    assert_eq!(to_redacted_json(&ev), "{}");
}
