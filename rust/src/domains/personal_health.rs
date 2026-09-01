//! Personal.Health - what this vertical knows and what it will not do.
//!
//! GENERATED from the shared domain table. The refusal below is this vertical's
//! own, in its own words, and is the reason the table exists rather than a
//! single generic decline.

use std::collections::HashMap;

/// What the your own health record vertical is working with. Held on the
/// device, shown back on request, and cleared when asked.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct PersonalHealthDomainContext {
    /// What the person is working on right now, in their words.
    pub focus: String,
    /// Facts this vertical has been given for this conversation. Held HERE and
    /// not in a model prompt, so what was supplied can be shown back and
    /// cleared.
    pub facts: HashMap<String, String>,
    /// The language to answer in. Empty means the device's.
    pub language: String,
}

impl PersonalHealthDomainContext {
    /// What this vertical is for.
    pub const PURPOSE: &'static str = "your own health record";

    /// What it will speak to. A topic list rather than a classifier, because a
    /// list can be read by the person it applies to.
    pub const TOPICS: &'static [&'static str] = &["measurements", "appointments", "medicines", "history"];

    /// The one thing it will NOT do, however it is asked.
    pub const REFUSES: &'static str = "interpret a test result";

    /// Why - in words for the person asking, not a policy identifier.
    pub const REFUSAL: &'static str = "the number I can store; what it means is for the person who ordered it";

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

/// The your own health record companion. Answers within its topics and
/// refuses the one thing it must, before anything else runs.
pub struct PersonalHealthCompanionAdapter {
    context: PersonalHealthDomainContext,
    answer: Option<Box<dyn Fn(&str, &PersonalHealthDomainContext) -> String + Send + Sync>>,
}

impl PersonalHealthCompanionAdapter {
    pub fn new(
        context: PersonalHealthDomainContext,
        answer: Option<Box<dyn Fn(&str, &PersonalHealthDomainContext) -> String + Send + Sync>>,
    ) -> Self {
        Self { context, answer }
    }

    pub fn context(&self) -> &PersonalHealthDomainContext {
        &self.context
    }

    pub fn context_mut(&mut self) -> &mut PersonalHealthDomainContext {
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
                PersonalHealthDomainContext::REFUSES,
                PersonalHealthDomainContext::REFUSAL
            );
        }
        match &self.answer {
            Some(answer) => answer(request, &self.context),
            None => format!(
                "{} is not set up on this device yet.",
                PersonalHealthDomainContext::PURPOSE
            ),
        }
    }
}

impl std::fmt::Debug for PersonalHealthCompanionAdapter {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PersonalHealthCompanionAdapter")
            .field("purpose", &PersonalHealthDomainContext::PURPOSE)
            .field("available", &self.is_available())
            .finish()
    }
}
