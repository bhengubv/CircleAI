/*
 * runtime.c — CircleAI.Runtime (C11 port).
 *
 * BackendSelector: deterministic table-style selection mirroring
 * BackendSelector.cs branch-for-branch, including the rationale strings.
 * NativeRuntimeRegistry: linear array of bundles, Find picks the highest
 * MnnVersion (ordinal) for a tuple. Probe/Fetcher are injected vtables.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/runtime.h"
#include "board_common.h"

#include <stdio.h>

#define GIB (1024LL * 1024 * 1024)

/* ── record free / copy ─────────────────────────────────────────────────── */

static void gpu_free_fields(ca_rt_gpu_info_t *g) {
    if (!g) return;
    free(g->model);
    free(g->driver_version);
    g->model = g->driver_version = NULL;
    g->has_driver_version = false;
}

static void npu_free_fields(ca_rt_npu_info_t *n) {
    if (!n) return;
    free(n->model);
    n->model = NULL;
}

void ca_rt_host_profile_free(ca_rt_host_profile_t *p) {
    if (!p) return;
    free(p->os_version);
    free(p->cpu_model);
    if (p->has_gpu) gpu_free_fields(&p->gpu);
    if (p->has_npu) npu_free_fields(&p->npu);
    p->os_version = p->cpu_model = NULL;
    p->has_gpu = p->has_npu = false;
}

bool ca_rt_host_profile_has_usable_gpu(const ca_rt_host_profile_t *p,
                                       int64_t minimum_vram_bytes) {
    if (!p || !p->has_gpu) return false;
    return p->gpu.vram_bytes >= minimum_vram_bytes;
}

bool ca_rt_host_profile_is_64bit(const ca_rt_host_profile_t *p) {
    if (!p) return false;
    return p->arch == CA_RT_ARCH_X64 || p->arch == CA_RT_ARCH_ARM64 ||
           p->arch == CA_RT_ARCH_LOONG64;
}

void ca_rt_backend_selection_free(ca_rt_backend_selection_t *s) {
    if (!s) return;
    free(s->rationale);
    s->rationale = NULL;
}

/* ── ICapabilityProbe ───────────────────────────────────────────────────── */

bool ca_rt_capability_probe(const ca_rt_capability_probe_t *probe,
                            ca_rt_host_profile_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!probe || !probe->probe || !out) return false;
    return probe->probe(probe->ctx, out) == 0;
}

/* Static template consumed by the unknown probe. arch/cores/probed_at are
 * carried in a small heap struct pointed to by ctx. To keep the returned
 * probe copyable by value with no lifetime worries, we stash the parameters in
 * a process-wide table indexed by a synthesised token. Simpler: encode into a
 * single allocated struct and leak-free via a fixed template — but the C# probe
 * is stateless-per-call. We use a small static struct + the fact that the
 * fields are value types (packed into ctx as a pointer to a heap struct that
 * the probe owns for the process lifetime). */
typedef struct {
    ca_rt_arch_kind_t arch;
    int               cores;
    int64_t           probed_at_ms;
} unknown_probe_state_t;

static int unknown_probe_run(void *ctx, ca_rt_host_profile_t *out) {
    const unknown_probe_state_t *s = (const unknown_probe_state_t *)ctx;
    memset(out, 0, sizeof(*out));
    out->os                          = CA_RT_OS_UNKNOWN;
    out->arch                        = s->arch;
    out->logical_core_count          = s->cores;
    out->physical_core_count         = s->cores;
    out->total_physical_memory_bytes = 0;
    out->probed_at_ms                = s->probed_at_ms;
    out->os_version = cab_strdup_empty("0.0.0.0");
    out->cpu_model  = cab_strdup_empty("Unknown CPU");
    if (!out->os_version || !out->cpu_model) { ca_rt_host_profile_free(out); return -1; }
    return 0;
}

