//! download_service.rs
//!
//! Ported from `CircleAI.Inference/IModelDownloadService.cs` +
//! `ModelDownloadService.cs`.
//!
//! Downloads and manages model files on disk. Supports both the legacy
//! single-file shape (one URL → one cached weight) and the bundle shape (a
//! per-model directory with every file MNN-LLM needs).
//!
//! Per the no-real-IO porting brief, the two external seams — the filesystem
//! and the network — are injected behind [`IFileStore`] and [`IContentFetcher`],
//! with deterministic in-memory defaults ([`InMemoryFileStore`] /
//! [`InMemoryContentFetcher`]). The verify / skip-cached / primary→fallback
//! URL / progress logic reproduces the C# exactly, including the byte-exact
//! [`strip_sha_algorithm_prefix`] helper.

use std::collections::BTreeMap;
use std::fmt;
use std::sync::Mutex;

use crate::memory::multimodal::compute_sha256;

/// One file in a model bundle (compatible shape with `CircleAI.Core.Models.BundleFile`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BundleFileSpec {
    /// Filename relative to the model directory (e.g. `config.json`).
    pub name: String,
    /// SHA-256 in `sha256:<hex>` or bare-hex form. The verify path strips the
    /// optional `sha256:` prefix before comparing.
    pub sha256: String,
    /// Expected file size for diagnostics.
    pub size_bytes: i64,
}

impl BundleFileSpec {
    pub fn new(name: impl Into<String>, sha256: impl Into<String>, size_bytes: i64) -> Self {
        Self {
            name: name.into(),
            sha256: sha256.into(),
            size_bytes,
        }
    }
}

/// Error surfaced by the download service. Covers argument validation, missing
/// content, and SHA mismatch (the deleted-partial paths in the C#).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DownloadServiceError(String);

