//! graph.rs
//!
//! Personal knowledge graph + HippoRAG multi-hop recall (Personalised PageRank).
//!
//! Ported from CircleAI.Domain (MemoryItem / MemoryHit / IHippoRagStore) and
//! CircleAI.Companion (SqliteKnowledgeGraph, SqliteHippoRagStore) — the C#
//! reference — and mirrors the TypeScript pilot (memory/graph.ts) and the Go
//! port (memory_graph.go) 1:1. This is the in-memory port: identical algorithms,
//! no SQLite.
//!
//! HippoRAG (Wang et al. 2024): each memory item is a node in the personal KG;
//! at recall time the query's entities seed a Personalised PageRank walk, and the
//! nodes with the highest steady-state probability are the multi-hop matches.

use chrono::{DateTime, Utc};
use std::collections::{HashMap, HashSet};
use std::sync::Mutex;

use crate::brain::BrainError;

// ---------------------------------------------------------------------------
// Shared recall currency (CircleAI.Domain Contracts)
// ---------------------------------------------------------------------------

/// One recallable memory with optional string metadata.
#[derive(Debug, Clone, PartialEq)]
pub struct MemoryItem {
    pub id: String,
    pub text: String,
    pub metadata: Option<HashMap<String, String>>,
}

impl MemoryItem {
    pub fn new(id: impl Into<String>, text: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            text: text.into(),
            metadata: None,
        }
    }

    pub fn with_metadata(
        id: impl Into<String>,
        text: impl Into<String>,
        metadata: HashMap<String, String>,
    ) -> Self {
        Self {
            id: id.into(),
            text: text.into(),
            metadata: Some(metadata),
        }
    }
}

/// A recalled memory paired with its relevance score.
#[derive(Debug, Clone, PartialEq)]
pub struct MemoryHit {
    pub item: MemoryItem,
    pub score: f64,
}

/// The HippoRAG-pattern memory + knowledge-graph + Personalised PageRank recall
/// seam. `Send + Sync` so it can be shared behind an `Arc`.
pub trait IHippoRagStore: Send + Sync {
    /// Identifies the backing implementation.
    fn backend_id(&self) -> &str;
    /// Ensures the memory item exists as a node the walker can land on.
    fn index(&self, item: &MemoryItem) -> Result<(), BrainError>;
    /// Seeds a Personalised PageRank walk from the query's terms and returns the
    /// `top_k` reached nodes.
    fn multi_hop_recall(&self, query: &str, top_k: usize) -> Result<Vec<MemoryHit>, BrainError>;
}

// ---------------------------------------------------------------------------
// Knowledge graph node + triple
// ---------------------------------------------------------------------------

/// A node in the personal knowledge graph.
#[derive(Debug, Clone, PartialEq)]
pub struct KnowledgeNode {
    pub id: String,
    pub kind: String,
    pub name: String,
    pub properties: HashMap<String, String>,
}

impl KnowledgeNode {
    pub fn new(
        id: impl Into<String>,
        kind: impl Into<String>,
        name: impl Into<String>,
    ) -> Self {
        Self {
            id: id.into(),
            kind: kind.into(),
            name: name.into(),
            properties: HashMap::new(),
        }
    }
}

/// One (subject, predicate, object) triple with provenance (source + confidence).
#[derive(Debug, Clone, PartialEq)]
pub struct KnowledgeTriple {
    pub subject: String,
    pub predicate: String,
    pub object: String,
    pub source: Option<String>,
    pub confidence: f64,
    pub recorded_at_utc: DateTime<Utc>,
}

const TRIPLE_SEP: &str = " ";

/// An in-memory personal knowledge graph. Triples are keyed by
/// (subject, predicate, object) — re-adding the same triple replaces its
/// provenance, matching the C# SQLite store's INSERT OR REPLACE on the composite
/// primary key. Safe for concurrent use.
#[derive(Debug, Default)]
pub struct KnowledgeGraph {
    inner: Mutex<GraphInner>,
}

#[derive(Debug, Default)]
struct GraphInner {
    nodes: HashMap<String, KnowledgeNode>,
    triples: HashMap<String, KnowledgeTriple>,
}

impl KnowledgeGraph {
    /// Returns an empty knowledge graph.
    pub fn new() -> Self {
        Self::default()
    }

    /// Inserts or replaces a node by id.
    pub fn upsert_node(&self, node: KnowledgeNode) -> Result<(), BrainError> {
        if node.id.trim().is_empty() {
            return Err(BrainError::new("node.ID required"));
        }
        let mut inner = self.inner.lock().unwrap();
        inner.nodes.insert(node.id.clone(), node);
        Ok(())
    }

    /// Returns the node with the given id, or `None`.
    pub fn get_node(&self, id: &str) -> Option<KnowledgeNode> {
        let inner = self.inner.lock().unwrap();
        inner.nodes.get(id).cloned()
    }

