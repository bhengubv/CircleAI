//! kv_compression.rs
//!
//! KV-cache compression mode + typed apply, plus the PowerBudget → concrete
//! knobs policy. Ported from the relevant parts of
//! `CircleAI.Inference/MnnInterop.cs` (KvCompressionMode / KvCompressionApplyResult
//! / MnnKvCompression) and `CircleAI.Inference/PowerBudget.cs` (PowerBudgetPolicy).
//!
//! The native C ABI is injected behind [`IKvCompressionHandle`] (the C# code
//! P/Invokes `mnn_llm_set_kv_compression_mode` / `_get_`); the in-memory
//! [`InMemoryKvCompressionHandle`] default reproduces the exact status-code
//! contract deterministically so the mapping is testable without a native lib.

use super::PowerBudget;

/// KV cache compression mode. Mirrors the C ABI's integer encoding so the
/// managed and native layers agree without translation tables.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum KvCompressionMode {
    /// Full FP16 KV cache — default behaviour, always supported.
    Off = 0,
    /// TurboQuant at 4 bits per channel — ~4× shrink, < 1% accuracy loss expected.
    TurboQuant4Bit = 1,
    /// TurboQuant at 3 bits per channel — ~5× shrink, marginal accuracy loss.
    TurboQuant3Bit = 2,
    /// TurboQuant at 2 bits per channel — ~8× shrink, noticeable accuracy loss.
    TurboQuant2Bit = 3,
}

impl KvCompressionMode {
    /// The raw C-ABI integer for this mode.
    pub fn as_i32(self) -> i32 {
        self as i32
    }

    /// Reconstructs a mode from its raw integer, mapping out-of-range values to
    /// [`KvCompressionMode::Off`] (matches the C# `raw is >= 0 and <= 3 ? … : Off`).
    pub fn from_raw(raw: i32) -> KvCompressionMode {
        match raw {
            0 => KvCompressionMode::Off,
            1 => KvCompressionMode::TurboQuant4Bit,
            2 => KvCompressionMode::TurboQuant3Bit,
            3 => KvCompressionMode::TurboQuant2Bit,
            _ => KvCompressionMode::Off,
        }
    }
}

/// Outcome of applying a KV-compression mode, translated from the C ABI status
/// codes into a typed result.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum KvCompressionApplyResult {
    /// Native path accepted the mode and will use it.
    Applied = 0,
    /// The mode value was outside the valid 0..3 range.
    InvalidMode = 1,
    /// LEGACY (mnnbridge ≤ 1.1.0) — scaffolding-only response. Kept for
    /// binary back-compat with older bridges.
    NotImplemented = 2,
    /// Handle pointer was invalid.
    HandleInvalid = -1,
}

impl KvCompressionApplyResult {
    /// Maps a raw C-ABI status code to the typed result, exactly as the C#
    /// `MnnKvCompression.Set` switch does (`0/1/2 → …`, else `HandleInvalid`).
    pub fn from_raw(raw: i32) -> KvCompressionApplyResult {
        match raw {
            0 => KvCompressionApplyResult::Applied,
            1 => KvCompressionApplyResult::InvalidMode,
            2 => KvCompressionApplyResult::NotImplemented,
            _ => KvCompressionApplyResult::HandleInvalid,
        }
    }
}

/// The native KV-compression C ABI, injected so the typed wrapper can be
/// exercised without a real `mnnbridge`. Implementations return the raw C-ABI
/// status codes; the wrapper [`MnnKvCompression`] translates them.
pub trait IKvCompressionHandle {
    /// Mirrors `mnn_llm_set_kv_compression_mode(handle, mode)` — returns the
    /// raw status code (0 applied, 1 invalid-mode, 2 not-implemented,
    /// negative on invalid handle).
    fn set_raw(&mut self, mode: i32) -> i32;

    /// Mirrors `mnn_llm_get_kv_compression_mode(handle)` — returns the raw
    /// last-set mode, or a negative value on invalid handle.
    fn get_raw(&self) -> i32;
}

/// Deterministic in-memory handle honouring the exact C-ABI contract:
/// accepts modes 0..=3 (returns 0), rejects out-of-range modes (returns 1),
/// and echoes the last accepted mode from `get_raw`.
#[derive(Debug, Default, Clone)]
pub struct InMemoryKvCompressionHandle {
    mode: i32,
    /// When true, `get_raw` returns a negative "invalid handle" sentinel and
    /// `set_raw` returns the invalid-handle code — models the null-handle path.
    invalid: bool,
}

