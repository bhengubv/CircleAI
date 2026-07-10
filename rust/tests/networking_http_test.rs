//! networking_http_test.rs
//!
//! Ports the `CircleAI.Networking.Http` surface: `HttpEndpointDescriptor` /
//! `HttpRequestSummary` / `HttpCacheKey`, `HttpStatusFamily`,
//! `InMemoryHttpRequestMetrics`, `IHttpMessageSender` /
//! `InMemoryHttpMessageSender`, and `HttpNetworkTransport` (URL construction +
//! retry loop + header stamping).

use std::sync::Arc;
use std::time::Duration;

use chrono::{Duration as ChronoDuration, Utc};
use circle_ai::networking::{INetworkTransport, MessagePriority, NetworkPayload, TransportKind};
use circle_ai::networking_transports::{
    HttpCacheKey, HttpEndpointDescriptor, HttpNetworkTransport, HttpRequestSummary,
    HttpStatusFamily, IHttpMessageSender, InMemoryHttpMessageSender, InMemoryHttpRequestMetrics,
};

// ── HttpStatusFamily ────────────────────────────────────────────────────────

#[test]
fn status_family_classifiers() {
    assert!(HttpStatusFamily::is_2xx(200));
    assert!(HttpStatusFamily::is_2xx(299));
    assert!(!HttpStatusFamily::is_2xx(300));
    assert!(HttpStatusFamily::is_3xx(301));
    assert!(HttpStatusFamily::is_4xx(404));
    assert!(HttpStatusFamily::is_5xx(503));
    assert!(!HttpStatusFamily::is_5xx(600));
}

#[test]
fn status_family_should_retry_matches_csharp() {
    for s in [408, 425, 429, 500, 502, 503, 504] {
        assert!(HttpStatusFamily::should_retry(s), "{s} should retry");
    }
    for s in [200, 301, 400, 401, 403, 404] {
        assert!(!HttpStatusFamily::should_retry(s), "{s} should not retry");
    }
}

// ── HttpCacheKey (value equality) ───────────────────────────────────────────

#[test]
fn cache_key_is_value_equatable() {
    let a = HttpCacheKey::new("GET", "https://x/a", "application/json");
    let b = HttpCacheKey::new("GET", "https://x/a", "application/json");
    let c = HttpCacheKey::new("POST", "https://x/a", "application/json");
    assert_eq!(a, b);
    assert_ne!(a, c);
}

// ── InMemoryHttpRequestMetrics ──────────────────────────────────────────────

#[test]
fn metrics_register_and_get_endpoint() {
    let m = InMemoryHttpRequestMetrics::new();
    m.register(
        "e1",
        HttpEndpointDescriptor::new("GET", "https://x", "/ping", None),
    );
    assert_eq!(m.get_endpoint("e1").unwrap().path, "/ping");
    assert!(m.get_endpoint("nope").is_none());
}

#[test]
fn metrics_recent_requests_newest_first_and_limited() {
    let m = InMemoryHttpRequestMetrics::new();
    let base = Utc::now();
    for i in 0..4 {
        m.log(HttpRequestSummary::new(
            "e1",
            200,
            Duration::from_millis(10),
            100,
            base + ChronoDuration::seconds(i),
        ));
    }
    let recent = m.recent_requests(2);
    assert_eq!(recent.len(), 2);
    // Newest first (they share endpoint id, differ by timestamp).
    assert_eq!(recent[0].at_utc, base + ChronoDuration::seconds(3));
}

#[test]
fn metrics_avg_2xx_latency_ignores_non_2xx() {
    let m = InMemoryHttpRequestMetrics::new();
    let now = Utc::now();
    m.log(HttpRequestSummary::new("e1", 200, Duration::from_millis(100), 1, now));
    m.log(HttpRequestSummary::new("e1", 204, Duration::from_millis(300), 1, now));
    m.log(HttpRequestSummary::new("e1", 500, Duration::from_millis(9999), 1, now)); // ignored
    // (100 + 300) / 2 = 200ms.
    assert!((m.avg_2xx_latency_ms("e1") - 200.0).abs() < 1e-9);
    // Unknown endpoint => 0.
    assert_eq!(m.avg_2xx_latency_ms("nope"), 0.0);
}

// ── HttpNetworkTransport: URL construction ──────────────────────────────────

#[test]
fn transport_posts_to_messages_with_escaped_destination() {
    let sender = Arc::new(InMemoryHttpMessageSender::new());
    let t = HttpNetworkTransport::new(Arc::clone(&sender) as Arc<dyn IHttpMessageSender>, "https://api/");
    t.start();
    let payload = NetworkPayload::create(
        vec![1, 2],
        Some("node A/1".into()), // needs escaping: space + slash
        MessagePriority::High,
        "application/json",
        None,
    );
    t.send(&payload).unwrap();
    let reqs = sender.requests();
    assert_eq!(reqs.len(), 1);
    // baseUrl trailing slash trimmed; destination URL-escaped (space -> %20, / -> %2F).
    assert_eq!(reqs[0].url, "https://api/messages/node%20A%2F1");
    assert_eq!(reqs[0].content_type, "application/json");
    assert_eq!(reqs[0].body, vec![1, 2]);
}

