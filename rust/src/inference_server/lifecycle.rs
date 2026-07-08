//! lifecycle.rs
//!
//! Model-lifecycle layer, ported from
//! `CircleAI.Inference.Server/Lifecycle/` (`ModelLifecycleTypes.cs`,
//! `IModelLifecycleManager.cs`, `ModelLifecycleManager.cs`,
//! `INativeRuntimeStatus.cs`).
//!
//! The lifecycle manager is the policy gate around the in-memory model registry:
//! it decides whether a load is admitted (VRAM/RAM headroom + duplicate check)
//! and tracks the on-host footprint. The capability probe is injected as a
//! [`HostProfile`] (the C# calls `ICapabilityProbe.ProbeAsync`), cached after the
//! first read exactly like the C# `_cachedProfile`.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Utc};

use crate::inference_server::bridge::{BackendKind, CapabilityTier, IBridgeFactory};
use crate::inference_server::registry::{IInferenceServerModelRegistry, SharedBridge};

/// A minimal host-capability profile the admission gate reads. Mirrors the parts
/// of `HostProfile` the C# `ModelLifecycleManager` uses.
#[derive(Debug, Clone)]
pub struct HostProfile {
    /// Total physical RAM in bytes.
    pub total_physical_memory_bytes: i64,
    /// GPU VRAM ceiling in bytes (0 when no GPU).
    pub gpu_vram_bytes: i64,
}

impl HostProfile {
    /// A generous default profile (64 GiB RAM, 24 GiB VRAM) for tests/hosts.
    pub fn generous() -> Self {
        let gib = 1024i64 * 1024 * 1024;
        Self {
            total_physical_memory_bytes: 64 * gib,
            gpu_vram_bytes: 24 * gib,
        }
    }
}

/// A probe that yields the host profile. The manager calls it at most once and
/// caches the result (mirrors `GetOrProbeAsync`).
pub type CapabilityProbe = Arc<dyn Fn() -> HostProfile + Send + Sync>;

/// What the caller wants to load. Mirrors `ModelLoadDescriptor` — the bridge
/// factory + the (modelId, backend, tier) request key + footprint estimates.
#[derive(Clone)]
pub struct ModelLoadDescriptor {
    pub model_id: String,
    pub backend: BackendKind,
    pub requested_tier: CapabilityTier,
    pub vram_required_bytes: i64,
    pub ram_required_bytes: i64,
    /// Factory invoked only after the admission gate passes.
    pub bridge_factory: Arc<dyn IBridgeFactory>,
}

impl ModelLoadDescriptor {
    /// Constructs a descriptor over an [`IBridgeFactory`].
    pub fn new(
        model_id: impl Into<String>,
        backend: BackendKind,
        requested_tier: CapabilityTier,
        vram_required_bytes: i64,
        ram_required_bytes: i64,
        bridge_factory: Arc<dyn IBridgeFactory>,
    ) -> Self {
        Self {
            model_id: model_id.into(),
            backend,
            requested_tier,
            vram_required_bytes,
            ram_required_bytes,
            bridge_factory,
        }
    }
}

/// Runtime view of one loaded model. Mirrors `ModelLoadState`.
#[derive(Debug, Clone, PartialEq)]
pub struct ModelLoadState {
    pub model_id: String,
    pub backend: BackendKind,
    pub tier: CapabilityTier,
    pub vram_bytes: i64,
    pub ram_bytes: i64,
    pub loaded_at: DateTime<Utc>,
}

/// Outcome enum for a load attempt. Mirrors `LoadOutcome`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LoadOutcome {
    /// Bridge factory ran, registry was updated.
    Loaded,
    /// The model was already loaded — no-op success.
    AlreadyLoaded,
    /// Insufficient VRAM headroom.
    InsufficientVram,
    /// Insufficient RAM headroom.
    InsufficientRam,
    /// Bridge factory failed — registry untouched.
    FactoryFailed,
}

/// Result of a load attempt. Mirrors `LoadResult`.
#[derive(Debug, Clone, PartialEq)]
pub struct LoadResult {
    pub outcome: LoadOutcome,
    pub state: Option<ModelLoadState>,
    pub rationale: String,
}

/// Outcome enum for an unload attempt. Mirrors `UnloadOutcome`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UnloadOutcome {
    /// Model was loaded; bridge was disposed and removed.
    Unloaded,
    /// Model was not loaded; nothing to do.
    NotLoaded,
}

