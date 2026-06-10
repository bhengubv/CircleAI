//! device.rs
//!
//! DeviceProbe, DeviceTier classification, DefaultDeviceContext.

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum DeviceTier {
    Phone = 0,
    Wearable = 1,
    Tablet = 2,
    Laptop = 3,
    Workstation = 4,
    Embedded = 5,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum GpuKind {
    None = 0,
    Integrated = 1,
    Discrete = 2,
    NeuralEngine = 3,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum ThermalClass {
    Normal = 0,
    Fair = 1,
    Serious = 2,
    Critical = 3,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum Connectivity {
    Offline = 0,
    Cellular = 1,
    WiFi = 2,
    Ethernet = 3,
}

/// Snapshot of device capabilities for selector and runtime decisions.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DeviceSnapshot {
    pub tier: DeviceTier,
    pub ram_bytes: u64,
    pub free_storage_bytes: u64,
    pub cpu_cores: u32,
    pub gpu_kind: GpuKind,
    pub thermal: ThermalClass,
    pub connectivity: Connectivity,
    pub os: String,
    pub arch: String,
}

/// Recommended context-window, concurrency, and agentic depth for a tier.
#[derive(Debug, Clone, Copy)]
pub struct DeviceTierDefaults {
    pub context_window: u32,
    pub max_concurrent: u32,
    pub max_agentic_iterations: u32,
}

impl DeviceTierDefaults {
    pub fn for_tier(tier: DeviceTier) -> Self {
        match tier {
            DeviceTier::Wearable => Self { context_window: 1024, max_concurrent: 1, max_agentic_iterations: 2 },
            DeviceTier::Phone => Self { context_window: 4096, max_concurrent: 2, max_agentic_iterations: 4 },
            DeviceTier::Embedded => Self { context_window: 2048, max_concurrent: 1, max_agentic_iterations: 2 },
            DeviceTier::Tablet => Self { context_window: 8192, max_concurrent: 4, max_agentic_iterations: 8 },
            DeviceTier::Laptop => Self { context_window: 16384, max_concurrent: 6, max_agentic_iterations: 16 },
            DeviceTier::Workstation => Self { context_window: 32768, max_concurrent: 12, max_agentic_iterations: 32 },
        }
    }
}

/// Source of runtime device state for selectors and observers.
pub trait IDeviceContext: Send + Sync {
    fn snapshot(&self) -> DeviceSnapshot;
}

/// No-op context. Returns a deterministic stub useful for tests.
pub struct NullDeviceContext;

impl IDeviceContext for NullDeviceContext {
    fn snapshot(&self) -> DeviceSnapshot {
        DeviceSnapshot {
            tier: DeviceTier::Phone,
            ram_bytes: 4 * 1024 * 1024 * 1024,
            free_storage_bytes: 8 * 1024 * 1024 * 1024,
            cpu_cores: 4,
            gpu_kind: GpuKind::None,
            thermal: ThermalClass::Normal,
            connectivity: Connectivity::WiFi,
            os: "unknown".into(),
            arch: "unknown".into(),
        }
    }
}

/// Real-device probe. Inspects RAM, CPU count, OS, arch, free disk.
pub struct DefaultDeviceContext;

impl IDeviceContext for DefaultDeviceContext {
    fn snapshot(&self) -> DeviceSnapshot {
        let probe = DeviceProbe::probe();
        DeviceSnapshot {
            tier: probe.tier,
            ram_bytes: probe.ram_bytes,
            free_storage_bytes: probe.free_storage_bytes,
            cpu_cores: probe.cpu_cores,
            gpu_kind: GpuKind::None,
            thermal: ThermalClass::Normal,
            connectivity: Connectivity::WiFi,
            os: probe.os,
            arch: probe.arch,
        }
    }
}

/// Lightweight probe used by selector heuristics and observers.
#[derive(Debug, Clone)]
pub struct DeviceProbe {
    pub tier: DeviceTier,
    pub ram_bytes: u64,
    pub free_storage_bytes: u64,
    pub cpu_cores: u32,
    pub os: String,
    pub arch: String,
}

impl DeviceProbe {
    /// Probe the current device. Best-effort — falls back to defaults
    /// when a sysconf isn't available.
    pub fn probe() -> Self {
        let ram = probe_ram_bytes();
        let cpu = num_cpus_logical();
        let os = std::env::consts::OS.to_string();
        let arch = std::env::consts::ARCH.to_string();
        let tier = classify_tier(ram, cpu, &os);
        let free_storage = probe_free_storage_bytes();
        Self { tier, ram_bytes: ram, free_storage_bytes: free_storage, cpu_cores: cpu, os, arch }
    }
}

fn classify_tier(ram_bytes: u64, cpu_cores: u32, _os: &str) -> DeviceTier {
    let gib = ram_bytes / (1024 * 1024 * 1024);
    if gib >= 32 && cpu_cores >= 16 {
        DeviceTier::Workstation
    } else if gib >= 16 && cpu_cores >= 8 {
        DeviceTier::Laptop
    } else if gib >= 6 && cpu_cores >= 4 {
        DeviceTier::Tablet
    } else if gib >= 3 {
        DeviceTier::Phone
    } else if gib >= 1 {
        DeviceTier::Embedded
    } else {
        DeviceTier::Wearable
    }
}

#[cfg(unix)]
fn probe_ram_bytes() -> u64 {
    // sysconf(_SC_PHYS_PAGES) * sysconf(_SC_PAGESIZE)
    unsafe {
        let pages = libc_sysconf(SC_PHYS_PAGES);
        let page_size = libc_sysconf(SC_PAGESIZE);
        if pages > 0 && page_size > 0 {
            (pages as u64) * (page_size as u64)
        } else {
            0
        }
    }
}

#[cfg(not(unix))]
fn probe_ram_bytes() -> u64 {
    // Best-effort fallback. Windows could use GlobalMemoryStatusEx via WinAPI,
    // but we don't want to pull a crate. Return 0 → selector falls back to
    // smallest tier.
    0
}

#[cfg(unix)]
fn probe_free_storage_bytes() -> u64 {
    use std::ffi::CString;
    use std::mem::MaybeUninit;
    let path = CString::new("/").unwrap();
    unsafe {
        let mut buf: MaybeUninit<libc_statvfs> = MaybeUninit::zeroed();
        if libc_statvfs_call(path.as_ptr(), buf.as_mut_ptr()) == 0 {
            let s = buf.assume_init();
            (s.f_bavail as u64) * (s.f_frsize as u64)
        } else {
            0
        }
    }
}

#[cfg(not(unix))]
fn probe_free_storage_bytes() -> u64 {
    0
}

fn num_cpus_logical() -> u32 {
    std::thread::available_parallelism()
        .map(|n| n.get() as u32)
        .unwrap_or(1)
}

// ────────────────────────────────────────────────────────────────────────────
// Unix sysconf/statvfs bindings (manual — avoids pulling the `libc` crate).
// Constants pulled from Linux + Darwin headers; values match across both.
// ────────────────────────────────────────────────────────────────────────────

#[cfg(unix)]
const SC_PHYS_PAGES: i32 = {
    #[cfg(target_os = "linux")]
    { 85 }
    #[cfg(target_os = "macos")]
    { 200 }
    #[cfg(not(any(target_os = "linux", target_os = "macos")))]
    { 200 }
};

#[cfg(unix)]
const SC_PAGESIZE: i32 = {
    #[cfg(target_os = "linux")]
    { 30 }
    #[cfg(target_os = "macos")]
    { 29 }
    #[cfg(not(any(target_os = "linux", target_os = "macos")))]
    { 29 }
};

#[cfg(unix)]
#[allow(non_camel_case_types)]
#[repr(C)]
struct libc_statvfs {
    f_bsize: u64,
    f_frsize: u64,
    f_blocks: u64,
    f_bfree: u64,
    f_bavail: u64,
    f_files: u64,
    f_ffree: u64,
    f_favail: u64,
    f_fsid: u64,
    f_flag: u64,
    f_namemax: u64,
    f_reserved: [u32; 8],
}

#[cfg(unix)]
extern "C" {
    fn sysconf(name: i32) -> i64;
    fn statvfs(path: *const i8, buf: *mut libc_statvfs) -> i32;
}

#[cfg(unix)]
unsafe fn libc_sysconf(name: i32) -> i64 {
    sysconf(name)
}

#[cfg(unix)]
unsafe fn libc_statvfs_call(path: *const i8, buf: *mut libc_statvfs) -> i32 {
    statvfs(path, buf)
}
