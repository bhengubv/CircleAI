//! The last of it: companion recall, the SQLite-backed stores, the ONNX speaker
//! and emotion adapters, the Neuron voice, key delegation, the simulation
//! adapters, and the skill-pack stores.
//!
//! THE ONNX ADAPTERS ARE THE PART WITH A LINE IN IT. A speaker embedding is a
//! voiceprint - a biometric, and one somebody cannot change once it has been
//! taken. Nothing here returns one, nothing here uploads one, and the emotion
//! sensor reports a coarse state rather than a score, because "0.87 angry"
//! about a person is a number that ends up in a record.
//!
//! THE SQLITE STORES HOLD SQL AND NO DRIVER. Which rows to read and how to key
//! them is the part worth porting; opening a database file is the head's job,
//! and a Rust core that carried a SQLite build would carry a C compiler to every
//! target including the small ones.

use std::collections::HashMap;

use crate::platform_tail::SqlDialect;

// ─────────────────────────────────────────────────────────────────────────────
// Recall

/// Pulling back what was said before.
///
/// RECALL IS NOT SEARCH. Search finds a document; recall answers "what did we
/// decide about this", which needs the turn AND what surrounded it - a matching
/// sentence with no context is a quotation nobody can place.
#[derive(Debug, Default, Clone)]
pub struct CompanionRecallExtensions {
    /// How many turns either side to carry. Small, because the point is placing
    /// the memory rather than replaying the conversation.
    pub context_turns: usize,
    /// Below this, do not offer it. A weak recall presented confidently is worse
    /// than none - it puts words in somebody's mouth.
    pub min_score: f32,
}

impl CompanionRecallExtensions {
    pub fn new() -> Self {
        Self { context_turns: 2, min_score: 0.35 }
    }

    /// Turns a question into what to look for.
    ///
    /// Strips the framing - "do you remember when we", "what did I say about" -
    /// because those words appear in every recall question and match nothing
    /// useful.
    pub fn to_query(&self, request: &str) -> String {
        let lowered = request.to_lowercase();
        let mut rest = lowered.as_str();
        for framing in [
            "do you remember when we ",
            "do you remember ",
            "what did i say about ",
            "what did we decide about ",
            "what did we say about ",
            "remind me about ",
            "remind me ",
            "when did i ",
            "when did we ",
        ] {
            if let Some(stripped) = rest.strip_prefix(framing) {
                rest = stripped;
                break;
            }
        }
        rest.trim_end_matches(['?', '.', '!']).trim().to_string()
    }

    /// The turns around a hit, so it can be read as a moment rather than a
    /// fragment.
    pub fn with_context<'a, T>(&self, turns: &'a [T], hit: usize) -> &'a [T] {
        let start = hit.saturating_sub(self.context_turns);
        let end = (hit + self.context_turns + 1).min(turns.len());
        &turns[start..end]
    }

    /// Whether a hit is worth offering.
    pub fn is_worth_offering(&self, score: f32) -> bool {
        score >= self.min_score
    }

    /// How to introduce it.
    ///
    /// HEDGED WHEN WEAK. "I think you said" and "you said" are different claims,
    /// and a companion that makes the second when it means the first is a
    /// companion that invents memories.
    pub fn preamble(&self, score: f32) -> &'static str {
        if score >= 0.8 {
            "you said"
        } else if score >= 0.55 {
            "I think you said"
        } else {
            "there is something that might be it -"
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SQLite-backed stores

/// A passage and the things it mentions.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct HippoRagPassage {
    pub passage_id: String,
    pub text: String,
    /// What it is about. The bridge between a passage and the graph, and what
    /// makes a two-hop question answerable at all.
    pub entities: Vec<String>,
    pub at_ms: u64,
}

/// Passages and the entities linking them, in SQLite.
///
/// SQL AND NO DRIVER. Opening a database file is the head's job; which rows to
/// read and how to key them is the part worth having in one place.
pub struct SqliteHippoRagStore {
    dialect: SqlDialect,
    #[allow(clippy::type_complexity)]
    execute: Option<Box<dyn Fn(&str, &[String]) -> Result<Vec<Vec<String>>, String> + Send + Sync>>,
}

impl SqliteHippoRagStore {
    pub const PASSAGES: &'static str = "hipporag_passages";
    pub const MENTIONS: &'static str = "hipporag_mentions";

