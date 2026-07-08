//! llm_extractor_test.rs
//!
//! Verifies LlmKnowledgeGraphExtractor: parses a clean JSON array of triples,
//! tolerates prose/markdown-fence-wrapped JSON, defaults confidence when "c" is
//! missing/invalid, clamps out-of-range confidence, skips objects with blank
//! s/p/o, and returns [] on garbage / on an empty turn / on a failing generator.
//! Mirrors the TS pilot suite tests/llm_extractor.test.ts 1:1.

use std::sync::Mutex;

use circle_ai::brain::BrainError;
use circle_ai::inference::{GenerationOptions, IChatGenerator};
use circle_ai::memory::extractor::IKnowledgeGraphExtractor;
use circle_ai::memory::llm_extractor::LlmKnowledgeGraphExtractor;
use circle_ai::models::ChatMessage;

/// Minimal fake IChatGenerator that returns a canned reply, records the messages.
struct FakeChatGenerator {
    reply: String,
    last_messages: Mutex<Vec<ChatMessage>>,
}

impl FakeChatGenerator {
    fn new(reply: &str) -> Self {
        Self {
            reply: reply.to_string(),
            last_messages: Mutex::new(Vec::new()),
        }
    }

    fn last_messages(&self) -> Vec<ChatMessage> {
        self.last_messages.lock().unwrap().clone()
    }
}

impl IChatGenerator for FakeChatGenerator {
    type Error = BrainError;

    fn generate(
        &self,
        messages: &[ChatMessage],
        _opts: Option<&GenerationOptions>,
    ) -> Result<String, Self::Error> {
        *self.last_messages.lock().unwrap() = messages.to_vec();
        Ok(self.reply.clone())
    }

    fn stream(
        &self,
        _messages: &[ChatMessage],
        _opts: Option<&GenerationOptions>,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error> {
        let reply = self.reply.clone();
        Ok(Box::new(std::iter::once(Ok(reply))))
    }
}

/// A generator that always errors — exercises the graceful-degradation path.
struct ThrowingChatGenerator;

impl IChatGenerator for ThrowingChatGenerator {
    type Error = BrainError;

    fn generate(
        &self,
        _messages: &[ChatMessage],
        _opts: Option<&GenerationOptions>,
    ) -> Result<String, Self::Error> {
        Err(BrainError::new("model offline"))
    }

