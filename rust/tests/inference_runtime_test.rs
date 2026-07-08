//! inference_runtime_test.rs
//!
//! Covers the CircleAI.Inference runtime gaps: the deterministic chat generator,
//! context-window budget manager, prefix cache, KV compression + power-budget
//! policy, capability flags, and the Qwen prompt builder.

use circle_ai::inference::capability::{ChatCapability, VisionInput};
use circle_ai::inference::chat_generator::{
    build_qwen_chat_prompt, default_stop_sequences, extract_system_prompt, DeterministicChatGenerator,
    FinishReason,
};
use circle_ai::inference::context_budget::ContextWindowBudgetManager;
use circle_ai::inference::kv_compression::{
    InMemoryKvCompressionHandle, KvCompressionApplyResult, KvCompressionMode, MnnKvCompression,
    PowerBudgetPolicy,
};
use circle_ai::inference::prefix_cache::PrefixCacheService;
use circle_ai::inference::{GenerationOptions, IChatGenerator, PowerBudget};
use circle_ai::models::ChatMessage;

// ── DeterministicChatGenerator ────────────────────────────────────────────────

#[test]
fn generator_is_deterministic_for_same_seed() {
    let gen = DeterministicChatGenerator::new("qwen3-0.6b");
    let msgs = [ChatMessage::user("plan a weekend trip to the coast")];
    let opts = GenerationOptions::default().with_seed(7);

    let a = gen.generate(&msgs, Some(&opts)).unwrap();
    let b = gen.generate(&msgs, Some(&opts)).unwrap();
    assert_eq!(a, b, "same seed must produce byte-identical output");
    assert!(!a.is_empty());
    // Reply is composed from the input (not a canned constant).
    assert!(a.contains("coast") || a.contains("weekend") || a.contains("trip"));
}

#[test]
fn generator_different_seed_changes_word_order() {
    let gen = DeterministicChatGenerator::new("qwen3-0.6b");
    let msgs = [ChatMessage::user("alpha beta gamma delta epsilon")];
    let a = gen.generate(&msgs, Some(&GenerationOptions::default().with_seed(0))).unwrap();
    let b = gen.generate(&msgs, Some(&GenerationOptions::default().with_seed(2))).unwrap();
    assert_ne!(a, b, "different seeds rotate the salient-word order");
}

#[test]
fn generator_max_tokens_yields_length_finish() {
    let gen = DeterministicChatGenerator::new("qwen3-0.6b");
    let msgs = [ChatMessage::user("one two three four five six seven eight nine ten")];
    // Budget None honours max_tokens literally; cap to 3 words.
    let opts = GenerationOptions {
        max_tokens: 3,
        budget: PowerBudget::None,
        ..GenerationOptions::default()
    };
    let resp = gen.generate_response(&msgs, Some(&opts));
    assert_eq!(resp.finish_reason, FinishReason::Length);
    assert_eq!(resp.text.split_whitespace().count(), 3);
}

#[test]
fn generator_stop_sequence_truncates_and_reports_stop() {
    let gen = DeterministicChatGenerator::new("qwen3-0.6b");
    let msgs = [ChatMessage::user("hello world")];
    let opts = GenerationOptions {
        stop_sequences: Some(vec![",".to_string()]),
        budget: PowerBudget::None,
        ..GenerationOptions::default()
    };
    let resp = gen.generate_response(&msgs, Some(&opts));
    // "Regarding …, here is a considered reply." — truncated at the first comma.
    assert!(!resp.text.contains(','));
    assert_eq!(resp.finish_reason, FinishReason::Stop);
}

#[test]
fn reasoning_generator_populates_reasoning_channel() {
    let gen = DeterministicChatGenerator::reasoning("qwen3-reasoning");
    let msgs = [ChatMessage::user("why is the sky blue")];
    let resp = gen.generate_response(&msgs, Some(&GenerationOptions::default()));
    assert!(resp.reasoning_content.is_some());
    assert!(!resp.reasoning_content.unwrap().is_empty());
    // Content channel never carries the reasoning text.
    assert!(!resp.text.contains("word(s)"));
}

#[test]
fn include_reasoning_false_drops_reasoning() {
    let gen = DeterministicChatGenerator::reasoning("qwen3-reasoning");
    let msgs = [ChatMessage::user("explain gravity")];
    let opts = GenerationOptions {
        include_reasoning: false,
        ..GenerationOptions::default()
    };
    let resp = gen.generate_response(&msgs, Some(&opts));
    assert!(resp.reasoning_content.is_none());
}