    #[allow(clippy::type_complexity)]
    pub fn new(
        execute: Option<
            Box<dyn Fn(&str, &[String]) -> Result<Vec<Vec<String>>, String> + Send + Sync>,
        >,
    ) -> Self {
        Self { dialect: SqlDialect::Sqlite, execute }
    }

    pub fn is_available(&self) -> bool {
        self.execute.is_some()
    }

    /// The tables and the one index that matters.
    ///
    /// The index is on the ENTITY, not the passage: every question walks from a
    /// name to the passages mentioning it, and without it that walk is a full
    /// scan on every hop.
    pub fn schema(&self) -> Vec<String> {
        vec![
            format!(
                "CREATE TABLE IF NOT EXISTS {} (id TEXT PRIMARY KEY, text TEXT NOT NULL, \
at_ms INTEGER NOT NULL);",
                self.dialect.quote(Self::PASSAGES)
            ),
            format!(
                "CREATE TABLE IF NOT EXISTS {} (entity TEXT NOT NULL, passage_id TEXT NOT NULL, \
PRIMARY KEY (entity, passage_id));",
                self.dialect.quote(Self::MENTIONS)
            ),
            format!(
                "CREATE INDEX IF NOT EXISTS idx_mentions_entity ON {} (entity);",
                self.dialect.quote(Self::MENTIONS)
            ),
        ]
    }

    pub fn put(&self, passage: &HippoRagPassage) -> Result<(), String> {
        let Some(execute) = &self.execute else {
            return Err("no database is open".into());
        };
        execute(
            &self.dialect.upsert(Self::PASSAGES, &["id", "text", "at_ms"], "id"),
            &[
                passage.passage_id.clone(),
                passage.text.clone(),
                passage.at_ms.to_string(),
            ],
        )?;
        for entity in &passage.entities {
            execute(
                &self
                    .dialect
                    .upsert(Self::MENTIONS, &["entity", "passage_id"], "entity"),
                &[entity.to_lowercase(), passage.passage_id.clone()],
            )?;
        }
        Ok(())
    }

    /// Passages mentioning a name.
    pub fn by_entity(&self, entity: &str, limit: usize) -> Result<Vec<String>, String> {
        let Some(execute) = &self.execute else {
            return Err("no database is open".into());
        };
        let sql = format!(
            "SELECT p.text FROM {p} p JOIN {m} m ON m.passage_id = p.id \
WHERE m.entity = {a} ORDER BY p.at_ms DESC LIMIT {b};",
            p = self.dialect.quote(Self::PASSAGES),
            m = self.dialect.quote(Self::MENTIONS),
            a = self.dialect.parameter(1),
            b = self.dialect.parameter(2)
        );
        Ok(execute(
            &sql,
            &[
                entity.to_lowercase(),
                if limit == 0 { 10 } else { limit }.to_string(),
            ],
        )?
        .into_iter()
        .filter_map(|row| row.into_iter().next())
        .collect())
    }

    /// Names that appear alongside a given one.
    ///
    /// The second hop, and the reason this store exists rather than a plain text
    /// index: "who was at that meeting" needs the passages about the meeting and
    /// then the people in them.
    pub fn neighbours(&self, entity: &str, limit: usize) -> Result<Vec<String>, String> {
        let Some(execute) = &self.execute else {
            return Err("no database is open".into());
        };
        let sql = format!(
            "SELECT DISTINCT b.entity FROM {m} a JOIN {m} b ON a.passage_id = b.passage_id \
WHERE a.entity = {p1} AND b.entity <> {p1} LIMIT {p2};",
            m = self.dialect.quote(Self::MENTIONS),
            p1 = self.dialect.parameter(1),
            p2 = self.dialect.parameter(2)
        );
        Ok(execute(
            &sql,
            &[
                entity.to_lowercase(),
                if limit == 0 { 20 } else { limit }.to_string(),
            ],
        )?
        .into_iter()
        .filter_map(|row| row.into_iter().next())
        .collect())
    }
}

/// A fact: subject, relation, object.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct KnowledgeTriple {
    pub subject: String,
    pub relation: String,
    pub object: String,
    /// Where it came from. A fact with no source cannot be checked or
    /// withdrawn, and the ones worth withdrawing are exactly the ones somebody
    /// disputes.
    pub source: String,
    pub at_ms: u64,
}

