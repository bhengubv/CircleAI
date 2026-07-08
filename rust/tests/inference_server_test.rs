//! inference_server_test.rs
//!
//! Covers the CircleAI.Inference.Server ports: the bridge contract +
//! LocalProcessInferenceBridge, the OpenAI DTO JSON shape, the API-key auth
//! handler, the model registry, the lifecycle manager admission gate, the
//! companion session resolver, and the in-memory HTTP handlers.

use std::collections::BTreeMap;
use std::sync::Arc;

use circle_ai::inference_server::auth::{ApiKeyAuthHandler, ApiKeyOptions, AuthResult, AuthSchemes};
use circle_ai::inference_server::bridge::{
    BackendKind, CapabilityTier, DeterministicBridge, DeterministicBridgeFactory, IBridgeFactory,
    IInferenceBridge, InferenceRequest, InferenceStatus, ModelDescriptor, ModelFormat,
    UnconfiguredBridgeFactory,
};
use circle_ai::inference_server::companion_resolver::{
    ICompanionSessionResolver, InMemoryCompanionSessionFactory, InMemoryCompanionSessionResolver,
};
use circle_ai::inference_server::handlers::{
    status, AdmissionControl, AdminHandler, ChatCompletionsHandler, CompanionHandler,
    CompanionTurnRequest, HandlerResult, ServerCounters,
};
use circle_ai::inference_server::lifecycle::{
    HostProfile, IModelLifecycleManager, INativeRuntimeStatus, LoadOutcome, ModelLifecycleManager,
    ModelLoadDescriptor, NativeRuntimePaths, NativeRuntimeStatus, UnloadOutcome,
};
use circle_ai::inference_server::openai::{
    ChatCompletionMessage, ChatCompletionRequest, ErrorResponse,
};
use circle_ai::inference_server::registry::{
    IInferenceServerModelRegistry, InferenceServerModelRegistry,
};

fn descriptor(model_id: &str) -> ModelDescriptor {
    ModelDescriptor {
        model_id: model_id.to_string(),
        version: "1.0".to_string(),
        format: ModelFormat::Gguf,
        context_window_tokens: 4096,
        vocab_size: 151_936,
        parameter_count: 0,
        quantisation_label: Some("Q4_K_M".to_string()),
        approximate_memory_bytes: 1024,
    }
}

// ── Bridge + LocalProcessInferenceBridge ──────────────────────────────────────

#[test]
fn deterministic_bridge_completes_and_lists() {
    let bridge = DeterministicBridge::new(descriptor("qwen"), false);
    assert!(bridge.is_model_loaded("qwen"));
    assert!(!bridge.is_model_loaded("other"));
    assert_eq!(bridge.list_loaded_models().len(), 1);

    let req = InferenceRequest::create("qwen", "hello there");
    let resp = bridge.complete(&req);
    assert_eq!(resp.model_id, "qwen");
    assert!(!resp.output_text.is_empty());
    assert_ne!(resp.status, InferenceStatus::Failed);
    assert!(resp.prompt_token_count > 0);
}

#[test]
fn bridge_rejects_unloaded_model() {
    let bridge = DeterministicBridge::new(descriptor("qwen"), false);
    let req = InferenceRequest::create("not-loaded", "x");
    let resp = bridge.complete(&req);
    assert_eq!(resp.status, InferenceStatus::Failed);
    assert!(resp.failure_message.unwrap().contains("not loaded"));
}

#[test]
fn reasoning_bridge_populates_reasoning_text() {
    let bridge = DeterministicBridge::new(descriptor("qwen-r"), true);
    let req = InferenceRequest::create("qwen-r", "explain");
    let resp = bridge.complete(&req);
    assert!(resp.reasoning_text.is_some());
    // Streaming fragments include reasoning-kind frames.
    let frags = bridge.stream_fragments(&req);
    use circle_ai::inference_server::bridge::InferenceFragmentKind;
    assert!(frags.iter().any(|f| f.kind == InferenceFragmentKind::Reasoning));
    assert!(frags.iter().any(|f| f.kind == InferenceFragmentKind::Content));
}

// ── IBridgeFactory ────────────────────────────────────────────────────────────

#[test]
fn unconfigured_factory_refuses() {
    let f = UnconfiguredBridgeFactory;
    let r = f.create("m", BackendKind::Cpu, CapabilityTier::Tier1Small);
    assert!(r.is_err());
}

