//! registry.rs
//!
//! ModelRegistryService + check_for_upgrades + write_installed_manifest.
//!
//! This is the byte-for-byte parity port of C# ModelRegistryService.CheckForUpgradesAsync.

use chrono::Utc;
use std::collections::HashMap;
use std::path::{Path, PathBuf};

use crate::catalog::{ModelEntry, ModelRegistry, ModelScopeCatalogClient};
use crate::models_v15::{BundleFile, InstalledManifest, UpgradeInfo, UpgradeReason};

pub struct ModelRegistryService {
    registry: Option<ModelRegistry>,
}

impl ModelRegistryService {
    pub fn new() -> Self {
        Self { registry: None }
    }

    pub fn from_catalog(client: &ModelScopeCatalogClient) -> Self {
        Self { registry: client.load_from_disk() }
    }

    pub fn set_registry(&mut self, reg: ModelRegistry) {
        self.registry = Some(reg);
    }

    pub fn all_models(&self) -> Vec<ModelEntry> {
        self.registry
            .as_ref()
            .map(|r| r.models.clone())
            .unwrap_or_default()
    }

    pub fn get_latest_model(&self, name: &str) -> Option<ModelEntry> {
        let lname = name.to_ascii_lowercase();
        self.registry
            .as_ref()?
            .models
            .iter()
            .find(|m| m.name.to_ascii_lowercase() == lname)
            .cloned()
    }

    /// Walks the storage dir and emits an `UpgradeInfo` for every installed
    /// model whose manifest is missing or drifts from the catalog.
    pub fn check_for_upgrades(&self, storage_directory: &Path) -> Vec<UpgradeInfo> {
        assert!(
            !storage_directory.as_os_str().is_empty(),
            "storage_directory must not be empty"
        );
        let now = Utc::now();
        let mut out = Vec::new();

        for entry in self.all_models() {
            let model_dir = storage_directory.join(&entry.name);
            if !model_dir.is_dir() {
                continue;
            }
            let manifest_path = model_dir.join("installed.json");
            let manifest = read_manifest(&manifest_path);
            let Some(m) = manifest else {
                out.push(UpgradeInfo {
                    model_id: entry.name.clone(),
                    installed_version: None,
                    available_version: entry.version.clone(),
                    reason: UpgradeReason::Unknown,
                    estimated_download_bytes: entry.total_bytes as i64,
                    detected_at: now,
                });
                continue;
            };

            let version_changed = m.version != entry.version;
            let (sha_changed, drift_bytes) = compare_bundle_sha(&m.files, &entry.bundle_files);
            if !version_changed && !sha_changed {
                continue;
            }
            let reason = match (version_changed, sha_changed) {
                (true, true) => UpgradeReason::Both,
                (true, false) => UpgradeReason::VersionChanged,
                (false, true) => UpgradeReason::ShaChanged,
                (false, false) => unreachable!(),
            };
            out.push(UpgradeInfo {
                model_id: entry.name.clone(),
                installed_version: Some(m.version.clone()),
                available_version: entry.version.clone(),
                reason,
                estimated_download_bytes: drift_bytes,
                detected_at: now,
            });
        }
        out
    }
}

impl Default for ModelRegistryService {
    fn default() -> Self {
        Self::new()
    }
}

fn read_manifest(path: &Path) -> Option<InstalledManifest> {
    let bytes = std::fs::read(path).ok()?;
    serde_json::from_slice(&bytes).ok()
}

fn compare_bundle_sha(installed: &[BundleFile], available: &[BundleFile]) -> (bool, i64) {
    if available.is_empty() {
        return (false, 0);
    }
    let by_name: HashMap<&str, &BundleFile> =
        installed.iter().map(|f| (f.name.as_str(), f)).collect();
    let mut drift = false;
    let mut bytes: i64 = 0;
    for av in available {
        match by_name.get(av.name.as_str()) {
            None => {
                drift = true;
                bytes += av.size_bytes;
            }
            Some(inst) if !inst.sha256.eq_ignore_ascii_case(&av.sha256) => {
                drift = true;
                bytes += av.size_bytes;
            }
            _ => {}
        }
    }
    (drift, bytes)
}

/// Writes installed.json after a successful model install. Best-effort —
/// silently no-ops on IO error so a failed write never breaks an otherwise
/// successful install.
pub fn write_installed_manifest(
    model_dir: &Path,
    model_id: &str,
    version: &str,
    repo: Option<&str>,
    bundle_files: Vec<BundleFile>,
) {
    let _ = (|| -> std::io::Result<()> {
        std::fs::create_dir_all(model_dir)?;
        let total: i64 = bundle_files.iter().map(|f| f.size_bytes.max(0)).sum();
        let manifest = InstalledManifest {
            model_id: model_id.to_string(),
            version: version.to_string(),
            repo: repo.map(|s| s.to_string()),
            total_bytes: total,
            files: bundle_files,
            installed_at_utc: Utc::now(),
        };
        let bytes = serde_json::to_vec_pretty(&manifest)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::Other, e))?;
        let path = model_dir.join("installed.json");
        std::fs::write(path, bytes)
    })();
}

pub fn _path_join(a: impl AsRef<Path>, b: impl AsRef<Path>) -> PathBuf {
    a.as_ref().join(b)
}
