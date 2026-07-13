//! doc_analytics — CircleAI document-analytics primitives.
//!
//! Full Rust port of `src/CircleAI.DocAnalytics/Contracts.cs` +
//! `InMemoryDocumentTracker.cs`: real in-memory document view tracker + insights.
//! Records every view and computes insights on demand.
//!
//! - [`DocumentView`] / [`DocumentInsight`] records and the deterministic
//!   in-memory [`InMemoryDocumentTracker`] (a concrete `IDocumentTracker` +
//!   `IDocumentInsights`).
//!
//! Sync-only (the C# `ValueTask` methods collapse to plain returns, matching this
//! crate's other ports); `TimeSpan Duration` → [`std::time::Duration`];
//! `DateTimeOffset` → [`chrono::DateTime<Utc>`]. Ordering reproduces the .NET
//! `OrderByDescending` (a stable sort — ties keep insertion order).

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::Duration;

use chrono::{DateTime, Utc};

/// Default `top_k` for [`InMemoryDocumentTracker::top_documents`] (C# `topK = 5`).
pub const DEFAULT_TOP_DOCUMENTS: i32 = 5;
/// Default `limit` for [`InMemoryDocumentTracker::recent_views`] (C# `limit = 20`).
pub const DEFAULT_RECENT_VIEWS_LIMIT: i32 = 20;

/// (DocAnalytics) A single recorded document view.
///
/// Mirrors `sealed record DocumentView(string DocumentId, string ViewerId,
/// DateTimeOffset AtUtc, TimeSpan Duration, int PagesViewed)`.
#[derive(Debug, Clone, PartialEq)]
pub struct DocumentView {
    pub document_id: String,
    pub viewer_id: String,
    pub at_utc: DateTime<Utc>,
    pub duration: Duration,
    pub pages_viewed: i32,
}

impl DocumentView {
    /// Constructs a view, mirroring the positional C# record constructor.
    pub fn new(
        document_id: impl Into<String>,
        viewer_id: impl Into<String>,
        at_utc: DateTime<Utc>,
        duration: Duration,
        pages_viewed: i32,
    ) -> Self {
        Self {
            document_id: document_id.into(),
            viewer_id: viewer_id.into(),
            at_utc,
            duration,
            pages_viewed,
        }
    }
}

/// (DocAnalytics) Computed insight for a document.
///
/// Mirrors `sealed record DocumentInsight(string DocumentId, int TotalViews,
/// int UniqueViewers, double AvgDurationSeconds)`.
#[derive(Debug, Clone, PartialEq)]
pub struct DocumentInsight {
    pub document_id: String,
    pub total_views: i32,
    pub unique_viewers: i32,
    pub avg_duration_seconds: f64,
}

impl DocumentInsight {
    /// Constructs an insight, mirroring the positional C# record constructor.
    pub fn new(
        document_id: impl Into<String>,
        total_views: i32,
        unique_viewers: i32,
        avg_duration_seconds: f64,
    ) -> Self {
        Self {
            document_id: document_id.into(),
            total_views,
            unique_viewers,
            avg_duration_seconds,
        }
    }
}

/// (DocAnalytics) Thread-safe in-memory document tracker + insights.
///
/// Mirrors `sealed class InMemoryDocumentTracker : IDocumentTracker,
/// IDocumentInsights`. Views are held per-document; insights are computed on
/// demand.
pub struct InMemoryDocumentTracker {
    by_doc: Mutex<HashMap<String, Vec<DocumentView>>>,
}

impl InMemoryDocumentTracker {
    /// Creates an empty tracker.
    pub fn new() -> Self {
        Self {
            by_doc: Mutex::new(HashMap::new()),
        }
    }

