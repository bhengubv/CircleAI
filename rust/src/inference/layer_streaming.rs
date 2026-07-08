//! layer_streaming.rs
//!
//! Ported from `CircleAI.Inference/LayerStreamingInference.cs`.
//!
//! (3.3.0) Layer-by-layer streaming inference — the AirLLM idea: load one
//! transformer layer's weights at a time from disk into RAM/VRAM, run forward,
//! save the activations, evict the layer, load the next. Lets a 70B model fit on
//! a 4 GB device at the cost of disk bandwidth per token.
//!
//! The actual MNN/CUDA glue is host-supplied via [`ILayerStreamingRunner`]. This
//! module defines the contract + a null default + the orchestrator + a shard
//! discovery helper (operating over an injected file listing to stay IO-free).

use std::fmt;

/// (3.3.0) One layer's weights packed for streaming.
#[derive(Debug, Clone, PartialEq)]
pub struct LayerWeightShard {
    /// 0-based transformer layer index.
    pub layer_index: i32,
    /// Path on disk to this layer's tensor shard.
    pub weight_shard_path: String,
    /// Size of the shard, for memory accounting.
    pub approx_bytes: i64,
}

impl LayerWeightShard {
    pub fn new(layer_index: i32, weight_shard_path: impl Into<String>, approx_bytes: i64) -> Self {
        Self {
            layer_index,
            weight_shard_path: weight_shard_path.into(),
            approx_bytes,
        }
    }
}

/// (3.3.0) Layer-streaming model plan.
#[derive(Debug, Clone, PartialEq)]
pub struct LayerStreamingPlan {
    pub model_id: String,
    pub total_layers: i32,
    pub shards: Vec<LayerWeightShard>,
    pub approx_parameter_bytes: i64,
}

/// (3.3.0) One layer's hidden-state output after forward.
#[derive(Debug, Clone, PartialEq)]
pub struct LayerActivations {
    pub layer_index: i32,
    pub hidden: Vec<f32>,
}

impl LayerActivations {
    pub fn new(layer_index: i32, hidden: impl Into<Vec<f32>>) -> Self {
        Self {
            layer_index,
            hidden: hidden.into(),
        }
    }
}

/// Error surfaced by the layer-streaming layer.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LayerStreamingError(String);

impl LayerStreamingError {
    fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }
    /// The error message.
    pub fn message(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for LayerStreamingError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for LayerStreamingError {}

/// (3.3.0) Host-supplied per-layer runner (load + forward + evict).
pub trait ILayerStreamingRunner {
    /// Backend identifier (e.g. "null", "airllm-cuda").
    fn backend_id(&self) -> &str;
    /// Whether the backend is available on this host.
    fn is_available(&self) -> bool;

    /// Forward one layer; returns hidden states.
    fn run_layer(
        &self,
        shard: &LayerWeightShard,
        input_hidden: &[f32],
    ) -> Result<LayerActivations, LayerStreamingError>;

    /// Drop the layer from RAM after forward.
    fn evict(&self, layer_index: i32) -> Result<(), LayerStreamingError>;
}

/// (3.3.0) Null runner that errors on use — drop-in default. Mirrors the C#
/// `NullLayerStreamingRunner`.
#[derive(Debug, Default, Clone)]
pub struct NullLayerStreamingRunner;

impl ILayerStreamingRunner for NullLayerStreamingRunner {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn is_available(&self) -> bool {
        false
    }
    fn run_layer(
        &self,
        _shard: &LayerWeightShard,
        _input_hidden: &[f32],
    ) -> Result<LayerActivations, LayerStreamingError> {
        Err(LayerStreamingError::new(
            "No ILayerStreamingRunner is wired. Register one \
             (CircleAI.Inference.Native.AirLlm) to enable layer-streaming.",
        ))
    }
    fn evict(&self, _layer_index: i32) -> Result<(), LayerStreamingError> {
        Ok(())
    }
}

/// (3.3.0) Drives a full forward pass layer by layer. Mirrors the C#
/// `LayerStreamingOrchestrator`.
pub struct LayerStreamingOrchestrator<R: ILayerStreamingRunner> {
    runner: R,
}

impl<R: ILayerStreamingRunner> LayerStreamingOrchestrator<R> {
    /// Constructs the orchestrator over a runner.
    pub fn new(runner: R) -> Self {
        Self { runner }
    }

    /// Access the underlying runner (test helper).
    pub fn runner(&self) -> &R {
        &self.runner
    }

    /// (3.3.0) Stream every layer in `plan`, evicting after each. Returns the
    /// final hidden state. `on_layer_complete` fires after each layer so callers
    /// can update progress. Errors when the plan has no shards.
    pub fn forward(
        &self,
        plan: &LayerStreamingPlan,
        initial_hidden: &[f32],
        mut on_layer_complete: Option<&mut dyn FnMut(&LayerActivations)>,
    ) -> Result<LayerActivations, LayerStreamingError> {
        if plan.shards.is_empty() {
            return Err(LayerStreamingError::new("Plan has no layer shards."));
        }

        let mut hidden: Vec<f32> = initial_hidden.to_vec();
        let mut last: Option<LayerActivations> = None;
        for shard in &plan.shards {
            let act = self.runner.run_layer(shard, &hidden)?;
            hidden = act.hidden.clone();
            if let Some(cb) = on_layer_complete.as_mut() {
                cb(&act);
            }
            self.runner.evict(shard.layer_index)?;
            last = Some(act);
        }
        Ok(last.expect("shards non-empty checked above"))
    }
}

/// (3.3.0) Discover layer shards from a file listing. Mirrors the C#
/// `LayerShardDiscovery.Discover`, but takes the directory's `(path, size)`
/// entries directly (the C# scans the disk) so the parse logic stays IO-free.
///
/// Files named `layer_NNN.<ext>` are recognised: the digits after the first
/// underscore of the stem become the layer index; other files are ignored.
/// Shards are returned sorted ascending by layer index.
pub fn discover_layer_shards(
    model_id: &str,
    files: &[(String, i64)],
) -> Result<LayerStreamingPlan, LayerStreamingError> {
    if model_id.trim().is_empty() {
        return Err(LayerStreamingError::new("modelId required"));
    }

    let mut shards: Vec<LayerWeightShard> = Vec::new();
    let mut total: i64 = 0;
    for (path, size) in files {
        let stem = file_stem(path);
        let underscore = match stem.find('_') {
            Some(i) => i,
            None => continue,
        };
        let index = match stem[underscore + 1..].parse::<i32>() {
            Ok(n) => n,
            Err(_) => continue,
        };
        shards.push(LayerWeightShard::new(index, path.clone(), *size));
        total += *size;
    }

    shards.sort_by_key(|s| s.layer_index);
    let count = shards.len() as i32;
    Ok(LayerStreamingPlan {
        model_id: model_id.to_string(),
        total_layers: count,
        shards,
        approx_parameter_bytes: total,
    })
}

/// Filename without directory or extension — the `Path.GetFileNameWithoutExtension`
/// equivalent. `dir/layer_007.safetensors` → `layer_007`.
fn file_stem(path: &str) -> &str {
    let name = match path.rsplit(['/', '\\']).next() {
        Some(n) => n,
        None => path,
    };
    match name.rfind('.') {
        Some(dot) if dot > 0 => &name[..dot],
        _ => name,
    }
}
