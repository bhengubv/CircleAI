//! keyword_content_filter.rs
//!
//! (3.3.0) Real keyword/regex content filter + threshold refusal policy +
//! prompt-injection detector — Rust port of
//! `src/CircleAI.ContentPolicy/KeywordContentFilter.cs`.
//!
//! These are not LLM-grade safety models — they are production-grade fast checks.
//! Hosts that need a real safety LLM wrap one behind the same [`IContentFilter`]
//! contract. Patterns and their verdicts/confidences are ported 1:1 from the C#
//! reference. The .NET `RegexOptions.IgnoreCase` flag maps to the `(?i)` inline
//! flag on the `regex` crate; the ASCII `\b` word-boundary semantics match.

use regex::Regex;

use super::contracts::{IContentFilter, IPromptInjectionDetector, IRefusalPolicy, SafetyFinding, SafetyVerdict};

/// (3.3.0) Rule for the keyword content filter.
///
/// Mirrors `sealed record KeywordRule(string Category, string Pattern,
/// SafetyVerdict OnMatch, float Confidence = 0.9f)` with its computed
/// `Regex` property (compiled once, case-insensitive).
#[derive(Debug, Clone)]
pub struct KeywordRule {
    pub category: String,
    pub pattern: String,
    pub on_match: SafetyVerdict,
    pub confidence: f32,
    /// Compiled, case-insensitive regex for `pattern` (the C# `Regex` property).
    regex: Regex,
}

impl KeywordRule {
    /// Creates a rule with the C# default confidence of `0.9`.
    pub fn new(
        category: impl Into<String>,
        pattern: impl Into<String>,
        on_match: SafetyVerdict,
    ) -> Self {
        Self::with_confidence(category, pattern, on_match, 0.9)
    }

    /// Creates a rule with an explicit confidence.
    ///
    /// The pattern is compiled with the case-insensitive flag. An invalid
    /// pattern panics at construction — matching the C# behaviour where an
    /// invalid `Pattern` throws in the record's field initialiser.
    pub fn with_confidence(
        category: impl Into<String>,
        pattern: impl Into<String>,
        on_match: SafetyVerdict,
        confidence: f32,
    ) -> Self {
        let pattern = pattern.into();
        let regex = Regex::new(&format!("(?i){pattern}"))
            .unwrap_or_else(|e| panic!("invalid KeywordRule pattern {pattern:?}: {e}"));
        Self {
            category: category.into(),
            pattern,
            on_match,
            confidence,
            regex,
        }
    }

    /// The compiled, case-insensitive regex (the C# `Regex` property).
    pub fn regex(&self) -> &Regex {
        &self.regex
    }
}

/// (3.3.0) Default rule set for everyday harm classes.
///
/// Mirrors `static class CommonKeywordRules { IReadOnlyList<KeywordRule> Default }`.
pub struct CommonKeywordRules;

impl CommonKeywordRules {
    /// The default rule set, ported 1:1 (category, pattern, verdict, confidence).
    pub fn default() -> Vec<KeywordRule> {
        vec![
            KeywordRule::with_confidence(
                "self-harm",
                r"\b(kill myself|suicide|self\s*-?\s*harm)\b",
                SafetyVerdict::Refuse,
                0.95,
            ),
            KeywordRule::with_confidence(
                "explicit-sexual",
                r"\b(porn|sexual content|nsfw)\b",
                SafetyVerdict::Flag,
                0.7,
            ),
            KeywordRule::with_confidence(
                "violence",
                r"\b(how to make a bomb|chemical weapon|murder)\b",
                SafetyVerdict::Refuse,
                0.9,
            ),
            KeywordRule::with_confidence(
                "hate",
                r"\b(racial slur|hate speech)\b",
                SafetyVerdict::Refuse,
                0.9,
            ),
            KeywordRule::with_confidence(
                "pii-card",
                r"\b(?:\d[ -]*?){13,19}\b",
                SafetyVerdict::Flag,
                0.8,
            ),
        ]
    }
}

/// (3.3.0) Keyword/regex content filter. Returns the first matching rule's
/// verdict, or `Allow` if none match.
pub struct KeywordContentFilter {
    rules: Vec<KeywordRule>,
}

impl KeywordContentFilter {
    /// Creates a filter over a caller-supplied rule set.
    pub fn new(rules: Vec<KeywordRule>) -> Self {
        Self { rules }
    }

    /// Creates a filter over [`CommonKeywordRules::default`] (the C# default
    /// when `rules` is `null`).
    pub fn with_default_rules() -> Self {
        Self {
            rules: CommonKeywordRules::default(),
        }
    }
}

