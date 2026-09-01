//! What the companion remembers, and what it lets go of.
//!
//! A MEMORY THAT ONLY GROWS IS NOT A MEMORY, it is a log. The interesting half
//! of this file is forgetting: what fades, what does not, and why.
//!
//! THE ONE THAT READS BACKWARDS AND HAS TO BE STATED. `kind_floor` is the
//! fraction of retrievability a kind KEEPS no matter how long it sits - a FLOOR,
//! not a decay rate. Reading it as a rate and computing `1 - floor` gives a
//! plain fact a floor of 1.0, so it never fades at all, and the store grows
//! forever while reporting that forgetting works. That bug was found by RUNNING
//! the curve during the Python port rather than reading it, and this comment
//! exists so it is not reintroduced by somebody who reads the name and not the
//! formula.
//!
//! AND THE SECOND: a ruling never fades because it was DECIDED. A decision that
//! quietly stops being offered is a decision made twice, and the second time it
//! may go the other way.

use std::collections::HashMap;

// ─────────────────────────────────────────────────────────────────────────────
// What an atom is

/// What kind of thing is being remembered.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum AtomKind {
    /// Something that is true. Fades if it is not used.
    #[default]
    Fact,
    /// Something somebody decided. Never fades below its floor.
    Ruling,
    /// How somebody likes things. Fades slowly.
    Preference,
    /// Who somebody is to somebody else. Holds as hard as a ruling.
    Relationship,
    /// Something that happened, with a time attached.
    Episode,
    /// Something being worked towards.
    Goal,
    /// A correction. Beats what it corrects, whatever the other's strength.
    Correction,
}

/// How a decision turned out.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DecisionOutcome {
    /// Not yet known. The honest default and a real value.
    #[default]
    Unknown,
    Worked,
    DidNot,
    /// Reversed later. Kept, because the reversal is the useful part.
    Reversed,
}

/// The starting stability, in days.
pub const INITIAL_STABILITY_DAYS: f64 = 90.0;

const MS_PER_DAY: f64 = 24.0 * 60.0 * 60.0 * 1000.0;

/// One thing remembered.
#[derive(Debug, Clone, PartialEq)]
pub struct MemoryAtom {
    pub id: String,
    pub kind: AtomKind,
    pub text: String,
    pub created_at_ms: u64,
    pub last_recalled_at_ms: u64,
    /// How long this resists forgetting, in DAYS. Grows each time the atom is
    /// recalled, which is the spacing effect: a thing remembered after a long
    /// gap is remembered longer next time.
    pub stability_days: f64,
    pub recall_count: u32,
    /// Which module or conversation it belongs to. Empty means general.
    pub folder: String,
    /// Where it came from, so a wrong memory can be traced to what said it.
    pub source: String,
    pub outcome: DecisionOutcome,
}

impl MemoryAtom {
    pub fn new(id: &str, text: &str, kind: AtomKind, at_ms: u64) -> Self {
        Self {
            id: id.to_string(),
            kind,
            text: text.to_string(),
            created_at_ms: at_ms,
            last_recalled_at_ms: at_ms,
            stability_days: INITIAL_STABILITY_DAYS,
            recall_count: 0,
            folder: String::new(),
            source: String::new(),
            outcome: DecisionOutcome::Unknown,
        }
    }
}

/// The forgetting curve.
///
/// `retrievability = floor + (1 - floor) * exp(-days / stability)`
///
/// An exponential rather than a step, because memory does not have a cliff -
/// something half-forgotten is still worth offering with less confidence, and a
/// threshold that deletes at a boundary loses it entirely the day before it
/// would have been useful.
pub struct Forgetting;

impl Forgetting {
    /// Below this an atom is not offered. It is not deleted - see `should_drop`.
    pub const OFFER_THRESHOLD: f64 = 0.25;

    /// The fraction each kind KEEPS forever. A FLOOR, NOT A DECAY RATE.
    ///
    /// A ruling keeps 0.40 and so can never fade below it; a relationship the
    /// same; a preference keeps 0.20; everything else keeps nothing and decays
    /// towards zero. Reading these as decay rates inverts the whole table.
    pub fn kind_floor(kind: AtomKind) -> f64 {
        match kind {
            AtomKind::Ruling | AtomKind::Relationship | AtomKind::Correction => 0.40,
            AtomKind::Preference => 0.20,
            _ => 0.0,
        }
    }

    pub fn retrievability(atom: &MemoryAtom, now_ms: u64) -> f64 {
        let elapsed_days =
            (now_ms.saturating_sub(atom.last_recalled_at_ms)) as f64 / MS_PER_DAY;
        let stability = atom.stability_days.max(1.0);
        let base = (-elapsed_days / stability).exp();
        let floor = Self::kind_floor(atom.kind);
        floor + (1.0 - floor) * base
    }

    /// Recalling STRENGTHENS, and by more when the gap was long.
    ///
    /// The spacing effect: something remembered after a long gap is remembered
    /// longer next time, and something re-read immediately is not learned at
    /// all. A flat multiplier would make a hundred rapid recalls as valuable as
    /// one after a month, which is the opposite of true.
    pub fn strengthen(atom: &MemoryAtom, now_ms: u64) -> MemoryAtom {
        let r = Self::retrievability(atom, now_ms);
        // The harder it was to retrieve, the more the recall is worth - so the
        // gain is largest when retrievability was LOW.
        let gain = 1.0 + 2.0 * (1.0 - r);
        MemoryAtom {
            last_recalled_at_ms: now_ms,
            stability_days: (atom.stability_days * gain).min(3650.0),
            recall_count: atom.recall_count + 1,
            ..atom.clone()
        }
    }

