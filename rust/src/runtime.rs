//! runtime.rs
//!
//! Port of `CircleAI.Runtime/Backends/` + `CircleAI.Runtime/Capabilities/` — the
//! deterministic hardware -> backend routing table plus the host-capability
//! record model that feeds it.
//!
//!   * [`BackendKind`] — the MNN execution backend an operator can route to.
//!   * [`CapabilityTier`] — the Qwen/DeepSeek/GLM/Kimi model-size band a host can run.
//!   * [`OperatingSystemKind`] / [`ArchitectureKind`] / [`GpuVendor`] / [`NpuVendor`] —
//!     the enums the probe normalises onto.
//!   * [`GpuInfo`] / [`NpuInfo`] / [`HostProfile`] — the record model.
//!   * [`IBackendSelector`] (+ [`BackendSelection`]) and [`BackendSelector`] — the
//!     deterministic, no-I/O, never-fails selection logic with VRAM / unified-memory
//!     tier clamping. Ported 1:1 from `BackendSelector.cs`.
//!   * [`ICapabilityProbe`] and [`CapabilityProbe`] — the probe SEAM. The C#
//!     reference reads WMI / `/proc` / `sysctl` / `Build.*`; those platform bodies
//!     are host-specific and are INJECTED here. [`CapabilityProbe::new`] wraps a
//!     host-supplied probe; [`CapabilityProbe::unknown`] is the fail-open default
//!     that returns an `Unknown` [`HostProfile`] (mirrors `UnknownCapabilityProbe`).
//!
//! C# async `Task<HostProfile>` maps to `#[async_trait]`. The selector itself is
//! synchronous (no I/O) and must NEVER panic or return a non-runnable combo — every
//! host can run the CPU backend at Tier 0 as a last resort.

use async_trait::async_trait;
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

/// One gibibyte, in bytes. Matches the C# `GiB` constant.
const GIB: i64 = 1024 * 1024 * 1024;

// ─────────────────────────────────────────────────────────────────────────────
// BackendKind
// ─────────────────────────────────────────────────────────────────────────────

/// MNN execution backend. Picked by [`IBackendSelector`] based on the host's
/// [`HostProfile`]. Values match the runtime-package layout shipped by Alibaba MNN.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum BackendKind {
    /// Pure-CPU SIMD backend. Always available.
    Cpu = 0,
    /// NVIDIA CUDA. Requires CUDA toolkit + NVIDIA driver.
    Cuda = 1,
    /// Vulkan compute. Cross-vendor (AMD, Intel, Apple via MoltenVK).
    Vulkan = 2,
    /// OpenCL. Mostly used on older AMD/Intel Linux deployments.
    OpenCL = 3,
    /// Apple Metal. Apple Silicon and Intel mac discrete GPUs.
    Metal = 4,
    /// Huawei Ascend (CANN). Atlas + Ascend 310/910 + Kirin NPU.
    Ascend = 5,
    /// Cambricon MLU.
    Cambricon = 6,
    /// Apple Core ML — used for ANE acceleration on Apple Silicon.
    CoreML = 7,
}

// ─────────────────────────────────────────────────────────────────────────────
// CapabilityTier
// ─────────────────────────────────────────────────────────────────────────────

/// Capability tier that maps to a Qwen / DeepSeek / GLM / Kimi model size band.
/// Higher tiers require more RAM / VRAM. `Tier0` is the always-runnable floor
/// (~600 MB footprint); `Tier4` targets 24 GB+ VRAM frontier models.
///
/// Ordinals are ordered — `Tier0 < Tier1 < ... < Tier4` — so the selector can
/// clamp a requested tier down to a ceiling.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum CapabilityTier {
    /// Tier 0 — Qwen3-0.6B class. ~600 MB. CPU-friendly. Always available.
    Tier0Tiny = 0,
    /// Tier 1 — 1.7B-4B class. ~2 GB. CPU usable but slow; GPU preferred.
    Tier1Small = 1,
    /// Tier 2 — 7B-9B class Q4. ~6 GB. Needs >=8 GB VRAM or >=16 GB RAM.
    Tier2Medium = 2,
    /// Tier 3 — 14B-32B class Q4. ~12 GB. Needs >=12 GB VRAM or >=32 GB RAM.
    Tier3Large = 3,
    /// Tier 4 — 70B+ class Q4, or 32B Q6. ~24 GB+. Frontier / data-centre tier.
    Tier4Frontier = 4,
}

// ─────────────────────────────────────────────────────────────────────────────
// Capability enums (probe normalisation targets)
// ─────────────────────────────────────────────────────────────────────────────

