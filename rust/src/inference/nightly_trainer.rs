//! nightly_trainer.rs
//!
//! Ported from `CircleAI.Inference/NightlyAdapterTrainer.cs` plus the
//! `LoRAAdapterManager` surface from `MnnInteropRtFeatures.cs`.
//!
//! (Phase D3) Periodically drains the [`super::feedback_queue::IFeedbackTrainingQueue`],
//! runs LoRA gradient steps against the current model handle, saves the adapter,
//! and applies it. Idle-and-charging gating is host-supplied via a predicate.
//!
//! The native MNN training seam is injected behind [`ILoRAAdapterManager`]; the
//! background timer loop (`IHostedService.StartAsync` / `Task.Delay`) is host
//! plumbing, so the core drain-and-train pass is exposed as
//! [`NightlyAdapterTrainer::run_once`] — which the C# also exposes publicly for
//! manual triggering. [`InMemoryLoRAAdapterManager`] is a deterministic default
//! that computes a real (token-overlap) loss and records saved/applied adapters.

use std::sync::Arc;
use std::sync::Mutex;

use super::feedback_queue::{IFeedbackTrainingQueue, TrainingSample};

/// Outcome of one [`ILoRAAdapterManager::train_step`].
#[derive(Debug, Clone, PartialEq)]
pub enum TrainStepError {
    /// The native MNN binary was compiled without training support (mirrors the
    /// C# `NotSupportedException` from rc == -12). The trainer re-queues the
    /// batch and bails when it sees this.
    TrainingNotSupported,
    /// Any other training failure (mirrors the C# `InvalidOperationException`).
    Failed(String),
}

/// The LoRA-adapter native surface: run a gradient step, persist, apply.
/// Sync port of the training-relevant `LoRAAdapterManager` members.
pub trait ILoRAAdapterManager {
    /// Run one gradient-descent step on the LoRA adapter weights. Returns the
    /// scalar loss for the batch, or a [`TrainStepError`].
    fn train_step(
        &self,
        input_tokens: &[i32],
        target_tokens: &[i32],
        learning_rate: f32,
        lora_rank: i32,
    ) -> Result<f32, TrainStepError>;

    /// Persist the current LoRA adapter weights to `adapter_path`.
    fn save_adapter(&self, adapter_path: &str) -> Result<(), TrainStepError>;

    /// Apply a previously-saved adapter from `adapter_path` to the live model.
    fn apply(&self, adapter_path: &str) -> Result<(), TrainStepError>;
}

/// Deterministic in-memory [`ILoRAAdapterManager`]. Computes a real loss from
/// the input/target token overlap (lower overlap → higher loss), and records
/// the paths it saved / applied so tests can assert the trainer wired them.
#[derive(Debug, Default)]
pub struct InMemoryLoRAAdapterManager {
    training_supported: bool,
    saved: Mutex<Vec<String>>,
    applied: Mutex<Vec<String>>,
    step_count: Mutex<usize>,
}

impl InMemoryLoRAAdapterManager {
    /// A manager whose native training path is available.
    pub fn new() -> Self {
        Self {
            training_supported: true,
            saved: Mutex::new(Vec::new()),
            applied: Mutex::new(Vec::new()),
            step_count: Mutex::new(0),
        }
    }

    /// A manager that reports training is unavailable (`train_step` returns
    /// [`TrainStepError::TrainingNotSupported`]).
    pub fn without_training() -> Self {
        Self {
            training_supported: false,
            ..Self::new()
        }
    }

    /// Adapter paths passed to [`ILoRAAdapterManager::save_adapter`].
    pub fn saved_adapters(&self) -> Vec<String> {
        self.saved.lock().unwrap().clone()
    }

    /// Adapter paths passed to [`ILoRAAdapterManager::apply`].
    pub fn applied_adapters(&self) -> Vec<String> {
        self.applied.lock().unwrap().clone()
    }

    /// Number of successful training steps run.
    pub fn step_count(&self) -> usize {
        *self.step_count.lock().unwrap()
    }
}

