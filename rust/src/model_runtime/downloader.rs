//! downloader.rs
//!
//! Port of:
//!   - `CircleAI.Core.IModelDownloader`
//!   - `CircleAI.Core.ModelDownloader` (+ nested `DownloadProgressReport`,
//!     `ModelEntry`, `BundleFileEntry`)
//!
//! The C# downloader walks a list of `IModelSource` instances in order, falling
//! through on failure. It resolves URLs from an embedded `registry.json`. Per the
//! porting brief the registry is injected (deterministic) rather than read from
//! an embedded resource, and the network lives behind [`ContentProvider`] inside
//! each source.

use std::collections::HashMap;
use std::fs;
use std::path::Path;
use std::sync::{Arc, Mutex};

use super::sources::{
    url_host, DownloadProgress, IModelSource, ProgressSink, SourceError,
};

/// Progress report shape emitted during downloads. Mirrors
/// `ModelDownloader.DownloadProgressReport`.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct DownloadProgressReport {
    pub file_name: String,
    pub bytes_received: i64,
    pub total_bytes: i64,
    pub bytes_per_second: f64,
    pub estimated_time_remaining: std::time::Duration,
}

/// One bundle file entry. Mirrors `ModelDownloader.BundleFileEntry`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BundleFileEntry {
    pub name: String,
    pub sha256: String,
    pub size_bytes: i64,
}

/// A single registry row. Supports both the legacy single-file shape and the
/// bundle shape. Mirrors `ModelDownloader.ModelEntry`.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ModelEntry {
    pub file_name: String,
    pub primary_url: Option<String>,
    pub fallback_url: Option<String>,
    pub checksum: Option<String>,
    pub size_bytes: i64,
    pub version: Option<String>,
    pub architecture: Option<String>,
    pub quantization_type: Option<String>,

    // Bundle shape.
    pub repo: Option<String>,
    pub total_bytes: i64,
    pub bundle_files: Option<Vec<BundleFileEntry>>,
}

impl ModelEntry {
    /// True when the entry is a multi-file bundle.
    pub fn is_bundle(&self) -> bool {
        self.bundle_files.as_ref().is_some_and(|f| !f.is_empty())
    }
}

/// Downloads a model file (or set of files) to local storage. Implementations
/// walk a chain of [`IModelSource`] instances. Mirrors
/// `CircleAI.Core.IModelDownloader` (sync per crate convention).
pub trait IModelDownloader {
    /// Download a model identified by `model_id` to `local_path`. Implementations
    /// resolve the URL set internally.
    fn download_model(&self, model_id: &str, local_path: &Path) -> Result<(), SourceError>;

    /// Download a single model file by trying each candidate URL in order.
    /// Returns the name of the source that succeeded.
    fn download_from_candidates(
        &self,
        candidate_urls: &[String],
        local_file_path: &Path,
        progress: Option<&mut ProgressSink<'_>>,
    ) -> Result<String, SourceError>;
}

/// Source-agnostic model downloader. Walks a list of [`IModelSource`] in order,
/// falling through on failure. Mirrors `CircleAI.Core.ModelDownloader`.
pub struct ModelDownloader {
    sources: Vec<Box<dyn IModelSource + Send + Sync>>,
    registry: HashMap<String, ModelEntry>,
    /// Subscribers to progress reports (the analogue of the `ProgressChanged`
    /// event).
    progress_handlers: Mutex<Vec<Box<dyn FnMut(DownloadProgressReport) + Send>>>,
}

impl ModelDownloader {
    /// Construct with one or more sources. Errors if `sources` is empty.
    /// The registry starts empty; populate it with [`ModelDownloader::set_registry`].
    pub fn new(sources: Vec<Box<dyn IModelSource + Send + Sync>>) -> Result<Self, SourceError> {
        if sources.is_empty() {
            return Err(SourceError(
                "At least one model source is required".into(),
            ));
        }
        Ok(Self {
            sources,
            registry: HashMap::new(),
            progress_handlers: Mutex::new(Vec::new()),
        })
    }

    /// Construct with sources and a pre-built registry (the deterministic analogue
    /// of loading the embedded `registry.json`).
    pub fn with_registry(
        sources: Vec<Box<dyn IModelSource + Send + Sync>>,
        registry: HashMap<String, ModelEntry>,
    ) -> Result<Self, SourceError> {
        let mut d = Self::new(sources)?;
        d.registry = registry;
        Ok(d)
    }

    /// Replace the in-memory registry.
    pub fn set_registry(&mut self, registry: HashMap<String, ModelEntry>) {
        self.registry = registry;
    }

    /// Register a progress handler (analogue of `event ProgressChanged`).
    pub fn on_progress(&self, handler: Box<dyn FnMut(DownloadProgressReport) + Send>) {
        self.progress_handlers.lock().unwrap().push(handler);
    }

    fn emit_progress(&self, report: DownloadProgressReport) {
        for h in self.progress_handlers.lock().unwrap().iter_mut() {
            h(report.clone());
        }
    }