#[test]
fn deterministic_factory_scales_memory_with_tier() {
    let f = DeterministicBridgeFactory::new();
    let b0 = f.create("m", BackendKind::Cpu, CapabilityTier::Tier0Tiny).unwrap();
    let d0 = &b0.list_loaded_models()[0];
    let b4 = f.create("m", BackendKind::Cuda, CapabilityTier::Tier4Frontier).unwrap();
    let d4 = &b4.list_loaded_models()[0];
    assert!(d4.approximate_memory_bytes > d0.approximate_memory_bytes);
    assert_eq!(
        DeterministicBridgeFactory::approx_memory_from_tier(CapabilityTier::Tier0Tiny),
        1024i64 * 1024 * 1024
    );
}

#[test]
fn backend_and_tier_parse() {
    assert_eq!(BackendKind::parse("cuda"), BackendKind::Cuda);
    assert_eq!(BackendKind::parse("nonsense"), BackendKind::Cpu);
    assert!(BackendKind::Cuda.is_gpu_class());
    assert!(!BackendKind::Cpu.is_gpu_class());
    assert_eq!(CapabilityTier::parse("Tier3_Large"), CapabilityTier::Tier3Large);
    assert_eq!(CapabilityTier::parse("junk"), CapabilityTier::Tier1Small);
}

// ── OpenAI DTO JSON shape ─────────────────────────────────────────────────────

#[test]
fn chat_request_deserialises_openai_shape() {
    let json = r#"{"model":"qwen","messages":[{"role":"user","content":"hi"}],"max_tokens":128,"stream":true,"stop":["\n\n"]}"#;
    let req: ChatCompletionRequest = serde_json::from_str(json).unwrap();
    assert_eq!(req.model, "qwen");
    assert_eq!(req.messages.len(), 1);
    assert_eq!(req.max_tokens, Some(128));
    assert!(req.stream);
    assert_eq!(req.stop, Some(vec!["\n\n".to_string()]));
}

#[test]
fn message_omits_null_reasoning_content() {
    let m = ChatCompletionMessage {
        role: "assistant".to_string(),
        content: "hi".to_string(),
        name: None,
        reasoning_content: None,
    };
    let json = serde_json::to_string(&m).unwrap();
    assert!(!json.contains("reasoning_content"), "null reasoning omitted");
    assert!(!json.contains("name"));

    let m2 = ChatCompletionMessage {
        reasoning_content: Some("because".to_string()),
        ..m
    };
    assert!(serde_json::to_string(&m2).unwrap().contains("reasoning_content"));
}

#[test]
fn error_response_shape() {
    let e = ErrorResponse::of("bad", "invalid_request_error", Some("missing_model"));
    let json = serde_json::to_string(&e).unwrap();
    assert!(json.contains("\"error\""));
    assert!(json.contains("\"type\":\"invalid_request_error\""));
    assert!(json.contains("\"code\":\"missing_model\""));
}

// ── ApiKeyAuthHandler ─────────────────────────────────────────────────────────

#[test]
fn auth_disabled_succeeds_anonymous() {
    let h = ApiKeyAuthHandler::new(ApiKeyOptions {
        enabled: false,
        ..ApiKeyOptions::default()
    });
    let result = h.authenticate(&BTreeMap::new());
    match result {
        AuthResult::Success(claims) => {
            assert!(claims.iter().any(|c| c.value == "anonymous"));
        }
        _ => panic!("disabled auth must succeed"),
    }
    assert_eq!(AuthSchemes::API_KEY, "ApiKey");
}

#[test]
fn auth_matches_valid_key_constant_time() {
    let h = ApiKeyAuthHandler::new(ApiKeyOptions {
        enabled: true,
        header_name: "X-API-Key".to_string(),
        keys: vec!["secret-123".to_string()],
    });
    let mut headers = BTreeMap::new();
    // Case-insensitive header lookup.
    headers.insert("x-api-key".to_string(), "secret-123".to_string());
    assert!(h.authenticate(&headers).is_success());

    // Wrong key → Fail.
    headers.insert("x-api-key".to_string(), "wrong-000".to_string());
    assert!(matches!(h.authenticate(&headers), AuthResult::Fail(_)));

    // Missing header → NoResult.
    assert_eq!(h.authenticate(&BTreeMap::new()), AuthResult::NoResult);
}

// ── Model registry ────────────────────────────────────────────────────────────

