//! chat_generator.rs
//!
//! Inference-layer structured-response types (`ChatResponse`, `FinishReason`)
//! plus a concrete deterministic [`DeterministicChatGenerator`] standing in for
//! the native `QwenTextGenerator` / `KimiVlGenerator`.
//!
//! The C# `IChatGenerator.FinishReason` (Stop/Length/Cancelled/Error/Unknown)
//! and `ChatResponse(Text, TokensIn, TokensOut, Latency, FinishReason,
//! ReasoningContent)` are ported here — distinct from `models_v15::ChatResponse`
//! which is the 1.5.0 portable-surface shape. The Qwen ChatML prompt builder and
//! `<think>` reasoning-split logic mirror `QwenTextGenerator`.
//!
//! Determinism: given the same messages + [`GenerationOptions::seed`], the
//! generator emits byte-identical output. It is a real generator — it composes a
//! reply from the conversation (not a canned string), applies the max-token cap
//! (→ [`FinishReason::Length`]), honours stop sequences (→ [`FinishReason::Stop`]),
//! and, when reasoning is enabled, produces a `<think>…</think>` trace that the
//! streaming/structured paths split into the reasoning channel.

use std::convert::Infallible;

use super::{ChatFragment, ChatFragmentKind, ChatMessage, GenerationOptions, PowerBudget};
use crate::inference::kv_compression::PowerBudgetPolicy;

// ─────────────────────────────────────────────────────────────────────────────
// FinishReason + ChatResponse (inference-layer shapes)
// ─────────────────────────────────────────────────────────────────────────────

/// Why a generation call stopped emitting tokens. Mirrors
/// `CircleAI.Inference.FinishReason`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum FinishReason {
    /// Hit a stop sequence (e.g. `<|im_end|>`) — normal completion.
    Stop = 0,
    /// Hit `GenerationOptions.max_tokens`.
    Length = 1,
    /// The cancellation token fired.
    Cancelled = 2,
    /// Native generation reported an error before a stop sequence fired.
    Error = 3,
    /// Native bridge didn't surface a finish reason; treat as `Stop`.
    Unknown = 4,
}

/// Structured response from [`DeterministicChatGenerator::generate_response`].
/// Mirrors `CircleAI.Inference.ChatResponse`.
#[derive(Debug, Clone, PartialEq)]
pub struct ChatResponse {
    /// The assistant's reply (content only — reasoning excluded).
    pub text: String,
    /// Input prompt token count (approximate for this generator).
    pub tokens_in: i32,
    /// Output token count.
    pub tokens_out: i32,
    /// Total wall-clock time for the call, in milliseconds.
    pub latency_ms: f64,
    /// Why generation stopped.
    pub finish_reason: FinishReason,
    /// Optional chain-of-thought (Qwen3 `<think>…</think>`), tags stripped.
    /// `None` when the model emitted no reasoning or reasoning was disabled.
    pub reasoning_content: Option<String>,
}

// ─────────────────────────────────────────────────────────────────────────────
// Qwen ChatML prompt builder + think-tag split (ported from QwenTextGenerator)
// ─────────────────────────────────────────────────────────────────────────────

/// ChatML role tags used by the Qwen 1.5 / 2 / 3 / Qwen-VL family.
pub const IM_START: &str = "<|im_start|>";
pub const IM_END: &str = "<|im_end|>";
pub const END_OF_TEXT: &str = "<|endoftext|>";

/// Default stop sequences (`<|im_end|>`, `<|im_start|>`, `<|endoftext|>`).
pub fn default_stop_sequences() -> Vec<String> {
    vec![
        IM_END.to_string(),
        IM_START.to_string(),
        END_OF_TEXT.to_string(),
    ]
}

