//! model_alignment_test.rs
//!
//! Ports the behaviour of `CircleAI.ModelAlignment`
//! (`InMemoryModelAlignment.cs`, `Contracts.cs`, `NullImplementations.cs`):
//! reversible-only apply, revert semantics, the publish auditor that refuses
//! aligned models, argument validation, and the fail-closed `Null*` defaults.

use std::sync::Arc;

use chrono::Utc;
use circle_ai::model_alignment::{
    AlignmentError, AlignmentProfile, IAlignmentAuditor, IAlignmentToolkit, InMemoryAlignmentToolkit,
    NullAlignmentAuditor, NullAlignmentToolkit, RefuseAlignedPublishAuditor,
};

fn profile(id: &str, reversible: bool) -> AlignmentProfile {
    AlignmentProfile::new(
        id,
        "test profile",
        vec!["violence".to_string(), "self-harm".to_string()],
        Utc::now(),
        reversible,
    )
}

// ── InMemoryAlignmentToolkit ────────────────────────────────────────────────

#[test]
fn toolkit_backend_id() {
    assert_eq!(InMemoryAlignmentToolkit::new().backend_id(), "in-memory");
}

#[test]
fn apply_reversible_succeeds_and_lists() {
    let tk = InMemoryAlignmentToolkit::new();
    let r = tk.apply("model-a", &profile("p1", true)).unwrap();
    assert!(r.success);
    assert_eq!(r.profile_id, "p1");
    assert!(r.failure_reason.is_none());

    let applied = tk.list_applied("model-a").unwrap();
    assert_eq!(applied.len(), 1);
    assert_eq!(applied[0].profile_id, "p1");
}

#[test]
fn apply_non_reversible_is_refused() {
    let tk = InMemoryAlignmentToolkit::new();
    let r = tk.apply("model-a", &profile("p1", false)).unwrap();
    assert!(!r.success);
    assert_eq!(
        r.failure_reason.as_deref(),
        Some("Non-reversible alignment refused by InMemoryAlignmentToolkit")
    );
    // Nothing recorded.
    assert!(tk.list_applied("model-a").unwrap().is_empty());
}

#[test]
fn apply_empty_model_id_is_invalid_argument() {
    let tk = InMemoryAlignmentToolkit::new();
    let err = tk.apply("   ", &profile("p1", true)).unwrap_err();
    assert_eq!(err, AlignmentError::InvalidArgument("modelId required".into()));
}

#[test]
fn revert_removes_applied_profile() {
    let tk = InMemoryAlignmentToolkit::new();
    tk.apply("m", &profile("p1", true)).unwrap();
    tk.apply("m", &profile("p2", true)).unwrap();

    let r = tk.revert("m", "p1").unwrap();
    assert!(r.success);
    let applied = tk.list_applied("m").unwrap();
    assert_eq!(applied.len(), 1);
    assert_eq!(applied[0].profile_id, "p2");
}

#[test]
fn revert_unknown_model_reports_unknown() {
    let tk = InMemoryAlignmentToolkit::new();
    let r = tk.revert("ghost", "p1").unwrap();
    assert!(!r.success);
    assert_eq!(r.failure_reason.as_deref(), Some("Unknown model"));
}

#[test]
fn revert_not_applied_profile_reports_not_applied() {
    let tk = InMemoryAlignmentToolkit::new();
    tk.apply("m", &profile("p1", true)).unwrap();
    let r = tk.revert("m", "nope").unwrap();
    assert!(!r.success);
    assert_eq!(
        r.failure_reason.as_deref(),
        Some("Profile not applied to this model")
    );
}

#[test]
fn revert_requires_both_ids() {
    let tk = InMemoryAlignmentToolkit::new();
    assert_eq!(
        tk.revert("", "p1").unwrap_err(),
        AlignmentError::InvalidArgument("modelId required".into())
    );
    assert_eq!(
        tk.revert("m", "  ").unwrap_err(),
        AlignmentError::InvalidArgument("profileId required".into())
    );
}

#[test]
fn list_applied_unknown_model_is_empty() {
    let tk = InMemoryAlignmentToolkit::new();
    assert!(tk.list_applied("nobody").unwrap().is_empty());
}

#[test]
fn list_applied_empty_model_id_is_invalid_argument() {
    let tk = InMemoryAlignmentToolkit::new();
    assert_eq!(
        tk.list_applied(" ").unwrap_err(),
        AlignmentError::InvalidArgument("modelId required".into())
    );
}

// ── RefuseAlignedPublishAuditor ─────────────────────────────────────────────

#[test]
fn auditor_backend_id() {
    let tk = Arc::new(InMemoryAlignmentToolkit::new());
    assert_eq!(
        RefuseAlignedPublishAuditor::new(tk).backend_id(),
        "refuse-aligned"
    );
}

#[test]
fn publish_ok_when_no_profiles_applied() {
    let tk = Arc::new(InMemoryAlignmentToolkit::new());
    let auditor = RefuseAlignedPublishAuditor::new(tk);
    assert!(auditor.assert_ok_to_publish("clean-model").is_ok());
}

#[test]
fn publish_refused_when_profiles_applied() {
    let tk = Arc::new(InMemoryAlignmentToolkit::new());
    tk.apply("aligned-model", &profile("p1", true)).unwrap();
    let auditor = RefuseAlignedPublishAuditor::new(tk);

    let err = auditor.assert_ok_to_publish("aligned-model").unwrap_err();
    match err {
        AlignmentError::NotAllowed(msg) => {
            assert!(msg.contains("aligned-model"));
            assert!(msg.contains("1 alignment profile(s) applied"));
        }
        other => panic!("expected NotAllowed, got {other:?}"),
    }
}

#[test]
fn publish_ok_again_after_revert() {
    let tk = Arc::new(InMemoryAlignmentToolkit::new());
    tk.apply("m", &profile("p1", true)).unwrap();
    tk.revert("m", "p1").unwrap();
    let auditor = RefuseAlignedPublishAuditor::new(tk);
    assert!(auditor.assert_ok_to_publish("m").is_ok());
}

#[test]
fn auditor_rejects_empty_model_id() {
    let tk = Arc::new(InMemoryAlignmentToolkit::new());
    let auditor = RefuseAlignedPublishAuditor::new(tk);
    assert_eq!(
        auditor.assert_ok_to_publish("").unwrap_err(),
        AlignmentError::InvalidArgument("modelId required".into())
    );
}

// ── Null (fail-closed / fail-open) implementations ──────────────────────────

#[test]
fn null_toolkit_refuses_apply_and_revert() {
    let tk = NullAlignmentToolkit::INSTANCE;
    assert_eq!(tk.backend_id(), "null");

    let a = tk.apply("m", &profile("p1", true)).unwrap();
    assert!(!a.success);
    assert_eq!(
        a.failure_reason.as_deref(),
        Some("NullAlignmentToolkit: no real backend wired.")
    );
    assert_eq!(a.profile_id, "p1");

    let r = tk.revert("m", "p1").unwrap();
    assert!(!r.success);
    assert_eq!(
        r.failure_reason.as_deref(),
        Some("NullAlignmentToolkit: nothing to revert.")
    );

    assert!(tk.list_applied("m").unwrap().is_empty());
}

#[test]
fn null_auditor_always_allows_publish() {
    let auditor = NullAlignmentAuditor::INSTANCE;
    assert_eq!(auditor.backend_id(), "null");
    assert!(auditor.assert_ok_to_publish("anything").is_ok());
}