    /// Whether an atom may be REMOVED, which is a much higher bar than not
    /// offering it.
    ///
    /// A ruling and a relationship are never dropped whatever their
    /// retrievability says, because their floor guarantees they never reach
    /// zero - and a correction is never dropped because it exists to override
    /// something else that is still there.
    pub fn should_drop(atom: &MemoryAtom, now_ms: u64) -> bool {
        if Self::kind_floor(atom.kind) > 0.0 || atom.kind == AtomKind::Goal {
            return false;
        }
        // A YEAR below the threshold, not a moment. Something that dipped last
        // week and has not been needed since may still be needed tomorrow.
        let elapsed_days = (now_ms.saturating_sub(atom.last_recalled_at_ms)) as f64 / MS_PER_DAY;
        Self::retrievability(atom, now_ms) < 0.05 && elapsed_days > 365.0
    }
}

/// How much a module's memory is kept.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum MemoryRetention {
    /// Nothing is kept past the conversation.
    None,
    /// Kept for a session and then dropped.
    Session,
    /// Kept and allowed to fade. The normal setting.
    #[default]
    Fading,
    /// Kept indefinitely. Only for things somebody explicitly pinned.
    Pinned,
}

/// Where an atom lives.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct MemoryFolder {
    pub name: String,
    pub retention: MemoryRetention,
    /// A cap, so one talkative module cannot fill the store. Zero means no cap.
    pub max_atoms: usize,
}

// ─────────────────────────────────────────────────────────────────────────────
// Getting atoms out of what was said

/// Something that might be worth remembering.
#[derive(Debug, Clone, PartialEq)]
pub struct AtomCandidate {
    pub text: String,
    pub kind: AtomKind,
    /// 0..1. Below the learner's floor it is discarded rather than stored
    /// weakly - a store full of maybes is worse than a small store of facts.
    pub confidence: f32,
    pub source: String,
}

/// Pulls candidates out of text.
pub trait AtomExtractor {
    fn extract(&self, text: &str, source: &str) -> Vec<AtomCandidate>;
}

/// Finds the phrases that signal something worth keeping.
///
/// RULES, NOT A MODEL, and deliberately so: this runs on every turn, and a model
/// call per turn is a battery cost per turn. The rules are conservative - they
/// miss things, and what they find is nearly always right, which is the correct
/// trade for something that writes to a person's memory.
#[derive(Debug, Default, Clone, Copy)]
pub struct CueExtractor;

impl CueExtractor {
    const RULING: &'static [&'static str] = &[
        "we decided", "let's go with", "from now on", "the rule is", "always",
        "never", "i've decided", "we agreed",
    ];
    const PREFERENCE: &'static [&'static str] = &[
        "i prefer", "i like", "i don't like", "i hate", "i'd rather",
        "please always", "please don't",
    ];
    const CORRECTION: &'static [&'static str] = &[
        "actually", "no, ", "that's wrong", "i meant", "correction", "not quite",
    ];
    const RELATIONSHIP: &'static [&'static str] = &[
        "my wife", "my husband", "my mother", "my father", "my son",
        "my daughter", "my brother", "my sister", "my boss", "my partner",
        "my friend",
    ];
}

impl AtomExtractor for CueExtractor {
    fn extract(&self, text: &str, source: &str) -> Vec<AtomCandidate> {
        let mut out = Vec::new();
        for sentence in text.split_inclusive(['.', '!', '?']) {
            let trimmed = sentence.trim();
            if trimmed.is_empty() {
                continue;
            }
            let lower = trimmed.to_lowercase();
            let candidate = |kind: AtomKind, confidence: f32| AtomCandidate {
                text: trimmed.to_string(),
                kind,
                confidence,
                source: source.to_string(),
            };
            // Order matters: a correction that also contains a preference cue is
            // a CORRECTION, because it is overriding something already stored.
            if Self::CORRECTION.iter().any(|c| lower.starts_with(c) || lower.contains(&format!(" {c}"))) {
                out.push(candidate(AtomKind::Correction, 0.75));
            } else if Self::RULING.iter().any(|c| lower.contains(c)) {
                out.push(candidate(AtomKind::Ruling, 0.8));
            } else if Self::PREFERENCE.iter().any(|c| lower.contains(c)) {
                out.push(candidate(AtomKind::Preference, 0.7));
            } else if Self::RELATIONSHIP.iter().any(|c| lower.contains(c)) {
                out.push(candidate(AtomKind::Relationship, 0.7));
            }
        }
        out
    }
}

/// What a learning pass did.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct LearnReport {
    pub considered: usize,
    pub stored: usize,
    pub merged: usize,
    pub discarded: usize,
    /// Why things were discarded, so a quiet learner can be diagnosed rather
    /// than assumed broken.
    pub reasons: Vec<String>,
}

/// Where atoms are kept.
pub trait AtomStore {
    fn put(&mut self, atom: MemoryAtom);
    fn get(&self, id: &str) -> Option<&MemoryAtom>;
    fn all(&self) -> Vec<MemoryAtom>;
    fn remove(&mut self, id: &str) -> bool;
}