    fn build_candidate_list(entry: &ModelEntry) -> Vec<String> {
        let mut list = Vec::with_capacity(2);
        if let Some(u) = &entry.primary_url {
            if !u.trim().is_empty() {
                list.push(u.clone());
            }
        }
        if let Some(u) = &entry.fallback_url {
            if !u.trim().is_empty() {
                list.push(u.clone());
            }
        }
        list
    }

    /// Heuristic match: by source name substring in host, else the ModelScope
    /// special case. Mirrors `MatchSource`.
    fn match_source(&self, url: &str) -> Option<&(dyn IModelSource + Send + Sync)> {
        let host = url_host(url)?;
        let host_lower = host.to_ascii_lowercase();

        for s in &self.sources {
            if host_lower.contains(&s.name().to_ascii_lowercase()) {
                return Some(s.as_ref());
            }
        }

        if host_lower.contains("modelscope") {
            return self
                .sources
                .iter()
                .find(|s| s.name().eq_ignore_ascii_case("ModelScope"))
                .map(|b| b.as_ref());
        }

        None
    }

    fn cleanup_partial_file(path: &Path) {
        let _ = fs::remove_file(path);
    }
}

impl IModelDownloader for ModelDownloader {
    fn download_model(&self, model_id: &str, local_path: &Path) -> Result<(), SourceError> {
        if model_id.trim().is_empty() {
            return Err(SourceError("modelId".into()));
        }
        if local_path.as_os_str().is_empty() {
            return Err(SourceError("localPath".into()));
        }

        let entry = self.registry.get(model_id).ok_or_else(|| {
            let mut keys: Vec<&str> = self.registry.keys().map(|s| s.as_str()).collect();
            keys.sort_unstable();
            SourceError(format!(
                "Model '{}' is not in the embedded registry. Known models: {}",
                model_id,
                keys.join(", ")
            ))
        })?;

        fs::create_dir_all(local_path).map_err(|e| SourceError(e.to_string()))?;

        if entry.is_bundle() {
            return Err(SourceError(format!(
                "Model '{model_id}' is a multi-file MNN bundle (registry entry has BundleFiles[]). \
                 Use CircleAI.Inference.ModelDownloadService.EnsureBundleAsync from \
                 MnnInferenceBridgeFactory instead — this legacy single-file downloader \
                 cannot fetch a multi-file bundle."
            )));
        }

        let target_file = local_path.join(&entry.file_name);
        let candidates = Self::build_candidate_list(entry);
        if candidates.is_empty() {
            return Err(SourceError(format!(
                "Model '{model_id}' has no PrimaryUrl or FallbackUrl configured."
            )));
        }

        // Bridge DownloadProgress -> DownloadProgressReport into the event.
        let mut bridge = |p: DownloadProgress| {
            self.emit_progress(DownloadProgressReport {
                file_name: p.file_name,
                bytes_received: p.bytes_received,
                total_bytes: p.total_bytes,
                bytes_per_second: p.bytes_per_second,
                estimated_time_remaining: p.estimated_time_remaining,
            });
        };

        let result =
            self.download_from_candidates(&candidates, &target_file, Some(&mut bridge));
        match result {
            Ok(_winner) => Ok(()),
            Err(e) => {
                Self::cleanup_partial_file(&target_file);
                Err(e)
            }
        }
    }

    fn download_from_candidates(
        &self,
        candidate_urls: &[String],
        local_file_path: &Path,
        mut progress: Option<&mut ProgressSink<'_>>,
    ) -> Result<String, SourceError> {
        if candidate_urls.is_empty() {
            return Err(SourceError(
                "At least one candidate URL is required".into(),
            ));
        }
        if local_file_path.as_os_str().is_empty() {
            return Err(SourceError("localFilePath".into()));
        }

        if let Some(dir) = local_file_path.parent() {
            if !dir.as_os_str().is_empty() {
                fs::create_dir_all(dir).map_err(|e| SourceError(e.to_string()))?;
            }
        }

        let mut failures: Vec<String> = Vec::new();

        for url in candidate_urls {
            if url.trim().is_empty() {
                continue;
            }

            let source = match self.match_source(url) {
                Some(s) => s,
                None => {
                    failures.push(format!("(no registered source for '{url}')"));
                    continue;
                }
            };

            match source.download(url, local_file_path, progress.as_deref_mut()) {
                Ok(()) => return Ok(source.name().to_string()),
                Err(ex) => {
                    failures.push(format!("{}: {}", source.name(), ex));
                    // Drop the partial so the next source can start clean.
                    Self::cleanup_partial_file(local_file_path);
                }
            }
        }

        Err(SourceError(format!(
            "All model sources failed:\n  {}",
            failures.join("\n  ")
        )))
    }
}

// Allow the downloader to be shared across the manager via Arc.
pub type SharedDownloader = Arc<dyn IModelDownloader + Send + Sync>;
