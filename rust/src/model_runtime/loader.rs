//! loader.rs
//!
//! Port of:
//!   - `CircleAI.Core.IModelLoader`
//!   - `CircleAI.Core.LocalModelLoader` (+ nested `ModelInfo`, `BundleFileInfo`)
//!
//! `LocalModelLoader` resolves model files from a local model directory using an
//! embedded `registry.json`. Per the porting brief the registry is injected
//! (deterministic) and the network `CheckForCriticalUpdate` fetch lives behind an
//! injected [`super::sources::ContentProvider`].
//!
//! Checksum verification uses the crate's real SHA-256
//! ([`crate::memory::multimodal::sha256`]) so integrity checks are exact.

use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use crate::memory::multimodal::sha256;

use super::sources::{ContentProvider, InMemoryContentProvider, SourceError};

/// Anchor file inside a bundle — present in every MNN-LLM model bundle.
const BUNDLE_ANCHOR_FILE_NAME: &str = "llm.mnn.weight";

/// The URL `CheckForCriticalUpdate` probes.
const CRITICAL_VERSIONS_URL: &str =
    "https://raw.githubusercontent.com/BhenguAI/models/main/versions.txt";

/// One bundle file entry. Mirrors `LocalModelLoader.BundleFileInfo`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BundleFileInfo {
    pub name: String,
    pub sha256: String,
    pub size_bytes: i64,
}

/// Internal registry-row shape. Supports both the legacy single-file shape and
/// the bundle shape. Mirrors `LocalModelLoader.ModelInfo`.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ModelInfo {
    pub file_name: Option<String>,
    pub primary_url: Option<String>,
    pub fallback_url: Option<String>,
    pub checksum: Option<String>,
    pub size_bytes: i64,
    pub version: String,
    pub architecture: String,
    pub quantization_type: String,

    // Bundle shape.
    pub repo: Option<String>,
    pub total_bytes: i64,
    pub bundle_files: Option<Vec<BundleFileInfo>>,
}

impl ModelInfo {
    pub fn is_bundle(&self) -> bool {
        self.bundle_files.as_ref().is_some_and(|f| !f.is_empty())
    }
}

/// Errors surfaced by [`LocalModelLoader`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ModelLoaderError {
    /// `ArgumentException` — model not supported / not in registry.
    Argument(String),
    /// `FileNotFoundException`.
    NotFound(String),
    /// `InvalidOperationException` — e.g. a bundle routed to the single-file path.
    InvalidOperation(String),
    /// `InvalidDataException` — checksum mismatch after download.
    InvalidData(String),
    /// A download/source-level failure.
    Source(String),
}

impl std::fmt::Display for ModelLoaderError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ModelLoaderError::Argument(m)
            | ModelLoaderError::NotFound(m)
            | ModelLoaderError::InvalidOperation(m)
            | ModelLoaderError::InvalidData(m)
            | ModelLoaderError::Source(m) => f.write_str(m),
        }
    }
}

impl std::error::Error for ModelLoaderError {}

impl From<SourceError> for ModelLoaderError {
    fn from(e: SourceError) -> Self {
        ModelLoaderError::Source(e.0)
    }
}

/// Loader contract. Mirrors `CircleAI.Core.IModelLoader` (sync per crate
/// convention; `IDisposable` maps to `Drop`).
pub trait IModelLoader {
    /// Ensure the model is present locally, returning its path. `progress` is a
    /// 0.0–1.0 fraction sink (the analogue of `IProgress<float>`).
    fn download_model(
        &self,
        model_name: &str,
        progress: Option<&mut dyn FnMut(f32)>,
    ) -> Result<PathBuf, ModelLoaderError>;

    /// The on-disk path a model would occupy.
    fn get_model_path(&self, model_name: &str) -> Result<PathBuf, ModelLoaderError>;

    /// True when the model file exists and passes its checksum.
    fn model_exists(&self, model_name: &str) -> bool;