/// Atoms in memory.
#[derive(Debug, Default)]
pub struct InMemoryAtomStore {
    atoms: HashMap<String, MemoryAtom>,
}

impl InMemoryAtomStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl AtomStore for InMemoryAtomStore {
    fn put(&mut self, atom: MemoryAtom) {
        self.atoms.insert(atom.id.clone(), atom);
    }
    fn get(&self, id: &str) -> Option<&MemoryAtom> {
        self.atoms.get(id)
    }
    fn all(&self) -> Vec<MemoryAtom> {
        self.atoms.values().cloned().collect()
    }
    fn remove(&mut self, id: &str) -> bool {
        self.atoms.remove(id).is_some()
    }
}

/// Turns candidates into stored atoms.
///
/// IT MERGES RATHER THAN DUPLICATING. Somebody who says the same thing twice
/// should end up with one stronger memory, not two identical ones that both
/// surface in every recall.
pub struct AtomLearner {
    counter: u64,
}

impl Default for AtomLearner {
    fn default() -> Self {
        Self::new()
    }
}

impl AtomLearner {
    /// Below this a candidate is discarded. A store full of maybes is worse than
    /// a small store of facts.
    pub const CONFIDENCE_FLOOR: f32 = 0.6;

    pub fn new() -> Self {
        Self { counter: 0 }
    }

    /// Normalised for comparison: case, punctuation and spacing dropped.
    pub fn key(text: &str) -> String {
        text.to_lowercase()
            .chars()
            .map(|c| if c.is_alphanumeric() || c.is_whitespace() { c } else { ' ' })
            .collect::<String>()
            .split_whitespace()
            .collect::<Vec<_>>()
            .join(" ")
    }

    pub fn learn<S: AtomStore>(
        &mut self,
        store: &mut S,
        candidates: &[AtomCandidate],
        folder: &str,
        now_ms: u64,
    ) -> LearnReport {
        let mut report = LearnReport { considered: candidates.len(), ..Default::default() };
        let mut existing: HashMap<String, MemoryAtom> = store
            .all()
            .into_iter()
            .map(|a| (Self::key(&a.text), a))
            .collect();

        for candidate in candidates {
            if candidate.confidence < Self::CONFIDENCE_FLOOR {
                report.discarded += 1;
                report.reasons.push(format!(
                    "\"{}\" was not certain enough",
                    candidate.text.chars().take(40).collect::<String>()
                ));
                continue;
            }
            let key = Self::key(&candidate.text);
            if let Some(found) = existing.get(&key) {
                // Merging STRENGTHENS rather than replacing, so a repeated
                // statement becomes a firmer memory rather than a fresh weak
                // one.
                store.put(Forgetting::strengthen(found, now_ms));
                report.merged += 1;
                continue;
            }
            self.counter += 1;
            let mut atom = MemoryAtom::new(
                &format!("a-{:06}", self.counter),
                &candidate.text,
                candidate.kind,
                now_ms,
            );
            atom.folder = folder.to_string();
            atom.source = candidate.source.clone();
            existing.insert(key, atom.clone());
            store.put(atom);
            report.stored += 1;
        }
        report
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Getting atoms back

/// What is going on, for recall to work with.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Situation {
    pub text: String,
    pub folder: String,
    pub at_ms: u64,
    /// Who is present, if known. Recall is narrowed to what is appropriate in
    /// front of them - a household device is used in front of other people.
    pub present_speaker_ids: Vec<String>,
}

/// How much recall may spend.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RecallBudget {
    /// At most this many atoms reach a prompt. Small on purpose: more context is
    /// not more relevance, and every extra atom competes for attention with what
    /// was actually asked.
    pub max_atoms: usize,
    pub max_characters: usize,
    /// Below this retrievability an atom is not offered at all.
    pub floor: f64,
}

impl Default for RecallBudget {
    fn default() -> Self {
        Self { max_atoms: 5, max_characters: 800, floor: Forgetting::OFFER_THRESHOLD }
    }
}

/// What recall found.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct RecallResult {
    pub atoms: Vec<MemoryAtom>,
    /// Ready to put in a prompt. EMPTY when nothing cleared the floor - a
    /// heading with nothing under it tells a model there is nothing to remember
    /// about this person, which is worse than saying nothing.
    pub prompt_text: String,
    pub considered_count: usize,
}

/// Chooses what to bring back.
///
/// RANKED ON BOTH strength and relevance. Strength alone surfaces the same
/// favourite fact forever; relevance alone surfaces whatever happens to share a
/// word with the question.
pub struct Recall;

impl Recall {
    fn terms(text: &str) -> Vec<String> {
        text.to_lowercase()
            .split(|c: char| !c.is_alphanumeric())
            .filter(|w| w.len() > 2)
            .map(str::to_string)
            .collect()
    }

