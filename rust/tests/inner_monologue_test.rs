//! inner_monologue_test.rs
//!
//! Verifies the companion inner monologue: TemplateInnerMonologue (model-free
//! narrative frames) and ReasoningLoopInnerMonologue (reasoning-capable LLM
//! loop that prefers the reasoning trace over visible content). Mirrors the C#
//! IInnerMonologue.ReflectAsync behaviour.

use std::convert::Infallible;
use std::fmt;

use circle_ai::companion::inner_monologue::{
    IInnerMonologue, ReasoningLoopInnerMonologue, TemplateInnerMonologue,
};
use circle_ai::inference::{ChatMessage, GenerationOptions, IChatGenerator};
use circle_ai::models_v15::ChatFragment;

// ── TemplateInnerMonologue ─────────────────────────────────────────────────

#[test]
fn template_reflection_is_non_empty_and_fills_a_frame() {
    let m = TemplateInnerMonologue::new();
    let r = m.reflect("{\"user\":\"asked about weather\"}");
    assert!(!r.thought.is_empty());
    // No placeholder tokens should survive.
    assert!(!r.thought.contains("{summary}"));
    assert!(!r.thought.contains("{direction}"));
}

#[test]
fn template_error_context_steers_direction_to_diagnosis() {
    let m = TemplateInnerMonologue::new();
    let r = m.reflect("{\"status\":\"error: null reference\"}");
    assert!(
        r.thought.contains("diagnose the failure first"),
        "error context should map to the diagnosis direction, got: {}",
        r.thought
    );
}

#[test]
fn template_goal_context_steers_direction_to_the_goal() {
    let m = TemplateInnerMonologue::new();
    let r = m.reflect("{\"goal\":\"ship the release\"}");
    assert!(r.thought.contains("advance toward the stated goal"), "{}", r.thought);
}

#[test]
fn template_user_context_steers_direction_to_the_user() {
    let m = TemplateInnerMonologue::new();
    // Contains "user" but not "error"/"goal".
    let r = m.reflect("{\"speaker\":\"user says hi\"}");
    assert!(r.thought.contains("respond to the user"), "{}", r.thought);
}

#[test]
fn template_bland_context_falls_back_to_gathering_context() {
    let m = TemplateInnerMonologue::new();
    let r = m.reflect("{\"weather\":\"sunny\"}");
    assert!(r.thought.contains("gather more context"), "{}", r.thought);
}

#[test]
fn template_is_deterministic_for_the_same_context() {
    let m = TemplateInnerMonologue::new();
    let a = m.reflect("{\"topic\":\"budget planning session\"}");
    let b = m.reflect("{\"topic\":\"budget planning session\"}");
    // Same content ⇒ same frame ⇒ identical thought text.
    assert_eq!(a.thought, b.thought);
}

// ── ReasoningLoopInnerMonologue ────────────────────────────────────────────

/// A scripted chat generator that replays a fixed fragment list.
struct FakeGenerator {
    fragments: Vec<ChatFragment>,
}

#[derive(Debug)]
struct FakeErr;
impl fmt::Display for FakeErr {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("fake")
    }
}
impl std::error::Error for FakeErr {}

impl IChatGenerator for FakeGenerator {
    type Error = FakeErr;

    fn generate(
        &self,
        _messages: &[ChatMessage],
        _opts: Option<&GenerationOptions>,
    ) -> Result<String, Self::Error> {
        Ok(self
            .fragments
            .iter()
            .filter(|f| matches!(f.kind, circle_ai::models_v15::ChatFragmentKind::Content))
            .map(|f| f.text.clone())
            .collect())
    }

    fn stream(
        &self,
        _messages: &[ChatMessage],
        _opts: Option<&GenerationOptions>,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error> {
        let items: Vec<Result<String, Self::Error>> = self
            .fragments
            .iter()
            .filter(|f| matches!(f.kind, circle_ai::models_v15::ChatFragmentKind::Content))
            .map(|f| Ok(f.text.clone()))
            .collect();
        Ok(Box::new(items.into_iter()))
    }

    fn stream_fragments(
        &self,
        _messages: &[ChatMessage],
        _opts: Option<&GenerationOptions>,
    ) -> Result<Box<dyn Iterator<Item = Result<ChatFragment, Self::Error>>>, Self::Error> {
        let items: Vec<Result<ChatFragment, Self::Error>> =
            self.fragments.iter().cloned().map(Ok).collect();
        Ok(Box::new(items.into_iter()))
    }
}

/// A generator whose fragment stream always errors immediately.
struct ErroringGenerator;
impl IChatGenerator for ErroringGenerator {
    type Error = Infallible;
    fn generate(
        &self,
        _m: &[ChatMessage],
        _o: Option<&GenerationOptions>,
    ) -> Result<String, Self::Error> {
        Ok(String::new())
    }
    fn stream(
        &self,
        _m: &[ChatMessage],
        _o: Option<&GenerationOptions>,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error> {
        Ok(Box::new(std::iter::empty()))
    }
    // stream_fragments uses the default impl, wrapping the empty content stream.
}

#[test]
fn reasoning_loop_prefers_the_reasoning_trace() {
    let gen = FakeGenerator {
        fragments: vec![
            ChatFragment::reasoning("  weighing the options carefully  "),
            ChatFragment::content("Take a walk."),
        ],
    };
    let m = ReasoningLoopInnerMonologue::new(gen);
    let r = m.reflect("{\"mood\":\"restless\"}");
    // Reasoning wins over content, and is trimmed.
    assert_eq!(r.thought, "weighing the options carefully");
}

#[test]
fn reasoning_loop_falls_back_to_content_without_reasoning() {
    let gen = FakeGenerator {
        fragments: vec![ChatFragment::content("  Just breathe.  ")],
    };
    let m = ReasoningLoopInnerMonologue::new(gen);
    let r = m.reflect("{}");
    assert_eq!(r.thought, "Just breathe.");
}

#[test]
fn reasoning_loop_falls_back_to_placeholder_when_empty() {
    let gen = FakeGenerator { fragments: vec![] };
    let m = ReasoningLoopInnerMonologue::new(gen);
    let r = m.reflect("{}");
    assert_eq!(r.thought, "(no inner state)");
}

#[test]
fn reasoning_loop_survives_a_broken_stream() {
    // The default stream_fragments over an empty content stream yields nothing;
    // the loop must degrade to the placeholder rather than panic.
    let m = ReasoningLoopInnerMonologue::new(ErroringGenerator);
    let r = m.reflect("{\"x\":1}");
    assert_eq!(r.thought, "(no inner state)");
}