#[test]
fn registry_registers_resolves_deregisters() {
    let reg = InferenceServerModelRegistry::new();
    let bridge: Arc<dyn IInferenceBridge> = Arc::new(DeterministicBridge::new(descriptor("m1"), false));
    reg.register("m1", bridge);
    assert!(reg.resolve("m1").is_some());
    assert!(reg.resolve("nope").is_none());
    assert_eq!(reg.chat_model_ids(), vec!["m1".to_string()]);
    assert!(reg.deregister("m1"));
    assert!(!reg.deregister("m1"));
    assert!(reg.resolve("m1").is_none());
}

// ── Lifecycle manager ─────────────────────────────────────────────────────────

fn manager() -> (Arc<InferenceServerModelRegistry>, ModelLifecycleManager) {
    let reg = Arc::new(InferenceServerModelRegistry::new());
    let probe = Arc::new(HostProfile::generous);
    let mgr = ModelLifecycleManager::new(reg.clone(), probe);
    (reg, mgr)
}

#[test]
fn lifecycle_loads_registers_and_is_idempotent() {
    let (reg, mgr) = manager();
    let factory: Arc<dyn IBridgeFactory> = Arc::new(DeterministicBridgeFactory::new());
    let desc = ModelLoadDescriptor::new(
        "qwen",
        BackendKind::Cpu,
        CapabilityTier::Tier1Small,
        0,
        1024,
        factory.clone(),
    );
    let r = mgr.load(desc);
    assert_eq!(r.outcome, LoadOutcome::Loaded);
    assert!(reg.resolve("qwen").is_some());
    assert_eq!(mgr.list().len(), 1);
    assert_eq!(mgr.total_allocated_ram_bytes(), 1024);

    // Second load of same id → AlreadyLoaded.
    let desc2 = ModelLoadDescriptor::new(
        "qwen",
        BackendKind::Cpu,
        CapabilityTier::Tier1Small,
        0,
        1024,
        factory,
    );
    assert_eq!(mgr.load(desc2).outcome, LoadOutcome::AlreadyLoaded);
}

#[test]
fn lifecycle_rejects_insufficient_ram() {
    let reg = Arc::new(InferenceServerModelRegistry::new());
    // Tiny host: 1 KiB RAM.
    let probe = Arc::new(|| HostProfile {
        total_physical_memory_bytes: 1024,
        gpu_vram_bytes: 0,
    });
    let mgr = ModelLifecycleManager::new(reg, probe);
    let factory: Arc<dyn IBridgeFactory> = Arc::new(DeterministicBridgeFactory::new());
    let desc = ModelLoadDescriptor::new(
        "big",
        BackendKind::Cpu,
        CapabilityTier::Tier4Frontier,
        0,
        1024 * 1024 * 1024,
        factory,
    );
    let r = mgr.load(desc);
    assert_eq!(r.outcome, LoadOutcome::InsufficientRam);
    assert!(r.rationale.contains("MiB RAM"));
}

#[test]
fn lifecycle_rejects_insufficient_vram_on_gpu() {
    let reg = Arc::new(InferenceServerModelRegistry::new());
    let probe = Arc::new(|| HostProfile {
        total_physical_memory_bytes: 64 * 1024 * 1024 * 1024,
        gpu_vram_bytes: 1024, // 1 KiB VRAM
    });
    let mgr = ModelLifecycleManager::new(reg, probe);
    let factory: Arc<dyn IBridgeFactory> = Arc::new(DeterministicBridgeFactory::new());
    let desc = ModelLoadDescriptor::new(
        "gpu-model",
        BackendKind::Cuda,
        CapabilityTier::Tier2Medium,
        1024 * 1024 * 1024,
        1024,
        factory,
    );
    assert_eq!(mgr.load(desc).outcome, LoadOutcome::InsufficientVram);
}

#[test]
fn lifecycle_factory_failure_rolls_back() {
    let (reg, mgr) = manager();
    let factory: Arc<dyn IBridgeFactory> = Arc::new(UnconfiguredBridgeFactory);
    let desc = ModelLoadDescriptor::new(
        "x",
        BackendKind::Cpu,
        CapabilityTier::Tier1Small,
        0,
        1024,
        factory,
    );
    let r = mgr.load(desc);
    assert_eq!(r.outcome, LoadOutcome::FactoryFailed);
    assert_eq!(mgr.list().len(), 0, "reservation rolled back");
    assert!(reg.resolve("x").is_none());
}

#[test]
fn lifecycle_unload() {
    let (_reg, mgr) = manager();
    let factory: Arc<dyn IBridgeFactory> = Arc::new(DeterministicBridgeFactory::new());
    let desc = ModelLoadDescriptor::new(
        "m",
        BackendKind::Cpu,
        CapabilityTier::Tier1Small,
        0,
        512,
        factory,
    );
    mgr.load(desc);
    assert_eq!(mgr.unload("m"), UnloadOutcome::Unloaded);
    assert_eq!(mgr.unload("m"), UnloadOutcome::NotLoaded);
}

