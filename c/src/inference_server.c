/*
 * inference_server.c — CircleAI.Inference.Server contracts + in-memory
 * handlers (C11 port). See inference_server.h.
 *
 * In-memory only: HTTP/DI/native seams are injected behind vtables. Pure C11 +
 * libc. No sockets, no threads.
 */

#include "circle_ai/inference_server.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <time.h>
#include <math.h>

#if defined(_WIN32)
  #define strncasecmp _strnicmp
  #define strcasecmp  _stricmp
#else
  #include <strings.h>
#endif

/* ─────────────────────── helpers ─────────────────────── */

static char *xstrdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static bool is_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; p++)
        if (!isspace(*p)) return false;
    return true;
}

static int64_t now_unix_ms(void) { return (int64_t)time(NULL) * 1000; }
static int64_t now_unix_s(void)  { return (int64_t)time(NULL); }

/* 1 token ~= 4 chars, min 1 when non-empty. */
static int approx_tokens(const char *s) {
    if (!s || !*s) return 0;
    int n = (int)(strlen(s) / 4);
    return n > 1 ? n : 1;
}

/* A pseudo-guid "N"-format string (32 hex). Deterministic-ish via rand/time is
 * not required — uniqueness within a process is enough for id fields. */
static char *make_id(const char *prefix) {
    static uint64_t counter = 0;
    counter++;
    uint64_t a = (uint64_t)time(NULL);
    uint64_t b = counter * 0x9E3779B97F4A7C15ULL + a;
    char hex[33];
    snprintf(hex, sizeof(hex), "%08x%08x%08x%08x",
             (unsigned)(a & 0xffffffff), (unsigned)(b & 0xffffffff),
             (unsigned)((b >> 16) & 0xffffffff), (unsigned)((a >> 8) & 0xffffffff));
    size_t n = (prefix ? strlen(prefix) : 0) + 33;
    char *out = (char *)malloc(n);
    if (out) snprintf(out, n, "%s%s", prefix ? prefix : "", hex);
    return out;
}

/* ===========================================================================
 * Backend / tier parse
 * =========================================================================== */

bool ca_backend_kind_parse(const char *s, ca_backend_kind_t *out) {
    if (!s || !out) return false;
    struct { const char *n; ca_backend_kind_t v; } table[] = {
        {"cpu", CA_BACKEND_CPU}, {"cuda", CA_BACKEND_CUDA}, {"vulkan", CA_BACKEND_VULKAN},
        {"opencl", CA_BACKEND_OPENCL}, {"metal", CA_BACKEND_METAL}, {"ascend", CA_BACKEND_ASCEND},
        {"cambricon", CA_BACKEND_CAMBRICON}, {"coreml", CA_BACKEND_COREML},
    };
    for (size_t i = 0; i < sizeof(table) / sizeof(table[0]); i++)
        if (strcasecmp(s, table[i].n) == 0) { *out = table[i].v; return true; }
    return false;
}

bool ca_capability_tier_parse(const char *s, ca_capability_tier_t *out) {
    if (!s || !out) return false;
    struct { const char *n; ca_capability_tier_t v; } table[] = {
        {"Tier0_Tiny", CA_TIER0_TINY}, {"Tier1_Small", CA_TIER1_SMALL},
        {"Tier2_Medium", CA_TIER2_MEDIUM}, {"Tier3_Large", CA_TIER3_LARGE},
        {"Tier4_Frontier", CA_TIER4_FRONTIER},
    };
    for (size_t i = 0; i < sizeof(table) / sizeof(table[0]); i++)
        if (strcasecmp(s, table[i].n) == 0) { *out = table[i].v; return true; }
    return false;
}

/* ===========================================================================
 * InferenceRequest / InferenceResponse
 * =========================================================================== */

void ca_inference_request_free(ca_inference_request_t *r) {
    if (!r) return;
    free(r->model_id);
    free(r->prompt);
    if (r->stop_sequences) {
        for (size_t i = 0; i < r->stop_count; i++) free(r->stop_sequences[i]);
        free(r->stop_sequences);
    }
    memset(r, 0, sizeof(*r));
}

void ca_inference_response_free(ca_inference_response_t *r) {
    if (!r) return;
    free(r->output_text);
    free(r->failure_message);
    free(r->reasoning_text);
    memset(r, 0, sizeof(*r));
}

/* ===========================================================================
 * IInferenceBridge
 * =========================================================================== */

struct ca_inference_bridge {
    ca_inference_bridge_vtable_t vt;
};

ca_inference_bridge_t *ca_inference_bridge_create(ca_inference_bridge_vtable_t vt) {
    if (!vt.complete) return NULL;
    ca_inference_bridge_t *b = (ca_inference_bridge_t *)calloc(1, sizeof(*b));
    if (!b) return NULL;
    b->vt = vt;
    return b;
}

void ca_inference_bridge_destroy(ca_inference_bridge_t *b) {
    if (!b) return;
    if (b->vt.destroy) b->vt.destroy(b->vt.state);
    free(b);
}

bool ca_inference_bridge_complete(ca_inference_bridge_t *b,
                                  const ca_inference_request_t *req,
                                  ca_inference_response_t *out) {
    if (!b || !req || !out) return false;
    return b->vt.complete(b->vt.state, req, out);
}

/* Echo bridge. */
static bool echo_bridge_complete(void *state, const ca_inference_request_t *req,
                                 ca_inference_response_t *out) {
    (void)state;
    memset(out, 0, sizeof(*out));
    const char *prompt = req->prompt ? req->prompt : "";
    size_t n = strlen(prompt) + 6;
    out->output_text = (char *)malloc(n);
    if (!out->output_text) return false;
    snprintf(out->output_text, n, "echo:%s", prompt);
    out->prompt_token_count = approx_tokens(prompt);
    out->output_token_count = approx_tokens(out->output_text);
    out->status = CA_INFER_COMPLETED;
    out->inference_millis = 0.0;
    out->failure_message = NULL;
    out->reasoning_text = NULL;
    return true;
}

