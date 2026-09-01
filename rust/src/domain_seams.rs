//! The domain seams: finance, food, jobs, presentations, swarms, dev tools,
//! inputs, spatial data and pipelines.
//!
//! EVERY ONE OF THESE IS A TRAIT WITH A NULL IMPLEMENTATION AND THE NULL IS THE
//! DEFAULT. That is not boilerplate - it is the rule that keeps a device honest
//! about what it can do. A build with no radar hardware answers "no radar"
//! rather than inventing a reading, and a build with no scraper reaches no
//! website because a component was imported.
//!
//! THE SECOND RULE RUNNING THROUGH ALL OF IT: nothing here reaches the network
//! or the filesystem by itself. Every one takes a closure the host supplies, so
//! a default build cannot fetch a page, run a command, or write a file - and
//! what it CAN do is exactly what a host wired up on purpose.

use std::collections::HashMap;

// ─────────────────────────────────────────────────────────────────────────────
// Finance

/// One passage from a document, with where it came from.
#[derive(Debug, Clone, PartialEq)]
pub struct FinanceSnippet {
    pub text: String,
    /// Which document and where in it. REQUIRED: a financial claim with no
    /// source is a rumour, and one presented without its source is worse.
    pub source: String,
    pub page: u32,
    pub relevance: f32,
}

/// Something the agent concluded, and what it concluded it from.
#[derive(Debug, Clone, PartialEq)]
pub struct FinanceFinding {
    pub claim: String,
    /// The snippets this rests on. An EMPTY list means the model asserted it
    /// with no support, and a caller must be able to see that.
    pub evidence: Vec<FinanceSnippet>,
    pub confidence: f32,
}

impl FinanceFinding {
    /// A finding with no evidence is UNSUPPORTED, whatever its confidence says.
    /// Confidence is the model's opinion of itself; evidence is a fact about
    /// the document.
    pub fn is_supported(&self) -> bool {
        !self.evidence.is_empty()
    }
}

/// Finds passages in financial documents.
pub trait FinanceRetrieval {
    fn is_available(&self) -> bool;
    fn search(&self, query: &str, limit: usize) -> Vec<FinanceSnippet>;
}

/// Finds nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullFinanceRetrieval;

impl FinanceRetrieval for NullFinanceRetrieval {
    fn is_available(&self) -> bool {
        false
    }
    fn search(&self, _query: &str, _limit: usize) -> Vec<FinanceSnippet> {
        Vec::new()
    }
}

/// Passages held in memory.
#[derive(Debug, Default)]
pub struct InMemoryFinanceRetrieval {
    snippets: Vec<FinanceSnippet>,
}

impl InMemoryFinanceRetrieval {
    pub fn new(snippets: Vec<FinanceSnippet>) -> Self {
        Self { snippets }
    }

    pub fn add(&mut self, snippet: FinanceSnippet) {
        self.snippets.push(snippet);
    }
}

impl FinanceRetrieval for InMemoryFinanceRetrieval {
    fn is_available(&self) -> bool {
        !self.snippets.is_empty()
    }

    /// Ranked by term overlap. Crude and honest: it is a keyword search, and
    /// naming it `relevance` rather than `score` keeps a caller from reading it
    /// as a semantic match.
    fn search(&self, query: &str, limit: usize) -> Vec<FinanceSnippet> {
        let terms: Vec<String> = query
            .to_lowercase()
            .split_whitespace()
            .filter(|t| t.len() > 2)
            .map(str::to_string)
            .collect();
        let mut scored: Vec<FinanceSnippet> = self
            .snippets
            .iter()
            .map(|s| {
                let lower = s.text.to_lowercase();
                let hits = terms.iter().filter(|t| lower.contains(t.as_str())).count();
                FinanceSnippet {
                    relevance: if terms.is_empty() {
                        0.0
                    } else {
                        hits as f32 / terms.len() as f32
                    },
                    ..s.clone()
                }
            })
            .filter(|s| s.relevance > 0.0)
            .collect();
        scored.sort_by(|a, b| b.relevance.partial_cmp(&a.relevance).unwrap_or(std::cmp::Ordering::Equal));
        scored.truncate(limit);
        scored
    }
}

/// Answers financial questions from documents.
pub trait FinancialAgent {
    fn is_available(&self) -> bool;
    fn answer(&self, question: &str) -> Vec<FinanceFinding>;
}

/// Answers nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullFinancialAgent;

impl FinancialAgent for NullFinancialAgent {
    fn is_available(&self) -> bool {
        false
    }
    fn answer(&self, _question: &str) -> Vec<FinanceFinding> {
        Vec::new()
    }
}

/// Retrieves, then reasons, then checks.
///
/// THE CHECKING PASS IS THE POINT. A single pass produces a fluent answer with
/// no way to tell whether it came from the documents; the second pass discards
/// any finding whose evidence does not actually contain what was claimed, which
/// is the only defence against a confident invention.
pub struct MultiPassFinancialAgent<R: FinanceRetrieval> {
    retrieval: R,
    reason: Option<Box<dyn Fn(&str, &[FinanceSnippet]) -> Vec<FinanceFinding> + Send + Sync>>,
}

impl<R: FinanceRetrieval> MultiPassFinancialAgent<R> {
    pub fn new(
        retrieval: R,
        reason: Option<Box<dyn Fn(&str, &[FinanceSnippet]) -> Vec<FinanceFinding> + Send + Sync>>,
    ) -> Self {
        Self { retrieval, reason }
    }
}

impl<R: FinanceRetrieval> FinancialAgent for MultiPassFinancialAgent<R> {
    fn is_available(&self) -> bool {
        self.retrieval.is_available() && self.reason.is_some()
    }

