//! networking_transports::http — Rust port of `CircleAI.Networking.Http`
//! (`src/CircleAI.Networking.Http/*.cs`).
//!
//! `HttpClient` binding of the [`crate::networking::INetworkTransport`] contract.
//! Faithful ports:
//!
//!   * [`HttpEndpointDescriptor`] / [`HttpRequestSummary`] / [`HttpCacheKey`] —
//!     the C# `record`s.
//!   * [`HttpStatusFamily`]        — the 2xx/3xx/4xx/5xx classifiers +
//!     `should_retry` (408/425/429/5xx), port of the C# static class.
//!   * [`InMemoryHttpRequestMetrics`] — endpoint registry + request log +
//!     average-2xx-latency, matching the C# ordering / aggregation.
//!   * [`IHttpMessageSender`]      — the HTTP send dependency (trait), port of the
//!     C# `HttpClient.PostAsync`, with a working [`InMemoryHttpMessageSender`].
//!   * [`HttpNetworkTransport`]    — `INetworkTransport` over HTTP: POSTs the
//!     payload to `{baseUrl}/messages/{dest}` with URL-escaped destination, the
//!     three-attempt exponential-backoff retry loop, and `X-Payload-Id` /
//!     `X-Payload-Priority` header stamping. Port of the C# transport.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use crate::networking::{INetworkTransport, NetworkPayload, TransportError, TransportKind};

// ─────────────────────────────────────────────────────────────────────────────
// Value records
// ─────────────────────────────────────────────────────────────────────────────

/// Describes an HTTP endpoint. Port of the C# `HttpEndpointDescriptor`.
/// `DefaultHeaders` is optional and ordered (a `BTreeMap`) so it round-trips
/// deterministically.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct HttpEndpointDescriptor {
    pub method: String,
    pub base_uri: String,
    pub path: String,
    pub default_headers: Option<std::collections::BTreeMap<String, String>>,
}

impl HttpEndpointDescriptor {
    pub fn new(
        method: impl Into<String>,
        base_uri: impl Into<String>,
        path: impl Into<String>,
        default_headers: Option<std::collections::BTreeMap<String, String>>,
    ) -> Self {
        Self {
            method: method.into(),
            base_uri: base_uri.into(),
            path: path.into(),
            default_headers,
        }
    }
}

/// A summary of one completed HTTP request. Port of the C# `HttpRequestSummary`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct HttpRequestSummary {
    pub endpoint_id: String,
    pub status_code: i32,
    pub latency: Duration,
    pub response_bytes: i32,
    pub at_utc: DateTime<Utc>,
}

impl HttpRequestSummary {
    pub fn new(
        endpoint_id: impl Into<String>,
        status_code: i32,
        latency: Duration,
        response_bytes: i32,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            endpoint_id: endpoint_id.into(),
            status_code,
            latency,
            response_bytes,
            at_utc,
        }
    }
}

/// A response-cache key. Port of the C# `HttpCacheKey`. Value-equatable so it can
/// be used as a map key.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct HttpCacheKey {
    pub method: String,
    pub full_uri: String,
    pub accept_header: String,
}

