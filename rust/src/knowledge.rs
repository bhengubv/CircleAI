//! knowledge — CircleAI.Knowledge (Rust port).
//!
//! Markdown-on-disk knowledge notes + an episodic store layered on top.
//!
//! Ports:
//!   - `CircleAI.Knowledge.YamlFrontmatter` → [`yaml_frontmatter`] (write/read,
//!     flat string↔string only; nesting/lists rejected)
//!   - `CircleAI.Knowledge.KnowledgeNote` → [`KnowledgeNote`] (to_file_text /
//!     parse_file round-trip)
//!   - `CircleAI.Knowledge.IKnowledgeStore` → [`IKnowledgeStore`]
//!   - `CircleAI.Knowledge.FileSystemKnowledgeStore` → [`FileSystemKnowledgeStore`]
//!     (real fs I/O: one `.md` per note, atomic write-tmp-then-rename, per-id
//!     mutex)
//!   - `CircleAI.Knowledge.MarkdownEpisodicMemoryStore` → [`MarkdownEpisodicMemoryStore`]
//!     (YAML-frontmatter round-trip of an [`EpisodicMemoryEntry`], base64 raw-float
//!     embedding codec, cosine search)
//!
//! Async streaming (`IAsyncEnumerable`) collapses to eager `Vec` returns per
//! crate convention; the trait stays object-safe so alternate stores (Git,
//! remote sync) can be injected. Errors are a hand-rolled [`KnowledgeError`]
//! (no `thiserror`).

use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;
use std::sync::Mutex;

use chrono::{DateTime, SecondsFormat, Utc};
use uuid::Uuid;

use crate::memory::{base64_decode, base64_encode, EpisodicMemoryEntry};

// ─────────────────────────────────────────────────────────────────────────────
// Errors
// ─────────────────────────────────────────────────────────────────────────────

/// Errors surfaced by the knowledge store. Hand-rolled (no `thiserror`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum KnowledgeError {
    /// `ArgumentException` — bad input (empty path/tag, etc.).
    Argument(String),
    /// `FormatException` — malformed note / frontmatter.
    Format(String),
    /// I/O failure.
    Io(String),
}

impl std::fmt::Display for KnowledgeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            KnowledgeError::Argument(m)
            | KnowledgeError::Format(m)
            | KnowledgeError::Io(m) => f.write_str(m),
        }
    }
}

impl std::error::Error for KnowledgeError {}

// ─────────────────────────────────────────────────────────────────────────────
// YamlFrontmatter — minimal flat string↔string YAML block
// ─────────────────────────────────────────────────────────────────────────────

/// Minimal YAML frontmatter parser/writer. Only flat string→string mappings are
/// supported; nested keys, flow-style structures, anchors and lists are
/// rejected. Port of the internal `YamlFrontmatter`.
pub mod yaml_frontmatter {
    use super::KnowledgeError;

    const DELIMITER: &str = "---";

    /// Renders `frontmatter` into a YAML block followed by `body`. An empty map
    /// still emits a delimited (empty) block so the format stays uniform.
    ///
    /// Iteration order of `pairs` is the caller's — callers that need a stable
    /// on-disk order pass an ordered slice (mirrors the C# insertion-ordered
    /// `Dictionary`).
    pub fn write(pairs: &[(String, String)], body: &str) -> Result<String, KnowledgeError> {
        let mut sb = String::new();
        sb.push_str(DELIMITER);
        sb.push('\n');
        for (k, v) in pairs {
            validate_key(k)?;
            sb.push_str(k);
            sb.push_str(": ");
            sb.push_str(&encode_value(v));
            sb.push('\n');
        }
        sb.push_str(DELIMITER);
        sb.push('\n');
        sb.push_str(body);
        Ok(sb)
    }

