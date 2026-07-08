//! inference_download_train_test.rs
//!
//! Covers the model download service (single-file + bundle), the feedback
//! training queue, the nightly adapter trainer, and layer-streaming.

use circle_ai::inference::download_service::{
    strip_sha_algorithm_prefix, BundleFileSpec, IFileStore, IModelDownloadService,
    InMemoryContentFetcher, InMemoryFileStore, ModelDownloadService,
};
use circle_ai::inference::feedback_queue::{
    IFeedbackTrainingQueue, InMemoryFeedbackTrainingQueue, TrainingSample,
};
use circle_ai::inference::layer_streaming::{
    discover_layer_shards, ILayerStreamingRunner, LayerActivations, LayerStreamingOrchestrator,
    LayerStreamingPlan, LayerWeightShard, NullLayerStreamingRunner,
};
use circle_ai::inference::nightly_trainer::{
    sample_now, ILoRAAdapterManager, InMemoryLoRAAdapterManager, NightlyAdapterTrainer,
    NightlyAdapterTrainerOptions,
};
use circle_ai::memory::multimodal::compute_sha256;
use chrono::Utc;

// ── ModelDownloadService ──────────────────────────────────────────────────────

fn sha_of(bytes: &[u8]) -> String {
    format!("sha256:{}", compute_sha256(bytes))
}

#[test]
fn ensure_model_downloads_verifies_and_caches() {
    let payload = b"weights-bytes".to_vec();
    let url = "https://example.test/model.gguf";
    let fetcher = InMemoryContentFetcher::new().with_url(url, payload.clone());
    let svc = ModelDownloadService::new("/models", InMemoryFileStore::new(), fetcher).unwrap();

    let mut fractions = Vec::new();
    let path = {
        let mut cb = |f: f64| fractions.push(f);
        svc.ensure_model("qwen", url, Some(&sha_of(&payload)), Some(&mut cb))
            .unwrap()
    };
    assert_eq!(path, "/models/qwen.gguf");
    assert!(svc.store().exists(&path));
    assert_eq!(fractions.last().copied(), Some(1.0));

    // Second call is a cache hit (verified) — still returns the path.
    let again = svc.ensure_model("qwen", url, Some(&sha_of(&payload)), None).unwrap();
    assert_eq!(again, path);
    assert!(svc.is_model_cached("qwen").unwrap());
}

#[test]
fn ensure_model_sha_mismatch_errors() {
    let url = "https://example.test/bad.gguf";
    let fetcher = InMemoryContentFetcher::new().with_url(url, b"actual".to_vec());
    let svc = ModelDownloadService::new("/m", InMemoryFileStore::new(), fetcher).unwrap();
    let err = svc
        .ensure_model("bad", url, Some(&sha_of(b"expected-different")), None)
        .unwrap_err();
    assert!(err.message().contains("SHA-256 mismatch"));
    assert!(!svc.is_model_cached("bad").unwrap());
}

#[test]
fn ensure_bundle_uses_primary_then_verifies() {
    let config = b"{\"k\":1}".to_vec();
    let weights = b"llm-weights".to_vec();
    let repo = "MNN/Qwen3-0.6B-MNN";
    // Register the PRIMARY (API-form) URLs the service builds.
    let primary = |name: &str| {
        format!(
            "https://modelscope.cn/api/v1/models/{repo}/repo?Revision=master&FilePath={name}"
        )
    };
    let fetcher = InMemoryContentFetcher::new()
        .with_url(primary("config.json"), config.clone())
        .with_url(primary("llm.mnn"), weights.clone());
    let svc = ModelDownloadService::new("/store", InMemoryFileStore::new(), fetcher).unwrap();

    let files = vec![
        BundleFileSpec::new("config.json", sha_of(&config), config.len() as i64),
        BundleFileSpec::new("llm.mnn", sha_of(&weights), weights.len() as i64),
    ];
    let dir = svc.ensure_bundle("qwen-bundle", repo, &files, None).unwrap();
    assert_eq!(dir, "/store/qwen-bundle");
    assert!(svc.store().exists("/store/qwen-bundle/config.json"));
    assert!(svc.store().exists("/store/qwen-bundle/llm.mnn"));
    assert!(svc.is_model_cached("qwen-bundle").unwrap());
}

#[test]
fn ensure_bundle_falls_back_to_cdn_url() {
    let weights = b"w".to_vec();
    let repo = "MNN/X";
    // Only the FALLBACK (CDN-form) URL is reachable — the primary errors.
    let fallback = format!("https://modelscope.cn/models/{repo}/resolve/master/llm.mnn");
    let fetcher = InMemoryContentFetcher::new().with_url(fallback, weights.clone());
    let svc = ModelDownloadService::new("/s", InMemoryFileStore::new(), fetcher).unwrap();
    let files = vec![BundleFileSpec::new("llm.mnn", sha_of(&weights), weights.len() as i64)];
    let dir = svc.ensure_bundle("x", repo, &files, None).unwrap();
    assert!(svc.store().exists(&format!("{dir}/llm.mnn")));
}