// ── Native runtime status ─────────────────────────────────────────────────────

#[test]
fn native_runtime_status_holds_latest() {
    let s = NativeRuntimeStatus::new();
    assert!(s.latest().is_none());
    let paths = NativeRuntimePaths {
        mnn_core_path: "/mnn/MNN.dll".to_string(),
        bridge_path: "/mnn/mnnbridge.dll".to_string(),
        extracted_root: "/mnn".to_string(),
    };
    s.update(paths.clone());
    assert_eq!(s.latest(), Some(paths));
}

// ── Companion resolver ────────────────────────────────────────────────────────

#[test]
fn companion_resolver_caches_and_single_flights() {
    let factory = Arc::new(InMemoryCompanionSessionFactory);
    let resolver = InMemoryCompanionSessionResolver::new(factory);
    assert_eq!(resolver.cached_session_count(), 0);

    let s1 = resolver.resolve("sess-1", "id-1").unwrap();
    let s2 = resolver.resolve("sess-1", "id-1").unwrap();
    assert!(Arc::ptr_eq(&s1, &s2), "same key returns the cached session");
    assert_eq!(resolver.cached_session_count(), 1);

    // Different key → new session.
    resolver.resolve("sess-2", "id-1").unwrap();
    assert_eq!(resolver.cached_session_count(), 2);

    // Blank ids → None.
    assert!(resolver.resolve("", "id").is_none());
    assert!(resolver.resolve("s", "").is_none());
}

#[test]
fn companion_session_generates_and_records_history() {
    let factory = Arc::new(InMemoryCompanionSessionFactory);
    let resolver = InMemoryCompanionSessionResolver::new(factory);
    let session = resolver.resolve("s", "id").unwrap();
    let reply = session.send("hello companion");
    assert!(!reply.is_empty());
    assert_eq!(session.history_len(), 2); // user + assistant
    let agentic = session.agent("do a task");
    assert!(agentic.starts_with("[agent]"));
    assert_eq!(session.history_len(), 4);
}

// ── HTTP handlers (in-memory) ─────────────────────────────────────────────────

fn chat_stack() -> (Arc<InferenceServerModelRegistry>, ChatCompletionsHandler, Arc<ServerCounters>) {
    let reg = Arc::new(InferenceServerModelRegistry::new());
    let counters = Arc::new(ServerCounters::new());
    let admission = Arc::new(AdmissionControl::new(4, counters.clone()));
    let handler = ChatCompletionsHandler::new(reg.clone(), admission, counters.clone());
    (reg, handler, counters)
}

#[test]
fn chat_handler_validates_and_routes() {
    let (reg, handler, _c) = chat_stack();

    // Missing model → 400.
    let bad = ChatCompletionRequest::default();
    match handler.handle(&bad) {
        HandlerResult::Error(code, _) => assert_eq!(code, status::BAD_REQUEST),
        _ => panic!("expected 400"),
    }

    // Model not loaded → 404.
    let req = ChatCompletionRequest {
        model: "qwen".to_string(),
        messages: vec![ChatCompletionMessage {
            role: "user".to_string(),
            content: "hi".to_string(),
            ..ChatCompletionMessage::default()
        }],
        ..ChatCompletionRequest::default()
    };
    match handler.handle(&req) {
        HandlerResult::Error(code, _) => assert_eq!(code, status::NOT_FOUND),
        _ => panic!("expected 404"),
    }

    // Register the model → 200 with a real reply.
    let bridge: Arc<dyn IInferenceBridge> = Arc::new(DeterministicBridge::new(descriptor("qwen"), false));
    reg.register("qwen", bridge);
    match handler.handle(&req) {
        HandlerResult::Json(code, resp) => {
            assert_eq!(code, status::OK);
            assert_eq!(resp.model, "qwen");
            assert_eq!(resp.choices.len(), 1);
            assert!(!resp.choices[0].message.content.is_empty());
            assert_eq!(resp.choices[0].finish_reason, "stop");
            assert!(resp.usage.total_tokens > 0);
        }
        _ => panic!("expected 200"),
    }
}

