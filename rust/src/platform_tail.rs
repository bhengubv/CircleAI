//! The remainder: realtime and workflow events, testing helpers, the dependency
//! bot, the model operator, plugin hosting, tools, media, decks, the web board,
//! native runtimes, image generation, and the long tail of single types.
//!
//! THE EVENT TYPES ARE SEPARATE STRUCTS, not variants of one enum with an
//! optional payload. A `TranscriptDelta` and a `SessionError` have nothing in
//! common but a timestamp, and folding them into one shape produces a type where
//! most fields are empty most of the time - which is how a consumer ends up
//! reading a field that was never set for the event it actually received.
//!
//! `TranscriptFinalEvent_v2` KEEPS ITS SUFFIX. Renaming it would hide the fact
//! that two shapes exist on the wire, and a consumer written against the wrong
//! one fails on a field that is simply absent.

use std::collections::{HashMap, HashSet};

// ─────────────────────────────────────────────────────────────────────────────
// Realtime and lifecycle events

// `event!` was written once as a macro over the table below and expanded
// here, so each event appears under its own name.

#[doc = "Somebody started talking. The signal barge-in is built on: everything the \
     assistant is saying stops here."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct SpeechStartedEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub is_caller: bool,
}

impl SpeechStartedEvent {
    pub const NAME: &'static str = "SpeechStartedEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "They stopped making noise. NOT the same as having finished a sentence - \
     treating it as end of turn cuts people off mid-thought."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct SpeechEndedEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub is_caller: bool,
    pub silence_ms: u64,
}

impl SpeechEndedEvent {
    pub const NAME: &'static str = "SpeechEndedEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "A revision of the current utterance. Deltas REPLACE each other; a consumer \
     that appends renders the sentence growing by duplication."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct TranscriptDeltaEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub text: String,
    pub replaces_previous: bool,
}

impl TranscriptDeltaEvent {
    pub const NAME: &'static str = "TranscriptDeltaEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "The settled transcript for one utterance."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct TranscriptFinalEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub text: String,
    pub confidence: Option<f32>,
}

impl TranscriptFinalEvent {
    pub const NAME: &'static str = "TranscriptFinalEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "The exchange finished and it is the other side's turn."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct TurnCompleteEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub turn_index: u32,
    pub was_interrupted: bool,
}

impl TurnCompleteEvent {
    pub const NAME: &'static str = "TurnCompleteEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "The model asked for a tool. Carries the arguments AS TEXT rather than \
     parsed, so a caller validates them itself instead of trusting a shape \
     something else decided."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ToolCallEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub tool: String,
    pub arguments_json: String,
    pub call_id: String,
}

impl ToolCallEvent {
    pub const NAME: &'static str = "ToolCallEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "Something went wrong. `fatal` separates a hiccup from a dead session, \
     because those demand opposite reactions."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct SessionErrorEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub code: String,
    pub message: String,
    pub fatal: bool,
}

impl SessionErrorEvent {
    pub const NAME: &'static str = "SessionErrorEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "The caller began talking, on a phone line."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CallerSpeechStartedEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
}

impl CallerSpeechStartedEvent {
    pub const NAME: &'static str = "CallerSpeechStartedEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "The caller stopped."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CallerSpeechEndedEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub silence_ms: u64,
}

impl CallerSpeechEndedEvent {
    pub const NAME: &'static str = "CallerSpeechEndedEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "A partial transcript on a call."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct TranscriptInterimEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub text: String,
}

impl TranscriptInterimEvent {
    pub const NAME: &'static str = "TranscriptInterimEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "The settled transcript on a call, WITH a word-level breakdown the first \
     version did not carry. The suffix stays: renaming it would hide that two \
     shapes exist on the wire."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct TranscriptFinalEvent_v2 {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub text: String,
    pub confidence: Option<f32>,
    pub words: Vec<(String, u64, u64)>,
}

impl TranscriptFinalEvent_v2 {
    pub const NAME: &'static str = "TranscriptFinalEvent_v2";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "The assistant is working. Emitted BEFORE the answer exists so a filler can \
     start - silence on a phone line reads as a dropped call."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct AgentThinkingEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
}

impl AgentThinkingEvent {
    pub const NAME: &'static str = "AgentThinkingEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "The assistant began speaking."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct AgentSpeakingStartedEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
}

impl AgentSpeakingStartedEvent {
    pub const NAME: &'static str = "AgentSpeakingStartedEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "The assistant finished, or was cut off."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct AgentSpeakingFinishedEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub was_interrupted: bool,
}

impl AgentSpeakingFinishedEvent {
    pub const NAME: &'static str = "AgentSpeakingFinishedEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "Something went wrong on a call."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct SpeechErrorEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub code: String,
    pub message: String,
    pub fatal: bool,
}

impl SpeechErrorEvent {
    pub const NAME: &'static str = "SpeechErrorEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "An agent started or finished a piece of work, for a live view of a \
     workflow."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct AgentActivityEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub agent: String,
    pub activity: String,
    pub finished: bool,
}

impl AgentActivityEvent {
    pub const NAME: &'static str = "AgentActivityEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "A conversation moved to its next step."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ConversationStepEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub step: String,
    pub index: u32,
}

impl ConversationStepEvent {
    pub const NAME: &'static str = "ConversationStepEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "Somebody's cursor moved in a shared document. THE MOST FREQUENT EVENT here \
     by far, which is why it carries nothing but a position."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct DocCursorMoveEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub author: String,
    pub offset: usize,
}

impl DocCursorMoveEvent {
    pub const NAME: &'static str = "DocCursorMoveEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "Cached data went stale and should be fetched again. Names the KEY rather \
     than carrying the new value, so a client that does not care about that \
     query does no work."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct QueryInvalidationEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub key: String,
}

impl QueryInvalidationEvent {
    pub const NAME: &'static str = "QueryInvalidationEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}

#[doc = "A task changed."]
#[derive(Debug, Clone, PartialEq, Default)]
pub struct TaskUpdatedEvent {
    /// Which session or call this belongs to. ALWAYS present - an event
    /// that cannot be attributed cannot be acted on.
    pub session_id: String,
    pub at_ms: u64,
    pub task_id: String,
    pub status: String,
}

impl TaskUpdatedEvent {
    pub const NAME: &'static str = "TaskUpdatedEvent";

    pub fn new(session_id: &str, at_ms: u64) -> Self {
        Self {
            session_id: session_id.to_string(),
            at_ms,
            ..Default::default()
        }
    }
}


/// Marks this package for a host that discovers assemblies by marker type.
///
/// Carries the wire version, because that is the fact a host actually needs
/// when deciding whether it can talk to this build.
#[derive(Debug, Default, Clone, Copy)]
pub struct RealtimePackageMarker;

impl RealtimePackageMarker {
    pub const WIRE_VERSION: u32 = 2;

    /// Whether a peer speaking `their_version` can be talked to.
    ///
    /// An OLDER peer is fine - the events it does not know it ignores. A NEWER
    /// one is not, because it may send a shape this build will misread rather
    /// than reject.
    pub fn can_talk_to(their_version: u32) -> bool {
        their_version <= Self::WIRE_VERSION
    }
}

/// Wires the realtime surface.
#[derive(Debug, Default)]
pub struct RealtimeServiceCollectionExtensions {
    registered: Vec<String>,
}

impl RealtimeServiceCollectionExtensions {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn add(&mut self, name: &str) -> &mut Self {
        if !self.registered.iter().any(|r| r == name) {
            self.registered.push(name.to_string());
        }
        self
    }

    pub fn registered(&self) -> &[String] {
        &self.registered
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Testing helpers

/// A clock that does not move unless told to.
///
/// THE POINT IS DETERMINISM. A test that reads the real clock passes on a fast
/// machine and fails on a loaded one, and the failure looks like a bug in the
/// code rather than in the test.
#[derive(Debug, Clone, Default)]
pub struct FrozenClock {
    now_ms: u64,
}

impl FrozenClock {
    pub fn at(now_ms: u64) -> Self {
        Self { now_ms }
    }

    pub fn now_ms(&self) -> u64 {
        self.now_ms
    }

    /// Moves forward only. A clock that can go backwards makes every
    /// expiry check in the codebase testable in a state that cannot happen -
    /// and hides the one real case, which is a device whose clock was corrected.
    pub fn advance(&mut self, by_ms: u64) -> u64 {
        self.now_ms = self.now_ms.saturating_add(by_ms);
        self.now_ms
    }

    /// The one way to go backwards, named so a test that does it is obvious -
    /// because it is testing exactly that case.
    pub fn rewind_for_clock_correction_test(&mut self, to_ms: u64) {
        self.now_ms = to_ms;
    }
}

/// Identifiers that are the same on every run.
///
/// A random id in a snapshot makes every comparison fail, and the usual fix -
/// stripping ids before comparing - also strips the case where the wrong id was
/// used.
#[derive(Debug, Clone, Default)]
pub struct DeterministicIds {
    counters: HashMap<String, u64>,
}

impl DeterministicIds {
    pub fn new() -> Self {
        Self::default()
    }

    /// `prefix-1`, `prefix-2`, and so on. Per-prefix, so adding a new kind of
    /// id does not renumber the existing ones and rewrite every snapshot.
    pub fn next(&mut self, prefix: &str) -> String {
        let counter = self.counters.entry(prefix.to_string()).or_insert(0);
        *counter += 1;
        format!("{prefix}-{counter}")
    }

    pub fn reset(&mut self) {
        self.counters.clear();
    }
}

/// What changed between a recorded output and a new one.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SnapshotDiff {
    /// `(line number, expected, actual)`. Line numbers are 1-based, matching
    /// what an editor shows.
    pub changes: Vec<(usize, String, String)>,
    pub added: Vec<(usize, String)>,
    pub removed: Vec<(usize, String)>,
}

impl SnapshotDiff {
    pub fn is_empty(&self) -> bool {
        self.changes.is_empty() && self.added.is_empty() && self.removed.is_empty()
    }

    /// A report somebody can act on without opening both files.
    pub fn describe(&self) -> String {
        if self.is_empty() {
            return "no change".into();
        }
        let mut out = Vec::new();
        for (line, expected, actual) in &self.changes {
            out.push(format!("line {line}: expected {expected:?}, got {actual:?}"));
        }
        for (line, text) in &self.added {
            out.push(format!("line {line}: unexpected {text:?}"));
        }
        for (line, text) in &self.removed {
            out.push(format!("line {line}: missing {text:?}"));
        }
        out.join("\n")
    }
}

/// Compares a new output against a recorded one.
pub trait SnapshotComparer {
    fn compare(&self, expected: &str, actual: &str) -> SnapshotDiff;
}

/// Compares line by line.
#[derive(Debug, Default, Clone, Copy)]
pub struct LineDiffSnapshotComparer {
    /// Whether trailing whitespace counts. Off by default: an editor that strips
    /// it would otherwise fail every snapshot.
    pub strict_whitespace: bool,
}

impl SnapshotComparer for LineDiffSnapshotComparer {
    fn compare(&self, expected: &str, actual: &str) -> SnapshotDiff {
        // Line endings are normalised FIRST. A snapshot recorded on one platform
        // and compared on another otherwise differs on every single line, which
        // buries the one real change.
        let prepare = |text: &str| -> Vec<String> {
            text.replace("\r\n", "\n")
                .lines()
                .map(|l| {
                    if self.strict_whitespace {
                        l.to_string()
                    } else {
                        l.trim_end().to_string()
                    }
                })
                .collect()
        };
        let (expected, actual) = (prepare(expected), prepare(actual));

        let mut diff = SnapshotDiff::default();
        for i in 0..expected.len().max(actual.len()) {
            match (expected.get(i), actual.get(i)) {
                (Some(e), Some(a)) if e != a => diff.changes.push((i + 1, e.clone(), a.clone())),
                (Some(e), None) => diff.removed.push((i + 1, e.clone())),
                (None, Some(a)) => diff.added.push((i + 1, a.clone())),
                _ => {}
            }
        }
        diff
    }
}

/// Compares nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSnapshotComparer;

impl SnapshotComparer for NullSnapshotComparer {
    /// Reports a DIFFERENCE rather than a match. A comparer that says everything
    /// matches turns a whole suite green while checking nothing, which is the
    /// most expensive kind of false confidence here.
    fn compare(&self, _expected: &str, actual: &str) -> SnapshotDiff {
        SnapshotDiff {
            changes: vec![(
                1,
                "<no comparer configured>".into(),
                actual.lines().next().unwrap_or("").to_string(),
            )],
            ..Default::default()
        }
    }
}

/// Keeps recorded outputs.
pub trait GoldenStore {
    fn get(&self, name: &str) -> Option<String>;
    fn put(&mut self, name: &str, content: &str) -> Result<(), String>;
    fn names(&self) -> Vec<String>;
    /// Whether recording over an existing snapshot is allowed. OFF unless
    /// somebody turned it on, because a suite that rewrites its own
    /// expectations passes by definition.
    fn accepts_updates(&self) -> bool;
}

/// Snapshots in memory.
#[derive(Debug, Default)]
pub struct InMemoryGoldenStore {
    golden: HashMap<String, String>,
    update: bool,
}

impl InMemoryGoldenStore {
    pub fn new(update: bool) -> Self {
        Self { golden: HashMap::new(), update }
    }
}

impl GoldenStore for InMemoryGoldenStore {
    fn get(&self, name: &str) -> Option<String> {
        self.golden.get(name).cloned()
    }

    fn put(&mut self, name: &str, content: &str) -> Result<(), String> {
        if self.golden.contains_key(name) && !self.update {
            return Err(format!(
                "'{name}' is already recorded; run with updates allowed to change it"
            ));
        }
        self.golden.insert(name.to_string(), content.to_string());
        Ok(())
    }

    fn names(&self) -> Vec<String> {
        let mut out: Vec<String> = self.golden.keys().cloned().collect();
        out.sort();
        out
    }