#[test]
fn delete_model_removes_single_and_bundle() {
    let url = "u";
    let fetcher = InMemoryContentFetcher::new().with_url(url, b"a".to_vec());
    let svc = ModelDownloadService::new("/d", InMemoryFileStore::new(), fetcher).unwrap();
    svc.ensure_model("m", url, None, None).unwrap();
    assert!(svc.is_model_cached("m").unwrap());
    svc.delete_model("m").unwrap();
    assert!(!svc.is_model_cached("m").unwrap());
}

#[test]
fn strip_sha_prefix_matches_csharp() {
    assert_eq!(strip_sha_algorithm_prefix("sha256:abc"), "abc");
    assert_eq!(strip_sha_algorithm_prefix("SHA-256: DEF "), "DEF");
    assert_eq!(strip_sha_algorithm_prefix("bareHexNoColon"), "bareHexNoColon");
    // A colon after a long non-alg token is left intact.
    let long = "thisisaverylongprefixover16:tail";
    assert_eq!(strip_sha_algorithm_prefix(long), long);
    assert_eq!(strip_sha_algorithm_prefix(""), "");
}

// ── FeedbackTrainingQueue ─────────────────────────────────────────────────────

#[test]
fn feedback_queue_enqueue_drain_fifo() {
    let q = InMemoryFeedbackTrainingQueue::new();
    assert_eq!(q.pending(), 0);
    for i in 0..5 {
        q.enqueue(TrainingSample::new(
            format!("u{i}"),
            format!("a{i}"),
            format!("p{i}"),
            1,
            Utc::now(),
        ))
        .unwrap();
    }
    assert_eq!(q.pending(), 5);
    let first_two = q.drain(2).unwrap();
    assert_eq!(first_two.len(), 2);
    assert_eq!(first_two[0].user_text, "u0");
    assert_eq!(first_two[1].user_text, "u1");
    assert_eq!(q.pending(), 3, "drained samples are removed");
    // Remaining come out in order.
    let rest = q.drain(10).unwrap();
    assert_eq!(rest.len(), 3);
    assert_eq!(rest[0].user_text, "u2");
    assert_eq!(q.pending(), 0);
}

#[test]
fn feedback_queue_drain_zero_errors() {
    let q = InMemoryFeedbackTrainingQueue::new();
    assert!(q.drain(0).is_err());
    assert!(q.drain(-1).is_err());
}

// ── NightlyAdapterTrainer ─────────────────────────────────────────────────────

fn fill_queue(q: &InMemoryFeedbackTrainingQueue, n: usize) {
    for i in 0..n {
        q.enqueue(sample_now(
            format!("please help with task {i}"),
            "old answer",
            "please help with task correction",
            0,
        ))
        .unwrap();
    }
}

#[test]
fn trainer_skips_below_min_batch() {
    let q = InMemoryFeedbackTrainingQueue::new();
    fill_queue(&q, 3);
    let opts = NightlyAdapterTrainerOptions {
        min_batch_size: 16,
        ..NightlyAdapterTrainerOptions::default()
    };
    let trainer = NightlyAdapterTrainer::new(q, InMemoryLoRAAdapterManager::new(), opts);
    let r = trainer.run_once();
    assert!(r.skipped_below_min);
    assert_eq!(r.steps, 0);
    assert!(!r.adapter_committed);
    // Nothing drained.
    assert_eq!(trainer.queue().pending(), 3);
}

#[test]
fn trainer_trains_saves_and_applies() {
    let q = InMemoryFeedbackTrainingQueue::new();
    fill_queue(&q, 20);
    let opts = NightlyAdapterTrainerOptions {
        min_batch_size: 16,
        max_samples_per_run: 256,
        adapter_path: "adapter.mnn".to_string(),
        ..NightlyAdapterTrainerOptions::default()
    };
    let trainer = NightlyAdapterTrainer::new(q, InMemoryLoRAAdapterManager::new(), opts);
    let r = trainer.run_once();
    assert_eq!(r.steps, 20);
    assert!(r.adapter_committed);
    assert!(r.avg_loss >= 0.0);
    assert_eq!(trainer.adapter().saved_adapters(), vec!["adapter.mnn".to_string()]);
    assert_eq!(trainer.adapter().applied_adapters(), vec!["adapter.mnn".to_string()]);
    assert_eq!(trainer.queue().pending(), 0);
}

#[test]
fn trainer_requeues_when_training_unsupported() {
    let q = InMemoryFeedbackTrainingQueue::new();
    fill_queue(&q, 20);
    let opts = NightlyAdapterTrainerOptions {
        min_batch_size: 16,
        ..NightlyAdapterTrainerOptions::default()
    };
    let trainer =
        NightlyAdapterTrainer::new(q, InMemoryLoRAAdapterManager::without_training(), opts);
    let r = trainer.run_once();
    assert!(r.requeued_unsupported);
    assert_eq!(r.steps, 0);
    assert!(!r.adapter_committed);
    // The whole drained batch was re-queued.
    assert_eq!(trainer.queue().pending(), 20);
}

