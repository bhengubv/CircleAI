//! context_budget.rs
//!
//! Faithful port of `CircleAI.Inference/ContextWindowBudgetManager.cs`.
//!
//! Tracks token usage against a fixed context window and signals when the KV
//! cache should be partially evicted to keep inference latency manageable.

use std::fmt;

/// Error returned by [`ContextWindowBudgetManager`] on out-of-range input,
/// mirroring the C# `ArgumentOutOfRangeException` throws.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BudgetError(String);

impl BudgetError {
    fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }
    /// The error message.
    pub fn message(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for BudgetError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for BudgetError {}

/// Tracks token usage against a fixed context window and signals when the KV
/// cache should be partially evicted.
#[derive(Debug, Clone)]
pub struct ContextWindowBudgetManager {
    context_size: i32,
    used_tokens: i32,
    eviction_threshold: f64,
}

impl ContextWindowBudgetManager {
    /// Initialises a new budget manager with the default eviction threshold
    /// (0.85). Returns an error if `context_size <= 0`.
    pub fn new(context_size: i32) -> Result<Self, BudgetError> {
        Self::with_threshold(context_size, 0.85)
    }

    /// Initialises a new budget manager.
    ///
    /// * `context_size` — total context window size in tokens. Must be > 0.
    /// * `eviction_threshold` — fill ratio (0–1) that triggers eviction.
    pub fn with_threshold(
        context_size: i32,
        eviction_threshold: f64,
    ) -> Result<Self, BudgetError> {
        if context_size <= 0 {
            return Err(BudgetError::new(
                "Context size must be greater than zero.",
            ));
        }
        if !(0.0..=1.0).contains(&eviction_threshold) {
            return Err(BudgetError::new(
                "Eviction threshold must be in the range [0, 1].",
            ));
        }
        Ok(Self {
            context_size,
            used_tokens: 0,
            eviction_threshold,
        })
    }

    /// Maximum number of tokens the model's context window can hold.
    pub fn context_size(&self) -> i32 {
        self.context_size
    }

    /// Cumulative tokens consumed so far (prompt + completion).
    pub fn used_tokens(&self) -> i32 {
        self.used_tokens
    }

    /// Fill ratio at or above which [`Self::should_evict`] becomes `true`.
    pub fn eviction_threshold(&self) -> f64 {
        self.eviction_threshold
    }

    /// Tokens still available before the context window is full.
    pub fn remaining_tokens(&self) -> i32 {
        self.context_size - self.used_tokens
    }

    /// Proportion of the context window that is currently occupied (0–1).
    pub fn fill_ratio(&self) -> f64 {
        self.used_tokens as f64 / self.context_size as f64
    }

    /// `true` when the fill ratio has reached or exceeded the eviction
    /// threshold and older context should be dropped.
    pub fn should_evict(&self) -> bool {
        self.fill_ratio() >= self.eviction_threshold
    }

    /// Records the token cost of one exchange (a prompt + its completion).
    /// Returns an error if either count is negative.
    pub fn record_exchange(
        &mut self,
        prompt_tokens: i32,
        completion_tokens: i32,
    ) -> Result<(), BudgetError> {
        if prompt_tokens < 0 || completion_tokens < 0 {
            return Err(BudgetError::new("Token counts must not be negative."));
        }
        self.used_tokens += prompt_tokens + completion_tokens;
        Ok(())
    }

    /// Calculates how many of the oldest tokens should be dropped so that the
    /// fill ratio returns to `target_fill_ratio`. Returns 0 when the fill ratio
    /// is already at or below the target. Defaults to 0.50 via
    /// [`Self::calculate_eviction_count_default`].
    pub fn calculate_eviction_count(
        &self,
        target_fill_ratio: f64,
    ) -> Result<i32, BudgetError> {
        if !(0.0..=1.0).contains(&target_fill_ratio) {
            return Err(BudgetError::new(
                "Target fill ratio must be in the range [0, 1].",
            ));
        }
        let target_used = (self.context_size as f64 * target_fill_ratio) as i32;
        let evict = self.used_tokens - target_used;
        Ok(if evict > 0 { evict } else { 0 })
    }

    /// [`Self::calculate_eviction_count`] with the default target of 0.50.
    pub fn calculate_eviction_count_default(&self) -> Result<i32, BudgetError> {
        self.calculate_eviction_count(0.50)
    }

    /// Resets the used-token counter to zero. Call this after clearing the KV
    /// cache.
    pub fn reset(&mut self) {
        self.used_tokens = 0;
    }
}