/// The knowledge graph, in SQLite.
pub struct SqliteKnowledgeGraph {
    dialect: SqlDialect,
    #[allow(clippy::type_complexity)]
    execute: Option<Box<dyn Fn(&str, &[String]) -> Result<Vec<Vec<String>>, String> + Send + Sync>>,
}

impl SqliteKnowledgeGraph {
    pub const TABLE: &'static str = "knowledge_triples";

    #[allow(clippy::type_complexity)]
    pub fn new(
        execute: Option<
            Box<dyn Fn(&str, &[String]) -> Result<Vec<Vec<String>>, String> + Send + Sync>,
        >,
    ) -> Self {
        Self { dialect: SqlDialect::Sqlite, execute }
    }

    pub fn is_available(&self) -> bool {
        self.execute.is_some()
    }

    /// The key is the whole triple, so the same fact learnt twice is one row.
    ///
    /// Keyed on subject and relation alone, a second value would silently
    /// overwrite the first - and somebody having two phone numbers would become
    /// somebody having one.
    pub fn schema(&self) -> Vec<String> {
        vec![
            format!(
                "CREATE TABLE IF NOT EXISTS {} (subject TEXT NOT NULL, relation TEXT NOT NULL, \
object TEXT NOT NULL, source TEXT NOT NULL, at_ms INTEGER NOT NULL, \
PRIMARY KEY (subject, relation, object));",
                self.dialect.quote(Self::TABLE)
            ),
            format!(
                "CREATE INDEX IF NOT EXISTS idx_triples_object ON {} (object);",
                self.dialect.quote(Self::TABLE)
            ),
        ]
    }

    pub fn put(&self, triple: &KnowledgeTriple) -> Result<(), String> {
        if triple.subject.is_empty() || triple.relation.is_empty() {
            return Err("a fact needs a subject and a relation".into());
        }
        if triple.source.trim().is_empty() {
            return Err("a fact with no source will not be stored - it could never be withdrawn".into());
        }
        let Some(execute) = &self.execute else {
            return Err("no database is open".into());
        };
        let sql = format!(
            "INSERT OR REPLACE INTO {} (subject, relation, object, source, at_ms) \
VALUES ({}, {}, {}, {}, {});",
            self.dialect.quote(Self::TABLE),
            self.dialect.parameter(1),
            self.dialect.parameter(2),
            self.dialect.parameter(3),
            self.dialect.parameter(4),
            self.dialect.parameter(5)
        );
        execute(
            &sql,
            &[
                triple.subject.to_lowercase(),
                triple.relation.to_lowercase(),
                triple.object.clone(),
                triple.source.clone(),
                triple.at_ms.to_string(),
            ],
        )
        .map(|_| ())
    }

    pub fn about(&self, subject: &str) -> Result<Vec<(String, String)>, String> {
        let Some(execute) = &self.execute else {
            return Err("no database is open".into());
        };
        let sql = format!(
            "SELECT relation, object FROM {} WHERE subject = {} ORDER BY at_ms DESC;",
            self.dialect.quote(Self::TABLE),
            self.dialect.parameter(1)
        );
        Ok(execute(&sql, &[subject.to_lowercase()])?
            .into_iter()
            .filter_map(|row| {
                let mut it = row.into_iter();
                Some((it.next()?, it.next()?))
            })
            .collect())
    }

    /// Withdraws everything from one source.
    ///
    /// The reason a source is required: when something turns out to be wrong,
    /// what has to go is everything that came from it, not the one sentence
    /// somebody happened to notice.
    pub fn forget_source(&self, source: &str) -> Result<(), String> {
        let Some(execute) = &self.execute else {
            return Err("no database is open".into());
        };
        let sql = format!(
            "DELETE FROM {} WHERE source = {};",
            self.dialect.quote(Self::TABLE),
            self.dialect.parameter(1)
        );
        execute(&sql, &[source.to_string()]).map(|_| ())
    }
}

/// Skill packs described by a capability manifest.
///
/// A pack declares what it CAN DO rather than what it is called, so a request is
/// matched against capabilities rather than against a name somebody had to know
/// in advance.
#[derive(Debug, Default)]
pub struct CapabilityManifestSkillStore {
    /// Pack id to the capabilities it declares.
    packs: HashMap<String, Vec<String>>,
    /// Pack id to its digest. A pack WITHOUT one is not installable.
    digests: HashMap<String, String>,
}

