//! feedback_queue.rs
//!
//! Ported from `CircleAI.Inference/FeedbackTrainingQueue.cs`.
//!
//! (Phase D2) Append-only queue of user feedback signals that the
//! [`super::nightly_trainer::NightlyAdapterTrainer`] drains into LoRA training
//! batches.
//!
//! The C# queue is disk-backed (one JSON line per sample) so it survives process
//! restarts. Per the no-real-IO porting brief, this Rust port is an in-memory
//! [`InMemoryFeedbackTrainingQueue`] with byte-identical semantics: append-only
//! enqueue, FIFO drain of at most N samples (removing exactly those N), and a
//! `pending` count. Serialisation round-trips through JSON so a disk-backed host
//! can swap in a file writer behind the same [`IFeedbackTrainingQueue`] trait.

use std::collections::VecDeque;
use std::fmt;
use std::sync::Mutex;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

/// (Phase D2) One feedback-tagged turn that will inform fine-tuning.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TrainingSample {
    /// What the user said.
    #[serde(rename = "UserText")]
    pub user_text: String,
    /// What we replied (the "current" answer).
    #[serde(rename = "AssistantText")]
    pub assistant_text: String,
    /// User's correction or the accepted form. Falls back to `assistant_text`
    /// for thumbs-up.
    #[serde(rename = "PreferredText")]
    pub preferred_text: String,
    /// +1 (positive) / -1 (negative) / 0 (correction).
    #[serde(rename = "Polarity")]
    pub polarity: i32,
    /// When the feedback was given.
    #[serde(rename = "AtUtc")]
    pub at_utc: DateTime<Utc>,
}

impl TrainingSample {
    pub fn new(
        user_text: impl Into<String>,
        assistant_text: impl Into<String>,
        preferred_text: impl Into<String>,
        polarity: i32,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            user_text: user_text.into(),
            assistant_text: assistant_text.into(),
            preferred_text: preferred_text.into(),
            polarity,
            at_utc,
        }
    }
}

/// Error returned on invalid queue operations (mirrors the C# argument throws).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FeedbackQueueError(String);

impl FeedbackQueueError {
    fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }
    /// The error message.
    pub fn message(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for FeedbackQueueError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for FeedbackQueueError {}

/// Append-only queue of training samples. Sync port of `IFeedbackTrainingQueue`.
pub trait IFeedbackTrainingQueue {
    /// Append one sample to the tail of the queue.
    fn enqueue(&self, sample: TrainingSample) -> Result<(), FeedbackQueueError>;

    /// Remove and return up to `max_samples` samples from the head (FIFO).
    /// The remaining samples stay queued in order. Returns an error when
    /// `max_samples <= 0` (mirrors the C# `ArgumentOutOfRangeException`).
    fn drain(&self, max_samples: i32) -> Result<Vec<TrainingSample>, FeedbackQueueError>;

    /// Number of samples currently queued.
    fn pending(&self) -> usize;
}

/// (Phase D2) In-memory append-only queue. JSON-serialises each sample on
/// enqueue and deserialises on drain, matching the C# line-delimited-JSON
/// round-trip so malformed payloads would be skippable the same way.
#[derive(Debug, Default)]
pub struct InMemoryFeedbackTrainingQueue {
    lines: Mutex<VecDeque<String>>,
}

impl InMemoryFeedbackTrainingQueue {
    /// Creates an empty queue.
    pub fn new() -> Self {
        Self {
            lines: Mutex::new(VecDeque::new()),
        }
    }
}

impl IFeedbackTrainingQueue for InMemoryFeedbackTrainingQueue {
    fn enqueue(&self, sample: TrainingSample) -> Result<(), FeedbackQueueError> {
        let line = serde_json::to_string(&sample)
            .map_err(|e| FeedbackQueueError::new(format!("serialize failed: {e}")))?;
        self.lines.lock().unwrap().push_back(line);
        Ok(())
    }

    fn drain(&self, max_samples: i32) -> Result<Vec<TrainingSample>, FeedbackQueueError> {
        if max_samples <= 0 {
            return Err(FeedbackQueueError::new("max_samples must be > 0"));
        }
        let mut guard = self.lines.lock().unwrap();
        let take = (max_samples as usize).min(guard.len());
        let mut taken = Vec::with_capacity(take);
        for _ in 0..take {
            if let Some(line) = guard.pop_front() {
                // A malformed line is skipped (mirrors the C# try/catch) — it is
                // still consumed so it can't wedge the head of the queue.
                if let Ok(sample) = serde_json::from_str::<TrainingSample>(&line) {
                    taken.push(sample);
                }
            }
        }
        Ok(taken)
    }

    fn pending(&self) -> usize {
        self.lines.lock().unwrap().len()
    }
}
