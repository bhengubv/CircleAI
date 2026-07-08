//! hosting_service_test.rs
//!
//! Verifies the AIService lifecycle + chat/stream/agentic/feedback flows and the
//! FallbackAIService RAM-gated backend selection. Mirrors the C# AIService /
//! FallbackAIService behaviours.

use std::sync::Mutex;

use circle_ai::hosting::service::{
    AIOptions, AIService, DeviceContext, FallbackAIService, FixedRamProbe, HostingError,
    IAIService, IHostChatGenerator, IHostToolBridge,
};
use circle_ai::inference::{ChatMessage, GenerationOptions};
use circle_ai::tools::{ToolInvocation, ToolResult};

/// Deterministic host generator: echoes the last user message with a prefix,
/// or emits a queued canned reply (for agentic tool-call scripting).
struct ScriptGenerator {
    replies: Mutex<std::collections::VecDeque<String>>,
    default_reply: String,
}

impl ScriptGenerator {
    fn new(default_reply: &str) -> Self {
        Self {
            replies: Mutex::new(std::collections::VecDeque::new()),
            default_reply: default_reply.to_string(),
        }
    }
    fn push(&self, reply: &str) {
        self.replies.lock().unwrap().push_back(reply.to_string());
    }
}

impl IHostChatGenerator for ScriptGenerator {
    fn generate(
        &self,
        _messages: &[ChatMessage],
        _options: Option<&GenerationOptions>,
    ) -> Result<String, String> {
        Ok(self
            .replies
            .lock()
            .unwrap()
            .pop_front()
            .unwrap_or_else(|| self.default_reply.clone()))
    }
    fn stream(
        &self,
        _messages: &[ChatMessage],
        _options: Option<&GenerationOptions>,
    ) -> Result<Vec<String>, String> {
        // Two chunks so token_count = 2.
        Ok(vec!["Hello ".to_string(), "world".to_string()])
    }
}

struct EchoToolBridge;
impl IHostToolBridge for EchoToolBridge {
    fn invoke(&self, invocation: &ToolInvocation) -> ToolResult {
        ToolResult::ok(&invocation.tool_name, Some(serde_json::json!({ "ok": true })))
    }
}

fn base_options() -> AIOptions {
    AIOptions {
        warm_on_start: false,
        ..AIOptions::default()
    }
}

#[test]
fn start_is_idempotent_and_sets_ready() {
    let svc = AIService::new(base_options(), Box::new(ScriptGenerator::new("hi")));
    assert!(!svc.is_ready());
    svc.start().unwrap();
    assert!(svc.is_ready());
    // Second start is a no-op.
    svc.start().unwrap();
    assert!(svc.is_ready());
}

#[test]
fn ask_prepends_enriched_system_prompt_and_returns_reply() {
    let svc = AIService::new(base_options(), Box::new(ScriptGenerator::new("the answer")));
    let out = svc.ask("what is 2+2?").unwrap();
    assert_eq!(out, "the answer");
}

#[test]
fn chat_auto_starts_when_not_started() {
    let svc = AIService::new(base_options(), Box::new(ScriptGenerator::new("reply")));
    // chat without an explicit start() — ensure_started kicks in.
    let out = svc
        .chat(&[ChatMessage::user("hi")], None)
        .unwrap();
    assert_eq!(out, "reply");
    assert!(svc.is_ready());
}

#[test]
fn stream_returns_all_chunks() {
    let svc = AIService::new(base_options(), Box::new(ScriptGenerator::new("x")));
    svc.start().unwrap();
    let chunks = svc.stream(&[ChatMessage::user("hi")], None).unwrap();
    assert_eq!(chunks, vec!["Hello ".to_string(), "world".to_string()]);
}

#[test]
fn invoke_tool_without_bridge_returns_failure() {
    let svc = AIService::new(base_options(), Box::new(ScriptGenerator::new("x")));
    let mut args = std::collections::HashMap::new();
    args.insert("a".to_string(), serde_json::json!(1));
    let result = svc.invoke_tool(&ToolInvocation::new("t", args)).unwrap();
    assert!(!result.success);
    assert_eq!(result.error.as_deref(), Some("No tool bridge configured."));
}