#[test]
fn transport_posts_to_bare_messages_when_no_destination() {
    let sender = Arc::new(InMemoryHttpMessageSender::new());
    let t = HttpNetworkTransport::new(Arc::clone(&sender) as Arc<dyn IHttpMessageSender>, "https://api");
    t.start();
    t.send(&NetworkPayload::of(vec![1])).unwrap();
    assert_eq!(sender.requests()[0].url, "https://api/messages");
}

#[test]
fn transport_stamps_payload_headers() {
    let sender = Arc::new(InMemoryHttpMessageSender::new());
    let t = HttpNetworkTransport::new(Arc::clone(&sender) as Arc<dyn IHttpMessageSender>, "https://api");
    t.start();
    let payload = NetworkPayload::create(
        vec![1],
        None,
        MessagePriority::Urgent,
        "text/plain",
        None,
    );
    t.send(&payload).unwrap();
    let headers = &sender.requests()[0].headers;
    assert_eq!(headers.get("X-Payload-Id").unwrap(), &payload.id);
    assert_eq!(headers.get("X-Payload-Priority").unwrap(), "Urgent");
}

// ── HttpNetworkTransport: retry loop ────────────────────────────────────────

#[test]
fn transport_is_always_available() {
    let sender = Arc::new(InMemoryHttpMessageSender::new());
    let t = HttpNetworkTransport::new(sender as Arc<dyn IHttpMessageSender>, "https://api");
    assert_eq!(t.kind(), TransportKind::Http);
    assert!(t.is_available()); // C#: always true if configured
}

#[test]
fn transport_succeeds_on_first_2xx() {
    let sender = Arc::new(InMemoryHttpMessageSender::new());
    // Default sender returns 200 => one attempt.
    let t = HttpNetworkTransport::new(Arc::clone(&sender) as Arc<dyn IHttpMessageSender>, "https://api");
    t.start();
    t.send(&NetworkPayload::of(vec![1])).unwrap();
    assert_eq!(sender.request_count(), 1);
}

#[test]
fn transport_retries_transient_failure_then_succeeds() {
    let sender = Arc::new(InMemoryHttpMessageSender::new());
    // Attempt 0 throws (transient), attempt 1 succeeds.
    sender.script_failure("connection reset");
    sender.script_status(200);
    let t = HttpNetworkTransport::new(Arc::clone(&sender) as Arc<dyn IHttpMessageSender>, "https://api");
    t.start();
    t.send(&NetworkPayload::of(vec![1])).unwrap();
    assert_eq!(sender.request_count(), 2); // retried once
}

#[test]
fn transport_retries_up_to_three_attempts_on_persistent_failure() {
    let sender = Arc::new(InMemoryHttpMessageSender::new());
    sender.script_failure("f1");
    sender.script_failure("f2");
    sender.script_failure("f3");
    let t = HttpNetworkTransport::new(Arc::clone(&sender) as Arc<dyn IHttpMessageSender>, "https://api");
    t.start();
    // Best-effort: the loop exhausts three attempts and returns Ok (the C# loop
    // exits without rethrowing after attempt 2).
    t.send(&NetworkPayload::of(vec![1])).unwrap();
    assert_eq!(sender.request_count(), 3);
}

#[test]
fn transport_retries_non_2xx_status_then_succeeds() {
    let sender = Arc::new(InMemoryHttpMessageSender::new());
    // 503 then 200: C# EnsureSuccessStatusCode throws on 503 => retried => 200 ok.
    sender.script_status(503);
    sender.script_status(200);
    let t = HttpNetworkTransport::new(Arc::clone(&sender) as Arc<dyn IHttpMessageSender>, "https://api");
    t.start();
    t.send(&NetworkPayload::of(vec![1])).unwrap();
    assert_eq!(sender.request_count(), 2);
}

#[test]
fn transport_retries_any_non_2xx_including_4xx() {
    // C# fidelity: EnsureSuccessStatusCode throws for ANY non-2xx (e.g. 404), so
    // the loop retries it three times — the transport does NOT consult the
    // ShouldRetry set. (ShouldRetry is a Commons helper, not part of the send
    // loop.)
    let sender = Arc::new(InMemoryHttpMessageSender::new());
    sender.script_status(404);
    sender.script_status(404);
    sender.script_status(404);
    let t = HttpNetworkTransport::new(Arc::clone(&sender) as Arc<dyn IHttpMessageSender>, "https://api");
    t.start();
    t.send(&NetworkPayload::of(vec![1])).unwrap();
    assert_eq!(sender.request_count(), 3);
}

#[test]
fn transport_stops_retrying_once_a_2xx_arrives() {
    // A 4xx then a 2xx: retried once, then success — three attempts are the cap,
    // not a floor.
    let sender = Arc::new(InMemoryHttpMessageSender::new());
    sender.script_status(400);
    sender.script_status(200);
    sender.script_status(500); // must never be consumed
    let t = HttpNetworkTransport::new(Arc::clone(&sender) as Arc<dyn IHttpMessageSender>, "https://api");
    t.start();
    t.send(&NetworkPayload::of(vec![1])).unwrap();
    assert_eq!(sender.request_count(), 2);
}