    pub fn recall<S: AtomStore>(
        store: &mut S,
        situation: &Situation,
        budget: RecallBudget,
    ) -> RecallResult {
        let now_ms = situation.at_ms;
        let wanted = Self::terms(&situation.text);
        let all = store.all();

        let mut scored: Vec<(MemoryAtom, f64)> = all
            .iter()
            .filter(|a| {
                situation.folder.is_empty() || a.folder.is_empty() || a.folder == situation.folder
            })
            .map(|atom| {
                let strength = Forgetting::retrievability(atom, now_ms);
                let words: Vec<String> = Self::terms(&atom.text);
                let shared = wanted.iter().filter(|t| words.contains(t)).count();
                let relevance = if wanted.is_empty() {
                    0.0
                } else {
                    shared as f64 / wanted.len() as f64
                };
                // A CORRECTION outranks everything of the same relevance,
                // because it exists to override something still in the store.
                let bonus = if atom.kind == AtomKind::Correction { 0.15 } else { 0.0 };
                (atom.clone(), strength * 0.4 + relevance * 0.6 + bonus)
            })
            .filter(|(atom, _)| Forgetting::retrievability(atom, now_ms) >= budget.floor)
            .collect();
        scored.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));

        let mut chosen = Vec::new();
        let mut characters = 0usize;
        for (atom, _) in scored {
            if chosen.len() >= budget.max_atoms {
                break;
            }
            if characters + atom.text.len() > budget.max_characters {
                continue;
            }
            characters += atom.text.len();
            chosen.push(atom);
        }

        // The chosen atoms are STRENGTHENED, because being recalled is what
        // recall means - and without this the spacing effect never fires and
        // everything decays at the same rate whether it is used or not.
        for atom in &chosen {
            store.put(Forgetting::strengthen(atom, now_ms));
        }

        RecallResult {
            prompt_text: if chosen.is_empty() {
                String::new()
            } else {
                format!(
                    "worth remembering:\n{}",
                    chosen
                        .iter()
                        .map(|a| format!("- {}", a.text))
                        .collect::<Vec<_>>()
                        .join("\n")
                )
            },
            atoms: chosen,
            considered_count: all.len(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Wear

/// How often something has been reached for.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct MemoryTrace {
    pub atom_id: String,
    pub recall_count: u32,
    pub last_recalled_at_ms: u64,
    /// How often it was recalled and then NOT used in the answer. High wear with
    /// low use means recall keeps offering something unhelpful.
    pub offered_not_used: u32,
}

/// Watches which memories earn their place.
///
/// THE USEFUL SIGNAL IS OFFERED-BUT-NOT-USED. An atom that surfaces in every
/// recall and never influences an answer is noise that is crowding out something
/// better, and nothing else in the system can see that.
#[derive(Debug, Default)]
pub struct MemoryWear {
    traces: HashMap<String, MemoryTrace>,
}

impl MemoryWear {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn offered(&mut self, atom_id: &str, now_ms: u64) {
        let trace = self.traces.entry(atom_id.to_string()).or_insert_with(|| MemoryTrace {
            atom_id: atom_id.to_string(),
            ..Default::default()
        });
        trace.recall_count += 1;
        trace.offered_not_used += 1;
        trace.last_recalled_at_ms = now_ms;
    }

    /// Called when an atom actually influenced an answer.
    pub fn used(&mut self, atom_id: &str) {
        if let Some(trace) = self.traces.get_mut(atom_id) {
            trace.offered_not_used = trace.offered_not_used.saturating_sub(1);
        }
    }

    pub fn trace(&self, atom_id: &str) -> Option<&MemoryTrace> {
        self.traces.get(atom_id)
    }

    /// Atoms that keep being offered and never help. Enough samples to mean
    /// something, so one unlucky turn does not condemn a memory.
    pub fn noisy(&self, min_offers: u32) -> Vec<String> {
        self.traces
            .values()
            .filter(|t| t.recall_count >= min_offers && t.offered_not_used >= min_offers)
            .map(|t| t.atom_id.clone())
            .collect()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The log and the stores

/// One row as it is written.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct AtomRecord {
    pub id: String,
    pub kind: String,
    pub text: String,
    pub created_at: String,
    pub last_recalled_at: String,
    pub stability_days: f64,
    pub recall_count: u32,
    pub folder: String,
    pub source: String,
    pub outcome: String,
}

/// A running record of what was learned and forgotten.
///
/// APPEND-ONLY, and separate from the store. When somebody asks why the
/// companion thinks something, the store says what it thinks and only the log
/// says how it came to.
#[derive(Debug, Default)]
pub struct AtomLog {
    entries: Vec<(u64, String, String, String)>,
}

impl AtomLog {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn record(&mut self, at_ms: u64, action: &str, atom_id: &str, note: &str) {
        self.entries.push((
            at_ms,
            action.to_string(),
            atom_id.to_string(),
            note.to_string(),
        ));
    }

    /// Everything about one atom, oldest first. The answer to "why do you think
    /// that?"
    pub fn history_of(&self, atom_id: &str) -> Vec<(u64, String, String)> {
        self.entries
            .iter()
            .filter(|(_, _, id, _)| id == atom_id)
            .map(|(at, action, _, note)| (*at, action.clone(), note.clone()))
            .collect()
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }
}

/// Atoms in SQLite.
///
/// PARAMETERISED, ALWAYS. Every value here came from something somebody said,
/// and a store built by concatenating strings into SQL can be rewritten by
/// saying the right sentence out loud.
pub struct SqliteAtomStore {
    execute: Option<Box<dyn Fn(&str, &[String]) -> Vec<Vec<String>> + Send + Sync>>,
    cache: HashMap<String, MemoryAtom>,
}

impl SqliteAtomStore {
    pub const SCHEMA: &'static [&'static str] = &[
        "CREATE TABLE IF NOT EXISTS atoms (\
         id TEXT PRIMARY KEY, kind TEXT NOT NULL, text TEXT NOT NULL, \
         created_at INTEGER NOT NULL, last_recalled_at INTEGER NOT NULL, \
         stability_days REAL NOT NULL, recall_count INTEGER NOT NULL, \
         folder TEXT NOT NULL DEFAULT '', source TEXT NOT NULL DEFAULT '', \
         outcome TEXT NOT NULL DEFAULT 'unknown')",
        // Indexed on folder AND last_recalled_at together: recall filters by
        // folder and orders by recency, and two separate indexes let the planner
        // use only one of them.
        "CREATE INDEX IF NOT EXISTS atoms_folder_recency ON atoms (folder, last_recalled_at DESC)",
    ];

    pub fn new(
        execute: Option<Box<dyn Fn(&str, &[String]) -> Vec<Vec<String>> + Send + Sync>>,
    ) -> Self {
        Self { execute, cache: HashMap::new() }
    }

    pub fn initialise(&self) -> bool {
        let Some(execute) = &self.execute else { return false };
        for statement in Self::SCHEMA {
            execute(statement, &[]);
        }
        true
    }
}

impl AtomStore for SqliteAtomStore {
    fn put(&mut self, atom: MemoryAtom) {
        if let Some(execute) = &self.execute {
            execute(
                "INSERT OR REPLACE INTO atoms \
                 (id, kind, text, created_at, last_recalled_at, stability_days, \
                  recall_count, folder, source, outcome) \
                 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                &[
                    atom.id.clone(),
                    format!("{:?}", atom.kind),
                    atom.text.clone(),
                    atom.created_at_ms.to_string(),
                    atom.last_recalled_at_ms.to_string(),
                    atom.stability_days.to_string(),
                    atom.recall_count.to_string(),
                    atom.folder.clone(),
                    atom.source.clone(),
                    format!("{:?}", atom.outcome),
                ],
            );
        }
        self.cache.insert(atom.id.clone(), atom);
    }

    fn get(&self, id: &str) -> Option<&MemoryAtom> {
        self.cache.get(id)
    }

    fn all(&self) -> Vec<MemoryAtom> {
        self.cache.values().cloned().collect()
    }

    fn remove(&mut self, id: &str) -> bool {
        if let Some(execute) = &self.execute {
            execute("DELETE FROM atoms WHERE id = ?", &[id.to_string()]);
        }
        self.cache.remove(id).is_some()
    }
}