ca_inference_bridge_t *ca_echo_inference_bridge_create(void) {
    ca_inference_bridge_vtable_t vt = { echo_bridge_complete, NULL, NULL };
    return ca_inference_bridge_create(vt);
}

/* ===========================================================================
 * IBridgeFactory
 * =========================================================================== */

struct ca_bridge_factory {
    ca_bridge_factory_vtable_t vt;
};

ca_bridge_factory_t *ca_bridge_factory_create(ca_bridge_factory_vtable_t vt) {
    if (!vt.create) return NULL;
    ca_bridge_factory_t *f = (ca_bridge_factory_t *)calloc(1, sizeof(*f));
    if (!f) return NULL;
    f->vt = vt;
    return f;
}

void ca_bridge_factory_destroy(ca_bridge_factory_t *f) {
    if (!f) return;
    if (f->vt.destroy) f->vt.destroy(f->vt.state);
    free(f);
}

ca_inference_bridge_t *ca_bridge_factory_make(ca_bridge_factory_t *f, const char *model_id,
                                              ca_backend_kind_t backend,
                                              ca_capability_tier_t tier) {
    if (!f || is_blank(model_id)) return NULL;
    return f->vt.create(f->vt.state, model_id, backend, tier);
}

static ca_inference_bridge_t *unconfigured_create(void *state, const char *model_id,
                                                  ca_backend_kind_t backend,
                                                  ca_capability_tier_t tier) {
    (void)state; (void)model_id; (void)backend; (void)tier;
    return NULL; /* refuses every load */
}

ca_bridge_factory_t *ca_unconfigured_bridge_factory_create(void) {
    ca_bridge_factory_vtable_t vt = { unconfigured_create, NULL, NULL };
    return ca_bridge_factory_create(vt);
}

static ca_inference_bridge_t *echo_factory_create(void *state, const char *model_id,
                                                  ca_backend_kind_t backend,
                                                  ca_capability_tier_t tier) {
    (void)state; (void)model_id; (void)backend; (void)tier;
    return ca_echo_inference_bridge_create();
}

ca_bridge_factory_t *ca_echo_bridge_factory_create(void) {
    ca_bridge_factory_vtable_t vt = { echo_factory_create, NULL, NULL };
    return ca_bridge_factory_create(vt);
}

/* ===========================================================================
 * ITextEmbedder
 * =========================================================================== */

struct ca_text_embedder {
    ca_text_embedder_vtable_t vt;
};

ca_text_embedder_t *ca_text_embedder_create(ca_text_embedder_vtable_t vt) {
    if (!vt.generate) return NULL;
    ca_text_embedder_t *e = (ca_text_embedder_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->vt = vt;
    return e;
}

void ca_text_embedder_destroy(ca_text_embedder_t *e) {
    if (!e) return;
    if (e->vt.destroy) e->vt.destroy(e->vt.state);
    free(e);
}

float *ca_text_embedder_generate(ca_text_embedder_t *e, const char *text, size_t *out_dim) {
    if (!e || !text || !out_dim) return NULL;
    return e->vt.generate(e->vt.state, text, out_dim);
}

/* Hashing embedder — stable per-char FNV-1a folded into `dim` buckets, then
 * L2-normalised. Deterministic; no native deps. */
static float *hashing_generate(void *state, const char *text, size_t *out_dim) {
    size_t dim = (size_t)(uintptr_t)state;
    if (dim == 0) return NULL;
    float *v = (float *)calloc(dim, sizeof(float));
    if (!v) return NULL;
    uint32_t h = 2166136261u;
    for (const unsigned char *p = (const unsigned char *)(text ? text : ""); *p; p++) {
        h ^= *p;
        h *= 16777619u;
        v[h % dim] += 1.0f;
    }
    /* L2 normalise. */
    double s = 0.0;
    for (size_t i = 0; i < dim; i++) s += (double)v[i] * v[i];
    if (s > 0.0) {
        double norm = sqrt(s);
        for (size_t i = 0; i < dim; i++) v[i] = (float)((double)v[i] / norm);
    }
    *out_dim = dim;
    return v;
}

ca_text_embedder_t *ca_hashing_text_embedder_create(size_t dim) {
    if (dim == 0) return NULL;
    ca_text_embedder_vtable_t vt = { hashing_generate, NULL, (void *)(uintptr_t)dim };
    return ca_text_embedder_create(vt);
}

/* ===========================================================================
 * IInferenceServerModelRegistry
 * =========================================================================== */

typedef struct { char *id; ca_inference_bridge_t *bridge; } reg_chat_entry;
typedef struct { char *id; ca_text_embedder_t *embedder; } reg_embed_entry;

struct ca_inference_server_registry {
    reg_chat_entry  *chat;   size_t chat_count, chat_cap;
    reg_embed_entry *embed;  size_t embed_count, embed_cap;
};

ca_inference_server_registry_t *ca_inference_server_registry_create(void) {
    return (ca_inference_server_registry_t *)calloc(1, sizeof(ca_inference_server_registry_t));
}

void ca_inference_server_registry_destroy(ca_inference_server_registry_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->chat_count; i++) {
        free(r->chat[i].id);
        ca_inference_bridge_destroy(r->chat[i].bridge);
    }
    free(r->chat);
    for (size_t i = 0; i < r->embed_count; i++) {
        free(r->embed[i].id);
        ca_text_embedder_destroy(r->embed[i].embedder);
    }
    free(r->embed);
    free(r);
}