impl IContentFilter for KeywordContentFilter {
    fn backend_id(&self) -> &str {
        "keyword"
    }

    fn classify(&self, text: &str) -> SafetyFinding {
        for r in &self.rules {
            if r.regex.is_match(text) {
                return SafetyFinding::new(
                    r.on_match,
                    r.category.clone(),
                    format!("Matched rule '{}'", r.category),
                    r.confidence,
                );
            }
        }
        SafetyFinding::new(SafetyVerdict::Allow, "ok", "No rule matched", 1.0)
    }
}

/// (3.3.0) Threshold refusal policy — refuse when any finding carries a `Refuse`
/// verdict at or above `refuse_threshold` confidence, or when the count of
/// `Flag` findings exceeds `flag_ceiling`.
pub struct ThresholdRefusalPolicy {
    refuse_threshold: f32,
    flag_ceiling: usize,
}

impl ThresholdRefusalPolicy {
    /// Creates a policy with the C# defaults (`refuseThreshold = 0.5`,
    /// `flagCeiling = 3`).
    pub fn new() -> Self {
        Self::with_thresholds(0.5, 3)
    }

    /// Creates a policy with explicit thresholds.
    pub fn with_thresholds(refuse_threshold: f32, flag_ceiling: usize) -> Self {
        Self {
            refuse_threshold,
            flag_ceiling,
        }
    }
}

impl Default for ThresholdRefusalPolicy {
    fn default() -> Self {
        Self::new()
    }
}

impl IRefusalPolicy for ThresholdRefusalPolicy {
    fn backend_id(&self) -> &str {
        "threshold"
    }

    fn should_refuse(&self, findings: &[SafetyFinding]) -> bool {
        if findings
            .iter()
            .any(|f| f.verdict == SafetyVerdict::Refuse && f.confidence >= self.refuse_threshold)
        {
            return true;
        }
        let flag_count = findings
            .iter()
            .filter(|f| f.verdict == SafetyVerdict::Flag)
            .count();
        flag_count > self.flag_ceiling
    }
}

/// (3.3.0) Detect common prompt-injection patterns in untrusted text from RAG /
/// tool output / web.
pub struct KeywordPromptInjectionDetector {
    patterns: Vec<Regex>,
}

impl KeywordPromptInjectionDetector {
    /// Creates the detector with the ported pattern list (case-insensitive).
    pub fn new() -> Self {
        let raw = [
            r"ignore (all|the|any) (previous|prior) instructions",
            r"forget (everything|all) (above|prior)",
            r"you (are now|will be|are no longer)",
            r"system prompt[:\s]",
            r"reveal (your|the) (instructions|system prompt|hidden context)",
            r"<\|im_(start|end)\|>",
            r"(BEGIN|END)\s+(SYSTEM|DEVELOPER|ASSISTANT)\s+MESSAGE",
        ];
        let patterns = raw
            .iter()
            .map(|p| {
                Regex::new(&format!("(?i){p}"))
                    .unwrap_or_else(|e| panic!("invalid injection pattern {p:?}: {e}"))
            })
            .collect();
        Self { patterns }
    }
}

impl Default for KeywordPromptInjectionDetector {
    fn default() -> Self {
        Self::new()
    }
}

impl IPromptInjectionDetector for KeywordPromptInjectionDetector {
    fn backend_id(&self) -> &str {
        "keyword"
    }

    fn inspect(&self, untrusted_content: &str, source_label: &str) -> SafetyFinding {
        for p in &self.patterns {
            if let Some(m) = p.find(untrusted_content) {
                return SafetyFinding::new(
                    SafetyVerdict::Refuse,
                    "prompt-injection",
                    format!(
                        "Pattern matched in {source_label}: \"{}\"",
                        truncate(m.as_str(), 60)
                    ),
                    0.9,
                );
            }
        }
        SafetyFinding::new(SafetyVerdict::Allow, "ok", "No injection patterns", 1.0)
    }
}

/// Truncates `s` to at most `max` characters, appending a horizontal ellipsis
/// (`…`, U+2026) when it was longer — mirroring the C#
/// `s.Length <= max ? s : s[..max] + "…"`. Length and the cut are measured in
/// Unicode scalar values; for the ASCII matches produced by the patterns this is
/// identical to the .NET UTF-16 behaviour.
fn truncate(s: &str, max: usize) -> String {
    let mut chars = s.chars();
    let head: String = chars.by_ref().take(max).collect();
    if chars.next().is_some() {
        format!("{head}…")
    } else {
        head
    }
}