    /// Parses `text` into a frontmatter list (in file order) and a body string.
    /// Returns `Format` on malformed input.
    pub fn read(text: &str) -> Result<(Vec<(String, String)>, String), KnowledgeError> {
        // Normalise line endings.
        let text = text.replace("\r\n", "\n").replace('\r', "\n");

        let open = format!("{DELIMITER}\n");
        if !text.starts_with(&open) {
            return Err(KnowledgeError::Format(
                "Frontmatter must start with '---' on its own line.".into(),
            ));
        }

        let search_start = DELIMITER.len() + 1;
        let closing_marker = format!("\n{DELIMITER}\n");
        let closing_idx = match text[search_start..].find(&closing_marker) {
            Some(rel) => search_start + rel,
            None => {
                return Err(KnowledgeError::Format(
                    "Missing closing '---' line for frontmatter block.".into(),
                ))
            }
        };

        let yaml = &text[search_start..closing_idx];
        let body = text[closing_idx + closing_marker.len()..].to_string();

        let mut out: Vec<(String, String)> = Vec::new();
        for raw_line in yaml.split('\n') {
            if raw_line.trim().is_empty() {
                continue;
            }
            let first = raw_line.chars().next().unwrap();
            if first == ' ' || first == '\t' {
                return Err(KnowledgeError::Format("Nested YAML is not supported.".into()));
            }
            if raw_line.starts_with("- ") {
                return Err(KnowledgeError::Format("YAML lists are not supported.".into()));
            }
            let colon = match raw_line.find(':') {
                Some(i) if i > 0 => i,
                _ => {
                    return Err(KnowledgeError::Format(format!(
                        "Malformed YAML line: '{raw_line}'."
                    )))
                }
            };
            let key = raw_line[..colon].trim().to_string();
            let rest = if colon + 1 < raw_line.len() {
                raw_line[colon + 1..].trim_start()
            } else {
                ""
            };
            validate_key(&key)?;
            if rest.starts_with('{') || rest.starts_with('[') {
                return Err(KnowledgeError::Format(
                    "Flow-style YAML structures are not supported.".into(),
                ));
            }
            out.push((key, decode_value(rest)?));
        }

        Ok((out, body))
    }

    fn validate_key(key: &str) -> Result<(), KnowledgeError> {
        if key.trim().is_empty() {
            return Err(KnowledgeError::Format("YAML key cannot be empty.".into()));
        }
        for ch in key.chars() {
            if !(ch.is_alphanumeric() || ch == '_' || ch == '-' || ch == '.') {
                return Err(KnowledgeError::Format(format!(
                    "Invalid character '{ch}' in YAML key '{key}'."
                )));
            }
        }
        Ok(())
    }

    /// Encodes a value; values with reserved characters (or leading/trailing
    /// space) are double-quoted with standard escapes.
    fn encode_value(value: &str) -> String {
        if value.is_empty() {
            return "\"\"".to_string();
        }
        let mut needs_quoting = value.chars().any(|ch| {
            matches!(
                ch,
                ':' | '#' | '\n' | '\r' | '\t' | '"' | '\\' | '\'' | '{' | '['
            )
        });
        let first = value.chars().next().unwrap();
        let last = value.chars().next_back().unwrap();
        if !needs_quoting && (first == ' ' || last == ' ') {
            needs_quoting = true;
        }
        if !needs_quoting {
            return value.to_string();
        }

        let mut sb = String::with_capacity(value.len() + 2);
        sb.push('"');
        for ch in value.chars() {
            match ch {
                '\\' => sb.push_str("\\\\"),
                '"' => sb.push_str("\\\""),
                '\n' => sb.push_str("\\n"),
                '\r' => sb.push_str("\\r"),
                '\t' => sb.push_str("\\t"),
                _ => sb.push(ch),
            }
        }
        sb.push('"');
        sb
    }

