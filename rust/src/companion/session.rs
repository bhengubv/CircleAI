//! session.rs
//!
//! The conscious loop: a concrete [`ICompanionSession`] that recalls from fused
//! memory, persists each turn, and encodes it into the graph off the hot path.
//! Ported from CircleAI.Companion (CompanionSession) — the C# reference — and
//! mirrors the TypeScript pilot (companion/session.ts) and the Go port
//! (companion_session.go) 1:1.
//!
//! On every turn it (1) recalls the most relevant memories + the user's own facts
//! and injects them into the system prompt, (2) calls the generator, (3) persists
//! the exchange to episodic memory, and (4) hands it to the background encoder so
//! the knowledge graph fills for future associative recall.

use std::sync::Arc;

use chrono::Utc;
use uuid::Uuid;

use crate::brain::BrainError;
use crate::companion::belief::SelfBeliefStore;
use crate::companion::memory_encoder::CompanionMemoryEncoder;
use crate::companion::types::{
    CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind,
};
use crate::inference::IChatGenerator;
use crate::memory::episodic::InMemoryEpisodicStore;
use crate::memory::graph::MemoryHit;
use crate::memory::recall::IRecall;
use crate::memory::stores::EpisodicMemoryEntry;
use crate::models::ChatMessage;

/// Computes an embedding for the given text. Returns `None` when no embedding is
/// available (→ episodic recency recall). Optional.
pub type EmbedderFn =
    dyn Fn(&str) -> Result<Option<Vec<f32>>, BrainError> + Send + Sync;

/// Construction-time configuration for a [`CompanionSession`].
#[derive(Default)]
pub struct CompanionSessionOptions {
    pub session_id: String,
    pub identity_id: String,
    pub interface: InterfaceKind,
    pub display_name: String,
    pub preferred_language: Option<String>,
    /// A static persona hint block prepended to the system prompt.
    pub persona_hints: String,
    /// A static affect hint block prepended to the system prompt.
    pub affect_summary: String,
    pub active_goals: Vec<String>,
    /// How many memories to recall per turn. Default 5 (when 0).
    pub recall_top_k: usize,
    /// An optional app context stamped onto persisted episodes.
    pub app_context: Option<String>,
    /// The background graph/belief encoder. When `None`, turns are not encoded.
    pub encoder: Option<Arc<CompanionMemoryEncoder>>,
    /// Holds the user's own facts, surfaced into the system prompt.
    pub beliefs: Option<Arc<SelfBeliefStore>>,
    /// An optional embedder for associative episodic recall; `None` → recency.
    pub embedder: Option<Arc<EmbedderFn>>,
}

impl Default for InterfaceKind {
    fn default() -> Self {
        InterfaceKind::Headless
    }
}

/// A companion session that thinks with fused memory and remembers what it
/// learns. Generic over the concrete chat generator `G` (reuses the existing
/// [`IChatGenerator`] trait); tests inject a fake generator that captures the
/// messages it is handed.
pub struct CompanionSession<G: IChatGenerator> {
    generator: G,
    episodic: Arc<InMemoryEpisodicStore>,
    recall: Arc<dyn IRecall>,
    opts: CompanionSessionOptions,

    history: Vec<CompanionTurn>,
    context: CompanionContext,
}

struct PreparedTurn {
    messages: Vec<ChatMessage>,
    query_embedding: Option<Vec<f32>>,
    snippets: Vec<String>,
}

impl<G: IChatGenerator> CompanionSession<G> {
    /// Creates a session. `generator`, `episodic` and `recall` are required.
    pub fn new(
        generator: G,
        episodic: Arc<InMemoryEpisodicStore>,
        recall: Arc<dyn IRecall>,
        opts: CompanionSessionOptions,
    ) -> Result<Self, BrainError> {
        let context = build_context(&opts, &[]);
        Ok(Self {
            generator,
            episodic,
            recall,
            opts,
            history: Vec::new(),
            context,
        })
    }

    fn recall_top_k(&self) -> usize {
        if self.opts.recall_top_k == 0 {
            5
        } else {
            self.opts.recall_top_k
        }
    }

    /// Borrows the underlying chat generator. Useful for tests that inject a
    /// capturing generator and want to inspect the messages it was handed.
    pub fn generator(&self) -> &G {
        &self.generator
    }

    /// Recalls before the current turn is persisted, so recall draws on prior
    /// memory and never echoes the message back.
    fn prepare(&self, message: &str) -> Result<PreparedTurn, BrainError> {
        let query_embedding = match &self.opts.embedder {
            Some(embed) => embed(message)?,
            None => None,
        };

        let hits = self.recall.recall(
            message,
            query_embedding.as_deref(),
            self.recall_top_k(),
        )?;
        let snippets = snippets_from_hits(&hits);

        let mut messages: Vec<ChatMessage> =
            vec![ChatMessage::system(self.build_system_prompt(&snippets))];
        for turn in &self.history {
            messages.push(ChatMessage::new(turn.role.clone(), turn.content.clone()));
        }
        messages.push(ChatMessage::user(message));

        Ok(PreparedTurn {
            messages,
            query_embedding,
            snippets,
        })
    }