/// OS family the probe recognised.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum OperatingSystemKind {
    /// Probe could not identify the OS.
    Unknown = 0,
    /// Microsoft Windows desktop / Server.
    Windows = 1,
    /// Any Linux distribution.
    Linux = 2,
    /// Apple macOS.
    MacOS = 3,
    /// Google Android (including Android-derived OSes that report as Linux + Bionic).
    Android = 4,
    /// Apple iOS / iPadOS / tvOS / watchOS.
    IOS = 5,
    /// Huawei HarmonyOS / OpenHarmony.
    HarmonyOS = 6,
}

/// CPU architecture family.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum ArchitectureKind {
    /// Probe could not identify the architecture.
    Unknown = 0,
    /// 32-bit Intel/AMD.
    X86 = 1,
    /// 64-bit Intel/AMD (AMD64 / Intel 64).
    X64 = 2,
    /// 32-bit ARM (Cortex-A, etc.).
    Arm = 3,
    /// 64-bit ARM (ARMv8 / Apple Silicon / Cortex-A76+).
    Arm64 = 4,
    /// Loongson LoongArch64 (mainland China sovereign arch).
    Loong64 = 5,
}

/// GPU vendor identifier.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum GpuVendor {
    /// No GPU detected, or vendor unknown.
    None = 0,
    /// NVIDIA Corp.
    Nvidia = 1,
    /// Advanced Micro Devices.
    Amd = 2,
    /// Intel Corp. (integrated and Arc).
    Intel = 3,
    /// Apple Silicon GPU (M1/M2/M3/M4 family).
    Apple = 4,
    /// Qualcomm Adreno (Snapdragon mobile / compute).
    Qualcomm = 5,
    /// Huawei Maleoon / Mali-licensed GPUs on Kirin SoCs.
    Huawei = 6,
    /// ARM Mali (third-party SoCs not covered by other vendors).
    Arm = 7,
    /// Vendor was identified but is not in this enum yet.
    Other = 99,
}

/// NPU / neural accelerator vendor identifier.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum NpuVendor {
    /// No NPU detected.
    None = 0,
    /// Apple Neural Engine (ANE) on Apple Silicon.
    AppleNeuralEngine = 1,
    /// Qualcomm Hexagon DSP / NPU.
    QualcommHexagon = 2,
    /// Huawei Ascend (data-centre + Atlas + Kirin NPU).
    HuaweiAscend = 3,
    /// Intel VPU (Movidius / Meteor Lake NPU).
    IntelVpu = 4,
    /// Cambricon MLU.
    CambriconMlu = 5,
    /// Vendor was identified but is not in this enum yet.
    Other = 99,
}

// ─────────────────────────────────────────────────────────────────────────────
// Record model: GpuInfo / NpuInfo / HostProfile
// ─────────────────────────────────────────────────────────────────────────────

/// Discovered GPU details. 1:1 with the C# `sealed record GpuInfo`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct GpuInfo {
    /// Vendor family.
    pub vendor: GpuVendor,
    /// Marketing name (e.g. `"NVIDIA GeForce RTX 4080"`).
    pub model: String,
    /// Dedicated video memory in bytes. `0` when the probe could not determine.
    pub vram_bytes: i64,
    /// Driver version string when known.
    pub driver_version: Option<String>,
}

impl GpuInfo {
    /// Creates a new [`GpuInfo`].
    pub fn new(
        vendor: GpuVendor,
        model: impl Into<String>,
        vram_bytes: i64,
        driver_version: Option<String>,
    ) -> Self {
        Self {
            vendor,
            model: model.into(),
            vram_bytes,
            driver_version,
        }
    }
}

/// Discovered NPU details. 1:1 with the C# `sealed record NpuInfo`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NpuInfo {
    /// Vendor family.
    pub vendor: NpuVendor,
    /// Marketing name (e.g. `"Apple Neural Engine 16-core"`).
    pub model: String,
}

impl NpuInfo {
    /// Creates a new [`NpuInfo`].
    pub fn new(vendor: NpuVendor, model: impl Into<String>) -> Self {
        Self {
            vendor,
            model: model.into(),
        }
    }
}

