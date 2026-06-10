//! catalog.rs
//!
//! ModelEntry + ModelRegistry types. ModelScopeCatalogClient with disk-cache
//! support + cadence enum. HTTP fetch is feature-gated under `catalog-http`
//! (off by default) so the rest of the crate stays dep-light.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use crate::models_v15::BundleFile;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum CatalogCadence {
    Never = 0,
    Manual = 1,
    OnStartup = 2,
    Daily = 3,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ModelEntry {
    pub name: String,
    pub version: String,
    pub quantization: String,
    pub repo: String,
    #[serde(rename = "totalBytes")]
    pub total_bytes: u64,
    #[serde(default, rename = "bundleFiles")]
    pub bundle_files: Vec<BundleFile>,
    #[serde(default, rename = "capabilities", skip_serializing_if = "Option::is_none")]
    pub capabilities: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ModelRegistry {
    #[serde(rename = "registryUrl")]
    pub registry_url: String,
    #[serde(rename = "lastUpdated")]
    pub last_updated: DateTime<Utc>,
    pub models: Vec<ModelEntry>,
}

/// Verifies a fetched catalog payload before trusting it.
pub trait ICatalogSignatureVerifier: Send + Sync {
    fn verify(&self, bytes: &[u8]) -> bool;
}

/// Default verifier — accepts nothing. Real deployments should provide one.
pub struct NullCatalogSignatureVerifier;
impl ICatalogSignatureVerifier for NullCatalogSignatureVerifier {
    fn verify(&self, _bytes: &[u8]) -> bool {
        false
    }
}

/// Catalog client that knows how to load the registry from disk and (when the
/// `catalog-http` feature is on) refresh it from the registry URL.
pub struct ModelScopeCatalogClient {
    pub cache_path: std::path::PathBuf,
    pub cadence: CatalogCadence,
    pub verifier: Box<dyn ICatalogSignatureVerifier>,
}

impl ModelScopeCatalogClient {
    pub fn new(
        cache_path: impl Into<std::path::PathBuf>,
        cadence: CatalogCadence,
        verifier: Box<dyn ICatalogSignatureVerifier>,
    ) -> Self {
        Self { cache_path: cache_path.into(), cadence, verifier }
    }

    /// Reads the cached registry from disk. Returns `None` when missing or
    /// unreadable.
    pub fn load_from_disk(&self) -> Option<ModelRegistry> {
        let bytes = std::fs::read(&self.cache_path).ok()?;
        serde_json::from_slice(&bytes).ok()
    }

    /// Writes a registry to disk (atomic-ish via tmp-and-rename).
    pub fn save_to_disk(&self, reg: &ModelRegistry) -> std::io::Result<()> {
        if let Some(parent) = self.cache_path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let tmp = self.cache_path.with_extension("json.tmp");
        let bytes = serde_json::to_vec_pretty(reg)?;
        std::fs::write(&tmp, &bytes)?;
        std::fs::rename(&tmp, &self.cache_path)?;
        Ok(())
    }
}