#[test]
fn stream_fragments_orders_reasoning_before_content() {
    let gen = DeterministicChatGenerator::reasoning("qwen3-reasoning");
    let msgs = [ChatMessage::user("summarise the plan")];
    let frags: Vec<_> = gen
        .stream_fragments(&msgs, Some(&GenerationOptions::default()))
        .unwrap()
        .map(|f| f.unwrap())
        .collect();
    use circle_ai::models_v15::ChatFragmentKind;
    let first_content = frags
        .iter()
        .position(|f| f.kind == ChatFragmentKind::Content)
        .unwrap();
    let last_reasoning = frags
        .iter()
        .rposition(|f| f.kind == ChatFragmentKind::Reasoning)
        .unwrap();
    assert!(last_reasoning < first_content, "reasoning precedes content");
}

#[test]
fn stream_concatenates_to_full_reply() {
    let gen = DeterministicChatGenerator::new("qwen3-0.6b");
    let msgs = [ChatMessage::user("compose a short greeting")];
    let full = gen.generate(&msgs, Some(&GenerationOptions::default())).unwrap();
    let streamed: String = gen
        .stream(&msgs, Some(&GenerationOptions::default()))
        .unwrap()
        .map(|c| c.unwrap())
        .collect();
    assert_eq!(streamed, full);
}

#[test]
fn save_then_load_session_round_trips() {
    let gen = DeterministicChatGenerator::new("qwen3-0.6b");
    assert!(!gen.load_session("s.bin").unwrap(), "no marker yet");
    assert!(gen.save_session("s.bin").unwrap());
    assert!(gen.load_session("s.bin").unwrap());
    assert!(!gen.save_session("").unwrap(), "blank path is a no-op false");
}

// ── ContextWindowBudgetManager ────────────────────────────────────────────────

#[test]
fn budget_tracks_usage_and_eviction() {
    let mut m = ContextWindowBudgetManager::new(1000).unwrap();
    assert_eq!(m.remaining_tokens(), 1000);
    assert!(!m.should_evict());
    m.record_exchange(400, 200).unwrap();
    assert_eq!(m.used_tokens(), 600);
    assert_eq!(m.remaining_tokens(), 400);
    assert!((m.fill_ratio() - 0.6).abs() < 1e-9);
    assert!(!m.should_evict());
    m.record_exchange(300, 0).unwrap();
    assert_eq!(m.used_tokens(), 900);
    assert!(m.should_evict(), "0.90 >= 0.85 threshold");
}

#[test]
fn budget_eviction_count_targets_fill_ratio() {
    let mut m = ContextWindowBudgetManager::new(1000).unwrap();
    m.record_exchange(900, 0).unwrap();
    // Target 0.50 → keep 500 → evict 400.
    assert_eq!(m.calculate_eviction_count_default().unwrap(), 400);
    // Already below target returns 0.
    m.reset();
    m.record_exchange(100, 0).unwrap();
    assert_eq!(m.calculate_eviction_count(0.5).unwrap(), 0);
}

#[test]
fn budget_rejects_bad_construction() {
    assert!(ContextWindowBudgetManager::new(0).is_err());
    assert!(ContextWindowBudgetManager::with_threshold(100, 1.5).is_err());
    let mut m = ContextWindowBudgetManager::new(100).unwrap();
    assert!(m.record_exchange(-1, 0).is_err());
}

// ── PrefixCacheService ────────────────────────────────────────────────────────

#[test]
fn prefix_key_is_deterministic_and_gated() {
    let k1 = PrefixCacheService::key_for("model-a", Some("you are helpful"));
    let k2 = PrefixCacheService::key_for("model-a", Some("you are helpful"));
    assert_eq!(k1, k2);
    let k = k1.unwrap();
    // Two 16-hex components joined by '_'.
    assert_eq!(k.len(), 33);
    assert_eq!(k.matches('_').count(), 1);
    // No system prompt → None.
    assert!(PrefixCacheService::key_for("model-a", None).is_none());
    assert!(PrefixCacheService::key_for("model-a", Some("")).is_none());
    assert!(PrefixCacheService::key_for("  ", Some("x")).is_none());
    // Different model or system → different key.
    assert_ne!(
        PrefixCacheService::key_for("model-b", Some("you are helpful")),
        Some(k)
    );
}

#[test]
fn prefix_cache_save_load_touch() {
    let mut cache = PrefixCacheService::new();
    let key = PrefixCacheService::key_for("m", Some("sys")).unwrap();
    assert!(!cache.has_entry(&key));
    cache.save(&key, vec![1u8, 2, 3]);
    assert!(cache.has_entry(&key));
    assert_eq!(cache.load(&key), Some(vec![1u8, 2, 3]));
    assert_eq!(cache.load("missing"), None);
    assert_eq!(cache.total_bytes(), 3);
}

