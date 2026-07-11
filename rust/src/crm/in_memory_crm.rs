//! in_memory_crm.rs
//!
//! (3.3.0) Real in-memory CRM — Rust port of
//! `src/CircleAI.CRM/InMemoryCrm.cs`: contact store with name/email substring
//! search, deal pipeline indexed by stage, activity log per contact.
//!
//! The C# `ConcurrentDictionary<string, T>` collapses to `Mutex`-guarded
//! `HashMap`s here. Substring / ordering queries reproduce the .NET
//! `Contains(..., OrdinalIgnoreCase)` + `OrderBy` / `OrderByDescending`
//! semantics. As in the C# (which enumerates `ConcurrentDictionary.Values`),
//! the relative order of entries with an equal sort key is unspecified.

use std::collections::HashMap;
use std::sync::Mutex;

use super::contracts::{Activity, Contact, Deal, IActivityLog, IContactStore, IDealPipeline};

/// Ordinal-ignore-case `Contains`, mirroring the C# substring test.
fn ci_contains(haystack: &str, needle: &str) -> bool {
    haystack.to_lowercase().contains(&needle.to_lowercase())
}

/// (3.3.0) In-memory [`IContactStore`].
///
/// Mirrors `sealed class InMemoryContactStore`.
pub struct InMemoryContactStore {
    items: Mutex<HashMap<String, Contact>>,
}

impl InMemoryContactStore {
    /// Creates an empty store.
    pub fn new() -> Self {
        Self {
            items: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryContactStore {
    fn default() -> Self {
        Self::new()
    }
}

impl IContactStore for InMemoryContactStore {
    fn backend_id(&self) -> &str {
        "in-memory"
    }

    fn upsert(&self, c: Contact) {
        if c.contact_id.trim().is_empty() {
            panic!("ContactId required");
        }
        self.items.lock().unwrap().insert(c.contact_id.clone(), c);
    }

    fn get(&self, id: &str) -> Option<Contact> {
        if id.trim().is_empty() {
            panic!("id required");
        }
        self.items.lock().unwrap().get(id).cloned()
    }

    fn search(&self, query: &str, top_k: usize) -> Vec<Contact> {
        if top_k == 0 {
            panic!("topK out of range");
        }
        let items = self.items.lock().unwrap();
        let mut hits: Vec<Contact> = items
            .values()
            .filter(|c| {
                ci_contains(&c.full_name, query)
                    || c.email.as_deref().is_some_and(|e| ci_contains(e, query))
            })
            .cloned()
            .collect();
        // OrderBy(FullName, OrdinalIgnoreCase).
        hits.sort_by(|a, b| a.full_name.to_lowercase().cmp(&b.full_name.to_lowercase()));
        hits.truncate(top_k);
        hits
    }
}

/// (3.3.0) In-memory [`IDealPipeline`].
///
/// Mirrors `sealed class InMemoryDealPipeline`.
pub struct InMemoryDealPipeline {
    items: Mutex<HashMap<String, Deal>>,
}

impl InMemoryDealPipeline {
    /// Creates an empty pipeline.
    pub fn new() -> Self {
        Self {
            items: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryDealPipeline {
    fn default() -> Self {
        Self::new()
    }
}

impl IDealPipeline for InMemoryDealPipeline {
    fn backend_id(&self) -> &str {
        "in-memory"
    }

    fn upsert(&self, d: Deal) {
        if d.deal_id.trim().is_empty() {
            panic!("DealId required");
        }
        self.items.lock().unwrap().insert(d.deal_id.clone(), d);
    }

    fn get(&self, id: &str) -> Option<Deal> {
        self.items.lock().unwrap().get(id).cloned()
    }

    fn list_by_stage(&self, stage: &str) -> Vec<Deal> {
        if stage.trim().is_empty() {
            panic!("stage required");
        }
        let items = self.items.lock().unwrap();
        let mut hits: Vec<Deal> = items
            .values()
            .filter(|d| d.stage.eq_ignore_ascii_case(stage))
            .cloned()
            .collect();
        // OrderByDescending(Value).
        hits.sort_by(|a, b| {
            b.value
                .partial_cmp(&a.value)
                .unwrap_or(std::cmp::Ordering::Equal)
        });
        hits
    }
}

/// (3.3.0) In-memory [`IActivityLog`].
///
/// Mirrors `sealed class InMemoryActivityLog`.
pub struct InMemoryActivityLog {
    by_contact: Mutex<HashMap<String, Vec<Activity>>>,
}

impl InMemoryActivityLog {
    /// Creates an empty log.
    pub fn new() -> Self {
        Self {
            by_contact: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryActivityLog {
    fn default() -> Self {
        Self::new()
    }
}

impl IActivityLog for InMemoryActivityLog {
    fn backend_id(&self) -> &str {
        "in-memory"
    }

    fn append(&self, a: Activity) {
        if a.contact_id.trim().is_empty() {
            panic!("ContactId required");
        }
        self.by_contact
            .lock()
            .unwrap()
            .entry(a.contact_id.clone())
            .or_default()
            .push(a);
    }

    fn read_for_contact(&self, contact_id: &str, limit: usize) -> Vec<Activity> {
        if contact_id.trim().is_empty() {
            panic!("contactId required");
        }
        let by_contact = self.by_contact.lock().unwrap();
        let Some(list) = by_contact.get(contact_id) else {
            return Vec::new();
        };
        // OrderByDescending(AtUtc).Take(limit); stable — equal timestamps keep
        // insertion order.
        let mut out: Vec<Activity> = list.clone();
        out.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        out.truncate(limit);
        out
    }
}
