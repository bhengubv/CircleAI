//! skills.rs
//!
//! Port of `CircleAI.Skills/` — the persistent store for B! skills. Skills are
//! named, tagged capability definitions that can be injected into the system
//! prompt to guide B!'s behaviour for specific tasks.
//!
//! C# → Rust map:
//!   * `SkillSource` enum          → [`SkillSource`]
//!   * `SkillDetail` / `SkillSummary` / `SkillDraft` records → same structs
//!   * `ISkillStore`               → [`ISkillStore`] (`#[async_trait]`, since the
//!                                    C# surface is `Task<T>`-based)
//!   * `InMemorySkillStore`        → [`InMemorySkillStore`] (`Mutex<HashMap>` for
//!                                    the C# `ConcurrentDictionary`)
//!   * `FileSkillStore`            → [`FileSkillStore`] (SKILL.md YAML front-matter)
//!   * `SkillContextBuilder`       → [`SkillContextBuilder`]
//!   * `SkillPackSource` / `KnownSkillPacks` → [`SkillPackSource`] / [`KnownSkillPacks`]
//!   * `SkillPackManifest` / `ParsedSkill` / `SkillPackLoader` → same names
//!   * `SkillPackSourcesOptions` / `IPackDownloader` / `SkillPackAutoImporter`
//!
//! Notes on constructs that did not map 1:1:
//!   * `ArgumentException.ThrowIfNullOrWhiteSpace` guards → return a
//!     [`SkillError`] (fail-loud) rather than panic; `?`-friendly.
//!   * The C# `HttpPackDownloader` uses `System.Net.Http` + `System.Formats.Tar`
//!     to fetch + extract a GitHub tarball. The Rust crate carries no HTTP/tar
//!     dependency, so the download strategy is expressed purely as the
//!     [`IPackDownloader`] trait; the shipped default is [`LocalCachePackDownloader`]
//!     (materialises from an already-present cache dir, mirroring the fake test
//!     downloader the C# uses), and the tarball-URL construction logic is kept as
//!     [`build_tarball_url`] for a host that wires real networking.
//!   * `IAsyncEnumerable<ParsedSkill>` (the streaming loader) → an eager
//!     `Vec<ParsedSkill>` returned by [`SkillPackLoader::load`], which is the
//!     idiomatic sync-filesystem equivalent.

use std::collections::{HashMap, HashSet};
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use async_trait::async_trait;
use chrono::{DateTime, Utc};
use regex::Regex;

// ─────────────────────────────────────────────────────────────────────────────
// SkillError
// ─────────────────────────────────────────────────────────────────────────────

/// Failure surface for the skills subsystem. Covers the C#
/// `ArgumentException`/`DirectoryNotFoundException`/IO guard rails.
#[derive(Debug)]
pub enum SkillError {
    /// A required argument was null / empty / whitespace.
    InvalidArgument(String),
    /// A referenced directory does not exist.
    DirectoryNotFound(String),
    /// Underlying filesystem failure.
    Io(std::io::Error),
}

impl fmt::Display for SkillError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            SkillError::InvalidArgument(m) => write!(f, "invalid argument: {m}"),
            SkillError::DirectoryNotFound(m) => write!(f, "directory not found: {m}"),
            SkillError::Io(e) => write!(f, "io error: {e}"),
        }
    }
}

impl std::error::Error for SkillError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            SkillError::Io(e) => Some(e),
            _ => None,
        }
    }
}

impl From<std::io::Error> for SkillError {
    fn from(e: std::io::Error) -> Self {
        SkillError::Io(e)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SkillSource
// ─────────────────────────────────────────────────────────────────────────────

/// Indicates where a [`SkillDetail`] originated. 1:1 with the C# `SkillSource`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SkillSource {
    /// Loaded from a SKILL.md file on disk.
    File,
    /// Created programmatically and held in memory.
    InMemory,
    /// Fetched from a remote skill registry.
    Remote,
}

// ─────────────────────────────────────────────────────────────────────────────
// SkillDetail / SkillSummary / SkillDraft
// ─────────────────────────────────────────────────────────────────────────────

/// Full skill record — the complete definition of a single B! skill, including
/// the detailed instructions injected into the system prompt when selected.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SkillDetail {
    /// Unique slug identifier, e.g. `"calendar-summariser"`.
    pub id: String,
    /// Human-readable display name.
    pub name: String,
    /// One-line summary of what this skill does.
    pub description: String,
    /// Detailed instructions for B! on how to execute this skill.
    pub instructions: String,
    /// Free-form tags for filtering and search.
    pub tags: Vec<String>,
    /// Where this record was loaded from.
    pub source: SkillSource,
    /// UTC timestamp of the most recent modification.
    pub last_modified: DateTime<Utc>,
}

impl SkillDetail {
    fn to_summary(&self) -> SkillSummary {
        SkillSummary {
            id: self.id.clone(),
            name: self.name.clone(),
            description: self.description.clone(),
            tags: self.tags.clone(),
            source: self.source,
        }
    }

    /// Case-insensitive substring match over name / description / tags — the C#
    /// `MatchesQuery` predicate shared by both stores.
    fn matches_query(&self, query: &str) -> bool {
        let q = query.to_lowercase();
        self.name.to_lowercase().contains(&q)
            || self.description.to_lowercase().contains(&q)
            || self.tags.iter().any(|t| t.to_lowercase().contains(&q))
    }
}