// ── KV compression + PowerBudgetPolicy ────────────────────────────────────────

#[test]
fn kv_compression_apply_maps_status_codes() {
    let mut handle = InMemoryKvCompressionHandle::new();
    assert_eq!(
        MnnKvCompression::set(&mut handle, KvCompressionMode::TurboQuant4Bit),
        KvCompressionApplyResult::Applied
    );
    assert_eq!(
        MnnKvCompression::get(&handle),
        KvCompressionMode::TurboQuant4Bit
    );
    // Invalid handle path.
    let mut invalid = InMemoryKvCompressionHandle::invalid();
    assert_eq!(
        MnnKvCompression::set(&mut invalid, KvCompressionMode::Off),
        KvCompressionApplyResult::HandleInvalid
    );
    assert_eq!(MnnKvCompression::get(&invalid), KvCompressionMode::Off);
}

#[test]
fn kv_mode_from_raw_clamps_out_of_range() {
    assert_eq!(KvCompressionMode::from_raw(3), KvCompressionMode::TurboQuant2Bit);
    assert_eq!(KvCompressionMode::from_raw(99), KvCompressionMode::Off);
    assert_eq!(KvCompressionMode::from_raw(-1), KvCompressionMode::Off);
}

#[test]
fn power_budget_policy_matches_csharp_mapping() {
    // Low caps at 64 + prefers smaller model.
    let low = PowerBudgetPolicy::resolve(PowerBudget::Low, 1000);
    assert_eq!(low.max_tokens, 64);
    assert!(low.prefer_smaller_model_in_chain);
    assert_eq!(low.preferred_kv_mode, KvCompressionMode::TurboQuant4Bit);

    // Normal caps at 512.
    assert_eq!(PowerBudgetPolicy::resolve(PowerBudget::Normal, 1000).max_tokens, 512);

    // High caps at 2048 with FP16 (Off) KV.
    let high = PowerBudgetPolicy::resolve(PowerBudget::High, 5000);
    assert_eq!(high.max_tokens, 2048);
    assert_eq!(high.preferred_kv_mode, KvCompressionMode::Off);

    // None honours the request literally.
    assert_eq!(PowerBudgetPolicy::resolve(PowerBudget::None, 4000).max_tokens, 4000);
}

#[test]
fn power_budget_auto_downgrades_on_device_state() {
    // Normal below 15% battery → Low (64 cap).
    let r = PowerBudgetPolicy::resolve_with_state(PowerBudget::Normal, 1000, Some(10), false);
    assert_eq!(r.max_tokens, 64);
    assert!(r.prefer_smaller_model_in_chain);

    // High + thermal throttle → Normal (512 cap, TQ4).
    let r2 = PowerBudgetPolicy::resolve_with_state(PowerBudget::High, 1000, None, true);
    assert_eq!(r2.max_tokens, 512);
    assert_eq!(r2.preferred_kv_mode, KvCompressionMode::TurboQuant4Bit);
}

// ── ChatCapability + VisionInput + prompt builder ─────────────────────────────

#[test]
fn chat_capability_flags_compose() {
    let caps = ChatCapability::VISION | ChatCapability::TOOLS;
    assert!(caps.contains(ChatCapability::VISION));
    assert!(caps.contains(ChatCapability::TOOLS));
    assert!(!caps.contains(ChatCapability::VIDEO));
    assert_eq!(ChatCapability::default(), ChatCapability::NONE);
}

#[test]
fn vision_input_holds_bytes_and_mime() {
    let v = VisionInput::new(vec![0xFF, 0xD8]).with_mime_type("image/jpeg");
    assert_eq!(v.image_bytes, vec![0xFF, 0xD8]);
    assert_eq!(v.mime_type.as_deref(), Some("image/jpeg"));
}

#[test]
fn qwen_prompt_builder_matches_chatml_shape() {
    let msgs = [
        ChatMessage::system("be terse"),
        ChatMessage::user("hi"),
    ];
    let prompt = build_qwen_chat_prompt(&msgs);
    assert!(prompt.starts_with("<|im_start|>system\nbe terse\n<|im_end|>\n"));
    assert!(prompt.contains("<|im_start|>user\nhi\n<|im_end|>\n"));
    assert!(prompt.ends_with("<|im_start|>assistant\n"));
    assert_eq!(extract_system_prompt(&msgs), Some("be terse"));
    assert_eq!(default_stop_sequences().len(), 3);
}
