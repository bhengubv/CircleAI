//! personality.rs
//!
//! Port of `CircleAI.Personality/` — the user-DECLARED persona artefact and the
//! machinery around it.
//!
//! Distinct from [`crate::memory::PersonaState`] (the AI's LEARNED model of the
//! user). [`Persona`] is the user's structured, editable, exportable identity
//! declaration — a document the user owns.
//!
//!   * [`Persona`] / [`FormalityRange`] / [`PrivacyLevel`] — the declared artefact.
//!   * [`IPersonaProvider`] — storage contract (async). [`JsonPersonaProvider`] is
//!     the reference file-system implementation, storing each persona as
//!     `{rootDir}/{userId}.persona.json`.
//!   * [`IPersonaConflictResolver`] — reconciles declared vs learned. The default
//!     [`DeclaredWinsResolver`] clamps the learned formality into the declared
//!     bounds; [`LearnedWinsResolver`] passes the declared identity through.
//!   * [`PersonaPromptBuilder`] — renders a [`Persona`] into a compact,
//!     prompt-injection-hardened system-prompt hint.
//!
//! C# async (`Task<>` / `IAsyncEnumerable<>`) maps to `#[async_trait]` methods;
//! the export stream is collected into a `Vec` (Rust has no stable async-stream
//! trait in stable std, and the collection is bounded by on-disk persona count).

use async_trait::async_trait;
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fmt;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use uuid::Uuid;

use crate::memory::PersonaState;

// ─────────────────────────────────────────────────────────────────────────────
// PrivacyLevel
// ─────────────────────────────────────────────────────────────────────────────

/// Declared privacy posture controlling how aggressively the assistant minimises
/// stored signals and how visibly it surfaces personal context.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum PrivacyLevel {
    /// Minimum retention, no proactive surfacing, no third-party calls without prompt.
    Strict,
    /// Default. Reasonable retention, helpful proactive prompts.
    Balanced,
    /// Maximum retention, willing to share personal context across surfaces.
    Open,
}

