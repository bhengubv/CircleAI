//! feedback_analyser.rs
//!
//! Analyses a window of [`FeedbackSignal`] records and produces
//! [`PersonaAdaptation`] deltas. Ported from CircleAI.Memory.FeedbackAnalyser
//! (C#) and mirrors the TypeScript reference (memory/feedback_analyser.ts) 1:1.
//!
//! Rules (applied to the most-recent N signals, default N = 20):
//!   - >70% negative signals → verbosity_delta = -0.1
//!   - >70% positive signals → verbosity_delta = +0.05
//!   - formality_delta is always 0 (reserved for future heuristics)
//!   - preferred_topics is always empty — [`FeedbackSignal`] carries no topic tags
//!
//! The C# `PersonaAdaptation` holds `float` deltas. Rust `f32` is native, so the
//! constants (`-0.1f32`, `0.05f32`) reproduce the exact FP32 values the C# record
//! carries — keeping the cross-language fixture contract byte-identical.

use super::stores::{FeedbackPolarity, FeedbackSignal, IFeedbackStore};
use crate::brain::BrainError;
use std::sync::Mutex;

/// FP32 delta constants, matching the C# `float` literals.
const VERBOSITY_DOWN: f32 = -0.1;
const VERBOSITY_UP: f32 = 0.05;

/// Deltas to apply to `PersonaState` after analysing feedback signals.
///
/// Mirrors the C# `PersonaAdaptation` record and the TS `PersonaAdaptation`
/// interface.
#[derive(Debug, Clone, PartialEq)]
pub struct PersonaAdaptation {
    /// Adjustment to persona verbosity (-0.1, 0, or +0.05).
    pub verbosity_delta: f32,
    /// Adjustment to persona formality (always 0 for now).
    pub formality_delta: f32,
    /// Preferred topics extracted from feedback (always empty for now).
    pub preferred_topics: Vec<String>,
}

impl PersonaAdaptation {
    /// The neutral adaptation: no verbosity/formality change, no topics.
    fn zero() -> Self {
        Self {
            verbosity_delta: 0.0,
            formality_delta: 0.0,
            preferred_topics: Vec::new(),
        }
    }
}

/// Analyses recent [`FeedbackSignal`] records and produces
/// [`PersonaAdaptation`] adjustments.
#[derive(Debug, Clone)]
pub struct FeedbackAnalyser {
    window_size: usize,
}

impl Default for FeedbackAnalyser {
    /// Default window of 20 most-recent signals.
    fn default() -> Self {
        Self { window_size: 20 }
    }
}

impl FeedbackAnalyser {
    /// Creates an analyser considering the most-recent `window_size` signals.
    /// `window_size` must be at least 1.
    pub fn new(window_size: usize) -> Result<Self, BrainError> {
        if window_size < 1 {
            return Err(BrainError::new("Window size must be at least 1."));
        }
        Ok(Self { window_size })
    }

