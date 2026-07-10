//! in_memory_model_alignment.rs
//!
//! (3.3.0) Real in-memory alignment toolkit + auditor — Rust port of
//! `src/CircleAI.ModelAlignment/InMemoryModelAlignment.cs`.
//!
//! `apply` only allows **reversible** profiles (matches the "no permanent
//! abliteration" licence stance); the auditor **refuses** to publish any model
//! that has applied alignment profiles. Hosts that need different policy swap
//! auditors. The C# `ConcurrentDictionary<string, List<AlignmentProfile>>` +
//! `object _lock` collapses to a single `Mutex<HashMap<..>>` here — the whole
//! surface already serialises through `_lock`.

use std::collections::HashMap;
use std::sync::Mutex;

use super::contracts::{
    AlignmentError, AlignmentProfile, AlignmentResult, IAlignmentAuditor, IAlignmentToolkit,
};

/// (3.3.0) In-memory alignment toolkit. Tracks the reversible profiles applied
/// per model id.
pub struct InMemoryAlignmentToolkit {
    by_model: Mutex<HashMap<String, Vec<AlignmentProfile>>>,
}

impl InMemoryAlignmentToolkit {
    /// Creates an empty toolkit.
    pub fn new() -> Self {
        Self {
            by_model: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryAlignmentToolkit {
    fn default() -> Self {
        Self::new()
    }
}

/// True for `null`/empty/whitespace strings — the Rust analogue of the C#
/// `string.IsNullOrWhiteSpace`.
fn is_null_or_whitespace(s: &str) -> bool {
    s.trim().is_empty()
}

impl IAlignmentToolkit for InMemoryAlignmentToolkit {
    fn backend_id(&self) -> &str {
        "in-memory"
    }

    fn apply(
        &self,
        model_id: &str,
        profile: &AlignmentProfile,
    ) -> Result<AlignmentResult, AlignmentError> {
        if is_null_or_whitespace(model_id) {
            return Err(AlignmentError::InvalidArgument("modelId required".to_string()));
        }
        if !profile.is_reversible {
            return Ok(AlignmentResult::failed(
                profile.profile_id.clone(),
                "Non-reversible alignment refused by InMemoryAlignmentToolkit",
            ));
        }

        let mut map = self.by_model.lock().unwrap();
        map.entry(model_id.to_string())
            .or_default()
            .push(profile.clone());
        Ok(AlignmentResult::ok(profile.profile_id.clone()))
    }

    fn revert(
        &self,
        model_id: &str,
        profile_id: &str,
    ) -> Result<AlignmentResult, AlignmentError> {
        if is_null_or_whitespace(model_id) {
            return Err(AlignmentError::InvalidArgument("modelId required".to_string()));
        }
        if is_null_or_whitespace(profile_id) {
            return Err(AlignmentError::InvalidArgument(
                "profileId required".to_string(),
            ));
        }

        let mut map = self.by_model.lock().unwrap();
        let Some(list) = map.get_mut(model_id) else {
            return Ok(AlignmentResult::failed(profile_id, "Unknown model"));
        };
        let before = list.len();
        list.retain(|p| p.profile_id != profile_id);
        let removed = before - list.len();
        Ok(if removed > 0 {
            AlignmentResult::ok(profile_id)
        } else {
            AlignmentResult::failed(profile_id, "Profile not applied to this model")
        })
    }

    fn list_applied(&self, model_id: &str) -> Result<Vec<AlignmentProfile>, AlignmentError> {
        if is_null_or_whitespace(model_id) {
            return Err(AlignmentError::InvalidArgument("modelId required".to_string()));
        }
        let map = self.by_model.lock().unwrap();
        Ok(map.get(model_id).cloned().unwrap_or_default())
    }
}

/// (3.3.0) Refuses to publish weights that carry alignment deltas. Wired by
/// default. Holds a shared reference to the toolkit it audits.
pub struct RefuseAlignedPublishAuditor<T: IAlignmentToolkit> {
    toolkit: std::sync::Arc<T>,
}

impl<T: IAlignmentToolkit> RefuseAlignedPublishAuditor<T> {
    /// Creates an auditor over `toolkit`.
    pub fn new(toolkit: std::sync::Arc<T>) -> Self {
        Self { toolkit }
    }
}

impl<T: IAlignmentToolkit> IAlignmentAuditor for RefuseAlignedPublishAuditor<T> {
    fn backend_id(&self) -> &str {
        "refuse-aligned"
    }

    fn assert_ok_to_publish(&self, model_id: &str) -> Result<(), AlignmentError> {
        if is_null_or_whitespace(model_id) {
            return Err(AlignmentError::InvalidArgument("modelId required".to_string()));
        }
        let applied = self.toolkit.list_applied(model_id)?;
        if !applied.is_empty() {
            return Err(AlignmentError::NotAllowed(format!(
                "Cannot publish '{model_id}': {} alignment profile(s) applied — \
                 this would distribute weights with safety modifications.",
                applied.len()
            )));
        }
        Ok(())
    }
}
