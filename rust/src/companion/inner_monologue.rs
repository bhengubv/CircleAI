//! inner_monologue.rs
//!
//! Self-reflection / inner monologue. Port of the C# reference
//! (CircleAI.Companion.HerJarvis `IInnerMonologue` + `TemplateInnerMonologue`,
//! and CircleAI.Companion `ReasoningLoopInnerMonologue`) 1:1.
//!
//! Two concrete engines:
//!   * [`TemplateInnerMonologue`] — narrative-template reflection over the raw
//!     context JSON (model-free, deterministic).
//!   * [`ReasoningLoopInnerMonologue`] — an o1 / DeepSeek-R1 style loop that
//!     drives an [`IChatGenerator`] and captures its `Reasoning`-kind fragments
//!     as the inner monologue, falling back to the visible `Content`.

use chrono::{DateTime, Utc};

use crate::inference::{IChatGenerator, GenerationOptions};
use crate::models::ChatMessage;
use crate::models_v15::ChatFragmentKind;

/// A single reflective thought captured at an instant.
#[derive(Debug, Clone, PartialEq)]
pub struct SelfReflection {
    pub thought: String,
    pub at: DateTime<Utc>,
}

impl SelfReflection {
    pub fn new(thought: impl Into<String>, at: DateTime<Utc>) -> Self {
        Self {
            thought: thought.into(),
            at,
        }
    }
}

/// Inner-monologue contract. Not `Send + Sync`-bound here: the reasoning-loop
/// impl is parameterised over a generator that may not be shareable.
pub trait IInnerMonologue {
    /// Produces a reflective thought about the given context JSON.
    fn reflect(&self, context_json: &str) -> SelfReflection;
}

// =====================================================================
// TemplateInnerMonologue — narrative-template reflection over context.
// =====================================================================

const FRAMES: [&str; 3] = [
    "Observation: {summary}. Implication: this likely means {direction}.",
    "Looking at {summary}, the salient pattern is {direction}.",
    "Given {summary}, my next step is to {direction}.",
];

/// A model-free inner monologue: summarises the context, infers a direction
/// keyword-wise, and fills a deterministically-chosen narrative frame.
#[derive(Debug, Default, Clone, Copy)]
pub struct TemplateInnerMonologue;

impl TemplateInnerMonologue {
    /// Returns a new template monologue.
    pub fn new() -> Self {
        Self
    }

    /// Reproduces the C# `Summarise`: strip `{ } [ ] "`, split on spaces, take
    /// the first 12 tokens, join with single spaces.
    fn summarise(json: &str) -> String {
        let cleaned: String = json
            .chars()
            .map(|c| match c {
                '{' | '}' | '[' | ']' | '"' => ' ',
                other => other,
            })
            .collect();
        cleaned
            .split(' ')
            .filter(|s| !s.is_empty())
            .take(12)
            .collect::<Vec<_>>()
            .join(" ")
    }

    /// Reproduces the C# `InferDirection`: first matching keyword wins.
    fn infer_direction(json: &str) -> &'static str {
        let lower = json.to_lowercase();
        if lower.contains("error") {
            "diagnose the failure first"
        } else if lower.contains("goal") {
            "advance toward the stated goal"
        } else if lower.contains("user") {
            "respond to the user"
        } else {
            "gather more context"
        }
    }
}

/// A deterministic, stable-across-runs hash for frame selection.
///
/// The C# reference uses `string.GetHashCode()`, whose value is randomised per
/// process (and differs from .NET's runtime hash), so it can never be
/// byte-reproduced across languages. We substitute a fixed FNV-1a hash so the
/// frame choice is a stable, content-derived function within this port.
fn stable_hash(s: &str) -> u32 {
    let mut hash: u32 = 0x811c_9dc5;
    for b in s.bytes() {
        hash ^= b as u32;
        hash = hash.wrapping_mul(0x0100_0193);
    }
    hash & i32::MAX as u32
}

impl IInnerMonologue for TemplateInnerMonologue {
    fn reflect(&self, context_json: &str) -> SelfReflection {
        let summary = Self::summarise(context_json);
        let direction = Self::infer_direction(context_json);
        let seed = stable_hash(context_json);
        let frame = FRAMES[(seed as usize) % FRAMES.len()];
        let thought = frame
            .replace("{summary}", &summary)
            .replace("{direction}", direction);
        SelfReflection::new(thought, Utc::now())
    }
}

// =====================================================================
// ReasoningLoopInnerMonologue — reasoning-capable LLM inner monologue.
// =====================================================================

const REASONING_SYSTEM_PROMPT: &str = concat!(
    "You are this user's inner monologue. Reason carefully before responding. ",
    "Use <think>...</think> blocks for chain-of-thought. The visible answer ",
    "afterwards should be short and reflective — not a solution, an observation."
);

/// Inner-monologue powered by a reasoning-capable LLM. Drives the generator's
/// fragment stream and prefers the reasoning trace as the "thought", falling
/// back to the visible content, then to `(no inner state)`.
pub struct ReasoningLoopInnerMonologue<G: IChatGenerator> {
    llm: G,
}

impl<G: IChatGenerator> ReasoningLoopInnerMonologue<G> {
    /// Wraps the given chat generator.
    pub fn new(llm: G) -> Self {
        Self { llm }
    }
}

impl<G: IChatGenerator> IInnerMonologue for ReasoningLoopInnerMonologue<G>
where
    G::Error: 'static,
{
    fn reflect(&self, context_json: &str) -> SelfReflection {
        let messages = [
            ChatMessage::system(REASONING_SYSTEM_PROMPT),
            ChatMessage::user(format!(
                "Context (raw JSON):\n{context_json}\n\nReflect on this in 2-3 sentences."
            )),
        ];
        let options = GenerationOptions {
            max_tokens: 256,
            temperature: 0.5,
            include_reasoning: true,
            ..GenerationOptions::default()
        };

        let mut reasoning = String::new();
        let mut content = String::new();

        // Mirror the C# try/catch: any stream failure simply stops iteration;
        // whatever fragments arrived first are kept.
        if let Ok(stream) = self.llm.stream_fragments(&messages, Some(&options)) {
            for frag in stream {
                match frag {
                    Ok(f) => match f.kind {
                        ChatFragmentKind::Reasoning => reasoning.push_str(&f.text),
                        ChatFragmentKind::Content => content.push_str(&f.text),
                    },
                    Err(_) => break,
                }
            }
        }

        // Prefer the reasoning trace; fall back to visible content.
        let mut thought = if !reasoning.is_empty() {
            reasoning.trim().to_string()
        } else {
            content.trim().to_string()
        };
        if thought.is_empty() {
            thought = "(no inner state)".to_string();
        }
        SelfReflection::new(thought, Utc::now())
    }
}