static reg_chat_entry *find_chat(ca_inference_server_registry_t *r, const char *id) {
    for (size_t i = 0; i < r->chat_count; i++)
        if (strcmp(r->chat[i].id, id) == 0) return &r->chat[i];
    return NULL;
}
static reg_embed_entry *find_embed(ca_inference_server_registry_t *r, const char *id) {
    for (size_t i = 0; i < r->embed_count; i++)
        if (strcmp(r->embed[i].id, id) == 0) return &r->embed[i];
    return NULL;
}

bool ca_inference_server_registry_register(ca_inference_server_registry_t *r,
                                           const char *model_id, ca_inference_bridge_t *bridge) {
    if (!r || is_blank(model_id) || !bridge) return false;
    reg_chat_entry *e = find_chat(r, model_id);
    if (e) { ca_inference_bridge_destroy(e->bridge); e->bridge = bridge; return true; }
    if (r->chat_count == r->chat_cap) {
        size_t nc = r->chat_cap ? r->chat_cap * 2 : 4;
        reg_chat_entry *n = (reg_chat_entry *)realloc(r->chat, nc * sizeof(*n));
        if (!n) return false;
        r->chat = n; r->chat_cap = nc;
    }
    r->chat[r->chat_count].id = xstrdup(model_id);
    if (!r->chat[r->chat_count].id) return false;
    r->chat[r->chat_count].bridge = bridge;
    r->chat_count++;
    return true;
}

bool ca_inference_server_registry_register_embedder(ca_inference_server_registry_t *r,
                                                    const char *model_id, ca_text_embedder_t *embedder) {
    if (!r || is_blank(model_id) || !embedder) return false;
    reg_embed_entry *e = find_embed(r, model_id);
    if (e) { ca_text_embedder_destroy(e->embedder); e->embedder = embedder; return true; }
    if (r->embed_count == r->embed_cap) {
        size_t nc = r->embed_cap ? r->embed_cap * 2 : 4;
        reg_embed_entry *n = (reg_embed_entry *)realloc(r->embed, nc * sizeof(*n));
        if (!n) return false;
        r->embed = n; r->embed_cap = nc;
    }
    r->embed[r->embed_count].id = xstrdup(model_id);
    if (!r->embed[r->embed_count].id) return false;
    r->embed[r->embed_count].embedder = embedder;
    r->embed_count++;
    return true;
}

bool ca_inference_server_registry_deregister(ca_inference_server_registry_t *r, const char *model_id) {
    if (!r || is_blank(model_id)) return false;
    for (size_t i = 0; i < r->chat_count; i++) {
        if (strcmp(r->chat[i].id, model_id) == 0) {
            free(r->chat[i].id);
            ca_inference_bridge_destroy(r->chat[i].bridge);
            r->chat[i] = r->chat[r->chat_count - 1];
            r->chat_count--;
            return true;
        }
    }
    return false;
}

ca_inference_bridge_t *ca_inference_server_registry_resolve(ca_inference_server_registry_t *r,
                                                            const char *model_id) {
    if (!r || is_blank(model_id)) return NULL;
    reg_chat_entry *e = find_chat(r, model_id);
    return e ? e->bridge : NULL;
}

ca_text_embedder_t *ca_inference_server_registry_resolve_embedder(ca_inference_server_registry_t *r,
                                                                  const char *model_id) {
    if (!r || is_blank(model_id)) return NULL;
    reg_embed_entry *e = find_embed(r, model_id);
    return e ? e->embedder : NULL;
}

char **ca_inference_server_registry_chat_model_ids(ca_inference_server_registry_t *r, size_t *out_count) {
    if (!r || !out_count) return NULL;
    *out_count = 0;
    if (r->chat_count == 0) return NULL;
    char **arr = (char **)malloc(r->chat_count * sizeof(char *));
    if (!arr) return NULL;
    for (size_t i = 0; i < r->chat_count; i++) arr[i] = xstrdup(r->chat[i].id);
    *out_count = r->chat_count;
    return arr;
}

char **ca_inference_server_registry_all_model_ids(ca_inference_server_registry_t *r, size_t *out_count) {
    if (!r || !out_count) return NULL;
    *out_count = 0;
    size_t cap = r->chat_count + r->embed_count;
    if (cap == 0) return NULL;
    char **arr = (char **)malloc(cap * sizeof(char *));
    if (!arr) return NULL;
    size_t n = 0;
    for (size_t i = 0; i < r->chat_count; i++) arr[n++] = xstrdup(r->chat[i].id);
    for (size_t i = 0; i < r->embed_count; i++) {
        bool dup = false;
        for (size_t j = 0; j < r->chat_count; j++)
            if (strcmp(r->embed[i].id, r->chat[j].id) == 0) { dup = true; break; }
        if (!dup) arr[n++] = xstrdup(r->embed[i].id);
    }
    *out_count = n;
    return arr;
}

/* ===========================================================================
 * IModelLifecycleManager
 * =========================================================================== */

void ca_model_load_state_free(ca_model_load_state_t *s) {
    if (!s) return;
    free(s->model_id);
    s->model_id = NULL;
}

void ca_model_load_states_free(ca_model_load_state_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; i++) ca_model_load_state_free(&arr[i]);
    free(arr);
}

void ca_load_result_free(ca_load_result_t *r) {
    if (!r) return;
    if (r->has_state) ca_model_load_state_free(&r->state);
    free(r->rationale);
    memset(r, 0, sizeof(*r));
}

typedef struct {
    char                 *model_id;
    ca_backend_kind_t     backend;
    ca_capability_tier_t  tier;
    int64_t               vram_bytes;
    int64_t               ram_bytes;
    int64_t               loaded_at_unix_ms;
} loaded_row;