    /// Adds (or replaces) a triple with full provenance. Re-adding the same
    /// (subject, predicate, object) replaces the prior provenance.
    pub fn add_triple(
        &self,
        subject: &str,
        predicate: &str,
        object: &str,
        source: Option<&str>,
        confidence: f64,
    ) -> Result<(), BrainError> {
        if subject.trim().is_empty() {
            return Err(BrainError::new("subject required"));
        }
        if predicate.trim().is_empty() {
            return Err(BrainError::new("predicate required"));
        }
        if object.trim().is_empty() {
            return Err(BrainError::new("object required"));
        }
        if !(0.0..=1.0).contains(&confidence) {
            return Err(BrainError::new("confidence must be in [0,1]"));
        }

        let key = format!("{subject}{TRIPLE_SEP}{predicate}{TRIPLE_SEP}{object}");
        let triple = KnowledgeTriple {
            subject: subject.to_string(),
            predicate: predicate.to_string(),
            object: object.to_string(),
            source: source.map(|s| s.to_string()),
            confidence,
            recorded_at_utc: Utc::now(),
        };
        let mut inner = self.inner.lock().unwrap();
        inner.triples.insert(key, triple);
        Ok(())
    }

    /// Returns every triple — used by HippoRAG for the graph walk.
    pub fn all_triples(&self) -> Vec<KnowledgeTriple> {
        let inner = self.inner.lock().unwrap();
        inner.triples.values().cloned().collect()
    }

    /// Returns the raw triples for one subject (inspection / debugging).
    pub fn read_triples(&self, subject: &str) -> Result<Vec<KnowledgeTriple>, BrainError> {
        if subject.trim().is_empty() {
            return Err(BrainError::new("subject required"));
        }
        let inner = self.inner.lock().unwrap();
        Ok(inner
            .triples
            .values()
            .filter(|t| t.subject == subject)
            .cloned()
            .collect())
    }
}

// ---------------------------------------------------------------------------
// HippoRagStore — Personalised PageRank multi-hop recall
// ---------------------------------------------------------------------------

/// Real HippoRAG recall over a [`KnowledgeGraph`]. It walks the personal graph
/// via Personalised PageRank (power iteration) seeded from the query's terms.
///
/// Three precision guarantees carried from the C# reference:
///  1. No query term touches the graph → returns empty (never fabricates an
///     association from arbitrary nodes).
///  2. Seed nodes are excluded from results (recall returns the *associated*
///     nodes the walk reached, not the query echoed back).
///  3. Edge spread is confidence-weighted — a high-confidence edge carries more
///     of the walk's mass than a guessed one, so a shaky belief does not steer
///     recall like a stated fact.
pub struct HippoRagStore {
    kg: std::sync::Arc<KnowledgeGraph>,
    walk_iterations: usize,
    damping: f64,
}

impl HippoRagStore {
    /// Returns a HippoRAG store over `kg` with default tuning (walk iterations
    /// 32, damping 0.85).
    pub fn new(kg: std::sync::Arc<KnowledgeGraph>) -> Result<Self, BrainError> {
        Self::tuned(kg, 32, 0.85)
    }

    /// Returns a HippoRAG store with explicit walk iterations and damping factor.
    pub fn tuned(
        kg: std::sync::Arc<KnowledgeGraph>,
        walk_iterations: usize,
        damping: f64,
    ) -> Result<Self, BrainError> {
        Ok(Self {
            kg,
            walk_iterations,
            damping,
        })
    }
}

impl IHippoRagStore for HippoRagStore {
    fn backend_id(&self) -> &str {
        "inmemory-hippo-ppr"
    }

    /// Ensures the memory item exists as a node so the walker can land on it.
    /// The graph itself is populated by the KnowledgeGraphExtractor.
    fn index(&self, item: &MemoryItem) -> Result<(), BrainError> {
        if item.id.trim().is_empty() {
            return Err(BrainError::new("item.ID required"));
        }
        self.kg
            .add_triple(&item.id, "memory_text", &item.text, Some(&item.id), 1.0)?;
        if let Some(meta) = &item.metadata {
            for (k, v) in meta {
                self.kg.add_triple(&item.id, k, v, Some(&item.id), 0.9)?;
            }
        }
        Ok(())
    }

