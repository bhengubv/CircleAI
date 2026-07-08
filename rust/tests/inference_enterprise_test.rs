//! inference_enterprise_test.rs
//!
//! Covers CircleAI.Inference.Server.Enterprise: the round-robin tenant router,
//! in-memory batch scheduler, even-split shard planner, policy cross-tier
//! offload, and the single-node null defaults. Also exercises the embeddings
//! handler (which lives in the server module) against a real embedder.

use std::sync::Arc;

use circle_ai::embeddings::{HashingEmbeddingBackend, ITextEmbedder, TextEmbedder};
use circle_ai::inference_server::enterprise::{
    EvenSplitModelShardPlanner, IBatchScheduler, ICrossTierOffload, IModelShardPlanner,
    ITenantRouter, InMemoryBatchScheduler, NullBatchScheduler, NullCrossTierOffload,
    NullModelShardPlanner, NullTenantRouter, PolicyCrossTierOffload, RoundRobinTenantRouter,
    ServerTier, TenantContext, TenantQuota,
};
use circle_ai::inference_server::handlers::{status, EmbeddingsHandler, HandlerResult};
use circle_ai::inference_server::openai::EmbeddingsRequest;
use circle_ai::inference_server::registry::{
    IInferenceServerModelRegistry, InferenceServerModelRegistry, SharedEmbedder,
};

// ── RoundRobinTenantRouter ────────────────────────────────────────────────────

#[test]
fn tenant_router_round_robins_over_nodes() {
    let router = RoundRobinTenantRouter::new();
    router.register_node("qwen", "node-a");
    router.register_node("qwen", "node-b");
    router.register_node("qwen", "node-a"); // idempotent

    let t = TenantContext::new("tenant-1");
    let picks: Vec<String> = (0..4)
        .map(|_| router.choose_node(&t, "qwen").unwrap())
        .collect();
    assert_eq!(picks, vec!["node-a", "node-b", "node-a", "node-b"]);

    // Unknown model → None.
    assert!(router.choose_node(&t, "unknown").is_none());
    assert_eq!(router.backend_id(), "round-robin");
}

#[test]
fn tenant_router_stores_quota() {
    let router = RoundRobinTenantRouter::new();
    assert!(router.get_quota("t1").is_none());
    let q = TenantQuota {
        tenant_id: "t1".to_string(),
        max_concurrent_requests: 8,
        max_models_loaded: 3,
        max_bytes_in_flight: 1_000_000,
        daily_token_budget: 100_000,
    };
    router.set_quota(q.clone());
    assert_eq!(router.get_quota("t1"), Some(q));
}

// ── InMemoryBatchScheduler ────────────────────────────────────────────────────

#[test]
fn batch_scheduler_reserves_and_releases() {
    let sched = InMemoryBatchScheduler::new();
    let slot = sched.reserve("qwen", 128, chrono::Duration::milliseconds(50));
    assert_eq!(slot.model_id, "qwen");
    assert_eq!(slot.tokens, 128);
    assert!(slot.slot_id.starts_with("slot-"));
    assert_eq!(sched.reserved_count(), 1);
    // A second reservation gets a distinct id.
    let slot2 = sched.reserve("qwen", 64, chrono::Duration::milliseconds(50));
    assert_ne!(slot.slot_id, slot2.slot_id);
    assert_eq!(sched.reserved_count(), 2);
    sched.release(&slot);
    assert_eq!(sched.reserved_count(), 1);
}

// ── EvenSplitModelShardPlanner ────────────────────────────────────────────────

#[test]
fn shard_planner_splits_evenly_with_remainder() {
    let planner = EvenSplitModelShardPlanner::new(|_m| {
        vec!["n0".to_string(), "n1".to_string(), "n2".to_string()]
    });
    // 10 bytes over 3 nodes → 4, 3, 3 (remainder 1 goes to the first).
    let shards = planner.plan("big", 10);
    assert_eq!(shards.len(), 3);
    assert_eq!((shards[0].range_start, shards[0].range_end), (0, 4));
    assert_eq!((shards[1].range_start, shards[1].range_end), (4, 7));
    assert_eq!((shards[2].range_start, shards[2].range_end), (7, 10));
    assert_eq!(shards[0].node_id, "n0");
    assert_eq!(shards[2].shard_id, "shard-big-2");

    // No nodes → empty plan.
    let empty = EvenSplitModelShardPlanner::new(|_m| Vec::new());
    assert!(empty.plan("m", 100).is_empty());
}