ca_rt_capability_probe_t ca_rt_unknown_probe(ca_rt_arch_kind_t arch, int cores,
                                             int64_t probed_at_ms) {
    /* One heap struct per call; intentionally retained for the process lifetime
     * (the probe is meant to be a long-lived singleton, mirroring DI). Callers
     * that churn probes should reuse one. On OOM the probe degrades to a NULL
     * fn, which ca_rt_capability_probe treats as failure. */
    unknown_probe_state_t *s = (unknown_probe_state_t *)malloc(sizeof(*s));
    ca_rt_capability_probe_t p;
    p.ctx = s;
    if (s) {
        s->arch = arch;
        s->cores = cores;
        s->probed_at_ms = probed_at_ms;
        p.probe = unknown_probe_run;
    } else {
        p.probe = NULL;
    }
    return p;
}

/* ── BackendSelector ────────────────────────────────────────────────────── */

static ca_rt_capability_tier_t clamp_tier(ca_rt_capability_tier_t requested,
                                          ca_rt_capability_tier_t ceiling) {
    return requested <= ceiling ? requested : ceiling;
}

static ca_rt_capability_tier_t tier_for_vram(int64_t vram) {
    if (vram >= 24LL * GIB) return CA_RT_TIER4_FRONTIER;
    if (vram >= 12LL * GIB) return CA_RT_TIER3_LARGE;
    if (vram >= 8LL  * GIB) return CA_RT_TIER2_MEDIUM;
    if (vram >= 4LL  * GIB) return CA_RT_TIER1_SMALL;
    return CA_RT_TIER0_TINY;
}
static ca_rt_capability_tier_t tier_for_unified(int64_t ram) {
    if (ram >= 64LL * GIB) return CA_RT_TIER4_FRONTIER;
    if (ram >= 32LL * GIB) return CA_RT_TIER3_LARGE;
    if (ram >= 16LL * GIB) return CA_RT_TIER2_MEDIUM;
    if (ram >= 8LL  * GIB) return CA_RT_TIER1_SMALL;
    return CA_RT_TIER0_TINY;
}
static ca_rt_capability_tier_t tier_for_cpu_ram(int64_t ram) {
    if (ram >= 64LL * GIB) return CA_RT_TIER3_LARGE;
    if (ram >= 32LL * GIB) return CA_RT_TIER2_MEDIUM;
    if (ram >= 16LL * GIB) return CA_RT_TIER1_SMALL;
    if (ram >= 8LL  * GIB) return CA_RT_TIER1_SMALL;
    return CA_RT_TIER0_TINY;
}

/* C# ToString() on the enums used inside rationale strings. */
static const char *tier_name(ca_rt_capability_tier_t t) {
    switch (t) {
        case CA_RT_TIER0_TINY:     return "Tier0_Tiny";
        case CA_RT_TIER1_SMALL:    return "Tier1_Small";
        case CA_RT_TIER2_MEDIUM:   return "Tier2_Medium";
        case CA_RT_TIER3_LARGE:    return "Tier3_Large";
        case CA_RT_TIER4_FRONTIER: return "Tier4_Frontier";
    }
    return "Tier0_Tiny";
}
static const char *gpu_vendor_name(ca_rt_gpu_vendor_t v) {
    switch (v) {
        case CA_RT_GPU_NONE:     return "None";
        case CA_RT_GPU_NVIDIA:   return "Nvidia";
        case CA_RT_GPU_AMD:      return "Amd";
        case CA_RT_GPU_INTEL:    return "Intel";
        case CA_RT_GPU_APPLE:    return "Apple";
        case CA_RT_GPU_QUALCOMM: return "Qualcomm";
        case CA_RT_GPU_HUAWEI:   return "Huawei";
        case CA_RT_GPU_ARM:      return "Arm";
        case CA_RT_GPU_OTHER:    return "Other";
    }
    return "Other";
}

/* Set *out from a printf-formatted rationale. Returns -1 on OOM. */
static int set_selection(ca_rt_backend_selection_t *out,
                         ca_rt_backend_kind_t backend,
                         ca_rt_capability_tier_t tier,
                         const char *rationale) {
    out->backend     = backend;
    out->actual_tier = tier;
    out->rationale   = cab_strdup(rationale);
    return out->rationale ? 0 : -1;
}