    /// Seeds a Personalised PageRank walk from the query's terms and returns the
    /// `top_k` reached nodes (seeds excluded).
    fn multi_hop_recall(&self, query: &str, top_k: usize) -> Result<Vec<MemoryHit>, BrainError> {
        if query.trim().is_empty() {
            return Err(BrainError::new("query required"));
        }
        if top_k == 0 {
            return Err(BrainError::new("topK must be positive"));
        }

        let triples = self.kg.all_triples();
        if triples.is_empty() {
            return Ok(Vec::new());
        }

        // Adjacency list: subject -> [(object, confidence)].
        let mut outgoing: HashMap<String, Vec<(String, f64)>> = HashMap::new();
        let mut all_nodes: HashSet<String> = HashSet::new();
        for t in &triples {
            all_nodes.insert(t.subject.clone());
            all_nodes.insert(t.object.clone());
            outgoing
                .entry(t.subject.clone())
                .or_default()
                .push((t.object.clone(), t.confidence));
        }

        // Seed the personalisation vector from query terms that appear as nodes.
        let query_terms: HashSet<String> = split_non_alnum(query)
            .into_iter()
            .filter(|t| !t.is_empty())
            .map(|t| t.to_lowercase())
            .collect();
        let mut seed_nodes: Vec<String> = all_nodes
            .iter()
            .filter(|n| query_terms.contains(&n.to_lowercase()))
            .cloned()
            .collect();
        // Deterministic seed ordering (Go iterates a map here; ordering does not
        // change the maths but we keep it stable for reproducibility).
        seed_nodes.sort();

        // Precision guarantee 1: no genuine association → return nothing.
        if seed_nodes.is_empty() {
            return Ok(Vec::new());
        }

        let mut rank: HashMap<String, f64> =
            all_nodes.iter().map(|n| (n.clone(), 0.0)).collect();
        let seed_mass = 1.0 / seed_nodes.len() as f64;
        for s in &seed_nodes {
            rank.insert(s.clone(), seed_mass);
        }

        // Power-iteration Personalised PageRank.
        for _iter in 0..self.walk_iterations {
            let mut next: HashMap<String, f64> =
                all_nodes.iter().map(|n| (n.clone(), 0.0)).collect();

            // Random-jump component (personalisation): mass returns to the seeds.
            for seed in &seed_nodes {
                *next.get_mut(seed).unwrap() += (1.0 - self.damping) * seed_mass;
            }

            // Walk component.
            for (node, mass) in &rank {
                if *mass <= 0.0 {
                    continue;
                }
                match outgoing.get(node) {
                    None => {
                        // Dangling node: redistribute via personalisation.
                        for seed in &seed_nodes {
                            *next.get_mut(seed).unwrap() +=
                                (self.damping * *mass) / seed_nodes.len() as f64;
                        }
                    }
                    Some(nbrs) if nbrs.is_empty() => {
                        for seed in &seed_nodes {
                            *next.get_mut(seed).unwrap() +=
                                (self.damping * *mass) / seed_nodes.len() as f64;
                        }
                    }
                    Some(nbrs) => {
                        // Precision guarantee 3: confidence-weighted spread. With
                        // equal confidences this reduces to the plain 1/count split.
                        let total_conf: f64 = nbrs.iter().map(|(_, c)| *c).sum();
                        for (nbr, conf) in nbrs {
                            let weight = if total_conf > 0.0 {
                                *conf / total_conf
                            } else {
                                1.0 / nbrs.len() as f64
                            };
                            *next.get_mut(nbr).unwrap() += self.damping * *mass * weight;
                        }
                    }
                }
            }

            rank = next;
        }

        // Precision guarantee 2: exclude the seeds — they are the query's own terms.
        let seed_set: HashSet<&String> = seed_nodes.iter().collect();

        let mut ranked: Vec<(String, f64)> = rank
            .into_iter()
            .filter(|(key, value)| *value > 0.0 && !seed_set.contains(key))
            .collect();
        // Highest PPR mass first. Ties broken by key for deterministic order.
        ranked.sort_by(|a, b| {
            if a.1 != b.1 {
                b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal)
            } else {
                a.0.cmp(&b.0)
            }
        });
        ranked.truncate(top_k);

        let hits = ranked
            .into_iter()
            .map(|(key, value)| {
                let node = self.kg.get_node(&key);
                let mut text = key.clone();
                let mut props: Option<HashMap<String, String>> = None;
                if let Some(n) = node {
                    if !n.name.is_empty() {
                        text = n.name.clone();
                    }
                    props = Some(n.properties);
                }
                MemoryHit {
                    item: MemoryItem {
                        id: key,
                        text,
                        metadata: props,
                    },
                    score: value,
                }
            })
            .collect();
        Ok(hits)
    }
}

/// Splits `s` on any run of non-alphanumeric ASCII characters, matching the
/// C#/TS regex `[^A-Za-z0-9]+` and the Go `splitNonAlnum`.
pub(crate) fn split_non_alnum(s: &str) -> Vec<String> {
    s.split(|c: char| !c.is_ascii_alphanumeric())
        .filter(|tok| !tok.is_empty())
        .map(|tok| tok.to_string())
        .collect()
}