/// Admits/rejects model loads based on current capacity and keeps the
/// authoritative ledger. Sync port of `IModelLifecycleManager`.
pub trait IModelLifecycleManager: Send + Sync {
    /// Try to load `descriptor`, running the admission gate before invoking the
    /// factory.
    fn load(&self, descriptor: ModelLoadDescriptor) -> LoadResult;

    /// Unload the model with the given id.
    fn unload(&self, model_id: &str) -> UnloadOutcome;

    /// Snapshot of every model currently held.
    fn list(&self) -> Vec<ModelLoadState>;

    /// Total VRAM currently allocated across all loaded models.
    fn total_allocated_vram_bytes(&self) -> i64;

    /// Total system RAM currently allocated across all loaded models.
    fn total_allocated_ram_bytes(&self) -> i64;
}

/// Default lifecycle manager. Mirrors `ModelLifecycleManager` including the
/// reserve-before-factory overcommit guard and the roll-back on factory failure.
pub struct ModelLifecycleManager {
    registry: Arc<dyn IInferenceServerModelRegistry>,
    probe: CapabilityProbe,
    loaded: Mutex<BTreeMap<String, ModelLoadState>>,
    cached_profile: Mutex<Option<HostProfile>>,
}

impl ModelLifecycleManager {
    /// Constructs the manager over the registry + capability probe.
    pub fn new(registry: Arc<dyn IInferenceServerModelRegistry>, probe: CapabilityProbe) -> Self {
        Self {
            registry,
            probe,
            loaded: Mutex::new(BTreeMap::new()),
            cached_profile: Mutex::new(None),
        }
    }

    fn get_or_probe(&self) -> HostProfile {
        let mut cache = self.cached_profile.lock().unwrap();
        if let Some(p) = cache.as_ref() {
            return p.clone();
        }
        let p = (self.probe)();
        *cache = Some(p.clone());
        p
    }
}

impl IModelLifecycleManager for ModelLifecycleManager {
    fn total_allocated_vram_bytes(&self) -> i64 {
        self.loaded.lock().unwrap().values().map(|s| s.vram_bytes).sum()
    }

    fn total_allocated_ram_bytes(&self) -> i64 {
        self.loaded.lock().unwrap().values().map(|s| s.ram_bytes).sum()
    }