    fn accepts_updates(&self) -> bool {
        self.update
    }
}

/// Keeps none.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullGoldenStore;

impl GoldenStore for NullGoldenStore {
    fn get(&self, _name: &str) -> Option<String> {
        None
    }
    fn put(&mut self, _name: &str, _content: &str) -> Result<(), String> {
        Err("no snapshot store is configured; nothing was recorded".into())
    }
    fn names(&self) -> Vec<String> {
        Vec::new()
    }
    fn accepts_updates(&self) -> bool {
        false
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Dependencies

/// Something this project depends on.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Dependency {
    pub name: String,
    pub version: String,
    /// Which manifest declares it, so a fix has somewhere to go.
    pub manifest: String,
    /// The ecosystem: `cargo`, `npm`, `nuget`, `pip`. Versions mean different
    /// things in each, so a comparison without this is a comparison of strings.
    pub ecosystem: String,
    /// Its licence. THE FIRST THING CHECKED, before a version - a dependency
    /// under the wrong licence is not upgradable, it is removable.
    pub licence: String,
    pub direct: bool,
}

impl Dependency {
    /// The licences this codebase may use: permissive only.
    pub const ALLOWED: &'static [&'static str] =
        &["MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "OFL-1.1", "CC0-1.0", "Unlicense"];

    /// An UNRECOGNISED licence is not allowed.
    ///
    /// Defaulting to permitted means the one dependency nobody checked is the
    /// one that ships - and a copyleft licence pulled into a linked binary is
    /// not a thing that can be undone quietly later.
    pub fn licence_is_allowed(&self) -> bool {
        let licence = self.licence.trim();
        !licence.is_empty()
            && licence
                .split(|c| c == '/' || c == ',')
                .flat_map(|part| part.split(" OR "))
                .any(|part| {
                    Self::ALLOWED
                        .iter()
                        .any(|a| a.eq_ignore_ascii_case(part.trim()))
                })
    }

    /// Compares versions by numeric component, so `1.10.0` sorts above `1.9.0`.
    ///
    /// A string comparison puts `1.9.0` above `1.10.0`, which reports the newer
    /// version as a downgrade and skips the upgrade entirely.
    pub fn is_newer(candidate: &str, current: &str) -> bool {
        let parts = |v: &str| -> Vec<u64> {
            v.trim_start_matches(['v', '^', '~', '='])
                .split(['.', '-', '+'])
                .map(|p| p.parse::<u64>().unwrap_or(0))
                .collect()
        };
        let (a, b) = (parts(candidate), parts(current));
        for i in 0..a.len().max(b.len()) {
            let (x, y) = (a.get(i).copied().unwrap_or(0), b.get(i).copied().unwrap_or(0));
            if x != y {
                return x > y;
            }
        }
        false
    }
}

/// A version somebody could move to.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct DependencyUpdate {
    pub dependency: Dependency,
    pub to_version: String,
    /// Whether the major number changes. A major bump is a decision, not an
    /// update, and an automatic one is how a build breaks overnight.
    pub is_major: bool,
    /// Whether this closes a known vulnerability. The one reason to take a major
    /// bump without discussion.
    pub is_security: bool,
    pub notes: String,
}

impl DependencyUpdate {
    /// Whether it can be applied without asking.
    ///
    /// Minor and patch only, and never one that changes the licence. A security
    /// fix is surfaced rather than applied - taking a major bump automatically
    /// to close a hole trades one broken thing for another.
    pub fn is_safe_to_apply(&self) -> bool {
        !self.is_major && self.dependency.licence_is_allowed()
    }
}

/// Reads what a project depends on.
pub trait DependencyAnalyzer {
    fn is_available(&self) -> bool;
    fn analyse(&self, manifest_path: &str, content: &str) -> Vec<Dependency>;
    /// Anything whose licence is not permitted. The list that blocks a release.
    fn licence_problems(&self, dependencies: &[Dependency]) -> Vec<Dependency>;
}

/// Reads manifests off the filesystem.
#[derive(Debug, Default, Clone, Copy)]
pub struct FilesystemDependencyAnalyzer;

impl FilesystemDependencyAnalyzer {
    /// Which ecosystem a manifest belongs to, by its name.
    pub fn ecosystem_of(manifest_path: &str) -> &'static str {
        let name = manifest_path.rsplit(['/', '\\']).next().unwrap_or("");
        match name {
            "Cargo.toml" => "cargo",
            "package.json" => "npm",
            "requirements.txt" | "pyproject.toml" => "pip",
            n if n.ends_with(".csproj") || n.ends_with(".fsproj") => "nuget",
            "go.mod" => "go",
            _ => "",
        }
    }
}

impl DependencyAnalyzer for FilesystemDependencyAnalyzer {
    fn is_available(&self) -> bool {
        true
    }

    /// Line-based, and honest about it: a real manifest parser is per-ecosystem
    /// and this finds `name = "version"` and `"name": "version"`, which is what
    /// the four manifests here actually contain.
    fn analyse(&self, manifest_path: &str, content: &str) -> Vec<Dependency> {
        let ecosystem = Self::ecosystem_of(manifest_path);
        let mut out = Vec::new();
        let mut in_dependencies = ecosystem == "pip";
        for line in content.lines() {
            let trimmed = line.trim();
            if trimmed.starts_with('[') {
                in_dependencies = trimmed.contains("dependencies");
                continue;
            }
            if trimmed.contains("\"dependencies\"") {
                in_dependencies = true;
                continue;
            }
            if !in_dependencies || trimmed.is_empty() || trimmed.starts_with('#') {
                continue;
            }
            let (name, version) = match trimmed.split_once(['=', ':']) {
                Some((n, v)) => (
                    n.trim().trim_matches(['"', '\'']).to_string(),
                    v.trim()
                        .trim_end_matches(',')
                        .trim_matches(['"', '\'', '{', '}'])
                        .trim()
                        .to_string(),
                ),
                None => continue,
            };
            if name.is_empty() || name.contains(' ') {
                continue;
            }
            out.push(Dependency {
                name,
                version,
                manifest: manifest_path.to_string(),
                ecosystem: ecosystem.to_string(),
                direct: true,
                ..Default::default()
            });
        }
        out
    }

    fn licence_problems(&self, dependencies: &[Dependency]) -> Vec<Dependency> {
        dependencies
            .iter()
            .filter(|d| !d.licence_is_allowed())
            .cloned()
            .collect()
    }
}

/// Reads nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullDependencyAnalyzer;

impl DependencyAnalyzer for NullDependencyAnalyzer {
    fn is_available(&self) -> bool {
        false
    }
    fn analyse(&self, _manifest_path: &str, _content: &str) -> Vec<Dependency> {
        Vec::new()
    }
    /// Reports NOTHING checked rather than nothing wrong - the two look
    /// identical from a caller and mean opposite things.
    fn licence_problems(&self, dependencies: &[Dependency]) -> Vec<Dependency> {
        dependencies.to_vec()
    }
}

/// Applies an update to a manifest.
pub trait DependencyUpdater {
    fn is_available(&self) -> bool;
    fn apply(&self, content: &str, update: &DependencyUpdate) -> Result<String, String>;
}

/// Rewrites the version in place.
///
/// A TEXT REWRITE, deliberately: re-serialising a manifest reformats the whole
/// file and produces a diff nobody can review, which is how an unrelated change
/// rides along with a version bump.
#[derive(Debug, Default, Clone, Copy)]
pub struct TextRewriteDependencyUpdater;

impl DependencyUpdater for TextRewriteDependencyUpdater {
    fn is_available(&self) -> bool {
        true
    }

    fn apply(&self, content: &str, update: &DependencyUpdate) -> Result<String, String> {
        if !update.is_safe_to_apply() {
            return Err(format!(
                "{} {} -> {} is not applied automatically{}",
                update.dependency.name,
                update.dependency.version,
                update.to_version,
                if update.is_major { " because it is a major version" } else { " because of its licence" }
            ));
        }
        let mut changed = false;
        let out: Vec<String> = content
            .lines()
            .map(|line| {
                // Only the line that names the dependency AND carries its
                // current version. Matching on the name alone rewrites a
                // comment mentioning it.
                if !changed
                    && line.contains(&update.dependency.name)
                    && line.contains(&update.dependency.version)
                {
                    changed = true;
                    line.replacen(&update.dependency.version, &update.to_version, 1)
                } else {
                    line.to_string()
                }
            })
            .collect();
        if !changed {
            return Err(format!(
                "{} {} was not found in that manifest",
                update.dependency.name, update.dependency.version
            ));
        }
        Ok(out.join("\n") + if content.ends_with('\n') { "\n" } else { "" })
    }
}

/// Applies nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullDependencyUpdater;

impl DependencyUpdater for NullDependencyUpdater {
    fn is_available(&self) -> bool {
        false
    }
    fn apply(&self, _content: &str, _update: &DependencyUpdate) -> Result<String, String> {
        Err("no updater is configured; the manifest was not touched".into())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The model operator

/// Where a model deployment has got to.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ModelLifecyclePhase {
    #[default]
    Absent,
    Downloading,
    /// Checking the digest. On a phone, hashing four gigabytes is a real wait,
    /// and a progress bar sitting at 100% with no explanation reads as a hang.
    Verifying,
    Loading,
    Ready,
    /// Being taken out of use, but still serving what is in flight. A model
    /// unloaded under a live request fails that request.
    Draining,
    Unloaded,
    Failed,
}

impl ModelLifecyclePhase {
    pub fn is_usable(&self) -> bool {
        matches!(self, Self::Ready | Self::Draining)
    }

    /// Whether new work may be sent. Draining takes no NEW work while still
    /// finishing what it has - which is the whole difference between draining
    /// and unloading.
    pub fn accepts_new_work(&self) -> bool {
        *self == Self::Ready
    }
}

/// How a model is doing right now.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ModelStatus {
    pub model_id: String,
    pub phase: ModelLifecyclePhase,
    pub loaded_bytes: u64,
    pub total_bytes: u64,
    pub in_flight: usize,
    /// Why it failed, in words. A phase of `Failed` with no reason sends
    /// somebody to the logs of a device they may not be holding.
    pub detail: String,
}

impl ModelStatus {
    /// `None` when the total is unknown, rather than 0% - a progress bar that
    /// sits at zero and then jumps to done is worse than no bar.
    pub fn progress(&self) -> Option<f32> {
        (self.total_bytes > 0)
            .then(|| (self.loaded_bytes as f64 / self.total_bytes as f64) as f32)
    }

    /// Whether it is safe to unload. Never while requests are in flight.
    pub fn can_unload(&self) -> bool {
        self.in_flight == 0 && self.phase != ModelLifecyclePhase::Loading
    }
}

/// One model being put into service.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ModelDeployment {
    pub model_id: String,
    pub revision: String,
    /// The digest of what should arrive. A deployment WITHOUT one cannot be
    /// verified, and an unverifiable model is not deployed.
    pub sha256: String,
    pub requested_at_ms: u64,
    pub requested_by: String,
}

impl ModelDeployment {
    pub fn is_verifiable(&self) -> bool {
        !self.sha256.trim().is_empty()
    }
}

/// Puts models into and out of service.
pub trait ModelOperator {
    fn deploy(&mut self, deployment: ModelDeployment) -> Result<(), String>;
    fn status(&self, model_id: &str) -> Option<ModelStatus>;
    /// Stops taking new work, keeps serving what is in flight.
    fn drain(&mut self, model_id: &str) -> Result<(), String>;
    fn unload(&mut self, model_id: &str) -> Result<(), String>;
    fn deployed(&self) -> Vec<ModelStatus>;
}

/// An operator in memory.
#[derive(Debug, Default)]
pub struct InMemoryModelOperator {
    statuses: HashMap<String, ModelStatus>,
    /// How much memory the device has to work with. Zero means UNKNOWN, and an
    /// unknown budget refuses rather than guesses.
    budget_bytes: u64,
}

impl InMemoryModelOperator {
    pub fn new(budget_bytes: u64) -> Self {
        Self { statuses: HashMap::new(), budget_bytes }
    }

    /// What is committed to models already in service.
    pub fn committed_bytes(&self) -> u64 {
        self.statuses
            .values()
            .filter(|s| s.phase.is_usable())
            .map(|s| s.total_bytes)
            .sum()
    }
}

impl ModelOperator for InMemoryModelOperator {
    /// REFUSES rather than overcommits.
    ///
    /// A device that loads one model too many is killed by the operating system
    /// mid-request, and from the outside that is indistinguishable from a crash.
    /// Refusing is visible and fixable.
    fn deploy(&mut self, deployment: ModelDeployment) -> Result<(), String> {
        if deployment.model_id.trim().is_empty() {
            return Err("a deployment needs a model".into());
        }
        if !deployment.is_verifiable() {
            return Err(format!(
                "{} has no checksum, so it will not be deployed",
                deployment.model_id
            ));
        }
        if self.budget_bytes == 0 {
            return Err(
                "this device's memory has not been measured, so nothing will be sized to it"
                    .into(),
            );
        }
        self.statuses.insert(
            deployment.model_id.clone(),
            ModelStatus {
                model_id: deployment.model_id,
                phase: ModelLifecyclePhase::Downloading,
                ..Default::default()
            },
        );
        Ok(())
    }

    fn status(&self, model_id: &str) -> Option<ModelStatus> {
        self.statuses.get(model_id).cloned()
    }

    fn drain(&mut self, model_id: &str) -> Result<(), String> {
        let Some(status) = self.statuses.get_mut(model_id) else {
            return Err("that model is not deployed".into());
        };
        if !status.phase.is_usable() {
            return Err("that model is not in service".into());
        }
        status.phase = ModelLifecyclePhase::Draining;
        Ok(())
    }

    fn unload(&mut self, model_id: &str) -> Result<(), String> {
        let Some(status) = self.statuses.get_mut(model_id) else {
            return Err("that model is not deployed".into());
        };
        if !status.can_unload() {
            return Err(format!(
                "{} still has {} requests in flight",
                model_id, status.in_flight
            ));
        }
        status.phase = ModelLifecyclePhase::Unloaded;
        Ok(())
    }