int ca_rt_backend_select(const ca_rt_host_profile_t *profile,
                         ca_rt_capability_tier_t requested_tier,
                         ca_rt_backend_selection_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!profile || !out) return -1;

    char buf[512];
    ca_rt_capability_tier_t tier;

    /* 1. Apple Silicon — Metal + ANE via unified memory. */
    if (profile->os == CA_RT_OS_MACOS && profile->arch == CA_RT_ARCH_ARM64 &&
        profile->has_gpu && profile->gpu.vendor == CA_RT_GPU_APPLE) {
        tier = clamp_tier(requested_tier,
                          tier_for_unified(profile->total_physical_memory_bytes));
        snprintf(buf, sizeof(buf),
            "Apple Silicon (%s); Metal over unified-memory GPU; tier capped to %s by %lld GiB unified RAM.",
            profile->cpu_model, tier_name(tier),
            (long long)(profile->total_physical_memory_bytes / GIB));
        return set_selection(out, CA_RT_BACKEND_METAL, tier, buf);
    }

    /* 2. NVIDIA + CUDA. */
    if (profile->has_gpu && profile->gpu.vendor == CA_RT_GPU_NVIDIA &&
        profile->gpu.vram_bytes >= 4 * GIB) {
        tier = clamp_tier(requested_tier, tier_for_vram(profile->gpu.vram_bytes));
        snprintf(buf, sizeof(buf),
            "NVIDIA %s with %lld GiB VRAM; CUDA backend; tier capped to %s by VRAM.",
            profile->gpu.model, (long long)(profile->gpu.vram_bytes / GIB),
            tier_name(tier));
        return set_selection(out, CA_RT_BACKEND_CUDA, tier, buf);
    }

    /* 3. Huawei Ascend NPU. */
    if (profile->has_npu && profile->npu.vendor == CA_RT_NPU_HUAWEI_ASCEND) {
        tier = clamp_tier(requested_tier, CA_RT_TIER3_LARGE);
        snprintf(buf, sizeof(buf),
            "Huawei Ascend NPU detected (%s); Ascend (CANN) backend; tier capped to %s.",
            profile->npu.model, tier_name(tier));
        return set_selection(out, CA_RT_BACKEND_ASCEND, tier, buf);
    }

    /* 4. Cambricon MLU. */
    if (profile->has_npu && profile->npu.vendor == CA_RT_NPU_CAMBRICON_MLU) {
        tier = clamp_tier(requested_tier, CA_RT_TIER3_LARGE);
        snprintf(buf, sizeof(buf),
            "Cambricon MLU detected; Cambricon backend; tier capped to %s.",
            tier_name(tier));
        return set_selection(out, CA_RT_BACKEND_CAMBRICON, tier, buf);
    }

    /* 5. AMD / Intel discrete GPU — Vulkan. */
    if (profile->has_gpu &&
        (profile->gpu.vendor == CA_RT_GPU_AMD || profile->gpu.vendor == CA_RT_GPU_INTEL) &&
        profile->gpu.vram_bytes >= 4 * GIB) {
        tier = clamp_tier(requested_tier, tier_for_vram(profile->gpu.vram_bytes));
        snprintf(buf, sizeof(buf),
            "%s %s with %lld GiB VRAM; Vulkan backend; tier capped to %s by VRAM.",
            gpu_vendor_name(profile->gpu.vendor), profile->gpu.model,
            (long long)(profile->gpu.vram_bytes / GIB), tier_name(tier));
        return set_selection(out, CA_RT_BACKEND_VULKAN, tier, buf);
    }

    /* 6. Qualcomm Hexagon NPU / Adreno — OpenCL. */
    if ((profile->has_npu && profile->npu.vendor == CA_RT_NPU_QUALCOMM_HEXAGON) ||
        (profile->has_gpu && profile->gpu.vendor == CA_RT_GPU_QUALCOMM)) {
        tier = clamp_tier(requested_tier, CA_RT_TIER1_SMALL);
        snprintf(buf, sizeof(buf),
            "Qualcomm Snapdragon platform; OpenCL backend (Adreno/Hexagon shared compute); tier capped to %s.",
            tier_name(tier));
        return set_selection(out, CA_RT_BACKEND_OPENCL, tier, buf);
    }

    /* 7. ARM Mali via Vulkan (Arm or Huawei GPU). */
    if (profile->has_gpu &&
        (profile->gpu.vendor == CA_RT_GPU_ARM || profile->gpu.vendor == CA_RT_GPU_HUAWEI)) {
        tier = clamp_tier(requested_tier, CA_RT_TIER1_SMALL);
        snprintf(buf, sizeof(buf),
            "ARM/Mali class GPU (%s); Vulkan backend; tier capped to %s.",
            profile->gpu.model, tier_name(tier));
        return set_selection(out, CA_RT_BACKEND_VULKAN, tier, buf);
    }

    /* 8. CPU fallback — always selectable. */
    tier = clamp_tier(requested_tier,
                      tier_for_cpu_ram(profile->total_physical_memory_bytes));
    snprintf(buf, sizeof(buf),
        "No usable accelerator detected; CPU SIMD backend on %s (%d logical cores, %lld GiB RAM); tier capped to %s by available RAM.",
        profile->cpu_model, profile->logical_core_count,
        (long long)(profile->total_physical_memory_bytes / GIB), tier_name(tier));
    return set_selection(out, CA_RT_BACKEND_CPU, tier, buf);
}