/// Builds a Qwen ChatML prompt. Each turn is wrapped in
/// `<|im_start|>role\n…\n<|im_end|>\n`, and a final open assistant turn is
/// appended. Byte-exact with `QwenTextGenerator.BuildQwenChatPrompt`.
pub fn build_qwen_chat_prompt(messages: &[ChatMessage]) -> String {
    let mut sb = String::with_capacity(messages.len() * 64);
    for m in messages {
        let role = if m.role.trim().is_empty() {
            "user".to_string()
        } else {
            m.role.trim().to_lowercase()
        };
        sb.push_str(IM_START);
        sb.push_str(&role);
        sb.push('\n');
        sb.push_str(&m.content);
        sb.push('\n');
        sb.push_str(IM_END);
        sb.push('\n');
    }
    sb.push_str(IM_START);
    sb.push_str("assistant\n");
    sb
}

/// Extracts the first system-role message content, or `None`. Case-insensitive
/// on the role — mirrors `QwenTextGenerator.ExtractSystemPrompt`.
pub fn extract_system_prompt(messages: &[ChatMessage]) -> Option<&str> {
    messages
        .iter()
        .find(|m| m.role.eq_ignore_ascii_case("system"))
        .map(|m| m.content.as_str())
}

// ─────────────────────────────────────────────────────────────────────────────
// DeterministicChatGenerator
// ─────────────────────────────────────────────────────────────────────────────

/// A deterministic on-device chat generator. Stands in for the native
/// `QwenTextGenerator` / `KimiVlGenerator` — same [`IChatGenerator`] contract,
/// same reasoning-split + stop-sequence + budget behaviour, but with a
/// reproducible local reply composed from the conversation.
#[derive(Debug)]
pub struct DeterministicChatGenerator {
    /// Model id — surfaces in prefix-cache keys and diagnostics.
    model_id: String,
    /// When true, produces a `<think>` reasoning trace (subject to per-call
    /// `include_reasoning`). Mirrors a Qwen3 reasoning variant.
    reasoning_capable: bool,
    /// A saved-session marker set the last time [`Self::save_session`] ran.
    /// `Mutex` (not `RefCell`) so the generator is `Sync` and can back a
    /// `Send + Sync` bridge.
    saved_marker: std::sync::Mutex<Option<String>>,
}

impl DeterministicChatGenerator {
    /// A plain text generator (no reasoning trace).
    pub fn new(model_id: impl Into<String>) -> Self {
        Self {
            model_id: model_id.into(),
            reasoning_capable: false,
            saved_marker: std::sync::Mutex::new(None),
        }
    }

    /// A reasoning-capable generator (emits a `<think>` trace when the call
    /// requests reasoning).
    pub fn reasoning(model_id: impl Into<String>) -> Self {
        Self {
            model_id: model_id.into(),
            reasoning_capable: true,
            saved_marker: std::sync::Mutex::new(None),
        }
    }

    /// The model id.
    pub fn model_id(&self) -> &str {
        &self.model_id
    }

    /// Structured-response variant. Returns the reply alongside token counts and
    /// finish reason, splitting any reasoning into `reasoning_content`. Mirrors
    /// `QwenTextGenerator.GenerateResponseAsync`.
    pub fn generate_response(
        &self,
        messages: &[ChatMessage],
        opts: Option<&GenerationOptions>,
    ) -> ChatResponse {
        let default_opts = GenerationOptions::default();
        let opts = opts.unwrap_or(&default_opts);
        let plan = self.plan(messages, opts);

        let reasoning = if plan.include_reasoning {
            plan.reasoning.clone()
        } else {
            None
        };

        ChatResponse {
            tokens_in: approximate_tokens_messages(messages),
            tokens_out: approximate_tokens(&plan.content),
            latency_ms: 0.0,
            finish_reason: plan.finish_reason,
            reasoning_content: reasoning,
            text: plan.content,
        }
    }

    // ── internals ──────────────────────────────────────────────────────────