    fn deployed(&self) -> Vec<ModelStatus> {
        let mut out: Vec<ModelStatus> = self.statuses.values().cloned().collect();
        out.sort_by(|a, b| a.model_id.cmp(&b.model_id));
        out
    }
}

/// Operates nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullModelOperator;

impl ModelOperator for NullModelOperator {
    fn deploy(&mut self, _deployment: ModelDeployment) -> Result<(), String> {
        Err("no model operator is configured on this device".into())
    }
    fn status(&self, _model_id: &str) -> Option<ModelStatus> {
        None
    }
    fn drain(&mut self, _model_id: &str) -> Result<(), String> {
        Err("no model operator is configured on this device".into())
    }
    fn unload(&mut self, _model_id: &str) -> Result<(), String> {
        Err("no model operator is configured on this device".into())
    }
    fn deployed(&self) -> Vec<ModelStatus> {
        Vec::new()
    }
}

/// Watches deployments.
pub trait DeploymentObserver {
    fn on_phase(&mut self, model_id: &str, phase: ModelLifecyclePhase, at_ms: u64);
    fn on_failure(&mut self, model_id: &str, detail: &str, at_ms: u64);
}

/// Watches nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullDeploymentObserver;

impl DeploymentObserver for NullDeploymentObserver {
    fn on_phase(&mut self, _model_id: &str, _phase: ModelLifecyclePhase, _at_ms: u64) {}
    fn on_failure(&mut self, _model_id: &str, _detail: &str, _at_ms: u64) {}
}

// ─────────────────────────────────────────────────────────────────────────────
// Plugin hosting

/// The event names a plugin may listen for.
///
/// A FIXED SET. Free-form event names produce three spellings of the same thing
/// and a plugin that silently never fires.
pub struct PluginEventNames;

impl PluginEventNames {
    pub const STARTED: &'static str = "plugin.started";
    pub const STOPPING: &'static str = "plugin.stopping";
    pub const PERMISSION_DENIED: &'static str = "plugin.permission-denied";
    pub const MESSAGE: &'static str = "plugin.message";
    pub const SETTINGS_CHANGED: &'static str = "plugin.settings-changed";

    pub const ALL: &'static [&'static str] = &[
        Self::STARTED, Self::STOPPING, Self::PERMISSION_DENIED,
        Self::MESSAGE, Self::SETTINGS_CHANGED,
    ];

    pub fn is_known(name: &str) -> bool {
        Self::ALL.contains(&name)
    }
}

/// What a plugin can subscribe to.
pub trait PluginEvents {
    fn subscribe(&mut self, event: &str) -> Result<(), String>;
    fn emit(&mut self, event: &str, payload: &str) -> usize;
    fn subscribed(&self) -> Vec<String>;
}

/// The default event bus.
#[derive(Debug, Default)]
pub struct InMemoryPluginEvents {
    subscriptions: HashSet<String>,
    delivered: Vec<(String, String)>,
}

impl InMemoryPluginEvents {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn delivered(&self) -> &[(String, String)] {
        &self.delivered
    }
}

impl PluginEvents for InMemoryPluginEvents {
    fn subscribe(&mut self, event: &str) -> Result<(), String> {
        if !PluginEventNames::is_known(event) {
            return Err(format!(
                "'{event}' is not an event; the known ones are {}",
                PluginEventNames::ALL.join(", ")
            ));
        }
        self.subscriptions.insert(event.to_string());
        Ok(())
    }

    fn emit(&mut self, event: &str, payload: &str) -> usize {
        if !self.subscriptions.contains(event) {
            return 0;
        }
        self.delivered.push((event.to_string(), payload.to_string()));
        1
    }

    fn subscribed(&self) -> Vec<String> {
        let mut out: Vec<String> = self.subscriptions.iter().cloned().collect();
        out.sort();
        out
    }
}

/// What a plugin is handed when it runs.
pub trait PluginContext {
    fn plugin_id(&self) -> &str;
    fn workspace(&self) -> &str;
    fn read(&self, path: &str) -> Result<String, String>;
    fn write(&mut self, path: &str, content: &str) -> Result<(), String>;
    fn fetch(&self, url: &str) -> Result<String, String>;
    fn infer(&self, prompt: &str) -> Result<String, String>;
}

/// A context that carries a plugin's identity and its workspace.
#[derive(Debug, Clone, Default)]
pub struct DefaultPluginContext {
    pub plugin_id: String,
    pub workspace: String,
}

impl DefaultPluginContext {
    pub fn new(plugin_id: &str, workspace: &str) -> Self {
        Self {
            plugin_id: plugin_id.to_string(),
            workspace: workspace.to_string(),
        }
    }

    /// Whether a path stays inside the workspace.
    ///
    /// Contained by NORMALISING the segments, not by searching for `..` in the
    /// text: a search misses an absolute path that overrides the join entirely,
    /// and misses a backslash on a platform that treats it as a separator.
    pub fn contains(&self, path: &str) -> Option<String> {
        if path.starts_with('/') || path.starts_with('\\') {
            return None;
        }
        if path.len() >= 2 && path.as_bytes()[1] == b':' {
            return None;
        }
        let mut segments: Vec<&str> = Vec::new();
        for part in path.split(['/', '\\']) {
            match part {
                "" | "." => continue,
                ".." => {
                    segments.pop()?;
                }
                other => segments.push(other),
            }
        }
        (!segments.is_empty()).then(|| format!("{}/{}", self.workspace, segments.join("/")))
    }
}

impl PluginContext for DefaultPluginContext {
    fn plugin_id(&self) -> &str {
        &self.plugin_id
    }
    fn workspace(&self) -> &str {
        &self.workspace
    }
    fn read(&self, _path: &str) -> Result<String, String> {
        Err("this context has no filesystem".into())
    }
    fn write(&mut self, _path: &str, _content: &str) -> Result<(), String> {
        Err("this context has no filesystem".into())
    }
    fn fetch(&self, _url: &str) -> Result<String, String> {
        Err("this context has no network".into())
    }
    fn infer(&self, _prompt: &str) -> Result<String, String> {
        Err("this context has no model".into())
    }
}

/// A context that checks a permission before every single operation.
///
/// THE CHECK IS HERE, not at the caller. A plugin holds this object and nothing
/// else, so there is no path to a file, a socket or a model that does not pass
/// through a permission check first - which is what makes the permission real
/// rather than advisory.
pub struct PermissionedPluginContext {
    inner: DefaultPluginContext,
    permissions: crate::platform_plugins::Permissions,
    #[allow(clippy::type_complexity)]
    read_file: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
    #[allow(clippy::type_complexity)]
    write_file: Option<Box<dyn Fn(&str, &str) -> Result<(), String> + Send + Sync>>,
    #[allow(clippy::type_complexity)]
    http: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
    #[allow(clippy::type_complexity)]
    model: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
    denials: Vec<String>,
}

impl PermissionedPluginContext {
    #[allow(clippy::type_complexity)]
    pub fn new(
        inner: DefaultPluginContext,
        permissions: crate::platform_plugins::Permissions,
        read_file: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
        write_file: Option<Box<dyn Fn(&str, &str) -> Result<(), String> + Send + Sync>>,
        http: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
        model: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
    ) -> Self {
        Self {
            inner,
            permissions,
            read_file,
            write_file,
            http,
            model,
            denials: Vec::new(),
        }
    }

    /// What it tried and was not allowed to do. Shown to the person, because a
    /// plugin repeatedly asking for something it was refused is worth knowing.
    pub fn denials(&self) -> &[String] {
        &self.denials
    }

    fn deny(&mut self, what: &str) -> String {
        let message = format!("'{}' is not allowed to {what}", self.inner.plugin_id);
        self.denials.push(message.clone());
        message
    }
}

impl PluginContext for PermissionedPluginContext {
    fn plugin_id(&self) -> &str {
        self.inner.plugin_id()
    }

    fn workspace(&self) -> &str {
        self.inner.workspace()
    }

    fn read(&self, path: &str) -> Result<String, String> {
        if !self.permissions.read_files {
            return Err(format!("'{}' is not allowed to read files", self.inner.plugin_id));
        }
        let Some(contained) = self.inner.contains(path) else {
            return Err("that path is outside the plugin's own folder".into());
        };
        let Some(read) = &self.read_file else {
            return Err("this build has no filesystem for plugins".into());
        };
        read(&contained)
    }

    fn write(&mut self, path: &str, content: &str) -> Result<(), String> {
        if !self.permissions.write_files {
            return Err(self.deny("change files"));
        }
        let Some(contained) = self.inner.contains(path) else {
            return Err("that path is outside the plugin's own folder".into());
        };
        let Some(write) = &self.write_file else {
            return Err("this build has no filesystem for plugins".into());
        };
        write(&contained, content)
    }

    fn fetch(&self, url: &str) -> Result<String, String> {
        if !self.permissions.network {
            return Err(format!(
                "'{}' is not allowed to use the internet",
                self.inner.plugin_id
            ));
        }
        let Some(http) = &self.http else {
            return Err("this build has no network for plugins".into());
        };
        http(url)
    }

    fn infer(&self, prompt: &str) -> Result<String, String> {
        if !self.permissions.inference {
            return Err(format!(
                "'{}' is not allowed to use the model",
                self.inner.plugin_id
            ));
        }
        let Some(model) = &self.model else {
            return Err("this build has no model for plugins".into());
        };
        model(prompt)
    }
}

/// What a plugin implements.
pub trait Plugin {
    fn plugin_id(&self) -> &str;
    fn version(&self) -> &str;
    /// Called once, with everything it will ever be able to reach.
    fn start(&mut self, context: &mut dyn PluginContext) -> Result<(), String>;
    fn handle(&mut self, event: &str, payload: &str) -> Option<String>;
    /// Must be IDEMPOTENT. A plugin is stopped by a navigation and by a teardown
    /// and often by both within a frame of each other.
    fn stop(&mut self);
}

// ─────────────────────────────────────────────────────────────────────────────
// Tools

/// One argument a tool takes.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ToolParameter {
    pub name: String,
    /// `string`, `number`, `boolean`, `array`, `object` - the JSON Schema words,
    /// because that is what a model was trained on.
    pub kind: String,
    pub description: String,
    pub required: bool,
    /// The allowed values, when there are few. A closed set beats a description
    /// asking the model to pick one of three.
    pub allowed: Vec<String>,
}

/// Builds a tool definition a model can be given.
///
/// THE DESCRIPTION IS THE INTERFACE. A model chooses a tool by reading it, so a
/// vague description is a tool called at the wrong moment - and one that does not
/// say what the tool CHANGES is a tool called when nobody wanted anything
/// changed.
#[derive(Debug, Default, Clone)]
pub struct ToolDefinitionBuilder {
    name: String,
    description: String,
    parameters: Vec<ToolParameter>,
    read_only: bool,
}

impl ToolDefinitionBuilder {
    pub fn new(name: &str) -> Self {
        Self { name: name.to_string(), read_only: true, ..Default::default() }
    }

    pub fn describe(mut self, description: &str) -> Self {
        self.description = description.to_string();
        self
    }

    /// Says this tool CHANGES something. Off by default, so a tool that acts has
    /// to say so rather than a tool that reads having to say it does not.
    pub fn changes_things(mut self) -> Self {
        self.read_only = false;
        self
    }

    pub fn parameter(mut self, parameter: ToolParameter) -> Self {
        self.parameters.push(parameter);
        self
    }

    pub fn is_read_only(&self) -> bool {
        self.read_only
    }

    /// The JSON Schema a model is given.
    ///
    /// `required` is a LIST at the object level, not a flag per property - a
    /// schema that puts it on the property is silently ignored, and every
    /// argument becomes optional.
    pub fn build(&self) -> Result<String, String> {
        if self.name.trim().is_empty() {
            return Err("a tool needs a name".into());
        }
        if self.description.trim().is_empty() {
            return Err(format!(
                "'{}' has no description, so a model cannot know when to use it",
                self.name
            ));
        }
        let escape = |s: &str| s.replace('\\', "\\\\").replace('"', "\\\"");
        let properties: Vec<String> = self
            .parameters
            .iter()
            .map(|p| {
                let allowed = if p.allowed.is_empty() {
                    String::new()
                } else {
                    format!(
                        ",\"enum\":[{}]",
                        p.allowed
                            .iter()
                            .map(|a| format!("\"{}\"", escape(a)))
                            .collect::<Vec<_>>()
                            .join(",")
                    )
                };
                format!(
                    "\"{}\":{{\"type\":\"{}\",\"description\":\"{}\"{allowed}}}",
                    escape(&p.name),
                    escape(&p.kind),
                    escape(&p.description)
                )
            })
            .collect();
        let required: Vec<String> = self
            .parameters
            .iter()
            .filter(|p| p.required)
            .map(|p| format!("\"{}\"", escape(&p.name)))
            .collect();
        Ok(format!(
            "{{\"name\":\"{}\",\"description\":\"{}\",\"readOnly\":{},\
\"parameters\":{{\"type\":\"object\",\"properties\":{{{}}},\"required\":[{}]}}}}",
            escape(&self.name),
            escape(&self.description),
            self.read_only,
            properties.join(","),
            required.join(",")
        ))
    }
}

/// Writes out everything a build offers.
#[derive(Debug, Default)]
pub struct ToolManifestGenerator {
    tools: Vec<ToolDefinitionBuilder>,
}

impl ToolManifestGenerator {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn add(&mut self, tool: ToolDefinitionBuilder) -> &mut Self {
        self.tools.push(tool);
        self
    }

    /// The tools that CHANGE things, listed separately.
    ///
    /// This is what a review screen shows: a manifest of forty tools is not
    /// readable, and the six that can do something irreversible are.
    pub fn acting_tools(&self) -> Vec<String> {
        self.tools
            .iter()
            .filter(|t| !t.is_read_only())
            .map(|t| t.name.clone())
            .collect()
    }

    pub fn generate(&self) -> Result<String, String> {
        let built: Result<Vec<String>, String> =
            self.tools.iter().map(|t| t.build()).collect();
        Ok(format!("[{}]", built?.join(",")))
    }
}