/* ── NativeRuntimeBundle / Install ──────────────────────────────────────── */

void ca_rt_native_bundle_free(ca_rt_native_bundle_t *b) {
    if (!b) return;
    free(b->mnn_version);
    free(b->primary_uri);
    free(b->fallback_uri);
    free(b->archive_sha256_hex);
    free(b->mnn_core_library_name);
    b->mnn_version = b->primary_uri = b->fallback_uri = NULL;
    b->archive_sha256_hex = b->mnn_core_library_name = NULL;
    b->has_fallback_uri = b->has_sha256 = false;
}
void ca_rt_native_bundle_free_array(ca_rt_native_bundle_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_rt_native_bundle_free(&arr[i]);
    free(arr);
}

static bool bundle_copy(ca_rt_native_bundle_t *dst,
                        const ca_rt_native_bundle_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->os      = src->os;
    dst->arch    = src->arch;
    dst->backend = src->backend;
    dst->mnn_version           = cab_strdup_empty(src->mnn_version);
    dst->primary_uri           = cab_strdup_empty(src->primary_uri);
    dst->mnn_core_library_name = cab_strdup_empty(src->mnn_core_library_name);
    bool ok = dst->mnn_version && dst->primary_uri && dst->mnn_core_library_name;
    if (ok && src->has_fallback_uri) {
        dst->fallback_uri = cab_strdup_empty(src->fallback_uri);
        ok = dst->fallback_uri != NULL;
        dst->has_fallback_uri = ok;
    }
    if (ok && src->has_sha256) {
        dst->archive_sha256_hex = cab_strdup_empty(src->archive_sha256_hex);
        ok = dst->archive_sha256_hex != NULL;
        dst->has_sha256 = ok;
    }
    if (!ok) { ca_rt_native_bundle_free(dst); return false; }
    return true;
}

void ca_rt_native_install_free(ca_rt_native_install_t *i) {
    if (!i) return;
    ca_rt_native_bundle_free(&i->bundle);
    free(i->extracted_root);
    free(i->mnn_core_path);
    i->extracted_root = i->mnn_core_path = NULL;
}

/* ── NativeRuntimeRegistry ──────────────────────────────────────────────── */

struct ca_rt_native_registry {
    ca_rt_native_bundle_t *bundles;
    size_t                 count, cap;
};

ca_rt_native_registry_t *ca_rt_native_registry_create(void) {
    return (ca_rt_native_registry_t *)calloc(1, sizeof(ca_rt_native_registry_t));
}
void ca_rt_native_registry_destroy(ca_rt_native_registry_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->count; ++i) ca_rt_native_bundle_free(&r->bundles[i]);
    free(r->bundles);
    free(r);
}

