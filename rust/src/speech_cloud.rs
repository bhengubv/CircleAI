//! speech_cloud — CircleAI.Speech.Cloud voice intent router (Rust port).
//!
//! Generic regex-based voice intent router lifted from CircleUp's
//! `KeywordVoiceCommandRouter`. The router matches an ordered list of intents
//! against the trimmed transcript; the first hit wins; on no match it falls
//! through to a caller-defined fallback intent (typically `"ask-ai"`).
//!
//! Ports:
//!   - `VoiceIntent`             → [`VoiceIntent`]
//!   - `VoiceIntentMatch`        → [`VoiceIntentMatch`]
//!   - `IVoiceIntentRouter`      → [`IVoiceIntentRouter`]
//!   - `KeywordVoiceIntentRouter`→ [`KeywordVoiceIntentRouter`]
//!   - `NullVoiceIntentRouter`   → [`NullVoiceIntentRouter`]
//!
//! `async`/`ValueTask` collapses to a synchronous call (matching sub-millisecond,
//! hermetic execution). Named regex captures are surfaced exactly like the C#
//! reference: every named group that matched non-empty is trimmed and exposed.

use std::collections::BTreeMap;

use regex::Regex;

/// One named intent the router recognises. `pattern` is matched against the
/// trimmed transcript; on a hit, every named group is exposed in
/// [`VoiceIntentMatch::captures`]. Mirrors `VoiceIntent`.
#[derive(Debug, Clone)]
pub struct VoiceIntent {
    pub name: String,
    pub pattern: Regex,
}

impl VoiceIntent {
    /// Creates an intent from a name and a compiled regex.
    pub fn new(name: impl Into<String>, pattern: Regex) -> Self {
        Self {
            name: name.into(),
            pattern,
        }
    }

    /// Convenience: compile `pattern_str` into a regex. Returns the regex crate
    /// error on an invalid pattern.
    pub fn compile(
        name: impl Into<String>,
        pattern_str: &str,
    ) -> Result<Self, regex::Error> {
        Ok(Self {
            name: name.into(),
            pattern: Regex::new(pattern_str)?,
        })
    }
}

/// One match outcome. `captures` holds the trimmed non-empty named groups
/// (ordinal-keyed, deterministic order). Mirrors `VoiceIntentMatch`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VoiceIntentMatch {
    pub intent_name: String,
    pub transcript: String,
    pub captures: BTreeMap<String, String>,
}

/// Maps a transcript to one of a host-supplied set of intents. Rule-based,
/// sub-millisecond per attempt, hermetic. Mirrors `IVoiceIntentRouter`
/// (synchronous — `ValueTask` collapses to a plain return).
pub trait IVoiceIntentRouter {
    /// Backend self-identification — `"keyword"`, `"null"`.
    fn backend_id(&self) -> &str;

    /// Matches the transcript against the configured intents. Returns a match
    /// for the first hitting intent, or for the fallback intent when nothing
    /// matches (whose `captures` is empty).
    fn route(&self, transcript: &str) -> VoiceIntentMatch;
}

/// Default [`IVoiceIntentRouter`]. Takes an ordered list of intents plus a
/// fallback name (typically `"ask-ai"`) and tries each pattern in order.
/// Mirrors `KeywordVoiceIntentRouter`.
pub struct KeywordVoiceIntentRouter {
    intents: Vec<VoiceIntent>,
    fallback_intent_name: String,
}

impl KeywordVoiceIntentRouter {
    /// Creates a router over `intents` with the given `fallback_intent_name`.
    pub fn new(
        intents: impl IntoIterator<Item = VoiceIntent>,
        fallback_intent_name: impl Into<String>,
    ) -> Self {
        Self {
            intents: intents.into_iter().collect(),
            fallback_intent_name: fallback_intent_name.into(),
        }
    }

    /// Creates a router with the canonical `"ask-ai"` fallback.
    pub fn with_default_fallback(intents: impl IntoIterator<Item = VoiceIntent>) -> Self {
        Self::new(intents, "ask-ai")
    }
}

impl IVoiceIntentRouter for KeywordVoiceIntentRouter {
    fn backend_id(&self) -> &str {
        "keyword"
    }

    fn route(&self, transcript: &str) -> VoiceIntentMatch {
        let text = transcript.trim();
        if text.is_empty() {
            return VoiceIntentMatch {
                intent_name: self.fallback_intent_name.clone(),
                transcript: String::new(),
                captures: BTreeMap::new(),
            };
        }

        for intent in &self.intents {
            let caps = match intent.pattern.captures(text) {
                Some(c) => c,
                None => continue,
            };

            let mut captures: BTreeMap<String, String> = BTreeMap::new();
            for name in intent.pattern.capture_names().flatten() {
                // Only surface *named* groups (the numeric/full-match groups are
                // skipped — `capture_names` yields `None` for those, already
                // filtered by `flatten`).
                if let Some(m) = caps.name(name) {
                    let v = m.as_str().trim();
                    if !v.is_empty() {
                        captures.insert(name.to_string(), v.to_string());
                    }
                }
            }

            return VoiceIntentMatch {
                intent_name: intent.name.clone(),
                transcript: text.to_string(),
                captures,
            };
        }

        VoiceIntentMatch {
            intent_name: self.fallback_intent_name.clone(),
            transcript: text.to_string(),
            captures: BTreeMap::new(),
        }
    }
}

/// Empty router — always returns the fallback intent (`"ask-ai"`). Mirrors
/// `NullVoiceIntentRouter`.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullVoiceIntentRouter;

impl NullVoiceIntentRouter {
    /// Creates the null router.
    pub fn new() -> Self {
        Self
    }
}

impl IVoiceIntentRouter for NullVoiceIntentRouter {
    fn backend_id(&self) -> &str {
        "null"
    }

    fn route(&self, transcript: &str) -> VoiceIntentMatch {
        VoiceIntentMatch {
            intent_name: "ask-ai".to_string(),
            transcript: transcript.to_string(),
            captures: BTreeMap::new(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn first_hit_wins_with_named_captures() {
        let intents = vec![
            VoiceIntent::compile("open-app", r"^open (?P<app>\w+)$").unwrap(),
            VoiceIntent::compile("greet", r"^hello$").unwrap(),
        ];
        let router = KeywordVoiceIntentRouter::with_default_fallback(intents);

        let m = router.route("  open bidbaas  ");
        assert_eq!(m.intent_name, "open-app");
        assert_eq!(m.transcript, "open bidbaas");
        assert_eq!(m.captures.get("app").unwrap(), "bidbaas");
    }

    #[test]
    fn falls_back_when_no_match() {
        let router = KeywordVoiceIntentRouter::with_default_fallback(vec![
            VoiceIntent::compile("greet", r"^hello$").unwrap(),
        ]);
        let m = router.route("something else");
        assert_eq!(m.intent_name, "ask-ai");
        assert!(m.captures.is_empty());
    }

    #[test]
    fn empty_transcript_falls_back_empty() {
        let router = KeywordVoiceIntentRouter::new(Vec::new(), "custom-fallback");
        let m = router.route("   ");
        assert_eq!(m.intent_name, "custom-fallback");
        assert_eq!(m.transcript, "");
    }

    #[test]
    fn null_router_always_ask_ai() {
        let router = NullVoiceIntentRouter::new();
        assert_eq!(router.backend_id(), "null");
        let m = router.route("open bidbaas");
        assert_eq!(m.intent_name, "ask-ai");
        assert_eq!(m.transcript, "open bidbaas");
    }
}