/// Brings tools in over HTTP.
pub struct HttpToolBridge {
    base_url: String,
    #[allow(clippy::type_complexity)]
    call: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
    /// Hosts this bridge may reach. EMPTY MEANS NONE, not all - a bridge that
    /// reaches anywhere by default is a way to make the device fetch a URL a
    /// model was talked into producing.
    allowed_hosts: Vec<String>,
}

impl HttpToolBridge {
    #[allow(clippy::type_complexity)]
    pub fn new(
        base_url: &str,
        call: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
        allowed_hosts: Vec<String>,
    ) -> Self {
        Self { base_url: base_url.to_string(), call, allowed_hosts }
    }

    /// The host of a URL, lower-cased, without a port.
    pub fn host_of(url: &str) -> Option<String> {
        let rest = url.split_once("://")?.1;
        let authority = rest.split(['/', '?', '#']).next()?;
        // Userinfo is stripped: `https://evil.com@allowed.host/` has host
        // `allowed.host` to a browser and `evil.com` to a naive split, which is
        // exactly how an allow-list gets walked past.
        let authority = authority.rsplit('@').next()?;
        Some(authority.split(':').next()?.to_lowercase())
    }

    pub fn is_allowed(&self, url: &str) -> bool {
        let Some(host) = Self::host_of(url) else { return false };
        self.allowed_hosts
            .iter()
            .any(|h| h.to_lowercase() == host)
    }

    pub fn invoke(&self, tool: &str, arguments_json: &str) -> Result<String, String> {
        let url = format!("{}/{tool}", self.base_url.trim_end_matches('/'));
        if !self.is_allowed(&url) {
            return Err(format!(
                "{} is not on this device's list of allowed hosts",
                Self::host_of(&url).unwrap_or_else(|| url.clone())
            ));
        }
        let Some(call) = &self.call else {
            return Err("this build has no way to reach a tool server".into());
        };
        call(&url, arguments_json)
    }
}

/// Brings in tools from Composio.
pub struct ComposioToolBridge {
    inner: HttpToolBridge,
    /// Which tools this device will accept. An empty list means NONE - a bridge
    /// to hundreds of third-party actions, enabled wholesale, is not a decision
    /// anybody made.
    enabled: Vec<String>,
}

impl ComposioToolBridge {
    pub fn new(inner: HttpToolBridge, enabled: Vec<String>) -> Self {
        Self { inner, enabled }
    }

    pub fn enabled_tools(&self) -> &[String] {
        &self.enabled
    }

    pub fn invoke(&self, tool: &str, arguments_json: &str) -> Result<String, String> {
        if !self.enabled.iter().any(|t| t == tool) {
            return Err(format!("'{tool}' is not switched on for this device"));
        }
        self.inner.invoke(tool, arguments_json)
    }
}

/// Tools that report on the device itself.
///
/// READ ONLY, all of them. Reporting battery and memory is one thing; changing a
/// radio or a system setting is a device-scoped action and there is no tool here
/// that does it.
#[derive(Debug, Default, Clone, Copy)]
pub struct DeviceDiagnosticsTools;

impl DeviceDiagnosticsTools {
    pub const NAMES: &'static [&'static str] = &[
        "device.memory", "device.battery", "device.storage",
        "device.thermal", "device.models",
    ];

    /// Nothing here toggles anything.
    pub fn is_read_only(_name: &str) -> bool {
        true
    }

    pub fn definitions() -> Vec<ToolDefinitionBuilder> {
        vec![
            ToolDefinitionBuilder::new("device.memory")
                .describe("How much memory this device has and how much is free."),
            ToolDefinitionBuilder::new("device.battery")
                .describe("The battery level, and whether it is charging."),
            ToolDefinitionBuilder::new("device.storage")
                .describe("Free storage, which is what decides whether a model can be downloaded."),
            ToolDefinitionBuilder::new("device.thermal")
                .describe("Whether the device is too hot to run inference at full speed."),
            ToolDefinitionBuilder::new("device.models")
                .describe("Which models are on this device and which are loaded."),
        ]
    }
}

/// The network's own tools.
#[derive(Debug, Default, Clone, Copy)]
pub struct TheGeekNetworkTools;

impl TheGeekNetworkTools {
    pub const NAMES: &'static [&'static str] =
        &["aether.peers", "aether.send", "aether.tag", "aether.share-app"];

    /// Sending and sharing CHANGE things - they put something on somebody else's
    /// device - and are named here so a manifest can list them apart.
    pub fn is_read_only(name: &str) -> bool {
        matches!(name, "aether.peers" | "aether.tag")
    }

    pub fn definitions() -> Vec<ToolDefinitionBuilder> {
        vec![
            ToolDefinitionBuilder::new("aether.peers")
                .describe("Which nearby devices this one can currently reach."),
            ToolDefinitionBuilder::new("aether.tag")
                .describe("This device's own tag, which is its address on the mesh."),
            ToolDefinitionBuilder::new("aether.send")
                .describe("Sends a message to a device that has been added.")
                .changes_things(),
            ToolDefinitionBuilder::new("aether.share-app")
                .describe("Shares an installed app with a nearby device, over the air, offline.")
                .changes_things(),
        ]
    }
}

/// Face tools.
///
/// EVERY ONE OF THESE IS ON-DEVICE ONLY and none of them stores a face. A
/// template that leaves the device is a biometric somebody cannot change once it
/// is out, which is why there is no upload here and no identify-against-a-list.
#[derive(Debug, Default, Clone, Copy)]
pub struct FacexTools;

impl FacexTools {
    pub const NAMES: &'static [&'static str] =
        &["face.detect", "face.landmarks", "face.blur"];

    pub fn is_read_only(name: &str) -> bool {
        name != "face.blur"
    }

    /// What this deliberately does NOT offer, and why.
    pub const REFUSES: &'static str =
        "matching a face against a list of people, because a face is a \
         biometric nobody can change once it has been taken";

    pub fn definitions() -> Vec<ToolDefinitionBuilder> {
        vec![
            ToolDefinitionBuilder::new("face.detect")
                .describe("Finds where faces are in a picture. Does not say whose."),
            ToolDefinitionBuilder::new("face.landmarks")
                .describe("Finds eyes, nose and mouth in a picture, for framing and effects."),
            ToolDefinitionBuilder::new("face.blur")
                .describe("Blurs the faces in a picture before it is shared.")
                .changes_things(),
        ]
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Media hub

/// Something playable.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct MediaItem {
    pub item_id: String,
    pub title: String,
    pub artist: String,
    pub duration_ms: u64,
    /// Where it is. A local path or a URL - the difference decides whether it
    /// plays with no network.
    pub source: String,
    pub mime_type: String,
}

impl MediaItem {
    pub fn plays_offline(&self) -> bool {
        !self.source.is_empty() && !self.source.contains("://")
    }
}

/// Where playback is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PlaybackPosition {
    pub position_ms: u64,
    pub duration_ms: u64,
    pub playing: bool,
    /// When this position was reported. Needed to extrapolate: a position from
    /// three seconds ago on a playing track is three seconds out, and syncing to
    /// it makes two devices drift apart rather than together.
    pub at_ms: u64,
}

impl PlaybackPosition {
    /// Where it would be now, given when this was measured.
    pub fn extrapolated(&self, now_ms: u64) -> u64 {
        if !self.playing {
            return self.position_ms;
        }
        (self.position_ms + now_ms.saturating_sub(self.at_ms)).min(self.duration_ms)
    }

    /// How far apart two devices are.
    pub fn drift_ms(&self, other: &PlaybackPosition, now_ms: u64) -> i64 {
        self.extrapolated(now_ms) as i64 - other.extrapolated(now_ms) as i64
    }
}

/// Playing the same thing on more than one device.
pub trait SyncedPlayback {
    fn is_available(&self) -> bool;
    fn play(&mut self, item: &MediaItem, at_ms: u64) -> Result<(), String>;
    fn position(&self, now_ms: u64) -> PlaybackPosition;
    /// Nudges towards a shared position.
    fn resynchronise(&mut self, target: &PlaybackPosition, now_ms: u64) -> i64;
    fn stop(&mut self);
}

/// Synced playback in memory.
#[derive(Debug, Default)]
pub struct InMemorySyncedPlayback {
    current: Option<MediaItem>,
    position: PlaybackPosition,
}

impl InMemorySyncedPlayback {
    /// Below this, leave it alone.
    ///
    /// A correction smaller than about eighty milliseconds is inaudible and a
    /// player that chases every measurement produces a constant stutter -
    /// which is far more noticeable than the drift it is correcting.
    pub const DEADBAND_MS: i64 = 80;

    pub fn new() -> Self {
        Self::default()
    }

    pub fn current(&self) -> Option<&MediaItem> {
        self.current.as_ref()
    }
}

impl SyncedPlayback for InMemorySyncedPlayback {
    fn is_available(&self) -> bool {
        true
    }

    fn play(&mut self, item: &MediaItem, at_ms: u64) -> Result<(), String> {
        if item.source.is_empty() {
            return Err("that item has nothing to play".into());
        }
        self.position = PlaybackPosition {
            position_ms: 0,
            duration_ms: item.duration_ms,
            playing: true,
            at_ms,
        };
        self.current = Some(item.clone());
        Ok(())
    }

    fn position(&self, now_ms: u64) -> PlaybackPosition {
        PlaybackPosition {
            position_ms: self.position.extrapolated(now_ms),
            at_ms: now_ms,
            ..self.position
        }
    }

    fn resynchronise(&mut self, target: &PlaybackPosition, now_ms: u64) -> i64 {
        let drift = target.extrapolated(now_ms) as i64 - self.position.extrapolated(now_ms) as i64;
        if drift.abs() <= Self::DEADBAND_MS {
            return 0;
        }
        self.position.position_ms = target.extrapolated(now_ms);
        self.position.at_ms = now_ms;
        drift
    }

    fn stop(&mut self) {
        self.position.playing = false;
        self.current = None;
    }
}

/// Plays nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSyncedPlayback;

impl SyncedPlayback for NullSyncedPlayback {
    fn is_available(&self) -> bool {
        false
    }
    fn play(&mut self, _item: &MediaItem, _at_ms: u64) -> Result<(), String> {
        Err("this device cannot play media".into())
    }
    fn position(&self, _now_ms: u64) -> PlaybackPosition {
        PlaybackPosition::default()
    }
    fn resynchronise(&mut self, _target: &PlaybackPosition, _now_ms: u64) -> i64 {
        0
    }
    fn stop(&mut self) {}
}

/// Holds nothing to play.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullMediaLibrary;

impl NullMediaLibrary {
    pub fn items(&self) -> Vec<MediaItem> {
        Vec::new()
    }
    pub fn get(&self, _item_id: &str) -> Option<MediaItem> {
        None
    }
    pub fn is_available(&self) -> bool {
        false
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Decks

/// One slide.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Slide {
    pub title: String,
    pub bullets: Vec<String>,
    /// What to say while it is up. Kept apart from the bullets, because a slide
    /// carrying the whole script is a slide nobody reads and a speaker nobody
    /// listens to.
    pub notes: String,
    pub image_path: String,
}

impl Slide {
    /// About the most that fits and stays readable.
    pub const MAX_BULLETS: usize = 6;
    pub const MAX_BULLET_CHARS: usize = 90;

    /// What is wrong with this slide, in words a person can act on.
    pub fn problems(&self) -> Vec<String> {
        let mut out = Vec::new();
        if self.title.trim().is_empty() {
            out.push("this slide has no title".to_string());
        }
        if self.bullets.len() > Self::MAX_BULLETS {
            out.push(format!(
                "{} bullets is more than fits - {} is about the limit",
                self.bullets.len(),
                Self::MAX_BULLETS
            ));
        }
        for bullet in &self.bullets {
            if bullet.chars().count() > Self::MAX_BULLET_CHARS {
                out.push(format!("\"{}...\" is a sentence, not a bullet", &bullet.chars().take(40).collect::<String>()));
            }
        }
        out
    }
}

/// A whole deck.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Deck {
    pub title: String,
    pub subtitle: String,
    pub author: String,
    pub slides: Vec<Slide>,
}

impl Deck {
    /// Roughly how long it takes to present. Two minutes a slide, which is the
    /// rate people actually manage rather than the one they plan for.
    pub fn estimated_minutes(&self) -> usize {
        self.slides.len() * 2
    }

    pub fn problems(&self) -> Vec<(usize, String)> {
        self.slides
            .iter()
            .enumerate()
            .flat_map(|(i, s)| s.problems().into_iter().map(move |p| (i + 1, p)))
            .collect()
    }
}

/// Renders a deck.
pub trait DeckEngine {
    fn is_available(&self) -> bool;
    fn render(&self, deck: &Deck) -> Result<Vec<u8>, String>;
}

/// The default engine.
///
/// Named for the C# class it mirrors, and it writes plain text: the text path is
/// the whole implementation on this port, and it says so rather than pretending
/// to a layout it does not have.
#[derive(Debug, Default, Clone, Copy)]
pub struct PdfSharpDeckEngine;

impl DeckEngine for PdfSharpDeckEngine {
    fn is_available(&self) -> bool {
        true
    }

    fn render(&self, deck: &Deck) -> Result<Vec<u8>, String> {
        if deck.slides.is_empty() {
            return Err("a deck with no slides renders nothing".into());
        }
        let mut out = vec![deck.title.to_uppercase()];
        if !deck.subtitle.is_empty() {
            out.push(deck.subtitle.clone());
        }
        if !deck.author.is_empty() {
            out.push(deck.author.clone());
        }
        for (index, slide) in deck.slides.iter().enumerate() {
            out.push(String::new());
            out.push(format!("--- {} / {} ---", index + 1, deck.slides.len()));
            out.push(slide.title.clone());
            out.extend(slide.bullets.iter().map(|b| format!("  - {b}")));
            if !slide.notes.is_empty() {
                out.push(format!("  (say: {})", slide.notes));
            }
        }
        Ok(out.join("\n").into_bytes())
    }
}

/// A deck to look at when learning what this makes.
///
/// REAL CONTENT, not lorem: a sample full of placeholder text teaches nothing
/// about whether the layout works, because placeholder text is uniformly short.
#[derive(Debug, Default, Clone, Copy)]
pub struct SampleDeck;

