//! rag.rs
//!
//! Retrieval-augmented context assembly. Ported from CircleAI.Memory (C#):
//!   - `ITextEmbedder` (CircleAI.Embeddings) — the semantic-ranking seam
//!   - `RagContextBuilder` — retrieves the most relevant episodes and formats
//!     them as a compact context block for injection into the B! system prompt
//!   - `RagPipelineBuilder` — fluent factory with sensible defaults
//!
//! Mirrors the TypeScript reference (memory/rag.ts) 1:1.
//!
//! RAG is strictly best-effort: any retrieval / embedding failure degrades to an
//! empty string and must never block inference. In-memory port — the C#
//! `WithSqliteStore` convenience is intentionally omitted (no SQLite backend in
//! the Rust tree); use `with_store` / `with_in_memory_store` instead.

use std::sync::Arc;

use super::episodic::InMemoryEpisodicStore;
use super::stores::EpisodicMemoryEntry;
use crate::brain::BrainError;

// ─────────────────────────────────────────────────────────────────────────────
// ITextEmbedder — CircleAI.Embeddings.ITextEmbedder
// ─────────────────────────────────────────────────────────────────────────────

/// Produces an embedding vector for a text. The semantic-ranking seam for RAG.
///
/// A failure is signalled by returning `Err`; the builder treats it as
/// non-fatal and falls back to recency ranking (matches the C#/TS `try/catch`).
pub trait ITextEmbedder: Send + Sync {
    /// Generates an embedding for `text`.
    fn generate(&self, text: &str) -> Result<Vec<f32>, BrainError>;
}

// ─────────────────────────────────────────────────────────────────────────────
// RagEpisodicStore — the read seam the builder depends on
// ─────────────────────────────────────────────────────────────────────────────

/// The episodic-store read seam used by [`RagContextBuilder`]. Mirrors the
/// `SearchAsync(queryEmbedding, topK)` method of the C#/TS `IEpisodicMemoryStore`
/// the builder calls. A failure is surfaced as `Err` so the builder can honour
/// its best-effort contract (returns an empty string).
pub trait RagEpisodicStore: Send + Sync {
    /// Returns the top-`top_k` entries most similar to `query_embedding` (cosine),
    /// falling back to recency when the query is `None`.
    fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<EpisodicMemoryEntry>, BrainError>;
}