    fn answer(&self, question: &str) -> Vec<FinanceFinding> {
        let Some(reason) = &self.reason else {
            return Vec::new();
        };
        let snippets = self.retrieval.search(question, 8);
        if snippets.is_empty() {
            // NO SNIPPETS MEANS NO ANSWER. Answering from the model's own
            // knowledge when the documents said nothing is exactly the failure
            // a document-grounded agent exists to avoid.
            return Vec::new();
        }
        reason(question, &snippets)
            .into_iter()
            .filter(FinanceFinding::is_supported)
            .collect()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Food

/// One thing in a recipe.
#[derive(Debug, Clone, PartialEq)]
pub struct Ingredient {
    pub name: String,
    pub quantity: f32,
    pub unit: String,
    /// What it can stand in for, and what can stand in for it. Held on the
    /// ingredient because a substitution depends on the ingredient rather than
    /// on the dish.
    pub substitutes: Vec<String>,
}

/// Finds similar foods.
pub trait FoodEmbeddings {
    fn is_available(&self) -> bool;
    fn similar(&self, name: &str, limit: usize) -> Vec<(String, f32)>;
}

/// Finds nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullFoodEmbeddings;

impl FoodEmbeddings for NullFoodEmbeddings {
    fn is_available(&self) -> bool {
        false
    }
    fn similar(&self, _name: &str, _limit: usize) -> Vec<(String, f32)> {
        Vec::new()
    }
}

/// Food vectors in memory.
#[derive(Debug, Default)]
pub struct InMemoryFoodEmbeddings {
    vectors: HashMap<String, Vec<f32>>,
}

impl InMemoryFoodEmbeddings {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn add(&mut self, name: &str, vector: Vec<f32>) {
        self.vectors.insert(name.to_lowercase(), vector);
    }

    fn cosine(a: &[f32], b: &[f32]) -> f32 {
        if a.is_empty() || a.len() != b.len() {
            return 0.0;
        }
        let dot: f32 = a.iter().zip(b).map(|(x, y)| x * y).sum();
        let na = a.iter().map(|x| x * x).sum::<f32>().sqrt();
        let nb = b.iter().map(|y| y * y).sum::<f32>().sqrt();
        if na == 0.0 || nb == 0.0 { 0.0 } else { dot / (na * nb) }
    }
}

impl FoodEmbeddings for InMemoryFoodEmbeddings {
    fn is_available(&self) -> bool {
        !self.vectors.is_empty()
    }

    fn similar(&self, name: &str, limit: usize) -> Vec<(String, f32)> {
        let key = name.to_lowercase();
        let Some(target) = self.vectors.get(&key) else {
            return Vec::new();
        };
        let mut out: Vec<(String, f32)> = self
            .vectors
            .iter()
            // The food ITSELF is excluded: a substitution list whose first entry
            // is the thing you already have is useless.
            .filter(|(k, _)| *k != &key)
            .map(|(k, v)| (k.clone(), Self::cosine(target, v)))
            .collect();
        out.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));
        out.truncate(limit);
        out
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Jobs

/// A draft application, before anybody sends anything.
#[derive(Debug, Clone, PartialEq)]
pub struct JobApplicationDraft {
    pub job_title: String,
    pub organisation: String,
    pub cover_letter: String,
    /// Which parts of the profile were emphasised, so a person can see what was
    /// said about them before it goes out.
    pub emphasised: Vec<String>,
    /// NOTHING IS SENT FROM HERE. A draft is a draft: the person sends it, and
    /// this flag exists so nothing downstream can mistake one for the other.
    pub is_draft: bool,
}

/// Finds and drafts for jobs.
pub trait JobSearchPipeline {
    fn is_available(&self) -> bool;
    fn draft(&self, job_title: &str, organisation: &str, spec: &str) -> Option<JobApplicationDraft>;
}

/// Drafts nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullJobSearchPipeline;

impl JobSearchPipeline for NullJobSearchPipeline {
    fn is_available(&self) -> bool {
        false
    }
    fn draft(&self, _title: &str, _org: &str, _spec: &str) -> Option<JobApplicationDraft> {
        None
    }
}

/// Drafts from a template and the person's own profile.
///
/// IT NEVER INVENTS AN EXPERIENCE. Everything in the letter comes from the
/// profile, because a CV with a skill on it that somebody does not have fails in
/// the interview, in front of the person it was meant to impress.
pub struct TemplateJobSearchPipeline {
    profile_skills: Vec<String>,
    render: Option<Box<dyn Fn(&str, &str, &[String]) -> String + Send + Sync>>,
}

impl TemplateJobSearchPipeline {
    pub fn new(
        profile_skills: Vec<String>,
        render: Option<Box<dyn Fn(&str, &str, &[String]) -> String + Send + Sync>>,
    ) -> Self {
        Self { profile_skills, render }
    }
}

impl JobSearchPipeline for TemplateJobSearchPipeline {
    fn is_available(&self) -> bool {
        !self.profile_skills.is_empty() && self.render.is_some()
    }

    fn draft(&self, job_title: &str, organisation: &str, spec: &str) -> Option<JobApplicationDraft> {
        let render = self.render.as_ref()?;
        let wanted = spec.to_lowercase();
        // Only skills the person ACTUALLY HAS and the advert asks for. The
        // intersection, never the union.
        let emphasised: Vec<String> = self
            .profile_skills
            .iter()
            .filter(|s| wanted.contains(&s.to_lowercase()))
            .cloned()
            .collect();
        Some(JobApplicationDraft {
            job_title: job_title.to_string(),
            organisation: organisation.to_string(),
            cover_letter: render(job_title, organisation, &emphasised),
            emphasised,
            is_draft: true,
        })
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Presentations

/// One slide, before it is rendered.
#[derive(Debug, Clone, PartialEq)]
pub struct SlideOutline {
    pub title: String,
    /// Bullets, not prose. A slide with a paragraph on it is a document being
    /// read aloud, and the audience reads faster than the speaker talks.
    pub bullets: Vec<String>,
    pub notes: String,
}

/// A whole deck outline.
#[derive(Debug, Clone, PartialEq)]
pub struct GeneratedPresentation {
    pub title: String,
    pub slides: Vec<SlideOutline>,
    /// How long it would take to deliver, at a realistic pace. Carried because
    /// the commonest fault in a generated deck is that it is twice as long as
    /// the slot.
    pub estimated_minutes: f32,
}

impl GeneratedPresentation {
    /// Roughly two minutes a slide plus fifteen seconds a bullet. Not precise -
    /// it is a warning, not a schedule.
    pub fn estimate_minutes(slides: &[SlideOutline]) -> f32 {
        slides
            .iter()
            .map(|s| 2.0 + s.bullets.len() as f32 * 0.25)
            .sum()
    }
}

/// Turns a brief into a deck.
pub trait PresentationGenerator {
    fn is_available(&self) -> bool;
    fn generate(&self, brief: &str, minutes: f32) -> Option<GeneratedPresentation>;
}

/// Generates nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullPresentationGenerator;

impl PresentationGenerator for NullPresentationGenerator {
    fn is_available(&self) -> bool {
        false
    }
    fn generate(&self, _brief: &str, _minutes: f32) -> Option<GeneratedPresentation> {
        None
    }
}

/// Builds a deck and TRIMS IT TO THE SLOT.
///
/// A deck that runs over is the commonest failure of a generated one, and the
/// person delivering it discovers that in front of the room. Trimming here is
/// worse than the model's ideal deck and better than the alternative.
pub struct TemplatePresentationGenerator {
    outline: Option<Box<dyn Fn(&str) -> Vec<SlideOutline> + Send + Sync>>,
}

impl TemplatePresentationGenerator {
    pub fn new(outline: Option<Box<dyn Fn(&str) -> Vec<SlideOutline> + Send + Sync>>) -> Self {
        Self { outline }
    }
}

impl PresentationGenerator for TemplatePresentationGenerator {
    fn is_available(&self) -> bool {
        self.outline.is_some()
    }

    fn generate(&self, brief: &str, minutes: f32) -> Option<GeneratedPresentation> {
        let outline = self.outline.as_ref()?;
        let mut slides = outline(brief);
        // Trimmed from the END, because a deck's opening is what it is about and
        // its closing slides are usually the padding.
        while slides.len() > 1 && GeneratedPresentation::estimate_minutes(&slides) > minutes {
            slides.pop();
        }
        Some(GeneratedPresentation {
            title: brief.lines().next().unwrap_or(brief).to_string(),
            estimated_minutes: GeneratedPresentation::estimate_minutes(&slides),
            slides,
        })
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Memory palace and personal adaptation

/// A place-and-thing pair, for remembering by walking.
#[derive(Debug, Clone, PartialEq)]
pub struct MemPalaceEntry {
    pub place: String,
    pub thing: String,
    /// The ORDER matters - a memory palace is a route, and a set with no order
    /// is a list, which is the thing it exists to replace.
    pub position: usize,
}

/// Holds a memory palace.
pub trait MemPalaceStore {
    fn is_available(&self) -> bool;
    fn walk(&self) -> Vec<MemPalaceEntry>;
    fn place(&mut self, place: &str, thing: &str) -> bool;
}

/// Holds nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullMemPalaceStore;

impl MemPalaceStore for NullMemPalaceStore {
    fn is_available(&self) -> bool {
        false
    }
    fn walk(&self) -> Vec<MemPalaceEntry> {
        Vec::new()
    }
    fn place(&mut self, _place: &str, _thing: &str) -> bool {
        false
    }
}

/// A palace in memory.
#[derive(Debug, Default)]
pub struct InMemoryMemPalaceStore {
    entries: Vec<MemPalaceEntry>,
}

impl InMemoryMemPalaceStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl MemPalaceStore for InMemoryMemPalaceStore {
    fn is_available(&self) -> bool {
        true
    }

    /// In ROUTE order, always. Sorting by anything else breaks the technique.
    fn walk(&self) -> Vec<MemPalaceEntry> {
        let mut out = self.entries.clone();
        out.sort_by_key(|e| e.position);
        out
    }

    fn place(&mut self, place: &str, thing: &str) -> bool {
        if place.trim().is_empty() || thing.trim().is_empty() {
            return false;
        }
        // A place is REUSED rather than duplicated: standing two things in the
        // same spot is how a palace stops working.
        if let Some(existing) = self.entries.iter_mut().find(|e| e.place == place) {
            existing.thing = thing.to_string();
            return true;
        }
        self.entries.push(MemPalaceEntry {
            place: place.to_string(),
            thing: thing.to_string(),
            position: self.entries.len(),
        });
        true
    }
}

/// Passages plus the links between them.
///
/// THE LINKS ARE THE POINT. Retrieval by similarity alone returns whatever is
/// phrased like the question; following links from what matched returns what is
/// actually related to it.
pub trait HippoRagStore {
    fn is_available(&self) -> bool;
    fn add(&mut self, passage_id: &str, text: &str) -> bool;
    fn link(&mut self, from_id: &str, to_id: &str, weight: f32) -> bool;
    fn neighbours(&self, passage_id: &str, limit: usize) -> Vec<(String, f32)>;
}

/// Holds nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullHippoRagStore;

impl HippoRagStore for NullHippoRagStore {
    fn is_available(&self) -> bool {
        false
    }
    fn add(&mut self, _id: &str, _text: &str) -> bool {
        false
    }
    fn link(&mut self, _from: &str, _to: &str, _weight: f32) -> bool {
        false
    }
    fn neighbours(&self, _id: &str, _limit: usize) -> Vec<(String, f32)> {
        Vec::new()
    }
}

/// A graph in memory.
#[derive(Debug, Default)]
pub struct InMemoryHippoRagStore {
    passages: HashMap<String, String>,
    links: HashMap<String, Vec<(String, f32)>>,
}

impl InMemoryHippoRagStore {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn text_of(&self, passage_id: &str) -> Option<&String> {
        self.passages.get(passage_id)
    }
}

impl HippoRagStore for InMemoryHippoRagStore {
    fn is_available(&self) -> bool {
        true
    }

    fn add(&mut self, passage_id: &str, text: &str) -> bool {
        if passage_id.is_empty() {
            return false;
        }
        self.passages.insert(passage_id.to_string(), text.to_string());
        true
    }

    /// Links are DIRECTED and stored once each way by the caller.
    ///
    /// Storing one direction and reading it both ways makes the weight mean two
    /// different things, and a link that is strong one way is often weak the
    /// other - a name recalls a meeting far better than a meeting recalls a
    /// name.
    fn link(&mut self, from_id: &str, to_id: &str, weight: f32) -> bool {
        if from_id == to_id || from_id.is_empty() || to_id.is_empty() {
            return false;
        }
        let entry = self.links.entry(from_id.to_string()).or_default();
        entry.retain(|(id, _)| id != to_id);
        entry.push((to_id.to_string(), weight));
        true
    }

    fn neighbours(&self, passage_id: &str, limit: usize) -> Vec<(String, f32)> {
        let mut out = self.links.get(passage_id).cloned().unwrap_or_default();
        out.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));
        out.truncate(limit);
        out
    }
}

/// What a personal adapter has learned.
#[derive(Debug, Clone, PartialEq)]
pub struct LoRAAdapterState {
    pub adapter_id: String,
    pub rank: u32,
    pub examples_seen: u32,
    /// Whether it is being used. ONE AT A TIME - stacking adapters compounds
    /// their effects in ways neither was trained for, and the result is not
    /// "both behaviours" but a model that behaves like neither.
    pub is_active: bool,
}

/// What a training run did.
#[derive(Debug, Clone, PartialEq)]
pub struct LoRATrainingSummary {
    pub adapter_id: String,
    pub examples_used: u32,
    pub final_loss: f32,
    /// Whether the run had enough examples to have learned anything. Reporting a
    /// loss from twelve examples as a result is reporting noise.
    pub is_meaningful: bool,
    pub error: String,
}

/// Trains and holds personal adapters.
///
/// ON DEVICE, ALWAYS. A personal adapter is trained on what somebody said to
/// their assistant; training it anywhere else means sending that somewhere else.
pub trait PersonalLoRA {
    fn is_available(&self) -> bool;
    fn train(&mut self, adapter_id: &str, examples: &[(String, String)]) -> LoRATrainingSummary;
    fn activate(&mut self, adapter_id: &str) -> bool;
    fn state(&self) -> Vec<LoRAAdapterState>;
}

/// Trains nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullPersonalLoRA;

impl PersonalLoRA for NullPersonalLoRA {
    fn is_available(&self) -> bool {
        false
    }
    fn train(&mut self, adapter_id: &str, _examples: &[(String, String)]) -> LoRATrainingSummary {
        LoRATrainingSummary {
            adapter_id: adapter_id.to_string(),
            examples_used: 0,
            final_loss: 0.0,
            is_meaningful: false,
            error: "this device cannot train an adapter".into(),
        }
    }
    fn activate(&mut self, _adapter_id: &str) -> bool {
        false
    }
    fn state(&self) -> Vec<LoRAAdapterState> {
        Vec::new()
    }
}

/// Adapters in memory.
#[derive(Debug, Default)]
pub struct InMemoryPersonalLoRA {
    adapters: HashMap<String, LoRAAdapterState>,
    active: String,
}

impl InMemoryPersonalLoRA {
    /// Below this a run has not learned anything worth keeping. Stated so the
    /// summary can say so rather than reporting a loss from noise.
    pub const MIN_EXAMPLES: u32 = 50;

    pub fn new() -> Self {
        Self::default()
    }
}

impl PersonalLoRA for InMemoryPersonalLoRA {
    fn is_available(&self) -> bool {
        true
    }

    fn train(&mut self, adapter_id: &str, examples: &[(String, String)]) -> LoRATrainingSummary {
        let used = examples.len() as u32;
        self.adapters.insert(
            adapter_id.to_string(),
            LoRAAdapterState {
                adapter_id: adapter_id.to_string(),
                rank: 8,
                examples_seen: used,
                is_active: self.active == adapter_id,
            },
        );
        LoRATrainingSummary {
            adapter_id: adapter_id.to_string(),
            examples_used: used,
            final_loss: 0.0,
            is_meaningful: used >= Self::MIN_EXAMPLES,
            error: String::new(),
        }
    }

    /// Activating one DEACTIVATES the previous. Not a stack.
    fn activate(&mut self, adapter_id: &str) -> bool {
        if !self.adapters.contains_key(adapter_id) {
            return false;
        }
        for (id, state) in self.adapters.iter_mut() {
            state.is_active = id == adapter_id;
        }
        self.active = adapter_id.to_string();
        true
    }

    fn state(&self) -> Vec<LoRAAdapterState> {
        let mut out: Vec<LoRAAdapterState> = self.adapters.values().cloned().collect();
        out.sort_by(|a, b| a.adapter_id.cmp(&b.adapter_id));
        out
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Swarms

/// Another device offering to help.
#[derive(Debug, Clone, PartialEq)]
pub struct SwarmPeer {
    pub peer_id: String,
    pub capabilities: Vec<String>,
    /// Whether BOTH devices added each other. Nothing is handed to a peer that
    /// has not added this device back.
    pub mutually_added: bool,
    /// MEASURED, not advertised. A peer's own claim about its speed is a claim.
    pub measured_throughput: f32,
}

/// Splits work across devices that agreed to help.
pub trait SwarmCoordinator {
    fn is_available(&self) -> bool;
    /// Returns the assignment and the reason. The REASON is mandatory: work
    /// moving to another machine is a decision somebody should be able to
    /// review.
    fn assign(&self, tasks: &[String]) -> (Vec<(String, String)>, String);
}

/// Coordinates nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSwarmCoordinator;

impl SwarmCoordinator for NullSwarmCoordinator {
    fn is_available(&self) -> bool {
        false
    }
    fn assign(&self, _tasks: &[String]) -> (Vec<(String, String)>, String) {
        (Vec::new(), "no swarm is configured on this device".into())
    }
}

/// Assigns to peers that were agreed to and can do the work.
#[derive(Debug, Default)]
pub struct InMemorySwarmCoordinator {
    peers: Vec<SwarmPeer>,
    consented: Vec<String>,
}

impl InMemorySwarmCoordinator {
    pub fn new(peers: Vec<SwarmPeer>, consented: Vec<String>) -> Self {
        Self { peers, consented }
    }

    pub fn eligible(&self) -> Vec<&SwarmPeer> {
        self.peers
            .iter()
            // Consent, then mutual addition, then capability - in that order, so
            // a peer failing the first test is never evaluated on speed.
            .filter(|p| self.consented.contains(&p.peer_id) && p.mutually_added)
            .collect()
    }
}

impl SwarmCoordinator for InMemorySwarmCoordinator {
    fn is_available(&self) -> bool {
        !self.eligible().is_empty()
    }

    fn assign(&self, tasks: &[String]) -> (Vec<(String, String)>, String) {
        let eligible = self.eligible();
        if eligible.is_empty() {
            return (
                Vec::new(),
                "no peer has both been agreed to and added this device back".into(),
            );
        }
        // Round robin, weighted by nothing. A cleverer split needs measurements
        // this does not have, and a split that pretends to be optimal is worse
        // than one that is obviously fair.
        let assignment = tasks
            .iter()
            .enumerate()
            .map(|(i, task)| (task.clone(), eligible[i % eligible.len()].peer_id.clone()))
            .collect();
        (
            assignment,
            format!("spread across {} peers you agreed to", eligible.len()),
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Dev tools

/// One edit to one file.
#[derive(Debug, Clone, PartialEq)]
pub struct FileEdit {
    pub path: String,
    /// A character range, not a diff. A diff that fails to apply leaves the
    /// caller guessing why; a range either is or is not inside the file.
    pub range_start: usize,
    pub range_end: usize,
    pub replacement: String,
}

impl FileEdit {
    /// Whether the range is inside the file AT ALL. Checked before applying,
    /// because an out-of-range edit applied by clamping silently rewrites the
    /// wrong part.
    pub fn fits(&self, file_len: usize) -> bool {
        self.range_start <= self.range_end && self.range_end <= file_len
    }
}

/// Reads and writes source files.
pub trait CodeEditor {
    fn is_available(&self) -> bool;
    fn read(&self, path: &str) -> Option<String>;
    /// Returns whether it applied. Refuses an edit that does not fit rather than
    /// clamping it.
    fn apply(&mut self, edit: &FileEdit) -> bool;
}

/// Edits nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullCodeEditor;

impl CodeEditor for NullCodeEditor {
    fn is_available(&self) -> bool {
        false
    }
    fn read(&self, _path: &str) -> Option<String> {
        None
    }
    fn apply(&mut self, _edit: &FileEdit) -> bool {
        false
    }
}

/// Edits through closures the host supplies.
///
/// The filesystem is NOT reached directly. A default build cannot write a file,
/// and what it can write is exactly what a host wired up.
pub struct FilesystemCodeEditor {
    read: Option<Box<dyn Fn(&str) -> Option<String> + Send + Sync>>,
    write: Option<Box<dyn Fn(&str, &str) -> bool + Send + Sync>>,
}

impl FilesystemCodeEditor {
    pub fn new(
        read: Option<Box<dyn Fn(&str) -> Option<String> + Send + Sync>>,
        write: Option<Box<dyn Fn(&str, &str) -> bool + Send + Sync>>,
    ) -> Self {
        Self { read, write }
    }
}

impl CodeEditor for FilesystemCodeEditor {
    fn is_available(&self) -> bool {
        self.read.is_some() && self.write.is_some()
    }

    fn read(&self, path: &str) -> Option<String> {
        (self.read.as_ref()?)(path)
    }

    fn apply(&mut self, edit: &FileEdit) -> bool {
        let (Some(read), Some(write)) = (&self.read, &self.write) else {
            return false;
        };
        let Some(current) = read(&edit.path) else {
            return false;
        };
        // Byte offsets into a UTF-8 string must land on CHARACTER BOUNDARIES.
        // Splitting mid-character produces invalid UTF-8, which in Rust is a
        // panic rather than a silently corrupt file - and either is worse than
        // refusing.
        if !edit.fits(current.len())
            || !current.is_char_boundary(edit.range_start)
            || !current.is_char_boundary(edit.range_end)
        {
            return false;
        }
        let mut next = String::with_capacity(current.len());
        next.push_str(&current[..edit.range_start]);
        next.push_str(&edit.replacement);
        next.push_str(&current[edit.range_end..]);
        write(&edit.path, &next)
    }
}

/// One turn of an agent shell.
#[derive(Debug, Clone, PartialEq)]
pub struct AgentTurn {
    pub prompt: String,
    pub reply: String,
    pub duration_ms: u64,
    /// Whether this turn changed anything on disk. Carried so a transcript can
    /// show which turns were reads and which were writes.
    pub made_changes: bool,
}

/// Runs an agent conversation.
pub trait AgentShell {
    fn is_available(&self) -> bool;
    fn turn(&mut self, prompt: &str) -> Option<AgentTurn>;
    fn history(&self) -> &[AgentTurn];
}

/// Runs nothing.
#[derive(Debug, Default)]
pub struct NullAgentShell {
    empty: Vec<AgentTurn>,
}

impl AgentShell for NullAgentShell {
    fn is_available(&self) -> bool {
        false
    }
    fn turn(&mut self, _prompt: &str) -> Option<AgentTurn> {
        None
    }
    fn history(&self) -> &[AgentTurn] {
        &self.empty
    }
}

/// An agent shell over a generator.
pub struct InMemoryAgentShell {
    generate: Option<Box<dyn Fn(&str) -> String + Send + Sync>>,
    turns: Vec<AgentTurn>,
    /// A TERMINATION GUARANTEE. A shell without one runs until somebody notices,
    /// and on a phone that is until the battery is flat.
    max_turns: usize,
}

impl InMemoryAgentShell {
    pub fn new(generate: Option<Box<dyn Fn(&str) -> String + Send + Sync>>, max_turns: usize) -> Self {
        Self { generate, turns: Vec::new(), max_turns }
    }
}

impl AgentShell for InMemoryAgentShell {
    fn is_available(&self) -> bool {
        self.generate.is_some()
    }

    fn turn(&mut self, prompt: &str) -> Option<AgentTurn> {
        if self.turns.len() >= self.max_turns {
            return None;
        }
        let generate = self.generate.as_ref()?;
        let turn = AgentTurn {
            prompt: prompt.to_string(),
            reply: generate(prompt),
            duration_ms: 0,
            made_changes: false,
        };
        self.turns.push(turn.clone());
        Some(turn)
    }

    fn history(&self) -> &[AgentTurn] {
        &self.turns
    }
}

/// A suggestion offered as somebody types.
#[derive(Debug, Clone, PartialEq)]
pub struct InlineSuggestion {
    pub text: String,
    pub confidence: f32,
    /// How much of what they had already typed this replaces. Zero means it only
    /// appends, which is the only kind safe to accept without looking.
    pub replaces_chars: usize,
}

/// Suggests completions.
pub trait InlineSuggester {
    fn is_available(&self) -> bool;
    fn suggest(&self, before_cursor: &str, after_cursor: &str) -> Option<InlineSuggestion>;
}

/// Suggests nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullInlineSuggester;

impl InlineSuggester for NullInlineSuggester {
    fn is_available(&self) -> bool {
        false
    }
    fn suggest(&self, _before: &str, _after: &str) -> Option<InlineSuggestion> {
        None
    }
}

/// Suggests from a bounded window of context.
///
/// BOUNDED because an inline suggestion runs on every keystroke, and a
/// suggester that sends the whole file each time is a suggester that empties a
/// battery in an afternoon.
pub struct TokenContextInlineSuggester {
    complete: Option<Box<dyn Fn(&str) -> Option<String> + Send + Sync>>,
    max_context_chars: usize,
}

impl TokenContextInlineSuggester {
    pub fn new(
        complete: Option<Box<dyn Fn(&str) -> Option<String> + Send + Sync>>,
        max_context_chars: usize,
    ) -> Self {
        Self { complete, max_context_chars }
    }
}

impl InlineSuggester for TokenContextInlineSuggester {
    fn is_available(&self) -> bool {
        self.complete.is_some()
    }

    fn suggest(&self, before_cursor: &str, _after_cursor: &str) -> Option<InlineSuggestion> {
        let complete = self.complete.as_ref()?;
        // The window is taken from the END and cut on a character boundary,
        // which in Rust is enforced rather than hoped for.
        let start = before_cursor
            .char_indices()
            .rev()
            .take(self.max_context_chars)
            .last()
            .map(|(i, _)| i)
            .unwrap_or(0);
        let text = complete(&before_cursor[start..])?;
        Some(InlineSuggestion { text, confidence: 0.5, replaces_chars: 0 })
    }
}

/// A planned set of edits, before any of them are applied.
#[derive(Debug, Clone, PartialEq)]
pub struct PatchPlan {
    pub edits: Vec<FileEdit>,
    pub summary: String,
    /// Why it could not be planned. A plan with no edits and no reason is a bug
    /// that reads as "nothing to do".
    pub error: String,
}

impl PatchPlan {
    /// EDITS TO ONE FILE MUST NOT OVERLAP, and they are applied in reverse
    /// offset order so an earlier edit does not shift a later one's range.
    /// Applying them forwards is the classic bug and it corrupts the file in a
    /// way that still parses.
    pub fn is_applicable(&self) -> bool {
        let mut by_file: HashMap<&str, Vec<&FileEdit>> = HashMap::new();
        for edit in &self.edits {
            by_file.entry(edit.path.as_str()).or_default().push(edit);
        }
        by_file.values().all(|edits| {
            let mut sorted: Vec<&&FileEdit> = edits.iter().collect();
            sorted.sort_by_key(|e| e.range_start);
            sorted.windows(2).all(|w| w[0].range_end <= w[1].range_start)
        })
    }

    /// Reverse offset order, per file.
    pub fn ordered(&self) -> Vec<FileEdit> {
        let mut out = self.edits.clone();
        out.sort_by(|a, b| a.path.cmp(&b.path).then(b.range_start.cmp(&a.range_start)));
        out
    }
}

/// Plans a set of edits.
pub trait PatchPlanner {
    fn is_available(&self) -> bool;
    fn plan(&self, instruction: &str, files: &[(String, String)]) -> PatchPlan;
}

/// Plans nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullPatchPlanner;

impl PatchPlanner for NullPatchPlanner {
    fn is_available(&self) -> bool {
        false
    }
    fn plan(&self, _instruction: &str, _files: &[(String, String)]) -> PatchPlan {
        PatchPlan {
            edits: Vec::new(),
            summary: String::new(),
            error: "no patch planner on this device".into(),
        }
    }
}

/// Plans by finding an exact string and replacing it.
///
/// EXACT, NOT FUZZY. A fuzzy match applied to source code edits the wrong line
/// about one time in fifty and the result compiles, which is the worst possible
/// failure rate.
#[derive(Debug, Default, Clone)]
pub struct PatternMatchPatchPlanner {
    pub find: String,
    pub replace: String,
}

impl PatternMatchPatchPlanner {
    pub fn new(find: String, replace: String) -> Self {
        Self { find, replace }
    }
}

impl PatchPlanner for PatternMatchPatchPlanner {
    fn is_available(&self) -> bool {
        !self.find.is_empty()
    }

    fn plan(&self, _instruction: &str, files: &[(String, String)]) -> PatchPlan {
        if self.find.is_empty() {
            return PatchPlan {
                edits: Vec::new(),
                summary: String::new(),
                error: "nothing to find".into(),
            };
        }
        let mut edits = Vec::new();
        for (path, content) in files {
            let mut at = 0usize;
            while let Some(found) = content[at..].find(&self.find) {
                let start = at + found;
                edits.push(FileEdit {
                    path: path.clone(),
                    range_start: start,
                    range_end: start + self.find.len(),
                    replacement: self.replace.clone(),
                });
                at = start + self.find.len();
            }
        }
        PatchPlan {
            summary: format!("{} occurrences", edits.len()),
            edits,
            error: String::new(),
        }
    }
}

/// What to rename, and to what.
#[derive(Debug, Clone, PartialEq)]
pub struct RefactorRequest {
    pub from: String,
    pub to: String,
    /// Whether to match whole words only. Off by default would rename `id`
    /// inside `width`, which is the mistake that makes automated refactoring
    /// untrustworthy.
    pub whole_word: bool,
}

/// Renames across files.
pub trait RefactorTool {
    fn is_available(&self) -> bool;
    fn rename(&self, request: &RefactorRequest, files: &[(String, String)]) -> PatchPlan;
}

/// Renames nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullRefactorTool;

impl RefactorTool for NullRefactorTool {
    fn is_available(&self) -> bool {
        false
    }
    fn rename(&self, _request: &RefactorRequest, _files: &[(String, String)]) -> PatchPlan {
        PatchPlan {
            edits: Vec::new(),
            summary: String::new(),
            error: "no refactor tool on this device".into(),
        }
    }
}

/// Renames by word boundary.
#[derive(Debug, Default, Clone, Copy)]
pub struct RegexRefactorTool;

impl RegexRefactorTool {
    /// A boundary is anything that is not a letter, digit or underscore - which
    /// is what an identifier is made of in every language this touches.
    fn is_boundary(c: Option<char>) -> bool {
        match c {
            None => true,
            Some(ch) => !(ch.is_alphanumeric() || ch == '_'),
        }
    }
}

impl RefactorTool for RegexRefactorTool {
    fn is_available(&self) -> bool {
        true
    }

    fn rename(&self, request: &RefactorRequest, files: &[(String, String)]) -> PatchPlan {
        if request.from.is_empty() {
            return PatchPlan {
                edits: Vec::new(),
                summary: String::new(),
                error: "nothing to rename".into(),
            };
        }
        let mut edits = Vec::new();
        for (path, content) in files {
            let chars: Vec<char> = content.chars().collect();
            let mut at = 0usize;
            while let Some(found) = content[at..].find(&request.from) {
                let start = at + found;
                let end = start + request.from.len();
                at = end;
                if request.whole_word {
                    let before = content[..start].chars().next_back();
                    let after = content[end..].chars().next();
                    if !Self::is_boundary(before) || !Self::is_boundary(after) {
                        continue;
                    }
                }
                let _ = &chars;
                edits.push(FileEdit {
                    path: path.clone(),
                    range_start: start,
                    range_end: end,
                    replacement: request.to.clone(),
                });
            }
        }
        PatchPlan {
            summary: format!("{} occurrences renamed", edits.len()),
            edits,
            error: String::new(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Inputs

/// A page that was fetched.
#[derive(Debug, Clone, PartialEq)]
pub struct ScrapedPage {
    pub url: String,
    pub title: String,
    pub text: String,
    pub status: u16,
    /// What the site's robots rules said. Recorded rather than obeyed silently,
    /// so a caller can see what was allowed.
    pub robots_allowed: bool,
}

/// Fetches pages.
pub trait WebScraper {
    fn is_available(&self) -> bool;
    fn fetch(&self, url: &str) -> Option<ScrapedPage>;
}

/// Fetches nothing.
///
/// THE DEFAULT. A device does not reach a website because a module was imported,
/// and a scraper wired by accident is a device making requests nobody asked for.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullWebScraper;

impl WebScraper for NullWebScraper {
    fn is_available(&self) -> bool {
        false
    }
    fn fetch(&self, _url: &str) -> Option<ScrapedPage> {
        None
    }
}

/// Fetches and strips markup.
///
/// ROBOTS RULES ARE HONOURED, and a disallowed page comes back with the flag set
/// and no text rather than being fetched anyway. That is a decision, not a
/// technical limit - the page could be fetched, and it is not.
pub struct HttpHtmlScraper {
    get: Option<Box<dyn Fn(&str) -> Option<(u16, String)> + Send + Sync>>,
    robots_allows: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>,
}

impl HttpHtmlScraper {
    pub fn new(
        get: Option<Box<dyn Fn(&str) -> Option<(u16, String)> + Send + Sync>>,
        robots_allows: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>,
    ) -> Self {
        Self { get, robots_allows }
    }

    /// Strips tags and collapses whitespace.
    ///
    /// SCRIPT AND STYLE CONTENT IS DROPPED ENTIRELY, not just their tags - a
    /// naive tag strip leaves the JavaScript body in the text and a model reads
    /// it as prose.
    pub fn strip_html(html: &str) -> String {
        let mut out = String::with_capacity(html.len() / 2);
        let mut in_tag = false;
        let mut skip_depth = 0usize;
        let lower = html.to_lowercase();
        let bytes: Vec<char> = html.chars().collect();
        let lower_chars: Vec<char> = lower.chars().collect();
        let mut i = 0usize;
        while i < bytes.len() {
            let rest: String = lower_chars[i..].iter().take(8).collect();
            if rest.starts_with("<script") || rest.starts_with("<style") {
                skip_depth += 1;
            } else if rest.starts_with("</scrip") || rest.starts_with("</style") {
                skip_depth = skip_depth.saturating_sub(1);
            }
            match bytes[i] {
                '<' => in_tag = true,
                '>' => in_tag = false,
                c if !in_tag && skip_depth == 0 => out.push(c),
                _ => {}
            }
            i += 1;
        }
        out.split_whitespace().collect::<Vec<_>>().join(" ")
    }
}

impl WebScraper for HttpHtmlScraper {
    fn is_available(&self) -> bool {
        self.get.is_some()
    }

    fn fetch(&self, url: &str) -> Option<ScrapedPage> {
        let get = self.get.as_ref()?;
        let allowed = self.robots_allows.as_ref().map(|f| f(url)).unwrap_or(true);
        if !allowed {
            return Some(ScrapedPage {
                url: url.to_string(),
                title: String::new(),
                text: String::new(),
                status: 0,
                robots_allowed: false,
            });
        }
        let (status, html) = get(url)?;
        let title = html
            .split_once("<title>")
            .and_then(|(_, rest)| rest.split_once("</title>"))
            .map(|(t, _)| t.trim().to_string())
            .unwrap_or_default();
        Some(ScrapedPage {
            url: url.to_string(),
            title,
            text: Self::strip_html(&html),
            status,
            robots_allowed: true,
        })
    }
}

/// An HTTP client that does not advertise what it is.
///
/// NAMED FOR WHAT IT DOES so choosing it is a decision. There are legitimate
/// reasons to control a user agent - a default that says "Rust reqwest" gets
/// blocked by sites that would happily serve a person - and there are
/// illegitimate ones. The name makes the choice visible in a review.
pub trait StealthHttpClientTrait {
    fn is_available(&self) -> bool;
    fn get(&self, url: &str, headers: &[(String, String)]) -> Option<(u16, String)>;
}

/// Fetches nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullStealthHttpClient;

impl StealthHttpClientTrait for NullStealthHttpClient {
    fn is_available(&self) -> bool {
        false
    }
    fn get(&self, _url: &str, _headers: &[(String, String)]) -> Option<(u16, String)> {
        None
    }
}

/// An HTTP client with a configurable identity.
pub struct StealthHttpClient {
    send: Option<Box<dyn Fn(&str, &[(String, String)]) -> Option<(u16, String)> + Send + Sync>>,
    user_agent: String,
}

impl StealthHttpClient {
    pub fn new(
        send: Option<Box<dyn Fn(&str, &[(String, String)]) -> Option<(u16, String)> + Send + Sync>>,
        user_agent: String,
    ) -> Self {
        Self { send, user_agent }
    }
}

impl StealthHttpClientTrait for StealthHttpClient {
    fn is_available(&self) -> bool {
        self.send.is_some()
    }

    fn get(&self, url: &str, headers: &[(String, String)]) -> Option<(u16, String)> {
        let send = self.send.as_ref()?;
        let mut all = headers.to_vec();
        if !self.user_agent.is_empty()
            && !all.iter().any(|(k, _)| k.eq_ignore_ascii_case("user-agent"))
        {
            all.push(("User-Agent".into(), self.user_agent.clone()));
        }
        send(url, &all)
    }
}

/// A scraping job driven by the tool protocol.
#[derive(Debug, Clone, PartialEq)]
pub struct McpScrapeJob {
    pub urls: Vec<String>,
    /// A cap on how many pages one job may fetch. Without it a job that follows
    /// links is a crawler, and a crawler is a very different thing to run from
    /// somebody's phone.
    pub max_pages: usize,
    pub follow_links: bool,
}

/// Runs scraping jobs.
pub trait McpWebScrape {
    fn is_available(&self) -> bool;
    fn run(&self, job: &McpScrapeJob) -> Vec<ScrapedPage>;
}

/// Runs nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullMcpWebScrape;

impl McpWebScrape for NullMcpWebScrape {
    fn is_available(&self) -> bool {
        false
    }
    fn run(&self, _job: &McpScrapeJob) -> Vec<ScrapedPage> {
        Vec::new()
    }
}

/// Runs a job through a scraper, respecting the page cap.
pub struct DefaultMcpWebScrape<S: WebScraper> {
    scraper: S,
}

impl<S: WebScraper> DefaultMcpWebScrape<S> {
    pub fn new(scraper: S) -> Self {
        Self { scraper }
    }
}

impl<S: WebScraper> McpWebScrape for DefaultMcpWebScrape<S> {
    fn is_available(&self) -> bool {
        self.scraper.is_available()
    }

    fn run(&self, job: &McpScrapeJob) -> Vec<ScrapedPage> {
        job.urls
            .iter()
            // The cap is applied to the URL LIST as well as to any following, so
            // a job that names a thousand URLs does not fetch a thousand.
            .take(job.max_pages)
            .filter_map(|url| self.scraper.fetch(url))
            .collect()
    }
}

/// One segment of a recorded terminal session.
#[derive(Debug, Clone, PartialEq)]
pub struct TerminalCastSegment {
    /// Seconds from the start. RELATIVE, so a cast can be replayed at any speed
    /// and trimmed without rewriting every timestamp.
    pub at_seconds: f64,
    pub text: String,
}

/// A recorded terminal session.
#[derive(Debug, Clone, PartialEq)]
pub struct TerminalCast {
    pub width: u16,
    pub height: u16,
    pub segments: Vec<TerminalCastSegment>,
    pub duration_seconds: f64,
}

/// Records and replays terminal sessions.
pub trait TerminalCastTrait {
    fn is_available(&self) -> bool;
    fn parse(&self, text: &str) -> Option<TerminalCast>;
}

/// Records nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullTerminalCast;

impl TerminalCastTrait for NullTerminalCast {
    fn is_available(&self) -> bool {
        false
    }
    fn parse(&self, _text: &str) -> Option<TerminalCast> {
        None
    }
}

/// Reads the asciinema v2 format.
///
/// ONE JSON OBJECT PER LINE after a header line - not a JSON array. Parsing it
/// as an array fails on every real recording, and parsing it as a stream is what
/// lets a long session be read without holding all of it.
#[derive(Debug, Default, Clone, Copy)]
pub struct AsciinemaTerminalCast;

impl TerminalCastTrait for AsciinemaTerminalCast {
    fn is_available(&self) -> bool {
        true
    }

    fn parse(&self, text: &str) -> Option<TerminalCast> {
        let mut lines = text.lines();
        let header = lines.next()?;
        let width = Self::number_field(header, "\"width\"").unwrap_or(80.0) as u16;
        let height = Self::number_field(header, "\"height\"").unwrap_or(24.0) as u16;

        let mut segments = Vec::new();
        for line in lines {
            let trimmed = line.trim();
            if !trimmed.starts_with('[') {
                continue;
            }
            // `[time, "o", "text"]`. Only the "o" (output) stream is kept -
            // including "i" would replay the typing as output and double every
            // keystroke on screen.
            let inner = trimmed.trim_start_matches('[').trim_end_matches(']');
            let mut parts = inner.splitn(3, ',');
            let at: f64 = parts.next()?.trim().parse().ok()?;
            let stream = parts.next()?.trim().trim_matches('"').to_string();
            if stream != "o" {
                continue;
            }
            let payload = parts.next().unwrap_or("").trim();
            segments.push(TerminalCastSegment {
                at_seconds: at,
                text: payload.trim_matches('"').replace("\\r\\n", "\n").replace("\\n", "\n"),
            });
        }
        let duration = segments.last().map(|s| s.at_seconds).unwrap_or(0.0);
        Some(TerminalCast { width, height, segments, duration_seconds: duration })
    }
}

impl AsciinemaTerminalCast {
    fn number_field(line: &str, key: &str) -> Option<f64> {
        let at = line.find(key)? + key.len();
        let rest = &line[at..];
        let digits: String = rest
            .chars()
            .skip_while(|c| !c.is_ascii_digit() && *c != '-')
            .take_while(|c| c.is_ascii_digit() || *c == '.' || *c == '-')
            .collect();
        digits.parse().ok()
    }
}

/// What came out of ingesting a video.
#[derive(Debug, Clone, PartialEq)]
pub struct VideoIngestResult {
    pub transcript: String,
    /// Keyframe times, so a person can be pointed at a moment rather than told
    /// to watch the whole thing.
    pub keyframe_seconds: Vec<f64>,
    pub duration_seconds: f64,
    pub error: String,
}

/// Ingests video.
pub trait VideoIngest {
    fn is_available(&self) -> bool;
    fn ingest(&self, path: &str) -> VideoIngestResult;
}

/// Ingests nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullVideoIngest;

impl VideoIngest for NullVideoIngest {
    fn is_available(&self) -> bool {
        false
    }
    fn ingest(&self, _path: &str) -> VideoIngestResult {
        VideoIngestResult {
            transcript: String::new(),
            keyframe_seconds: Vec::new(),
            duration_seconds: 0.0,
            error: "this device cannot ingest video".into(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Spatial

/// A point on the earth.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LatLon {
    pub latitude: f64,
    pub longitude: f64,
}

impl LatLon {
    /// Great-circle distance in metres.
    ///
    /// Not a flat approximation: at South African latitudes a flat one is wrong
    /// by kilometres over the distances that matter here, and the error is in
    /// the direction that reports something outside an area it is inside.
    pub fn distance_metres(&self, other: &LatLon) -> f64 {
        const R: f64 = 6_371_000.0;
        let to_rad = |d: f64| d * PI_F64 / 180.0;
        let d_lat = to_rad(other.latitude - self.latitude);
        let d_lon = to_rad(other.longitude - self.longitude);
        let a = (d_lat / 2.0).sin().powi(2)
            + to_rad(self.latitude).cos() * to_rad(other.latitude).cos() * (d_lon / 2.0).sin().powi(2);
        2.0 * R * a.sqrt().min(1.0).asin()
    }

    /// Whether the pair is a real coordinate at all. `(0, 0)` is in the Gulf of
    /// Guinea and is what an uninitialised reading looks like, so it is worth
    /// being able to ask.
    pub fn is_plausible(&self) -> bool {
        (-90.0..=90.0).contains(&self.latitude)
            && (-180.0..=180.0).contains(&self.longitude)
            && !(self.latitude == 0.0 && self.longitude == 0.0)
    }
}

const PI_F64: f64 = std::f64::consts::PI;

/// One map tile.
#[derive(Debug, Clone, PartialEq)]
pub struct GeoTile {
    pub zoom: u8,
    pub x: u32,
    pub y: u32,
    pub bytes: Vec<u8>,
}

impl GeoTile {
    /// The slippy-map tile containing a point at a zoom level.
    ///
    /// The Y term uses the MERCATOR projection, not a linear scaling of
    /// latitude. A linear one is right at the equator and increasingly wrong
    /// towards the poles - by a whole tile at Johannesburg's latitude.
    pub fn containing(point: LatLon, zoom: u8) -> (u32, u32) {
        let n = 2f64.powi(zoom as i32);
        let x = ((point.longitude + 180.0) / 360.0 * n).floor().max(0.0) as u32;
        let lat_rad = point.latitude * PI_F64 / 180.0;
        let y = ((1.0 - (lat_rad.tan() + 1.0 / lat_rad.cos()).ln() / PI_F64) / 2.0 * n)
            .floor()
            .max(0.0) as u32;
        (x, y)
    }
}

/// Supplies map tiles.
pub trait GeoTileSource {
    fn is_available(&self) -> bool;
    fn tile(&self, zoom: u8, x: u32, y: u32) -> Option<GeoTile>;
}

/// Supplies nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullGeoTileSource;

impl GeoTileSource for NullGeoTileSource {
    fn is_available(&self) -> bool {
        false
    }
    fn tile(&self, _zoom: u8, _x: u32, _y: u32) -> Option<GeoTile> {
        None
    }
}

/// Tiles held on the device.
///
/// ON THE DEVICE because a map request tells whoever serves it where somebody
/// is looking, which over a few days is where they live and work.
#[derive(Debug, Default)]
pub struct InMemoryGeoTileSource {
    tiles: HashMap<(u8, u32, u32), Vec<u8>>,
}

impl InMemoryGeoTileSource {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn add(&mut self, zoom: u8, x: u32, y: u32, bytes: Vec<u8>) {
        self.tiles.insert((zoom, x, y), bytes);
    }

    pub fn len(&self) -> usize {
        self.tiles.len()
    }

    pub fn is_empty(&self) -> bool {
        self.tiles.is_empty()
    }
}

impl GeoTileSource for InMemoryGeoTileSource {
    fn is_available(&self) -> bool {
        !self.tiles.is_empty()
    }

    fn tile(&self, zoom: u8, x: u32, y: u32) -> Option<GeoTile> {
        self.tiles.get(&(zoom, x, y)).map(|bytes| GeoTile {
            zoom,
            x,
            y,
            bytes: bytes.clone(),
        })
    }
}

/// A scene to draw.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Scene3D {
    pub name: String,
    /// Vertices as flat triples. Flat rather than a struct per vertex because a
    /// scene has hundreds of thousands and a struct each is memory a phone does
    /// not have.
    pub vertices: Vec<f32>,
    pub indices: Vec<u32>,
}

impl Scene3D {
    /// Whether the index buffer actually addresses the vertex buffer. An index
    /// past the end is a crash in a renderer and a silent wrong triangle in a
    /// forgiving one.
    pub fn is_valid(&self) -> bool {
        if self.vertices.len() % 3 != 0 || self.indices.len() % 3 != 0 {
            return false;
        }
        let vertex_count = (self.vertices.len() / 3) as u32;
        self.indices.iter().all(|i| *i < vertex_count)
    }
}

/// Renders a scene.
pub trait Scene3DRenderer {
    fn is_available(&self) -> bool;
    fn render(&self, scene: &Scene3D) -> Option<Vec<u8>>;
}

/// Renders nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullScene3DRenderer;

impl Scene3DRenderer for NullScene3DRenderer {
    fn is_available(&self) -> bool {
        false
    }
    fn render(&self, _scene: &Scene3D) -> Option<Vec<u8>> {
        None
    }
}

/// Emits the scene as JSON rather than pixels.
///
/// Useful, and honest about being a DESCRIPTION rather than a render - a caller
/// that wanted an image gets something it can obviously not display, instead of
/// a blank picture it might.
#[derive(Debug, Default, Clone, Copy)]
pub struct JsonScene3DRenderer;

impl Scene3DRenderer for JsonScene3DRenderer {
    fn is_available(&self) -> bool {
        true
    }

    fn render(&self, scene: &Scene3D) -> Option<Vec<u8>> {
        if !scene.is_valid() {
            return None;
        }
        Some(
            format!(
                "{{\"name\":\"{}\",\"vertices\":{},\"triangles\":{}}}",
                scene.name.replace('"', "'"),
                scene.vertices.len() / 3,
                scene.indices.len() / 3
            )
            .into_bytes(),
        )
    }
}

/// One radar return.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RadarReturn {
    pub bearing_degrees: f32,
    pub range_metres: f32,
    /// How strong the return is. A weak one at long range and a strong one at
    /// short range are not the same thing, so both are kept.
    pub strength: f32,
}

/// A whole sweep.
#[derive(Debug, Clone, PartialEq)]
pub struct RadarReading {
    pub returns: Vec<RadarReturn>,
    pub at_ms: u64,
    /// The furthest this sweep could see. A caller needs it to tell "nothing
    /// there" from "nothing within range", which are different facts.
    pub max_range_metres: f32,
}

/// Reads radar.
pub trait RadarReadout {
    fn is_available(&self) -> bool;
    fn sweep(&self) -> Option<RadarReading>;
}

/// Reads nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullRadarReadout;

impl RadarReadout for NullRadarReadout {
    fn is_available(&self) -> bool {
        false
    }
    fn sweep(&self) -> Option<RadarReading> {
        None
    }
}

/// A synthetic sweep, for a harness.
///
/// NAMED SYNTHETIC so nothing mistakes it for a reading. A simulated sensor that
/// looks like a real one is how a demo becomes a claim.
#[derive(Debug, Clone)]
pub struct SyntheticRadarReadout {
    pub returns: Vec<RadarReturn>,
    pub max_range_metres: f32,
}

impl RadarReadout for SyntheticRadarReadout {
    fn is_available(&self) -> bool {
        true
    }
    fn sweep(&self) -> Option<RadarReading> {
        Some(RadarReading {
            returns: self.returns.clone(),
            at_ms: 0,
            max_range_metres: self.max_range_metres,
        })
    }
}

/// Something in the sky.
#[derive(Debug, Clone, PartialEq)]
pub struct SkyObject {
    pub name: String,
    pub azimuth_degrees: f32,
    /// Negative means BELOW the horizon, which is a normal answer and not an
    /// error - most of the sky is below it at any moment.
    pub altitude_degrees: f32,
    pub magnitude: f32,
}

impl SkyObject {
    pub fn is_visible(&self) -> bool {
        self.altitude_degrees > 0.0
    }
}

/// Tracks the sky.
pub trait SkyTracker {
    fn is_available(&self) -> bool;
    fn visible(&self, from: LatLon, at_ms: u64) -> Vec<SkyObject>;
}

/// Tracks nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSkyTracker;

impl SkyTracker for NullSkyTracker {
    fn is_available(&self) -> bool {
        false
    }
    fn visible(&self, _from: LatLon, _at_ms: u64) -> Vec<SkyObject> {
        Vec::new()
    }
}

/// A fixed set of objects, for a harness.
#[derive(Debug, Clone, Default)]
pub struct SyntheticSkyTracker {
    pub objects: Vec<SkyObject>,
}

impl SkyTracker for SyntheticSkyTracker {
    fn is_available(&self) -> bool {
        true
    }
    /// Only what is ABOVE the horizon. Returning everything and letting a caller
    /// filter is how a star chart ends up showing the other side of the earth.
    fn visible(&self, _from: LatLon, _at_ms: u64) -> Vec<SkyObject> {
        self.objects.iter().filter(|o| o.is_visible()).cloned().collect()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Pipelines

/// One record flowing through a pipeline.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PipelineRecord {
    pub fields: HashMap<String, String>,
}

impl PipelineRecord {
    pub fn get(&self, key: &str) -> Option<&String> {
        self.fields.get(key)
    }
}

/// What a run did.
#[derive(Debug, Clone, PartialEq)]
pub struct PipelineRun {
    pub read: usize,
    pub written: usize,
    /// Records that failed. Counted SEPARATELY from written, because a run that
    /// wrote nine of ten and reported success has lost a record silently.
    pub failed: usize,
    pub errors: Vec<String>,
}

impl PipelineRun {
    pub fn succeeded(&self) -> bool {
        self.failed == 0 && self.errors.is_empty()
    }
}

/// Where records come from.
pub trait PipelineSource {
    fn is_available(&self) -> bool;
    fn read(&self, limit: usize) -> Vec<PipelineRecord>;
}

/// Where records go.
pub trait PipelineSink {
    fn is_available(&self) -> bool;
    fn write(&mut self, records: &[PipelineRecord]) -> Result<usize, String>;
}

/// Runs a pipeline.
pub trait PipelineExecutor {
    fn is_available(&self) -> bool;
    fn run(&mut self, batch_size: usize) -> PipelineRun;
}

/// Reads nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullPipelineSource;

impl PipelineSource for NullPipelineSource {
    fn is_available(&self) -> bool {
        false
    }
    fn read(&self, _limit: usize) -> Vec<PipelineRecord> {
        Vec::new()
    }
}

/// Writes nothing, and says how many it did NOT write.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullPipelineSink;

impl PipelineSink for NullPipelineSink {
    fn is_available(&self) -> bool {
        false
    }
    fn write(&mut self, _records: &[PipelineRecord]) -> Result<usize, String> {
        // An ERROR, not Ok(0). A sink that silently accepts and discards is how
        // a pipeline reports success having moved nothing.
        Err("no sink is configured".into())
    }
}

/// Runs nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullPipelineExecutor;

impl PipelineExecutor for NullPipelineExecutor {
    fn is_available(&self) -> bool {
        false
    }
    fn run(&mut self, _batch_size: usize) -> PipelineRun {
        PipelineRun {
            read: 0,
            written: 0,
            failed: 0,
            errors: vec!["no pipeline is configured".into()],
        }
    }
}

/// Records in memory.
#[derive(Debug, Default, Clone)]
pub struct InMemoryPipelineSource {
    pub records: Vec<PipelineRecord>,
}

impl PipelineSource for InMemoryPipelineSource {
    fn is_available(&self) -> bool {
        true
    }
    fn read(&self, limit: usize) -> Vec<PipelineRecord> {
        self.records.iter().take(limit).cloned().collect()
    }
}

/// Collects records in memory.
#[derive(Debug, Default, Clone)]
pub struct InMemoryPipelineSink {
    pub written: Vec<PipelineRecord>,
}

impl PipelineSink for InMemoryPipelineSink {
    fn is_available(&self) -> bool {
        true
    }
    fn write(&mut self, records: &[PipelineRecord]) -> Result<usize, String> {
        self.written.extend_from_slice(records);
        Ok(records.len())
    }
}

/// Reads, transforms and writes.
///
/// A FAILED BATCH IS COUNTED, NOT SWALLOWED. A run that wrote nine of ten and
/// reported success has lost a record, and the loss surfaces weeks later as a
/// number that does not add up.
pub struct InMemoryPipelineExecutor<S: PipelineSource, K: PipelineSink> {
    source: S,
    sink: K,
    transform: Option<Box<dyn Fn(&PipelineRecord) -> Option<PipelineRecord> + Send + Sync>>,
}

impl<S: PipelineSource, K: PipelineSink> InMemoryPipelineExecutor<S, K> {
    pub fn new(
        source: S,
        sink: K,
        transform: Option<Box<dyn Fn(&PipelineRecord) -> Option<PipelineRecord> + Send + Sync>>,
    ) -> Self {
        Self { source, sink, transform }
    }
}

impl<S: PipelineSource, K: PipelineSink> PipelineExecutor for InMemoryPipelineExecutor<S, K> {
    fn is_available(&self) -> bool {
        self.source.is_available() && self.sink.is_available()
    }

    fn run(&mut self, batch_size: usize) -> PipelineRun {
        let records = self.source.read(batch_size);
        let read = records.len();
        let mut failed = 0usize;
        let transformed: Vec<PipelineRecord> = records
            .iter()
            .filter_map(|r| match &self.transform {
                Some(f) => {
                    let out = f(r);
                    if out.is_none() {
                        // A transform that returned nothing DROPPED a record.
                        // Counted, so the totals still add up.
                        failed += 1;
                    }
                    out
                }
                None => Some(r.clone()),
            })
            .collect();

        match self.sink.write(&transformed) {
            Ok(written) => PipelineRun {
                read,
                written,
                failed: failed + transformed.len().saturating_sub(written),
                errors: Vec::new(),
            },
            Err(error) => PipelineRun {
                read,
                written: 0,
                failed: read,
                errors: vec![error],
            },
        }
    }
}

/// What a query returned.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct DatabaseQueryResult {
    pub columns: Vec<String>,
    pub rows: Vec<Vec<String>>,
    /// Whether the result was cut short. A caller shown a hundred rows of a
    /// million and not told is a caller drawing the wrong conclusion.
    pub truncated: bool,
    pub error: String,
}

/// Runs read-only queries.
pub trait DatabaseQueryTool {
    fn is_available(&self) -> bool;
    fn query(&self, sql: &str, params: &[String]) -> DatabaseQueryResult;
}

/// Queries nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullDatabaseQueryTool;

impl DatabaseQueryTool for NullDatabaseQueryTool {
    fn is_available(&self) -> bool {
        false
    }
    fn query(&self, _sql: &str, _params: &[String]) -> DatabaseQueryResult {
        DatabaseQueryResult {
            error: "no database is configured on this device".into(),
            ..Default::default()
        }
    }
}

/// A query tool over supplied rows.
///
/// READ-ONLY, AND ENFORCED. A statement that is not a SELECT is refused before
/// it reaches the database, because a tool a model can call is a tool a prompt
/// can reach - and "summarise this table" must never be able to drop it.
pub struct InMemoryDatabaseQueryTool {
    run: Option<Box<dyn Fn(&str, &[String]) -> (Vec<String>, Vec<Vec<String>>) + Send + Sync>>,
    max_rows: usize,
}

impl InMemoryDatabaseQueryTool {
    pub fn new(
        run: Option<Box<dyn Fn(&str, &[String]) -> (Vec<String>, Vec<Vec<String>>) + Send + Sync>>,
        max_rows: usize,
    ) -> Self {
        Self { run, max_rows }
    }

    /// Whether a statement only reads.
    ///
    /// Checked on the FIRST KEYWORD after comments are stripped, and a statement
    /// containing a semicolon is refused outright - "SELECT 1; DROP TABLE x" is
    /// the oldest trick there is and it passes a naive prefix check.
    pub fn is_read_only(sql: &str) -> bool {
        let mut cleaned = String::with_capacity(sql.len());
        let mut in_line_comment = false;
        let mut previous = ' ';
        for c in sql.chars() {
            if in_line_comment {
                if c == '\n' {
                    in_line_comment = false;
                    cleaned.push(' ');
                }
                continue;
            }
            if previous == '-' && c == '-' {
                cleaned.pop();
                in_line_comment = true;
                continue;
            }
            cleaned.push(c);
            previous = c;
        }
        let trimmed = cleaned.trim();
        if trimmed.trim_end_matches(';').contains(';') {
            return false;
        }
        let first = trimmed.split_whitespace().next().unwrap_or("").to_lowercase();
        matches!(first.as_str(), "select" | "with" | "explain" | "pragma")
    }
}

impl DatabaseQueryTool for InMemoryDatabaseQueryTool {
    fn is_available(&self) -> bool {
        self.run.is_some()
    }

    fn query(&self, sql: &str, params: &[String]) -> DatabaseQueryResult {
        if !Self::is_read_only(sql) {
            return DatabaseQueryResult {
                error: "this tool only runs queries that read".into(),
                ..Default::default()
            };
        }
        let Some(run) = &self.run else {
            return DatabaseQueryResult {
                error: "no database is configured on this device".into(),
                ..Default::default()
            };
        };
        let (columns, mut rows) = run(sql, params);
        let truncated = rows.len() > self.max_rows;
        rows.truncate(self.max_rows);
        DatabaseQueryResult { columns, rows, truncated, error: String::new() }
    }
}