impl SampleDeck {
    pub fn build() -> Deck {
        Deck {
            title: "What runs on the device".into(),
            subtitle: "and what does not".into(),
            author: String::new(),
            slides: vec![
                Slide {
                    title: "The phone is the target".into(),
                    bullets: vec![
                        "A desktop is a compile gate".into(),
                        "A phone is the benchmark".into(),
                        "Ran on device is the only level that counts".into(),
                    ],
                    notes: "Everything below ran-on-device is a claim about a compiler.".into(),
                    image_path: String::new(),
                },
                Slide {
                    title: "What leaves the device".into(),
                    bullets: vec![
                        "Nothing, unless somebody said so".into(),
                        "Every provider is off until a key is set".into(),
                        "The answer always says who answered".into(),
                    ],
                    notes: "A fallback is something agreed to, not something that happens.".into(),
                    image_path: String::new(),
                },
                Slide {
                    title: "When there is no network".into(),
                    bullets: vec![
                        "Wi-Fi Direct carries voice at about 50 messages a second".into(),
                        "BLE carries signalling at about 9, one way".into(),
                        "Both were measured on this hardware".into(),
                    ],
                    notes: "BLE cannot carry a call. Building as though it can produces a call that does not work.".into(),
                    image_path: String::new(),
                },
            ],
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Web

/// What a page says about itself.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct PageMetadata {
    pub title: String,
    pub description: String,
    pub canonical_url: String,
    pub language: String,
    /// Whether it may be indexed. Default is NO for anything personal, because
    /// a page that got indexed cannot be un-indexed.
    pub indexable: bool,
}

/// One route this app answers on.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct RouteDescriptor {
    pub path: String,
    pub title: String,
    /// Whether somebody has to be signed in.
    pub requires_auth: bool,
    /// Whether it works with no network. The property that decides what a phone
    /// on a train can still do.
    pub works_offline: bool,
}

impl RouteDescriptor {
    /// Matches a path with `:name` segments.
    ///
    /// Segment counts must match EXACTLY - a prefix match makes `/settings`
    /// answer for `/settings/danger/delete-everything`.
    pub fn matches(&self, path: &str) -> Option<HashMap<String, String>> {
        let split = |p: &str| -> Vec<String> {
            p.split('/').filter(|s| !s.is_empty()).map(String::from).collect()
        };
        let (pattern, actual) = (split(&self.path), split(path));
        if pattern.len() != actual.len() {
            return None;
        }
        let mut parameters = HashMap::new();
        for (p, a) in pattern.iter().zip(actual.iter()) {
            if let Some(name) = p.strip_prefix(':') {
                parameters.insert(name.to_string(), a.clone());
            } else if p != a {
                return None;
            }
        }
        Some(parameters)
    }
}

/// A response held for a while.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CachedResponse {
    pub body: String,
    pub content_type: String,
    pub stored_at_ms: u64,
    pub max_age_ms: u64,
    /// The version tag, so a revalidation can return "unchanged" rather than the
    /// whole body again.
    pub etag: String,
}

impl CachedResponse {
    pub fn is_fresh(&self, now_ms: u64) -> bool {
        self.max_age_ms > 0 && now_ms.saturating_sub(self.stored_at_ms) < self.max_age_ms
    }

    /// Whether a stale copy may still be shown while a new one is fetched.
    ///
    /// Up to a day past expiry, and only for something a person is reading -
    /// yesterday's page beats a blank screen on a phone with no signal. Never
    /// for anything that changes what happens.
    pub fn is_servable_stale(&self, now_ms: u64) -> bool {
        now_ms.saturating_sub(self.stored_at_ms) < self.max_age_ms + 24 * 60 * 60 * 1000
    }
}

/// Where pages and cached responses live.
pub trait WebBoard {
    fn routes(&self) -> Vec<RouteDescriptor>;
    fn resolve(&self, path: &str) -> Option<(RouteDescriptor, HashMap<String, String>)>;
    fn cache_put(&mut self, key: &str, response: CachedResponse);
    fn cache_get(&self, key: &str, now_ms: u64) -> Option<CachedResponse>;
}

/// A board in memory.
#[derive(Debug, Default)]
pub struct InMemoryWebBoard {
    routes: Vec<RouteDescriptor>,
    cache: HashMap<String, CachedResponse>,
    max_entries: usize,
}

impl InMemoryWebBoard {
    pub fn new(routes: Vec<RouteDescriptor>, max_entries: usize) -> Self {
        Self {
            routes,
            cache: HashMap::new(),
            max_entries: if max_entries == 0 { 200 } else { max_entries },
        }
    }

    /// What still works with no network.
    pub fn offline_routes(&self) -> Vec<RouteDescriptor> {
        self.routes.iter().filter(|r| r.works_offline).cloned().collect()
    }
}

impl WebBoard for InMemoryWebBoard {
    fn routes(&self) -> Vec<RouteDescriptor> {
        self.routes.clone()
    }

    /// The MOST SPECIFIC match wins - a literal segment beats a parameter.
    ///
    /// Without this, `/user/:id` answers for `/user/settings` if it was
    /// registered first, and which page a person lands on depends on
    /// registration order.
    fn resolve(&self, path: &str) -> Option<(RouteDescriptor, HashMap<String, String>)> {
        self.routes
            .iter()
            .filter_map(|r| r.matches(path).map(|p| (r.clone(), p)))
            .min_by_key(|(_, parameters)| parameters.len())
    }

    fn cache_put(&mut self, key: &str, response: CachedResponse) {
        self.cache.insert(key.to_string(), response);
        while self.cache.len() > self.max_entries {
            // Evicts the OLDEST, not an arbitrary one. Removing whatever the map
            // iterates first evicts a fresh entry as readily as a stale one.
            let Some(oldest) = self
                .cache
                .iter()
                .min_by_key(|(_, v)| v.stored_at_ms)
                .map(|(k, _)| k.clone())
            else {
                break;
            };
            self.cache.remove(&oldest);
        }
    }

    fn cache_get(&self, key: &str, now_ms: u64) -> Option<CachedResponse> {
        let entry = self.cache.get(key)?;
        entry.is_fresh(now_ms).then(|| entry.clone())
    }
}

/// The companion, over the web.
pub struct WebCompanionService {
    board: InMemoryWebBoard,
    #[allow(clippy::type_complexity)]
    respond: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
}

impl WebCompanionService {
    #[allow(clippy::type_complexity)]
    pub fn new(
        board: InMemoryWebBoard,
        respond: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
    ) -> Self {
        Self { board, respond }
    }

    pub fn board(&self) -> &InMemoryWebBoard {
        &self.board
    }

    /// What a page declares about itself.
    ///
    /// NOT INDEXABLE unless the route works without signing in. A page behind
    /// authentication that says it may be indexed is a page whose title and
    /// description end up in a search engine.
    pub fn metadata_for(&self, route: &RouteDescriptor, language: &str) -> PageMetadata {
        PageMetadata {
            title: route.title.clone(),
            description: String::new(),
            canonical_url: route.path.clone(),
            language: language.to_string(),
            indexable: !route.requires_auth,
        }
    }

    pub fn handle(&self, path: &str, body: &str) -> Result<String, String> {
        let Some((route, _)) = self.board.resolve(path) else {
            return Err(format!("there is no page at {path}"));
        };
        let Some(respond) = &self.respond else {
            return Err(format!(
                "{} is not answered on this device",
                route.title
            ));
        };
        respond(body)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Native runtimes

/// A native runtime for one platform.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct NativeRuntimeBundle {
    pub name: String,
    pub version: String,
    /// The triple this is for. A runtime for the wrong architecture loads and
    /// then fails at the first call, in a way that looks like a corrupt model.
    pub target: String,
    pub url: String,
    pub sha256: String,
    pub bytes: u64,
    pub licence: String,
}

impl NativeRuntimeBundle {
    /// Whether this bundle matches a device.
    ///
    /// Compared on the WHOLE triple. `aarch64-linux-android` and
    /// `aarch64-apple-darwin` share an architecture and share nothing else.
    pub fn matches(&self, target: &str) -> bool {
        self.target.eq_ignore_ascii_case(target)
    }

    pub fn is_installable(&self) -> bool {
        !self.sha256.trim().is_empty() && !self.url.is_empty() && !self.target.is_empty()
    }
}

/// A runtime that has been put in place.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct NativeRuntimeInstall {
    pub bundle: NativeRuntimeBundle,
    pub install_path: String,
    pub installed_at_ms: u64,
    /// Whether the digest was checked AFTER writing, not only during download.
    /// A file can be truncated by a full disk between the two.
    pub verified: bool,
}

/// Fetches native runtimes.
pub trait NativeRuntimeFetcherContract {
    fn is_available(&self) -> bool;
    fn fetch(&self, bundle: &NativeRuntimeBundle) -> Result<NativeRuntimeInstall, String>;
}

/// The default fetcher.
pub struct NativeRuntimeFetcher {
    #[allow(clippy::type_complexity)]
    download: Option<Box<dyn Fn(&str) -> Result<Vec<u8>, String> + Send + Sync>>,
    digest_of: Option<Box<dyn Fn(&[u8]) -> String + Send + Sync>>,
    install_root: String,
}

impl NativeRuntimeFetcher {
    #[allow(clippy::type_complexity)]
    pub fn new(
        download: Option<Box<dyn Fn(&str) -> Result<Vec<u8>, String> + Send + Sync>>,
        digest_of: Option<Box<dyn Fn(&[u8]) -> String + Send + Sync>>,
        install_root: &str,
    ) -> Self {
        Self { download, digest_of, install_root: install_root.to_string() }
    }
}

impl NativeRuntimeFetcherContract for NativeRuntimeFetcher {
    fn is_available(&self) -> bool {
        self.download.is_some() && self.digest_of.is_some()
    }

    /// Verified BEFORE it is installed.
    ///
    /// A native runtime is code that will be loaded into this process. Verifying
    /// after installing means a bad binary sat on disk where something else
    /// could load it first.
    fn fetch(&self, bundle: &NativeRuntimeBundle) -> Result<NativeRuntimeInstall, String> {
        if !bundle.is_installable() {
            return Err(format!(
                "{} has no checksum or no source, so it will not be installed",
                bundle.name
            ));
        }
        let (Some(download), Some(digest_of)) = (&self.download, &self.digest_of) else {
            return Err("this build cannot fetch native runtimes".into());
        };
        let bytes = download(&bundle.url)?;
        if !digest_of(&bytes).eq_ignore_ascii_case(bundle.sha256.trim()) {
            return Err(format!("{} does not match its checksum", bundle.name));
        }
        Ok(NativeRuntimeInstall {
            install_path: format!(
                "{}/{}/{}",
                self.install_root, bundle.target, bundle.name
            ),
            bundle: bundle.clone(),
            installed_at_ms: 0,
            verified: true,
        })
    }
}

/// Which runtimes are known.
#[derive(Debug, Default)]
pub struct NativeRuntimeRegistry {
    bundles: Vec<NativeRuntimeBundle>,
}

impl NativeRuntimeRegistry {
    pub fn new(bundles: Vec<NativeRuntimeBundle>) -> Self {
        Self { bundles }
    }

    pub fn add(&mut self, bundle: NativeRuntimeBundle) -> Result<(), String> {
        if !bundle.is_installable() {
            return Err(format!("{} has no checksum", bundle.name));
        }
        self.bundles.push(bundle);
        Ok(())
    }

    /// What is available for one device.
    pub fn for_target(&self, target: &str) -> Vec<NativeRuntimeBundle> {
        self.bundles.iter().filter(|b| b.matches(target)).cloned().collect()
    }

    /// Anything whose licence is not permissive.
    ///
    /// A native runtime is LINKED, so a copyleft licence here is not a
    /// dependency question - it is a question about what the whole binary must
    /// then be released under.
    pub fn licence_problems(&self) -> Vec<NativeRuntimeBundle> {
        self.bundles
            .iter()
            .filter(|b| {
                !Dependency {
                    licence: b.licence.clone(),
                    ..Default::default()
                }
                .licence_is_allowed()
            })
            .cloned()
            .collect()
    }
}

/// Wires the runtime services.
#[derive(Debug, Default)]
pub struct CircleAIRuntimeServiceCollectionExtensions {
    registered: Vec<String>,
}

impl CircleAIRuntimeServiceCollectionExtensions {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn add(&mut self, name: &str) -> &mut Self {
        self.registered.push(name.to_string());
        self
    }

    pub fn registered(&self) -> &[String] {
        &self.registered
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Image generation

/// Writes one image provider's options and its generator.
// `image_provider` was written once as a macro over the table below and
// expanded here, so each type appears under its own name.


#[doc = "OpenAI images."]
        #[derive(Clone, Default)]
        pub struct OpenAiImageOptions {
            pub key: String,
            pub base_url: String,
            pub model: String,
            pub size: String,
        }

        impl OpenAiImageOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://api.openai.com/v1/images/generations";
            pub const SUGGESTED_MODEL: &'static str = "gpt-image-1";

            pub fn is_configured(&self) -> bool {
                !self.key.is_empty()
            }

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
            }

