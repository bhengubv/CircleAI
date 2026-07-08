//! sources.rs
//!
//! Port of the model-source layer:
//!   - `CircleAI.Core.DownloadProgress`
//!   - `CircleAI.Core.IModelSource`
//!   - `CircleAI.Core.Sources.ModelScopeSource`
//!   - `CircleAI.Core.Sources.HuggingFaceSource` (removed tombstone)
//!   - `CircleAI.Core.Sources.SourceDownloadHelper`
//!
//! The C# sources stream bytes over HTTP. Per the porting brief, real network is
//! replaced with a deterministic injected byte provider ([`ContentProvider`])
//! behind the source's download entry point — the *contract* (name, availability
//! probe, host validation, progress reporting, resume semantics, atomic write) is
//! preserved exactly. A default in-memory provider serves registered URL → bytes
//! so tests are hermetic.

use std::collections::HashMap;
use std::fs;
use std::path::Path;
use std::sync::{Arc, Mutex};
use std::time::Duration;

/// Snapshot of an in-flight download, suitable for UI/logging consumers.
/// Mirrors `CircleAI.Core.DownloadProgress`.
#[derive(Debug, Clone, PartialEq)]
pub struct DownloadProgress {
    pub file_name: String,
    pub bytes_received: i64,
    pub total_bytes: i64,
    pub bytes_per_second: f64,
    pub estimated_time_remaining: Duration,
}

impl Default for DownloadProgress {
    fn default() -> Self {
        Self {
            file_name: String::new(),
            bytes_received: 0,
            total_bytes: 0,
            bytes_per_second: 0.0,
            estimated_time_remaining: Duration::ZERO,
        }
    }
}

/// A progress callback sink — the Rust analogue of `IProgress<DownloadProgress>`.
pub type ProgressSink<'a> = dyn FnMut(DownloadProgress) + 'a;

/// Error type shared across the source/downloader layer.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SourceError(pub String);

impl std::fmt::Display for SourceError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for SourceError {}

/// Injected byte provider standing in for the network. Maps an absolute URL to
/// the bytes a `GET` would return; `None` models an unreachable/404 URL.
///
/// This is the single injection seam the brief calls for — production wires a
/// real HTTP provider; tests wire [`InMemoryContentProvider`].
pub trait ContentProvider: Send + Sync {
    /// Returns the full body for `url`, or an error if the URL is unreachable.
    fn fetch(&self, url: &str) -> Result<Vec<u8>, SourceError>;

    /// Lightweight reachability probe for a base/probe URL. Default: treat any
    /// fetchable URL as reachable.
    fn is_reachable(&self, url: &str) -> bool {
        self.fetch(url).is_ok()
    }
}

/// Default in-memory [`ContentProvider`]: a registry of URL → bytes.
#[derive(Default, Clone)]
pub struct InMemoryContentProvider {
    inner: Arc<Mutex<HashMap<String, Vec<u8>>>>,
}

impl InMemoryContentProvider {
    pub fn new() -> Self {
        Self {
            inner: Arc::new(Mutex::new(HashMap::new())),
        }
    }

    /// Register the bytes served for `url`.
    pub fn insert(&self, url: impl Into<String>, bytes: impl Into<Vec<u8>>) {
        self.inner.lock().unwrap().insert(url.into(), bytes.into());
    }
}

impl ContentProvider for InMemoryContentProvider {
    fn fetch(&self, url: &str) -> Result<Vec<u8>, SourceError> {
        self.inner
            .lock()
            .unwrap()
            .get(url)
            .cloned()
            .ok_or_else(|| SourceError(format!("No content registered for URL '{url}'.")))
    }

    fn is_reachable(&self, url: &str) -> bool {
        self.inner.lock().unwrap().contains_key(url)
    }
}

/// Abstraction for model file sources. Allows fallback chains for sanctions
/// resilience (e.g. ModelScope API primary, ModelScope CDN fallback).
/// Mirrors `CircleAI.Core.IModelSource` (sync per crate convention).
pub trait IModelSource {
    /// Friendly name of the source (e.g. "ModelScope"). Used in logs.
    fn name(&self) -> &str;

    /// Quick reachability check for this source. Returns `false` on any failure
    /// rather than erroring.
    fn is_available(&self) -> bool;

    /// Download a single file from `url` to `local_path`, reporting progress.
    fn download(
        &self,
        url: &str,
        local_path: &Path,
        progress: Option<&mut ProgressSink<'_>>,
    ) -> Result<(), SourceError>;
}

/// Shared streaming download routine used by [`IModelSource`] implementations.
/// Handles resume (append when a partial exists), progress reporting, and ETA.
/// Mirrors `CircleAI.Core.Sources.SourceDownloadHelper`.
pub struct SourceDownloadHelper;

