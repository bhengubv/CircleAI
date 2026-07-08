//! hosting_endpoints_test.rs
//!
//! Verifies the endpoint surface: InProcessEndpoint accessor, HttpLoopbackEndpoint
//! auth + route dispatch + SSE framing, AIHttpClient round-trip through the
//! in-process transport, and AIApiClient's ButlerAPI proxy shapes.

use std::sync::Mutex;

use circle_ai::hosting::endpoints::{
    AIApiClient, AIHttpClient, HttpLoopbackEndpoint, HttpRequest, IAIEndpoint,
    InProcessEndpoint, InProcessLoopbackTransport, RecordingButlerTransport,
};
use circle_ai::hosting::service::{
    AIOptions, AIService, HostingError, IAIService, IHostChatGenerator,
};
use circle_ai::hosting::{HostAIOptions, HttpResponse};
use circle_ai::inference::{ChatMessage, GenerationOptions};
use circle_ai::tools::ToolInvocation;

struct FixedGen(String);
impl IHostChatGenerator for FixedGen {
    fn generate(&self, _m: &[ChatMessage], _o: Option<&GenerationOptions>) -> Result<String, String> {
        Ok(self.0.clone())
    }
    fn stream(&self, _m: &[ChatMessage], _o: Option<&GenerationOptions>) -> Result<Vec<String>, String> {
        Ok(vec!["a".to_string(), "b\nc".to_string()])
    }
}

fn service(reply: &str) -> AIService {
    AIService::new(
        AIOptions {
            warm_on_start: false,
            ..AIOptions::default()
        },
        Box::new(FixedGen(reply.to_string())),
    )
}

#[test]
fn in_process_endpoint_exposes_service() {
    let svc = service("hi");
    let mut ep = InProcessEndpoint::new();
    assert!(ep.service_accessor().is_none());
    ep.start(&svc).unwrap();
    assert!(ep.service_accessor().is_some());
    assert_eq!(ep.service_accessor().unwrap().ask("q").unwrap(), "hi");
    ep.stop().unwrap();
    assert!(ep.service_accessor().is_none());
}

fn loopback_with(options: &HostAIOptions, svc: &AIService) -> HttpResponse {
    // Helper unused directly; kept for symmetry. Returns a 401 sample.
    let ep = HttpLoopbackEndpoint::new(options);
    ep.handle(&HttpRequest::post("/butler/ask", "wrong", "{}"))
}

#[test]
fn loopback_rejects_bad_token() {
    let opts = HostAIOptions {
        loopback_token: Some("secret".to_string()),
        ..HostAIOptions::default()
    };
    let svc = service("hi");
    let resp = loopback_with(&opts, &svc);
    assert_eq!(resp.status, 401);
    assert_eq!(resp.body, "unauthorised");
}

#[test]
fn loopback_ask_route_returns_plain_answer() {
    let opts = HostAIOptions {
        loopback_token: Some("secret".to_string()),
        ..HostAIOptions::default()
    };
    let svc = service("the answer");
    let mut ep = HttpLoopbackEndpoint::new(&opts);
    ep.start(&svc).unwrap();
    let resp = ep.handle(&HttpRequest::post(
        "/butler/ask",
        "secret",
        r#"{"question":"hi"}"#,
    ));
    assert_eq!(resp.status, 200);
    assert_eq!(resp.body, "the answer");
    assert!(resp.content_type.starts_with("text/plain"));
}

#[test]
fn loopback_chat_route_returns_content_json() {
    let opts = HostAIOptions {
        loopback_token: Some("t".to_string()),
        ..HostAIOptions::default()
    };
    let svc = service("chat-reply");
    let mut ep = HttpLoopbackEndpoint::new(&opts);
    ep.start(&svc).unwrap();
    let resp = ep.handle(&HttpRequest::post(
        "/butler/chat",
        "t",
        r#"{"messages":[{"role":"user","content":"hi"}]}"#,
    ));
    assert_eq!(resp.status, 200);
    let v: serde_json::Value = serde_json::from_str(&resp.body).unwrap();
    assert_eq!(v.get("content").unwrap(), "chat-reply");
}

#[test]
fn loopback_stream_route_frames_sse() {
    let opts = HostAIOptions {
        loopback_token: Some("t".to_string()),
        ..HostAIOptions::default()
    };
    let svc = service("x");
    let mut ep = HttpLoopbackEndpoint::new(&opts);
    ep.start(&svc).unwrap();
    let resp = ep.handle(&HttpRequest::post(
        "/butler/stream",
        "t",
        r#"{"messages":[{"role":"user","content":"hi"}]}"#,
    ));
    assert_eq!(resp.content_type, "text/event-stream");
    // Two data frames + a done frame; the second chunk "b\nc" is JSON-encoded so
    // the embedded newline doesn't break framing.
    assert!(resp.body.contains("data: \"a\"\n\n"));
    assert!(resp.body.contains("data: \"b\\nc\"\n\n"));
    assert!(resp.body.contains("event: done\n"));
}