            pub fn resolved_model(&self) -> &str {
                if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
            }
        }

        impl std::fmt::Debug for OpenAiImageOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(OpenAiImageOptions))
                    .field("key", &if self.key.is_empty() { "<unset>" } else { "<set>" })
                    .field("base_url", &self.resolved_base_url())
                    .field("model", &self.resolved_model())
                    .finish()
            }
        }

        #[doc = concat!("Generates images through ", "openai-image", ".")]
        pub struct OpenAiImageGenerator {
            options: OpenAiImageOptions,
            #[allow(clippy::type_complexity)]
            post: Option<Box<dyn Fn(&str, &str) -> Result<Vec<u8>, String> + Send + Sync>>,
        }

        impl OpenAiImageGenerator {
            pub const PROVIDER: &'static str = "openai-image";

            #[allow(clippy::type_complexity)]
            pub fn new(
                options: OpenAiImageOptions,
                post: Option<Box<dyn Fn(&str, &str) -> Result<Vec<u8>, String> + Send + Sync>>,
            ) -> Self {
                Self { options, post }
            }

            pub fn options(&self) -> &OpenAiImageOptions {
                &self.options
            }

            pub fn is_available(&self) -> bool {
                self.options.is_configured() && self.post.is_some()
            }

            /// Generates one image.
            ///
            /// The prompt LEAVES THE DEVICE, which is the fact worth stating: a
            /// description of a picture somebody wants is a description of what
            /// they were thinking about.
            pub fn generate(&self, prompt: &str) -> Result<Vec<u8>, String> {
                if prompt.trim().is_empty() {
                    return Err("there is nothing to draw".into());
                }
                if !self.options.is_configured() {
                    return Err(format!("{} has no key set on this device", Self::PROVIDER));
                }
                let Some(post) = &self.post else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        Self::PROVIDER
                    ));
                };
                post(self.options.resolved_base_url(), prompt)
            }
        }

#[doc = "Stability. Its models can also be run locally, which is why the base URL \
     matters here more than for a provider that has no such option."]
        #[derive(Clone, Default)]
        pub struct StabilityImageOptions {
            pub key: String,
            pub base_url: String,
            pub model: String,
            pub size: String,
        }

        impl StabilityImageOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://api.stability.ai/v2beta/stable-image/generate";
            pub const SUGGESTED_MODEL: &'static str = "sd3.5-large";

            pub fn is_configured(&self) -> bool {
                !self.key.is_empty()
            }

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
            }

            pub fn resolved_model(&self) -> &str {
                if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
            }
        }

        impl std::fmt::Debug for StabilityImageOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(StabilityImageOptions))
                    .field("key", &if self.key.is_empty() { "<unset>" } else { "<set>" })
                    .field("base_url", &self.resolved_base_url())
                    .field("model", &self.resolved_model())
                    .finish()
            }
        }

        #[doc = concat!("Generates images through ", "stability", ".")]
        pub struct StabilityImageGenerator {
            options: StabilityImageOptions,
            #[allow(clippy::type_complexity)]
            post: Option<Box<dyn Fn(&str, &str) -> Result<Vec<u8>, String> + Send + Sync>>,
        }

        impl StabilityImageGenerator {
            pub const PROVIDER: &'static str = "stability";

            #[allow(clippy::type_complexity)]
            pub fn new(
                options: StabilityImageOptions,
                post: Option<Box<dyn Fn(&str, &str) -> Result<Vec<u8>, String> + Send + Sync>>,
            ) -> Self {
                Self { options, post }
            }

            pub fn options(&self) -> &StabilityImageOptions {
                &self.options
            }

            pub fn is_available(&self) -> bool {
                self.options.is_configured() && self.post.is_some()
            }

            /// Generates one image.
            ///
            /// The prompt LEAVES THE DEVICE, which is the fact worth stating: a
            /// description of a picture somebody wants is a description of what
            /// they were thinking about.
            pub fn generate(&self, prompt: &str) -> Result<Vec<u8>, String> {
                if prompt.trim().is_empty() {
                    return Err("there is nothing to draw".into());
                }
                if !self.options.is_configured() {
                    return Err(format!("{} has no key set on this device", Self::PROVIDER));
                }
                let Some(post) = &self.post else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        Self::PROVIDER
                    ));
                };
                post(self.options.resolved_base_url(), prompt)
            }
        }


/// Wires whichever image providers have keys.
#[derive(Debug, Default)]
pub struct VisionCloudServiceCollectionExtensions {
    configured: Vec<String>,
}

impl VisionCloudServiceCollectionExtensions {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn note(&mut self, provider: &str, configured: bool) -> &mut Self {
        if configured {
            self.configured.push(provider.to_string());
        }
        self
    }

    pub fn describe(&self) -> String {
        if self.configured.is_empty() {
            "no image provider has a key, so nothing is generated off this device".into()
        } else {
            format!("image providers: {}", self.configured.join(", "))
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Documents seen and read

/// What a person did with a document.
pub trait DocumentTracker {
    fn opened(&mut self, document_id: &str, at_ms: u64);
    fn closed(&mut self, document_id: &str, at_ms: u64);
    fn time_spent_ms(&self, document_id: &str) -> u64;
}

/// Tracks nothing.
///
/// The DEFAULT on a personal device. How long somebody spent reading something
/// is a fact about them, and there is no reason it should exist unless they
/// asked for it.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullDocumentTracker;

impl DocumentTracker for NullDocumentTracker {
    fn opened(&mut self, _document_id: &str, _at_ms: u64) {}
    fn closed(&mut self, _document_id: &str, _at_ms: u64) {}
    fn time_spent_ms(&self, _document_id: &str) -> u64 {
        0
    }
}

/// What can be said about a set of documents.
pub trait DocumentInsights {
    fn is_available(&self) -> bool;
    fn most_read(&self, limit: usize) -> Vec<(String, u64)>;
    fn untouched(&self, since_ms: u64) -> Vec<String>;
}

/// Says nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullDocumentInsights;

impl DocumentInsights for NullDocumentInsights {
    fn is_available(&self) -> bool {
        false
    }
    fn most_read(&self, _limit: usize) -> Vec<(String, u64)> {
        Vec::new()
    }
    fn untouched(&self, _since_ms: u64) -> Vec<String> {
        Vec::new()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The long tail

/// What a signature check concluded.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CatalogSignatureResult {
    pub verified: bool,
    /// Which key. Named, so a catalogue signed by an unexpected but valid key is
    /// distinguishable from one signed by the right one.
    pub key_id: String,
    pub signed_at_ms: u64,
    pub detail: String,
}

impl CatalogSignatureResult {
    /// A REFUSAL, used when there is no verifier at all.
    ///
    /// The default must not be `verified: true`, and building it through a named
    /// function rather than `Default` makes an accidental success impossible.
    pub fn unverified(detail: &str) -> Self {
        Self {
            verified: false,
            detail: detail.to_string(),
            ..Default::default()
        }
    }
}

/// How often a catalogue is fetched again.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CatalogRefreshCadence {
    /// Only when somebody asks. THE DEFAULT - a background fetch is a request
    /// that says this device exists, made on a schedule nobody watches.
    #[default]
    Manual,
    Daily,
    Weekly,
    /// Whenever the app starts. Chatty, and named so choosing it is deliberate.
    OnLaunch,
}

impl CatalogRefreshCadence {
    pub fn interval_ms(&self) -> Option<u64> {
        match self {
            Self::Manual | Self::OnLaunch => None,
            Self::Daily => Some(24 * 60 * 60 * 1000),
            Self::Weekly => Some(7 * 24 * 60 * 60 * 1000),
        }
    }
}

/// Where the model catalogue comes from.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ModelScopeCatalogOptions {
    pub base_url: String,
    pub cadence: CatalogRefreshCadence,
    /// Whether an unsigned catalogue is accepted. OFF - a catalogue decides
    /// which binaries this device downloads, so an unsigned one is somebody
    /// else's choice of what to run.
    pub allow_unsigned: bool,
    pub public_key: String,
}

impl ModelScopeCatalogOptions {
    pub fn is_usable(&self) -> bool {
        !self.base_url.is_empty() && (self.allow_unsigned || !self.public_key.is_empty())
    }
}

/// Records what has actually been verified about a piece of code.
///
/// The attribute form of the status type: applied at a declaration so a build
/// can COLLECT the claims and check them against what actually ran. A comment
/// saying the same thing cannot be counted, and so drifts.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CircleAIVerificationStatusAttribute {
    pub level: crate::platform_plugins::VerificationLevel,
    pub device: String,
    pub verified_on: String,
}

impl CircleAIVerificationStatusAttribute {
    /// A claim of having RUN needs a device named. "Ran on device" without
    /// saying which is the claim this type exists to stop.
    pub fn new(
        level: crate::platform_plugins::VerificationLevel,
        device: &str,
        verified_on: &str,
    ) -> Option<Self> {
        if level >= crate::platform_plugins::VerificationLevel::RanOnDevice
            && device.trim().is_empty()
        {
            return None;
        }
        Some(Self {
            level,
            device: device.to_string(),
            verified_on: verified_on.to_string(),
        })
    }
}

/// The companion's own domain context.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct PersonalDomainContext {
    pub focus: String,
    pub facts: HashMap<String, String>,
    pub language: String,
}

impl PersonalDomainContext {
    pub const PURPOSE: &'static str = "your own things - notes, reminders, people, days";
    pub const REFUSES: &'static str = "share anything with anyone without being asked to";
    pub const REFUSAL: &'static str =
        "what is here is yours; it goes nowhere unless you say so";

    pub fn new() -> Self {
        Self::default()
    }

    pub fn clear(&mut self) {
        self.facts.clear();
        self.focus.clear();
    }
}

/// The personal companion.
pub struct PersonalCompanionAdapter {
    context: PersonalDomainContext,
    #[allow(clippy::type_complexity)]
    answer: Option<Box<dyn Fn(&str, &PersonalDomainContext) -> String + Send + Sync>>,
}

impl PersonalCompanionAdapter {
    #[allow(clippy::type_complexity)]
    pub fn new(
        context: PersonalDomainContext,
        answer: Option<Box<dyn Fn(&str, &PersonalDomainContext) -> String + Send + Sync>>,
    ) -> Self {
        Self { context, answer }
    }

    pub fn context(&self) -> &PersonalDomainContext {
        &self.context
    }

    pub fn handle(&self, request: &str) -> String {
        match &self.answer {
            Some(answer) => answer(request, &self.context),
            None => "your things are not set up on this device yet".into(),
        }
    }
}

/// Proof that somebody agreed to something.
///
/// SCOPED, EXPIRING AND SINGLE-USE. A token that can be replayed is a permission
/// that was given once and taken repeatedly, which is the difference between
/// consent and a key.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct UserConsentToken {
    pub token_id: String,
    pub scope: String,
    pub granted_at_ms: u64,
    pub expires_at_ms: u64,
    /// What they were shown. Kept so it can be restated - a consent nobody can
    /// restate is a consent nobody really gave.
    pub prompt_shown: String,
    used: bool,
}

impl UserConsentToken {
    pub fn new(
        token_id: &str,
        scope: &str,
        granted_at_ms: u64,
        lifetime_ms: u64,
        prompt_shown: &str,
    ) -> Option<Self> {
        if token_id.is_empty() || scope.is_empty() || prompt_shown.trim().is_empty() {
            return None;
        }
        if lifetime_ms == 0 {
            return None;
        }
        Some(Self {
            token_id: token_id.to_string(),
            scope: scope.to_string(),
            granted_at_ms,
            expires_at_ms: granted_at_ms + lifetime_ms,
            prompt_shown: prompt_shown.to_string(),
            used: false,
        })
    }

    pub fn is_usable(&self, now_ms: u64, scope: &str) -> bool {
        !self.used
            && self.scope == scope
            && now_ms >= self.granted_at_ms
            && now_ms < self.expires_at_ms
    }

    /// Spends it. Returns false if it was already spent, which is what makes it
    /// single-use rather than merely intended to be.
    pub fn consume(&mut self, now_ms: u64, scope: &str) -> bool {
        if !self.is_usable(now_ms, scope) {
            return false;
        }
        self.used = true;
        true
    }
}

/// How a wake word detector is set up.
#[derive(Debug, Clone, PartialEq)]
pub struct ZipformerWakeConfig {
    pub model_path: String,
    pub tokens_path: String,
    /// The phrase, as words. Written the way somebody says it, not spelled -
    /// the model scores phonemes.
    pub keywords: Vec<String>,
    /// How sure it must be. HIGHER IS FEWER FALSE WAKES and more missed ones,
    /// and the wrong trade in either direction makes the feature unusable: a
    /// device that wakes to the television, or one that ignores its own name.
    pub threshold: f32,
    /// How long to wait before it may fire again. Without this, one utterance
    /// triggers several times as the score crosses back and forth.
    pub refractory_ms: u64,
    pub sample_rate_hz: u32,
}

impl Default for ZipformerWakeConfig {
    fn default() -> Self {
        Self {
            model_path: String::new(),
            tokens_path: String::new(),
            keywords: Vec::new(),
            threshold: 0.25,
            refractory_ms: 1500,
            sample_rate_hz: 16_000,
        }
    }
}

impl ZipformerWakeConfig {
    /// Needs a model, a token table and at least one phrase. A detector missing
    /// any of the three reports ready and never fires.
    pub fn is_complete(&self) -> bool {
        !self.model_path.is_empty()
            && !self.tokens_path.is_empty()
            && !self.keywords.is_empty()
    }
}

/// Listens for a wake word.
pub struct ZipformerWakeWordDetector {
    config: ZipformerWakeConfig,
    #[allow(clippy::type_complexity)]
    score: Option<Box<dyn Fn(&[f32]) -> Vec<(String, f32)> + Send + Sync>>,
    last_fired_ms: u64,
}

impl ZipformerWakeWordDetector {
    #[allow(clippy::type_complexity)]
    pub fn new(
        config: ZipformerWakeConfig,
        score: Option<Box<dyn Fn(&[f32]) -> Vec<(String, f32)> + Send + Sync>>,
    ) -> Self {
        Self { config, score, last_fired_ms: 0 }
    }

    pub fn config(&self) -> &ZipformerWakeConfig {
        &self.config
    }

    pub fn is_available(&self) -> bool {
        self.config.is_complete() && self.score.is_some()
    }

    /// Feeds audio. `Some(phrase)` when it fired.
    ///
    /// The refractory window is checked FIRST and is not reset by a
    /// below-threshold score - otherwise a long utterance keeps pushing the
    /// window out and the detector never fires again.
    pub fn feed(&mut self, samples: &[f32], now_ms: u64) -> Option<String> {
        if !self.is_available() {
            return None;
        }
        if now_ms.saturating_sub(self.last_fired_ms) < self.config.refractory_ms {
            return None;
        }
        let scores = (self.score.as_ref()?)(samples);
        let (phrase, _) = scores
            .into_iter()
            .filter(|(phrase, score)| {
                *score >= self.config.threshold
                    && self.config.keywords.iter().any(|k| k.eq_ignore_ascii_case(phrase))
            })
            .max_by(|a, b| a.1.partial_cmp(&b.1).unwrap_or(std::cmp::Ordering::Equal))?;
        self.last_fired_ms = now_ms;
        Some(phrase)
    }