    fn load(&self, descriptor: ModelLoadDescriptor) -> LoadResult {
        assert!(!descriptor.model_id.trim().is_empty(), "modelId required");

        // Idempotent fast path — already loaded is a success.
        if let Some(existing) = self.loaded.lock().unwrap().get(&descriptor.model_id).cloned() {
            return LoadResult {
                rationale: format!(
                    "Model '{}' is already loaded ({:?}, {:?}).",
                    descriptor.model_id, existing.backend, existing.tier
                ),
                outcome: LoadOutcome::AlreadyLoaded,
                state: Some(existing),
            };
        }

        let profile = self.get_or_probe();

        // VRAM admission — only on GPU-class backends.
        if descriptor.backend.is_gpu_class() {
            let vram_ceiling = profile.gpu_vram_bytes;
            let vram_free = vram_ceiling - self.total_allocated_vram_bytes();
            if vram_free < descriptor.vram_required_bytes {
                let mib = 1024 * 1024;
                return LoadResult {
                    outcome: LoadOutcome::InsufficientVram,
                    state: None,
                    rationale: format!(
                        "Need {} MiB VRAM, have {} MiB free ({} MiB of {} MiB in use).",
                        descriptor.vram_required_bytes / mib,
                        vram_free.max(0) / mib,
                        self.total_allocated_vram_bytes() / mib,
                        vram_ceiling / mib
                    ),
                };
            }
        }

        // RAM admission — always enforced.
        let ram_free = profile.total_physical_memory_bytes - self.total_allocated_ram_bytes();
        if ram_free < descriptor.ram_required_bytes {
            let mib = 1024 * 1024;
            return LoadResult {
                outcome: LoadOutcome::InsufficientRam,
                state: None,
                rationale: format!(
                    "Need {} MiB RAM, have {} MiB free ({} MiB of {} MiB in use).",
                    descriptor.ram_required_bytes / mib,
                    ram_free.max(0) / mib,
                    self.total_allocated_ram_bytes() / mib,
                    profile.total_physical_memory_bytes / mib
                ),
            };
        }

        // Reserve before invoking the factory so concurrent loads see the new
        // accounting (mirrors the C# `_gate` lock + reserve).
        let reserve_state = ModelLoadState {
            model_id: descriptor.model_id.clone(),
            backend: descriptor.backend,
            tier: descriptor.requested_tier,
            vram_bytes: descriptor.vram_required_bytes,
            ram_bytes: descriptor.ram_required_bytes,
            loaded_at: Utc::now(),
        };
        {
            let mut loaded = self.loaded.lock().unwrap();
            if let Some(race_winner) = loaded.get(&descriptor.model_id).cloned() {
                return LoadResult {
                    rationale: format!(
                        "Model '{}' was loaded by a concurrent request.",
                        descriptor.model_id
                    ),
                    outcome: LoadOutcome::AlreadyLoaded,
                    state: Some(race_winner),
                };
            }
            loaded.insert(descriptor.model_id.clone(), reserve_state.clone());
        }

        match descriptor.bridge_factory.create(
            &descriptor.model_id,
            descriptor.backend,
            descriptor.requested_tier,
        ) {
            Ok(bridge) => {
                let shared: SharedBridge = bridge;
                self.registry.register(&descriptor.model_id, shared);
                LoadResult {
                    rationale: format!(
                        "Loaded '{}' on {:?} at {:?}.",
                        descriptor.model_id, descriptor.backend, descriptor.requested_tier
                    ),
                    outcome: LoadOutcome::Loaded,
                    state: Some(reserve_state),
                }
            }
            Err(ex) => {
                // Roll the reservation back.
                self.loaded.lock().unwrap().remove(&descriptor.model_id);
                LoadResult {
                    outcome: LoadOutcome::FactoryFailed,
                    state: None,
                    rationale: format!(
                        "Bridge factory for '{}' failed: {}",
                        descriptor.model_id, ex
                    ),
                }
            }
        }
    }

    fn unload(&self, model_id: &str) -> UnloadOutcome {
        assert!(!model_id.trim().is_empty(), "modelId required");
        if self.loaded.lock().unwrap().remove(model_id).is_none() {
            return UnloadOutcome::NotLoaded;
        }
        // Drop it from the registry so any new request resolves to null. The
        // Arc keeps in-flight callers alive until they finish (the dispose
        // contract on bridges promises to tolerate that).
        self.registry.deregister(model_id);
        UnloadOutcome::Unloaded
    }

    fn list(&self) -> Vec<ModelLoadState> {
        self.loaded.lock().unwrap().values().cloned().collect()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// INativeRuntimeStatus
// ─────────────────────────────────────────────────────────────────────────────

/// Last-known native-runtime paths produced by the bridge factory, surfaced
/// through diagnostics. Mirrors `NativeRuntimePrep.NativeRuntimePaths`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NativeRuntimePaths {
    /// Absolute path to the resolved MNN core library.
    pub mnn_core_path: String,
    /// Absolute path to the resolved mnnbridge shim.
    pub bridge_path: String,
    /// Root the runtime bundle was extracted to.
    pub extracted_root: String,
}

/// Singleton holder of the last-known native-runtime paths. Sync port of
/// `INativeRuntimeStatus`.
pub trait INativeRuntimeStatus: Send + Sync {
    /// Most recent prep result, or `None` before the first model load.
    fn latest(&self) -> Option<NativeRuntimePaths>;
    /// Record the result of a successful prep run.
    fn update(&self, paths: NativeRuntimePaths);
}

/// Default thread-safe holder. Mirrors `NativeRuntimeStatus`.
#[derive(Debug, Default)]
pub struct NativeRuntimeStatus {
    latest: Mutex<Option<NativeRuntimePaths>>,
}

impl NativeRuntimeStatus {
    /// Constructs an empty holder.
    pub fn new() -> Self {
        Self {
            latest: Mutex::new(None),
        }
    }
}

impl INativeRuntimeStatus for NativeRuntimeStatus {
    fn latest(&self) -> Option<NativeRuntimePaths> {
        self.latest.lock().unwrap().clone()
    }

    fn update(&self, paths: NativeRuntimePaths) {
        *self.latest.lock().unwrap() = Some(paths);
    }
}