impl RagEpisodicStore for InMemoryEpisodicStore {
    fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<EpisodicMemoryEntry>, BrainError> {
        use super::episodic::EpisodicSearch;
        EpisodicSearch::search(self, query_embedding, top_k)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RagContextBuilder — CircleAI.Memory.RagContextBuilder
// ─────────────────────────────────────────────────────────────────────────────

/// Retrieves the most semantically relevant episodes from a [`RagEpisodicStore`]
/// and formats them as a compact context block for injection into the B! system
/// prompt.
pub struct RagContextBuilder {
    store: Arc<dyn RagEpisodicStore>,
    embedder: Option<Arc<dyn ITextEmbedder>>,
    top_k: usize,
    max_chars_per_entry: usize,
}

impl RagContextBuilder {
    /// Creates a builder.
    ///
    /// * `store` — the episodic store to query.
    /// * `embedder` — optional. When provided, uses semantic similarity to rank
    ///   results; when `None`, falls back to recency ranking.
    /// * `top_k` — max episodes to include. Floored at 1 (default caller passes 5).
    /// * `max_chars_per_entry` — max chars taken from each episode's texts.
    ///   Floored at 50 (default caller passes 300).
    pub fn new(
        store: Arc<dyn RagEpisodicStore>,
        embedder: Option<Arc<dyn ITextEmbedder>>,
        top_k: usize,
        max_chars_per_entry: usize,
    ) -> Self {
        Self {
            store,
            embedder,
            top_k: top_k.max(1),
            max_chars_per_entry: max_chars_per_entry.max(50),
        }
    }

    /// Convenience constructor with the C#/TS defaults (top_k = 5,
    /// max_chars_per_entry = 300, no embedder).
    pub fn with_store(store: Arc<dyn RagEpisodicStore>) -> Self {
        Self::new(store, None, 5, 300)
    }

    /// Builds a context block for the given `query` text. Returns an empty
    /// string when the query is blank, the store is empty, or any retrieval
    /// fails (RAG is best-effort and must never block inference).
    pub fn build_context(&self, query: &str) -> String {
        if query.trim().is_empty() {
            return String::new();
        }

        // Whole retrieval path is best-effort; any error → empty string.
        match self.try_build(query) {
            Ok(s) => s,
            Err(_) => String::new(),
        }
    }

    fn try_build(&self, query: &str) -> Result<String, BrainError> {
        let mut query_embedding: Option<Vec<f32>> = None;
        if let Some(embedder) = &self.embedder {
            // Embedding failure is non-fatal — fall back to recency.
            if let Ok(v) = embedder.generate(query) {
                query_embedding = Some(v);
            }
        }

        let entries = self
            .store
            .search(query_embedding.as_deref(), self.top_k)?;
        if entries.is_empty() {
            return Ok(String::new());
        }

        Ok(self.format_entries(&entries))
    }

    fn format_entries(&self, entries: &[EpisodicMemoryEntry]) -> String {
        // Half-budget per side, integer-divided to match the C# `_maxCharsPerEntry / 2`.
        let half = self.max_chars_per_entry / 2;
        let mut sb = String::from("[Relevant past exchanges — for context only]\n");

        for e in entries {
            let user = truncate(&e.user_text, half);
            let asst = truncate(&e.assistant_text, half);
            let when = format!("{} UTC", e.recorded_at_utc.format("%Y-%m-%d %H:%M"));

            sb.push_str("• [");
            sb.push_str(&when);
            sb.push_str("] ");
            if let Some(ctx) = &e.app_context {
                if !ctx.trim().is_empty() {
                    sb.push('(');
                    sb.push_str(ctx);
                    sb.push_str(") ");
                }
            }
            sb.push_str("User: ");
            sb.push_str(&user);
            sb.push('\n');
            sb.push_str("  B!: ");
            sb.push_str(&asst);
            sb.push('\n');
        }

        sb
    }
}

/// Truncate to `max_len` chars, replacing the last kept char with an ellipsis
/// (matches the C# `text[..(maxLen-1)] + "…"`). Operates on Unicode scalar values
/// (`char`s) so multi-byte text is not split mid-codepoint — matching the C#
/// `string.Length` (UTF-16) / TS `.length` semantics closely enough for the ASCII
/// fixtures the suites use.
fn truncate(text: &str, max_len: usize) -> String {
    if text.is_empty() {
        return String::new();
    }
    let char_count = text.chars().count();
    if char_count <= max_len {
        return text.to_string();
    }
    let kept: String = text.chars().take(max_len - 1).collect();
    format!("{kept}…")
}

// ─────────────────────────────────────────────────────────────────────────────
// RagPipelineBuilder — CircleAI.Memory.RagPipelineBuilder
// ─────────────────────────────────────────────────────────────────────────────

/// Fluent builder for constructing a [`RagContextBuilder`] with an episodic
/// store, optional embedder, and tuning parameters.
///
/// ```ignore
/// let rag = RagPipelineBuilder::create()
///     .with_in_memory_store()
///     .with_top_k(10)?
///     .with_max_chars_per_entry(500)?
///     .build()?;
/// let context = rag.build_context("user query");
/// ```
pub struct RagPipelineBuilder {
    store: Option<Arc<dyn RagEpisodicStore>>,
    embedder: Option<Arc<dyn ITextEmbedder>>,
    top_k: usize,
    max_chars_per_entry: usize,
}

impl Default for RagPipelineBuilder {
    fn default() -> Self {
        Self {
            store: None,
            embedder: None,
            top_k: 5,
            max_chars_per_entry: 300,
        }
    }
}

impl RagPipelineBuilder {
    /// Creates a new builder instance.
    pub fn create() -> Self {
        Self::default()
    }

    /// Sets the episodic memory store to retrieve past exchanges from.
    pub fn with_store(mut self, store: Arc<dyn RagEpisodicStore>) -> Self {
        self.store = Some(store);
        self
    }

    /// Convenience: creates an [`InMemoryEpisodicStore`] and uses it. Suitable
    /// for tests and short-lived processes where persistence is not needed.
    pub fn with_in_memory_store(mut self) -> Self {
        self.store = Some(Arc::new(InMemoryEpisodicStore::with_default_capacity()));
        self
    }

    /// Sets the text embedder for semantic similarity search. When not set, the
    /// builder falls back to recency-based retrieval.
    pub fn with_embedder(mut self, embedder: Arc<dyn ITextEmbedder>) -> Self {
        self.embedder = Some(embedder);
        self
    }

    /// Sets the max number of relevant past episodes to include. Default 5, min 1.
    pub fn with_top_k(mut self, top_k: usize) -> Result<Self, BrainError> {
        if top_k < 1 {
            return Err(BrainError::new("topK must be at least 1."));
        }
        self.top_k = top_k;
        Ok(self)
    }

    /// Sets the max characters taken from each episode's texts. Default 300, min 50.
    pub fn with_max_chars_per_entry(mut self, max_chars: usize) -> Result<Self, BrainError> {
        if max_chars < 50 {
            return Err(BrainError::new("maxChars must be at least 50."));
        }
        self.max_chars_per_entry = max_chars;
        Ok(self)
    }

    /// Builds the [`RagContextBuilder`] from the accumulated configuration.
    pub fn build(self) -> Result<RagContextBuilder, BrainError> {
        let store = self.store.ok_or_else(|| {
            BrainError::new(
                "An episodic memory store is required. Call with_store() or \
                 with_in_memory_store() before build().",
            )
        })?;
        Ok(RagContextBuilder::new(
            store,
            self.embedder,
            self.top_k,
            self.max_chars_per_entry,
        ))
    }
}