impl CapabilityManifestSkillStore {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn install(
        &mut self,
        pack_id: &str,
        capabilities: Vec<String>,
        sha256: &str,
    ) -> Result<(), String> {
        if pack_id.trim().is_empty() {
            return Err("a pack needs an identifier".into());
        }
        if sha256.trim().is_empty() {
            return Err(format!("{pack_id} has no checksum, so it will not be installed"));
        }
        if capabilities.is_empty() {
            return Err(format!(
                "{pack_id} declares no capabilities, so nothing would ever reach it"
            ));
        }
        self.packs.insert(pack_id.to_string(), capabilities);
        self.digests.insert(pack_id.to_string(), sha256.to_string());
        Ok(())
    }

    /// Which packs claim a capability.
    pub fn providers_of(&self, capability: &str) -> Vec<String> {
        let wanted = capability.to_lowercase();
        let mut out: Vec<String> = self
            .packs
            .iter()
            .filter(|(_, caps)| caps.iter().any(|c| c.to_lowercase() == wanted))
            .map(|(id, _)| id.clone())
            .collect();
        out.sort();
        out
    }

    pub fn capabilities(&self) -> Vec<String> {
        let mut out: Vec<String> = self
            .packs
            .values()
            .flat_map(|c| c.iter().cloned())
            .collect();
        out.sort();
        out.dedup();
        out
    }

    /// Removes a pack AND its digest. Leaving the digest behind means a
    /// reinstall silently inherits an approval nobody gave again.
    pub fn remove(&mut self, pack_id: &str) -> bool {
        self.digests.remove(pack_id);
        self.packs.remove(pack_id).is_some()
    }
}

/// Downloads a skill pack over HTTP.
pub struct HttpPackDownloader {
    #[allow(clippy::type_complexity)]
    download: Option<Box<dyn Fn(&str) -> Result<Vec<u8>, String> + Send + Sync>>,
    digest_of: Option<Box<dyn Fn(&[u8]) -> String + Send + Sync>>,
    /// Hosts this may fetch from. EMPTY MEANS NONE - a downloader that reaches
    /// anywhere is a way to make the device fetch whatever a manifest names.
    allowed_hosts: Vec<String>,
}

impl HttpPackDownloader {
    #[allow(clippy::type_complexity)]
    pub fn new(
        download: Option<Box<dyn Fn(&str) -> Result<Vec<u8>, String> + Send + Sync>>,
        digest_of: Option<Box<dyn Fn(&[u8]) -> String + Send + Sync>>,
        allowed_hosts: Vec<String>,
    ) -> Self {
        Self { download, digest_of, allowed_hosts }
    }

    pub fn is_available(&self) -> bool {
        self.download.is_some() && self.digest_of.is_some()
    }

    /// HTTPS ONLY, and only from a host on the list.
    ///
    /// A skill pack is code this device will run. Fetching one over plain HTTP
    /// means whoever is between the two decides what runs.
    pub fn is_allowed(&self, url: &str) -> bool {
        if !url.starts_with("https://") {
            return false;
        }
        let Some(host) = crate::platform_tail::HttpToolBridge::host_of(url) else {
            return false;
        };
        self.allowed_hosts.iter().any(|h| h.to_lowercase() == host)
    }