/// Full host capability snapshot — the result of an [`ICapabilityProbe::probe`]
/// call. 1:1 with the C# `sealed record HostProfile`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct HostProfile {
    /// OS family.
    pub os: OperatingSystemKind,
    /// OS version string (e.g. `"10.0.22631"`, `"14.4.1"`).
    pub os_version: String,
    /// CPU architecture family.
    pub arch: ArchitectureKind,
    /// CPU marketing name (e.g. `"Apple M2 Pro"`, `"AMD Ryzen 9 7950X"`).
    pub cpu_model: String,
    /// Logical CPU core count (includes SMT siblings on x86 HT).
    pub logical_core_count: i32,
    /// Physical CPU core count (HT pairs counted once).
    pub physical_core_count: i32,
    /// Installed RAM in bytes.
    pub total_physical_memory_bytes: i64,
    /// GPU details. `None` when no usable GPU was detected.
    pub gpu: Option<GpuInfo>,
    /// NPU details. `None` when no NPU was detected.
    pub npu: Option<NpuInfo>,
    /// UTC timestamp the probe was taken at.
    pub probed_at: DateTime<Utc>,
}

impl HostProfile {
    /// Convenience flag — true when [`HostProfile::gpu`] is present and has at
    /// least `minimum_vram_bytes` of dedicated VRAM. Defaults to 2 GiB via
    /// [`HostProfile::has_usable_gpu_default`].
    pub fn has_usable_gpu(&self, minimum_vram_bytes: i64) -> bool {
        matches!(&self.gpu, Some(g) if g.vram_bytes >= minimum_vram_bytes)
    }

    /// [`HostProfile::has_usable_gpu`] with the C# default of 2 GiB.
    pub fn has_usable_gpu_default(&self) -> bool {
        self.has_usable_gpu(2 * GIB)
    }

