#ifndef CIRCLE_AI_RUNTIME_H
#define CIRCLE_AI_RUNTIME_H

/*
 * runtime.h — CircleAI.Runtime (C11 port of the deterministic runtime surface:
 * CircleAI.Runtime.Capabilities + CircleAI.Runtime.Backends +
 * CircleAI.Runtime.NativeRuntimes).
 *
 *   Enums   : OperatingSystemKind { Unknown=0, Windows, Linux, MacOS, Android,
 *                                   IOS, HarmonyOS };
 *             ArchitectureKind { Unknown=0, X86, X64, Arm, Arm64, Loong64 };
 *             GpuVendor { None=0, Nvidia, Amd, Intel, Apple, Qualcomm, Huawei,
 *                         Arm, Other=99 };
 *             NpuVendor { None=0, AppleNeuralEngine, QualcommHexagon,
 *                         HuaweiAscend, IntelVpu, CambriconMlu, Other=99 };
 *             BackendKind { Cpu=0, Cuda, Vulkan, OpenCL, Metal, Ascend,
 *                           Cambricon, CoreML };
 *             CapabilityTier { Tier0_Tiny=0 .. Tier4_Frontier=4 }.
 *   Records : GpuInfo(Vendor, Model, VramBytes, string? DriverVersion);
 *             NpuInfo(Vendor, Model);
 *             HostProfile(Os, OsVersion, Arch, CpuModel, LogicalCoreCount,
 *                         PhysicalCoreCount, TotalPhysicalMemoryBytes,
 *                         GpuInfo? Gpu, NpuInfo? Npu, DateTimeOffset ProbedAt)
 *               + HasUsableGpu(min) + Is64Bit;
 *             BackendSelection(Backend, ActualTier, Rationale);
 *             NativeRuntimeBundle(MnnVersion, Os, Arch, Backend, PrimaryUri,
 *                                 string? FallbackUri, string? ArchiveSha256Hex,
 *                                 MnnCoreLibraryName);
 *             NativeRuntimeInstall(Bundle, ExtractedRoot, MnnCorePath).
 *   Seams   : ICapabilityProbe (vtable) — probe() -> HostProfile; the concrete
 *               WMI/proc/sysctl probes are host-injected. A fixed probe (returns
 *               a caller-supplied profile) and an Unknown default are provided.
 *             INativeRuntimeFetcher (vtable) — ensure/is-cached/list; the concrete
 *               HTTP+SHA+extract fetcher is host-injected (no network here).
 *   Impls   : BackendSelector — deterministic table-style Select(profile, tier)
 *               that NEVER fails and always returns a runnable combination,
 *               clamping the tier to the host ceiling. Pure; no I/O.
 *             NativeRuntimeRegistry — in-memory bundle set with Find(os,arch,
 *               backend) (highest MnnVersion by ordinal wins) + All.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL. Nullable via has_*.
 * ProbedAt as int64 Unix ms UTC. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── enums ──────────────────────────────────────────────────────────────── */

typedef enum {
    CA_RT_OS_UNKNOWN   = 0,
    CA_RT_OS_WINDOWS   = 1,
    CA_RT_OS_LINUX     = 2,
    CA_RT_OS_MACOS     = 3,
    CA_RT_OS_ANDROID   = 4,
    CA_RT_OS_IOS       = 5,
    CA_RT_OS_HARMONYOS = 6
} ca_rt_os_kind_t;

typedef enum {
    CA_RT_ARCH_UNKNOWN = 0,
    CA_RT_ARCH_X86     = 1,
    CA_RT_ARCH_X64     = 2,
    CA_RT_ARCH_ARM     = 3,
    CA_RT_ARCH_ARM64   = 4,
    CA_RT_ARCH_LOONG64 = 5
} ca_rt_arch_kind_t;

typedef enum {
    CA_RT_GPU_NONE     = 0,
    CA_RT_GPU_NVIDIA   = 1,
    CA_RT_GPU_AMD      = 2,
    CA_RT_GPU_INTEL    = 3,
    CA_RT_GPU_APPLE    = 4,
    CA_RT_GPU_QUALCOMM = 5,
    CA_RT_GPU_HUAWEI   = 6,
    CA_RT_GPU_ARM      = 7,
    CA_RT_GPU_OTHER    = 99
} ca_rt_gpu_vendor_t;

typedef enum {
    CA_RT_NPU_NONE                = 0,
    CA_RT_NPU_APPLE_NEURAL_ENGINE = 1,
    CA_RT_NPU_QUALCOMM_HEXAGON    = 2,
    CA_RT_NPU_HUAWEI_ASCEND       = 3,
    CA_RT_NPU_INTEL_VPU           = 4,
    CA_RT_NPU_CAMBRICON_MLU       = 5,
    CA_RT_NPU_OTHER               = 99
} ca_rt_npu_vendor_t;