    /// Computes persona adaptation from the provided signals.
    ///
    /// `verbosity_delta` is:
    ///   - -0.1  when more than 70% of the window is negative
    ///   - +0.05 when more than 70% of the window is positive
    ///   - 0     otherwise
    ///
    /// `formality_delta` is always 0 and `preferred_topics` is always empty
    /// because [`FeedbackSignal`] carries no topic metadata.
    pub fn analyse<I>(&self, signals: I) -> PersonaAdaptation
    where
        I: IntoIterator<Item = FeedbackSignal>,
    {
        // Most-recent-N by recorded_at descending.
        let mut window: Vec<FeedbackSignal> = signals.into_iter().collect();
        window.sort_by(|a, b| b.recorded_at_utc.cmp(&a.recorded_at_utc));
        window.truncate(self.window_size);

        if window.is_empty() {
            return PersonaAdaptation::zero();
        }

        let positive_count = window
            .iter()
            .filter(|s| s.polarity == FeedbackPolarity::Positive)
            .count();
        let negative_count = window
            .iter()
            .filter(|s| s.polarity == FeedbackPolarity::Negative)
            .count();
        let total = window.len();

        // Ratios computed in f32 to match the C# `(float)count / total`.
        let negative_ratio = negative_count as f32 / total as f32;
        let positive_ratio = positive_count as f32 / total as f32;

        let mut verbosity_delta = 0.0f32;
        if negative_ratio > 0.70 {
            verbosity_delta = VERBOSITY_DOWN;
        } else if positive_ratio > 0.70 {
            verbosity_delta = VERBOSITY_UP;
        }

        PersonaAdaptation {
            verbosity_delta,
            formality_delta: 0.0,
            preferred_topics: Vec::new(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryFeedbackStore — CircleAI.Memory.InMemoryFeedbackStore
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory [`IFeedbackStore`]. Ported from CircleAI.Memory
/// (InMemoryFeedbackStore) and mirrors the TS reference (stores.ts). Data is
/// lost on process exit; for tests and headless CLI use. Capacity is capped
/// (FIFO eviction).
///
/// Interior mutability ([`Mutex`]) lets the same value satisfy both the
/// `&mut self` sync [`IFeedbackStore`] trait and shared `&self` access behind an
/// [`std::sync::Arc`].
#[derive(Debug)]
pub struct InMemoryFeedbackStore {
    max_signals: usize,
    signals: Mutex<Vec<FeedbackSignal>>,
}

impl InMemoryFeedbackStore {
    /// Creates a store capped at `max_signals` (FIFO eviction). Must be positive.
    pub fn new(max_signals: usize) -> Result<Self, BrainError> {
        if max_signals == 0 {
            return Err(BrainError::new("maxSignals must be positive"));
        }
        Ok(Self {
            max_signals,
            signals: Mutex::new(Vec::new()),
        })
    }

    /// Creates a store with the default cap of 10,000.
    pub fn with_default_capacity() -> Self {
        Self {
            max_signals: 10_000,
            signals: Mutex::new(Vec::new()),
        }
    }

    /// Appends a signal, evicting the oldest once the cap is exceeded (FIFO).
    /// Shared-access counterpart of the `&mut self` trait method.
    pub fn add_shared(&self, signal: FeedbackSignal) -> Result<(), BrainError> {
        let mut signals = self.signals.lock().unwrap();
        signals.push(signal);
        while signals.len() > self.max_signals {
            signals.remove(0);
        }
        Ok(())
    }

    /// Returns the most recent `count` signals, newest-first.
    pub fn get_recent_shared(&self, count: usize) -> Result<Vec<FeedbackSignal>, BrainError> {
        let signals = self.signals.lock().unwrap();
        let mut snapshot = signals.clone();
        drop(signals);
        snapshot.sort_by(|a, b| b.recorded_at_utc.cmp(&a.recorded_at_utc));
        snapshot.truncate(count);
        Ok(snapshot)
    }

    /// Returns the number of signals currently stored.
    pub fn count_shared(&self) -> Result<usize, BrainError> {
        Ok(self.signals.lock().unwrap().len())
    }

    /// Fraction of stored signals that are positive, or `None` when empty.
    pub fn positive_ratio_shared(&self) -> Result<Option<f64>, BrainError> {
        let signals = self.signals.lock().unwrap();
        if signals.is_empty() {
            return Ok(None);
        }
        let pos = signals
            .iter()
            .filter(|s| s.polarity == FeedbackPolarity::Positive)
            .count();
        Ok(Some(pos as f64 / signals.len() as f64))
    }

    /// Snapshot of all stored signals (used by the analyser).
    pub fn snapshot(&self) -> Vec<FeedbackSignal> {
        self.signals.lock().unwrap().clone()
    }
}

impl IFeedbackStore for InMemoryFeedbackStore {
    type Error = BrainError;

    fn add(&mut self, signal: FeedbackSignal) -> Result<(), Self::Error> {
        self.add_shared(signal)
    }

    fn get_recent(&self, count: usize) -> Result<Vec<FeedbackSignal>, Self::Error> {
        self.get_recent_shared(count)
    }

    fn count(&self) -> Result<usize, Self::Error> {
        self.count_shared()
    }

    fn positive_ratio(&self) -> Result<Option<f64>, Self::Error> {
        self.positive_ratio_shared()
    }
}
