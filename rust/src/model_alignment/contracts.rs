//! contracts.rs
//!
//! (2.6.0) Model-alignment surface — Rust port of
//! `src/CircleAI.ModelAlignment/Contracts.cs`.
//!
//! Pattern-port of OBLITERATUS. Targeted abliteration lives behind contracts so a
//! host can apply / revert it deliberately — and so we can refuse to publish
//! abliterated weights. Contracts are SYNC (matches the crate's trait style).

use chrono::{DateTime, Utc};

/// (2.6.0) A named alignment (abliteration) profile that can be applied to a
/// model.
///
/// Mirrors `sealed record AlignmentProfile(string ProfileId, string Description,
/// IReadOnlyList<string> RefusalCategoriesRemoved, DateTimeOffset CreatedAtUtc,
/// bool IsReversible)`.
#[derive(Debug, Clone, PartialEq)]
pub struct AlignmentProfile {
    pub profile_id: String,
    pub description: String,
    pub refusal_categories_removed: Vec<String>,
    pub created_at_utc: DateTime<Utc>,
    pub is_reversible: bool,
}

impl AlignmentProfile {
    /// Constructs a profile, mirroring the positional C# record constructor.
    pub fn new(
        profile_id: impl Into<String>,
        description: impl Into<String>,
        refusal_categories_removed: Vec<String>,
        created_at_utc: DateTime<Utc>,
        is_reversible: bool,
    ) -> Self {
        Self {
            profile_id: profile_id.into(),
            description: description.into(),
            refusal_categories_removed,
            created_at_utc,
            is_reversible,
        }
    }
}

/// (2.6.0) Result of an apply / revert operation.
///
/// Mirrors `sealed record AlignmentResult(string ProfileId, bool Success,
/// string? FailureReason)`.
#[derive(Debug, Clone, PartialEq)]
pub struct AlignmentResult {
    pub profile_id: String,
    pub success: bool,
    pub failure_reason: Option<String>,
}

impl AlignmentResult {
    /// Constructs a result, mirroring the positional C# record constructor.
    pub fn new(
        profile_id: impl Into<String>,
        success: bool,
        failure_reason: Option<String>,
    ) -> Self {
        Self {
            profile_id: profile_id.into(),
            success,
            failure_reason,
        }
    }

    /// Convenience for a successful result (`Success = true, FailureReason = null`).
    pub fn ok(profile_id: impl Into<String>) -> Self {
        Self::new(profile_id, true, None)
    }

    /// Convenience for a failed result with a reason.
    pub fn failed(profile_id: impl Into<String>, reason: impl Into<String>) -> Self {
        Self::new(profile_id, false, Some(reason.into()))
    }
}

/// (2.6.0) Error raised by an [`IAlignmentAuditor`] when publishing is refused,
/// or by an [`IAlignmentToolkit`] on invalid arguments — the Rust equivalent of
/// the C# `ArgumentException` / `InvalidOperationException` throws.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AlignmentError {
    /// A required argument was null/empty/whitespace (C# `ArgumentException`).
    InvalidArgument(String),
    /// The operation is not permitted in the current state
    /// (C# `InvalidOperationException`) — e.g. publishing an aligned model.
    NotAllowed(String),
}

impl std::fmt::Display for AlignmentError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            AlignmentError::InvalidArgument(m) => write!(f, "{m}"),
            AlignmentError::NotAllowed(m) => write!(f, "{m}"),
        }
    }
}

impl std::error::Error for AlignmentError {}

/// (2.6.0) Targeted abliteration toolkit. Apply / revert / list alignment
/// profiles.
///
/// Mirrors `interface IAlignmentToolkit`. Argument validation that throws in C#
/// is surfaced as `Err(AlignmentError::InvalidArgument)`.
pub trait IAlignmentToolkit {
    /// Stable identifier for the backend implementation.
    fn backend_id(&self) -> &str;

    /// Applies `profile` to `model_id`. Returns an [`AlignmentResult`]; the
    /// `Err` arm is reserved for argument validation failures.
    fn apply(
        &self,
        model_id: &str,
        profile: &AlignmentProfile,
    ) -> Result<AlignmentResult, AlignmentError>;

    /// Reverts a previously applied profile from `model_id`.
    fn revert(
        &self,
        model_id: &str,
        profile_id: &str,
    ) -> Result<AlignmentResult, AlignmentError>;

    /// Lists the alignment profiles currently applied to `model_id`.
    fn list_applied(&self, model_id: &str) -> Result<Vec<AlignmentProfile>, AlignmentError>;
}

/// (2.6.0) Refuses to upload / publish weights that carry alignment deltas.
///
/// Mirrors `interface IAlignmentAuditor`.
pub trait IAlignmentAuditor {
    /// Stable identifier for the backend implementation.
    fn backend_id(&self) -> &str;

    /// Returns `Err(AlignmentError::NotAllowed)` (the C# `throw`) if the model
    /// has applied alignment profiles and the action is "publish upstream".
    fn assert_ok_to_publish(&self, model_id: &str) -> Result<(), AlignmentError>;
}