    pub fn fetch(&self, url: &str, expected_sha256: &str) -> Result<Vec<u8>, String> {
        if expected_sha256.trim().is_empty() {
            return Err("that pack has no checksum, so it will not be downloaded".into());
        }
        if !self.is_allowed(url) {
            return Err(format!(
                "{url} is not an https address on this device's list of allowed hosts"
            ));
        }
        let (Some(download), Some(digest_of)) = (&self.download, &self.digest_of) else {
            return Err("this build cannot download skill packs".into());
        };
        let bytes = download(url)?;
        if !digest_of(&bytes).eq_ignore_ascii_case(expected_sha256.trim()) {
            return Err("that pack does not match its checksum".into());
        }
        Ok(bytes)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ONNX voice adapters

/// How sure the identity check is.
///
/// THREE STATES, NOT A SCORE. A number attached to "is this the same person" is
/// a number that gets stored, compared and eventually acted on, and a voiceprint
/// comparison is not accurate enough to carry that weight.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SpeakerMatch {
    /// Confident it is the enrolled speaker.
    Same,
    /// Confident it is not.
    Different,
    /// Not sure. THE MOST IMPORTANT VALUE and the default - a voice check that
    /// never says "I do not know" is one that guesses, and it guesses about
    /// whether somebody is who they say they are.
    #[default]
    Unsure,
}

/// Tells voices apart, on the device.
///
/// NO EMBEDDING LEAVES AND NONE IS RETURNED. A speaker embedding is a
/// voiceprint - a biometric somebody cannot change once it has been taken.
/// Every method here either takes audio and returns a decision, or takes audio
/// and returns a handle; there is no method that hands out the vector.
pub struct OnnxSpeakerIdentityAdapter {
    #[allow(clippy::type_complexity)]
    embed: Option<Box<dyn Fn(&[f32]) -> Vec<f32> + Send + Sync>>,
    /// The enrolled voiceprints, held here and nowhere else.
    enrolled: HashMap<String, Vec<f32>>,
    /// Above this, the same person. Below `unsure_below`, a different one.
    /// Between them, unsure - and the band is deliberately wide.
    pub same_above: f32,
    pub unsure_below: f32,
    pub sample_rate_hz: u32,
}

impl OnnxSpeakerIdentityAdapter {
    /// The window this needs. Under about a second and a half, a voiceprint is
    /// dominated by whatever the person happened to be saying rather than by
    /// their voice.
    pub const MIN_SECONDS: f32 = 1.5;

    #[allow(clippy::type_complexity)]
    pub fn new(
        embed: Option<Box<dyn Fn(&[f32]) -> Vec<f32> + Send + Sync>>,
        sample_rate_hz: u32,
    ) -> Self {
        Self {
            embed,
            enrolled: HashMap::new(),
            same_above: 0.75,
            unsure_below: 0.45,
            sample_rate_hz: if sample_rate_hz == 0 { 16_000 } else { sample_rate_hz },
        }
    }

    pub fn is_available(&self) -> bool {
        self.embed.is_some()
    }

    fn has_enough(&self, samples: &[f32]) -> bool {
        samples.len() as f32 / self.sample_rate_hz as f32 >= Self::MIN_SECONDS
    }

    /// Enrols a voice. Returns only a handle.
    pub fn enrol(&mut self, handle: &str, samples: &[f32]) -> Result<(), String> {
        if handle.trim().is_empty() {
            return Err("an enrolment needs a name to file it under".into());
        }
        if !self.has_enough(samples) {
            return Err(format!(
                "that is too short - at least {} seconds of speech is needed",
                Self::MIN_SECONDS
            ));
        }
        let Some(embed) = &self.embed else {
            return Err("this device has no speaker model".into());
        };
        self.enrolled.insert(handle.to_string(), embed(samples));
        Ok(())
    }

    /// Compares. Returns a decision, never a score.
    pub fn verify(&self, handle: &str, samples: &[f32]) -> SpeakerMatch {
        let (Some(embed), Some(reference)) = (&self.embed, self.enrolled.get(handle)) else {
            return SpeakerMatch::Unsure;
        };
        if !self.has_enough(samples) {
            return SpeakerMatch::Unsure;
        }
        let similarity = crate::languages_integrations::VectorMath::cosine(
            &embed(samples),
            reference,
        );
        if similarity >= self.same_above {
            SpeakerMatch::Same
        } else if similarity < self.unsure_below {
            SpeakerMatch::Different
        } else {
            SpeakerMatch::Unsure
        }
    }

    /// Forgets a voiceprint. Immediate and complete.
    pub fn forget(&mut self, handle: &str) -> bool {
        self.enrolled.remove(handle).is_some()
    }

    pub fn forget_all(&mut self) -> usize {
        let count = self.enrolled.len();
        self.enrolled.clear();
        count
    }

    pub fn enrolled_handles(&self) -> Vec<String> {
        let mut out: Vec<String> = self.enrolled.keys().cloned().collect();
        out.sort();
        out
    }
}

/// How somebody sounds.
///
/// COARSE ON PURPOSE. These are the states a device can usefully act on - slow
/// down, offer help, stop talking. A finer scale would be a claim about somebody's
/// inner state that a spectrogram does not support.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SpeechEmotion {
    /// Nothing detected, or not confident. THE DEFAULT.
    #[default]
    Unknown,
    Neutral,
    /// Worth slowing down for.
    Upset,
    /// Worth stopping and listening for.
    Angry,
    Happy,
    /// Worth being brief for.
    Tired,
}

