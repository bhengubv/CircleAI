//! Gaming - what this vertical knows and what it will not do.
//!
//! GENERATED from the shared domain table. The refusal below is this vertical's
//! own, in its own words, and is the reason the table exists rather than a
//! single generic decline.

use std::collections::HashMap;

/// What the games vertical is working with. Held on the device, shown back
/// on request, and cleared when asked.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct GamingDomainContext {
    /// What the person is working on right now, in their words.
    pub focus: String,
    /// Facts this vertical has been given for this conversation. Held HERE and
    /// not in a model prompt, so what was supplied can be shown back and
    /// cleared.
    pub facts: HashMap<String, String>,
    /// The language to answer in. Empty means the device's.
    pub language: String,
}

impl GamingDomainContext {
    /// What this vertical is for.
    pub const PURPOSE: &'static str = 'games';

    /// What it will speak to. A topic list rather than a classifier, because a
    /// list can be read by the person it applies to.
    pub const TOPICS: &'static [&'static str] = &['progress', 'strategies', 'sessions', 'friends'];

    /// The one thing it will NOT do, however it is asked.
    pub const REFUSES: &'static str = 'spend money in a game';

    /// Why - in words for the person asking, not a policy identifier.
    pub const REFUSAL: &'static str = 'I will tell you what it costs; buying is yours';

    pub fn new() -> Self {
        Self::default()
    }

    pub fn with_fact(mut self, key: &str, value: &str) -> Self {
        self.facts.insert(key.to_string(), value.to_string());
        self
    }

    /// Whether a request is in scope. Matched against the topic words, so an
    /// unrelated question is not answered by this vertical with false
    /// confidence.
    pub fn covers(&self, request: &str) -> bool {
        let request = request.to_lowercase();
        Self::TOPICS.iter().any(|t| request.contains(&t.to_lowercase()))
    }

    /// Whether this is the thing it refuses.
    ///
    /// Matched on the ACTION words rather than the whole phrase - somebody does
    /// not ask in the wording of a policy, and a refusal that only triggers on
    /// an exact phrase is a refusal that never triggers.
    pub fn is_refused(&self, request: &str) -> bool {
        let request = request.to_lowercase();
        Self::REFUSES
            .split_whitespace()
            .filter(|w| w.len() > 3)
            .all(|w| request.contains(&w.to_lowercase()))
    }

    /// Everything it has been told, for showing back.
    pub fn describe(&self) -> String {
        if self.facts.is_empty() {
            return format!("{} - nothing supplied yet", Self::PURPOSE);
        }
        let mut keys: Vec<&String> = self.facts.keys().collect();
        keys.sort();
        format!(
            "{} - {}",
            Self::PURPOSE,
            keys.iter()
                .map(|k| format!("{k}: {}", self.facts[*k]))
                .collect::<Vec<_>>()
                .join(", ")
        )
    }

    /// Forgets everything supplied. What a "clear" control calls.
    pub fn clear(&mut self) {
        self.facts.clear();
        self.focus.clear();
    }
}

/// The games companion. Answers within its topics and refuses the one thing
/// it must, before anything else runs.
pub struct GamingCompanionAdapter {
    context: GamingDomainContext,
    answer: Option<Box<dyn Fn(&str, &GamingDomainContext) -> String + Send + Sync>>,
}

impl GamingCompanionAdapter {
    pub fn new(
        context: GamingDomainContext,
        answer: Option<Box<dyn Fn(&str, &GamingDomainContext) -> String + Send + Sync>>,
    ) -> Self {
        Self { context, answer }
    }

    pub fn context(&self) -> &GamingDomainContext {
        &self.context
    }

    pub fn context_mut(&mut self) -> &mut GamingDomainContext {
        &mut self.context
    }

    pub fn is_available(&self) -> bool {
        self.answer.is_some()
    }

    /// The refusal is checked BEFORE the model sees the request.
    ///
    /// Checking afterwards means the model has already produced the thing that
    /// should not have been produced, and the only remaining option is to hide
    /// it - which is not the same as not doing it.
    pub fn handle(&self, request: &str) -> String {
        if self.context.is_refused(request) {
            return format!(
                "I will not {} - {}.",
                GamingDomainContext::REFUSES,
                GamingDomainContext::REFUSAL
            );
        }
        match &self.answer {
            Some(answer) => answer(request, &self.context),
            None => format!(
                "{} is not set up on this device yet.",
                GamingDomainContext::PURPOSE
            ),
        }
    }
}

impl std::fmt::Debug for GamingCompanionAdapter {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("GamingCompanionAdapter")
            .field("purpose", &GamingDomainContext::PURPOSE)
            .field("available", &self.is_available())
            .finish()
    }
}