#[test]
fn trainer_should_fire_gate_respected() {
    use std::sync::Arc;
    let q = InMemoryFeedbackTrainingQueue::new();
    let opts = NightlyAdapterTrainerOptions {
        should_fire_now: Some(Arc::new(|| false)),
        ..NightlyAdapterTrainerOptions::default()
    };
    let trainer = NightlyAdapterTrainer::new(q, InMemoryLoRAAdapterManager::new(), opts);
    assert!(!trainer.should_fire_now());
}

#[test]
fn lora_train_step_validates_and_computes_loss() {
    let mgr = InMemoryLoRAAdapterManager::new();
    assert!(mgr.train_step(&[], &[1], 1e-4, 8).is_err());
    assert!(mgr.train_step(&[1], &[], 1e-4, 8).is_err());
    // Perfect overlap → loss near zero; disjoint → higher loss.
    let low = mgr.train_step(&[1, 2, 3], &[1, 2, 3], 1e-4, 8).unwrap();
    let high = mgr.train_step(&[1, 2, 3], &[9, 8, 7], 1e-4, 8).unwrap();
    assert!(low < high);
    assert_eq!(mgr.step_count(), 2);
}

// ── LayerStreaming ────────────────────────────────────────────────────────────

struct SumRunner;
impl ILayerStreamingRunner for SumRunner {
    fn backend_id(&self) -> &str {
        "sum"
    }
    fn is_available(&self) -> bool {
        true
    }
    fn run_layer(
        &self,
        shard: &LayerWeightShard,
        input_hidden: &[f32],
    ) -> Result<LayerActivations, circle_ai::inference::layer_streaming::LayerStreamingError> {
        // Deterministic transform: add the layer index to every element.
        let out: Vec<f32> = input_hidden
            .iter()
            .map(|x| x + shard.layer_index as f32)
            .collect();
        Ok(LayerActivations::new(shard.layer_index, out))
    }
    fn evict(
        &self,
        _layer_index: i32,
    ) -> Result<(), circle_ai::inference::layer_streaming::LayerStreamingError> {
        Ok(())
    }
}

#[test]
fn layer_orchestrator_runs_layers_in_order() {
    let plan = LayerStreamingPlan {
        model_id: "m".to_string(),
        total_layers: 3,
        shards: vec![
            LayerWeightShard::new(0, "l0", 10),
            LayerWeightShard::new(1, "l1", 10),
            LayerWeightShard::new(2, "l2", 10),
        ],
        approx_parameter_bytes: 30,
    };
    let orch = LayerStreamingOrchestrator::new(SumRunner);
    let mut completed = Vec::new();
    let final_act = {
        let mut cb = |a: &LayerActivations| completed.push(a.layer_index);
        orch.forward(&plan, &[0.0, 0.0], Some(&mut cb)).unwrap()
    };
    assert_eq!(completed, vec![0, 1, 2]);
    // 0 + 0 + 1 + 2 = 3 for each element.
    assert_eq!(final_act.hidden, vec![3.0, 3.0]);
    assert_eq!(final_act.layer_index, 2);
}

#[test]
fn null_runner_errors_and_empty_plan_rejected() {
    let orch = LayerStreamingOrchestrator::new(NullLayerStreamingRunner);
    let empty = LayerStreamingPlan {
        model_id: "m".to_string(),
        total_layers: 0,
        shards: vec![],
        approx_parameter_bytes: 0,
    };
    assert!(orch.forward(&empty, &[], None).is_err());

    // A non-empty plan still errors because the null runner refuses.
    let plan = LayerStreamingPlan {
        model_id: "m".to_string(),
        total_layers: 1,
        shards: vec![LayerWeightShard::new(0, "l0", 1)],
        approx_parameter_bytes: 1,
    };
    assert!(orch.forward(&plan, &[0.0], None).is_err());
    assert!(!NullLayerStreamingRunner.is_available());
}

#[test]
fn discover_shards_parses_and_sorts() {
    let files = vec![
        ("dir/layer_002.safetensors".to_string(), 200i64),
        ("dir/layer_000.safetensors".to_string(), 100i64),
        ("dir/config.json".to_string(), 5i64), // ignored (no digits after '_')
        ("dir/layer_001.bin".to_string(), 150i64),
    ];
    let plan = discover_layer_shards("m", &files).unwrap();
    assert_eq!(plan.total_layers, 3);
    assert_eq!(
        plan.shards.iter().map(|s| s.layer_index).collect::<Vec<_>>(),
        vec![0, 1, 2]
    );
    assert_eq!(plan.approx_parameter_bytes, 450);
    assert!(discover_layer_shards("", &files).is_err());
}