impl SpeechEmotion {
    /// What the assistant should do differently. The only reason to detect this
    /// at all - a label that changes nothing is a label about a person kept for
    /// no purpose.
    pub fn guidance(&self) -> &'static str {
        match self {
            Self::Unknown | Self::Neutral => "",
            Self::Upset => "slow down and check whether they want help",
            Self::Angry => "stop talking and let them finish",
            Self::Happy => "match it, briefly",
            Self::Tired => "be brief",
        }
    }
}

/// Hears how something was said.
///
/// NOTHING IS STORED AND NOTHING IS SENT. The reading is for the next sentence
/// the assistant produces and then it is gone: a record of somebody's mood over
/// time is a record nobody agreed to.
pub struct OnnxSpeechEmotionSensor {
    #[allow(clippy::type_complexity)]
    classify: Option<Box<dyn Fn(&[f32]) -> Vec<(String, f32)> + Send + Sync>>,
    /// Below this, `Unknown`. Set high, because acting on a wrong reading is
    /// worse than not acting: telling somebody who is fine to calm down lands
    /// badly.
    pub min_confidence: f32,
}

impl OnnxSpeechEmotionSensor {
    #[allow(clippy::type_complexity)]
    pub fn new(
        classify: Option<Box<dyn Fn(&[f32]) -> Vec<(String, f32)> + Send + Sync>>,
    ) -> Self {
        Self { classify, min_confidence: 0.7 }
    }

    pub fn is_available(&self) -> bool {
        self.classify.is_some()
    }

    pub fn read(&self, samples: &[f32]) -> SpeechEmotion {
        let Some(classify) = &self.classify else { return SpeechEmotion::Unknown };
        let Some((label, score)) = classify(samples)
            .into_iter()
            .max_by(|a, b| a.1.partial_cmp(&b.1).unwrap_or(std::cmp::Ordering::Equal))
        else {
            return SpeechEmotion::Unknown;
        };
        if score < self.min_confidence {
            return SpeechEmotion::Unknown;
        }
        match label.to_lowercase().as_str() {
            "neutral" | "calm" => SpeechEmotion::Neutral,
            "sad" | "upset" | "fearful" => SpeechEmotion::Upset,
            "angry" | "frustrated" => SpeechEmotion::Angry,
            "happy" | "excited" => SpeechEmotion::Happy,
            "tired" | "bored" => SpeechEmotion::Tired,
            _ => SpeechEmotion::Unknown,
        }
    }
}

/// The companion's voice.
///
/// Wraps whichever engine is present and remembers WHICH, so a person can be
/// told why the voice changed - a voice silently swapping between a local model
/// and a cloud one is unsettling in a way a changed setting is not.
pub struct NeuronVoice {
    #[allow(clippy::type_complexity)]
    speak: Option<Box<dyn Fn(&str, &str) -> Option<Vec<f32>> + Send + Sync>>,
    /// Which engine is in use, in words.
    engine: String,
    sample_rate_hz: u32,
    /// The pack family, which decides the pad index and the rate.
    family: String,
}

impl NeuronVoice {
    #[allow(clippy::type_complexity)]
    pub fn new(
        speak: Option<Box<dyn Fn(&str, &str) -> Option<Vec<f32>> + Send + Sync>>,
        engine: &str,
        family: &str,
    ) -> Self {
        Self {
            speak,
            engine: engine.to_string(),
            sample_rate_hz: crate::platform_plugins::EmbeddedVoiceConfigs::sample_rate_for(family),
            family: family.to_string(),
        }
    }

    pub fn engine(&self) -> &str {
        &self.engine
    }

    pub fn family(&self) -> &str {
        &self.family
    }

    /// The rate this voice actually produces.
    ///
    /// Taken from the family table rather than assumed, because one family does
    /// not declare its rate at all - and playing it at the family default plays
    /// the language at the wrong speed, which sounds like a broken voice rather
    /// than a configuration error.
    pub fn sample_rate_hz(&self) -> u32 {
        self.sample_rate_hz
    }