    /// True if the remote versions manifest flags a `[CRITICAL]` update.
    fn check_for_critical_update(&self) -> bool;
}

/// Local model loader over a filesystem model directory + injected registry.
/// Mirrors `CircleAI.Core.LocalModelLoader`.
pub struct LocalModelLoader {
    model_dir: PathBuf,
    registry: HashMap<String, ModelInfo>,
    /// Byte provider for both file downloads and the critical-update probe.
    provider: Arc<dyn ContentProvider>,
}

impl LocalModelLoader {
    /// Construct with a model directory and an injected registry. The directory
    /// is created if missing.
    pub fn new(
        model_directory: impl AsRef<Path>,
        registry: HashMap<String, ModelInfo>,
    ) -> Result<Self, ModelLoaderError> {
        Self::with_provider(
            model_directory,
            registry,
            Arc::new(InMemoryContentProvider::new()),
        )
    }

    /// Construct with a model directory, registry, and content provider.
    pub fn with_provider(
        model_directory: impl AsRef<Path>,
        registry: HashMap<String, ModelInfo>,
        provider: Arc<dyn ContentProvider>,
    ) -> Result<Self, ModelLoaderError> {
        let model_dir = model_directory.as_ref().to_path_buf();
        fs::create_dir_all(&model_dir).map_err(|e| ModelLoaderError::Source(e.to_string()))?;
        Ok(Self {
            model_dir,
            registry,
            provider,
        })
    }

    /// The content provider (so callers can register bytes / the versions file).
    pub fn provider(&self) -> &Arc<dyn ContentProvider> {
        &self.provider
    }

    /// Registry lookup is case-insensitive (matches `StringComparer.OrdinalIgnoreCase`).
    fn lookup(&self, model_name: &str) -> Option<&ModelInfo> {
        // Fast path: exact.
        if let Some(v) = self.registry.get(model_name) {
            return Some(v);
        }
        let target = model_name.to_ascii_lowercase();
        self.registry
            .iter()
            .find(|(k, _)| k.to_ascii_lowercase() == target)
            .map(|(_, v)| v)
    }

    fn download_file(&self, url: &str, output_path: &Path) -> Result<(), ModelLoaderError> {
        let bytes = self.provider.fetch(url)?;
        if let Some(dir) = output_path.parent() {
            if !dir.as_os_str().is_empty() {
                fs::create_dir_all(dir).map_err(|e| ModelLoaderError::Source(e.to_string()))?;
            }
        }
        fs::write(output_path, &bytes).map_err(|e| ModelLoaderError::Source(e.to_string()))?;
        Ok(())
    }

    /// Verify a file's SHA-256 against `expected_checksum`. Accepts both
    /// `"sha256:<hex>"` and bare-hex forms, case-insensitively — matches C#.
    fn verify_checksum(file_path: &Path, expected_checksum: &str) -> bool {
        let data = match fs::read(file_path) {
            Ok(d) => d,
            Err(_) => return false,
        };
        let actual_hex = to_hex_lower(&sha256(&data));

        let mut expected = expected_checksum.trim();
        if expected.len() >= 7 && expected[..7].eq_ignore_ascii_case("sha256:") {
            expected = expected[7..].trim();
        }
        expected.eq_ignore_ascii_case(&actual_hex)
    }
}