    /// Decodes a YAML scalar produced by [`encode_value`].
    fn decode_value(raw: &str) -> Result<String, KnowledgeError> {
        if raw.is_empty() {
            return Ok(String::new());
        }
        let first = raw.chars().next().unwrap();

        // Unquoted: strip a single trailing inline comment ('  # ...').
        if first != '"' && first != '\'' {
            if let Some(hash_idx) = raw.find(" #") {
                return Ok(raw[..hash_idx].trim_end().to_string());
            }
            return Ok(raw.to_string());
        }

        // Single-quoted form is intentionally rejected — we never emit it.
        if first == '\'' {
            return Err(KnowledgeError::Format(
                "Single-quoted YAML scalars are not supported.".into(),
            ));
        }

        let bytes: Vec<char> = raw.chars().collect();
        if bytes.len() < 2 || *bytes.last().unwrap() != '"' {
            return Err(KnowledgeError::Format(
                "Unterminated double-quoted YAML scalar.".into(),
            ));
        }
        let inner = &bytes[1..bytes.len() - 1];
        let mut sb = String::with_capacity(inner.len());
        let mut i = 0;
        while i < inner.len() {
            let ch = inner[i];
            if ch != '\\' {
                sb.push(ch);
                i += 1;
                continue;
            }
            if i + 1 >= inner.len() {
                return Err(KnowledgeError::Format(
                    "Trailing backslash in YAML scalar.".into(),
                ));
            }
            i += 1;
            let next = inner[i];
            match next {
                '\\' => sb.push('\\'),
                '"' => sb.push('"'),
                'n' => sb.push('\n'),
                'r' => sb.push('\r'),
                't' => sb.push('\t'),
                _ => {
                    return Err(KnowledgeError::Format(format!(
                        "Unsupported YAML escape '\\{next}'."
                    )))
                }
            }
            i += 1;
        }
        Ok(sb)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// KnowledgeNote
// ─────────────────────────────────────────────────────────────────────────────

const NOTE_TITLE_KEY: &str = "title";
const NOTE_CREATED_KEY: &str = "created_at";
const NOTE_UPDATED_KEY: &str = "updated_at";
const NOTE_ID_KEY: &str = "id";
const NOTE_TAGS_KEY: &str = "tags";

/// A markdown knowledge note: flat frontmatter metadata + a markdown body.
/// Serialised as `---\nkey: value\n---\n(body)`. Mirrors `KnowledgeNote`.
///
/// `frontmatter` holds only the *user-visible* keys — the well-known keys
/// (`id`, `title`, `created_at`, `updated_at`, `tags`) are merged in on write
/// and stripped on read.
#[derive(Debug, Clone, PartialEq)]
pub struct KnowledgeNote {
    pub id: Uuid,
    pub title: String,
    pub body_markdown: String,
    pub frontmatter: Vec<(String, String)>,
    pub tags: Vec<String>,
    pub created_at: DateTime<Utc>,
    pub updated_at: DateTime<Utc>,
}

impl KnowledgeNote {
    /// Serialises this note to its on-disk text form. Well-known fields win over
    /// user frontmatter with the same key.
    pub fn to_file_text(&self) -> Result<String, KnowledgeError> {
        // Preserve user frontmatter order, then override/append well-known keys.
        let mut merged: Vec<(String, String)> = Vec::new();
        let mut seen: HashMap<String, usize> = HashMap::new();
        let push = |k: &str, v: String, merged: &mut Vec<(String, String)>, seen: &mut HashMap<String, usize>| {
            if let Some(&idx) = seen.get(k) {
                merged[idx].1 = v;
            } else {
                seen.insert(k.to_string(), merged.len());
                merged.push((k.to_string(), v));
            }
        };
        for (k, v) in &self.frontmatter {
            push(k, v.clone(), &mut merged, &mut seen);
        }
        push(NOTE_ID_KEY, hyphenated(self.id), &mut merged, &mut seen);
        push(NOTE_TITLE_KEY, self.title.clone(), &mut merged, &mut seen);
        push(
            NOTE_CREATED_KEY,
            iso_o(self.created_at),
            &mut merged,
            &mut seen,
        );
        push(
            NOTE_UPDATED_KEY,
            iso_o(self.updated_at),
            &mut merged,
            &mut seen,
        );
        push(
            NOTE_TAGS_KEY,
            self.tags.join(","),
            &mut merged,
            &mut seen,
        );

        yaml_frontmatter::write(&merged, &self.body_markdown)
    }

    /// Parses the on-disk text form back into a note. Mirrors `ParseFile`.
    pub fn parse_file(text: &str) -> Result<KnowledgeNote, KnowledgeError> {
        let (frontmatter, body) = yaml_frontmatter::read(text)?;
        let map: HashMap<&str, &str> = frontmatter
            .iter()
            .map(|(k, v)| (k.as_str(), v.as_str()))
            .collect();

        let id = match map.get(NOTE_ID_KEY).and_then(|s| Uuid::parse_str(s).ok()) {
            Some(id) => id,
            None => {
                return Err(KnowledgeError::Format(
                    "Knowledge note frontmatter missing or invalid 'id'.".into(),
                ))
            }
        };

        let title = map.get(NOTE_TITLE_KEY).map(|s| s.to_string()).unwrap_or_default();
        let created = parse_timestamp(&map, NOTE_CREATED_KEY);
        let updated = parse_timestamp(&map, NOTE_UPDATED_KEY);

        let tags = match map.get(NOTE_TAGS_KEY) {
            Some(raw) if !raw.trim().is_empty() => raw
                .split(',')
                .map(|s| s.trim())
                .filter(|s| !s.is_empty())
                .map(|s| s.to_string())
                .collect(),
            _ => Vec::new(),
        };

        // Strip well-known keys from the user-visible frontmatter view.
        let user_frontmatter: Vec<(String, String)> = frontmatter
            .iter()
            .filter(|(k, _)| {
                !matches!(
                    k.as_str(),
                    NOTE_ID_KEY | NOTE_TITLE_KEY | NOTE_CREATED_KEY | NOTE_UPDATED_KEY | NOTE_TAGS_KEY
                )
            })
            .cloned()
            .collect();

        Ok(KnowledgeNote {
            id,
            title,
            body_markdown: body,
            frontmatter: user_frontmatter,
            tags,
            created_at: created,
            updated_at: updated,
        })
    }

    /// Looks up a user-frontmatter value by key.
    pub fn frontmatter_get(&self, key: &str) -> Option<&str> {
        self.frontmatter
            .iter()
            .find(|(k, _)| k == key)
            .map(|(_, v)| v.as_str())
    }
}

fn parse_timestamp(map: &HashMap<&str, &str>, key: &str) -> DateTime<Utc> {
    match map.get(key) {
        Some(raw) if !raw.trim().is_empty() => DateTime::parse_from_rfc3339(raw)
            .map(|dt| dt.with_timezone(&Utc))
            .unwrap_or_else(|_| Utc::now()),
        _ => Utc::now(),
    }
}

/// `Guid.ToString("D")` — lowercase 8-4-4-4-12 hyphenated form.
fn hyphenated(id: Uuid) -> String {
    id.as_hyphenated().to_string()
}

/// `DateTimeOffset.ToString("O")` — round-trip ISO-8601 with fractional seconds
/// and offset. `chrono`'s RFC-3339 with nanos matches the round-trip contract.
fn iso_o(dt: DateTime<Utc>) -> String {
    dt.to_rfc3339_opts(SecondsFormat::Nanos, true)
}

// ─────────────────────────────────────────────────────────────────────────────
// IKnowledgeStore
// ─────────────────────────────────────────────────────────────────────────────

/// Persistent store for [`KnowledgeNote`] documents. Mirrors `IKnowledgeStore`
/// (streaming `IAsyncEnumerable` methods collapse to eager `Vec` returns).
pub trait IKnowledgeStore {
    /// Loads the note with the given id, or `None` when absent.
    fn get(&self, id: Uuid) -> Result<Option<KnowledgeNote>, KnowledgeError>;

    /// Persists `note`; the returned record may differ (updated `updated_at`).
    fn save(&self, note: &KnowledgeNote) -> Result<KnowledgeNote, KnowledgeError>;

    /// Deletes the note with the given id. No-op if absent.
    fn delete(&self, id: Uuid) -> Result<(), KnowledgeError>;

    /// Returns notes carrying `tag` (case-insensitive) in their tags.
    fn search_by_tag(&self, tag: &str) -> Result<Vec<KnowledgeNote>, KnowledgeError>;

    /// Returns every note currently stored.
    fn enumerate_all(&self) -> Result<Vec<KnowledgeNote>, KnowledgeError>;
}

// ─────────────────────────────────────────────────────────────────────────────
// FileSystemKnowledgeStore
// ─────────────────────────────────────────────────────────────────────────────

/// File-system [`IKnowledgeStore`]. Each note is stored as
/// `{root}/{id-no-dashes}.md`. Writes are atomic (write-to-tmp + rename).
/// A single mutex serialises writes/deletes (the C# reference uses a per-Guid
/// `SemaphoreSlim`; a single gate is a safe, simpler equivalent for the
/// portable core). Mirrors `FileSystemKnowledgeStore`.
pub struct FileSystemKnowledgeStore {
    root_directory: PathBuf,
    gate: Mutex<()>,
}

impl FileSystemKnowledgeStore {
    /// Creates a store rooted at `root_directory`, creating it if absent.
    pub fn new(root_directory: impl Into<PathBuf>) -> Result<Self, KnowledgeError> {
        let root = root_directory.into();
        if root.as_os_str().is_empty() {
            return Err(KnowledgeError::Argument(
                "rootDirectory cannot be empty.".into(),
            ));
        }
        fs::create_dir_all(&root).map_err(|e| KnowledgeError::Io(e.to_string()))?;
        Ok(Self {
            root_directory: root,
            gate: Mutex::new(()),
        })
    }

    /// `{root}/{id.ToString("N")}.md` — 32-hex-digit stem (no dashes).
    fn note_path(&self, id: Uuid) -> PathBuf {
        self.root_directory
            .join(format!("{}.md", id.as_simple()))
    }
}

impl IKnowledgeStore for FileSystemKnowledgeStore {
    fn get(&self, id: Uuid) -> Result<Option<KnowledgeNote>, KnowledgeError> {
        let path = self.note_path(id);
        if !path.exists() {
            return Ok(None);
        }
        let _guard = self.gate.lock().unwrap();
        let text = fs::read_to_string(&path).map_err(|e| KnowledgeError::Io(e.to_string()))?;
        Ok(Some(KnowledgeNote::parse_file(&text)?))
    }

    fn save(&self, note: &KnowledgeNote) -> Result<KnowledgeNote, KnowledgeError> {
        let refreshed = KnowledgeNote {
            updated_at: Utc::now(),
            ..note.clone()
        };
        let target = self.note_path(refreshed.id);
        let tmp = {
            let mut s = target.clone().into_os_string();
            s.push(".");
            s.push(Uuid::new_v4().as_simple().to_string());
            s.push(".tmp");
            PathBuf::from(s)
        };
        let text = refreshed.to_file_text()?;

        let _guard = self.gate.lock().unwrap();
        // Write to tmp first so a crash mid-write never corrupts the canonical file.
        match fs::write(&tmp, text) {
            Ok(()) => {}
            Err(e) => {
                let _ = fs::remove_file(&tmp);
                return Err(KnowledgeError::Io(e.to_string()));
            }
        }
        if let Err(e) = fs::rename(&tmp, &target) {
            let _ = fs::remove_file(&tmp);
            return Err(KnowledgeError::Io(e.to_string()));
        }
        Ok(refreshed)
    }

    fn delete(&self, id: Uuid) -> Result<(), KnowledgeError> {
        let path = self.note_path(id);
        let _guard = self.gate.lock().unwrap();
        if path.exists() {
            fs::remove_file(&path).map_err(|e| KnowledgeError::Io(e.to_string()))?;
        }
        Ok(())
    }

    fn search_by_tag(&self, tag: &str) -> Result<Vec<KnowledgeNote>, KnowledgeError> {
        if tag.trim().is_empty() {
            return Err(KnowledgeError::Argument("tag cannot be empty.".into()));
        }
        let all = self.enumerate_all()?;
        Ok(all
            .into_iter()
            .filter(|note| note.tags.iter().any(|t| t.eq_ignore_ascii_case(tag)))
            .collect())
    }

    fn enumerate_all(&self) -> Result<Vec<KnowledgeNote>, KnowledgeError> {
        if !self.root_directory.exists() {
            return Ok(Vec::new());
        }
        let mut out = Vec::new();
        let read_dir =
            fs::read_dir(&self.root_directory).map_err(|e| KnowledgeError::Io(e.to_string()))?;
        for entry in read_dir {
            let entry = entry.map_err(|e| KnowledgeError::Io(e.to_string()))?;
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("md") {
                continue;
            }
            // Skip notes that are not in our format (e.g. a stray README.md).
            let text = match fs::read_to_string(&path) {
                Ok(t) => t,
                Err(_) => continue,
            };
            match KnowledgeNote::parse_file(&text) {
                Ok(note) => out.push(note),
                Err(_) => continue,
            }
        }
        Ok(out)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MarkdownEpisodicMemoryStore
// ─────────────────────────────────────────────────────────────────────────────

const EPISODE_ID_KEY: &str = "episode_id";
const RECORDED_AT_KEY: &str = "recorded_at";
const APP_CONTEXT_KEY: &str = "app_context";
const EMBEDDING_KEY: &str = "embedding";
const EMBEDDING_DIMS_KEY: &str = "embedding_dims";
const TAG_PREFIX: &str = "tag_";

/// Markdown-on-disk episodic store, backed by an [`IKnowledgeStore`]. Each
/// [`EpisodicMemoryEntry`] is one [`KnowledgeNote`] with structured frontmatter
/// and a `## User\n\n... ## Assistant\n\n...` body. The embedding is base64 of
/// the raw little-endian `f32` bytes. Mirrors `MarkdownEpisodicMemoryStore`.
pub struct MarkdownEpisodicMemoryStore<S: IKnowledgeStore> {
    store: S,
}

impl<S: IKnowledgeStore> MarkdownEpisodicMemoryStore<S> {
    /// Creates an episodic store backed by `store`.
    pub fn new(store: S) -> Self {
        Self { store }
    }

    /// Persists `entry` as a note.
    pub fn add(&self, entry: &EpisodicMemoryEntry) -> Result<(), KnowledgeError> {
        let note = Self::to_note(entry)?;
        self.store.save(&note)?;
        Ok(())
    }

    /// Returns the `top_k` entries most similar (cosine) to `query_embedding`,
    /// or the most recent `top_k` when the query is `None`/empty. Only entries
    /// with a matching embedding dimension take part in cosine ranking.
    pub fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<EpisodicMemoryEntry>, KnowledgeError> {
        let snapshot = self.snapshot()?;

        let query = match query_embedding {
            Some(q) if !q.is_empty() => q,
            _ => {
                let mut recent = snapshot;
                recent.sort_by(|a, b| b.recorded_at_utc.cmp(&a.recorded_at_utc));
                recent.truncate(top_k);
                return Ok(recent);
            }
        };

        let mut scored: Vec<(EpisodicMemoryEntry, f32)> = snapshot
            .into_iter()
            .filter_map(|e| match &e.embedding {
                Some(emb) if emb.len() == query.len() => {
                    let score = cosine_similarity(query, emb);
                    Some((e, score))
                }
                _ => None,
            })
            .collect();
        scored.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));
        scored.truncate(top_k);
        Ok(scored.into_iter().map(|(e, _)| e).collect())
    }

    /// Returns the most recent `count` entries, newest-first.
    pub fn get_recent(&self, count: usize) -> Result<Vec<EpisodicMemoryEntry>, KnowledgeError> {
        let mut snapshot = self.snapshot()?;
        snapshot.sort_by(|a, b| b.recorded_at_utc.cmp(&a.recorded_at_utc));
        snapshot.truncate(count);
        Ok(snapshot)
    }

    /// Returns the number of entries currently stored.
    pub fn count(&self) -> Result<usize, KnowledgeError> {
        Ok(self.store.enumerate_all()?.len())
    }

    /// Removes all entries recorded strictly before `cutoff`; returns the count
    /// removed.
    pub fn prune_older_than(&self, cutoff: DateTime<Utc>) -> Result<usize, KnowledgeError> {
        let mut doomed: Vec<Uuid> = Vec::new();
        for note in self.store.enumerate_all()? {
            let entry = Self::from_note(&note)?;
            if entry.recorded_at_utc < cutoff {
                doomed.push(note.id);
            }
        }
        let n = doomed.len();
        for id in doomed {
            self.store.delete(id)?;
        }
        Ok(n)
    }

    fn snapshot(&self) -> Result<Vec<EpisodicMemoryEntry>, KnowledgeError> {
        let mut out = Vec::new();
        for note in self.store.enumerate_all()? {
            out.push(Self::from_note(&note)?);
        }
        Ok(out)
    }

    /// Maps an entry to its note representation.
    pub fn to_note(entry: &EpisodicMemoryEntry) -> Result<KnowledgeNote, KnowledgeError> {
        let mut frontmatter: Vec<(String, String)> = Vec::new();
        frontmatter.push((EPISODE_ID_KEY.into(), hyphenated(entry.id)));
        frontmatter.push((RECORDED_AT_KEY.into(), iso_o(entry.recorded_at_utc)));
        if let Some(app) = entry.app_context.as_ref() {
            if !app.trim().is_empty() {
                frontmatter.push((APP_CONTEXT_KEY.into(), app.clone()));
            }
        }
        if let Some(emb) = entry.embedding.as_ref() {
            if !emb.is_empty() {
                // Encode as base64 of the raw little-endian f32 bytes.
                let mut bytes = Vec::with_capacity(emb.len() * 4);
                for f in emb {
                    bytes.extend_from_slice(&f.to_le_bytes());
                }
                frontmatter.push((EMBEDDING_KEY.into(), base64_encode(&bytes)));
                frontmatter.push((EMBEDDING_DIMS_KEY.into(), emb.len().to_string()));
            }
        }
        let mut tags: Vec<String> = Vec::new();
        if let Some(entry_tags) = entry.tags.as_ref() {
            for (k, v) in entry_tags {
                frontmatter.push((format!("{TAG_PREFIX}{k}"), v.clone()));
                tags.push(k.clone());
            }
        }

        let body = format!(
            "## User\n\n{}\n\n## Assistant\n\n{}",
            entry.user_text, entry.assistant_text
        );

        let id = if entry.id.is_nil() {
            Uuid::new_v4()
        } else {
            entry.id
        };

        Ok(KnowledgeNote {
            id,
            title: truncate_for_title(&entry.user_text),
            body_markdown: body,
            frontmatter,
            tags,
            created_at: entry.recorded_at_utc,
            updated_at: entry.recorded_at_utc,
        })
    }

    /// Inverse of [`to_note`](Self::to_note).
    pub fn from_note(note: &KnowledgeNote) -> Result<EpisodicMemoryEntry, KnowledgeError> {
        let episode_id = note
            .frontmatter_get(EPISODE_ID_KEY)
            .and_then(|s| Uuid::parse_str(s).ok())
            .unwrap_or(note.id);

        let recorded_at = note
            .frontmatter_get(RECORDED_AT_KEY)
            .and_then(|s| DateTime::parse_from_rfc3339(s).ok())
            .map(|dt| dt.with_timezone(&Utc))
            .unwrap_or(note.created_at);

        let app_context = note.frontmatter_get(APP_CONTEXT_KEY).map(|s| s.to_string());

        let embedding = match note.frontmatter_get(EMBEDDING_KEY) {
            Some(b64) if !b64.trim().is_empty() => match base64_decode(b64) {
                Some(bytes) => {
                    let mut v = Vec::with_capacity(bytes.len() / 4);
                    let mut chunks = bytes.chunks_exact(4);
                    for c in &mut chunks {
                        v.push(f32::from_le_bytes([c[0], c[1], c[2], c[3]]));
                    }
                    Some(v)
                }
                None => None,
            },
            _ => None,
        };

        let (user_text, assistant_text) = split_body(&note.body_markdown);

        let mut tags_out: Option<HashMap<String, String>> = None;
        for (k, v) in &note.frontmatter {
            if let Some(stripped) = k.strip_prefix(TAG_PREFIX) {
                tags_out
                    .get_or_insert_with(HashMap::new)
                    .insert(stripped.to_string(), v.clone());
            }
        }

        let mut entry = EpisodicMemoryEntry::default();
        entry.id = episode_id;
        entry.recorded_at_utc = recorded_at;
        entry.user_text = user_text;
        entry.assistant_text = assistant_text;
        entry.app_context = app_context;
        entry.embedding = embedding;
        entry.tags = tags_out;
        Ok(entry)
    }
}

fn split_body(body: &str) -> (String, String) {
    if body.is_empty() {
        return (String::new(), String::new());
    }
    let normal = body.replace("\r\n", "\n");
    const USER_MARKER: &str = "## User\n\n";
    const ASSISTANT_MARKER: &str = "\n\n## Assistant\n\n";

    let user_idx = match normal.find(USER_MARKER) {
        Some(i) => i,
        None => return (normal, String::new()),
    };
    let assistant_idx = match normal.find(ASSISTANT_MARKER) {
        Some(i) if i > user_idx => i,
        _ => return (normal, String::new()),
    };

    let user_start = user_idx + USER_MARKER.len();
    let user_text = normal[user_start..assistant_idx].to_string();
    let assistant_text = normal[assistant_idx + ASSISTANT_MARKER.len()..].to_string();
    (user_text, assistant_text)
}

fn truncate_for_title(source: &str) -> String {
    if source.trim().is_empty() {
        return "(untitled)".to_string();
    }
    let single: String = source
        .chars()
        .map(|c| if c == '\n' || c == '\r' { ' ' } else { c })
        .collect();
    let single = single.trim();
    if single.chars().count() <= 64 {
        single.to_string()
    } else {
        single.chars().take(64).collect()
    }
}

/// Cosine of two equal-length L2-normalised vectors (== dot product).
fn cosine_similarity(a: &[f32], b: &[f32]) -> f32 {
    let n = a.len().min(b.len());
    let mut dot = 0.0f32;
    for i in 0..n {
        dot += a[i] * b[i];
    }
    dot
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn yaml_round_trip_quoted_values() {
        let pairs = vec![
            ("id".to_string(), "abc".to_string()),
            ("weird".to_string(), "has: colon # and hash".to_string()),
        ];
        let text = yaml_frontmatter::write(&pairs, "body here").unwrap();
        let (back, body) = yaml_frontmatter::read(&text).unwrap();
        assert_eq!(body, "body here");
        assert_eq!(back[1].1, "has: colon # and hash");
    }

    #[test]
    fn note_round_trip() {
        let note = KnowledgeNote {
            id: Uuid::new_v4(),
            title: "Title".into(),
            body_markdown: "hello".into(),
            frontmatter: vec![("custom".into(), "value".into())],
            tags: vec!["a".into(), "b".into()],
            created_at: Utc::now(),
            updated_at: Utc::now(),
        };
        let text = note.to_file_text().unwrap();
        let back = KnowledgeNote::parse_file(&text).unwrap();
        assert_eq!(back.id, note.id);
        assert_eq!(back.title, "Title");
        assert_eq!(back.tags, vec!["a".to_string(), "b".to_string()]);
        assert_eq!(back.frontmatter_get("custom"), Some("value"));
    }

    #[test]
    fn episodic_entry_round_trip_with_embedding() {
        let mut entry = EpisodicMemoryEntry::default();
        entry.user_text = "what is the weather".into();
        entry.assistant_text = "sunny".into();
        entry.app_context = Some("tgn.app".into());
        entry.embedding = Some(vec![0.1, 0.2, 0.3, 0.4]);
        let mut tags = HashMap::new();
        tags.insert("locale".to_string(), "en".to_string());
        entry.tags = Some(tags);

        let note = MarkdownEpisodicMemoryStore::<FileSystemKnowledgeStore>::to_note(&entry).unwrap();
        let back =
            MarkdownEpisodicMemoryStore::<FileSystemKnowledgeStore>::from_note(&note).unwrap();
        assert_eq!(back.user_text, "what is the weather");
        assert_eq!(back.assistant_text, "sunny");
        assert_eq!(back.app_context.as_deref(), Some("tgn.app"));
        assert_eq!(back.embedding.as_ref().unwrap().len(), 4);
        assert!((back.embedding.as_ref().unwrap()[1] - 0.2).abs() < 1e-6);
        assert_eq!(back.tags.as_ref().unwrap().get("locale").unwrap(), "en");
    }

    #[test]
    fn filesystem_store_save_get_delete() {
        let dir = std::env::temp_dir().join(format!("ck_test_{}", Uuid::new_v4().as_simple()));
        let store = FileSystemKnowledgeStore::new(&dir).unwrap();
        let note = KnowledgeNote {
            id: Uuid::new_v4(),
            title: "T".into(),
            body_markdown: "b".into(),
            frontmatter: vec![],
            tags: vec!["tagx".into()],
            created_at: Utc::now(),
            updated_at: Utc::now(),
        };
        store.save(&note).unwrap();
        let got = store.get(note.id).unwrap().unwrap();
        assert_eq!(got.id, note.id);
        assert_eq!(store.search_by_tag("TAGX").unwrap().len(), 1);
        store.delete(note.id).unwrap();
        assert!(store.get(note.id).unwrap().is_none());
        let _ = fs::remove_dir_all(&dir);
    }
}