typedef enum {
    CA_RT_BACKEND_CPU       = 0,
    CA_RT_BACKEND_CUDA      = 1,
    CA_RT_BACKEND_VULKAN    = 2,
    CA_RT_BACKEND_OPENCL    = 3,
    CA_RT_BACKEND_METAL     = 4,
    CA_RT_BACKEND_ASCEND    = 5,
    CA_RT_BACKEND_CAMBRICON = 6,
    CA_RT_BACKEND_COREML    = 7
} ca_rt_backend_kind_t;

typedef enum {
    CA_RT_TIER0_TINY     = 0,
    CA_RT_TIER1_SMALL    = 1,
    CA_RT_TIER2_MEDIUM   = 2,
    CA_RT_TIER3_LARGE    = 3,
    CA_RT_TIER4_FRONTIER = 4
} ca_rt_capability_tier_t;

/* ── records ────────────────────────────────────────────────────────────── */

/* GpuInfo(Vendor, Model, VramBytes, string? DriverVersion). */
typedef struct {
    ca_rt_gpu_vendor_t vendor;
    char              *model;              /* owned, non-null */
    int64_t            vram_bytes;
    bool               has_driver_version; /* false == C# null DriverVersion */
    char              *driver_version;     /* owned, valid only when has_* */
} ca_rt_gpu_info_t;

/* NpuInfo(Vendor, Model). */
typedef struct {
    ca_rt_npu_vendor_t vendor;
    char              *model;              /* owned, non-null */
} ca_rt_npu_info_t;

/* HostProfile(...). Gpu/Npu nullable via has_gpu/has_npu. */
typedef struct {
    ca_rt_os_kind_t   os;
    char             *os_version;   /* owned, non-null */
    ca_rt_arch_kind_t arch;
    char             *cpu_model;    /* owned, non-null */
    int               logical_core_count;
    int               physical_core_count;
    int64_t           total_physical_memory_bytes;
    bool              has_gpu;
    ca_rt_gpu_info_t  gpu;          /* valid only when has_gpu */
    bool              has_npu;
    ca_rt_npu_info_t  npu;          /* valid only when has_npu */
    int64_t           probed_at_ms;
} ca_rt_host_profile_t;

void ca_rt_host_profile_free(ca_rt_host_profile_t *p);

/* HasUsableGpu(minimumVramBytes): Gpu present && VramBytes >= min.
 * C# default min = 2 GiB. */
bool ca_rt_host_profile_has_usable_gpu(const ca_rt_host_profile_t *p,
                                       int64_t minimum_vram_bytes);
/* Is64Bit: Arch in {X64, Arm64, Loong64}. */
bool ca_rt_host_profile_is_64bit(const ca_rt_host_profile_t *p);

/* BackendSelection(Backend, ActualTier, Rationale). */
typedef struct {
    ca_rt_backend_kind_t    backend;
    ca_rt_capability_tier_t actual_tier;
    char                   *rationale;    /* owned, non-null */
} ca_rt_backend_selection_t;

void ca_rt_backend_selection_free(ca_rt_backend_selection_t *s);

/* ── ICapabilityProbe (injected seam) ───────────────────────────────────── */

/* Fill *out with a fresh HostProfile; return 0 on success, -1 on failure.
 * Implementations MUST NOT fail on probe error — fields fall back to
 * Unknown/0/null. `out` is owned by the caller (free with
 * ca_rt_host_profile_free). */
typedef int (*ca_rt_probe_fn)(void *ctx, ca_rt_host_profile_t *out);

typedef struct {
    ca_rt_probe_fn probe;
    void          *ctx;
} ca_rt_capability_probe_t;

/* Run the probe. false on bad args or probe failure. */
bool ca_rt_capability_probe(const ca_rt_capability_probe_t *probe,
                            ca_rt_host_profile_t *out);

/* UnknownCapabilityProbe analogue — a probe that yields a HostProfile with
 * OS Unknown, the given arch/cores, no GPU/NPU. `arch`, `cores`, `probed_at_ms`
 * let the host inject the bits it does know without a real probe. Returns a
 * probe whose ctx points at a static template (safe to copy by value). */
ca_rt_capability_probe_t ca_rt_unknown_probe(ca_rt_arch_kind_t arch, int cores,
                                             int64_t probed_at_ms);

/* ── BackendSelector (deterministic; never fails) ───────────────────────── */

/* Select(profile, requestedTier) -> BackendSelection into *out (freshly owned;
 * free with ca_rt_backend_selection_free). Always returns a runnable combo,
 * clamping the tier to the host ceiling. 0 on success, -1 on bad args/OOM. */
int ca_rt_backend_select(const ca_rt_host_profile_t *profile,
                         ca_rt_capability_tier_t requested_tier,
                         ca_rt_backend_selection_t *out);

/* ── NativeRuntimeBundle / Install ──────────────────────────────────────── */

/* NativeRuntimeBundle(MnnVersion, Os, Arch, Backend, PrimaryUri,
 * string? FallbackUri, string? ArchiveSha256Hex, MnnCoreLibraryName). */
