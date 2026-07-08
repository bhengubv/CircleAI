//! manager.rs
//!
//! Port of:
//!   - `CircleAI.Core.IModelManager`
//!   - `CircleAI.Core.LocalModelManager`
//!
//! `LocalModelManager` resolves a model directory, downloading via an
//! [`IModelDownloader`] when the model is missing, and verifies the
//! `pytorch_model.bin` checksum. Checksum comparison uses the crate's real
//! SHA-256.

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use crate::memory::multimodal::sha256;

use super::downloader::{IModelDownloader, ModelDownloader};
use super::loader::ModelLoaderError;
use super::sources::{IModelSource, ModelScopeSource};

/// The canonical weight file the manager checks for / verifies.
const PYTORCH_MODEL_BIN: &str = "pytorch_model.bin";

/// Model-manager contract. Mirrors `CircleAI.Core.IModelManager` (sync per crate
/// convention; `IDisposable` maps to `Drop`).
pub trait IModelManager {
    /// Resolve (downloading if needed) the local directory for `model_id`.
    fn get_model_path(&self, model_id: &str) -> Result<PathBuf, ModelLoaderError>;

    /// Verify the model at `model_path` against `expected_checksum` (raw bytes).
    fn verify_model(
        &self,
        model_path: &Path,
        expected_checksum: &[u8],
    ) -> Result<bool, ModelLoaderError>;
}

/// Local model manager. Mirrors `CircleAI.Core.LocalModelManager`.
pub struct LocalModelManager {
    downloader: Option<Arc<dyn IModelDownloader + Send + Sync>>,
    models_directory: PathBuf,
}

impl LocalModelManager {
    /// Construct with an optional repository URL and models directory. When a URL
    /// is supplied, a [`ModelDownloader`] backed by a [`ModelScopeSource`] is
    /// created (ModelScope is the sole source). The directory is created if
    /// missing.
    ///
    /// Mirrors `LocalModelManager(Uri?, string)`.
    pub fn new(
        model_repository_url: Option<&str>,
        models_directory: impl AsRef<Path>,
    ) -> Result<Self, ModelLoaderError> {
        let models_directory = models_directory.as_ref().to_path_buf();

        let downloader: Option<Arc<dyn IModelDownloader + Send + Sync>> =
            if model_repository_url.is_some() {
                let sources: Vec<Box<dyn IModelSource + Send + Sync>> =
                    vec![Box::new(ModelScopeSource::new())];
                let dl = ModelDownloader::new(sources).map_err(|e| ModelLoaderError::Source(e.0))?;
                Some(Arc::new(dl))
            } else {
                None
            };

        fs::create_dir_all(&models_directory)
            .map_err(|e| ModelLoaderError::Source(e.to_string()))?;

        Ok(Self {
            downloader,
            models_directory,
        })
    }

    /// Construct with an explicit downloader and models directory.
    /// Mirrors `LocalModelManager(IModelDownloader, string)`.
    pub fn with_downloader(
        downloader: Arc<dyn IModelDownloader + Send + Sync>,
        models_directory: impl AsRef<Path>,
    ) -> Result<Self, ModelLoaderError> {
        let models_directory = models_directory.as_ref().to_path_buf();
        fs::create_dir_all(&models_directory)
            .map_err(|e| ModelLoaderError::Source(e.to_string()))?;
        Ok(Self {
            downloader: Some(downloader),
            models_directory,
        })
    }

    fn sanitize_model_id(model_id: &str) -> String {
        model_id.replace('/', "_").replace('\\', "_")
    }

    /// The verifying overload of `GetModelPathAsync(modelId, expectedChecksum)`.
    /// Downloads when absent, then verifies the weight file if a checksum is
    /// supplied.
    pub fn get_model_path_verified(
        &self,
        model_id: &str,
        expected_checksum: Option<&[u8]>,
    ) -> Result<PathBuf, ModelLoaderError> {
        let model_path = self.models_directory.join(Self::sanitize_model_id(model_id));

        let weight = model_path.join(PYTORCH_MODEL_BIN);
        if !model_path.is_dir() || !weight.is_file() {
            let downloader = self.downloader.as_ref().ok_or_else(|| {
                ModelLoaderError::InvalidOperation(
                    "Model not found and no downloader configured".into(),
                )
            })?;
            downloader.download_model(model_id, &model_path)?;
        }

        if let Some(expected) = expected_checksum {
            if !expected.is_empty() {
                let actual = compute_file_checksum(&weight)?;
                if actual != expected {
                    return Err(ModelLoaderError::InvalidData(format!(
                        "Model checksum verification failed for '{model_id}'. \
                         The file may be corrupt or tampered with."
                    )));
                }
            }
        }

        Ok(model_path)
    }
}

impl IModelManager for LocalModelManager {
    fn get_model_path(&self, model_id: &str) -> Result<PathBuf, ModelLoaderError> {
        self.get_model_path_verified(model_id, None)
    }

    fn verify_model(
        &self,
        model_path: &Path,
        expected_checksum: &[u8],
    ) -> Result<bool, ModelLoaderError> {
        // Verify the canonical weight file inside the model directory, or the
        // path itself if it is already a file.
        let target = if model_path.is_dir() {
            model_path.join(PYTORCH_MODEL_BIN)
        } else {
            model_path.to_path_buf()
        };
        if !target.exists() {
            return Ok(false);
        }
        let actual = compute_file_checksum(&target)?;
        Ok(actual == expected_checksum)
    }
}

fn compute_file_checksum(file_path: &Path) -> Result<Vec<u8>, ModelLoaderError> {
    let data = fs::read(file_path).map_err(|e| ModelLoaderError::Source(e.to_string()))?;
    Ok(sha256(&data).to_vec())
}