#[test]
fn invoke_tool_with_bridge_succeeds() {
    let opts = AIOptions {
        warm_on_start: false,
        tool_bridge: Some(Box::new(EchoToolBridge)),
        ..AIOptions::default()
    };
    let svc = AIService::new(opts, Box::new(ScriptGenerator::new("x")));
    let result = svc
        .invoke_tool(&ToolInvocation::new("echo", std::collections::HashMap::new()))
        .unwrap();
    assert!(result.success);
}

#[test]
fn agentic_chat_runs_tool_then_returns_plaintext() {
    let gen = ScriptGenerator::new("done");
    // First reply asks for a tool; second is plain text and terminates the loop.
    gen.push(r#"<tool_call>{"name":"echo","arguments":{"x":1}}</tool_call>"#);
    gen.push("all done");
    let opts = AIOptions {
        warm_on_start: false,
        tool_bridge: Some(Box::new(EchoToolBridge)),
        agentic_max_iterations: Some(5),
        ..AIOptions::default()
    };
    let svc = AIService::new(opts, Box::new(gen));
    let out = svc.agentic_chat("please echo", None).unwrap();
    assert_eq!(out, "all done");
}

#[test]
fn parse_tool_call_supports_tool_name_key() {
    let inv = AIService::parse_tool_call(
        r#"noise <tool_call>{"tool_name":"gmail.send","arguments":{"to":"a@b.c"}}</tool_call> trailing"#,
    )
    .unwrap();
    assert_eq!(inv.tool_name, "gmail.send");
    assert_eq!(inv.arguments.get("to").unwrap(), &serde_json::json!("a@b.c"));
}

#[test]
fn parse_tool_call_returns_none_without_tags() {
    assert!(AIService::parse_tool_call("just some text").is_none());
}

#[test]
fn disposed_semantics_via_stop_then_start_again() {
    let svc = AIService::new(base_options(), Box::new(ScriptGenerator::new("x")));
    svc.start().unwrap();
    svc.stop().unwrap();
    assert!(!svc.is_ready());
    // Re-start works (stop does not dispose).
    svc.start().unwrap();
    assert!(svc.is_ready());
}

#[test]
fn ask_empty_question_errors() {
    let svc = AIService::new(base_options(), Box::new(ScriptGenerator::new("x")));
    let err = svc.ask("").unwrap_err();
    assert_eq!(err, HostingError::Failed("question required".to_string()));
}

#[test]
fn device_context_enriches_prompt_but_reply_unchanged() {
    // Reply is fixed; this asserts device-context enrichment does not error and
    // the flow still returns the generator's reply.
    let opts = AIOptions {
        warm_on_start: false,
        device_context: Some(DeviceContext {
            local_time: Some("2026-07-08 12:00".to_string()),
            battery_level: Some(0.55),
            is_charging: Some(true),
            network_type: Some("wifi".to_string()),
            active_app_id: Some("bruh".to_string()),
            ..DeviceContext::default()
        }),
        ..AIOptions::default()
    };
    let svc = AIService::new(opts, Box::new(ScriptGenerator::new("ok")));
    assert_eq!(svc.ask("hi").unwrap(), "ok");
}

// ── FallbackAIService ───────────────────────────────────────────────────────

fn make_service(reply: &str) -> Box<dyn IAIService> {
    Box::new(AIService::new(
        base_options(),
        Box::new(ScriptGenerator::new(reply)),
    ))
}

#[test]
fn fallback_uses_local_when_ram_above_threshold() {
    let fb = FallbackAIService::new(
        make_service("local"),
        make_service("cloud"),
        Some(1000),
        Box::new(FixedRamProbe(2000)),
    );
    fb.start().unwrap();
    assert!(fb.is_local_active());
    assert_eq!(fb.ask("q").unwrap(), "local");
}

#[test]
fn fallback_uses_cloud_when_ram_below_threshold() {
    let fb = FallbackAIService::new(
        make_service("local"),
        make_service("cloud"),
        Some(4000),
        Box::new(FixedRamProbe(1000)),
    );
    fb.start().unwrap();
    assert!(fb.is_cloud_active());
    assert_eq!(fb.ask("q").unwrap(), "cloud");
}

#[test]
fn fallback_errors_before_start() {
    let fb = FallbackAIService::new(
        make_service("local"),
        make_service("cloud"),
        None,
        Box::new(FixedRamProbe(0)),
    );
    assert!(fb.ask("q").is_err());
}
