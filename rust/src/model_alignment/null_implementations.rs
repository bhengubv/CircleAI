//! null_implementations.rs
//!
//! (2.6.0) Fail-closed defaults — Rust port of
//! `src/CircleAI.ModelAlignment/NullImplementations.cs`.
//!
//! The null toolkit refuses to apply / revert anything (there is no real backend
//! wired) and lists nothing; the null auditor always asserts ok-to-publish (since
//! nothing was applied).

use super::contracts::{
    AlignmentError, AlignmentProfile, AlignmentResult, IAlignmentAuditor, IAlignmentToolkit,
};

/// (2.6.0) Fail-closed alignment toolkit — every apply/revert fails, list is
/// empty.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullAlignmentToolkit;

impl NullAlignmentToolkit {
    /// The shared singleton (the C# `Instance`).
    pub const INSTANCE: NullAlignmentToolkit = NullAlignmentToolkit;
}

impl IAlignmentToolkit for NullAlignmentToolkit {
    fn backend_id(&self) -> &str {
        "null"
    }

    fn apply(
        &self,
        _model_id: &str,
        profile: &AlignmentProfile,
    ) -> Result<AlignmentResult, AlignmentError> {
        Ok(AlignmentResult::failed(
            profile.profile_id.clone(),
            "NullAlignmentToolkit: no real backend wired.",
        ))
    }

    fn revert(
        &self,
        _model_id: &str,
        profile_id: &str,
    ) -> Result<AlignmentResult, AlignmentError> {
        Ok(AlignmentResult::failed(
            profile_id,
            "NullAlignmentToolkit: nothing to revert.",
        ))
    }

    fn list_applied(&self, _model_id: &str) -> Result<Vec<AlignmentProfile>, AlignmentError> {
        Ok(Vec::new())
    }
}

/// (2.6.0) Fail-open alignment auditor — publishing is always allowed (nothing
/// was applied).
#[derive(Debug, Default, Clone, Copy)]
pub struct NullAlignmentAuditor;

impl NullAlignmentAuditor {
    /// The shared singleton (the C# `Instance`).
    pub const INSTANCE: NullAlignmentAuditor = NullAlignmentAuditor;
}

impl IAlignmentAuditor for NullAlignmentAuditor {
    fn backend_id(&self) -> &str {
        "null"
    }

    fn assert_ok_to_publish(&self, _model_id: &str) -> Result<(), AlignmentError> {
        Ok(())
    }
}