impl IModelLoader for LocalModelLoader {
    fn download_model(
        &self,
        model_name: &str,
        mut progress: Option<&mut dyn FnMut(f32)>,
    ) -> Result<PathBuf, ModelLoaderError> {
        let info = self
            .lookup(model_name)
            .ok_or_else(|| ModelLoaderError::Argument(format!("Model {model_name} not supported")))?
            .clone();

        if info.is_bundle() {
            return Err(ModelLoaderError::InvalidOperation(format!(
                "Model '{model_name}' is a multi-file bundle (registry entry has BundleFiles[]); \
                 use ModelDownloadService.EnsureBundleAsync via MnnInferenceBridgeFactory instead. \
                 LocalModelLoader.DownloadModelAsync only handles legacy single-file entries."
            )));
        }

        let file_name = info
            .file_name
            .as_ref()
            .ok_or_else(|| ModelLoaderError::Argument("FileName missing".into()))?;
        let local_path = self.model_dir.join(file_name);

        // If the file exists already, honour the checksum-skip / verify logic.
        if local_path.exists() {
            match &info.checksum {
                None => return Ok(local_path),
                Some(c) if c.starts_with("sha256:TBD") => return Ok(local_path),
                Some(c) => {
                    if Self::verify_checksum(&local_path, c) {
                        return Ok(local_path);
                    }
                    let _ = fs::remove_file(&local_path);
                }
            }
        }

        // Try primary then fallback.
        let sources = [info.primary_url.clone(), info.fallback_url.clone()];
        let mut last_error: Option<ModelLoaderError> = None;
        for url in sources.into_iter().flatten() {
            if url.trim().is_empty() {
                continue;
            }
            match self.download_file(&url, &local_path) {
                Ok(()) => {
                    if let Some(p) = progress.as_deref_mut() {
                        p(1.0);
                    }
                    match &info.checksum {
                        None => return Ok(local_path),
                        Some(c) if c.starts_with("sha256:TBD") => return Ok(local_path),
                        Some(c) => {
                            if Self::verify_checksum(&local_path, c) {
                                return Ok(local_path);
                            }
                            let _ = fs::remove_file(&local_path);
                            last_error = Some(ModelLoaderError::InvalidData(
                                "Downloaded model failed checksum verification.".into(),
                            ));
                        }
                    }
                }
                Err(e) => last_error = Some(e),
            }
        }

        Err(last_error
            .unwrap_or_else(|| ModelLoaderError::InvalidOperation("All sources failed.".into())))
    }

    fn get_model_path(&self, model_name: &str) -> Result<PathBuf, ModelLoaderError> {
        let info = self
            .lookup(model_name)
            .ok_or_else(|| ModelLoaderError::NotFound(format!("Model {model_name} not found")))?;

        if info.is_bundle() {
            return Ok(self.model_dir.join(model_name).join(BUNDLE_ANCHOR_FILE_NAME));
        }

        let file_name = info
            .file_name
            .as_ref()
            .ok_or_else(|| ModelLoaderError::NotFound("FileName missing".into()))?;
        Ok(self.model_dir.join(file_name))
    }

    fn model_exists(&self, model_name: &str) -> bool {
        let info = match self.lookup(model_name) {
            Some(i) => i,
            None => return false,
        };

        let path = match self.get_model_path(model_name) {
            Ok(p) => p,
            Err(_) => return false,
        };
        if !path.exists() {
            return false;
        }

        if info.is_bundle() {
            let anchor = info.bundle_files.as_ref().and_then(|files| {
                files
                    .iter()
                    .find(|f| f.name.eq_ignore_ascii_case(BUNDLE_ANCHOR_FILE_NAME))
            });
            return match anchor {
                Some(a) => Self::verify_checksum(&path, &a.sha256),
                None => false,
            };
        }

        match &info.checksum {
            Some(c) => Self::verify_checksum(&path, c),
            None => false,
        }
    }

    fn check_for_critical_update(&self) -> bool {
        match self.provider.fetch(CRITICAL_VERSIONS_URL) {
            Ok(bytes) => String::from_utf8_lossy(&bytes).contains("[CRITICAL]"),
            Err(_) => false,
        }
    }
}

/// Lowercase hex encoding of a byte slice (matches
/// `BitConverter.ToString(..).Replace("-","").ToLowerInvariant()`).
pub(crate) fn to_hex_lower(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut s = String::with_capacity(bytes.len() * 2);
    for &b in bytes {
        s.push(HEX[(b >> 4) as usize] as char);
        s.push(HEX[(b & 0x0f) as usize] as char);
    }
    s
}