/// Episodes in SQLite.
pub struct SqliteEpisodicStore {
    execute: Option<Box<dyn Fn(&str, &[String]) -> Vec<Vec<String>> + Send + Sync>>,
}

impl SqliteEpisodicStore {
    pub const SCHEMA: &'static [&'static str] = &[
        "CREATE TABLE IF NOT EXISTS episodes (\
         id TEXT PRIMARY KEY, text TEXT NOT NULL, at INTEGER NOT NULL, \
         folder TEXT NOT NULL DEFAULT '')",
        "CREATE INDEX IF NOT EXISTS episodes_at ON episodes (at DESC)",
    ];

    pub fn new(
        execute: Option<Box<dyn Fn(&str, &[String]) -> Vec<Vec<String>> + Send + Sync>>,
    ) -> Self {
        Self { execute }
    }

    pub fn initialise(&self) -> bool {
        let Some(execute) = &self.execute else { return false };
        for statement in Self::SCHEMA {
            execute(statement, &[]);
        }
        true
    }

    pub fn add(&self, id: &str, text: &str, at_ms: u64, folder: &str) -> bool {
        let Some(execute) = &self.execute else { return false };
        if id.is_empty() {
            return false;
        }
        execute(
            "INSERT OR REPLACE INTO episodes (id, text, at, folder) VALUES (?, ?, ?, ?)",
            &[id.into(), text.into(), at_ms.to_string(), folder.into()],
        );
        true
    }

    /// BETWEEN two times, because "what happened on Tuesday" is the question
    /// people actually ask - not "the last twenty things".
    pub fn between(&self, from_ms: u64, to_ms: u64) -> Vec<Vec<String>> {
        match &self.execute {
            Some(execute) => execute(
                "SELECT id, text, at FROM episodes WHERE at >= ? AND at <= ? ORDER BY at",
                &[from_ms.to_string(), to_ms.to_string()],
            ),
            None => Vec::new(),
        }
    }
}

/// Goals in SQLite.
pub struct SqliteGoalStore {
    execute: Option<Box<dyn Fn(&str, &[String]) -> Vec<Vec<String>> + Send + Sync>>,
}