    /// True when the host runs on a 64-bit architecture (X64, Arm64, Loong64).
    pub fn is_64bit(&self) -> bool {
        matches!(
            self.arch,
            ArchitectureKind::X64 | ArchitectureKind::Arm64 | ArchitectureKind::Loong64
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IBackendSelector / BackendSelection
// ─────────────────────────────────────────────────────────────────────────────

/// Result of an [`IBackendSelector::select`] call. 1:1 with the C#
/// `sealed record BackendSelection`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BackendSelection {
    /// Chosen MNN execution backend.
    pub backend: BackendKind,
    /// Tier the host can actually run. Equal to or lower than the requested
    /// tier — the selector downgrades when compute is short.
    pub actual_tier: CapabilityTier,
    /// Human-readable explanation of why this combination was chosen. Suitable
    /// for logging and surfacing in operator dashboards.
    pub rationale: String,
}

impl BackendSelection {
    fn new(backend: BackendKind, actual_tier: CapabilityTier, rationale: impl Into<String>) -> Self {
        Self {
            backend,
            actual_tier,
            rationale: rationale.into(),
        }
    }
}

/// Picks the MNN backend and model tier for a given host. Implementations MUST
/// NEVER panic and MUST NEVER return a non-runnable combination — every host can
/// run the CPU backend at Tier 0 as a last resort.
pub trait IBackendSelector {
    /// Pick the best [`BackendKind`] + [`CapabilityTier`] combo for the given
    /// host. `requested_tier` is the upper bound — the returned tier may be
    /// lower if the host cannot run it.
    fn select(&self, profile: &HostProfile, requested_tier: CapabilityTier) -> BackendSelection;
}

/// Default [`IBackendSelector`]. Deterministic; no I/O; safe to call on hot
/// paths. The selection logic is intentionally explicit so operators can predict
/// routing without running the code. Ported 1:1 from `BackendSelector.cs`.
#[derive(Debug, Clone, Copy, Default)]
pub struct BackendSelector;

impl BackendSelector {
    /// Construct the default selector.
    pub fn new() -> Self {
        Self
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    fn clamp_tier(requested: CapabilityTier, ceiling: CapabilityTier) -> CapabilityTier {
        if requested <= ceiling {
            requested
        } else {
            ceiling
        }
    }

    fn tier_for_vram(vram_bytes: i64) -> CapabilityTier {
        if vram_bytes >= 24 * GIB {
            CapabilityTier::Tier4Frontier
        } else if vram_bytes >= 12 * GIB {
            CapabilityTier::Tier3Large
        } else if vram_bytes >= 8 * GIB {
            CapabilityTier::Tier2Medium
        } else if vram_bytes >= 4 * GIB {
            CapabilityTier::Tier1Small
        } else {
            CapabilityTier::Tier0Tiny
        }
    }

    fn tier_for_unified_memory(ram_bytes: i64) -> CapabilityTier {
        // Apple Silicon shares one pool — be more conservative because the OS,
        // app, and graphics surface all consume from the same RAM.
        if ram_bytes >= 64 * GIB {
            CapabilityTier::Tier4Frontier
        } else if ram_bytes >= 32 * GIB {
            CapabilityTier::Tier3Large
        } else if ram_bytes >= 16 * GIB {
            CapabilityTier::Tier2Medium
        } else if ram_bytes >= 8 * GIB {
            CapabilityTier::Tier1Small
        } else {
            CapabilityTier::Tier0Tiny
        }
    }

    fn tier_for_cpu_ram(ram_bytes: i64) -> CapabilityTier {
        if ram_bytes >= 64 * GIB {
            CapabilityTier::Tier3Large // Server CPU with lots of RAM
        } else if ram_bytes >= 32 * GIB {
            CapabilityTier::Tier2Medium
        } else if ram_bytes >= 16 * GIB {
            CapabilityTier::Tier1Small
        } else if ram_bytes >= 8 * GIB {
            CapabilityTier::Tier1Small
        } else {
            CapabilityTier::Tier0Tiny
        }
    }
}

impl IBackendSelector for BackendSelector {
    fn select(&self, profile: &HostProfile, requested_tier: CapabilityTier) -> BackendSelection {
        // ── 1. Apple Silicon — Metal + ANE coexist via unified memory ──────────
        if profile.os == OperatingSystemKind::MacOS
            && profile.arch == ArchitectureKind::Arm64
            && matches!(&profile.gpu, Some(g) if g.vendor == GpuVendor::Apple)
        {
            let tier = Self::clamp_tier(
                requested_tier,
                Self::tier_for_unified_memory(profile.total_physical_memory_bytes),
            );
            return BackendSelection::new(
                BackendKind::Metal,
                tier,
                format!(
                    "Apple Silicon ({}); Metal over unified-memory GPU; tier capped to {:?} by {} GiB unified RAM.",
                    profile.cpu_model,
                    tier,
                    profile.total_physical_memory_bytes / GIB
                ),
            );
        }

        // ── 2. NVIDIA + CUDA — best on Linux + Windows ─────────────────────────
        if let Some(g) = &profile.gpu {
            if g.vendor == GpuVendor::Nvidia && g.vram_bytes >= 4 * GIB {
                let tier = Self::clamp_tier(requested_tier, Self::tier_for_vram(g.vram_bytes));
                return BackendSelection::new(
                    BackendKind::Cuda,
                    tier,
                    format!(
                        "NVIDIA {} with {} GiB VRAM; CUDA backend; tier capped to {:?} by VRAM.",
                        g.model,
                        g.vram_bytes / GIB,
                        tier
                    ),
                );
            }
        }

        // ── 3. Huawei Ascend NPU — Chinese data-centre + Kirin laptops ─────────
        if let Some(npu) = &profile.npu {
            if npu.vendor == NpuVendor::HuaweiAscend {
                let tier = Self::clamp_tier(requested_tier, CapabilityTier::Tier3Large);
                return BackendSelection::new(
                    BackendKind::Ascend,
                    tier,
                    format!(
                        "Huawei Ascend NPU detected ({}); Ascend (CANN) backend; tier capped to {:?}.",
                        npu.model, tier
                    ),
                );
            }
        }

        // ── 4. Cambricon MLU — Chinese accelerator ─────────────────────────────
        if let Some(npu) = &profile.npu {
            if npu.vendor == NpuVendor::CambriconMlu {
                let tier = Self::clamp_tier(requested_tier, CapabilityTier::Tier3Large);
                return BackendSelection::new(
                    BackendKind::Cambricon,
                    tier,
                    format!("Cambricon MLU detected; Cambricon backend; tier capped to {:?}.", tier),
                );
            }
        }

        // ── 5. AMD / Intel discrete GPU — Vulkan ───────────────────────────────
        if let Some(g) = &profile.gpu {
            if (g.vendor == GpuVendor::Amd || g.vendor == GpuVendor::Intel) && g.vram_bytes >= 4 * GIB
            {
                let tier = Self::clamp_tier(requested_tier, Self::tier_for_vram(g.vram_bytes));
                return BackendSelection::new(
                    BackendKind::Vulkan,
                    tier,
                    format!(
                        "{:?} {} with {} GiB VRAM; Vulkan backend; tier capped to {:?} by VRAM.",
                        g.vendor,
                        g.model,
                        g.vram_bytes / GIB,
                        tier
                    ),
                );
            }
        }

        // ── 6. Qualcomm Hexagon NPU on Android / Snapdragon X — OpenCL ────────
        // Hexagon is most reliable via OpenCL on Android; CoreML-equivalent
        // bindings on Windows-on-Snapdragon are still maturing.
        let qualcomm_npu = matches!(&profile.npu, Some(n) if n.vendor == NpuVendor::QualcommHexagon);
        let qualcomm_gpu = matches!(&profile.gpu, Some(g) if g.vendor == GpuVendor::Qualcomm);
        if qualcomm_npu || qualcomm_gpu {
            let tier = Self::clamp_tier(requested_tier, CapabilityTier::Tier1Small);
            return BackendSelection::new(
                BackendKind::OpenCL,
                tier,
                format!(
                    "Qualcomm Snapdragon platform; OpenCL backend (Adreno/Hexagon shared compute); tier capped to {:?}.",
                    tier
                ),
            );
        }

        // ── 7. ARM Mali via Vulkan (MediaTek, Exynos, Tensor) ──────────────────
        if let Some(g) = &profile.gpu {
            if matches!(g.vendor, GpuVendor::Arm | GpuVendor::Huawei) {
                let tier = Self::clamp_tier(requested_tier, CapabilityTier::Tier1Small);
                return BackendSelection::new(
                    BackendKind::Vulkan,
                    tier,
                    format!(
                        "ARM/Mali class GPU ({}); Vulkan backend; tier capped to {:?}.",
                        g.model, tier
                    ),
                );
            }
        }

        // ── 8. CPU fallback — always selectable ────────────────────────────────
        let cpu_tier = Self::clamp_tier(
            requested_tier,
            Self::tier_for_cpu_ram(profile.total_physical_memory_bytes),
        );
        BackendSelection::new(
            BackendKind::Cpu,
            cpu_tier,
            format!(
                "No usable accelerator detected; CPU SIMD backend on {} ({} logical cores, {} GiB RAM); tier capped to {:?} by available RAM.",
                profile.cpu_model,
                profile.logical_core_count,
                profile.total_physical_memory_bytes / GIB,
                cpu_tier
            ),
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ICapabilityProbe / CapabilityProbe (platform seam)
// ─────────────────────────────────────────────────────────────────────────────

/// Discovers the host's hardware capabilities and returns a normalised
/// [`HostProfile`]. Implementations are OS-specific and INJECTED — the C#
/// reference reads WMI (Windows), `/proc` (Linux), `sysctl` (macOS), and
/// `Build.*` (Android); those platform bodies are host-specific.
///
/// Implementations MUST NOT error on probe failure — instead, fields the probe
/// could not resolve are returned as `Unknown` / `None` / `0` with the probe
/// taking a best effort.
#[async_trait]
pub trait ICapabilityProbe: Send + Sync {
    /// Runs the probe and returns the normalised [`HostProfile`].
    async fn probe(&self) -> HostProfile;
}

/// Default [`ICapabilityProbe`] wrapper. Because the platform introspection body
/// is a host-specific seam, [`CapabilityProbe`] wraps a host-supplied inner probe
/// (mirrors the C# `CapabilityProbe(ICapabilityProbe inner)` constructor). When
/// no probe is supplied, [`CapabilityProbe::unknown`] returns a synthetic
/// `Unknown` [`HostProfile`] so consumers never see an error (mirrors
/// `UnknownCapabilityProbe`).
pub struct CapabilityProbe {
    inner: Box<dyn ICapabilityProbe>,
}

impl CapabilityProbe {
    /// Construct with an explicit inner probe. This is the injection point host
    /// port packages (Windows / Linux / macOS / Android / HarmonyOS / iOS via a
    /// MAUI-equivalent) use to substitute their own probe implementation.
    pub fn new(inner: Box<dyn ICapabilityProbe>) -> Self {
        Self { inner }
    }

    /// The fail-open default — no in-process probe available, so every field
    /// falls back to `Unknown` / `0` / `None`. Mirrors `UnknownCapabilityProbe`.
    pub fn unknown() -> Self {
        Self {
            inner: Box::new(UnknownCapabilityProbe),
        }
    }
}

#[async_trait]
impl ICapabilityProbe for CapabilityProbe {
    async fn probe(&self) -> HostProfile {
        self.inner.probe().await
    }
}

/// Returned on platforms where no in-process probe is registered. All fields fall
/// back to `Unknown` / `0` / `None`. Hosts should register a real probe via
/// [`CapabilityProbe::new`].
pub struct UnknownCapabilityProbe;

#[async_trait]
impl ICapabilityProbe for UnknownCapabilityProbe {
    async fn probe(&self) -> HostProfile {
        HostProfile {
            os: OperatingSystemKind::Unknown,
            os_version: String::new(),
            arch: ArchitectureKind::Unknown,
            cpu_model: "Unknown CPU".to_string(),
            // No portable processor-count seam without std::thread — best effort.
            logical_core_count: 0,
            physical_core_count: 0,
            total_physical_memory_bytes: 0,
            gpu: None,
            npu: None,
            probed_at: Utc::now(),
        }
    }
}