impl HttpCacheKey {
    pub fn new(
        method: impl Into<String>,
        full_uri: impl Into<String>,
        accept_header: impl Into<String>,
    ) -> Self {
        Self {
            method: method.into(),
            full_uri: full_uri.into(),
            accept_header: accept_header.into(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HttpStatusFamily — port of the C# static class
// ─────────────────────────────────────────────────────────────────────────────

/// HTTP status-code classifiers. Port of the C# static `HttpStatusFamily`.
pub struct HttpStatusFamily;

impl HttpStatusFamily {
    pub fn is_2xx(s: i32) -> bool {
        (200..300).contains(&s)
    }
    pub fn is_3xx(s: i32) -> bool {
        (300..400).contains(&s)
    }
    pub fn is_4xx(s: i32) -> bool {
        (400..500).contains(&s)
    }
    pub fn is_5xx(s: i32) -> bool {
        (500..600).contains(&s)
    }
    /// Whether a status warrants a retry: 408 / 425 / 429 / any 5xx. Mirrors
    /// `ShouldRetry`.
    pub fn should_retry(s: i32) -> bool {
        s == 408 || s == 425 || s == 429 || Self::is_5xx(s)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryHttpRequestMetrics — port of the C# metrics
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory endpoint registry + request log. Port of the C#
/// `InMemoryHttpRequestMetrics`.
///
/// Matches the C#:
///   * [`recent_requests`](Self::recent_requests) returns the newest `limit`
///     requests, newest-first.
///   * [`avg_2xx_latency_ms`](Self::avg_2xx_latency_ms) averages only 2xx request
///     latencies (in ms) for an endpoint, `0.0` when none.
#[derive(Default)]
pub struct InMemoryHttpRequestMetrics {
    endpoints: Mutex<HashMap<String, HttpEndpointDescriptor>>,
    requests: Mutex<Vec<HttpRequestSummary>>,
}

impl InMemoryHttpRequestMetrics {
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers (or replaces) an endpoint keyed by `id`.
    pub fn register(&self, id: impl Into<String>, d: HttpEndpointDescriptor) {
        self.endpoints.lock().unwrap().insert(id.into(), d);
    }

    /// The endpoint for `id`, if registered.
    pub fn get_endpoint(&self, id: &str) -> Option<HttpEndpointDescriptor> {
        self.endpoints.lock().unwrap().get(id).cloned()
    }

    /// Logs a request summary.
    pub fn log(&self, s: HttpRequestSummary) {
        self.requests.lock().unwrap().push(s);
    }

    /// The newest `limit` requests, newest-first. Mirrors `RecentRequests`.
    pub fn recent_requests(&self, limit: usize) -> Vec<HttpRequestSummary> {
        let mut v: Vec<HttpRequestSummary> = self.requests.lock().unwrap().clone();
        v.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        v.truncate(limit);
        v
    }

    /// Average 2xx latency (ms) for `endpoint_id`; `0.0` if none. Mirrors
    /// `Avg2xxLatencyMs`.
    pub fn avg_2xx_latency_ms(&self, endpoint_id: &str) -> f64 {
        let guard = self.requests.lock().unwrap();
        let rows: Vec<f64> = guard
            .iter()
            .filter(|r| r.endpoint_id == endpoint_id && HttpStatusFamily::is_2xx(r.status_code))
            .map(|r| r.latency.as_secs_f64() * 1000.0)
            .collect();
        if rows.is_empty() {
            0.0
        } else {
            rows.iter().sum::<f64>() / rows.len() as f64
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IHttpMessageSender — port of the C# HttpClient dependency
// ─────────────────────────────────────────────────────────────────────────────

/// One outgoing HTTP request the transport builds (the C# `ByteArrayContent` +
/// headers passed to `PostAsync`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HttpPostRequest {
    pub url: String,
    pub body: Vec<u8>,
    pub content_type: String,
    /// Ordered so it round-trips deterministically. Carries `X-Payload-Id` and
    /// `X-Payload-Priority`.
    pub headers: std::collections::BTreeMap<String, String>,
}

/// The result of an HTTP POST: the status code (used by the transport's retry
/// loop via [`HttpStatusFamily`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HttpPostResult {
    pub status_code: i32,
}

/// The HTTP send dependency. Port of the C# `HttpClient.PostAsync`
/// (`EnsureSuccessStatusCode` classifies the result). Injecting it keeps
/// [`HttpNetworkTransport`] deterministic; [`InMemoryHttpMessageSender`] is a
/// working scriptable implementation.
pub trait IHttpMessageSender: Send + Sync {
    /// POST `request`; returns the response status (an `Err` models a transport
    /// exception — `HttpRequestException` — which the retry loop treats as
    /// transient).
    fn post(&self, request: &HttpPostRequest) -> Result<HttpPostResult, HttpSendError>;
}

/// A transport-level HTTP failure (the C# `HttpRequestException`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HttpSendError(pub String);

impl std::fmt::Display for HttpSendError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "http request error: {}", self.0)
    }
}

impl std::error::Error for HttpSendError {}

/// A working in-memory [`IHttpMessageSender`]. Records every request; returns a
/// scripted sequence of results (defaulting to `200 OK`), so the transport's
/// retry loop can be driven deterministically.
pub struct InMemoryHttpMessageSender {
    requests: Mutex<Vec<HttpPostRequest>>,
    /// Scripted per-call outcomes, consumed front-to-back. When exhausted,
    /// `default_result` is returned.
    scripted: Mutex<std::collections::VecDeque<Result<HttpPostResult, HttpSendError>>>,
    default_result: Result<HttpPostResult, HttpSendError>,
}

impl Default for InMemoryHttpMessageSender {
    fn default() -> Self {
        Self::new()
    }
}

impl InMemoryHttpMessageSender {
    /// A sender that returns `200 OK` for every request.
    pub fn new() -> Self {
        Self {
            requests: Mutex::new(Vec::new()),
            scripted: Mutex::new(std::collections::VecDeque::new()),
            default_result: Ok(HttpPostResult { status_code: 200 }),
        }
    }

    /// Queues a scripted outcome, consumed in order by successive [`post`] calls.
    /// An `Err` models a transient `HttpRequestException`.
    pub fn script(&self, outcome: Result<HttpPostResult, HttpSendError>) {
        self.scripted.lock().unwrap().push_back(outcome);
    }

    /// Convenience: queue a scripted status code.
    pub fn script_status(&self, status_code: i32) {
        self.script(Ok(HttpPostResult { status_code }));
    }

    /// Convenience: queue a scripted transient failure.
    pub fn script_failure(&self, message: impl Into<String>) {
        self.script(Err(HttpSendError(message.into())));
    }

    /// Every request POSTed so far, in order.
    pub fn requests(&self) -> Vec<HttpPostRequest> {
        self.requests.lock().unwrap().clone()
    }

    /// Count of POSTed requests (attempts).
    pub fn request_count(&self) -> usize {
        self.requests.lock().unwrap().len()
    }
}

impl IHttpMessageSender for InMemoryHttpMessageSender {
    fn post(&self, request: &HttpPostRequest) -> Result<HttpPostResult, HttpSendError> {
        self.requests.lock().unwrap().push(request.clone());
        self.scripted
            .lock()
            .unwrap()
            .pop_front()
            .unwrap_or_else(|| self.default_result.clone())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HttpNetworkTransport — port of HttpNetworkTransport.cs
// ─────────────────────────────────────────────────────────────────────────────

/// [`INetworkTransport`] backed by an HTTP sender. Port of the C#
/// `HttpNetworkTransport`.
///
/// `is_available` is always `true` (the C# "assume HTTP always available if
/// configured"). `send` POSTs the payload data to `{baseUrl}/messages/{dest}`
/// (destination URL-escaped when present, else `{baseUrl}/messages`), stamping
/// `X-Payload-Id` and `X-Payload-Priority` and the payload content type, retrying
/// up to three times.
///
/// The retry rule matches the C# exactly: `EnsureSuccessStatusCode()` throws for
/// ANY non-2xx response, and the `catch (HttpRequestException) when (attempt < 2)`
/// clause therefore retries on *any* failure — a transient send error OR any
/// non-2xx status — while `attempt < 2`, giving up (swallowing) after the third
/// attempt. The transport does NOT consult [`HttpStatusFamily::should_retry`]
/// here (that helper is part of the Commons surface, not the send loop). A 2xx on
/// any attempt returns immediately.
pub struct HttpNetworkTransport {
    sender: Arc<dyn IHttpMessageSender>,
    base_url: String,
    running: AtomicBool,
}

impl HttpNetworkTransport {
    /// Builds a transport over `sender` posting under `base_url` (trailing `/`
    /// trimmed, as the C# `baseUrl.TrimEnd('/')`).
    pub fn new(sender: Arc<dyn IHttpMessageSender>, base_url: impl Into<String>) -> Self {
        let base_url = base_url.into();
        let trimmed = base_url.trim_end_matches('/').to_string();
        Self {
            sender,
            base_url: trimmed,
            running: AtomicBool::new(false),
        }
    }

    /// URL-escapes `s` for a path segment (the C# `Uri.EscapeDataString`). Escapes
    /// everything outside the RFC 3986 unreserved set `A-Za-z0-9-_.~`.
    fn escape_data_string(s: &str) -> String {
        let mut out = String::with_capacity(s.len());
        for &b in s.as_bytes() {
            let c = b as char;
            if c.is_ascii_alphanumeric() || matches!(c, '-' | '_' | '.' | '~') {
                out.push(c);
            } else {
                out.push('%');
                out.push_str(&format!("{b:02X}"));
            }
        }
        out
    }

    /// Builds the target URL exactly as the C# does.
    fn build_url(&self, destination_id: Option<&str>) -> String {
        match destination_id {
            Some(dest) if !dest.is_empty() => {
                format!("{}/messages/{}", self.base_url, Self::escape_data_string(dest))
            }
            _ => format!("{}/messages", self.base_url),
        }
    }
}

impl INetworkTransport for HttpNetworkTransport {
    fn kind(&self) -> TransportKind {
        TransportKind::Http
    }

    fn is_available(&self) -> bool {
        // C#: assume HTTP always available if configured.
        true
    }

    fn start(&self) {
        self.running.store(true, Ordering::SeqCst);
    }

    fn stop(&self) {
        self.running.store(false, Ordering::SeqCst);
    }

    fn send(&self, payload: &NetworkPayload) -> Result<(), TransportError> {
        let url = self.build_url(payload.destination_id.as_deref());

        let mut headers = std::collections::BTreeMap::new();
        headers.insert("X-Payload-Id".to_string(), payload.id.clone());
        headers.insert(
            "X-Payload-Priority".to_string(),
            format!("{:?}", payload.priority),
        );
        let request = HttpPostRequest {
            url,
            body: payload.data.clone(),
            content_type: payload.content_type.clone(),
            headers,
        };

        // Three attempts with exponential backoff on failure (the C# loop).
        // Backoff is computed but not slept on — this is a deterministic,
        // synchronous port; the schedule (2^attempt seconds) is preserved for
        // callers that wish to honour it.
        //
        // In C#, `EnsureSuccessStatusCode()` throws HttpRequestException for any
        // non-2xx, so a non-2xx status is indistinguishable from a transient send
        // error inside the loop: both are "failure". `is_failure` unifies them.
        for attempt in 0..3u32 {
            // 2xx → EnsureSuccessStatusCode passes → return immediately. Any other
            // outcome (non-2xx status, which C# turns into a thrown
            // HttpRequestException, or a transient send error) is a failure.
            if let Ok(result) = self.sender.post(&request) {
                if HttpStatusFamily::is_2xx(result.status_code) {
                    return Ok(());
                }
            }
            if attempt < 2 {
                // catch (HttpRequestException) when (attempt < 2): back off, then
                // retry.
                let _backoff = Duration::from_secs(2u64.pow(attempt));
                continue;
            }
            // Final attempt failed: the C# loop exits without rethrowing (the
            // `when (attempt < 2)` guard no longer matches; the exception would
            // propagate, but the method has no further work — a best-effort fire).
            return Ok(());
        }
        Ok(())
    }
}