impl SqliteGoalStore {
    pub const SCHEMA: &'static [&'static str] = &[
        "CREATE TABLE IF NOT EXISTS goals (\
         id TEXT PRIMARY KEY, text TEXT NOT NULL, due_at INTEGER, \
         progress REAL NOT NULL DEFAULT 0, is_done INTEGER NOT NULL DEFAULT 0)",
    ];

    pub fn new(
        execute: Option<Box<dyn Fn(&str, &[String]) -> Vec<Vec<String>> + Send + Sync>>,
    ) -> Self {
        Self { execute }
    }

    pub fn initialise(&self) -> bool {
        let Some(execute) = &self.execute else { return false };
        for statement in Self::SCHEMA {
            execute(statement, &[]);
        }
        true
    }

    /// A goal with no deadline stores NULL, not 0.
    ///
    /// Zero is a real time - the epoch - and a goal with no deadline stored as 0
    /// shows as fifty-six years overdue.
    pub fn put(&self, id: &str, text: &str, due_at_ms: Option<u64>, progress: f32, is_done: bool) -> bool {
        let Some(execute) = &self.execute else { return false };
        if id.is_empty() {
            return false;
        }
        execute(
            "INSERT OR REPLACE INTO goals (id, text, due_at, progress, is_done) VALUES (?, ?, ?, ?, ?)",
            &[
                id.into(),
                text.into(),
                due_at_ms.map(|d| d.to_string()).unwrap_or_default(),
                progress.to_string(),
                u8::from(is_done).to_string(),
            ],
        );
        true
    }

    /// `due_at IS NULL` sorts LAST rather than first, which is what an undated
    /// goal deserves - it is not urgent, it is undated.
    pub fn open(&self) -> Vec<Vec<String>> {
        match &self.execute {
            Some(execute) => execute(
                "SELECT id, text, due_at, progress FROM goals WHERE is_done = 0 \
                 ORDER BY due_at IS NULL, due_at",
                &[],
            ),
            None => Vec::new(),
        }
    }
}

/// Affect readings, as JSON on disk.
pub struct JsonAffectStore {
    read: Option<Box<dyn Fn() -> Option<String> + Send + Sync>>,
    write: Option<Box<dyn Fn(&str) + Send + Sync>>,
    /// Older than this is dropped on save. Affect is a reading about a MOMENT
    /// and a year-old one says nothing about now - keeping it is a record of
    /// somebody's moods for no purpose.
    keep_days: u64,
    readings: Vec<(String, f32, u64)>,
}

impl JsonAffectStore {
    pub fn new(
        read: Option<Box<dyn Fn() -> Option<String> + Send + Sync>>,
        write: Option<Box<dyn Fn(&str) + Send + Sync>>,
        keep_days: u64,
    ) -> Self {
        Self { read, write, keep_days, readings: Vec::new() }
    }

    pub fn add(&mut self, label: &str, confidence: f32, at_ms: u64) {
        self.readings.push((label.to_string(), confidence, at_ms));
    }

    pub fn recent(&self, since_ms: u64) -> Vec<(String, f32, u64)> {
        self.readings
            .iter()
            .filter(|(_, _, at)| *at >= since_ms)
            .cloned()
            .collect()
    }

    pub fn save(&mut self, now_ms: u64) -> bool {
        let Some(write) = &self.write else { return false };
        let cutoff = now_ms.saturating_sub(self.keep_days * 24 * 60 * 60 * 1000);
        self.readings.retain(|(_, _, at)| *at >= cutoff);
        let body: Vec<String> = self
            .readings
            .iter()
            .map(|(l, c, at)| format!("{{\"label\":\"{l}\",\"confidence\":{c},\"at\":{at}}}"))
            .collect();
        write(&format!("[{}]", body.join(",")));
        true
    }
}

/// How the companion presents itself, as JSON on disk.
pub struct JsonPersonaStore {
    read: Option<Box<dyn Fn() -> Option<String> + Send + Sync>>,
    write: Option<Box<dyn Fn(&str) + Send + Sync>>,
    persona: HashMap<String, String>,
}

impl JsonPersonaStore {
    pub fn new(
        read: Option<Box<dyn Fn() -> Option<String> + Send + Sync>>,
        write: Option<Box<dyn Fn(&str) + Send + Sync>>,
    ) -> Self {
        Self { read, write, persona: HashMap::new() }
    }

    pub fn set(&mut self, key: &str, value: &str) {
        self.persona.insert(key.to_string(), value.to_string());
    }

    pub fn get(&self, key: &str) -> Option<&String> {
        self.persona.get(key)
    }