#[test]
fn loopback_unknown_route_404() {
    let opts = HostAIOptions {
        loopback_token: Some("t".to_string()),
        ..HostAIOptions::default()
    };
    let svc = service("x");
    let mut ep = HttpLoopbackEndpoint::new(&opts);
    ep.start(&svc).unwrap();
    let resp = ep.handle(&HttpRequest::post("/butler/nope", "t", "{}"));
    assert_eq!(resp.status, 404);
}

#[test]
fn loopback_get_method_405() {
    let opts = HostAIOptions {
        loopback_token: Some("t".to_string()),
        ..HostAIOptions::default()
    };
    let svc = service("x");
    let mut ep = HttpLoopbackEndpoint::new(&opts);
    ep.start(&svc).unwrap();
    let req = HttpRequest {
        method: "GET".to_string(),
        path: "/butler/ask".to_string(),
        token: Some("t".to_string()),
        body: "{}".to_string(),
    };
    assert_eq!(ep.handle(&req).status, 405);
}

#[test]
fn ai_http_client_round_trips_through_endpoint() {
    let opts = HostAIOptions {
        loopback_token: Some("shared".to_string()),
        ..HostAIOptions::default()
    };
    let svc = service("answer!");
    let mut ep = HttpLoopbackEndpoint::new(&opts);
    ep.start(&svc).unwrap();

    let transport = InProcessLoopbackTransport::new(&ep);
    let client = AIHttpClient::new(transport, "shared");

    assert_eq!(client.ask("hello").unwrap(), "answer!");
    assert_eq!(
        client.chat(&[ChatMessage::user("hi")], None).unwrap(),
        "answer!"
    );
    // Stream round-trips back into the two chunks.
    let chunks = client.stream(&[ChatMessage::user("hi")], None).unwrap();
    assert_eq!(chunks, vec!["a".to_string(), "b\nc".to_string()]);
}

#[test]
fn ai_http_client_bad_token_errors() {
    let opts = HostAIOptions {
        loopback_token: Some("right".to_string()),
        ..HostAIOptions::default()
    };
    let svc = service("x");
    let mut ep = HttpLoopbackEndpoint::new(&opts);
    ep.start(&svc).unwrap();
    let transport = InProcessLoopbackTransport::new(&ep);
    let client = AIHttpClient::new(transport, "wrong");
    assert!(client.ask("hi").is_err());
}

// ── AIApiClient (ButlerAPI proxy) ───────────────────────────────────────────

#[test]
fn api_client_health_then_ask() {
    let transport = RecordingButlerTransport::new();
    // health → 200
    transport.push_response(HttpResponse {
        status: 200,
        content_type: "text/plain".to_string(),
        body: String::new(),
    });
    // ask → {"text": "..."}
    transport.push_response(HttpResponse {
        status: 200,
        content_type: "application/json".to_string(),
        body: r#"{"text":"cloud answer"}"#.to_string(),
    });

    let client = AIApiClient::new(transport, Some("bearer".to_string()));
    assert!(!client.is_ready());
    client.start().unwrap();
    assert!(client.is_ready());
    assert_eq!(client.ask("hi").unwrap(), "cloud answer");
}

#[test]
fn api_client_sends_correct_routes_and_bearer() {
    let transport = RecordingButlerTransport::new();
    transport.push_response(HttpResponse {
        status: 200,
        content_type: "application/json".to_string(),
        body: r#"{"text":"ok"}"#.to_string(),
    });
    let client = AIApiClient::new(transport, Some("tok".to_string()));
    let _ = client.ask("hey").unwrap();

    // We can't read `transport` back after move; instead assert via a fresh one.
    let t2 = RecordingButlerTransport::new();
    t2.push_response(HttpResponse {
        status: 200,
        content_type: "application/json".to_string(),
        body: r#"{"text":"ok"}"#.to_string(),
    });
    // Route + bearer verified through a wrapper transport.
    let recorder = RouteRecorder::default();
    let client2 = AIApiClient::new(&recorder, Some("tok".to_string()));
    let _ = client2.ask("hey");
    let seen = recorder.seen.lock().unwrap().clone();
    assert_eq!(seen.len(), 1);
    assert_eq!(seen[0].0, "api/butler/ask");
    assert_eq!(seen[0].1.as_deref(), Some("tok"));
}

#[derive(Default)]
struct RouteRecorder {
    seen: Mutex<Vec<(String, Option<String>)>>,
}
impl circle_ai::hosting::IButlerTransport for RouteRecorder {
    fn send(&self, request: &HttpRequest) -> Result<HttpResponse, String> {
        self.seen
            .lock()
            .unwrap()
            .push((request.path.clone(), request.token.clone()));
        Ok(HttpResponse {
            status: 200,
            content_type: "application/json".to_string(),
            body: r#"{"text":"ok"}"#.to_string(),
        })
    }
}

#[test]
fn api_client_failed_health_errors() {
    let transport = RecordingButlerTransport::new();
    transport.push_response(HttpResponse {
        status: 503,
        content_type: "text/plain".to_string(),
        body: "down".to_string(),
    });
    let client = AIApiClient::new(transport, None);
    assert!(matches!(client.start(), Err(HostingError::Failed(_))));
}
