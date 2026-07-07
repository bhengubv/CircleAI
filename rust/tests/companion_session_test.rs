//! companion_session_test.rs
//!
//! Verifies the concrete CompanionSession end-to-end: a turn recalls fused memory
//! + the user's own facts into the system prompt, calls the generator, persists
//! the exchange, hands it to the background encoder, recalls it on a later turn,
//! and streams. Mirrors the TS pilot suite tests/companion_session.test.ts and
//! the Go suite companion_session_test.go 1:1.

use std::sync::{Arc, Mutex};

use chrono::{TimeZone, Utc};
use circle_ai::brain::BrainError;
use circle_ai::companion::belief::{HeuristicBeliefExtractor, IBeliefExtractor, SelfBeliefStore};
use circle_ai::companion::memory_encoder::CompanionMemoryEncoder;
use circle_ai::companion::session::{CompanionSession, CompanionSessionOptions};
use circle_ai::companion::types::{ICompanionSession, InterfaceKind};
use circle_ai::inference::{GenerationOptions, IChatGenerator};
use circle_ai::memory::episodic::InMemoryEpisodicStore;
use circle_ai::memory::extractor::HeuristicKnowledgeGraphExtractor;
use circle_ai::memory::graph::KnowledgeGraph;
use circle_ai::memory::recall::FusedRecall;
use circle_ai::memory::EpisodicMemoryEntry;
use circle_ai::models::ChatMessage;
use uuid::Uuid;

/// Records the prompt it was handed and returns a canned reply / chunks.
struct CapturingGenerator {
    reply: String,
    chunks: Option<Vec<String>>,
    last_msgs: Mutex<Vec<ChatMessage>>,
}

impl CapturingGenerator {
    fn new(reply: &str) -> Self {
        Self {
            reply: reply.to_string(),
            chunks: None,
            last_msgs: Mutex::new(Vec::new()),
        }
    }

    fn with_chunks(reply: &str, chunks: Vec<&str>) -> Self {
        Self {
            reply: reply.to_string(),
            chunks: Some(chunks.into_iter().map(|c| c.to_string()).collect()),
            last_msgs: Mutex::new(Vec::new()),
        }
    }

    fn last_msgs(&self) -> Vec<ChatMessage> {
        self.last_msgs.lock().unwrap().clone()
    }
}

impl IChatGenerator for CapturingGenerator {
    type Error = BrainError;

    fn generate(
        &self,
        messages: &[ChatMessage],
        _opts: Option<&GenerationOptions>,
    ) -> Result<String, Self::Error> {
        *self.last_msgs.lock().unwrap() = messages.to_vec();
        Ok(self.reply.clone())
    }