typedef struct {
    char                *mnn_version;      /* owned, non-null */
    ca_rt_os_kind_t      os;
    ca_rt_arch_kind_t    arch;
    ca_rt_backend_kind_t backend;
    char                *primary_uri;      /* owned, non-null */
    bool                 has_fallback_uri; /* false == C# null FallbackUri */
    char                *fallback_uri;     /* owned, valid only when has_* */
    bool                 has_sha256;       /* false == C# null ArchiveSha256Hex */
    char                *archive_sha256_hex; /* owned, valid only when has_* */
    char                *mnn_core_library_name; /* owned, non-null */
} ca_rt_native_bundle_t;

void ca_rt_native_bundle_free(ca_rt_native_bundle_t *b);
void ca_rt_native_bundle_free_array(ca_rt_native_bundle_t *arr, size_t count);

/* NativeRuntimeInstall(Bundle, ExtractedRoot, MnnCorePath). */
typedef struct {
    ca_rt_native_bundle_t bundle;         /* owned */
    char                 *extracted_root; /* owned, non-null */
    char                 *mnn_core_path;  /* owned, non-null */
} ca_rt_native_install_t;

void ca_rt_native_install_free(ca_rt_native_install_t *i);

/* ── NativeRuntimeRegistry ──────────────────────────────────────────────── */

typedef struct ca_rt_native_registry ca_rt_native_registry_t;

ca_rt_native_registry_t *ca_rt_native_registry_create(void); /* NULL on OOM */
void ca_rt_native_registry_destroy(ca_rt_native_registry_t *r);

/* Add a bundle (deep copy). 0 / -1 on bad args/OOM. */
int ca_rt_native_registry_add(ca_rt_native_registry_t *r,
                              const ca_rt_native_bundle_t *b);

/* All -> fresh owned array (*out_count) in insertion order. NULL + 0 when
 * empty; NULL + SIZE_MAX on error. */
ca_rt_native_bundle_t *ca_rt_native_registry_all(const ca_rt_native_registry_t *r,
                                                 size_t *out_count);

/* Find(os, arch, backend) -> fresh owned copy into *out, true; false on miss.
 * Highest MnnVersion (ordinal) wins among ties. */
bool ca_rt_native_registry_find(const ca_rt_native_registry_t *r,
                                ca_rt_os_kind_t os, ca_rt_arch_kind_t arch,
                                ca_rt_backend_kind_t backend,
                                ca_rt_native_bundle_t *out);

/* ── INativeRuntimeFetcher (injected seam) ──────────────────────────────── */

/* EnsureRuntimeAsync(os, arch, backend) -> fill *out (owned; free with
 * ca_rt_native_install_free). 0 on success, -1 on failure (no bundle / SHA
 * mismatch / download error). */
typedef int (*ca_rt_fetch_ensure_fn)(void *ctx, ca_rt_os_kind_t os,
                                     ca_rt_arch_kind_t arch,
                                     ca_rt_backend_kind_t backend,
                                     ca_rt_native_install_t *out);
/* IsRuntimeCachedAsync -> true/false. */
typedef bool (*ca_rt_fetch_is_cached_fn)(void *ctx, ca_rt_os_kind_t os,
                                        ca_rt_arch_kind_t arch,
                                        ca_rt_backend_kind_t backend);
/* ListAvailableBundles -> fresh owned array (*out_count); NULL+SIZE_MAX on err. */
typedef ca_rt_native_bundle_t *(*ca_rt_fetch_list_fn)(void *ctx,
                                                     size_t *out_count);

typedef struct {
    ca_rt_fetch_ensure_fn    ensure;
    ca_rt_fetch_is_cached_fn is_cached;
    ca_rt_fetch_list_fn      list;
    void                    *ctx;
} ca_rt_native_fetcher_t;

/* Thin dispatchers over the fetcher vtable. */
int  ca_rt_native_fetcher_ensure(const ca_rt_native_fetcher_t *f,
                                 ca_rt_os_kind_t os, ca_rt_arch_kind_t arch,
                                 ca_rt_backend_kind_t backend,
                                 ca_rt_native_install_t *out);
bool ca_rt_native_fetcher_is_cached(const ca_rt_native_fetcher_t *f,
                                    ca_rt_os_kind_t os, ca_rt_arch_kind_t arch,
                                    ca_rt_backend_kind_t backend);
ca_rt_native_bundle_t *ca_rt_native_fetcher_list(const ca_rt_native_fetcher_t *f,
                                                 size_t *out_count);

/* A fetcher backed by a NativeRuntimeRegistry: is_cached is always false (no
 * disk here) and ensure fails (no download here), but list returns the
 * registry's bundles. Lets the registry stand in for ListAvailableBundles
 * without a real download stack. The registry must outlive the fetcher. */
ca_rt_native_fetcher_t ca_rt_registry_fetcher(const ca_rt_native_registry_t *r);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_RUNTIME_H */