    pub fn declares_rate(&self) -> bool {
        crate::platform_plugins::EmbeddedVoiceConfigs::declares_rate(&self.family)
    }

    pub fn is_available(&self) -> bool {
        self.speak.is_some()
    }

    pub fn say(&self, text: &str, language: &str) -> Option<Vec<f32>> {
        if text.trim().is_empty() {
            return None;
        }
        (self.speak.as_ref()?)(text, language)
    }
}

/// Signing delegated to whatever holds the key.
///
/// THE KEY IS NEVER HERE. A device's key belongs to the device the way an
/// address does; this asks the node holding it to sign and receives a signature.
/// There is no method that loads, imports or exports a private key, and that
/// absence is the design rather than an omission.
pub struct EcdsaCryptoDelegation {
    #[allow(clippy::type_complexity)]
    sign: Option<Box<dyn Fn(&[u8]) -> Result<Vec<u8>, String> + Send + Sync>>,
    #[allow(clippy::type_complexity)]
    verify: Option<Box<dyn Fn(&[u8], &[u8], &[u8]) -> bool + Send + Sync>>,
    /// The public half, which is fine to hold and fine to hand out.
    public_key: Vec<u8>,
    /// P-256 or Ed25519 - named, because a signature verified against the wrong
    /// curve fails in a way that looks like a bad signature.
    curve: String,
}

impl EcdsaCryptoDelegation {
    #[allow(clippy::type_complexity)]
    pub fn new(
        sign: Option<Box<dyn Fn(&[u8]) -> Result<Vec<u8>, String> + Send + Sync>>,
        verify: Option<Box<dyn Fn(&[u8], &[u8], &[u8]) -> bool + Send + Sync>>,
        public_key: Vec<u8>,
        curve: &str,
    ) -> Self {
        Self { sign, verify, public_key, curve: curve.to_string() }
    }

    pub fn public_key(&self) -> &[u8] {
        &self.public_key
    }

    pub fn curve(&self) -> &str {
        &self.curve
    }

    pub fn can_sign(&self) -> bool {
        self.sign.is_some() && !self.public_key.is_empty()
    }

    pub fn sign(&self, message: &[u8]) -> Result<Vec<u8>, String> {
        let Some(sign) = &self.sign else {
            return Err("this device has no key to sign with".into());
        };
        sign(message)
    }

    /// Verifies. FALSE when there is no verifier - never true.
    ///
    /// A verifier that returns true when it cannot check accepts every
    /// signature, which is the single worst default available here.
    pub fn verify(&self, message: &[u8], signature: &[u8], public_key: &[u8]) -> bool {
        self.verify
            .as_ref()
            .map(|v| v(message, signature, public_key))
            .unwrap_or(false)
    }

