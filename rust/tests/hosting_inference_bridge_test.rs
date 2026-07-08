//! hosting_inference_bridge_test.rs
//!
//! Verifies LocalProcessInferenceBridge (status classification, reasoning
//! passthrough, wrong-model failure, stream fallback) and MockInferenceBridge.
//! Mirrors the C# InferenceBridge reference + mock.

use circle_ai::hosting_inference_bridge::{
    DeviceCapabilities, FakeBridgeGenerator, FixedCapabilityProbe, IInferenceBridge,
    InferenceRequest, InferenceStatus, LocalProcessInferenceBridge, MockInferenceBridge,
    ModelDescriptor, ModelFormat,
};

fn descriptor(model_id: &str) -> ModelDescriptor {
    ModelDescriptor {
        model_id: model_id.to_string(),
        version: "1.0".to_string(),
        format: ModelFormat::Gguf,
        context_window_tokens: 4096,
        vocab_size: 32000,
        parameter_count: 8_000_000_000,
        quantisation_label: Some("Q4_K_M".to_string()),
        approximate_memory_bytes: 5_000_000_000,
    }
}

fn probe() -> Box<FixedCapabilityProbe> {
    Box::new(FixedCapabilityProbe(DeviceCapabilities {
        os_name: "Linux".to_string(),
        os_version: "6.1".to_string(),
        physical_memory_bytes: 16 * 1024 * 1024 * 1024,
        cpu_core_count: 8,
        has_gpu: true,
        gpu_name: Some("RTX".to_string()),
        gpu_memory_bytes: Some(8 * 1024 * 1024 * 1024),
        has_npu: false,
        npu_name: None,
        has_transport_layer_encryption: false, // bridge should force true
    }))
}

#[test]
fn local_bridge_lists_and_reports_loaded_model() {
    let bridge = LocalProcessInferenceBridge::new(
        Box::new(FakeBridgeGenerator::new("hello")),
        descriptor("qwen3-8b"),
        probe(),
    );
    assert_eq!(bridge.list_loaded_models().len(), 1);
    assert!(bridge.is_model_loaded("qwen3-8b"));
    assert!(!bridge.is_model_loaded("other"));
}

#[test]
fn local_bridge_completes_and_classifies_completed() {
    let bridge = LocalProcessInferenceBridge::new(
        Box::new(FakeBridgeGenerator::new("short reply")),
        descriptor("qwen3-8b"),
        probe(),
    );
    let req = InferenceRequest::create("qwen3-8b", "hi");
    let resp = bridge.complete(&req);
    assert_eq!(resp.status, InferenceStatus::Completed);
    assert_eq!(resp.output_text, "short reply");
    assert!(resp.output_token_count >= 1);
    assert_eq!(resp.request_id, req.id);
}

#[test]
fn local_bridge_classifies_stopped_by_length() {
    // Small max_output_tokens so the produced count meets/exceeds it.
    let long = "word ".repeat(20); // ~25 tokens by the /4 heuristic
    let bridge = LocalProcessInferenceBridge::new(
        Box::new(FakeBridgeGenerator::new(long)),
        descriptor("m"),
        probe(),
    );
    let req = InferenceRequest::create_with("m", "hi", 3, 0.7, 0.95);
    let resp = bridge.complete(&req);
    assert_eq!(resp.status, InferenceStatus::StoppedByLength);
}

#[test]
fn local_bridge_classifies_stopped_by_token() {
    let bridge = LocalProcessInferenceBridge::new(
        Box::new(FakeBridgeGenerator::new("answer <END> trailing")),
        descriptor("m"),
        probe(),
    );
    let mut req = InferenceRequest::create("m", "hi");
    req.stop_sequences = vec!["<END>".to_string()];
    let resp = bridge.complete(&req);
    assert_eq!(resp.status, InferenceStatus::StoppedByToken);
}

#[test]
fn local_bridge_wrong_model_fails() {
    let bridge = LocalProcessInferenceBridge::new(
        Box::new(FakeBridgeGenerator::new("x")),
        descriptor("qwen3-8b"),
        probe(),
    );
    let req = InferenceRequest::create("some-other-model", "hi");
    let resp = bridge.complete(&req);
    assert_eq!(resp.status, InferenceStatus::Failed);
    assert!(resp.failure_message.unwrap().contains("not loaded"));
}

#[test]
fn local_bridge_passes_reasoning_through() {
    let bridge = LocalProcessInferenceBridge::new(
        Box::new(FakeBridgeGenerator::new("final").with_reasoning("let me think")),
        descriptor("m"),
        probe(),
    );
    let resp = bridge.complete(&InferenceRequest::create("m", "hi"));
    assert_eq!(resp.reasoning_text.as_deref(), Some("let me think"));
}

#[test]
fn local_bridge_stream_falls_back_to_full_completion() {
    // Generator with no chunks → bridge yields the full completion in one chunk.
    let bridge = LocalProcessInferenceBridge::new(
        Box::new(FakeBridgeGenerator::new("the whole thing")),
        descriptor("m"),
        probe(),
    );
    let chunks = bridge.stream_completion(&InferenceRequest::create("m", "hi"));
    assert_eq!(chunks, vec!["the whole thing".to_string()]);
}

#[test]
fn local_bridge_streams_chunks_when_available() {
    let bridge = LocalProcessInferenceBridge::new(
        Box::new(FakeBridgeGenerator::new("full").with_chunks(vec!["a".into(), "b".into()])),
        descriptor("m"),
        probe(),
    );
    let chunks = bridge.stream_completion(&InferenceRequest::create("m", "hi"));
    assert_eq!(chunks, vec!["a".to_string(), "b".to_string()]);
}

#[test]
fn local_bridge_forces_transport_encryption_true() {
    let bridge = LocalProcessInferenceBridge::new(
        Box::new(FakeBridgeGenerator::new("x")),
        descriptor("m"),
        probe(),
    );
    let caps = bridge.get_device_capabilities();
    assert!(caps.has_transport_layer_encryption);
    assert_eq!(caps.os_name, "Linux");
}

#[test]
fn mock_bridge_returns_canned_output() {
    let mock = MockInferenceBridge::new("canned", None);
    assert_eq!(mock.descriptor().model_id, "mock-model");
    let resp = mock.complete(&InferenceRequest::create("mock-model", "hi"));
    assert_eq!(resp.output_text, "canned");
    assert_eq!(resp.status, InferenceStatus::Completed);
    assert_eq!(mock.stream_completion(&InferenceRequest::create("mock-model", "hi")), vec!["canned".to_string()]);
    let caps = mock.get_device_capabilities();
    assert_eq!(caps.os_name, "Mock");
    assert!(caps.has_transport_layer_encryption);
}

#[test]
fn request_create_defaults() {
    let req = InferenceRequest::create("m", "prompt");
    assert_eq!(req.max_output_tokens, 256);
    assert!((req.temperature - 0.7).abs() < 1e-6);
    assert!((req.top_p - 0.95).abs() < 1e-6);
    assert!(req.stop_sequences.is_empty());
}