impl ILoRAAdapterManager for InMemoryLoRAAdapterManager {
    fn train_step(
        &self,
        input_tokens: &[i32],
        target_tokens: &[i32],
        learning_rate: f32,
        lora_rank: i32,
    ) -> Result<f32, TrainStepError> {
        if input_tokens.is_empty() {
            return Err(TrainStepError::Failed("inputTokens required".into()));
        }
        if target_tokens.is_empty() {
            return Err(TrainStepError::Failed("targetTokens required".into()));
        }
        if learning_rate <= 0.0 {
            return Err(TrainStepError::Failed("learningRate out of range".into()));
        }
        if lora_rank <= 0 {
            return Err(TrainStepError::Failed("loraRank out of range".into()));
        }
        if !self.training_supported {
            return Err(TrainStepError::TrainingNotSupported);
        }

        // Deterministic loss: 1 - overlap ratio of target tokens present in the
        // input, scaled by learning rate. Real, reproducible, and monotone in
        // how well the target already matches the input.
        let matches = target_tokens
            .iter()
            .filter(|t| input_tokens.contains(t))
            .count();
        let overlap = matches as f32 / target_tokens.len() as f32;
        let loss = (1.0 - overlap) * (1.0 + learning_rate);

        *self.step_count.lock().unwrap() += 1;
        Ok(loss)
    }

    fn save_adapter(&self, adapter_path: &str) -> Result<(), TrainStepError> {
        if adapter_path.trim().is_empty() {
            return Err(TrainStepError::Failed("adapterPath required".into()));
        }
        self.saved.lock().unwrap().push(adapter_path.to_string());
        Ok(())
    }

    fn apply(&self, adapter_path: &str) -> Result<(), TrainStepError> {
        if adapter_path.trim().is_empty() {
            return Err(TrainStepError::Failed("adapterPath required".into()));
        }
        self.applied.lock().unwrap().push(adapter_path.to_string());
        Ok(())
    }
}

/// A tokeniser converting text → int IDs. The default char-level tokeniser maps
/// each UTF-16 code unit to its value, matching the C# `CharTokenizer` fallback.
pub type Tokenizer = Arc<dyn Fn(&str) -> Vec<i32> + Send + Sync>;

/// A gate deciding whether the trainer should fire now (battery / charging /
/// idle), matching the C# `Func<bool> ShouldFireNow`.
pub type ShouldFireNow = Arc<dyn Fn() -> bool + Send + Sync>;

/// Options for the nightly adapter trainer. Mirrors `NightlyAdapterTrainerOptions`.
#[derive(Clone)]
pub struct NightlyAdapterTrainerOptions {
    /// Minimum samples to bother training. Skip otherwise. Default 16.
    pub min_batch_size: i32,
    /// Cap per run so a backlog can't lock the device. Default 256.
    pub max_samples_per_run: i32,
    /// Adam-style LR for the LoRA adapter parameters. Default 1e-4.
    pub learning_rate: f32,
    /// Rank of the LoRA decomposition. Default 8.
    pub lora_rank: i32,
    /// Where to persist the trained adapter file. Default `circleai-lora.mnn`.
    pub adapter_path: String,
    /// Optional gate (battery/charging/idle). `None` = always fire.
    pub should_fire_now: Option<ShouldFireNow>,
    /// Tokeniser. `None` uses the char-level fallback.
    pub tokenizer: Option<Tokenizer>,
}

impl Default for NightlyAdapterTrainerOptions {
    fn default() -> Self {
        Self {
            min_batch_size: 16,
            max_samples_per_run: 256,
            learning_rate: 1e-4,
            lora_rank: 8,
            adapter_path: "circleai-lora.mnn".to_string(),
            should_fire_now: None,
            tokenizer: None,
        }
    }
}

/// Result of one [`NightlyAdapterTrainer::run_once`] pass.
#[derive(Debug, Clone, PartialEq)]
pub struct RunOnceResult {
    /// Number of successful training steps.
    pub steps: usize,
    /// Average loss across the steps (0.0 when `steps == 0`).
    pub avg_loss: f32,
    /// `true` when the batch was skipped because `pending < min_batch_size`.
    pub skipped_below_min: bool,
    /// `true` when training was unavailable and the batch was re-queued.
    pub requeued_unsupported: bool,
    /// `true` when the adapter was saved + applied.
    pub adapter_committed: bool,
}

/// (Phase D3) Drains the feedback queue and trains a LoRA adapter in one pass.
pub struct NightlyAdapterTrainer<Q: IFeedbackTrainingQueue, A: ILoRAAdapterManager> {
    queue: Q,
    adapter: A,
    opts: NightlyAdapterTrainerOptions,
}