    fn record_turn(
        &mut self,
        user_text: &str,
        reply: &str,
        query_embedding: Option<Vec<f32>>,
        snippets: Vec<String>,
    ) -> Result<(), BrainError> {
        let episode_id = Uuid::new_v4();
        let entry = EpisodicMemoryEntry {
            id: episode_id,
            recorded_at_utc: Utc::now(),
            user_text: user_text.to_string(),
            assistant_text: reply.to_string(),
            app_context: self.opts.app_context.clone(),
            embedding: query_embedding,
            tags: None,
        };
        self.episodic.add_shared(entry)?;

        // Off the hot path: fill the graph + form attributed beliefs for next time.
        if let Some(encoder) = &self.opts.encoder {
            encoder.enqueue(user_text, reply, &episode_id.to_string());
        }

        let now = Utc::now();
        self.history.push(CompanionTurn {
            role: "user".to_string(),
            content: user_text.to_string(),
            timestamp: now,
        });
        self.history.push(CompanionTurn {
            role: "assistant".to_string(),
            content: reply.to_string(),
            timestamp: now,
        });
        self.context = build_context(&self.opts, &snippets);
        Ok(())
    }

    fn build_system_prompt(&self, snippets: &[String]) -> String {
        let mut parts: Vec<String> = Vec::new();
        if !self.opts.persona_hints.trim().is_empty() {
            parts.push(self.opts.persona_hints.trim().to_string());
        }
        if !self.opts.affect_summary.trim().is_empty() {
            parts.push(self.opts.affect_summary.trim().to_string());
        }

        let facts = self.user_facts();
        if !facts.is_empty() {
            let mut b = String::from("[What you know about the user]");
            for f in &facts {
                b.push_str("\n- ");
                b.push_str(f);
            }
            parts.push(b);
        }
        if !snippets.is_empty() {
            let mut b = String::from("[Relevant memories]");
            for snip in snippets {
                b.push_str("\n- ");
                b.push_str(snip);
            }
            parts.push(b);
        }
        parts.join("\n\n")
    }

    fn user_facts(&self) -> Vec<String> {
        match &self.opts.beliefs {
            None => Vec::new(),
            Some(beliefs) => beliefs
                .self_facts()
                .into_iter()
                .map(|f| f.object)
                .collect(),
        }
    }
}

impl<G: IChatGenerator> ICompanionSession for CompanionSession<G>
where
    G::Error: 'static,
{
    type Error = BrainError;

    fn session_id(&self) -> &str {
        &self.opts.session_id
    }

    fn identity_id(&self) -> &str {
        &self.opts.identity_id
    }

    fn interface(&self) -> InterfaceKind {
        self.opts.interface
    }

    fn send(&mut self, message: &str) -> Result<String, Self::Error> {
        let prepared = self.prepare(message)?;
        let reply = self
            .generator
            .generate(&prepared.messages, None)
            .map_err(|e| BrainError::new(e.to_string()))?;
        self.record_turn(
            message,
            &reply,
            prepared.query_embedding,
            prepared.snippets,
        )?;
        Ok(reply)
    }

    fn stream(
        &mut self,
        message: &str,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error> {
        let prepared = self.prepare(message)?;

        // Drive the generator's stream to completion, accumulating the reply so
        // the full text is persisted once the stream ends (mirrors the reference:
        // stream chunks out, persist the accumulated reply on completion).
        let stream = self
            .generator
            .stream(&prepared.messages, None)
            .map_err(|e| BrainError::new(e.to_string()))?;

        let mut chunks: Vec<String> = Vec::new();
        let mut accumulated = String::new();
        for item in stream {
            let chunk = item.map_err(|e| BrainError::new(e.to_string()))?;
            accumulated.push_str(&chunk);
            chunks.push(chunk);
        }

        self.record_turn(
            message,
            &accumulated,
            prepared.query_embedding,
            prepared.snippets,
        )?;

        Ok(Box::new(chunks.into_iter().map(Ok)))
    }

    fn agent(&mut self, instruction: &str) -> Result<String, Self::Error> {
        // Pilot: no tool-execution loop yet — agentic tool calling is a later
        // slice. Falls back to a plain reply so the surface is complete.
        self.send(instruction)
    }

    fn get_context(&self) -> &CompanionContext {
        &self.context
    }

    fn refresh_context(&mut self) -> Result<(), Self::Error> {
        let hits = self.recall.recall("", None, self.recall_top_k())?;
        let snippets = snippets_from_hits(&hits);
        self.context = build_context(&self.opts, &snippets);
        Ok(())
    }

    fn history(&self) -> &[CompanionTurn] {
        &self.history
    }

    fn signal_feedback(&mut self, _positive: bool, _note: Option<&str>) -> Result<(), Self::Error> {
        // Pilot: accepted but not yet routed to a feedback store / affect update.
        Ok(())
    }
}

fn build_context(opts: &CompanionSessionOptions, snippets: &[String]) -> CompanionContext {
    CompanionContext {
        identity_id: opts.identity_id.clone(),
        display_name: opts.display_name.clone(),
        preferred_language: opts.preferred_language.clone(),
        interface: opts.interface,
        persona_hints: opts.persona_hints.clone(),
        affect_summary: opts.affect_summary.clone(),
        recent_memory_snippets: snippets.to_vec(),
        active_goals: opts.active_goals.clone(),
        context_built_at: Utc::now(),
    }
}

fn snippets_from_hits(hits: &[MemoryHit]) -> Vec<String> {
    hits.iter().map(|h| h.item.text.clone()).collect()
}