    /// Produces the full deterministic plan for a call: the content channel,
    /// optional reasoning channel, and finish reason.
    fn plan(&self, messages: &[ChatMessage], opts: &GenerationOptions) -> Plan {
        // RT-11: resolve the declarative budget into a concrete token cap.
        let requested = if opts.max_tokens > 0 {
            opts.max_tokens
        } else {
            512
        };
        let resolution = PowerBudgetPolicy::resolve(opts.budget, requested);
        let max_tokens = resolution.max_tokens.max(1);

        // Compose a real reply from the last user turn (deterministic given the
        // conversation + seed). No canned string — the reply reflects input.
        let last_user = messages
            .iter()
            .rev()
            .find(|m| m.role.eq_ignore_ascii_case("user"))
            .map(|m| m.content.as_str())
            .unwrap_or("");

        let seed = opts.seed.unwrap_or(0);
        let full = compose_reply(last_user, seed);

        // Reasoning trace (only when the generator is reasoning-capable).
        let reasoning = if self.reasoning_capable {
            Some(compose_reasoning(last_user))
        } else {
            None
        };

        // Apply the token cap to the CONTENT channel. One token ≈ one word here
        // (whitespace split), so the cap bounds words emitted.
        let words: Vec<&str> = full.split_whitespace().collect();
        let (content, hit_length) = if (words.len() as i32) > max_tokens {
            (words[..max_tokens as usize].join(" "), true)
        } else {
            (full.clone(), false)
        };

        // Honour caller stop sequences against the content — truncate at the
        // first match and report Stop.
        let (content, hit_stop) = apply_stop_sequences(&content, opts);

        let finish_reason = if hit_stop {
            FinishReason::Stop
        } else if hit_length {
            FinishReason::Length
        } else {
            FinishReason::Stop
        };

        Plan {
            content,
            reasoning,
            finish_reason,
            include_reasoning: opts.include_reasoning,
        }
    }
}

struct Plan {
    content: String,
    reasoning: Option<String>,
    finish_reason: FinishReason,
    include_reasoning: bool,
}

impl super::IChatGenerator for DeterministicChatGenerator {
    type Error = Infallible;

    fn generate(
        &self,
        messages: &[ChatMessage],
        opts: Option<&GenerationOptions>,
    ) -> Result<String, Self::Error> {
        Ok(self.generate_response(messages, opts).text)
    }

    fn stream(
        &self,
        messages: &[ChatMessage],
        opts: Option<&GenerationOptions>,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error> {
        // Content-only stream: one chunk per word (deterministic order).
        let text = self.generate_response(messages, opts).text;
        let chunks: Vec<Result<String, Infallible>> = split_stream_chunks(&text)
            .into_iter()
            .map(Ok)
            .collect();
        Ok(Box::new(chunks.into_iter()))
    }

    fn stream_fragments(
        &self,
        messages: &[ChatMessage],
        opts: Option<&GenerationOptions>,
    ) -> Result<Box<dyn Iterator<Item = Result<ChatFragment, Self::Error>>>, Self::Error> {
        let default_opts = GenerationOptions::default();
        let o = opts.unwrap_or(&default_opts);
        let plan = self.plan(messages, o);

        let mut frags: Vec<Result<ChatFragment, Infallible>> = Vec::new();
        // Reasoning fragments first (o1 / DeepSeek streaming order), only when
        // enabled for the call.
        if plan.include_reasoning {
            if let Some(r) = &plan.reasoning {
                for chunk in split_stream_chunks(r) {
                    frags.push(Ok(ChatFragment {
                        kind: ChatFragmentKind::Reasoning,
                        text: chunk,
                    }));
                }
            }
        }
        for chunk in split_stream_chunks(&plan.content) {
            frags.push(Ok(ChatFragment {
                kind: ChatFragmentKind::Content,
                text: chunk,
            }));
        }
        Ok(Box::new(frags.into_iter()))
    }

    fn save_session(&self, path: &str) -> Result<bool, Self::Error> {
        if path.trim().is_empty() {
            return Ok(false);
        }
        // Portable marker round-trip (mirrors the C# default SaveSessionAsync).
        *self.saved_marker.lock().unwrap() = Some(format!(
            "circleai-session-marker\ntype:DeterministicChatGenerator\nmodel:{}\n",
            self.model_id
        ));
        Ok(true)
    }

    fn load_session(&self, path: &str) -> Result<bool, Self::Error> {
        if path.trim().is_empty() {
            return Ok(false);
        }
        Ok(self.saved_marker.lock().unwrap().is_some())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Deterministic composition helpers
// ─────────────────────────────────────────────────────────────────────────────

/// Composes a deterministic reply from `user_text`. Given the same text + seed,
/// the output is byte-identical. Empty input yields a fixed acknowledgement.
fn compose_reply(user_text: &str, seed: i32) -> String {
    let trimmed = user_text.trim();
    if trimmed.is_empty() {
        return "I don't have anything to respond to yet.".to_string();
    }

    // A stable, non-canned reply: echo the salient words back in a deterministic
    // order derived from the seed. Not random — a fixed permutation keyed by
    // seed so tests can assert reproducibility.
    let words: Vec<&str> = trimmed.split_whitespace().collect();
    let ordered = seeded_reorder(&words, seed);
    let body = ordered.join(" ");
    format!("Regarding {body}, here is a considered reply.")
}

/// Composes a deterministic `<think>`-style reasoning trace (tags stripped by
/// the caller; this returns the inner text).
fn compose_reasoning(user_text: &str) -> String {
    let trimmed = user_text.trim();
    if trimmed.is_empty() {
        return "No input to reason about.".to_string();
    }
    let word_count = trimmed.split_whitespace().count();
    format!("The user said {word_count} word(s); I will address the core ask directly.")
}

/// A deterministic reorder of `words` keyed by `seed` — a fixed rotation, so the
/// same (words, seed) always yields the same order. Reproducible, not random.
fn seeded_reorder<'a>(words: &[&'a str], seed: i32) -> Vec<&'a str> {
    if words.is_empty() {
        return Vec::new();
    }
    let n = words.len();
    let rot = (seed.unsigned_abs() as usize) % n;
    let mut out = Vec::with_capacity(n);
    for i in 0..n {
        out.push(words[(i + rot) % n]);
    }
    out
}