impl<Q: IFeedbackTrainingQueue, A: ILoRAAdapterManager> NightlyAdapterTrainer<Q, A> {
    /// Constructs the trainer over a feedback queue + adapter manager.
    pub fn new(queue: Q, adapter: A, opts: NightlyAdapterTrainerOptions) -> Self {
        Self {
            queue,
            adapter,
            opts,
        }
    }

    /// Access the underlying adapter manager (test helper).
    pub fn adapter(&self) -> &A {
        &self.adapter
    }

    /// Access the underlying queue (test helper).
    pub fn queue(&self) -> &Q {
        &self.queue
    }

    /// `true` when the host gate (if any) permits firing now.
    pub fn should_fire_now(&self) -> bool {
        match &self.opts.should_fire_now {
            Some(g) => g(),
            None => true,
        }
    }

    /// (Phase D3) Drain + train in one pass. Reproduces the C# `RunOnceAsync`:
    /// skip when `pending < min_batch_size`; drain up to `max_samples_per_run`;
    /// tokenise input = user text, target = preferred text when polarity >= 0
    /// else assistant text; accumulate loss over train steps; on
    /// [`TrainStepError::TrainingNotSupported`] re-queue the whole batch and
    /// bail; on `steps > 0` save + apply the adapter.
    pub fn run_once(&self) -> RunOnceResult {
        let mut result = RunOnceResult {
            steps: 0,
            avg_loss: 0.0,
            skipped_below_min: false,
            requeued_unsupported: false,
            adapter_committed: false,
        };

        if (self.queue.pending() as i32) < self.opts.min_batch_size {
            result.skipped_below_min = true;
            return result;
        }

        let samples = match self.queue.drain(self.opts.max_samples_per_run) {
            Ok(s) => s,
            Err(_) => return result,
        };
        if samples.is_empty() {
            return result;
        }

        let mut total_loss = 0f32;
        let mut step_count = 0usize;

        for sample in &samples {
            let input = self.tokenize(&sample.user_text);
            let target = if sample.polarity >= 0 {
                self.tokenize(&sample.preferred_text)
            } else {
                self.tokenize(&sample.assistant_text)
            };
            if input.is_empty() || target.is_empty() {
                continue;
            }

            match self.adapter.train_step(
                &input,
                &target,
                self.opts.learning_rate,
                self.opts.lora_rank,
            ) {
                Ok(loss) => {
                    total_loss += loss;
                    step_count += 1;
                }
                Err(TrainStepError::TrainingNotSupported) => {
                    // Native MNN not built with training — re-queue and bail.
                    for s in &samples {
                        let _ = self.queue.enqueue(s.clone());
                    }
                    result.requeued_unsupported = true;
                    return result;
                }
                Err(TrainStepError::Failed(_)) => {
                    // Step failed for this sample — skip it (mirrors the C#
                    // per-sample warn-and-continue).
                }
            }
        }

        if step_count > 0 {
            let saved = self.adapter.save_adapter(&self.opts.adapter_path);
            let applied = self.adapter.apply(&self.opts.adapter_path);
            result.adapter_committed = saved.is_ok() && applied.is_ok();
        }

        result.steps = step_count;
        result.avg_loss = if step_count > 0 {
            total_loss / step_count as f32
        } else {
            0.0
        };
        result
    }

    fn tokenize(&self, text: &str) -> Vec<i32> {
        match &self.opts.tokenizer {
            Some(t) => t(text),
            None => char_tokenizer(text),
        }
    }
}

/// (Phase D3) Char-level tokeniser fallback — every UTF-16 code unit becomes its
/// value, matching the C# `CharTokenizer`.
pub fn char_tokenizer(text: &str) -> Vec<i32> {
    if text.is_empty() {
        return Vec::new();
    }
    text.encode_utf16().map(|u| u as i32).collect()
}

/// Convenience helper to build a re-usable [`TrainingSample`] for tests/hosts.
pub fn sample_now(
    user: impl Into<String>,
    assistant: impl Into<String>,
    preferred: impl Into<String>,
    polarity: i32,
) -> TrainingSample {
    TrainingSample::new(user, assistant, preferred, polarity, chrono::Utc::now())
}