struct ca_model_lifecycle_manager {
    ca_inference_server_registry_t *registry; /* borrowed */
    int64_t total_ram;
    int64_t gpu_vram;
    loaded_row *loaded; size_t count, cap;
};

ca_model_lifecycle_manager_t *ca_model_lifecycle_manager_create(
    ca_inference_server_registry_t *registry,
    int64_t total_physical_memory_bytes, int64_t gpu_vram_bytes) {
    if (!registry) return NULL;
    ca_model_lifecycle_manager_t *m =
        (ca_model_lifecycle_manager_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    m->registry = registry;
    m->total_ram = total_physical_memory_bytes;
    m->gpu_vram = gpu_vram_bytes;
    return m;
}

void ca_model_lifecycle_manager_destroy(ca_model_lifecycle_manager_t *m) {
    if (!m) return;
    for (size_t i = 0; i < m->count; i++) free(m->loaded[i].model_id);
    free(m->loaded);
    free(m);
}

static loaded_row *lm_find(ca_model_lifecycle_manager_t *m, const char *id) {
    for (size_t i = 0; i < m->count; i++)
        if (strcmp(m->loaded[i].model_id, id) == 0) return &m->loaded[i];
    return NULL;
}

int64_t ca_model_lifecycle_manager_total_vram(const ca_model_lifecycle_manager_t *m) {
    if (!m) return 0;
    int64_t s = 0;
    for (size_t i = 0; i < m->count; i++) s += m->loaded[i].vram_bytes;
    return s;
}

int64_t ca_model_lifecycle_manager_total_ram(const ca_model_lifecycle_manager_t *m) {
    if (!m) return 0;
    int64_t s = 0;
    for (size_t i = 0; i < m->count; i++) s += m->loaded[i].ram_bytes;
    return s;
}

static void fill_state_from_row(ca_model_load_state_t *st, const loaded_row *row) {
    st->model_id = xstrdup(row->model_id);
    st->backend = row->backend;
    st->tier = row->tier;
    st->vram_bytes = row->vram_bytes;
    st->ram_bytes = row->ram_bytes;
    st->loaded_at_unix_ms = row->loaded_at_unix_ms;
}

bool ca_model_lifecycle_manager_load(
    ca_model_lifecycle_manager_t *m, const char *model_id,
    ca_backend_kind_t backend, ca_capability_tier_t tier,
    int64_t vram_required_bytes, int64_t ram_required_bytes,
    ca_bridge_factory_t *factory, ca_load_result_t *out) {
    if (!m || is_blank(model_id) || !factory || !out) return false;
    memset(out, 0, sizeof(*out));

    /* Idempotent fast path. */
    loaded_row *existing = lm_find(m, model_id);
    if (existing) {
        out->outcome = CA_LOAD_ALREADY_LOADED;
        out->has_state = true;
        fill_state_from_row(&out->state, existing);
        char buf[256];
        snprintf(buf, sizeof(buf), "Model '%s' is already loaded.", model_id);
        out->rationale = xstrdup(buf);
        return true;
    }

    /* VRAM admission — GPU-class backends only. */
    if (backend == CA_BACKEND_CUDA || backend == CA_BACKEND_VULKAN ||
        backend == CA_BACKEND_METAL || backend == CA_BACKEND_OPENCL) {
        int64_t vram_ceiling = m->gpu_vram;
        int64_t vram_free = vram_ceiling - ca_model_lifecycle_manager_total_vram(m);
        if (vram_free < vram_required_bytes) {
            out->outcome = CA_LOAD_INSUFFICIENT_VRAM;
            char buf[256];
            snprintf(buf, sizeof(buf),
                "Need %lld MiB VRAM, have %lld MiB free.",
                (long long)(vram_required_bytes / (1024 * 1024)),
                (long long)((vram_free > 0 ? vram_free : 0) / (1024 * 1024)));
            out->rationale = xstrdup(buf);
            return true;
        }
    }

    /* RAM admission — always. */
    int64_t ram_free = m->total_ram - ca_model_lifecycle_manager_total_ram(m);
    if (ram_free < ram_required_bytes) {
        out->outcome = CA_LOAD_INSUFFICIENT_RAM;
        char buf[256];
        snprintf(buf, sizeof(buf),
            "Need %lld MiB RAM, have %lld MiB free.",
            (long long)(ram_required_bytes / (1024 * 1024)),
            (long long)((ram_free > 0 ? ram_free : 0) / (1024 * 1024)));
        out->rationale = xstrdup(buf);
        return true;
    }

    /* Reserve BEFORE invoking the factory. */
    if (m->count == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 4;
        loaded_row *n = (loaded_row *)realloc(m->loaded, nc * sizeof(*n));
        if (!n) return false;
        m->loaded = n; m->cap = nc;
    }
    loaded_row *row = &m->loaded[m->count];
    row->model_id = xstrdup(model_id);
    if (!row->model_id) return false;
    row->backend = backend;
    row->tier = tier;
    row->vram_bytes = vram_required_bytes;
    row->ram_bytes = ram_required_bytes;
    row->loaded_at_unix_ms = now_unix_ms();
    m->count++;

    ca_inference_bridge_t *bridge = ca_bridge_factory_make(factory, model_id, backend, tier);
    if (!bridge) {
        /* Roll back the reservation. */
        free(row->model_id);
        m->count--;
        out->outcome = CA_LOAD_FACTORY_FAILED;
        char buf[256];
        snprintf(buf, sizeof(buf), "Bridge factory for '%s' failed.", model_id);
        out->rationale = xstrdup(buf);
        return true;
    }

    if (!ca_inference_server_registry_register(m->registry, model_id, bridge)) {
        ca_inference_bridge_destroy(bridge);
        free(row->model_id);
        m->count--;
        out->outcome = CA_LOAD_FACTORY_FAILED;
        out->rationale = xstrdup("Registry registration failed.");
        return true;
    }

    out->outcome = CA_LOAD_LOADED;
    out->has_state = true;
    fill_state_from_row(&out->state, row);
    char buf[256];
    snprintf(buf, sizeof(buf), "Loaded '%s'.", model_id);
    out->rationale = xstrdup(buf);
    return true;
}

ca_unload_outcome_t ca_model_lifecycle_manager_unload(ca_model_lifecycle_manager_t *m,
                                                      const char *model_id) {
    if (!m || is_blank(model_id)) return CA_UNLOAD_NOT_LOADED;
    for (size_t i = 0; i < m->count; i++) {
        if (strcmp(m->loaded[i].model_id, model_id) == 0) {
            free(m->loaded[i].model_id);
            m->loaded[i] = m->loaded[m->count - 1];
            m->count--;
            /* Deregister destroys the bridge (registry owns it). */
            ca_inference_server_registry_deregister(m->registry, model_id);
            return CA_UNLOAD_UNLOADED;
        }
    }
    return CA_UNLOAD_NOT_LOADED;
}

ca_model_load_state_t *ca_model_lifecycle_manager_list(ca_model_lifecycle_manager_t *m,
                                                       size_t *out_count) {
    if (!m || !out_count) return NULL;
    *out_count = 0;
    if (m->count == 0) return NULL;
    ca_model_load_state_t *arr =
        (ca_model_load_state_t *)calloc(m->count, sizeof(*arr));
    if (!arr) return NULL;
    for (size_t i = 0; i < m->count; i++) fill_state_from_row(&arr[i], &m->loaded[i]);
    *out_count = m->count;
    return arr;
}

/* ===========================================================================
 * ICompanionSessionResolver
 * =========================================================================== */

typedef struct { char *session_id; char *identity_id; void *session; } session_entry;

struct ca_companion_session_resolver {
    ca_companion_session_factory_vtable_t vt;
    session_entry *entries; size_t count, cap;
};

ca_companion_session_resolver_t *ca_companion_session_resolver_create(
    ca_companion_session_factory_vtable_t vt) {
    if (!vt.create) return NULL;
    ca_companion_session_resolver_t *r =
        (ca_companion_session_resolver_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->vt = vt;
    return r;
}

void ca_companion_session_resolver_destroy(ca_companion_session_resolver_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->count; i++) {
        free(r->entries[i].session_id);
        free(r->entries[i].identity_id);
        if (r->vt.session_destroy) r->vt.session_destroy(r->entries[i].session);
    }
    free(r->entries);
    if (r->vt.state_destroy) r->vt.state_destroy(r->vt.state);
    free(r);
}

void *ca_companion_session_resolver_resolve(ca_companion_session_resolver_t *r,
                                            const char *session_id, const char *identity_id) {
    if (!r || is_blank(session_id) || is_blank(identity_id)) return NULL;
    for (size_t i = 0; i < r->count; i++)
        if (strcmp(r->entries[i].session_id, session_id) == 0 &&
            strcmp(r->entries[i].identity_id, identity_id) == 0)
            return r->entries[i].session;

    void *session = r->vt.create(r->vt.state, identity_id);
    if (!session) return NULL; /* failed construction does not poison the cache */

    if (r->count == r->cap) {
        size_t nc = r->cap ? r->cap * 2 : 4;
        session_entry *n = (session_entry *)realloc(r->entries, nc * sizeof(*n));
        if (!n) { if (r->vt.session_destroy) r->vt.session_destroy(session); return NULL; }
        r->entries = n; r->cap = nc;
    }
    r->entries[r->count].session_id = xstrdup(session_id);
    r->entries[r->count].identity_id = xstrdup(identity_id);
    r->entries[r->count].session = session;
    r->count++;
    return session;
}

int ca_companion_session_resolver_cached_count(const ca_companion_session_resolver_t *r) {
    return r ? (int)r->count : 0;
}

/* ===========================================================================
 * INativeRuntimeStatus
 * =========================================================================== */

void ca_native_runtime_paths_free(ca_native_runtime_paths_t *p) {
    if (!p) return;
    free(p->mnnbridge_path);
    free(p->mnn_core_path);
    free(p->extracted_root);
    memset(p, 0, sizeof(*p));
}

struct ca_native_runtime_status {
    bool has;
    ca_native_runtime_paths_t latest;
};

ca_native_runtime_status_t *ca_native_runtime_status_create(void) {
    return (ca_native_runtime_status_t *)calloc(1, sizeof(ca_native_runtime_status_t));
}

void ca_native_runtime_status_destroy(ca_native_runtime_status_t *s) {
    if (!s) return;
    ca_native_runtime_paths_free(&s->latest);
    free(s);
}

bool ca_native_runtime_status_update(ca_native_runtime_status_t *s,
                                     const char *mnnbridge_path,
                                     const char *mnn_core_path,
                                     const char *extracted_root) {
    if (!s) return false;
    char *a = mnnbridge_path ? xstrdup(mnnbridge_path) : NULL;
    char *b = mnn_core_path ? xstrdup(mnn_core_path) : NULL;
    char *c = extracted_root ? xstrdup(extracted_root) : NULL;
    if ((mnnbridge_path && !a) || (mnn_core_path && !b) || (extracted_root && !c)) {
        free(a); free(b); free(c);
        return false;
    }
    ca_native_runtime_paths_free(&s->latest);
    s->latest.mnnbridge_path = a;
    s->latest.mnn_core_path = b;
    s->latest.extracted_root = c;
    s->has = true;
    return true;
}

bool ca_native_runtime_status_latest(const ca_native_runtime_status_t *s,
                                     ca_native_runtime_paths_t *out) {
    if (!out) return false;
    memset(out, 0, sizeof(*out));
    if (!s || !s->has) return false;
    out->mnnbridge_path = s->latest.mnnbridge_path ? xstrdup(s->latest.mnnbridge_path) : NULL;
    out->mnn_core_path  = s->latest.mnn_core_path  ? xstrdup(s->latest.mnn_core_path)  : NULL;
    out->extracted_root = s->latest.extracted_root ? xstrdup(s->latest.extracted_root) : NULL;
    return true;
}

/* ===========================================================================
 * ApiKeyAuthHandler
 * =========================================================================== */

struct ca_api_key_auth {
    bool   enabled;
    char  *header_name;
    char **keys;
    size_t key_count;
};

ca_api_key_auth_t *ca_api_key_auth_create(bool enabled, const char *header_name,
                                          const char *const *keys, size_t key_count) {
    if (is_blank(header_name)) return NULL;
    ca_api_key_auth_t *h = (ca_api_key_auth_t *)calloc(1, sizeof(*h));
    if (!h) return NULL;
    h->enabled = enabled;
    h->header_name = xstrdup(header_name);
    if (!h->header_name) { free(h); return NULL; }
    if (key_count > 0 && keys) {
        h->keys = (char **)calloc(key_count, sizeof(char *));
        if (!h->keys) { free(h->header_name); free(h); return NULL; }
        for (size_t i = 0; i < key_count; i++) h->keys[i] = xstrdup(keys[i] ? keys[i] : "");
        h->key_count = key_count;
    }
    return h;
}

void ca_api_key_auth_destroy(ca_api_key_auth_t *h) {
    if (!h) return;
    free(h->header_name);
    if (h->keys) { for (size_t i = 0; i < h->key_count; i++) free(h->keys[i]); free(h->keys); }
    free(h);
}

/* Constant-time equality over equal-length byte spans. */
static bool fixed_time_equals(const char *a, size_t alen, const char *b, size_t blen) {
    if (alen != blen) return false;
    unsigned char diff = 0;
    for (size_t i = 0; i < alen; i++) diff |= (unsigned char)a[i] ^ (unsigned char)b[i];
    return diff == 0;
}

ca_auth_result_t ca_api_key_auth_authenticate(const ca_api_key_auth_t *h, const char *presented) {
    if (!h) return CA_AUTH_FAIL;
    if (!h->enabled) return CA_AUTH_SUCCESS_ANONYMOUS;
    if (is_blank(presented)) return CA_AUTH_NO_RESULT;
    size_t plen = strlen(presented);
    for (size_t i = 0; i < h->key_count; i++) {
        if (!h->keys[i] || h->keys[i][0] == 0) continue;
        if (fixed_time_equals(presented, plen, h->keys[i], strlen(h->keys[i])))
            return CA_AUTH_SUCCESS;
    }
    return CA_AUTH_FAIL;
}

const char *ca_api_key_auth_header_name(const ca_api_key_auth_t *h) {
    return h ? h->header_name : NULL;
}

/* ===========================================================================
 * DTO frees
 * =========================================================================== */

static void chat_message_free_fields(ca_chat_completion_message_t *m) {
    if (!m) return;
    free(m->role); free(m->content); free(m->name); free(m->reasoning_content);
    memset(m, 0, sizeof(*m));
}

void ca_chat_completion_request_free(ca_chat_completion_request_t *r) {
    if (!r) return;
    free(r->model);
    if (r->messages) {
        for (size_t i = 0; i < r->message_count; i++) chat_message_free_fields(&r->messages[i]);
        free(r->messages);
    }
    if (r->stop) { for (size_t i = 0; i < r->stop_count; i++) free(r->stop[i]); free(r->stop); }
    free(r->user);
    memset(r, 0, sizeof(*r));
}

void ca_chat_completion_response_free(ca_chat_completion_response_t *r) {
    if (!r) return;
    free(r->id); free(r->object); free(r->model);
    if (r->choices) {
        for (size_t i = 0; i < r->choice_count; i++) {
            chat_message_free_fields(&r->choices[i].message);
            free(r->choices[i].finish_reason);
        }
        free(r->choices);
    }
    memset(r, 0, sizeof(*r));
}

void ca_error_response_free(ca_error_response_t *e) {
    if (!e) return;
    free(e->message); free(e->type); free(e->code);
    memset(e, 0, sizeof(*e));
}

void ca_embeddings_request_free(ca_embeddings_request_t *r) {
    if (!r) return;
    free(r->model);
    if (r->inputs) { for (size_t i = 0; i < r->input_count; i++) free(r->inputs[i]); free(r->inputs); }
    free(r->user);
    memset(r, 0, sizeof(*r));
}

void ca_embeddings_response_free(ca_embeddings_response_t *r) {
    if (!r) return;
    free(r->object); free(r->model);
    if (r->data) { for (size_t i = 0; i < r->data_count; i++) free(r->data[i].embedding); free(r->data); }
    memset(r, 0, sizeof(*r));
}

static void set_error(ca_error_response_t *e, const char *msg, const char *type, const char *code) {
    if (!e) return;
    memset(e, 0, sizeof(*e));
    e->message = xstrdup(msg);
    e->type = xstrdup(type);
    e->code = code ? xstrdup(code) : NULL;
}

/* ===========================================================================
 * Chat completion routing
 * =========================================================================== */

/* Build the joined prompt (mirrors BuildInferenceRequest). */
static char *build_prompt(const ca_chat_completion_request_t *body) {
    /* "<|role|>\n<content>\n<|end|>" joined by "\n" */
    size_t total = 1;
    for (size_t i = 0; i < body->message_count; i++) {
        const char *role = body->messages[i].role ? body->messages[i].role : "";
        const char *content = body->messages[i].content ? body->messages[i].content : "";
        total += strlen(role) + strlen(content) + 16;
    }
    char *buf = (char *)malloc(total);
    if (!buf) return NULL;
    buf[0] = 0;
    size_t off = 0;
    for (size_t i = 0; i < body->message_count; i++) {
        const char *role = body->messages[i].role ? body->messages[i].role : "";
        const char *content = body->messages[i].content ? body->messages[i].content : "";
        int w = snprintf(buf + off, total - off, "%s<|%s|>\n%s\n<|end|>",
                         i ? "\n" : "", role, content);
        if (w < 0) { free(buf); return NULL; }
        off += (size_t)w;
    }
    return buf;
}

static const char *map_finish(ca_inference_status_t s) {
    switch (s) {
        case CA_INFER_COMPLETED:         return "stop";
        case CA_INFER_STOPPED_BY_TOKEN:  return "stop";
        case CA_INFER_STOPPED_BY_LENGTH: return "length";
        case CA_INFER_CANCELLED:         return "cancelled";
        default:                         return "error";
    }
}

/* Assemble a ca_inference_request_t from the body. */
static bool build_inference_request(const ca_chat_completion_request_t *body,
                                    ca_inference_request_t *req) {
    memset(req, 0, sizeof(*req));
    req->model_id = xstrdup(body->model);
    req->prompt = build_prompt(body);
    if (!req->model_id || !req->prompt) { ca_inference_request_free(req); return false; }
    req->max_output_tokens = body->has_max_tokens ? body->max_tokens : 512;
    req->temperature = body->has_temperature ? body->temperature : 0.7f;
    req->top_p = body->has_top_p ? body->top_p : 0.9f;
    if (body->stop_count > 0 && body->stop) {
        req->stop_sequences = (char **)calloc(body->stop_count, sizeof(char *));
        if (!req->stop_sequences) { ca_inference_request_free(req); return false; }
        for (size_t i = 0; i < body->stop_count; i++)
            req->stop_sequences[i] = xstrdup(body->stop[i] ? body->stop[i] : "");
        req->stop_count = body->stop_count;
    }
    return true;
}

ca_handler_status_t ca_handle_chat_completion(
    ca_inference_server_registry_t *registry, const ca_chat_completion_request_t *body,
    ca_chat_completion_response_t *out_resp, ca_error_response_t *out_err) {
    if (out_resp) memset(out_resp, 0, sizeof(*out_resp));
    if (out_err) memset(out_err, 0, sizeof(*out_err));
    if (!registry || !body || !out_resp || !out_err) return CA_HANDLER_INTERNAL_ERROR;

    if (is_blank(body->model)) {
        set_error(out_err, "Missing or empty 'model' field.", "invalid_request_error", "missing_model");
        return CA_HANDLER_BAD_REQUEST;
    }
    if (body->message_count == 0 || !body->messages) {
        set_error(out_err, "Missing 'messages' array.", "invalid_request_error", "missing_messages");
        return CA_HANDLER_BAD_REQUEST;
    }

    ca_inference_bridge_t *bridge = ca_inference_server_registry_resolve(registry, body->model);
    if (!bridge) {
        char buf[256];
        snprintf(buf, sizeof(buf), "Model '%s' is not loaded.", body->model);
        set_error(out_err, buf, "invalid_request_error", "model_not_found");
        return CA_HANDLER_NOT_FOUND;
    }

    ca_inference_request_t req;
    if (!build_inference_request(body, &req)) {
        set_error(out_err, "Out of memory building request.", "internal_error", "oom");
        return CA_HANDLER_INTERNAL_ERROR;
    }

    ca_inference_response_t resp;
    bool ok = ca_inference_bridge_complete(bridge, &req, &resp);
    ca_inference_request_free(&req);
    if (!ok) {
        set_error(out_err, "Bridge failure.", "internal_error", "bridge_failure");
        return CA_HANDLER_INTERNAL_ERROR;
    }
    if (resp.status == CA_INFER_FAILED) {
        set_error(out_err, resp.failure_message ? resp.failure_message : "Inference failed.",
                  "internal_error", "inference_failed");
        ca_inference_response_free(&resp);
        return CA_HANDLER_INTERNAL_ERROR;
    }

    /* Build the OpenAI-shaped response. */
    out_resp->id = make_id("chatcmpl-");
    out_resp->object = xstrdup("chat.completion");
    out_resp->created = now_unix_s();
    out_resp->model = xstrdup(body->model);
    out_resp->choices = (ca_chat_completion_choice_t *)calloc(1, sizeof(ca_chat_completion_choice_t));
    if (!out_resp->id || !out_resp->object || !out_resp->model || !out_resp->choices) {
        ca_inference_response_free(&resp);
        ca_chat_completion_response_free(out_resp);
        set_error(out_err, "Out of memory building response.", "internal_error", "oom");
        return CA_HANDLER_INTERNAL_ERROR;
    }
    out_resp->choice_count = 1;
    ca_chat_completion_choice_t *ch = &out_resp->choices[0];
    ch->index = 0;
    ch->message.role = xstrdup("assistant");
    ch->message.content = xstrdup(resp.output_text ? resp.output_text : "");
    ch->message.reasoning_content = resp.reasoning_text ? xstrdup(resp.reasoning_text) : NULL;
    ch->finish_reason = xstrdup(map_finish(resp.status));
    out_resp->usage.prompt_tokens = resp.prompt_token_count;
    out_resp->usage.completion_tokens = resp.output_token_count;
    out_resp->usage.total_tokens = resp.prompt_token_count + resp.output_token_count;

    ca_inference_response_free(&resp);
    return CA_HANDLER_OK;
}

ca_handler_status_t ca_handle_chat_completion_stream(
    ca_inference_server_registry_t *registry, const ca_chat_completion_request_t *body,
    ca_chat_stream_delta_fn on_delta, void *user, ca_error_response_t *out_err) {
    if (out_err) memset(out_err, 0, sizeof(*out_err));
    if (!registry || !body || !on_delta || !out_err) return CA_HANDLER_INTERNAL_ERROR;

    if (is_blank(body->model)) {
        set_error(out_err, "Missing or empty 'model' field.", "invalid_request_error", "missing_model");
        return CA_HANDLER_BAD_REQUEST;
    }
    if (body->message_count == 0 || !body->messages) {
        set_error(out_err, "Missing 'messages' array.", "invalid_request_error", "missing_messages");
        return CA_HANDLER_BAD_REQUEST;
    }
    ca_inference_bridge_t *bridge = ca_inference_server_registry_resolve(registry, body->model);
    if (!bridge) {
        char buf[256];
        snprintf(buf, sizeof(buf), "Model '%s' is not loaded.", body->model);
        set_error(out_err, buf, "invalid_request_error", "model_not_found");
        return CA_HANDLER_NOT_FOUND;
    }

    ca_inference_request_t req;
    if (!build_inference_request(body, &req)) {
        set_error(out_err, "Out of memory building request.", "internal_error", "oom");
        return CA_HANDLER_INTERNAL_ERROR;
    }

    /* Role announcement frame. */
    { ca_chat_stream_delta_t d = { 0, NULL, false, NULL }; on_delta(&d, user); }

    ca_inference_response_t resp;
    bool ok = ca_inference_bridge_complete(bridge, &req, &resp);
    ca_inference_request_free(&req);

    if (ok && resp.status != CA_INFER_FAILED) {
        /* Reasoning frame (if any), then content frame. Non-streaming bridges
         * surface the full text at once — the router emits it as one frame,
         * mirroring the C# per-fragment loop with a single fragment. */
        if (resp.reasoning_text && resp.reasoning_text[0]) {
            char *t = xstrdup(resp.reasoning_text);
            ca_chat_stream_delta_t d = { 1, t, false, NULL };
            on_delta(&d, user);
            free(t);
        }
        if (resp.output_text && resp.output_text[0]) {
            char *t = xstrdup(resp.output_text);
            ca_chat_stream_delta_t d = { 0, t, false, NULL };
            on_delta(&d, user);
            free(t);
        }
    } else {
        char *msg = NULL;
        const char *m = (ok && resp.failure_message) ? resp.failure_message : "bridge failure";
        size_t n = strlen(m) + 16;
        msg = (char *)malloc(n);
        if (msg) {
            snprintf(msg, n, "[error: %s]", m);
            char *fr = xstrdup("error");
            ca_chat_stream_delta_t d = { 0, msg, false, fr };
            on_delta(&d, user);
            free(msg); free(fr);
        }
    }
    if (ok) ca_inference_response_free(&resp);

    /* Final stop frame. */
    { char *fr = xstrdup("stop"); ca_chat_stream_delta_t d = { 0, NULL, true, fr }; on_delta(&d, user); free(fr); }
    return CA_HANDLER_OK;
}

/* ===========================================================================
 * Embeddings routing
 * =========================================================================== */

ca_handler_status_t ca_handle_embeddings(
    ca_inference_server_registry_t *registry, const ca_embeddings_request_t *body,
    ca_embeddings_response_t *out_resp, ca_error_response_t *out_err) {
    if (out_resp) memset(out_resp, 0, sizeof(*out_resp));
    if (out_err) memset(out_err, 0, sizeof(*out_err));
    if (!registry || !body || !out_resp || !out_err) return CA_HANDLER_INTERNAL_ERROR;

    if (is_blank(body->model)) {
        set_error(out_err, "Missing or empty 'model' field.", "invalid_request_error", "missing_model");
        return CA_HANDLER_BAD_REQUEST;
    }
    ca_text_embedder_t *embedder = ca_inference_server_registry_resolve_embedder(registry, body->model);
    if (!embedder) {
        char buf[256];
        snprintf(buf, sizeof(buf), "Embedding model '%s' is not loaded.", body->model);
        set_error(out_err, buf, "invalid_request_error", "model_not_found");
        return CA_HANDLER_NOT_FOUND;
    }
    if (body->input_count == 0 || !body->inputs) {
        set_error(out_err, "'input' array must not be empty.", "invalid_request_error", "invalid_input");
        return CA_HANDLER_BAD_REQUEST;
    }

    out_resp->data = (ca_embedding_datum_t *)calloc(body->input_count, sizeof(ca_embedding_datum_t));
    if (!out_resp->data) {
        set_error(out_err, "Out of memory.", "internal_error", "oom");
        return CA_HANDLER_INTERNAL_ERROR;
    }
    int total_chars = 0;
    for (size_t i = 0; i < body->input_count; i++) {
        size_t dim = 0;
        float *vec = ca_text_embedder_generate(embedder, body->inputs[i] ? body->inputs[i] : "", &dim);
        if (!vec) {
            ca_embeddings_response_free(out_resp);
            set_error(out_err, "Embedding failure.", "internal_error", "embedding_failure");
            return CA_HANDLER_INTERNAL_ERROR;
        }
        out_resp->data[i].index = (int)i;
        out_resp->data[i].embedding = vec;
        out_resp->data[i].dim = dim;
        total_chars += (int)strlen(body->inputs[i] ? body->inputs[i] : "");
    }
    out_resp->data_count = body->input_count;
    out_resp->object = xstrdup("list");
    out_resp->model = xstrdup(body->model);
    int est = total_chars / 4;
    if (est < 1) est = 1;
    out_resp->usage.prompt_tokens = est;
    out_resp->usage.completion_tokens = 0;
    out_resp->usage.total_tokens = est;
    return CA_HANDLER_OK;
}