impl DownloadServiceError {
    fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }
    /// The error message.
    pub fn message(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for DownloadServiceError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for DownloadServiceError {}

type DsResult<T> = Result<T, DownloadServiceError>;

/// Injected filesystem seam. The default [`InMemoryFileStore`] keeps a flat
/// path→bytes map. Paths use `/` separators; directories are implicit.
pub trait IFileStore {
    /// `true` when a file exists at `path`.
    fn exists(&self, path: &str) -> bool;
    /// Read a file's bytes, or `None` when absent.
    fn read(&self, path: &str) -> Option<Vec<u8>>;
    /// Write (create/overwrite) a file with the given bytes.
    fn write(&self, path: &str, bytes: &[u8]);
    /// Delete a file. No-op when absent.
    fn delete(&self, path: &str);
    /// `true` when any file exists under the directory prefix `dir` (implicit
    /// directory presence).
    fn dir_exists(&self, dir: &str) -> bool;
    /// Delete every file under the directory prefix `dir` (recursive).
    fn delete_dir(&self, dir: &str);
    /// Bytes of free space on the host drive (diagnostics).
    fn available_free_space(&self) -> i64;
}

/// Injected network seam. The default [`InMemoryContentFetcher`] serves a
/// url→bytes map, returning an error for unknown URLs (mirrors an HTTP failure).
pub trait IContentFetcher {
    /// Fetch the bytes for `url`, or an error when the URL is unreachable.
    fn fetch(&self, url: &str) -> DsResult<Vec<u8>>;
}

/// In-memory [`IFileStore`] backed by a `BTreeMap<String, Vec<u8>>`.
#[derive(Debug, Default)]
pub struct InMemoryFileStore {
    files: Mutex<BTreeMap<String, Vec<u8>>>,
    free_space: i64,
}

impl InMemoryFileStore {
    /// Creates an empty store reporting `i64::MAX` free bytes.
    pub fn new() -> Self {
        Self {
            files: Mutex::new(BTreeMap::new()),
            free_space: i64::MAX,
        }
    }

    /// Creates an empty store reporting a fixed free-space figure.
    pub fn with_free_space(free_space: i64) -> Self {
        Self {
            files: Mutex::new(BTreeMap::new()),
            free_space,
        }
    }

    /// Number of files currently stored (test helper).
    pub fn file_count(&self) -> usize {
        self.files.lock().unwrap().len()
    }
}

fn under_dir(path: &str, dir: &str) -> bool {
    let prefix = if dir.ends_with('/') {
        dir.to_string()
    } else {
        format!("{dir}/")
    };
    path == dir || path.starts_with(&prefix)
}

impl IFileStore for InMemoryFileStore {
    fn exists(&self, path: &str) -> bool {
        self.files.lock().unwrap().contains_key(path)
    }
    fn read(&self, path: &str) -> Option<Vec<u8>> {
        self.files.lock().unwrap().get(path).cloned()
    }
    fn write(&self, path: &str, bytes: &[u8]) {
        self.files
            .lock()
            .unwrap()
            .insert(path.to_string(), bytes.to_vec());
    }
    fn delete(&self, path: &str) {
        self.files.lock().unwrap().remove(path);
    }
    fn dir_exists(&self, dir: &str) -> bool {
        self.files
            .lock()
            .unwrap()
            .keys()
            .any(|k| under_dir(k, dir))
    }
    fn delete_dir(&self, dir: &str) {
        self.files
            .lock()
            .unwrap()
            .retain(|k, _| !under_dir(k, dir));
    }
    fn available_free_space(&self) -> i64 {
        self.free_space
    }
}

/// In-memory [`IContentFetcher`] serving a url→bytes map.
#[derive(Debug, Default)]
pub struct InMemoryContentFetcher {
    responses: BTreeMap<String, Vec<u8>>,
}

impl InMemoryContentFetcher {
    /// Creates an empty fetcher.
    pub fn new() -> Self {
        Self {
            responses: BTreeMap::new(),
        }
    }

    /// Registers the bytes served for `url`.
    pub fn with_url(mut self, url: impl Into<String>, bytes: impl Into<Vec<u8>>) -> Self {
        self.responses.insert(url.into(), bytes.into());
        self
    }

    /// Registers the bytes served for `url` (mutable form).
    pub fn insert(&mut self, url: impl Into<String>, bytes: impl Into<Vec<u8>>) {
        self.responses.insert(url.into(), bytes.into());
    }
}

impl IContentFetcher for InMemoryContentFetcher {
    fn fetch(&self, url: &str) -> DsResult<Vec<u8>> {
        self.responses
            .get(url)
            .cloned()
            .ok_or_else(|| DownloadServiceError::new(format!("No content configured for URL '{url}'.")))
    }
}

/// Downloads and manages model files. Sync port of `IModelDownloadService`.
///
/// Progress is reported through the caller-supplied `progress` closure as a
/// 0.0–1.0 fraction (the C# `IProgress<double>`); pass `None` to skip.
pub trait IModelDownloadService {
    /// Ensures a single model file is present and matches `expected_sha256`.
    /// Returns the absolute (store-relative) path to the cached file.
    fn ensure_model(
        &self,
        model_id: &str,
        download_uri: &str,
        expected_sha256: Option<&str>,
        progress: Option<&mut dyn FnMut(f64)>,
    ) -> DsResult<String>;

    /// Ensures every file in `bundle_files` is present under a per-model
    /// directory and matches its pinned SHA-256. Returns the model directory.
    fn ensure_bundle(
        &self,
        model_id: &str,
        repo: &str,
        bundle_files: &[BundleFileSpec],
        progress: Option<&mut dyn FnMut(f64)>,
    ) -> DsResult<String>;

    /// `true` when the model (single-file or bundle) exists.
    fn is_model_cached(&self, model_id: &str) -> DsResult<bool>;

    /// Deletes the model file or directory if it exists. No-op when absent.
    fn delete_model(&self, model_id: &str) -> DsResult<()>;

    /// Free bytes available on the drive hosting the storage directory.
    fn available_disk_space_bytes(&self) -> i64;
}

/// Default [`IModelDownloadService`]. Single-file entries land at
/// `{storage}/{modelId}.gguf`; bundle entries land at `{storage}/{modelId}/`.
pub struct ModelDownloadService<F: IFileStore, C: IContentFetcher> {
    storage_directory: String,
    store: F,
    fetcher: C,
}

impl<F: IFileStore, C: IContentFetcher> ModelDownloadService<F, C> {
    /// Constructs the service rooted at `storage_directory`.
    pub fn new(storage_directory: impl Into<String>, store: F, fetcher: C) -> DsResult<Self> {
        let dir = storage_directory.into();
        if dir.trim().is_empty() {
            return Err(DownloadServiceError::new(
                "Storage directory must not be empty.",
            ));
        }
        Ok(Self {
            storage_directory: dir,
            store,
            fetcher,
        })
    }

    /// Access the underlying file store (test helper).
    pub fn store(&self) -> &F {
        &self.store
    }

    fn single_file_path(&self, model_id: &str) -> String {
        format!("{}/{}.gguf", self.storage_directory, model_id)
    }

    fn model_dir(&self, model_id: &str) -> String {
        format!("{}/{}", self.storage_directory, model_id)
    }

    fn validate_model_id(model_id: &str) -> DsResult<()> {
        if model_id.trim().is_empty() {
            return Err(DownloadServiceError::new("Model ID must not be empty."));
        }
        Ok(())
    }

    /// Verifies `bytes` against `expected_hex` (either `sha256:<hex>` or bare
    /// hex). Case-insensitive, prefix-stripped — matches the C# verify path.
    fn verify_sha256(bytes: &[u8], expected_hex: &str) -> bool {
        let actual = compute_sha256(bytes);
        let expected = strip_sha_algorithm_prefix(expected_hex);
        actual.eq_ignore_ascii_case(&expected)
    }

    /// Build the ModelScope primary (API-form) URL for a bundle file.
    fn build_primary_url(repo: &str, file_name: &str) -> String {
        format!(
            "https://modelscope.cn/api/v1/models/{repo}/repo?Revision=master&FilePath={}",
            escape_data_string(file_name)
        )
    }

    /// Build the ModelScope fallback (CDN-form) URL for a bundle file.
    fn build_fallback_url(repo: &str, file_name: &str) -> String {
        format!(
            "https://modelscope.cn/models/{repo}/resolve/master/{}",
            escape_data_string(file_name)
        )
    }

    fn report_overall(progress: &mut Option<&mut dyn FnMut(f64)>, done: i64, total: i64) {
        if let Some(p) = progress.as_mut() {
            if total <= 0 {
                p(0.0);
            } else {
                p((done as f64 / total as f64).min(0.999));
            }
        }
    }
}

impl<F: IFileStore, C: IContentFetcher> IModelDownloadService for ModelDownloadService<F, C> {
    fn ensure_model(
        &self,
        model_id: &str,
        download_uri: &str,
        expected_sha256: Option<&str>,
        mut progress: Option<&mut dyn FnMut(f64)>,
    ) -> DsResult<String> {
        Self::validate_model_id(model_id)?;
        if download_uri.trim().is_empty() {
            return Err(DownloadServiceError::new("download_uri required"));
        }

        let file_path = self.single_file_path(model_id);

        // Cached + verified (or cached + no hash to check) → return as-is.
        if self.store.exists(&file_path) {
            match expected_sha256 {
                Some(expected) => {
                    let bytes = self.store.read(&file_path).unwrap_or_default();
                    if Self::verify_sha256(&bytes, expected) {
                        if let Some(p) = progress.as_mut() {
                            p(1.0);
                        }
                        return Ok(file_path);
                    }
                    self.store.delete(&file_path);
                }
                None => {
                    if let Some(p) = progress.as_mut() {
                        p(1.0);
                    }
                    return Ok(file_path);
                }
            }
        }

        // Download → verify → commit.
        let bytes = self.fetcher.fetch(download_uri)?;
        if let Some(expected) = expected_sha256 {
            if !Self::verify_sha256(&bytes, expected) {
                return Err(DownloadServiceError::new(format!(
                    "SHA-256 mismatch for model '{model_id}'. The downloaded file has been deleted."
                )));
            }
        }
        self.store.write(&file_path, &bytes);
        if let Some(p) = progress.as_mut() {
            p(1.0);
        }
        Ok(file_path)
    }

    fn ensure_bundle(
        &self,
        model_id: &str,
        repo: &str,
        bundle_files: &[BundleFileSpec],
        mut progress: Option<&mut dyn FnMut(f64)>,
    ) -> DsResult<String> {
        Self::validate_model_id(model_id)?;
        if repo.trim().is_empty() {
            return Err(DownloadServiceError::new(
                "Repo path is required for bundle entries.",
            ));
        }
        if bundle_files.is_empty() {
            return Err(DownloadServiceError::new(
                "Bundle file list must not be empty.",
            ));
        }

        let model_dir = self.model_dir(model_id);

        let total_bytes: i64 = bundle_files.iter().map(|f| f.size_bytes.max(0)).sum();
        let mut done_bytes: i64 = 0;

        for file in bundle_files {
            if file.name.trim().is_empty() {
                return Err(DownloadServiceError::new(format!(
                    "Bundle for '{model_id}' contains a file with no Name."
                )));
            }

            let dest_path = format!("{model_dir}/{}", file.name);

            // Skip when cached + valid.
            if self.store.exists(&dest_path) {
                let bytes = self.store.read(&dest_path).unwrap_or_default();
                if Self::verify_sha256(&bytes, &file.sha256) {
                    done_bytes += file.size_bytes;
                    Self::report_overall(&mut progress, done_bytes, total_bytes);
                    continue;
                }
                self.store.delete(&dest_path);
            }

            // PrimaryUrl (API form) → FallbackUrl (CDN form). Either one is the
            // same bytes; try both before giving up.
            let primary = Self::build_primary_url(repo, &file.name);
            let fallback = Self::build_fallback_url(repo, &file.name);
            let bytes = match self.fetcher.fetch(&primary) {
                Ok(b) => b,
                Err(_) => self.fetcher.fetch(&fallback)?,
            };

            if !Self::verify_sha256(&bytes, &file.sha256) {
                return Err(DownloadServiceError::new(format!(
                    "SHA-256 mismatch for bundle file '{}' of model '{model_id}'. \
                     The downloaded file has been deleted.",
                    file.name
                )));
            }
            self.store.write(&dest_path, &bytes);
            done_bytes += file.size_bytes;
            Self::report_overall(&mut progress, done_bytes, total_bytes);
        }

        if let Some(p) = progress.as_mut() {
            p(1.0);
        }
        Ok(model_dir)
    }

    fn is_model_cached(&self, model_id: &str) -> DsResult<bool> {
        Self::validate_model_id(model_id)?;
        let single = self.single_file_path(model_id);
        if self.store.exists(&single) {
            return Ok(true);
        }
        Ok(self.store.dir_exists(&self.model_dir(model_id)))
    }

    fn delete_model(&self, model_id: &str) -> DsResult<()> {
        Self::validate_model_id(model_id)?;
        let single = self.single_file_path(model_id);
        if self.store.exists(&single) {
            self.store.delete(&single);
        }
        let dir = self.model_dir(model_id);
        if self.store.dir_exists(&dir) {
            self.store.delete_dir(&dir);
        }
        Ok(())
    }

    fn available_disk_space_bytes(&self) -> i64 {
        self.store.available_free_space()
    }
}

/// Returns the hex portion of a SHA-256 checksum, stripping an optional leading
/// algorithm token of the form `sha256:`, `SHA-256:`, etc. Byte-exact port of
/// the C# `ModelDownloadService.StripShaAlgorithmPrefix`.
pub fn strip_sha_algorithm_prefix(raw: &str) -> String {
    if raw.is_empty() {
        return String::new();
    }
    let trimmed = raw.trim();
    let colon = match trimmed.find(':') {
        Some(i) => i,
        None => return trimmed.to_string(),
    };
    let prefix = &trimmed[..colon];
    if !prefix.is_empty() && prefix.len() <= 16 {
        let is_alg_name = prefix
            .chars()
            .all(|c| c.is_alphanumeric() || c == '-' || c == '_');
        if is_alg_name {
            return trimmed[colon + 1..].trim().to_string();
        }
    }
    trimmed.to_string()
}

/// Minimal `Uri.EscapeDataString` equivalent for the characters that appear in
/// ModelScope file paths (RFC 3986 unreserved set is left as-is; everything
/// else is percent-encoded). Sufficient for filenames like `config.json` and
/// `model-00001-of-00002.safetensors`.
fn escape_data_string(input: &str) -> String {
    let mut out = String::with_capacity(input.len());
    for b in input.bytes() {
        let c = b as char;
        if c.is_ascii_alphanumeric() || matches!(c, '-' | '_' | '.' | '~') {
            out.push(c);
        } else {
            out.push_str(&format!("%{b:02X}"));
        }
    }
    out
}