    pub fn save(&self) -> bool {
        let Some(write) = &self.write else { return false };
        let body: Vec<String> = self
            .persona
            .iter()
            .map(|(k, v)| format!("\"{k}\":\"{}\"", v.replace('"', "'")))
            .collect();
        write(&format!("{{{}}}", body.join(",")));
        true
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// A module's own memory

/// Memory scoped to one module.
pub trait ModuleMemoryTrait {
    fn folder(&self) -> &MemoryFolder;
    fn remember(&mut self, text: &str, kind: AtomKind, now_ms: u64);
    fn recall(&mut self, about: &str, budget: RecallBudget, now_ms: u64) -> RecallResult;
    fn forget(&mut self, id: &str) -> bool;
}

/// One module's slice of memory.
///
/// SCOPED, so the health module cannot read the business module's memories by
/// accident. A single shared pool is simpler and it is how a question about an
/// invoice ends up with somebody's medication in the prompt.
pub struct ModuleMemory<S: AtomStore> {
    folder: MemoryFolder,
    store: S,
    counter: u64,
}

impl<S: AtomStore> ModuleMemory<S> {
    pub fn new(folder: MemoryFolder, store: S) -> Self {
        Self { folder, store, counter: 0 }
    }

    pub fn store(&self) -> &S {
        &self.store
    }
}

impl<S: AtomStore> ModuleMemoryTrait for ModuleMemory<S> {
    fn folder(&self) -> &MemoryFolder {
        &self.folder
    }

    fn remember(&mut self, text: &str, kind: AtomKind, now_ms: u64) {
        if self.folder.retention == MemoryRetention::None {
            return;
        }
        if self.folder.max_atoms > 0 {
            let mine: Vec<MemoryAtom> = self
                .store
                .all()
                .into_iter()
                .filter(|a| a.folder == self.folder.name)
                .collect();
            if mine.len() >= self.folder.max_atoms {
                // The WEAKEST goes, not the oldest. An old memory that is still
                // recalled weekly is worth more than a recent one nobody has
                // needed.
                if let Some(weakest) = mine.iter().min_by(|a, b| {
                    Forgetting::retrievability(a, now_ms)
                        .partial_cmp(&Forgetting::retrievability(b, now_ms))
                        .unwrap_or(std::cmp::Ordering::Equal)
                }) {
                    self.store.remove(&weakest.id);
                }
            }
        }
        self.counter += 1;
        let mut atom = MemoryAtom::new(
            &format!("{}-{:06}", self.folder.name, self.counter),
            text,
            kind,
            now_ms,
        );
        atom.folder = self.folder.name.clone();
        self.store.put(atom);
    }

    fn recall(&mut self, about: &str, budget: RecallBudget, now_ms: u64) -> RecallResult {
        Recall::recall(
            &mut self.store,
            &Situation {
                text: about.to_string(),
                folder: self.folder.name.clone(),
                at_ms: now_ms,
                present_speaker_ids: Vec::new(),
            },
            budget,
        )
    }

    /// A module can only forget its OWN memories. Otherwise "forget that" in one
    /// context deletes something from another.
    fn forget(&mut self, id: &str) -> bool {
        match self.store.get(id) {
            Some(atom) if atom.folder == self.folder.name => self.store.remove(id),
            _ => false,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The service

/// What was carried to a hook.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct HookPayload {
    pub event: String,
    pub text: String,
    pub folder: String,
    pub at_ms: u64,
}

/// The memory the companion uses.
pub trait MemoryServiceTrait {
    fn observe(&mut self, text: &str, folder: &str, source: &str, now_ms: u64) -> LearnReport;
    fn recall(&mut self, situation: &Situation, budget: RecallBudget) -> RecallResult;
    fn sweep(&mut self, now_ms: u64) -> usize;
    fn atom_count(&self) -> usize;
}

/// Remembering, recalling, and letting go.
pub struct MemoryService<S: AtomStore, E: AtomExtractor> {
    store: S,
    extractor: E,
    learner: AtomLearner,
    pub log: AtomLog,
}

impl<S: AtomStore, E: AtomExtractor> MemoryService<S, E> {
    pub fn new(store: S, extractor: E) -> Self {
        Self { store, extractor, learner: AtomLearner::new(), log: AtomLog::new() }
    }
}

impl<S: AtomStore, E: AtomExtractor> MemoryServiceTrait for MemoryService<S, E> {
    fn observe(&mut self, text: &str, folder: &str, source: &str, now_ms: u64) -> LearnReport {
        let candidates = self.extractor.extract(text, source);
        let report = self
            .learner
            .learn(&mut self.store, &candidates, folder, now_ms);
        self.log.record(
            now_ms,
            "observe",
            "",
            &format!("{} stored, {} merged", report.stored, report.merged),
        );
        report
    }

    fn recall(&mut self, situation: &Situation, budget: RecallBudget) -> RecallResult {
        Recall::recall(&mut self.store, situation, budget)
    }

    /// Removes what has genuinely gone, and returns how many.
    ///
    /// SEPARATE FROM RECALL and never automatic on a read path: a sweep that ran
    /// during a recall would delete things in the middle of answering a
    /// question, and a slow sweep would make every answer slow.
    fn sweep(&mut self, now_ms: u64) -> usize {
        let doomed: Vec<MemoryAtom> = self
            .store
            .all()
            .into_iter()
            .filter(|a| Forgetting::should_drop(a, now_ms))
            .collect();
        for atom in &doomed {
            self.store.remove(&atom.id);
            self.log.record(now_ms, "forget", &atom.id, "faded past recovery");
        }
        doomed.len()
    }

    fn atom_count(&self) -> usize {
        self.store.all().len()
    }
}

/// What a sync run did.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SyncReport {
    pub sent: usize,
    pub received: usize,
    pub conflicts: usize,
    /// Resolved by keeping BOTH, never by picking one silently.
    pub kept_both: usize,
}

/// Memory between a person's own devices.
///
/// BETWEEN THEIR DEVICES ONLY, over the mesh, never through a server. A memory
/// store that syncs through somebody else's computer is a copy of somebody's
/// life on somebody else's computer, whatever the encryption story is.
///
/// A CONFLICT KEEPS BOTH. Two devices that learned different things about the
/// same subject have both learned something, and picking one by timestamp
/// discards a real memory because a clock was wrong.
pub struct MemorySync {
    send: Option<Box<dyn Fn(&[MemoryAtom]) -> Vec<MemoryAtom> + Send + Sync>>,
}

impl MemorySync {
    pub fn new(send: Option<Box<dyn Fn(&[MemoryAtom]) -> Vec<MemoryAtom> + Send + Sync>>) -> Self {
        Self { send }
    }

    pub fn sync<S: AtomStore>(&self, store: &mut S, since_ms: u64) -> SyncReport {
        let Some(send) = &self.send else { return SyncReport::default() };
        let mine: Vec<MemoryAtom> = store
            .all()
            .into_iter()
            .filter(|a| a.last_recalled_at_ms >= since_ms)
            .collect();
        let theirs = send(&mine);

        let existing: HashMap<String, MemoryAtom> =
            store.all().into_iter().map(|a| (a.id.clone(), a)).collect();
        let mut report = SyncReport { sent: mine.len(), ..Default::default() };

        for incoming in theirs {
            match existing.get(&incoming.id) {
                None => {
                    store.put(incoming);
                    report.received += 1;
                }
                Some(mine) if mine.text == incoming.text => {
                    // The same memory on both sides: keep the STRONGER, which is
                    // the one that has been used more.
                    if incoming.stability_days > mine.stability_days {
                        store.put(incoming);
                    }
                }
                Some(_) => {
                    report.conflicts += 1;
                    // Both kept, under distinct ids. The suffix makes the
                    // duplicate visible to a person, which is who should resolve
                    // it.
                    let mut kept = incoming.clone();
                    kept.id = format!("{}-b", incoming.id);
                    store.put(kept);
                    report.kept_both += 1;
                }
            }
        }
        report
    }
}

/// Affect readings paired with whether anybody was actually speaking.
///
/// A voice-derived reading taken while nobody is speaking is reading the room's
/// air conditioning, and pairing the two is what stops an empty room being
/// recorded as a calm person.
pub struct AffectStateVadExtensions;

impl AffectStateVadExtensions {
    pub fn is_trustworthy(source: &str, speech_present: bool, confidence: f32) -> bool {
        if source == "voice" && !speech_present {
            return false;
        }
        confidence >= 0.5
    }

    /// Combines readings, weighted by confidence, with DISAGREEMENT lowering the
    /// result.
    ///
    /// Two sources that disagree are less informative than one that is sure, and
    /// averaging them into a confident middle is the standard way to turn two
    /// weak signals into one wrong strong one.
    pub fn combine(readings: &[(String, f32)]) -> (String, f32) {
        let usable: Vec<&(String, f32)> = readings
            .iter()
            .filter(|(label, _)| !label.is_empty() && label != "uncertain")
            .collect();
        if usable.is_empty() {
            return ("uncertain".into(), 0.0);
        }
        let mut totals: HashMap<&str, f32> = HashMap::new();
        for (label, confidence) in &usable {
            *totals.entry(label.as_str()).or_insert(0.0) += confidence;
        }
        let all: f32 = totals.values().sum();
        let (label, weight) = totals
            .into_iter()
            .max_by(|a, b| a.1.partial_cmp(&b.1).unwrap_or(std::cmp::Ordering::Equal))
            .unwrap_or(("uncertain", 0.0));
        let agreement = if all > 0.0 { weight / all } else { 0.0 };
        (
            label.to_string(),
            (weight / usable.len() as f32).min(1.0) * agreement,
        )
    }
}

/// A goal somebody is working towards.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct StoredGoal {
    pub goal_id: String,
    pub text: String,
    /// `None` means no deadline, which is different from a deadline that has
    /// passed. A goal with no date should never appear as overdue.
    pub due_at_ms: Option<u64>,
    pub progress: f32,
    pub is_done: bool,
    pub created_at_ms: u64,
}

impl StoredGoal {
    pub fn is_overdue_at(&self, now_ms: u64) -> bool {
        !self.is_done && self.due_at_ms.map(|d| now_ms > d).unwrap_or(false)
    }
}

/// Goals, in memory.
///
/// ORDERED BY WHAT IS ACTUALLY PRESSING - overdue first, then by deadline, then
/// the undated. Sorting by creation date buries a deadline under whatever was
/// typed most recently.
#[derive(Debug, Default)]
pub struct InMemoryGoalStore {
    goals: HashMap<String, StoredGoal>,
}

impl InMemoryGoalStore {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn put(&mut self, goal: StoredGoal) {
        self.goals.insert(goal.goal_id.clone(), goal);
    }

    pub fn get(&self, goal_id: &str) -> Option<&StoredGoal> {
        self.goals.get(goal_id)
    }

    pub fn complete(&mut self, goal_id: &str) -> bool {
        match self.goals.get_mut(goal_id) {
            Some(goal) if !goal.is_done => {
                goal.is_done = true;
                goal.progress = 1.0;
                true
            }
            _ => false,
        }
    }

    pub fn open_goals(&self, now_ms: u64) -> Vec<StoredGoal> {
        let mut live: Vec<StoredGoal> =
            self.goals.values().filter(|g| !g.is_done).cloned().collect();
        live.sort_by_key(|g| {
            (
                !g.is_overdue_at(now_ms),
                // `None` sorts LAST - an undated goal is not urgent, it is
                // undated.
                g.due_at_ms.unwrap_or(u64::MAX),
                g.created_at_ms,
            )
        });
        live
    }

    pub fn overdue(&self, now_ms: u64) -> Vec<StoredGoal> {
        self.goals
            .values()
            .filter(|g| g.is_overdue_at(now_ms))
            .cloned()
            .collect()
    }
}

/// Wires the memory service.
pub struct MemoryRegistration;

impl MemoryRegistration {
    pub fn add_memory() -> MemoryService<InMemoryAtomStore, CueExtractor> {
        MemoryService::new(InMemoryAtomStore::new(), CueExtractor)
    }
}