int ca_rt_native_registry_add(ca_rt_native_registry_t *r,
                              const ca_rt_native_bundle_t *b) {
    if (!r || !b) return -1;
    ca_rt_native_bundle_t copy;
    if (!bundle_copy(&copy, b)) return -1;
    if (r->count == r->cap) {
        size_t nc = r->cap ? r->cap * 2 : 4;
        void *n = realloc(r->bundles, nc * sizeof(*r->bundles));
        if (!n) { ca_rt_native_bundle_free(&copy); return -1; }
        r->bundles = (ca_rt_native_bundle_t *)n;
        r->cap = nc;
    }
    r->bundles[r->count++] = copy;
    return 0;
}

ca_rt_native_bundle_t *ca_rt_native_registry_all(const ca_rt_native_registry_t *r,
                                                 size_t *out_count) {
    if (!out_count) return NULL;
    if (!r) { *out_count = (size_t)-1; return NULL; }
    if (r->count == 0) { *out_count = 0; return NULL; }
    ca_rt_native_bundle_t *out =
        (ca_rt_native_bundle_t *)calloc(r->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < r->count; ++i) {
        if (!bundle_copy(&out[i], &r->bundles[i])) {
            ca_rt_native_bundle_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = r->count;
    return out;
}

bool ca_rt_native_registry_find(const ca_rt_native_registry_t *r,
                                ca_rt_os_kind_t os, ca_rt_arch_kind_t arch,
                                ca_rt_backend_kind_t backend,
                                ca_rt_native_bundle_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!r || !out) return false;
    const ca_rt_native_bundle_t *best = NULL;
    for (size_t i = 0; i < r->count; ++i) {
        const ca_rt_native_bundle_t *b = &r->bundles[i];
        if (b->os != os || b->arch != arch || b->backend != backend) continue;
        if (!best || strcmp(b->mnn_version, best->mnn_version) > 0) best = b;
    }
    if (!best) return false;
    return bundle_copy(out, best);
}

/* ── INativeRuntimeFetcher dispatchers ──────────────────────────────────── */

int ca_rt_native_fetcher_ensure(const ca_rt_native_fetcher_t *f,
                                ca_rt_os_kind_t os, ca_rt_arch_kind_t arch,
                                ca_rt_backend_kind_t backend,
                                ca_rt_native_install_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!f || !f->ensure || !out) return -1;
    return f->ensure(f->ctx, os, arch, backend, out);
}
bool ca_rt_native_fetcher_is_cached(const ca_rt_native_fetcher_t *f,
                                    ca_rt_os_kind_t os, ca_rt_arch_kind_t arch,
                                    ca_rt_backend_kind_t backend) {
    if (!f || !f->is_cached) return false;
    return f->is_cached(f->ctx, os, arch, backend);
}
ca_rt_native_bundle_t *ca_rt_native_fetcher_list(const ca_rt_native_fetcher_t *f,
                                                 size_t *out_count) {
    if (!out_count) return NULL;
    if (!f || !f->list) { *out_count = (size_t)-1; return NULL; }
    return f->list(f->ctx, out_count);
}

/* Registry-backed fetcher. */
static int registry_fetch_ensure(void *ctx, ca_rt_os_kind_t os,
                                 ca_rt_arch_kind_t arch,
                                 ca_rt_backend_kind_t backend,
                                 ca_rt_native_install_t *out) {
    (void)ctx; (void)os; (void)arch; (void)backend; (void)out;
    /* No download stack in-process — the real fetcher is injected. */
    return -1;
}
static bool registry_fetch_is_cached(void *ctx, ca_rt_os_kind_t os,
                                     ca_rt_arch_kind_t arch,
                                     ca_rt_backend_kind_t backend) {
    (void)ctx; (void)os; (void)arch; (void)backend;
    return false;
}
static ca_rt_native_bundle_t *registry_fetch_list(void *ctx, size_t *out_count) {
    return ca_rt_native_registry_all((const ca_rt_native_registry_t *)ctx, out_count);
}

ca_rt_native_fetcher_t ca_rt_registry_fetcher(const ca_rt_native_registry_t *r) {
    ca_rt_native_fetcher_t f;
    f.ensure    = registry_fetch_ensure;
    f.is_cached = registry_fetch_is_cached;
    f.list      = registry_fetch_list;
    f.ctx       = (void *)r;
    return f;
}