    /// Clears the window. Called when the microphone is released, so the next
    /// session is not deaf for a second and a half.
    pub fn reset(&mut self) {
        self.last_fired_ms = 0;
    }
}

/// Spots keywords anywhere in speech, not only a wake word.
///
/// A LOWER THRESHOLD than a wake detector on purpose: a missed keyword mid-
/// sentence costs a command, and a false one costs a moment - the opposite trade
/// from waking a device that was not addressed.
pub struct ZipformerKwsSpotter {
    config: ZipformerWakeConfig,
    #[allow(clippy::type_complexity)]
    score: Option<Box<dyn Fn(&[f32]) -> Vec<(String, f32)> + Send + Sync>>,
}

impl ZipformerKwsSpotter {
    #[allow(clippy::type_complexity)]
    pub fn new(
        config: ZipformerWakeConfig,
        score: Option<Box<dyn Fn(&[f32]) -> Vec<(String, f32)> + Send + Sync>>,
    ) -> Self {
        Self { config, score }
    }

    pub fn is_available(&self) -> bool {
        self.config.is_complete() && self.score.is_some()
    }

    /// Everything heard above the threshold, best first.
    pub fn spot(&self, samples: &[f32]) -> Vec<(String, f32)> {
        let Some(score) = &self.score else { return Vec::new() };
        let mut hits: Vec<(String, f32)> = score(samples)
            .into_iter()
            .filter(|(_, s)| *s >= self.config.threshold)
            .collect();
        hits.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));
        hits
    }
}

/// A length in bytes, so a byte count cannot be passed where a duration belongs.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Default)]
pub struct Bytes(pub u64);

impl Bytes {
    pub const KIB: u64 = 1024;
    pub const MIB: u64 = 1024 * 1024;
    pub const GIB: u64 = 1024 * 1024 * 1024;

    /// For a person to read. Binary units, and named as such - a "GB" that is
    /// actually a gibibyte is a seven per cent lie about a download.
    pub fn describe(&self) -> String {
        match self.0 {
            b if b >= Self::GIB => format!("{:.1} GiB", b as f64 / Self::GIB as f64),
            b if b >= Self::MIB => format!("{:.1} MiB", b as f64 / Self::MIB as f64),
            b if b >= Self::KIB => format!("{:.1} KiB", b as f64 / Self::KIB as f64),
            b => format!("{b} bytes"),
        }
    }
}

/// Something went wrong casting.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CastControlException {
    pub action: String,
    pub message: String,
    /// Whether trying again might work. A renderer that has gone away will not
    /// come back on a retry; one that was busy might.
    pub retryable: bool,
}

impl CastControlException {
    pub fn new(action: &str, message: &str, retryable: bool) -> Self {
        Self {
            action: action.to_string(),
            message: message.to_string(),
            retryable,
        }
    }
}

impl std::fmt::Display for CastControlException {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{} failed: {}", self.action, self.message)
    }
}

impl std::error::Error for CastControlException {}

/// A model download was not allowed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelDownloadBlockedException {
    pub model_id: String,
    /// Why, in words for the person. "Blocked" alone sends somebody looking for
    /// a setting they will not find.
    pub reason: String,
    /// What would let it through. A refusal with no way forward is a dead end.
    pub remedy: String,
}

impl ModelDownloadBlockedException {
    pub fn new(model_id: &str, reason: &str, remedy: &str) -> Self {
        Self {
            model_id: model_id.to_string(),
            reason: reason.to_string(),
            remedy: remedy.to_string(),
        }
    }
}

impl std::fmt::Display for ModelDownloadBlockedException {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{} was not downloaded: {}. {}", self.model_id, self.reason, self.remedy)
    }
}

impl std::error::Error for ModelDownloadBlockedException {}

/// Writes a server-sent-event stream.
///
/// THE BLANK LINE IS THE DELIMITER, and a payload containing a newline must be
/// split across several `data:` lines. Writing it as one line produces a stream
/// that truncates at the newline, which shows up as a reply stopping
/// mid-sentence and gets blamed on the model.
#[derive(Debug, Default, Clone, Copy)]
pub struct ServerSentEventsWriter;

impl ServerSentEventsWriter {
    pub const CONTENT_TYPE: &'static str = "text/event-stream";

    pub fn event(payload: &str) -> String {
        payload
            .replace("\r\n", "\n")
            .split('\n')
            .map(|line| format!("data: {line}\n"))
            .collect::<String>()
            + "\n"
    }

    pub fn named(event: &str, payload: &str) -> String {
        format!("event: {event}\n{}", Self::event(payload))
    }

    /// The sentinel every OpenAI-shaped client waits for. A stream that ends
    /// without it leaves the client waiting until it times out.
    pub fn done() -> String {
        "data: [DONE]\n\n".into()
    }

    /// A comment, which keeps a connection alive through a proxy that closes
    /// idle ones. Clients ignore it.
    pub fn keepalive() -> String {
        ": keep-alive\n\n".into()
    }
}

/// Extra instructions folded into the system prompt.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SystemPromptEnrichment {
    /// What the device is, so the model does not offer to do things it cannot.
    pub device_line: String,
    /// The language to answer in.
    pub language_line: String,
    /// What the person has asked it to remember about how to answer.
    pub preferences: Vec<String>,
}

impl SystemPromptEnrichment {
    /// Nothing personal goes in here unless it was put there deliberately.
    ///
    /// A system prompt is sent with EVERY request, so anything folded into it
    /// leaves the device on every turn - which makes it the easiest place to
    /// leak something a person mentioned once.
    pub fn build(&self, base: &str) -> String {
        let mut parts: Vec<String> = vec![base.to_string()];
        for line in [&self.device_line, &self.language_line] {
            if !line.trim().is_empty() {
                parts.push(line.clone());
            }
        }
        parts.extend(self.preferences.iter().cloned());
        parts.retain(|p| !p.trim().is_empty());
        parts.join("\n\n")
    }
}

/// Wires the Neuron services.
#[derive(Debug, Default)]
pub struct NeuronServiceCollectionExtensions {
    registered: Vec<String>,
}

impl NeuronServiceCollectionExtensions {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn add(&mut self, name: &str) -> &mut Self {
        self.registered.push(name.to_string());
        self
    }
    pub fn registered(&self) -> &[String] {
        &self.registered
    }
}

/// Wires the multiplayer services.
#[derive(Debug, Default)]
pub struct MultiplayerServiceCollectionExtensions {
    registered: Vec<String>,
}

impl MultiplayerServiceCollectionExtensions {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn add(&mut self, name: &str) -> &mut Self {
        self.registered.push(name.to_string());
        self
    }
    pub fn registered(&self) -> &[String] {
        &self.registered
    }
}

/// Wires the tool-protocol services.
#[derive(Debug, Default)]
pub struct McpServiceCollectionExtensions {
    registered: Vec<String>,
}

impl McpServiceCollectionExtensions {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn add(&mut self, name: &str) -> &mut Self {
        self.registered.push(name.to_string());
        self
    }
    pub fn registered(&self) -> &[String] {
        &self.registered
    }
}

/// The tool-protocol endpoints.
pub struct McpEndpoints {
    tools: Vec<ToolDefinitionBuilder>,
    #[allow(clippy::type_complexity)]
    invoke: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
    /// Whether a tool that CHANGES something may be invoked over this. Off,
    /// because a tool server reachable over a socket is reachable by whatever
    /// else is on the device.
    allow_acting: bool,
}

impl McpEndpoints {
    pub const LIST_PATH: &'static str = "/mcp/tools";
    pub const CALL_PATH: &'static str = "/mcp/call";

    #[allow(clippy::type_complexity)]
    pub fn new(
        tools: Vec<ToolDefinitionBuilder>,
        invoke: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
        allow_acting: bool,
    ) -> Self {
        Self { tools, invoke, allow_acting }
    }

    pub fn list(&self) -> Result<String, String> {
        let mut generator = ToolManifestGenerator::new();
        for tool in &self.tools {
            generator.add(tool.clone());
        }
        generator.generate()
    }

    pub fn call(&self, name: &str, arguments_json: &str) -> Result<String, String> {
        let Some(tool) = self.tools.iter().find(|t| t.name == name) else {
            return Err(format!("there is no tool called '{name}'"));
        };
        if !tool.is_read_only() && !self.allow_acting {
            return Err(format!(
                "'{name}' changes things, and this server is only allowed to read"
            ));
        }
        let Some(invoke) = &self.invoke else {
            return Err("no tool runner is configured".into());
        };
        invoke(name, arguments_json)
    }
}

/// Where a business keeps its records.
pub trait BusinessStore {
    fn is_available(&self) -> bool;
    fn put(&mut self, collection: &str, id: &str, json: &str) -> Result<(), String>;
    fn get(&self, collection: &str, id: &str) -> Option<String>;
    fn list(&self, collection: &str) -> Vec<String>;
    fn delete(&mut self, collection: &str, id: &str) -> Result<(), String>;
}

/// Where biometric templates live.
///
/// A TEMPLATE NEVER LEAVES THE DEVICE, and there is no method here that returns
/// one. A fingerprint or a face cannot be changed once it has been taken, which
/// is why every operation is a comparison performed here rather than a value
/// handed out.
pub trait BiometricStore {
    fn is_available(&self) -> bool;
    /// Enrols. Takes the template and returns only a handle.
    fn enrol(&mut self, subject: &str, template: &[u8]) -> Result<String, String>;
    /// Compares. Returns a decision, never a score and never the template.
    fn verify(&self, handle: &str, candidate: &[u8]) -> bool;
    fn forget(&mut self, handle: &str) -> bool;
}

/// Identities in memory.
#[derive(Debug, Default)]
pub struct InMemoryIdentityStore {
    identities: HashMap<String, String>,
    display_names: HashMap<String, String>,
}

impl InMemoryIdentityStore {
    pub fn new() -> Self {
        Self::default()
    }

    /// Records a tag against a name.
    ///
    /// The TAG belongs to the device, like an address, and this holds nothing
    /// but a name somebody chose to attach to one. No key is stored here -
    /// an app asks a node, it does not hold a key.
    pub fn add(&mut self, aether_tag: &str, display_name: &str) -> Result<(), String> {
        if aether_tag.trim().is_empty() {
            return Err("an identity needs a tag".into());
        }
        self.identities
            .insert(aether_tag.to_string(), display_name.to_string());
        self.display_names
            .insert(display_name.to_lowercase(), aether_tag.to_string());
        Ok(())
    }

    pub fn name_of(&self, aether_tag: &str) -> Option<String> {
        self.identities.get(aether_tag).cloned()
    }

    pub fn tag_of(&self, display_name: &str) -> Option<String> {
        self.display_names.get(&display_name.to_lowercase()).cloned()
    }

    pub fn all(&self) -> Vec<(String, String)> {
        let mut out: Vec<(String, String)> = self
            .identities
            .iter()
            .map(|(t, n)| (t.clone(), n.clone()))
            .collect();
        out.sort_by(|a, b| a.1.cmp(&b.1));
        out
    }
}

/// Which SQL dialect a store is talking to.
///
/// THEY DIFFER IN THE PARTS THAT MATTER. Parameter markers, identifier quoting
/// and upsert syntax are all different, and a query written for one runs on
/// another right up until it does not.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SqlDialect {
    #[default]
    Sqlite,
    Postgres,
    SqlServer,
}

impl SqlDialect {
    /// The placeholder for the `index`-th parameter, ONE-BASED.
    ///
    /// Postgres numbers from `$1`; SQL Server names them; SQLite takes a bare
    /// `?`. Mixing these produces a query that binds nothing and returns
    /// everything.
    pub fn parameter(&self, index: usize) -> String {
        match self {
            Self::Sqlite => "?".into(),
            Self::Postgres => format!("${index}"),
            Self::SqlServer => format!("@p{index}"),
        }
    }

    /// Quotes an identifier.
    ///
    /// The quote character is DOUBLED to escape it - not backslashed. A
    /// backslash is not an escape in SQL identifiers, and treating it as one is
    /// how a quote in a name becomes an injection.
    pub fn quote(&self, identifier: &str) -> String {
        match self {
            Self::SqlServer => format!("[{}]", identifier.replace(']', "]]")),
            _ => format!("\"{}\"", identifier.replace('"', "\"\"")),
        }
    }

    /// Insert-or-update, which is spelled differently everywhere.
    pub fn upsert(&self, table: &str, columns: &[&str], key: &str) -> String {
        let quoted: Vec<String> = columns.iter().map(|c| self.quote(c)).collect();
        let markers: Vec<String> = (1..=columns.len()).map(|i| self.parameter(i)).collect();
        let assignments: Vec<String> = columns
            .iter()
            .filter(|c| **c != key)
            .map(|c| format!("{} = excluded.{}", self.quote(c), self.quote(c)))
            .collect();
        match self {
            Self::SqlServer => format!(
                "MERGE {} AS t USING (SELECT {} AS {}) AS s ON t.{} = s.{} \
WHEN MATCHED THEN UPDATE SET {} WHEN NOT MATCHED THEN INSERT ({}) VALUES ({});",
                self.quote(table),
                self.parameter(1),
                self.quote(key),
                self.quote(key),
                self.quote(key),
                assignments
                    .iter()
                    .map(|a| a.replace("excluded.", "s."))
                    .collect::<Vec<_>>()
                    .join(", "),
                quoted.join(", "),
                markers.join(", ")
            ),
            _ => format!(
                "INSERT INTO {} ({}) VALUES ({}) ON CONFLICT ({}) DO UPDATE SET {};",
                self.quote(table),
                quoted.join(", "),
                markers.join(", "),
                self.quote(key),
                assignments.join(", ")
            ),
        }
    }
}