    fn stream(
        &self,
        _messages: &[ChatMessage],
        _opts: Option<&GenerationOptions>,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error> {
        Err(BrainError::new("model offline"))
    }
}

// ── clean JSON ────────────────────────────────────────────────────────────────

#[test]
fn parses_a_plain_json_array_of_triples() {
    let gen = FakeChatGenerator::new(
        "[{\"s\":\"Tony\",\"p\":\"has_daughter\",\"o\":\"Alex\",\"c\":0.9},\
         {\"s\":\"Alex\",\"p\":\"lives_in\",\"o\":\"Durban\",\"c\":0.5}]",
    );
    let ex = LlmKnowledgeGraphExtractor::new(gen);
    let triples = ex.extract_from_turn("hi", "ok", Some("ep1")).expect("extract");

    assert_eq!(triples.len(), 2);
    assert_eq!(triples[0].subject, "Tony");
    assert_eq!(triples[0].predicate, "has_daughter");
    assert_eq!(triples[0].object, "Alex");
    assert_eq!(triples[0].confidence, 0.9);
    assert_eq!(triples[0].source.as_deref(), Some("ep1"));
    assert_eq!(triples[1].object, "Durban");
    assert_eq!(triples[1].confidence, 0.5);
}

#[test]
fn sends_the_verbatim_system_prompt_and_user_assistant_framed_user_message() {
    let gen = FakeChatGenerator::new("[]");
    let ex = LlmKnowledgeGraphExtractor::new(gen);
    ex.extract_from_turn("the weather", "is sunny", Some("ep1"))
        .expect("extract");

    let msgs = ex.generator().last_messages();
    assert_eq!(msgs.len(), 2);
    assert_eq!(msgs[0].role, "system");
    assert!(msgs[0]
        .content
        .starts_with("You are a knowledge-graph extractor."));
    assert_eq!(msgs[1].role, "user");
    assert_eq!(msgs[1].content, "USER:\nthe weather\nASSISTANT:\nis sunny\n");
}

// ── defensive parsing ─────────────────────────────────────────────────────────

#[test]
fn extracts_json_embedded_in_prose_or_markdown_fences() {
    let gen = FakeChatGenerator::new(
        "Sure! Here are the triples:\n```json\n[{\"s\":\"Paris\",\"p\":\"capital_of\",\"o\":\"France\",\"c\":0.95}]\n```\nHope that helps.",
    );
    let ex = LlmKnowledgeGraphExtractor::new(gen);
    let triples = ex.extract_from_turn("u", "a", Some("ep2")).expect("extract");

    assert_eq!(triples.len(), 1);
    assert_eq!(triples[0].subject, "Paris");
    assert_eq!(triples[0].predicate, "capital_of");
    assert_eq!(triples[0].object, "France");
    assert_eq!(triples[0].confidence, 0.95);
}

#[test]
fn defaults_confidence_to_0_75_when_c_is_missing() {
    let gen = FakeChatGenerator::new("[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}]");
    let ex = LlmKnowledgeGraphExtractor::new(gen);
    let triples = ex.extract_from_turn("u", "a", Some("ep3")).expect("extract");
    assert_eq!(triples.len(), 1);
    assert_eq!(triples[0].confidence, 0.75);
}

#[test]
fn defaults_confidence_to_0_75_when_c_is_non_numeric() {
    let gen = FakeChatGenerator::new("[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\",\"c\":\"high\"}]");
    let ex = LlmKnowledgeGraphExtractor::new(gen);
    let triples = ex.extract_from_turn("u", "a", Some("ep3")).expect("extract");
    assert_eq!(triples[0].confidence, 0.75);
}

#[test]
fn clamps_confidence_into_0_1() {
    let gen = FakeChatGenerator::new(
        "[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\",\"c\":5},{\"s\":\"d\",\"p\":\"e\",\"o\":\"f\",\"c\":-2}]",
    );
    let ex = LlmKnowledgeGraphExtractor::new(gen);
    let triples = ex.extract_from_turn("u", "a", Some("ep3")).expect("extract");
    assert_eq!(triples[0].confidence, 1.0);
    assert_eq!(triples[1].confidence, 0.0);
}

#[test]
fn skips_objects_whose_spo_are_blank_or_missing() {
    let gen = FakeChatGenerator::new(
        "[{\"s\":\"\",\"p\":\"b\",\"o\":\"c\"},{\"s\":\"a\",\"p\":\"  \",\"o\":\"c\"},{\"s\":\"a\",\"p\":\"b\"},{\"s\":\"keep\",\"p\":\"p\",\"o\":\"o\"}]",
    );
    let ex = LlmKnowledgeGraphExtractor::new(gen);
    let triples = ex.extract_from_turn("u", "a", Some("ep3")).expect("extract");
    assert_eq!(triples.len(), 1);
    assert_eq!(triples[0].subject, "keep");
}

#[test]
fn skips_non_object_array_entries() {
    let gen = FakeChatGenerator::new("[1, \"two\", null, {\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}]");
    let ex = LlmKnowledgeGraphExtractor::new(gen);
    let triples = ex.extract_from_turn("u", "a", Some("ep3")).expect("extract");
    assert_eq!(triples.len(), 1);
    assert_eq!(triples[0].subject, "a");
}

// ── empty results ─────────────────────────────────────────────────────────────

#[test]
fn returns_empty_on_pure_garbage_no_brackets() {
    let gen = FakeChatGenerator::new("I could not find any facts, sorry.");
    let ex = LlmKnowledgeGraphExtractor::new(gen);
    assert!(ex.extract_from_turn("u", "a", Some("ep4")).expect("extract").is_empty());
}

#[test]
fn returns_empty_on_malformed_json_inside_brackets() {
    let gen = FakeChatGenerator::new("[{\"s\":\"a\", \"p\": }]");
    let ex = LlmKnowledgeGraphExtractor::new(gen);
    assert!(ex.extract_from_turn("u", "a", Some("ep4")).expect("extract").is_empty());
}

#[test]
fn returns_empty_when_the_json_is_an_object_not_an_array() {
    let gen = FakeChatGenerator::new("{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}");
    let ex = LlmKnowledgeGraphExtractor::new(gen);
    // No '[' before ']' — object braces only, so no valid slice.
    assert!(ex.extract_from_turn("u", "a", Some("ep4")).expect("extract").is_empty());
}

#[test]
fn returns_empty_when_both_user_and_assistant_text_are_blank_no_llm_call() {
    let gen = FakeChatGenerator::new("[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}]");
    let ex = LlmKnowledgeGraphExtractor::new(gen);
    assert!(ex.extract_from_turn("   ", "", None).expect("extract").is_empty());
    // No LLM call was made (blank-both short-circuits before generate()).
    assert!(ex.generator().last_messages().is_empty());
}

#[test]
fn returns_empty_when_the_generator_throws() {
    let ex = LlmKnowledgeGraphExtractor::new(ThrowingChatGenerator);
    assert!(ex.extract_from_turn("u", "a", Some("ep5")).expect("extract").is_empty());
}