    fn stream(
        &self,
        messages: &[ChatMessage],
        _opts: Option<&GenerationOptions>,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error> {
        *self.last_msgs.lock().unwrap() = messages.to_vec();
        let chunks = self
            .chunks
            .clone()
            .unwrap_or_else(|| vec![self.reply.clone()]);
        Ok(Box::new(chunks.into_iter().map(Ok)))
    }
}

fn record_self_fact(beliefs: &SelfBeliefStore, text: &str) {
    let bx = HeuristicBeliefExtractor::new();
    let bs = bx.extract(text, Some("t0")).expect("Extract");
    for b in bs {
        beliefs.record(b).expect("Record");
    }
}

struct SessionExtras {
    beliefs: Option<Arc<SelfBeliefStore>>,
    encoder: Option<Arc<CompanionMemoryEncoder>>,
}

impl Default for SessionExtras {
    fn default() -> Self {
        Self {
            beliefs: None,
            encoder: None,
        }
    }
}

fn make_session(
    gen: CapturingGenerator,
    episodic: Arc<InMemoryEpisodicStore>,
    extras: SessionExtras,
) -> CompanionSession<CapturingGenerator> {
    let recall = Arc::new(
        FusedRecall::new(episodic.clone(), None, None).expect("NewFusedRecall"),
    );
    CompanionSession::new(
        gen,
        episodic,
        recall,
        CompanionSessionOptions {
            session_id: "s1".to_string(),
            identity_id: "u1".to_string(),
            interface: InterfaceKind::Mobile,
            beliefs: extras.beliefs,
            encoder: extras.encoder,
            ..Default::default()
        },
    )
    .expect("NewCompanionSession")
}

fn seed_entry(user_text: &str, assistant_text: &str) -> EpisodicMemoryEntry {
    EpisodicMemoryEntry {
        id: Uuid::new_v4(),
        recorded_at_utc: Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap(),
        user_text: user_text.to_string(),
        assistant_text: assistant_text.to_string(),
        app_context: None,
        embedding: None,
        tags: None,
    }
}

// ── Send path ────────────────────────────────────────────────────────────────

#[test]
fn injects_recalled_memories_and_user_facts_into_the_system_prompt() {
    let episodic = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    episodic.add_shared(seed_entry("I have a peanut allergy", "Noted")).expect("Add");
    let beliefs = Arc::new(SelfBeliefStore::new());
    record_self_fact(&beliefs, "i am vegetarian");

    let gen = CapturingGenerator::new("Here are some options");
    let mut session = make_session(
        gen,
        episodic,
        SessionExtras {
            beliefs: Some(Arc::clone(&beliefs)),
            ..Default::default()
        },
    );

    let reply = session.send("what can I eat?").expect("Send");
    assert_eq!(reply, "Here are some options");

    let msgs = session.generator().last_msgs();
    let system = &msgs[0];
    assert_eq!(system.role, "system", "first message role should be system");
    assert!(system.content.contains("peanut allergy"), "recalled memory should be in the prompt:\n{}", system.content);
    assert!(system.content.contains("vegetarian"), "user fact should be in the prompt:\n{}", system.content);
    let last = &msgs[msgs.len() - 1];
    assert_eq!(last.content, "what can I eat?", "last message should be the user message");
}

#[test]
fn persists_the_turn_and_grows_the_history() {
    let episodic = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let mut session = make_session(CapturingGenerator::new("ok"), Arc::clone(&episodic), SessionExtras::default());

    session.send("hello").expect("Send");
    assert_eq!(episodic.count_shared().unwrap(), 1);
    let hist = session.history();
    assert_eq!(hist.len(), 2);
    assert_eq!(hist[0].role, "user");
    assert_eq!(hist[1].role, "assistant");
}

#[test]
fn recalls_a_prior_turn_on_a_later_turn() {
    let episodic = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let mut session = make_session(CapturingGenerator::new("noted"), episodic, SessionExtras::default());

    session.send("my favourite colour is blue").expect("Send");
    session.send("what's my favourite colour?").expect("Send");

    let msgs = session.generator().last_msgs();
    let system = &msgs[0];
    assert!(
        system.content.contains("favourite colour is blue"),
        "the earlier turn should be recalled:\n{}",
        system.content
    );
}

#[test]
fn hands_the_turn_to_the_background_encoder_filling_the_graph() {
    let episodic = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let graph = Arc::new(KnowledgeGraph::new());
    let encoder = CompanionMemoryEncoder::new(
        Arc::new(HeuristicKnowledgeGraphExtractor::new()),
        Arc::clone(&graph),
        None,
        None,
        0,
    )
    .expect("NewCompanionMemoryEncoder");
    let mut session = make_session(
        CapturingGenerator::new("ok"),
        episodic,
        SessionExtras {
            encoder: Some(Arc::clone(&encoder)),
            ..Default::default()
        },
    );

    session.send("remember my dentist appointment").expect("Send");
    encoder.close().expect("encoder.Close");

    let found = graph.all_triples().iter().any(|t| t.object == "dentist");
    assert!(found, "the encoder should have extracted the turn into the graph");
}

// ── Stream & context ─────────────────────────────────────────────────────────

#[test]
fn streams_chunks_and_still_persists_the_full_reply() {
    let episodic = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let gen = CapturingGenerator::with_chunks("unused", vec!["Hel", "lo"]);
    let mut session = make_session(gen, Arc::clone(&episodic), SessionExtras::default());

    let stream = session.stream("hi").expect("stream");
    let chunks: Vec<String> = stream.map(|r| r.expect("chunk")).collect();

    assert_eq!(chunks, vec!["Hel", "lo"]);
    assert_eq!(episodic.count_shared().unwrap(), 1);
    let hist = session.history();
    assert_eq!(hist.len(), 2);
    assert_eq!(hist[1].content, "Hello", "accumulated reply should be persisted");
}

#[test]
fn get_context_reflects_the_memories_recalled_on_the_last_turn() {
    let episodic = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    episodic.add_shared(seed_entry("I live in Durban", "Nice")).expect("Add");
    let mut session = make_session(CapturingGenerator::new("ok"), episodic, SessionExtras::default());

    session.send("where do I live?").expect("Send");
    let snippets = &session.get_context().recent_memory_snippets;
    assert!(
        snippets.iter().any(|s| s == "I live in Durban"),
        "context snippets should include the recalled memory: {snippets:?}"
    );
}

#[test]
fn agent_returns_a_reply_and_persists_no_tool_loop_in_the_pilot() {
    let episodic = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let mut session = make_session(CapturingGenerator::new("done"), Arc::clone(&episodic), SessionExtras::default());
    let reply = session.agent("do the thing").expect("Agent");
    assert_eq!(reply, "done");
    assert_eq!(episodic.count_shared().unwrap(), 1);
}