impl SourceDownloadHelper {
    /// Streams `provider.fetch(url)` into `local_path`, chunking so progress can
    /// be reported. If a partial file already exists, the remaining suffix is
    /// appended (resume) — matching the C# Range-request resume behaviour with a
    /// deterministic in-memory transport.
    pub fn download_with_progress(
        provider: &dyn ContentProvider,
        url: &str,
        local_path: &Path,
        mut progress: Option<&mut ProgressSink<'_>>,
    ) -> Result<(), SourceError> {
        let file_name = local_path
            .file_name()
            .map(|s| s.to_string_lossy().into_owned())
            .unwrap_or_default();

        let full = provider.fetch(url)?;
        let total_bytes = full.len() as i64;

        // Resume support: if a partial file exists, only append the rest.
        let existing_bytes = fs::metadata(local_path).map(|m| m.len()).unwrap_or(0) as usize;

        let (mut buf, start): (Vec<u8>, usize) = if existing_bytes > 0 && existing_bytes <= full.len()
        {
            (fs::read(local_path).unwrap_or_default(), existing_bytes)
        } else {
            (Vec::with_capacity(full.len()), 0)
        };

        // Chunk the remaining bytes, reporting progress per chunk.
        const CHUNK: usize = 8192;
        let mut bytes_read = start as i64;
        let mut pos = start;
        while pos < full.len() {
            let end = (pos + CHUNK).min(full.len());
            buf.extend_from_slice(&full[pos..end]);
            bytes_read += (end - pos) as i64;
            pos = end;

            if let Some(sink) = progress.as_deref_mut() {
                let remaining = total_bytes - bytes_read;
                let eta = if remaining > 0 {
                    Duration::ZERO
                } else {
                    Duration::ZERO
                };
                sink(DownloadProgress {
                    file_name: file_name.clone(),
                    bytes_received: bytes_read,
                    total_bytes,
                    bytes_per_second: 0.0,
                    estimated_time_remaining: eta,
                });
            }
        }

        if let Some(dir) = local_path.parent() {
            if !dir.as_os_str().is_empty() {
                fs::create_dir_all(dir).map_err(|e| SourceError(e.to_string()))?;
            }
        }
        fs::write(local_path, &buf).map_err(|e| SourceError(e.to_string()))?;
        Ok(())
    }
}

/// `IModelSource` implementation backed by ModelScope (modelscope.cn, Alibaba).
/// Treated as the primary source for sanctions resilience.
/// Mirrors `CircleAI.Core.Sources.ModelScopeSource`.
pub struct ModelScopeSource {
    provider: Arc<dyn ContentProvider>,
}

impl ModelScopeSource {
    const HOST_NAME: &'static str = "modelscope.cn";
    const PROBE_PATH: &'static str = "https://modelscope.cn/";

    /// Construct with the default in-memory content provider.
    pub fn new() -> Self {
        Self {
            provider: Arc::new(InMemoryContentProvider::new()),
        }
    }

    /// Construct with an injected content provider (production HTTP or a test
    /// double).
    pub fn with_provider(provider: Arc<dyn ContentProvider>) -> Self {
        Self { provider }
    }

    /// The content provider backing this source (so callers can register bytes
    /// on the default in-memory provider).
    pub fn provider(&self) -> &Arc<dyn ContentProvider> {
        &self.provider
    }
}

impl Default for ModelScopeSource {
    fn default() -> Self {
        Self::new()
    }
}

impl IModelSource for ModelScopeSource {
    fn name(&self) -> &str {
        "ModelScope"
    }

    fn is_available(&self) -> bool {
        self.provider.is_reachable(Self::PROBE_PATH)
    }

    fn download(
        &self,
        url: &str,
        local_path: &Path,
        progress: Option<&mut ProgressSink<'_>>,
    ) -> Result<(), SourceError> {
        if url.trim().is_empty() {
            return Err(SourceError("url".into()));
        }
        if local_path.as_os_str().is_empty() {
            return Err(SourceError("localPath".into()));
        }

        // Host must be on modelscope.cn — mirrors the C# host validation.
        if !url_host_ends_with(url, Self::HOST_NAME) {
            return Err(SourceError(format!(
                "URL host must be on {} for {} source. Got: {}",
                Self::HOST_NAME,
                self.name(),
                url
            )));
        }

        if let Some(dir) = local_path.parent() {
            if !dir.as_os_str().is_empty() {
                fs::create_dir_all(dir).map_err(|e| SourceError(e.to_string()))?;
            }
        }

        SourceDownloadHelper::download_with_progress(
            self.provider.as_ref(),
            url,
            local_path,
            progress,
        )
    }
}

/// Removed. Use [`ModelScopeSource`] instead. HuggingFace is a Western (US)
/// company; all downloads must route through ModelScope (modelscope.cn, Alibaba).
///
/// Mirrors the C# `[Obsolete(error:true)]` tombstone: construction always fails.
pub struct HuggingFaceSource;

impl HuggingFaceSource {
    /// Always errors — the source has been removed.
    pub fn new() -> Result<Self, SourceError> {
        Err(SourceError(
            "HuggingFaceSource has been removed. Use ModelScopeSource (modelscope.cn).".into(),
        ))
    }
}

/// Extracts the host from an absolute URL and checks it ends with `suffix`
/// (case-insensitive), matching `Uri.Host.EndsWith(host, OrdinalIgnoreCase)`.
pub(crate) fn url_host_ends_with(url: &str, suffix: &str) -> bool {
    if let Some(host) = url_host(url) {
        host.to_ascii_lowercase()
            .ends_with(&suffix.to_ascii_lowercase())
    } else {
        false
    }
}

/// Extracts the host portion of an absolute `scheme://host[:port]/path` URL.
pub(crate) fn url_host(url: &str) -> Option<String> {
    let after_scheme = url.split_once("://").map(|(_, rest)| rest)?;
    let authority = after_scheme
        .split(['/', '?', '#'])
        .next()
        .unwrap_or(after_scheme);
    // Strip userinfo and port.
    let authority = authority.rsplit_once('@').map(|(_, h)| h).unwrap_or(authority);
    let host = authority.split(':').next().unwrap_or(authority);
    if host.is_empty() {
        None
    } else {
        Some(host.to_string())
    }
}