impl Default for PrivacyLevel {
    fn default() -> Self {
        PrivacyLevel::Balanced
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FormalityRange
// ─────────────────────────────────────────────────────────────────────────────

/// Declared bounds on conversational formality. The AI's learned
/// [`PersonaState::formality`] can drift within these bounds but is clamped to
/// [`FormalityRange::floor`] / [`FormalityRange::ceiling`] by an
/// [`IPersonaConflictResolver`].
///
/// Allowed values for both fields: `"casual"`, `"neutral"`, `"formal"`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FormalityRange {
    /// Lowest acceptable formality.
    pub floor: String,
    /// Highest acceptable formality.
    pub ceiling: String,
}

impl FormalityRange {
    pub fn new(floor: impl Into<String>, ceiling: impl Into<String>) -> Self {
        Self {
            floor: floor.into(),
            ceiling: ceiling.into(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Persona
// ─────────────────────────────────────────────────────────────────────────────

/// User-declared persona artefact. Captures the structured identity the user has
/// chosen to share with the assistant — distinct from the AI's
/// [`crate::memory::PersonaState`], which is what the AI has inferred about the
/// user over time.
///
/// 1:1 with the C# `sealed record Persona`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Persona {
    /// Stable identifier for the persona document.
    pub id: Uuid,
    /// User's preferred display name.
    pub display_name: String,
    /// Free-form pronouns (e.g. "she/her", "they/them"). May be `None`.
    pub pronouns: Option<String>,
    /// Free-form identity tags (e.g. "parent", "vegan", "isiZulu learner").
    pub identity_tags: Vec<String>,
    /// Stated values the assistant should respect (e.g. "privacy", "family", "faith").
    pub values: Vec<String>,
    /// Topics the assistant must refuse or avoid.
    pub taboos: Vec<String>,
    /// IETF BCP-47 locale.
    pub preferred_locale: String,
    /// Optional preferred voice tag (e.g. "warm-female", "neutral").
    pub voice_preference: Option<String>,
    /// Declared formality range — the AI's learned `PersonaState` may ride inside
    /// these bounds.
    pub formality: FormalityRange,
    /// Declared privacy posture.
    pub privacy: PrivacyLevel,
    /// UTC time of initial creation.
    pub created_at: DateTime<Utc>,
    /// UTC time of the last modification.
    pub updated_at: DateTime<Utc>,
}

impl Persona {
    /// Creates a new [`Persona`] with sensible defaults: balanced privacy, no
    /// taboos or values, formality range `"casual".."formal"` (effectively
    /// unconstrained), and timestamps stamped to now.
    ///
    /// # Panics
    /// Panics when `display_name` or `locale` is empty/whitespace, matching the
    /// C# `ArgumentException.ThrowIfNullOrWhiteSpace` guards.
    pub fn create(display_name: impl Into<String>, locale: impl Into<String>) -> Self {
        let display_name = display_name.into();
        let locale = locale.into();
        assert!(
            !display_name.trim().is_empty(),
            "displayName must not be null or whitespace."
        );
        assert!(
            !locale.trim().is_empty(),
            "locale must not be null or whitespace."
        );

        let now = Utc::now();
        Persona {
            id: Uuid::new_v4(),
            display_name,
            pronouns: None,
            identity_tags: Vec::new(),
            values: Vec::new(),
            taboos: Vec::new(),
            preferred_locale: locale,
            voice_preference: None,
            formality: FormalityRange::new("casual", "formal"),
            privacy: PrivacyLevel::Balanced,
            created_at: now,
            updated_at: now,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IPersonaProvider
// ─────────────────────────────────────────────────────────────────────────────

/// Persists and retrieves user-declared [`Persona`] documents. Implementations
/// may persist to local JSON files, cloud sync, or an encrypted on-device store.
/// Every implementation must support full user-driven export (the user owns this
/// data).
///
/// The C# `IAsyncEnumerable<Persona> ExportAllAsync(...)` is modelled here as an
/// `async fn` returning a collected `Vec<Persona>`.
#[async_trait]
pub trait IPersonaProvider {
    /// Error surface for the provider.
    type Error: std::error::Error;

    /// Loads the persona associated with `user_id`. Returns `Ok(None)` when no
    /// persona has been saved for that user.
    async fn get(&self, user_id: &str) -> Result<Option<Persona>, Self::Error>;

    /// Persists `persona` for `user_id`. Implementations must refresh
    /// [`Persona::updated_at`] to the current UTC time and return the saved record.
    async fn save(&self, user_id: &str, persona: Persona) -> Result<Persona, Self::Error>;

    /// Returns whether a persona is currently stored for `user_id`.
    async fn exists(&self, user_id: &str) -> Result<bool, Self::Error>;

    /// Returns every persona currently stored. Used for user-driven export
    /// (GDPR / POPIA "give me everything you have on me").
    async fn export_all(&self) -> Result<Vec<Persona>, Self::Error>;
}

// ─────────────────────────────────────────────────────────────────────────────
// JsonPersonaProvider
// ─────────────────────────────────────────────────────────────────────────────

/// Error surfaced by [`JsonPersonaProvider`]: argument validation, IO, and
/// (de)serialisation failures.
#[derive(Debug)]
pub enum PersonaProviderError {
    /// A required argument was empty/whitespace.
    InvalidArgument(String),
    /// An underlying filesystem operation failed.
    Io(std::io::Error),
    /// JSON (de)serialisation failed.
    Serde(serde_json::Error),
}

impl fmt::Display for PersonaProviderError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            PersonaProviderError::InvalidArgument(m) => write!(f, "invalid argument: {m}"),
            PersonaProviderError::Io(e) => write!(f, "persona io error: {e}"),
            PersonaProviderError::Serde(e) => write!(f, "persona serialization error: {e}"),
        }
    }
}

impl std::error::Error for PersonaProviderError {}

impl From<std::io::Error> for PersonaProviderError {
    fn from(e: std::io::Error) -> Self {
        PersonaProviderError::Io(e)
    }
}

impl From<serde_json::Error> for PersonaProviderError {
    fn from(e: serde_json::Error) -> Self {
        PersonaProviderError::Serde(e)
    }
}

/// File-system [`IPersonaProvider`] that stores each persona as a JSON document
/// under a configured root directory (`{rootDir}/{userId}.persona.json`).
///
/// Atomic write-then-rename. Per-`userId` [`Mutex`] serialises concurrent
/// writes within a single process — like the C# `SemaphoreSlim` map, this is NOT
/// multi-replica safe (concurrent writes from multiple host processes can race
/// on disk).
pub struct JsonPersonaProvider {
    root_directory: PathBuf,
    locks: Mutex<HashMap<String, std::sync::Arc<Mutex<()>>>>,
}

impl JsonPersonaProvider {
    /// Component name, mirroring the C# `ComponentName` override.
    pub const COMPONENT_NAME: &'static str = "JsonPersonaProvider";

    /// Creates a new provider rooted at `root_directory`. The directory is created
    /// if it does not already exist.
    ///
    /// # Panics
    /// Panics when `root_directory` is empty/whitespace (matching the C# guard).
    pub fn new(root_directory: impl Into<String>) -> Self {
        let root = root_directory.into();
        assert!(
            !root.trim().is_empty(),
            "rootDirectory must not be null or whitespace."
        );
        let root_directory = PathBuf::from(root);
        // Best-effort create; a later operation surfaces the error if this failed.
        let _ = std::fs::create_dir_all(&root_directory);
        Self {
            root_directory,
            locks: Mutex::new(HashMap::new()),
        }
    }

    /// Per-`userId` lock, created on first use.
    fn lock_for(&self, user_id: &str) -> std::sync::Arc<Mutex<()>> {
        let mut map = self.locks.lock().unwrap();
        map.entry(user_id.to_string())
            .or_insert_with(|| std::sync::Arc::new(Mutex::new(())))
            .clone()
    }

    /// Maps a `user_id` to its on-disk persona path, replacing OS-invalid
    /// filename characters with `_` (mirrors the C# `Path.GetInvalidFileNameChars`
    /// sanitisation).
    fn persona_path(&self, user_id: &str) -> PathBuf {
        let mut safe: String = user_id
            .chars()
            .map(|c| if is_invalid_file_name_char(c) { '_' } else { c })
            .collect();
        if safe.trim().is_empty() {
            safe = "default".to_string();
        }
        self.root_directory.join(format!("{safe}.persona.json"))
    }

    fn read_persona(path: &Path) -> Result<Option<Persona>, PersonaProviderError> {
        if !path.exists() {
            return Ok(None);
        }
        let bytes = std::fs::read(path)?;
        let persona: Persona = serde_json::from_slice(&bytes)?;
        Ok(Some(persona))
    }
}

#[async_trait]
impl IPersonaProvider for JsonPersonaProvider {
    type Error = PersonaProviderError;

    async fn get(&self, user_id: &str) -> Result<Option<Persona>, Self::Error> {
        if user_id.trim().is_empty() {
            return Err(PersonaProviderError::InvalidArgument("userId".into()));
        }
        let path = self.persona_path(user_id);
        if !path.exists() {
            return Ok(None);
        }
        let gate = self.lock_for(user_id);
        let _guard = gate.lock().unwrap();
        Self::read_persona(&path)
    }

    async fn save(&self, user_id: &str, persona: Persona) -> Result<Persona, Self::Error> {
        if user_id.trim().is_empty() {
            return Err(PersonaProviderError::InvalidArgument("userId".into()));
        }
        let refreshed = Persona {
            updated_at: Utc::now(),
            ..persona
        };
        let target = self.persona_path(user_id);
        let tmp = {
            let mut t = target.clone().into_os_string();
            t.push(format!(".{}.tmp", Uuid::new_v4().simple()));
            PathBuf::from(t)
        };

        let gate = self.lock_for(user_id);
        let _guard = gate.lock().unwrap();

        let json = serde_json::to_vec_pretty(&refreshed)?;
        match std::fs::write(&tmp, &json).and_then(|_| std::fs::rename(&tmp, &target)) {
            Ok(_) => Ok(refreshed),
            Err(e) => {
                let _ = std::fs::remove_file(&tmp); // best effort
                Err(PersonaProviderError::Io(e))
            }
        }
    }

    async fn exists(&self, user_id: &str) -> Result<bool, Self::Error> {
        if user_id.trim().is_empty() {
            return Err(PersonaProviderError::InvalidArgument("userId".into()));
        }
        Ok(self.persona_path(user_id).exists())
    }

    async fn export_all(&self) -> Result<Vec<Persona>, Self::Error> {
        let mut out = Vec::new();
        if !self.root_directory.exists() {
            return Ok(out);
        }
        for entry in std::fs::read_dir(&self.root_directory)? {
            let entry = entry?;
            let path = entry.path();
            let is_persona = path
                .file_name()
                .and_then(|n| n.to_str())
                .map(|n| n.ends_with(".persona.json"))
                .unwrap_or(false);
            if !is_persona {
                continue;
            }
            // Skip corrupted records during export rather than failing the whole
            // stream (matches the C# per-file try/catch).
            if let Ok(Some(persona)) = Self::read_persona(&path) {
                out.push(persona);
            }
        }
        Ok(out)
    }
}

/// Characters disallowed in a filename on Windows (the superset also covers
/// POSIX, where only `/` and NUL are illegal). Mirrors
/// `Path.GetInvalidFileNameChars()`.
fn is_invalid_file_name_char(c: char) -> bool {
    matches!(c, '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|')
        || (c as u32) < 0x20
}

// ─────────────────────────────────────────────────────────────────────────────
// IPersonaConflictResolver
// ─────────────────────────────────────────────────────────────────────────────

/// Reconciles a user-declared [`Persona`] with the AI's learned [`PersonaState`].
/// The output is the persona that should be applied to the active session —
/// either the declared one with bounds enforced, or the learned one overriding
/// declaration.
pub trait IPersonaConflictResolver {
    /// Resolves any disagreement between `declared` and `learned`. Implementations
    /// must be deterministic and must NEVER mutate either input.
    fn resolve(&self, declared: &Persona, learned: &PersonaState) -> Persona;
}

/// Default resolver: the declared persona's bounds are hard limits. The learned
/// formality is clamped to the declared [`FormalityRange`]. Everything else from
/// the declared persona passes through unchanged. This is the privacy-respecting
/// default — the user's stated preference wins.
#[derive(Debug, Default, Clone, Copy)]
pub struct DeclaredWinsResolver;

impl IPersonaConflictResolver for DeclaredWinsResolver {
    fn resolve(&self, declared: &Persona, learned: &PersonaState) -> Persona {
        // Clamp the learned formality into the declared range. The declared record
        // is otherwise the source of truth.
        let clamped = clamp_formality(&learned.formality, &declared.formality);
        if clamped == learned.formality {
            // Learned was within bounds — no adjustment to surface.
            return declared.clone();
        }

        // Learned drifted outside declared bounds — surface the clamped value by
        // replacing the floor or ceiling so future projections respect it.
        let range = match clamped.as_str() {
            "casual" => FormalityRange::new("casual", declared.formality.ceiling.clone()),
            "formal" => FormalityRange::new(declared.formality.floor.clone(), "formal"),
            _ => declared.formality.clone(),
        };

        Persona {
            formality: range,
            ..declared.clone()
        }
    }
}

/// Alternative resolver: the learned [`PersonaState`] overrides the declared
/// [`Persona`]. Intended for "privacy mode off" scenarios where the user has
/// opted in to letting the AI follow what it has observed rather than what was
/// declared.
///
/// Passes the declared persona through so identity, taboos, and values stay
/// intact — the learned formality/locale/verbosity are applied separately by the
/// prompt builder.
#[derive(Debug, Default, Clone, Copy)]
pub struct LearnedWinsResolver;

impl IPersonaConflictResolver for LearnedWinsResolver {
    fn resolve(&self, declared: &Persona, _learned: &PersonaState) -> Persona {
        declared.clone()
    }
}

/// Clamps `learned` into `range`. If the declared range is inverted, treats the
/// declared side as fixed at `range.floor`.
fn clamp_formality(learned: &str, range: &FormalityRange) -> String {
    let learned_rank = formality_rank(learned);
    let floor_rank = formality_rank(&range.floor);
    let ceiling_rank = formality_rank(&range.ceiling);

    if floor_rank > ceiling_rank {
        return range.floor.clone();
    }
    if learned_rank < floor_rank {
        return range.floor.clone();
    }
    if learned_rank > ceiling_rank {
        return range.ceiling.clone();
    }
    learned.to_string()
}

/// Rank ordering for the three formality levels. Unknown values rank as neutral.
fn formality_rank(formality: &str) -> i32 {
    match formality {
        "casual" => 0,
        "neutral" => 1,
        "formal" => 2,
        _ => 1,
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PersonaPromptBuilder
// ─────────────────────────────────────────────────────────────────────────────

/// Builds the natural-language system-prompt block describing a [`Persona`].
/// Returns an empty string when the persona is in its default/unedited state so
/// the prompt is not bloated with no-op instructions.
///
/// Defensive against prompt-injection: every user-controlled string is emitted as
/// a JSON string literal (via [`serde_json`]) so any embedded quotes, newlines,
/// or directives ("ignore previous instructions") are rendered inert inside a
/// quoted string.
pub struct PersonaPromptBuilder;

impl PersonaPromptBuilder {
    /// Renders `persona` into a compact system-prompt hint, or an empty string
    /// when the persona is effectively default (display name only).
    pub fn build_system_hint(persona: &Persona) -> String {
        if Self::is_effectively_default(persona) {
            return String::new();
        }

        let mut sb = String::new();
        sb.push_str("[Persona]");

        sb.push_str("\nYou are speaking with ");
        sb.push_str(&quote(&persona.display_name));
        sb.push('.');

        if let Some(pronouns) = &persona.pronouns {
            if !pronouns.trim().is_empty() {
                sb.push_str(" They identify as ");
                sb.push_str(&quote(pronouns));
                sb.push('.');
            }
        }

        sb.push_str("\nThey prefer responses in ");
        sb.push_str(&quote(&persona.preferred_locale));
        sb.push_str(", tone between ");
        sb.push_str(&quote(&persona.formality.floor));
        sb.push_str(" and ");
        sb.push_str(&quote(&persona.formality.ceiling));
        sb.push('.');

        if !persona.identity_tags.is_empty() {
            sb.push_str("\nIdentity tags: ");
            sb.push_str(&quote_list(&persona.identity_tags));
            sb.push('.');
        }

        if !persona.values.is_empty() {
            sb.push_str("\nTheir declared values: ");
            sb.push_str(&quote_list(&persona.values));
            sb.push('.');
        }

        if !persona.taboos.is_empty() {
            sb.push_str("\nAvoid: ");
            sb.push_str(&quote_list(&persona.taboos));
            sb.push('.');
        }

        if let Some(voice) = &persona.voice_preference {
            if !voice.trim().is_empty() {
                sb.push_str("\nPreferred voice tag: ");
                sb.push_str(&quote(voice));
                sb.push('.');
            }
        }

        match persona.privacy {
            PrivacyLevel::Strict => sb.push_str(
                "\nPrivacy: strict — minimize stored signals, do not surface personal context proactively, and never share personal context across surfaces without explicit prompt.",
            ),
            PrivacyLevel::Open => sb.push_str(
                "\nPrivacy: open — the user has authorised broader retention and proactive surfacing.",
            ),
            PrivacyLevel::Balanced => {}
        }

        sb
    }

    /// True when the persona contains no information beyond the
    /// [`Persona::create`] defaults.
    fn is_effectively_default(p: &Persona) -> bool {
        p.pronouns.as_deref().map(str::trim).unwrap_or("").is_empty()
            && p.identity_tags.is_empty()
            && p.values.is_empty()
            && p.taboos.is_empty()
            && p
                .voice_preference
                .as_deref()
                .map(str::trim)
                .unwrap_or("")
                .is_empty()
            && p.privacy == PrivacyLevel::Balanced
            && p.formality.floor == "casual"
            && p.formality.ceiling == "formal"
    }
}

/// JSON-encodes `value` into a quoted literal. This is the prompt-injection
/// defence: any embedded quote, newline, or directive is rendered as inert text
/// inside a quoted string. `serde_json::to_string` on a `&str` never fails, so a
/// failure falls back to an empty JSON string.
fn quote(value: &str) -> String {
    serde_json::to_string(value).unwrap_or_else(|_| "\"\"".to_string())
}

fn quote_list(items: &[String]) -> String {
    items
        .iter()
        .map(|i| quote(i))
        .collect::<Vec<_>>()
        .join(", ")
}