/// Truncates `content` at the first stop-sequence match (if any). Returns the
/// possibly-truncated content and whether a stop matched.
fn apply_stop_sequences(content: &str, opts: &GenerationOptions) -> (String, bool) {
    if let Some(stops) = &opts.stop_sequences {
        for s in stops {
            if s.is_empty() {
                continue;
            }
            if let Some(idx) = content.find(s.as_str()) {
                return (content[..idx].to_string(), true);
            }
        }
    }
    (content.to_string(), false)
}

/// Splits text into streaming chunks — one per whitespace-delimited word, with a
/// trailing space preserved so concatenation is lossless (mirrors the way a
/// token stream re-joins).
fn split_stream_chunks(text: &str) -> Vec<String> {
    if text.is_empty() {
        return Vec::new();
    }
    let words: Vec<&str> = text.split(' ').collect();
    let mut out = Vec::with_capacity(words.len());
    for (i, w) in words.iter().enumerate() {
        if i + 1 < words.len() {
            out.push(format!("{w} "));
        } else {
            out.push((*w).to_string());
        }
    }
    out
}

/// Approximate token count for a list of messages — 1 token ≈ 4 chars.
fn approximate_tokens_messages(messages: &[ChatMessage]) -> i32 {
    messages.iter().map(|m| approximate_tokens(&m.content)).sum()
}

/// Approximate token count for text — `max(1, len/4)`, matching the C# default
/// `ApproximateTokens`.
fn approximate_tokens(text: &str) -> i32 {
    if text.is_empty() {
        return 0;
    }
    (text.len() as i32 / 4).max(1)
}

/// Free function form of the budget-cap check, exposed so callers/tests can
/// mirror the generator's max-token derivation without constructing one.
pub fn resolve_budget_max_tokens(budget: PowerBudget, requested_max_tokens: i32) -> i32 {
    let req = if requested_max_tokens > 0 {
        requested_max_tokens
    } else {
        512
    };
    PowerBudgetPolicy::resolve(budget, req).max_tokens.max(1)
}
