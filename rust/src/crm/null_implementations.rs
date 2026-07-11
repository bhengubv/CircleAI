//! null_implementations.rs
//!
//! (2.8.0) Fail-closed CRM defaults — Rust port of
//! `src/CircleAI.CRM/NullImplementations.cs`.
//!
//! Each null backend reports `backend_id() == "null"`, accepts writes as no-ops
//! and returns nothing on reads.

use super::contracts::{Activity, Contact, Deal, IActivityLog, IContactStore, IDealPipeline};

/// (CRM) Fail-closed [`IContactStore`].
///
/// Mirrors `sealed class NullContactStore` (the C# `Instance` singleton is a
/// unit struct here).
#[derive(Debug, Clone, Copy, Default)]
pub struct NullContactStore;

impl NullContactStore {
    /// The shared instance (mirrors the C# `static readonly Instance`).
    pub const INSTANCE: NullContactStore = NullContactStore;
}

impl IContactStore for NullContactStore {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn upsert(&self, _c: Contact) {}
    fn get(&self, _id: &str) -> Option<Contact> {
        None
    }
    fn search(&self, _query: &str, _top_k: usize) -> Vec<Contact> {
        Vec::new()
    }
}

/// (CRM) Fail-closed [`IDealPipeline`].
///
/// Mirrors `sealed class NullDealPipeline`.
#[derive(Debug, Clone, Copy, Default)]
pub struct NullDealPipeline;

impl NullDealPipeline {
    /// The shared instance.
    pub const INSTANCE: NullDealPipeline = NullDealPipeline;
}

impl IDealPipeline for NullDealPipeline {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn upsert(&self, _d: Deal) {}
    fn get(&self, _id: &str) -> Option<Deal> {
        None
    }
    fn list_by_stage(&self, _stage: &str) -> Vec<Deal> {
        Vec::new()
    }
}

/// (CRM) Fail-closed [`IActivityLog`].
///
/// Mirrors `sealed class NullActivityLog`.
#[derive(Debug, Clone, Copy, Default)]
pub struct NullActivityLog;

impl NullActivityLog {
    /// The shared instance.
    pub const INSTANCE: NullActivityLog = NullActivityLog;
}

impl IActivityLog for NullActivityLog {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn append(&self, _a: Activity) {}
    fn read_for_contact(&self, _contact_id: &str, _limit: usize) -> Vec<Activity> {
        Vec::new()
    }
}