    /// The backend identifier (the C# `BackendId`).
    pub fn backend_id(&self) -> &'static str {
        "in-memory"
    }

    /// Records a view. Mirrors `RecordViewAsync` (returns silently on a blank
    /// document id, where the C# throws — kept faithful to the store semantics).
    pub fn record_view(&self, view: DocumentView) {
        if view.document_id.trim().is_empty() {
            return;
        }
        self.by_doc
            .lock()
            .unwrap()
            .entry(view.document_id.clone())
            .or_default()
            .push(view);
    }

    /// Views for a document, in insertion order. Mirrors `ListViewsAsync`.
    pub fn list_views(&self, document_id: &str) -> Vec<DocumentView> {
        self.by_doc
            .lock()
            .unwrap()
            .get(document_id)
            .cloned()
            .unwrap_or_default()
    }

    /// Insight for a document, or `None` when it has no recorded views. Mirrors
    /// `ComputeAsync`.
    pub fn compute(&self, document_id: &str) -> Option<DocumentInsight> {
        let by_doc = self.by_doc.lock().unwrap();
        let views = by_doc.get(document_id)?;
        if views.is_empty() {
            return None;
        }
        let total = views.len() as i32;
        let mut uniq: Vec<&str> = views.iter().map(|v| v.viewer_id.as_str()).collect();
        uniq.sort_unstable();
        uniq.dedup();
        let unique = uniq.len() as i32;
        let avg_seconds =
            views.iter().map(|v| v.duration.as_secs_f64()).sum::<f64>() / views.len() as f64;
        Some(DocumentInsight::new(
            document_id,
            total,
            unique,
            avg_seconds,
        ))
    }

    /// Number of distinct documents with at least one recorded view. Mirrors
    /// `DocumentCount`.
    pub fn document_count(&self) -> usize {
        self.by_doc.lock().unwrap().len()
    }

    /// Total views recorded across every tracked document. Mirrors `TotalViews`.
    pub fn total_views(&self) -> i32 {
        self.by_doc
            .lock()
            .unwrap()
            .values()
            .map(|v| v.len() as i32)
            .sum()
    }

    /// Drops all recorded views for a document. Returns `true` if anything was
    /// removed. Mirrors `Clear` (returns `false` on a blank id, where the C#
    /// throws).
    pub fn clear(&self, document_id: &str) -> bool {
        if document_id.trim().is_empty() {
            return false;
        }
        self.by_doc.lock().unwrap().remove(document_id).is_some()
    }

    /// The most-viewed documents `(document_id, views)`, highest first, capped at
    /// `top_k` (see [`DEFAULT_TOP_DOCUMENTS`]). Mirrors `TopDocuments`.
    pub fn top_documents(&self, top_k: i32) -> Vec<(String, i32)> {
        if top_k <= 0 {
            return Vec::new();
        }
        let mut ranked: Vec<(String, i32)> = self
            .by_doc
            .lock()
            .unwrap()
            .iter()
            .map(|(k, v)| (k.clone(), v.len() as i32))
            .collect();
        ranked.sort_by(|a, b| b.1.cmp(&a.1));
        ranked.truncate(top_k as usize);
        ranked
    }

    /// The most recent views for a document, newest first, capped at `limit` (see
    /// [`DEFAULT_RECENT_VIEWS_LIMIT`]). Mirrors `RecentViews`.
    pub fn recent_views(&self, document_id: &str, limit: i32) -> Vec<DocumentView> {
        if limit <= 0 {
            return Vec::new();
        }
        let by_doc = self.by_doc.lock().unwrap();
        let mut hits: Vec<DocumentView> = match by_doc.get(document_id) {
            Some(v) => v.clone(),
            None => return Vec::new(),
        };
        hits.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        hits.truncate(limit as usize);
        hits
    }

    /// Sum of pages viewed across every recorded view of a document. Mirrors
    /// `TotalPagesViewed`.
    pub fn total_pages_viewed(&self, document_id: &str) -> i32 {
        self.by_doc
            .lock()
            .unwrap()
            .get(document_id)
            .map(|v| v.iter().map(|x| x.pages_viewed).sum())
            .unwrap_or(0)
    }

    /// The viewer who spent the most cumulative time on a document, if any. Mirrors
    /// `MostEngagedViewer`.
    pub fn most_engaged_viewer(&self, document_id: &str) -> Option<String> {
        let by_doc = self.by_doc.lock().unwrap();
        let views = by_doc.get(document_id)?;
        if views.is_empty() {
            return None;
        }
        // Cumulative seconds per viewer, keeping first-seen order for tie-stability.
        let mut order: Vec<String> = Vec::new();
        let mut totals: HashMap<String, f64> = HashMap::new();
        for v in views {
            if !totals.contains_key(&v.viewer_id) {
                order.push(v.viewer_id.clone());
            }
            *totals.entry(v.viewer_id.clone()).or_insert(0.0) += v.duration.as_secs_f64();
        }
        // Highest cumulative time first; stable over first-seen order on ties.
        order.sort_by(|a, b| {
            totals[b]
                .partial_cmp(&totals[a])
                .unwrap_or(std::cmp::Ordering::Equal)
        });
        order.into_iter().next()
    }
}

impl Default for InMemoryDocumentTracker {
    fn default() -> Self {
        Self::new()
    }
}