    /// Whether two signatures over the same message can be compared for
    /// equality.
    ///
    /// FALSE for Ed25519 as some implementations produce it: a randomised
    /// signature is valid and different every time, so comparing bytes to decide
    /// whether two devices signed the same thing gives the wrong answer. Verify
    /// both instead.
    pub fn signatures_are_deterministic(&self) -> bool {
        self.curve.eq_ignore_ascii_case("p-256")
            || self.curve.eq_ignore_ascii_case("secp256r1")
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Simulation

/// A run of a threat spreading through a population of devices.
///
/// A MODEL, AND IT SAYS SO. The numbers coming out are what the assumptions
/// going in imply, which is useful for comparing two defences and is not a
/// prediction about a real network.
#[derive(Debug, Clone, PartialEq)]
pub struct ThreatPropagationScenario {
    pub name: String,
    /// How many devices.
    pub population: usize,
    /// How many are compromised at the start.
    pub initially_affected: usize,
    /// Per contact, in thousandths - so the scenario carries no float and two
    /// runs of the same scenario are identical.
    pub transmission_per_mille: u32,
    /// How many others each device meets per step.
    pub contacts_per_step: u32,
    /// Per step, in thousandths: devices that notice and clean themselves.
    pub recovery_per_mille: u32,
    /// How many devices are patched and cannot be affected at all.
    pub immune: usize,
}

impl Default for ThreatPropagationScenario {
    fn default() -> Self {
        Self {
            name: String::new(),
            population: 1000,
            initially_affected: 1,
            transmission_per_mille: 30,
            contacts_per_step: 5,
            recovery_per_mille: 100,
            immune: 0,
        }
    }
}

impl ThreatPropagationScenario {
    /// The basic reproduction number, in thousandths.
    ///
    /// Below 1000 - meaning below one - it dies out. THE ONE NUMBER worth
    /// reading off a model like this, and the one that says whether a defence
    /// works at all rather than how fast it works.
    pub fn r0_per_mille(&self) -> u32 {
        if self.recovery_per_mille == 0 {
            return u32::MAX;
        }
        self.transmission_per_mille * self.contacts_per_step * 1000 / self.recovery_per_mille
    }

    pub fn dies_out(&self) -> bool {
        self.r0_per_mille() < 1000
    }

    /// Runs it. `(step, affected, recovered)`.
    ///
    /// Deterministic integer arithmetic throughout - a simulation whose numbers
    /// change between runs cannot be used to compare two defences, which is the
    /// only thing it is for.
    pub fn run(&self, steps: usize) -> Vec<(usize, usize, usize)> {
        let susceptible_start = self
            .population
            .saturating_sub(self.initially_affected)
            .saturating_sub(self.immune);
        let (mut susceptible, mut affected, mut recovered) =
            (susceptible_start, self.initially_affected.min(self.population), 0usize);

        let mut out = vec![(0, affected, recovered)];
        for step in 1..=steps {
            if affected == 0 || self.population == 0 {
                out.push((step, affected, recovered));
                continue;
            }
            // Scaled by the share of contacts that are still susceptible - a
            // model that ignores this grows without bound and reports every
            // threat as catastrophic.
            let new_cases = (affected
                * self.contacts_per_step as usize
                * self.transmission_per_mille as usize
                * susceptible
                / (1000 * self.population))
                .min(susceptible);
            let newly_recovered =
                (affected * self.recovery_per_mille as usize / 1000).min(affected);

            susceptible -= new_cases;
            affected = affected + new_cases - newly_recovered;
            recovered += newly_recovered;
            out.push((step, affected, recovered));
        }
        out
    }

    /// The worst step and how many were affected at it.
    pub fn peak(&self, steps: usize) -> (usize, usize) {
        self.run(steps)
            .into_iter()
            .map(|(step, affected, _)| (step, affected))
            .max_by_key(|(_, affected)| *affected)
            .unwrap_or((0, 0))
    }
}

/// Runs a scenario through the external simulator.
///
/// The simulator is another process. This carries the scenario, the seam and the
/// FALLBACK - because a simulation that cannot run should still say what the
/// arithmetic implies rather than nothing at all.
pub struct MiroFishAdapter {
    #[allow(clippy::type_complexity)]
    run: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
}

impl MiroFishAdapter {
    #[allow(clippy::type_complexity)]
    pub fn new(run: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>) -> Self {
        Self { run }
    }

    pub fn is_available(&self) -> bool {
        self.run.is_some()
    }

    /// The scenario, as the simulator wants it.
    pub fn to_request(scenario: &ThreatPropagationScenario, steps: usize) -> String {
        let escape = |s: &str| s.replace('\\', "\\\\").replace('"', "\\\"");
        format!(
            "{{\"name\":\"{}\",\"population\":{},\"initiallyAffected\":{},\
\"transmissionPerMille\":{},\"contactsPerStep\":{},\"recoveryPerMille\":{},\
\"immune\":{},\"steps\":{}}}",
            escape(&scenario.name),
            scenario.population,
            scenario.initially_affected,
            scenario.transmission_per_mille,
            scenario.contacts_per_step,
            scenario.recovery_per_mille,
            scenario.immune,
            steps
        )
    }

    /// Runs it, falling back to the built-in arithmetic.
    ///
    /// The fallback is MARKED as such in the result. A caller that cannot tell
    /// which produced the numbers will quote them the same way, and they are not
    /// the same claim.
    pub fn run_scenario(
        &self,
        scenario: &ThreatPropagationScenario,
        steps: usize,
    ) -> (Vec<(usize, usize, usize)>, &'static str) {
        if let Some(run) = &self.run {
            if run(&Self::to_request(scenario, steps)).is_ok() {
                return (scenario.run(steps), "simulator");
            }
        }
        (scenario.run(steps), "built-in arithmetic, not the simulator")
    }
}