#[test]
fn chat_handler_streams_frames() {
    let (reg, handler, _c) = chat_stack();
    let bridge: Arc<dyn IInferenceBridge> = Arc::new(DeterministicBridge::new(descriptor("qwen"), false));
    reg.register("qwen", bridge);
    let req = ChatCompletionRequest {
        model: "qwen".to_string(),
        stream: true,
        messages: vec![ChatCompletionMessage {
            role: "user".to_string(),
            content: "stream this".to_string(),
            ..ChatCompletionMessage::default()
        }],
        ..ChatCompletionRequest::default()
    };
    let result = handler.handle_stream(&req).unwrap();
    assert!(result.done);
    // First frame announces the role.
    assert_eq!(result.frames[0].choices[0].delta.role.as_deref(), Some("assistant"));
    // Last frame carries the stop finish reason.
    let last = result.frames.last().unwrap();
    assert_eq!(last.choices[0].finish_reason.as_deref(), Some("stop"));
    // At least one content delta in between.
    assert!(result
        .frames
        .iter()
        .any(|f| f.choices[0].delta.content.is_some()));
}

#[test]
fn admission_cap_returns_503() {
    let reg = Arc::new(InferenceServerModelRegistry::new());
    let bridge: Arc<dyn IInferenceBridge> = Arc::new(DeterministicBridge::new(descriptor("qwen"), false));
    reg.register("qwen", bridge);
    let counters = Arc::new(ServerCounters::new());
    let admission = Arc::new(AdmissionControl::new(1, counters.clone()));
    // Hold the only slot.
    let _held = admission.try_enter().unwrap();

    let handler = ChatCompletionsHandler::new(reg, admission, counters.clone());
    let req = ChatCompletionRequest {
        model: "qwen".to_string(),
        messages: vec![ChatCompletionMessage {
            role: "user".to_string(),
            content: "hi".to_string(),
            ..ChatCompletionMessage::default()
        }],
        ..ChatCompletionRequest::default()
    };
    match handler.handle(&req) {
        HandlerResult::Error(code, _) => assert_eq!(code, status::SERVICE_UNAVAILABLE),
        _ => panic!("expected 503"),
    }
    assert_eq!(counters.rejected_requests(), 1);
}

#[test]
fn companion_handler_routes_turn() {
    let factory = Arc::new(InMemoryCompanionSessionFactory);
    let resolver: Arc<dyn ICompanionSessionResolver> =
        Arc::new(InMemoryCompanionSessionResolver::new(factory));
    let counters = Arc::new(ServerCounters::new());
    let admission = Arc::new(AdmissionControl::new(4, counters));
    let handler = CompanionHandler::new(resolver, admission);

    // Missing field → 400.
    let bad = CompanionTurnRequest::default();
    match handler.handle(&bad) {
        HandlerResult::Error(code, _) => assert_eq!(code, status::BAD_REQUEST),
        _ => panic!("expected 400"),
    }

    let req = CompanionTurnRequest {
        session_id: "s1".to_string(),
        identity_id: "u1".to_string(),
        message: "hello".to_string(),
        ..CompanionTurnRequest::default()
    };
    match handler.handle(&req) {
        HandlerResult::Json(code, resp) => {
            assert_eq!(code, status::OK);
            assert_eq!(resp.session_id, "s1");
            assert!(!resp.reply.is_empty());
            assert_eq!(resp.turn_index, 2);
        }
        _ => panic!("expected 200"),
    }
}

#[test]
fn admin_handler_load_unload_lifecycle() {
    let reg = Arc::new(InferenceServerModelRegistry::new());
    let probe = Arc::new(HostProfile::generous);
    let mgr: Arc<dyn IModelLifecycleManager> =
        Arc::new(ModelLifecycleManager::new(reg.clone(), probe));
    let factory: Arc<dyn IBridgeFactory> = Arc::new(DeterministicBridgeFactory::new());
    let admin = AdminHandler::new(mgr, factory);

    let (code, outcome, _) = admin.load(&circle_ai::inference_server::handlers::AdminLoadRequest {
        model_id: "qwen".to_string(),
        backend: "Cpu".to_string(),
        tier: "Tier1_Small".to_string(),
        vram_required_bytes: 0,
        ram_required_bytes: 1024,
    });
    assert_eq!(code, status::OK);
    assert_eq!(outcome, LoadOutcome::Loaded);
    assert!(reg.resolve("qwen").is_some());

    let life = admin.lifecycle();
    assert_eq!(life.loaded_model_ids, vec!["qwen".to_string()]);
    assert_eq!(life.total_allocated_ram_bytes, 1024);

    let (ucode, uoutcome) = admin.unload("qwen");
    assert_eq!(ucode, status::OK);
    assert_eq!(uoutcome, UnloadOutcome::Unloaded);
}
