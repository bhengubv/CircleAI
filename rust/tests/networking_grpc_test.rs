//! networking_grpc_test.rs
//!
//! Ports the `CircleAI.Networking.Grpc` surface: `GrpcChannelState`,
//! `GrpcChannelDescriptor` / `GrpcRetryPolicy` / `GrpcCallSummary`,
//! `GrpcRetryPolicies`, `InMemoryGrpcCallMetrics`, `IGrpcChannel` /
//! `InMemoryGrpcChannel`, and `GrpcNetworkTransport`.

use std::sync::Arc;
use std::time::Duration;

use chrono::{Duration as ChronoDuration, Utc};
use circle_ai::networking::{INetworkTransport, NetworkPayload, TransportError, TransportKind};
use circle_ai::networking_transports::{
    GrpcCallSummary, GrpcChannelDescriptor, GrpcChannelState, GrpcNetworkTransport,
    GrpcRetryPolicies, GrpcRetryPolicy, IGrpcChannel, InMemoryGrpcCallMetrics, InMemoryGrpcChannel,
    GRPC_SEND_NOT_SUPPORTED,
};

// ── GrpcRetryPolicies (static table) ────────────────────────────────────────

#[test]
fn retry_policies_match_csharp_values() {
    let d = GrpcRetryPolicies::default_policy();
    assert_eq!(d.max_attempts, 3);
    assert_eq!(d.initial_backoff, Duration::from_millis(100));
    assert_eq!(d.max_backoff, Duration::from_secs(2));
    assert_eq!(d.multiplier, 2.0);
    assert_eq!(d.retryable_status_codes, vec!["UNAVAILABLE", "DEADLINE_EXCEEDED"]);

    let a = GrpcRetryPolicies::aggressive();
    assert_eq!(a.max_attempts, 6);
    assert_eq!(a.initial_backoff, Duration::from_millis(50));
    assert_eq!(a.max_backoff, Duration::from_secs(5));
    assert_eq!(
        a.retryable_status_codes,
        vec!["UNAVAILABLE", "DEADLINE_EXCEEDED", "RESOURCE_EXHAUSTED"]
    );

    let n = GrpcRetryPolicies::no_retry();
    assert_eq!(n.max_attempts, 1);
    assert_eq!(n.initial_backoff, Duration::ZERO);
    assert_eq!(n.max_backoff, Duration::ZERO);
    assert_eq!(n.multiplier, 1.0);
    assert!(n.retryable_status_codes.is_empty());
}

#[test]
fn retry_policy_is_retryable() {
    let d = GrpcRetryPolicies::default_policy();
    assert!(d.is_retryable("UNAVAILABLE"));
    assert!(d.is_retryable("DEADLINE_EXCEEDED"));
    assert!(!d.is_retryable("RESOURCE_EXHAUSTED"));
    assert!(!d.is_retryable("OK"));
}

#[test]
fn retry_policy_backoff_grows_and_caps() {
    let p = GrpcRetryPolicy::new(
        5,
        Duration::from_millis(100),
        Duration::from_secs(2),
        2.0,
        vec![],
    );
    assert_eq!(p.backoff_for_attempt(0), Duration::from_millis(100));
    assert_eq!(p.backoff_for_attempt(1), Duration::from_millis(200));
    assert_eq!(p.backoff_for_attempt(2), Duration::from_millis(400));
    // Grows past the 2s cap => capped.
    assert_eq!(p.backoff_for_attempt(10), Duration::from_secs(2));
}

// ── InMemoryGrpcCallMetrics ─────────────────────────────────────────────────

#[test]
fn metrics_channel_register_and_state_default_idle() {
    let m = InMemoryGrpcCallMetrics::new();
    assert_eq!(m.state("unknown"), GrpcChannelState::Idle);
    m.register_channel(
        "c1",
        GrpcChannelDescriptor::new("https://x", true, 1024, 1024, Duration::from_secs(30)),
    );
    assert_eq!(m.get_channel("c1").unwrap().target, "https://x");
    m.set_state("c1", GrpcChannelState::Ready);
    assert_eq!(m.state("c1"), GrpcChannelState::Ready);
}

#[test]
fn metrics_log_call_returns_grpc_ids_monotonic() {
    let m = InMemoryGrpcCallMetrics::new();
    let now = Utc::now();
    let id1 = m.log_call(GrpcCallSummary::new(
        "Svc/Method",
        1,
        Duration::from_millis(5),
        "OK",
        now,
    ));
    let id2 = m.log_call(GrpcCallSummary::new(
        "Svc/Method",
        1,
        Duration::from_millis(6),
        "OK",
        now,
    ));
    assert_eq!(id1, "grpc-1");
    assert_eq!(id2, "grpc-2");
}

#[test]
fn metrics_recent_calls_newest_first_and_limited() {
    let m = InMemoryGrpcCallMetrics::new();
    let base = Utc::now();
    for i in 0..4 {
        m.log_call(GrpcCallSummary::new(
            format!("M{i}"),
            1,
            Duration::from_millis(1),
            "OK",
            base + ChronoDuration::seconds(i),
        ));
    }
    let recent = m.recent_calls(2);
    assert_eq!(recent.len(), 2);
    assert_eq!(recent[0].method, "M3"); // newest
    assert_eq!(recent[1].method, "M2");
}

// ── InMemoryGrpcChannel ─────────────────────────────────────────────────────

#[test]
fn channel_for_address_starts_idle() {
    let ch = InMemoryGrpcChannel::for_address("https://api.example");
    assert_eq!(ch.target(), "https://api.example");
    assert_eq!(ch.state(), GrpcChannelState::Idle);
    assert!(!ch.is_disposed());
}

#[test]
fn channel_dispose_shuts_down() {
    let ch = InMemoryGrpcChannel::for_address("https://x");
    ch.dispose();
    assert!(ch.is_disposed());
    assert_eq!(ch.state(), GrpcChannelState::Shutdown);
}

// ── GrpcNetworkTransport ────────────────────────────────────────────────────

#[test]
fn transport_kind_is_grpc_and_lifecycle_flag() {
    let t = GrpcNetworkTransport::for_address("https://x");
    assert_eq!(t.kind(), TransportKind::Grpc);
    assert!(!t.is_available());
    t.start();
    assert!(t.is_available());
    t.stop();
    assert!(!t.is_available());
}

#[test]
fn transport_send_is_not_supported() {
    let t = GrpcNetworkTransport::for_address("https://x");
    t.start();
    let err = t.send(&NetworkPayload::of(vec![1])).unwrap_err();
    assert_eq!(
        err,
        TransportError::NotSupported(TransportKind::Grpc, GRPC_SEND_NOT_SUPPORTED.to_string())
    );
    // The message matches the C# NotSupportedException text.
    assert!(GRPC_SEND_NOT_SUPPORTED.contains("not a generic send path"));
}

#[test]
fn transport_exposes_channel() {
    let ch = Arc::new(InMemoryGrpcChannel::for_address("https://api"));
    let t = GrpcNetworkTransport::new(Arc::clone(&ch) as Arc<dyn IGrpcChannel>);
    assert_eq!(t.channel().target(), "https://api");
}

#[test]
fn transport_drop_disposes_channel() {
    let ch = Arc::new(InMemoryGrpcChannel::for_address("https://api"));
    {
        let t = GrpcNetworkTransport::new(Arc::clone(&ch) as Arc<dyn IGrpcChannel>);
        let _ = t.kind();
    } // t dropped here
    assert!(ch.is_disposed());
}