// ── PolicyCrossTierOffload ────────────────────────────────────────────────────

#[test]
fn cross_tier_offload_policy() {
    let offload = PolicyCrossTierOffload::new(2048, Some("farm-node".to_string()));

    // Under the ceiling → stay local.
    let d1 = offload.should_offload("qwen", 1000, ServerTier::SingleNode);
    assert!(!d1.should_offload);
    assert_eq!(d1.reason.as_deref(), Some("Prompt fits locally"));

    // Over the ceiling → offload to the farm node.
    let d2 = offload.should_offload("qwen", 5000, ServerTier::Server);
    assert!(d2.should_offload);
    assert_eq!(d2.target_node_id.as_deref(), Some("farm-node"));

    // Farm-tier caller never offloads.
    let d3 = offload.should_offload("qwen", 5000, ServerTier::ServerFarm);
    assert!(!d3.should_offload);
    assert_eq!(d3.reason.as_deref(), Some("Caller is already top-tier"));
}

// ── Null defaults ─────────────────────────────────────────────────────────────

#[test]
fn null_defaults_are_single_node() {
    let t = TenantContext::new("t");
    assert!(NullTenantRouter.choose_node(&t, "m").is_none());
    assert_eq!(NullTenantRouter.backend_id(), "null");

    let slot = NullBatchScheduler.reserve("m", 10, chrono::Duration::seconds(1));
    assert_eq!(slot.model_id, "m");

    assert!(NullModelShardPlanner.plan("m", 100).is_empty());

    let d = NullCrossTierOffload.should_offload("m", 999_999, ServerTier::SingleNode);
    assert!(!d.should_offload);
    assert!(d.reason.unwrap().contains("Local execution"));
}

// ── EmbeddingsHandler ─────────────────────────────────────────────────────────

fn embedder() -> SharedEmbedder {
    // A real hashing embedder wrapped so the handler can call generate().
    struct BackendEmbedder(HashingEmbeddingBackend);
    impl ITextEmbedder for BackendEmbedder {
        fn generate(&self, text: &str) -> Result<Vec<f32>, circle_ai::embeddings::EmbedderError> {
            use circle_ai::embeddings::IEmbeddingBackend;
            self.0.embed(text)
        }
    }
    Arc::new(BackendEmbedder(HashingEmbeddingBackend::new(16)))
}

#[test]
fn embeddings_handler_embeds_single_and_array_input() {
    let reg = Arc::new(InferenceServerModelRegistry::new());
    reg.register_embedder("embed-model", embedder());
    let handler = EmbeddingsHandler::new(reg);

    // Single-string input.
    let req = EmbeddingsRequest {
        model: "embed-model".to_string(),
        input: serde_json::json!("hello world"),
        user: None,
    };
    match handler.handle(&req) {
        HandlerResult::Json(code, resp) => {
            assert_eq!(code, status::OK);
            assert_eq!(resp.data.len(), 1);
            assert_eq!(resp.data[0].embedding.len(), 16);
            assert!(resp.usage.total_tokens > 0);
        }
        _ => panic!("expected 200"),
    }

    // Array input → one datum per element with ascending index.
    let req2 = EmbeddingsRequest {
        model: "embed-model".to_string(),
        input: serde_json::json!(["a", "bb", "ccc"]),
        user: None,
    };
    match handler.handle(&req2) {
        HandlerResult::Json(_, resp) => {
            assert_eq!(resp.data.len(), 3);
            assert_eq!(resp.data[2].index, 2);
        }
        _ => panic!("expected 200"),
    }
}

#[test]
fn embeddings_handler_errors_for_unknown_model() {
    let reg = Arc::new(InferenceServerModelRegistry::new());
    let handler = EmbeddingsHandler::new(reg);
    let req = EmbeddingsRequest {
        model: "missing".to_string(),
        input: serde_json::json!("x"),
        user: None,
    };
    match handler.handle(&req) {
        HandlerResult::Error(code, _) => assert_eq!(code, status::NOT_FOUND),
        _ => panic!("expected 404"),
    }
}

// Silence unused import for TextEmbedder (referenced only for its trait path).
#[allow(unused_imports)]
use TextEmbedder as _TextEmbedder;