impl InMemoryKvCompressionHandle {
    /// A fresh, valid handle initialised to [`KvCompressionMode::Off`].
    pub fn new() -> Self {
        Self {
            mode: 0,
            invalid: false,
        }
    }

    /// A handle that behaves as an invalid native pointer (all ops fail).
    pub fn invalid() -> Self {
        Self {
            mode: 0,
            invalid: true,
        }
    }
}

impl IKvCompressionHandle for InMemoryKvCompressionHandle {
    fn set_raw(&mut self, mode: i32) -> i32 {
        if self.invalid {
            return -1;
        }
        if !(0..=3).contains(&mode) {
            return 1; // InvalidMode
        }
        self.mode = mode;
        0 // Applied
    }

    fn get_raw(&self) -> i32 {
        if self.invalid {
            -1
        } else {
            self.mode
        }
    }
}

/// Typed wrapper over the KV-compression C ABI so callers don't deal with raw
/// integers. Mirrors the C# `MnnKvCompression` static helper.
pub struct MnnKvCompression;

impl MnnKvCompression {
    /// Applies the requested mode and returns the typed result.
    pub fn set<H: IKvCompressionHandle>(
        handle: &mut H,
        mode: KvCompressionMode,
    ) -> KvCompressionApplyResult {
        let raw = handle.set_raw(mode.as_i32());
        KvCompressionApplyResult::from_raw(raw)
    }

    /// Reads the last-set mode (or [`KvCompressionMode::Off`] on invalid handle).
    pub fn get<H: IKvCompressionHandle>(handle: &H) -> KvCompressionMode {
        let raw = handle.get_raw();
        if (0..=3).contains(&raw) {
            KvCompressionMode::from_raw(raw)
        } else {
            KvCompressionMode::Off
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PowerBudgetPolicy — PowerBudget → concrete generation knobs
// ─────────────────────────────────────────────────────────────────────────────

/// The runtime's translation of a [`PowerBudget`] into concrete generation
/// knobs. Mirrors `PowerBudgetPolicy.Resolution`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Resolution {
    /// Cap on output tokens for this call.
    pub max_tokens: i32,
    /// Which [`KvCompressionMode`] the runtime prefers for this budget.
    pub preferred_kv_mode: KvCompressionMode,
    /// When a fallback chain is configured, whether to pick a smaller model.
    pub prefer_smaller_model_in_chain: bool,
}

/// Static helper mapping a [`PowerBudget`] to concrete knobs. Mirrors
/// `PowerBudgetPolicy`.
pub struct PowerBudgetPolicy;

impl PowerBudgetPolicy {
    /// Maps a budget to concrete knobs with no device-state inputs (equivalent
    /// to the C# `Resolve` call with `batteryLevelPercent = null,
    /// thermalThrottled = false`).
    pub fn resolve(budget: PowerBudget, requested_max_tokens: i32) -> Resolution {
        Self::resolve_with_state(budget, requested_max_tokens, None, false)
    }

    /// Maps a budget to concrete knobs, auto-downgrading based on device state:
    /// `Normal` → `Low` below 15% battery; `High` → `Normal` when thermally
    /// throttled. The returned [`Resolution`] caps over-budget values without
    /// altering the caller's request.
    pub fn resolve_with_state(
        budget: PowerBudget,
        requested_max_tokens: i32,
        battery_level_percent: Option<i32>,
        thermal_throttled: bool,
    ) -> Resolution {
        let mut budget = budget;

        // Auto-downgrade based on device state.
        if budget == PowerBudget::Normal && matches!(battery_level_percent, Some(b) if b < 15) {
            budget = PowerBudget::Low;
        }
        if budget == PowerBudget::High && thermal_throttled {
            budget = PowerBudget::Normal;
        }

        match budget {
            PowerBudget::None => Resolution {
                max_tokens: requested_max_tokens,
                preferred_kv_mode: KvCompressionMode::TurboQuant4Bit,
                prefer_smaller_model_in_chain: false,
            },
            PowerBudget::Low => Resolution {
                max_tokens: requested_max_tokens.min(64),
                preferred_kv_mode: KvCompressionMode::TurboQuant4Bit,
                prefer_smaller_model_in_chain: true,
            },
            PowerBudget::Normal => Resolution {
                max_tokens: requested_max_tokens.min(512),
                preferred_kv_mode: KvCompressionMode::TurboQuant4Bit,
                prefer_smaller_model_in_chain: false,
            },
            PowerBudget::High => Resolution {
                max_tokens: requested_max_tokens.min(2048),
                preferred_kv_mode: KvCompressionMode::Off,
                prefer_smaller_model_in_chain: false,
            },
        }
    }
}