/// Atoms in a SQL database.
///
/// EVERY QUERY IS PARAMETERISED. There is no method here that takes a fragment
/// of SQL from a caller, because a store that accepts one is a store that
/// accepts whatever produced it.
pub struct AdoAtomStore {
    dialect: SqlDialect,
    #[allow(clippy::type_complexity)]
    execute: Option<Box<dyn Fn(&str, &[String]) -> Result<Vec<Vec<String>>, String> + Send + Sync>>,
    table: String,
}

impl AdoAtomStore {
    #[allow(clippy::type_complexity)]
    pub fn new(
        dialect: SqlDialect,
        execute: Option<
            Box<dyn Fn(&str, &[String]) -> Result<Vec<Vec<String>>, String> + Send + Sync>,
        >,
        table: &str,
    ) -> Self {
        Self { dialect, execute, table: table.to_string() }
    }

    pub fn dialect(&self) -> SqlDialect {
        self.dialect
    }

    pub fn is_available(&self) -> bool {
        self.execute.is_some()
    }

    /// The table this store needs.
    pub fn schema(&self) -> String {
        format!(
            "CREATE TABLE IF NOT EXISTS {} ({} TEXT PRIMARY KEY, {} TEXT NOT NULL, \
{} INTEGER NOT NULL, {} TEXT NOT NULL);",
            self.dialect.quote(&self.table),
            self.dialect.quote("id"),
            self.dialect.quote("body"),
            self.dialect.quote("at_ms"),
            self.dialect.quote("kind")
        )
    }

    pub fn put(&self, id: &str, body: &str, at_ms: u64, kind: &str) -> Result<(), String> {
        let Some(execute) = &self.execute else {
            return Err("no database is connected".into());
        };
        execute(
            &self.dialect.upsert(&self.table, &["id", "body", "at_ms", "kind"], "id"),
            &[
                id.to_string(),
                body.to_string(),
                at_ms.to_string(),
                kind.to_string(),
            ],
        )
        .map(|_| ())
    }

    pub fn get(&self, id: &str) -> Result<Option<String>, String> {
        let Some(execute) = &self.execute else {
            return Err("no database is connected".into());
        };
        let sql = format!(
            "SELECT {} FROM {} WHERE {} = {};",
            self.dialect.quote("body"),
            self.dialect.quote(&self.table),
            self.dialect.quote("id"),
            self.dialect.parameter(1)
        );
        Ok(execute(&sql, &[id.to_string()])?
            .into_iter()
            .next()
            .and_then(|row| row.into_iter().next()))
    }
}

/// Vectors in the TurboVec index.
///
/// Quantised, so a search over many vectors fits in the memory a phone has. The
/// cost is precision - a quantised index returns approximately the right
/// neighbours, and a caller that needs exact ones has to rescore the candidates
/// against the full vectors.
#[derive(Debug, Default)]
pub struct TurboVecEmbeddingIndex {
    /// Quantised to bytes, one per dimension.
    vectors: Vec<(String, Vec<u8>)>,
    dimensions: usize,
    /// The range each byte covers. Held per index rather than per vector, so a
    /// distance can be computed without unpacking.
    scale: f32,
}

impl TurboVecEmbeddingIndex {
    pub fn new(dimensions: usize) -> Self {
        Self { vectors: Vec::new(), dimensions, scale: 1.0 / 127.0 }
    }

    /// Symmetric, centred on 128, and CLAMPED.
    ///
    /// An embedding component outside -1..1 that wraps becomes its opposite,
    /// which turns a near neighbour into a far one silently.
    pub fn quantise(&self, vector: &[f32]) -> Vec<u8> {
        vector
            .iter()
            .map(|v| ((v.clamp(-1.0, 1.0) * 127.0).round() as i16 + 128) as u8)
            .collect()
    }

    pub fn dequantise(&self, packed: &[u8]) -> Vec<f32> {
        packed
            .iter()
            .map(|b| (*b as i16 - 128) as f32 * self.scale)
            .collect()
    }

    pub fn add(&mut self, id: &str, vector: &[f32]) -> Result<(), String> {
        if vector.len() != self.dimensions {
            return Err(format!(
                "this index holds {}-dimensional vectors, and that one has {}",
                self.dimensions,
                vector.len()
            ));
        }
        self.vectors.push((id.to_string(), self.quantise(vector)));
        Ok(())
    }

    pub fn len(&self) -> usize {
        self.vectors.len()
    }

    pub fn is_empty(&self) -> bool {
        self.vectors.is_empty()
    }

    /// The nearest, nearest first.
    pub fn search(&self, query: &[f32], count: usize) -> Vec<(String, f32)> {
        if query.len() != self.dimensions {
            return Vec::new();
        }
        let mut scored: Vec<(String, f32)> = self
            .vectors
            .iter()
            .map(|(id, packed)| {
                let candidate = self.dequantise(packed);
                (
                    id.clone(),
                    crate::languages_integrations::VectorMath::cosine(query, &candidate),
                )
            })
            .collect();
        scored.sort_by(|a, b| {
            b.1.partial_cmp(&a.1)
                .unwrap_or(std::cmp::Ordering::Equal)
                .then_with(|| a.0.cmp(&b.0))
        });
        scored.truncate(count);
        scored
    }
}

/// Draws a scene.
///
/// Named `SceneRenderer3D` and not `3DSceneRenderer`: a Rust identifier cannot
/// begin with a digit, so the C# name minus its `I` prefix is not spellable
/// here. The rename is recorded in `rust/PARITY-EXCLUSIONS.md`.
pub trait SceneRenderer3D {
    fn is_available(&self) -> bool;
    fn render(&self, scene_json: &str, width: u32, height: u32) -> Result<Vec<u8>, String>;
}

/// Draws nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct Null3DSceneRenderer;

impl SceneRenderer3D for Null3DSceneRenderer {
    fn is_available(&self) -> bool {
        false
    }
    fn render(&self, _scene_json: &str, _width: u32, _height: u32) -> Result<Vec<u8>, String> {
        Err("this device has no 3D renderer".into())
    }
}

/// Watches quietly in the background.
///
/// SPEAKS RARELY. An ambient monitor that comments on everything is one people
/// turn off, and a monitor that has been turned off observes nothing at all.
pub struct AmbientCompanionMonitor {
    #[allow(clippy::type_complexity)]
    observe: Option<Box<dyn Fn(u64) -> Option<String> + Send + Sync>>,
    enabled: bool,
    min_interval_ms: u64,
    last_spoke_ms: u64,
}

impl AmbientCompanionMonitor {
    #[allow(clippy::type_complexity)]
    pub fn new(
        observe: Option<Box<dyn Fn(u64) -> Option<String> + Send + Sync>>,
        min_interval_ms: u64,
    ) -> Self {
        Self {
            observe,
            enabled: false,
            min_interval_ms: if min_interval_ms == 0 { 1_800_000 } else { min_interval_ms },
            last_spoke_ms: 0,
        }
    }

    pub fn enable(&mut self, enabled: bool) {
        self.enabled = enabled;
    }

    pub fn is_enabled(&self) -> bool {
        self.enabled
    }

    pub fn tick(&mut self, now_ms: u64) -> Option<String> {
        if !self.enabled {
            return None;
        }
        if now_ms.saturating_sub(self.last_spoke_ms) < self.min_interval_ms {
            return None;
        }
        let message = (self.observe.as_ref()?)(now_ms)?;
        self.last_spoke_ms = now_ms;
        Some(message)
    }
}

/// Runs proactive work on a schedule.
pub struct ProactiveSchedulerBackgroundService {
    #[allow(clippy::type_complexity)]
    run: Option<Box<dyn Fn(u64) -> Vec<String> + Send + Sync>>,
    interval_ms: u64,
    last_run_ms: u64,
    running: bool,
    /// Whether the device is on mains. WORK WAITS FOR A CHARGER - a proactive
    /// task that runs on battery spends somebody's phone on something they did
    /// not ask for.
    charging: bool,
}

impl ProactiveSchedulerBackgroundService {
    #[allow(clippy::type_complexity)]
    pub fn new(
        run: Option<Box<dyn Fn(u64) -> Vec<String> + Send + Sync>>,
        interval_ms: u64,
    ) -> Self {
        Self {
            run,
            interval_ms: if interval_ms == 0 { 900_000 } else { interval_ms },
            last_run_ms: 0,
            running: false,
            charging: false,
        }
    }

    pub fn start(&mut self) {
        self.running = true;
    }

    pub fn stop(&mut self) {
        self.running = false;
    }

    pub fn set_charging(&mut self, charging: bool) {
        self.charging = charging;
    }

    pub fn tick(&mut self, now_ms: u64) -> Vec<String> {
        if !self.running || !self.charging {
            return Vec::new();
        }
        if now_ms.saturating_sub(self.last_run_ms) < self.interval_ms {
            return Vec::new();
        }
        self.last_run_ms = now_ms;
        self.run.as_ref().map(|r| r(now_ms)).unwrap_or_default()
    }
}

/// Sensor readings into something the companion can use.
pub struct IoTCompanionPipeline {
    #[allow(clippy::type_complexity)]
    summarise: Option<Box<dyn Fn(&[(String, f64)]) -> String + Send + Sync>>,
    /// How much a reading must change before it is worth mentioning. Without
    /// this, a thermometer that wobbles by a tenth of a degree produces a
    /// notification a minute.
    pub change_threshold: f64,
    last: HashMap<String, f64>,
}

impl IoTCompanionPipeline {
    #[allow(clippy::type_complexity)]
    pub fn new(
        summarise: Option<Box<dyn Fn(&[(String, f64)]) -> String + Send + Sync>>,
        change_threshold: f64,
    ) -> Self {
        Self {
            summarise,
            change_threshold: if change_threshold <= 0.0 { 0.5 } else { change_threshold },
            last: HashMap::new(),
        }
    }

    /// Only what actually changed.
    pub fn changed(&mut self, readings: &[(String, f64)]) -> Vec<(String, f64)> {
        readings
            .iter()
            .filter(|(sensor, value)| {
                self.last
                    .get(sensor)
                    .map(|previous| (value - previous).abs() >= self.change_threshold)
                    .unwrap_or(true)
            })
            .map(|(sensor, value)| {
                self.last.insert(sensor.clone(), *value);
                (sensor.clone(), *value)
            })
            .collect()
    }

    pub fn summarise(&mut self, readings: &[(String, f64)]) -> Option<String> {
        let changed = self.changed(readings);
        if changed.is_empty() {
            return None;
        }
        self.summarise.as_ref().map(|s| s(&changed))
    }
}

/// What a wearable knows.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct WearableContext {
    /// Steps today.
    pub steps: u32,
    /// Beats per minute. `None` when not measured - zero is a reading nobody
    /// should ever act on as though it were real.
    pub heart_rate_bpm: Option<u16>,
    pub battery_percent: Option<u8>,
    pub on_wrist: bool,
    pub at_ms: u64,
}

impl WearableContext {
    /// HEALTH DATA STAYS ON THE DEVICE. There is no method here that serialises
    /// this for sending, and that absence is the design: a heart rate is a
    /// medical fact about a person.
    pub fn is_measuring(&self) -> bool {
        self.on_wrist && self.heart_rate_bpm.is_some()
    }

    /// Whether a reading is recent enough to show. Older than five minutes is
    /// history, not a current reading.
    pub fn is_current(&self, now_ms: u64) -> bool {
        now_ms.saturating_sub(self.at_ms) < 300_000
    }
}

/// Writes WAV bytes.
///
/// The same header as `WavIo`, exposed under the name the C# side uses. Both
/// call the one implementation, because two WAV writers is two places for the
/// RIFF size to be wrong.
#[derive(Debug, Default, Clone, Copy)]
pub struct WavWriter;

impl WavWriter {
    pub fn write(
        samples: &[f32],
        sample_rate_hz: u32,
        channels: u16,
    ) -> Vec<u8> {
        crate::voice_loop_telephony::WavIo::write(
            crate::voice_loop_telephony::WavFormat {
                sample_rate_hz,
                channels,
                bits_per_sample: 16,
            },
            samples,
        )
    }

    pub fn header(sample_rate_hz: u32, channels: u16, data_bytes: u32) -> Vec<u8> {
        crate::voice_loop_telephony::WavIo::header(
            crate::voice_loop_telephony::WavFormat {
                sample_rate_hz,
                channels,
                bits_per_sample: 16,
            },
            data_bytes,
        )
    }
}

/// Writes evidence with the sensitive parts removed.
///
/// REDACTION IS NOT DELETION and this does not pretend otherwise: it replaces a
/// value with a marker and a length, so a reader can see that something was
/// there. A field silently dropped reads as a field that never existed.
#[derive(Debug, Default, Clone)]
pub struct RedactedEvidenceJsonConverter {
    /// Field names whose values never appear. Matched case-insensitively,
    /// because a field is spelled three ways across three services.
    redacted_fields: Vec<String>,
}

impl RedactedEvidenceJsonConverter {
    /// The names that are always redacted, whatever else was configured.
    pub const ALWAYS: &'static [&'static str] = &[
        "password", "token", "secret", "api_key", "apikey", "authorization",
        "cookie", "session", "private_key", "credential", "pin", "otp",
    ];

    pub fn new(extra: Vec<String>) -> Self {
        Self { redacted_fields: extra }
    }

    pub fn is_redacted(&self, field: &str) -> bool {
        let field = field.to_lowercase().replace(['-', '_'], "");
        Self::ALWAYS
            .iter()
            .any(|a| field.contains(&a.replace('_', "")))
            || self
                .redacted_fields
                .iter()
                .any(|f| field.contains(&f.to_lowercase().replace(['-', '_'], "")))
    }

    /// The marker that replaces a value.
    ///
    /// Carries the LENGTH, which is enough to tell an empty field from a set one
    /// without revealing anything about the value itself.
    pub fn marker(value: &str) -> String {
        format!("<redacted:{}>", value.chars().count())
    }

    pub fn value_for(&self, field: &str, value: &str) -> String {
        if self.is_redacted(field) {
            Self::marker(value)
        } else {
            value.to_string()
        }
    }
}