/// Lightweight projection of a [`SkillDetail`] used in list and search results.
/// Does not include the full `instructions` text.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SkillSummary {
    pub id: String,
    pub name: String,
    pub description: String,
    pub tags: Vec<String>,
    pub source: SkillSource,
}

/// Input model for creating or updating a skill via [`ISkillStore::upsert`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SkillDraft {
    /// Human-readable display name. Used to auto-generate the slug ID when none
    /// is provided.
    pub name: String,
    pub description: String,
    pub instructions: String,
    pub tags: Vec<String>,
}

impl SkillDraft {
    pub fn new(
        name: impl Into<String>,
        description: impl Into<String>,
        instructions: impl Into<String>,
        tags: Vec<String>,
    ) -> Self {
        Self {
            name: name.into(),
            description: description.into(),
            instructions: instructions.into(),
            tags,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ISkillStore
// ─────────────────────────────────────────────────────────────────────────────

/// Persistent store for B! skills. `#[async_trait]` because the C# `ISkillStore`
/// surface is entirely `Task<T>`-returning.
#[async_trait]
pub trait ISkillStore {
    /// Returns all skills as lightweight summaries.
    async fn list(&self) -> Result<Vec<SkillSummary>, SkillError>;

    /// Returns the full detail for a single skill by ID. `Ok(None)` if no skill
    /// with the given ID exists.
    async fn get(&self, id: &str) -> Result<Option<SkillDetail>, SkillError>;

    /// Returns skills whose name, description, or tags contain `query`
    /// (case-insensitive substring). Empty list when `query` is empty.
    async fn search(&self, query: &str) -> Result<Vec<SkillSummary>, SkillError>;

    /// Creates or replaces a skill. When `id` is `None`/empty, a slug ID is
    /// auto-generated from [`SkillDraft::name`].
    async fn upsert(
        &self,
        id: Option<&str>,
        draft: SkillDraft,
    ) -> Result<SkillDetail, SkillError>;

    /// Removes the skill with the given ID. No-op if the skill does not exist.
    async fn delete(&self, id: &str) -> Result<(), SkillError>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Slug generation (C# InMemorySkillStore.GenerateSlug — a public static reused
// by FileSkillStore).
// ─────────────────────────────────────────────────────────────────────────────

/// Converts a display name to a URL-safe lowercase slug. `"My Skill"` → `"my-skill"`.
/// Falls back to a fresh UUID (hyphen-free) when nothing usable remains.
pub fn generate_slug(name: &str) -> String {
    if name.trim().is_empty() {
        return uuid::Uuid::new_v4().simple().to_string();
    }
    let mut slug = name.trim().to_lowercase();
    slug = Regex::new(r"\s+").unwrap().replace_all(&slug, "-").into_owned();
    slug = Regex::new(r"[^a-z0-9\-]")
        .unwrap()
        .replace_all(&slug, "")
        .into_owned();
    slug = Regex::new(r"-{2,}")
        .unwrap()
        .replace_all(&slug, "-")
        .into_owned();
    let slug = slug.trim_matches('-').to_string();
    if slug.is_empty() {
        uuid::Uuid::new_v4().simple().to_string()
    } else {
        slug
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemorySkillStore
// ─────────────────────────────────────────────────────────────────────────────

/// Thread-safe in-memory [`ISkillStore`]. The C# `ConcurrentDictionary` becomes
/// a `Mutex<HashMap>`; useful for tests and hosts that assemble skills
/// programmatically at startup.
pub struct InMemorySkillStore {
    skills: Mutex<HashMap<String, SkillDetail>>,
}

impl Default for InMemorySkillStore {
    fn default() -> Self {
        Self::new()
    }
}

impl InMemorySkillStore {
    pub fn new() -> Self {
        Self {
            skills: Mutex::new(HashMap::new()),
        }
    }
}

#[async_trait]
impl ISkillStore for InMemorySkillStore {
    async fn list(&self) -> Result<Vec<SkillSummary>, SkillError> {
        let guard = self.skills.lock().unwrap();
        let mut results: Vec<SkillSummary> = guard.values().map(SkillDetail::to_summary).collect();
        results.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
        Ok(results)
    }

    async fn get(&self, id: &str) -> Result<Option<SkillDetail>, SkillError> {
        if id.trim().is_empty() {
            return Err(SkillError::InvalidArgument("id".into()));
        }
        let guard = self.skills.lock().unwrap();
        Ok(guard.get(id).cloned())
    }

    async fn search(&self, query: &str) -> Result<Vec<SkillSummary>, SkillError> {
        if query.trim().is_empty() {
            return Ok(Vec::new());
        }
        let q = query.trim();
        let guard = self.skills.lock().unwrap();
        let mut results: Vec<SkillSummary> = guard
            .values()
            .filter(|s| s.matches_query(q))
            .map(SkillDetail::to_summary)
            .collect();
        results.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
        Ok(results)
    }

    async fn upsert(
        &self,
        id: Option<&str>,
        draft: SkillDraft,
    ) -> Result<SkillDetail, SkillError> {
        let effective_id = match id {
            Some(s) if !s.trim().is_empty() => s.trim().to_string(),
            _ => generate_slug(&draft.name),
        };
        let detail = SkillDetail {
            id: effective_id.clone(),
            name: draft.name,
            description: draft.description,
            instructions: draft.instructions,
            tags: draft.tags,
            source: SkillSource::InMemory,
            last_modified: Utc::now(),
        };
        self.skills
            .lock()
            .unwrap()
            .insert(effective_id, detail.clone());
        Ok(detail)
    }

    async fn delete(&self, id: &str) -> Result<(), SkillError> {
        if id.trim().is_empty() {
            return Err(SkillError::InvalidArgument("id".into()));
        }
        self.skills.lock().unwrap().remove(id);
        Ok(())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FileSkillStore
// ─────────────────────────────────────────────────────────────────────────────

/// [`ISkillStore`] backed by SKILL.md files in a directory. Each file uses YAML
/// front-matter for metadata and Markdown body for the skill instructions.
pub struct FileSkillStore {
    directory_path: PathBuf,
}

impl FileSkillStore {
    /// Initialises the store, creating `directory_path` if it does not yet exist.
    pub fn new(directory_path: impl AsRef<Path>) -> Result<Self, SkillError> {
        let dir = directory_path.as_ref();
        if dir.as_os_str().is_empty() {
            return Err(SkillError::InvalidArgument("directoryPath".into()));
        }
        fs::create_dir_all(dir)?;
        Ok(Self {
            directory_path: dir.to_path_buf(),
        })
    }

    fn skill_files(&self) -> Result<Vec<PathBuf>, SkillError> {
        let mut out = Vec::new();
        for entry in fs::read_dir(&self.directory_path)? {
            let path = entry?.path();
            if path.is_file() && path.extension().and_then(|e| e.to_str()) == Some("md") {
                out.push(path);
            }
        }
        Ok(out)
    }

    fn read_skill_file(path: &Path) -> Option<SkillDetail> {
        let content = fs::read_to_string(path).ok()?;
        let stem = path.file_stem().and_then(|s| s.to_str()).unwrap_or("");
        parse_skill_file(&content, stem, Some(path))
    }
}

/// Parses a SKILL.md file body into a [`SkillDetail`]. `Public` mirror of the C#
/// `FileSkillStore.ParseSkillFile`. `file_path` (when present) supplies the
/// last-modified timestamp; otherwise `Utc::now()` is used.
pub fn parse_skill_file(
    content: &str,
    file_name_without_ext: &str,
    file_path: Option<&Path>,
) -> Option<SkillDetail> {
    if content.trim().is_empty() {
        return None;
    }
    let normalised = content.replace("\r\n", "\n");
    let lines: Vec<&str> = normalised.split('\n').collect();
    if lines.len() < 2 || lines[0].trim() != "---" {
        return None;
    }
    let mut front_matter_end: isize = -1;
    for (i, line) in lines.iter().enumerate().skip(1) {
        if line.trim() == "---" {
            front_matter_end = i as isize;
            break;
        }
    }
    if front_matter_end < 0 {
        return None;
    }
    let fm_end = front_matter_end as usize;

    let mut meta: HashMap<String, String> = HashMap::new();
    for line in &lines[1..fm_end] {
        if let Some(colon) = line.find(':') {
            let key = line[..colon].trim().to_lowercase();
            let value = line[colon + 1..].trim().to_string();
            meta.insert(key, value);
        }
    }

    let id = match meta.get("id") {
        Some(v) if !v.trim().is_empty() => v.clone(),
        _ => file_name_without_ext.to_string(),
    };
    let name = meta.get("name").cloned().unwrap_or_else(|| id.clone());
    let description = meta.get("description").cloned().unwrap_or_default();
    let tags = parse_tags_list(meta.get("tags").map(|s| s.as_str()).unwrap_or(""));

    let instructions = lines[fm_end + 1..].join("\n").trim().to_string();

    let last_modified = file_path
        .and_then(|p| fs::metadata(p).ok())
        .and_then(|m| m.modified().ok())
        .map(DateTime::<Utc>::from)
        .unwrap_or_else(Utc::now);

    Some(SkillDetail {
        id,
        name,
        description,
        instructions,
        tags,
        source: SkillSource::File,
        last_modified,
    })
}

/// Parses a YAML inline list like `[a, b, c]` or a bare scalar.
fn parse_tags_list(raw: &str) -> Vec<String> {
    let mut raw = raw.trim();
    if raw.is_empty() {
        return Vec::new();
    }
    if raw.starts_with('[') && raw.ends_with(']') {
        raw = &raw[1..raw.len() - 1];
    }
    raw.split(',')
        .map(|t| t.trim())
        .filter(|t| !t.is_empty())
        .map(|t| t.to_string())
        .collect()
}

#[async_trait]
impl ISkillStore for FileSkillStore {
    async fn list(&self) -> Result<Vec<SkillSummary>, SkillError> {
        let mut results = Vec::new();
        for file in self.skill_files()? {
            if let Some(detail) = Self::read_skill_file(&file) {
                results.push(detail.to_summary());
            }
        }
        results.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
        Ok(results)
    }

    async fn get(&self, id: &str) -> Result<Option<SkillDetail>, SkillError> {
        if id.trim().is_empty() {
            return Err(SkillError::InvalidArgument("id".into()));
        }
        for file in self.skill_files()? {
            if let Some(detail) = Self::read_skill_file(&file) {
                if detail.id.eq_ignore_ascii_case(id) {
                    return Ok(Some(detail));
                }
            }
        }
        Ok(None)
    }

    async fn search(&self, query: &str) -> Result<Vec<SkillSummary>, SkillError> {
        if query.trim().is_empty() {
            return Ok(Vec::new());
        }
        let q = query.trim();
        let mut results = Vec::new();
        for file in self.skill_files()? {
            if let Some(detail) = Self::read_skill_file(&file) {
                if detail.matches_query(q) {
                    results.push(detail.to_summary());
                }
            }
        }
        results.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
        Ok(results)
    }

    async fn upsert(
        &self,
        id: Option<&str>,
        draft: SkillDraft,
    ) -> Result<SkillDetail, SkillError> {
        let effective_id = match id {
            Some(s) if !s.trim().is_empty() => s.trim().to_string(),
            _ => generate_slug(&draft.name),
        };
        let file_path = self.directory_path.join(format!("{effective_id}.md"));
        let tags = if draft.tags.is_empty() {
            "[]".to_string()
        } else {
            format!("[{}]", draft.tags.join(", "))
        };

        let mut content = String::new();
        content.push_str("---\n");
        content.push_str(&format!("id: {effective_id}\n"));
        content.push_str(&format!("name: {}\n", draft.name));
        content.push_str(&format!("description: {}\n", draft.description));
        content.push_str(&format!("tags: {tags}\n"));
        content.push_str("---\n\n");
        content.push_str(&draft.instructions);

        fs::write(&file_path, content.as_bytes())?;

        Ok(SkillDetail {
            id: effective_id,
            name: draft.name,
            description: draft.description,
            instructions: draft.instructions,
            tags: draft.tags,
            source: SkillSource::File,
            last_modified: Utc::now(),
        })
    }

    async fn delete(&self, id: &str) -> Result<(), SkillError> {
        if id.trim().is_empty() {
            return Err(SkillError::InvalidArgument("id".into()));
        }
        let file_path = self.directory_path.join(format!("{id}.md"));
        if file_path.exists() {
            fs::remove_file(&file_path)?;
        }
        Ok(())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SkillContextBuilder
// ─────────────────────────────────────────────────────────────────────────────

/// Selects the most relevant skills for a user query and formats them as a
/// system-prompt context block. Generic over the store `S` so the concrete
/// [`ISkillStore`] impl is monomorphised in.
pub struct SkillContextBuilder<S: ISkillStore> {
    store: S,
    max_skills: usize,
}

impl<S: ISkillStore> SkillContextBuilder<S> {
    /// Initialises the builder. `max_skills` must be at least 1 (the C#
    /// `ArgumentOutOfRangeException` guard).
    pub fn new(store: S, max_skills: usize) -> Result<Self, SkillError> {
        if max_skills < 1 {
            return Err(SkillError::InvalidArgument(
                "maxSkills must be at least 1".into(),
            ));
        }
        Ok(Self { store, max_skills })
    }

    /// Convenience constructor with the C# default of 5 skills.
    pub fn with_defaults(store: S) -> Self {
        Self {
            store,
            max_skills: 5,
        }
    }

    /// Returns a formatted system-prompt block listing the most relevant skills
    /// for `user_query`. Empty string when the store is empty or nothing matches.
    pub async fn build_context(&self, user_query: &str) -> Result<String, SkillError> {
        if user_query.trim().is_empty() {
            return Ok(String::new());
        }

        let matches = self.store.search(user_query).await?;
        let candidates: Vec<SkillSummary> = if !matches.is_empty() {
            matches.into_iter().take(self.max_skills).collect()
        } else {
            let all = self.store.list().await?;
            if all.is_empty() {
                return Ok(String::new());
            }
            all.into_iter().take(self.max_skills).collect()
        };

        let mut sb = String::new();
        sb.push_str("## Available Skills\n");

        for summary in candidates {
            let detail = match self.store.get(&summary.id).await? {
                Some(d) => d,
                None => continue,
            };
            sb.push('\n');
            sb.push_str(&format!("**{}** — {}\n", detail.id, detail.description));
            if !detail.instructions.trim().is_empty() {
                for line in detail.instructions.split('\n') {
                    sb.push_str(&format!("  {line}\n"));
                }
            }
        }

        Ok(sb.trim_end().to_string())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SkillPackSource / KnownSkillPacks
// ─────────────────────────────────────────────────────────────────────────────

/// Source declaration for a single skill pack. Ports the C# `SkillPackSource`
/// record (all defaults preserved).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SkillPackSource {
    /// Display name and tag prefix, e.g. `"Claude-BugHunter"`.
    pub name: String,
    /// Canonical repo URL.
    pub repo_url: String,
    /// Branch / tag / commit. `"main"` by default.
    pub git_ref: String,
    /// SPDX identifier or descriptive string.
    pub license: String,
    /// Optional path within the repo where SKILL.md files live. `""` walks the tree.
    pub skill_subdir: String,
    /// Cardinality hint for diagnostics + UI; not enforced.
    pub estimated_skill_count: i32,
    /// When `true`, the auto-importer imports this pack on first run.
    pub is_default_enabled: bool,
    /// Extra tags merged into every skill imported from this pack.
    pub default_tags: Vec<String>,
}

impl SkillPackSource {
    /// Builder mirroring the C# named-argument construction with its defaults
    /// (`git_ref = "main"`, `license = "unknown"`, `skill_subdir = ""`,
    /// `estimated_skill_count = 0`, `is_default_enabled = true`, no tags).
    pub fn new(name: impl Into<String>, repo_url: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            repo_url: repo_url.into(),
            git_ref: "main".into(),
            license: "unknown".into(),
            skill_subdir: String::new(),
            estimated_skill_count: 0,
            is_default_enabled: true,
            default_tags: Vec::new(),
        }
    }

    pub fn git_ref(mut self, v: impl Into<String>) -> Self {
        self.git_ref = v.into();
        self
    }
    pub fn license(mut self, v: impl Into<String>) -> Self {
        self.license = v.into();
        self
    }
    pub fn skill_subdir(mut self, v: impl Into<String>) -> Self {
        self.skill_subdir = v.into();
        self
    }
    pub fn estimated_skill_count(mut self, v: i32) -> Self {
        self.estimated_skill_count = v;
        self
    }
    pub fn is_default_enabled(mut self, v: bool) -> Self {
        self.is_default_enabled = v;
        self
    }
    pub fn default_tags(mut self, v: Vec<String>) -> Self {
        self.default_tags = v;
        self
    }
}

/// Default catalogue of skill packs CircleAI imports when auto-import is set.
/// 1:1 with the C# static `KnownSkillPacks`.
pub struct KnownSkillPacks;

impl KnownSkillPacks {
    /// bhengubv/awesome-agent-skills — 1000+ community skills.
    pub fn awesome_agent_skills() -> SkillPackSource {
        SkillPackSource::new(
            "awesome-agent-skills",
            "https://github.com/bhengubv/awesome-agent-skills",
        )
        .license("Apache-2.0")
        .skill_subdir("skills")
        .estimated_skill_count(1000)
        .default_tags(vec!["community".into()])
    }

    /// mukul975/Anthropic-Cybersecurity-Skills — 754 skills.
    pub fn anthropic_cybersecurity() -> SkillPackSource {
        SkillPackSource::new(
            "Anthropic-Cybersecurity-Skills",
            "https://github.com/mukul975/Anthropic-Cybersecurity-Skills",
        )
        .license("Apache-2.0")
        .skill_subdir("skills")
        .estimated_skill_count(754)
        .default_tags(vec!["security".into(), "mitre".into()])
    }

    /// mukul975/Privacy-Data-Protection-Skills — 282+ skills.
    pub fn privacy_data_protection() -> SkillPackSource {
        SkillPackSource::new(
            "Privacy-Data-Protection-Skills",
            "https://github.com/mukul975/Privacy-Data-Protection-Skills",
        )
        .license("Apache-2.0")
        .skill_subdir("skills")
        .estimated_skill_count(282)
        .default_tags(vec!["privacy".into(), "compliance".into()])
    }

    /// bhengubv/Claude-BugHunter — 51 hunting skills.
    pub fn claude_bug_hunter() -> SkillPackSource {
        SkillPackSource::new(
            "Claude-BugHunter",
            "https://github.com/bhengubv/Claude-BugHunter",
        )
        .license("Apache-2.0")
        .skill_subdir("skills")
        .estimated_skill_count(51)
        .default_tags(vec!["security".into(), "bug-bounty".into()])
    }

    /// bhengubv/last30days-skill — single researcher skill.
    pub fn last_30_days() -> SkillPackSource {
        SkillPackSource::new(
            "last30days-skill",
            "https://github.com/bhengubv/last30days-skill",
        )
        .license("MIT")
        .estimated_skill_count(1)
        .default_tags(vec!["research".into()])
    }

    /// bhengubv/eduba-brand — 1 brand skill.
    pub fn eduba_brand() -> SkillPackSource {
        SkillPackSource::new("eduba-brand", "https://github.com/bhengubv/eduba-brand")
            .license("n/a (pattern-port)")
            .skill_subdir(".agents/skills/eduba-brand")
            .estimated_skill_count(1)
            .default_tags(vec!["branding".into(), "eduba".into()])
    }

    /// bhengubv/career-ops — non-standard format; ships disabled by default.
    pub fn career_ops() -> SkillPackSource {
        SkillPackSource::new("career-ops", "https://github.com/bhengubv/career-ops")
            .license("MIT")
            .estimated_skill_count(14)
            .is_default_enabled(false)
            .default_tags(vec![
                "job-search".into(),
                "career".into(),
                "thejobcenter".into(),
            ])
    }

    /// bhengubv/build-your-own-x — awesome-list; disabled by default.
    pub fn build_your_own_x() -> SkillPackSource {
        SkillPackSource::new(
            "build-your-own-x",
            "https://github.com/bhengubv/build-your-own-x",
        )
        .license("MIT")
        .estimated_skill_count(0)
        .is_default_enabled(false)
        .default_tags(vec!["education".into(), "tutorial".into()])
    }

    /// Every known pack.
    pub fn all() -> Vec<SkillPackSource> {
        vec![
            Self::awesome_agent_skills(),
            Self::anthropic_cybersecurity(),
            Self::privacy_data_protection(),
            Self::claude_bug_hunter(),
            Self::last_30_days(),
            Self::eduba_brand(),
            Self::career_ops(),
            Self::build_your_own_x(),
        ]
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SkillPackManifest / ParsedSkill
// ─────────────────────────────────────────────────────────────────────────────

/// Description of a skill pack — name, version, where it came from. Persisted
/// alongside imported skills. 1:1 with the C# `SkillPackManifest`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SkillPackManifest {
    pub name: String,
    pub version: String,
    pub source_url: String,
    pub license: String,
    pub skill_count: i32,
}

/// One parsed skill straight from a SKILL.md file. 1:1 with the C# `ParsedSkill`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParsedSkill {
    pub id: String,
    pub name: String,
    pub description: String,
    pub instructions: String,
    pub tags: Vec<String>,
    pub source_file_path: String,
}

// ─────────────────────────────────────────────────────────────────────────────
// SkillPackLoader
// ─────────────────────────────────────────────────────────────────────────────

/// Walks a skill-pack directory, reads each SKILL.md file, parses YAML
/// front-matter + markdown body, and returns the loaded skills. 1:1 with the C#
/// static `SkillPackLoader`.
///
/// The C# `LoadAsync` is an `IAsyncEnumerable`; the idiomatic Rust equivalent for
/// a synchronous filesystem walk is [`SkillPackLoader::load`] returning a `Vec`.
pub struct SkillPackLoader;

impl SkillPackLoader {
    /// Default file name the loader searches for.
    pub const DEFAULT_SKILL_FILE: &'static str = "SKILL.md";

    /// Scan `root` recursively for files named `skill_file`, parse each, and
    /// return the resulting [`ParsedSkill`] records. Files that fail to parse are
    /// skipped, with the failure raised on `on_warning`.
    pub fn load(
        root: &Path,
        skill_file: &str,
        mut on_warning: Option<&mut dyn FnMut(&Path, &dyn std::error::Error)>,
    ) -> Result<Vec<ParsedSkill>, SkillError> {
        if root.as_os_str().is_empty() {
            return Err(SkillError::InvalidArgument("root".into()));
        }
        if !root.is_dir() {
            return Err(SkillError::DirectoryNotFound(format!(
                "Skill pack root not found: {}",
                root.display()
            )));
        }
        let mut out = Vec::new();
        Self::walk(root, skill_file, &mut out, &mut on_warning)?;
        Ok(out)
    }

    fn walk(
        dir: &Path,
        skill_file: &str,
        out: &mut Vec<ParsedSkill>,
        on_warning: &mut Option<&mut dyn FnMut(&Path, &dyn std::error::Error)>,
    ) -> Result<(), SkillError> {
        for entry in fs::read_dir(dir)? {
            let path = entry?.path();
            if path.is_dir() {
                Self::walk(&path, skill_file, out, on_warning)?;
            } else if path.file_name().and_then(|n| n.to_str()) == Some(skill_file) {
                match fs::read_to_string(&path) {
                    Ok(text) => match Self::parse(&text, path.to_string_lossy().as_ref()) {
                        Ok(skill) => out.push(skill),
                        Err(e) => {
                            if let Some(cb) = on_warning.as_mut() {
                                cb(&path, &e);
                            }
                        }
                    },
                    Err(e) => {
                        if let Some(cb) = on_warning.as_mut() {
                            cb(&path, &e);
                        }
                    }
                }
            }
        }
        Ok(())
    }

    /// Import every parsed skill into `store`. Returns a manifest with the count
    /// imported. Merges a `pack:<name>` tag into each skill (the C# behaviour).
    pub async fn import(
        store: &dyn ISkillStore,
        root: &Path,
        pack_name: &str,
        pack_version: &str,
        source_url: &str,
        license: &str,
        skill_file: &str,
    ) -> Result<SkillPackManifest, SkillError> {
        if pack_name.trim().is_empty() {
            return Err(SkillError::InvalidArgument("packName".into()));
        }
        let parsed = Self::load(root, skill_file, None)?;
        let pack_tag = format!("pack:{}", pack_name.to_lowercase());

        let mut count = 0;
        for p in parsed {
            let mut tags = p.tags.clone();
            // Distinct, case-insensitive, append pack tag if not already present.
            if !tags.iter().any(|t| t.eq_ignore_ascii_case(&pack_tag)) {
                tags.push(pack_tag.clone());
            }
            let draft = SkillDraft {
                name: p.name,
                description: p.description,
                instructions: p.instructions,
                tags,
            };
            store.upsert(Some(&p.id), draft).await?;
            count += 1;
        }
        Ok(SkillPackManifest {
            name: pack_name.to_string(),
            version: pack_version.to_string(),
            source_url: source_url.to_string(),
            license: license.to_string(),
            skill_count: count,
        })
    }

    /// Parse a single SKILL.md file's text. `source_file_path` is informational —
    /// used as a fallback when no name/heading can be extracted.
    pub fn parse(content: &str, source_file_path: &str) -> Result<ParsedSkill, SkillError> {
        if content.is_empty() {
            return Err(SkillError::InvalidArgument("content".into()));
        }
        let fm_regex =
            Regex::new(r"(?s)^\s*---\s*\r?\n(?P<body>.*?)\r?\n---\s*\r?\n").unwrap();

        let (fm_body, md_body): (String, String) = if let Some(m) = fm_regex.captures(content) {
            let body = m.name("body").unwrap().as_str().to_string();
            let full_len = m.get(0).unwrap().end();
            let rest = content[full_len..].trim_start_matches(['\r', '\n']).to_string();
            (body, rest)
        } else {
            (String::new(), content.to_string())
        };

        let name = extract_field(&fm_body, "name")
            .or_else(|| extract_first_heading(&md_body))
            .unwrap_or_else(|| {
                Path::new(source_file_path)
                    .file_stem()
                    .and_then(|s| s.to_str())
                    .unwrap_or("")
                    .to_string()
            });
        let description =
            extract_field(&fm_body, "description").unwrap_or_else(|| truncate(&md_body, 280));
        let tags = extract_tags(&fm_body);
        let id = slugify(&name);

        Ok(ParsedSkill {
            id,
            name,
            description,
            instructions: md_body.trim().to_string(),
            tags,
            source_file_path: source_file_path.to_string(),
        })
    }
}

// ── SkillPackLoader private parsing helpers ─────────────────────────────────

fn extract_field(fm_body: &str, field: &str) -> Option<String> {
    if fm_body.is_empty() {
        return None;
    }
    let pat = format!(r"(?m)^\s*{}\s*:\s*(?P<v>.*)$", regex::escape(field));
    let re = Regex::new(&pat).unwrap();
    let caps = re.captures(fm_body)?;
    let mut value = caps.name("v").unwrap().as_str().trim().to_string();
    if value.len() >= 2 {
        let b = value.as_bytes();
        if (b[0] == b'"' && b[value.len() - 1] == b'"')
            || (b[0] == b'\'' && b[value.len() - 1] == b'\'')
        {
            value = value[1..value.len() - 1].to_string();
        }
    }
    if value.is_empty() {
        None
    } else {
        Some(value)
    }
}

fn extract_tags(fm_body: &str) -> Vec<String> {
    if fm_body.is_empty() {
        return Vec::new();
    }
    // Inline: tags: [a, b, c]
    let inline = Regex::new(r"(?m)^\s*tags\s*:\s*\[(?P<v>[^\]]*)\]").unwrap();
    if let Some(caps) = inline.captures(fm_body) {
        return caps
            .name("v")
            .unwrap()
            .as_str()
            .split(',')
            .map(|s| s.trim().trim_matches(['\'', '"']).to_string())
            .filter(|s| !s.is_empty())
            .collect();
    }
    // Block:
    //   tags:
    //     - a
    //     - b
    let block = Regex::new(r"(?m)^\s*tags\s*:\s*\r?\n(?P<v>(?:\s+-\s+\S+\s*\r?\n?)+)").unwrap();
    if let Some(caps) = block.captures(fm_body) {
        return caps
            .name("v")
            .unwrap()
            .as_str()
            .split('\n')
            .map(|s| {
                s.trim()
                    .trim_start_matches('-')
                    .trim()
                    .trim_matches(['\'', '"'])
                    .to_string()
            })
            .filter(|s| !s.is_empty())
            .collect();
    }
    Vec::new()
}

fn extract_first_heading(md_body: &str) -> Option<String> {
    let re = Regex::new(r"(?m)^#\s+(?P<v>.+)$").unwrap();
    re.captures(md_body)
        .map(|c| c.name("v").unwrap().as_str().trim().to_string())
}

fn truncate(s: &str, max: usize) -> String {
    let s = s.replace('\r', " ").replace('\n', " ");
    let s = s.trim();
    let char_count = s.chars().count();
    if char_count <= max {
        return s.to_string();
    }
    let head: String = s.chars().take(max - 1).collect();
    format!("{head}…")
}

fn slugify(name: &str) -> String {
    let mut sb = String::new();
    let mut prev_dash = false;
    for ch in name.chars() {
        if ch.is_alphanumeric() {
            sb.extend(ch.to_lowercase());
            prev_dash = false;
        } else if !prev_dash && !sb.is_empty() {
            sb.push('-');
            prev_dash = true;
        }
    }
    let slug = sb.trim_end_matches('-').to_string();
    if slug.is_empty() {
        "unnamed".to_string()
    } else {
        slug
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SkillPackSourcesOptions / IPackDownloader / SkillPackAutoImporter
// ─────────────────────────────────────────────────────────────────────────────

/// Settings for [`SkillPackAutoImporter`]. 1:1 with the C#
/// `SkillPackSourcesOptions` (the `CacheTtl` is expressed as
/// [`chrono::Duration`]).
#[derive(Clone)]
pub struct SkillPackSourcesOptions {
    /// All packs the host knows about. Defaults to [`KnownSkillPacks::all`].
    pub sources: Vec<SkillPackSource>,
    /// Root directory for cached pack downloads.
    pub cache_directory: PathBuf,
    /// When `true`, import every default-enabled source.
    pub import_default_enabled_packs: bool,
    /// Pack names to opt in beyond the default-enabled set.
    pub explicitly_enabled: Vec<String>,
    /// Reuse cached extractions younger than this without re-downloading.
    pub cache_ttl: chrono::Duration,
}

impl Default for SkillPackSourcesOptions {
    fn default() -> Self {
        Self {
            sources: KnownSkillPacks::all(),
            cache_directory: default_cache_directory(),
            import_default_enabled_packs: true,
            explicitly_enabled: Vec::new(),
            cache_ttl: chrono::Duration::days(7),
        }
    }
}

fn default_cache_directory() -> PathBuf {
    // %LOCALAPPDATA%/CircleAI/skill-packs on Windows, else temp dir. Rust std has
    // no SpecialFolder; env var is the portable equivalent.
    let root = std::env::var("LOCALAPPDATA")
        .ok()
        .filter(|s| !s.is_empty())
        .map(PathBuf::from)
        .unwrap_or_else(std::env::temp_dir);
    root.join("CircleAI").join("skill-packs")
}

/// Strategy for materialising a remote pack into a local directory.
/// `#[async_trait]` mirroring the C# `IPackDownloader.EnsureAsync`.
#[async_trait]
pub trait IPackDownloader {
    /// Ensure `source` is materialised under `cache_root`. Returns the local path
    /// containing the extracted repo (the caller appends `skill_subdir`).
    async fn ensure(
        &self,
        source: &SkillPackSource,
        cache_root: &Path,
        cache_ttl: chrono::Duration,
    ) -> Result<PathBuf, SkillError>;
}

/// GitHub tarball URL for a source:
/// `https://github.com/<owner>/<repo>/archive/<ref>.tar.gz`. Kept as free
/// function (the C# `HttpPackDownloader.BuildTarballUrl`) so a networked host can
/// reuse it.
pub fn build_tarball_url(source: &SkillPackSource) -> String {
    let url = source.repo_url.trim_end_matches('/');
    format!("{url}/archive/{}.tar.gz", source.git_ref)
}

/// Sanitise a pack name into a filesystem-safe directory slug — the C#
/// `HttpPackDownloader.Sanitize` (replaces invalid filename chars with `_`).
pub fn sanitize_pack_name(name: &str) -> String {
    name.chars()
        .map(|c| match c {
            '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*' => '_',
            c if (c as u32) < 0x20 => '_',
            c => c,
        })
        .collect()
}

/// Default downloader that materialises from an already-extracted local cache
/// directory (`<cache_root>/<sanitized-name>`). This is the offline-safe stand-in
/// for the C# `HttpPackDownloader` (whose tarball fetch/extract needs HTTP + tar
/// deps not present in this crate). A networked host swaps in its own
/// [`IPackDownloader`] using [`build_tarball_url`].
pub struct LocalCachePackDownloader;

#[async_trait]
impl IPackDownloader for LocalCachePackDownloader {
    async fn ensure(
        &self,
        source: &SkillPackSource,
        cache_root: &Path,
        _cache_ttl: chrono::Duration,
    ) -> Result<PathBuf, SkillError> {
        if cache_root.as_os_str().is_empty() {
            return Err(SkillError::InvalidArgument("cacheRoot".into()));
        }
        let pack_dir = cache_root.join(sanitize_pack_name(&source.name));
        if !pack_dir.is_dir() {
            return Err(SkillError::DirectoryNotFound(format!(
                "pack '{}' not present in local cache at {}",
                source.name,
                pack_dir.display()
            )));
        }
        Ok(pack_dir)
    }
}

/// Orchestrates download + import for every enabled pack. Generic over the
/// downloader `D` and takes a `&dyn ISkillStore` so any store impl works.
pub struct SkillPackAutoImporter<D: IPackDownloader> {
    downloader: D,
    options: SkillPackSourcesOptions,
}

impl SkillPackAutoImporter<LocalCachePackDownloader> {
    /// Construct with the offline-safe [`LocalCachePackDownloader`].
    pub fn with_local_cache(options: SkillPackSourcesOptions) -> Self {
        Self {
            downloader: LocalCachePackDownloader,
            options,
        }
    }
}

impl<D: IPackDownloader> SkillPackAutoImporter<D> {
    pub fn new(downloader: D, options: SkillPackSourcesOptions) -> Self {
        Self {
            downloader,
            options,
        }
    }

    /// Resolve which packs to import, then download and import each. Continues on
    /// per-pack failure; returns one manifest per successfully-imported pack. The
    /// optional `on_error` receives `(pack-name, error)`.
    pub async fn import_enabled(
        &self,
        store: &dyn ISkillStore,
        mut on_error: Option<&mut dyn FnMut(&str, &dyn std::error::Error)>,
    ) -> Result<Vec<SkillPackManifest>, SkillError> {
        let mut results = Vec::new();
        fs::create_dir_all(&self.options.cache_directory)?;

        for source in self.enumerate_enabled() {
            let pack_dir = match self
                .downloader
                .ensure(&source, &self.options.cache_directory, self.options.cache_ttl)
                .await
            {
                Ok(p) => p,
                Err(e) => {
                    if let Some(cb) = on_error.as_mut() {
                        cb(&source.name, &e);
                    }
                    continue;
                }
            };
            let skill_root = if source.skill_subdir.is_empty() {
                pack_dir
            } else {
                pack_dir.join(&source.skill_subdir)
            };
            if !skill_root.is_dir() {
                if let Some(cb) = on_error.as_mut() {
                    let err = SkillError::DirectoryNotFound(format!(
                        "Skill subdir '{}' not found in pack '{}'.",
                        source.skill_subdir, source.name
                    ));
                    cb(&source.name, &err);
                }
                continue;
            }

            match SkillPackLoader::import(
                store,
                &skill_root,
                &source.name,
                &source.git_ref,
                &source.repo_url,
                &source.license,
                SkillPackLoader::DEFAULT_SKILL_FILE,
            )
            .await
            {
                Ok(manifest) => results.push(manifest),
                Err(e) => {
                    if let Some(cb) = on_error.as_mut() {
                        cb(&source.name, &e);
                    }
                }
            }
        }
        Ok(results)
    }

    fn enumerate_enabled(&self) -> Vec<SkillPackSource> {
        let mut seen: HashSet<String> = HashSet::new();
        let mut out = Vec::new();

        if self.options.import_default_enabled_packs {
            for s in &self.options.sources {
                if s.is_default_enabled && seen.insert(s.name.to_lowercase()) {
                    out.push(s.clone());
                }
            }
        }
        let by_name: HashMap<String, &SkillPackSource> = self
            .options
            .sources
            .iter()
            .map(|s| (s.name.to_lowercase(), s))
            .collect();
        for name in &self.options.explicitly_enabled {
            if let Some(src) = by_name.get(&name.to_lowercase()) {
                if seen.insert(src.name.to_lowercase()) {
                    out.push((*src).clone());
                }
            }
        }
        out
    }
}
