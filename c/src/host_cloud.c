/*
 * host_cloud.c — CircleAI.Hosting.CloudFallback (C11 port). See host_cloud.h.
 *
 * CloudFallbackChain (start-of-call ordering + fail-soft-frame filtering) and
 * BackupBrainOrchestrator (between-turn failover with degrade / cool-down /
 * half-open retry) ported from the C#. Cloud generators are injected behind the
 * generator seam; a deterministic fake stands in for tests.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/host_cloud.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>

static char *cl_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static bool contains_ci(const char *hay, const char *needle) {
    if (!hay || !needle || !*needle) return false;
    size_t nl = strlen(needle);
    for (const char *p = hay; *p; ++p) {
        size_t i = 0;
        while (i < nl && p[i] && tolower((unsigned char)p[i]) == tolower((unsigned char)needle[i])) i++;
        if (i == nl) return true;
    }
    return false;
}

/* ===========================================================================
 * Generator seam dispatchers
 * =========================================================================== */

char *ca_chat_gen_generate(ca_chat_gen_iface_t *g, const ca_chat_msg_t *messages,
                           size_t count, const ca_generation_options_t *opts) {
    return (g && g->generate) ? g->generate(g->self, messages, count, opts) : NULL;
}
long ca_chat_gen_stream(ca_chat_gen_iface_t *g, const ca_chat_msg_t *messages,
                        size_t count, const ca_generation_options_t *opts,
                        ca_gen_chunk_fn on_chunk, void *chunk_user) {
    if (!g) return -1;
    if (g->stream) return g->stream(g->self, messages, count, opts, on_chunk, chunk_user);
    /* fall back to generate as a single frame */
    char *full = ca_chat_gen_generate(g, messages, count, opts);
    if (!full) return -1;
    long r = 0;
    if (full[0] != '\0') { if (on_chunk) on_chunk(chunk_user, full); r = 1; }
    free(full);
    return r;
}
bool ca_chat_gen_is_configured(ca_chat_gen_iface_t *g) {
    if (!g) return false;
    return g->is_configured ? g->is_configured(g->self) : true; /* default true */
}
const char *ca_chat_gen_engine_label(ca_chat_gen_iface_t *g) {
    return (g && g->engine_label) ? g->engine_label(g->self) : NULL;
}
const char *ca_chat_gen_status_message(ca_chat_gen_iface_t *g) {
    return (g && g->status_message) ? g->status_message(g->self) : NULL;
}

/* ===========================================================================
 * Fake generator
 * =========================================================================== */

struct ca_fake_chat_generator {
    char *label;
    bool  configured;
    char *reply_override;
    int   fail_times;
    char *status;
};

static const char *last_user_content(const ca_chat_msg_t *messages, size_t count) {
    for (size_t i = count; i > 0; --i)
        if (messages[i - 1].role && strcmp(messages[i - 1].role, "user") == 0)
            return messages[i - 1].content ? messages[i - 1].content : "";
    if (count > 0 && messages[count - 1].content) return messages[count - 1].content;
    return "";
}

static char *fake_generate(void *self, const ca_chat_msg_t *messages, size_t count,
                           const ca_generation_options_t *opts) {
    (void)opts;
    ca_fake_chat_generator_t *g = (ca_fake_chat_generator_t *)self;
    if (g->fail_times > 0) { g->fail_times--; return NULL; } /* hard failure */
    if (!g->configured) {
        /* fail-soft frame, mirrors OpenAiCompatible "[<status>]" */
        size_t n = strlen(g->label) + 40;
        char *s = (char *)malloc(n);
        if (s) snprintf(s, n, "[%s API key not configured.]", g->label);
        return s;
    }
    if (g->reply_override) return cl_strdup(g->reply_override);
    const char *u = last_user_content(messages, count);
    size_t n = strlen(g->label) + strlen(u) + 4;
    char *s = (char *)malloc(n);
    if (s) snprintf(s, n, "%s: %s", g->label, u);
    return s;
}
static long fake_stream(void *self, const ca_chat_msg_t *messages, size_t count,
                        const ca_generation_options_t *opts,
                        ca_gen_chunk_fn on_chunk, void *chunk_user) {
    ca_fake_chat_generator_t *g = (ca_fake_chat_generator_t *)self;
    if (g->fail_times > 0) { g->fail_times--; return -1; }
    char *full = fake_generate(self, messages, count, opts);
    if (!full) return -1;
    long r = 0;
    if (full[0] != '\0') { if (on_chunk) on_chunk(chunk_user, full); r = 1; }
    free(full);
    return r;
}
static bool fake_is_configured(void *self) { return ((ca_fake_chat_generator_t *)self)->configured; }
static const char *fake_engine_label(void *self) { return ((ca_fake_chat_generator_t *)self)->label; }
static const char *fake_status_message(void *self) {
    ca_fake_chat_generator_t *g = (ca_fake_chat_generator_t *)self;
    free(g->status);
    size_t n = strlen(g->label) + 40;
    g->status = (char *)malloc(n);
    if (g->status) snprintf(g->status, n, "%s", g->configured ? "Ready" : "API key not configured.");
    return g->status;
}

ca_fake_chat_generator_t *ca_fake_chat_generator_create(const char *engine_label, bool configured) {
    ca_fake_chat_generator_t *g = (ca_fake_chat_generator_t *)calloc(1, sizeof(*g));
    if (!g) return NULL;
    g->label = cl_strdup(engine_label ? engine_label : "fake");
    g->configured = configured;
    return g;
}
void ca_fake_chat_generator_destroy(ca_fake_chat_generator_t *g) {
    if (!g) return;
    free(g->label); free(g->reply_override); free(g->status);
    free(g);
}
void ca_fake_chat_generator_set_fail_times(ca_fake_chat_generator_t *g, int fail_times) {
    if (g) g->fail_times = fail_times;
}
void ca_fake_chat_generator_set_reply(ca_fake_chat_generator_t *g, const char *reply) {
    if (!g) return;
    free(g->reply_override);
    g->reply_override = cl_strdup(reply);
}
ca_chat_gen_iface_t ca_fake_chat_generator_as_iface(ca_fake_chat_generator_t *g) {
    ca_chat_gen_iface_t v;
    v.generate = fake_generate;
    v.stream = fake_stream;
    v.is_configured = fake_is_configured;
    v.engine_label = fake_engine_label;
    v.status_message = fake_status_message;
    v.destroy = NULL;
    v.self = g;
    return v;
}

/* ===========================================================================
 * CloudFallbackChain
 * =========================================================================== */

struct ca_cloud_fallback_chain {
    ca_chat_gen_iface_t *gens;
    size_t               count;
    bool                 own;
};

ca_cloud_fallback_chain_t *ca_cloud_fallback_chain_create(
    const ca_chat_gen_iface_t *generators, size_t count, bool own) {
    ca_cloud_fallback_chain_t *c = (ca_cloud_fallback_chain_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    if (count) {
        c->gens = (ca_chat_gen_iface_t *)calloc(count, sizeof(ca_chat_gen_iface_t));
        if (!c->gens) { free(c); return NULL; }
        memcpy(c->gens, generators, count * sizeof(ca_chat_gen_iface_t));
        c->count = count;
    }
    c->own = own;
    return c;
}
void ca_cloud_fallback_chain_destroy(ca_cloud_fallback_chain_t *c) {
    if (!c) return;
    if (c->own)
        for (size_t i = 0; i < c->count; ++i)
            if (c->gens[i].destroy) c->gens[i].destroy(c->gens[i].self);
    free(c->gens);
    free(c);
}
size_t ca_cloud_fallback_chain_count(const ca_cloud_fallback_chain_t *c) { return c ? c->count : 0; }

static const char *CHAIN_SENTINEL =
    "[CloudFallbackChain: no configured generator could serve the request]";

static bool chain_is_ready(ca_chat_gen_iface_t *g) {
    /* IsReady: not-configurable => true; else IsConfigured. */
    if (!g->is_configured) return true;
    return g->is_configured(g->self);
}
static bool is_fail_soft_frame(const char *chunk) {
    return chunk && chunk[0] == '['
        && (contains_ci(chunk, "not configured") || contains_ci(chunk, "CloudFallbackChain"));
}

char *ca_cloud_fallback_chain_generate(ca_cloud_fallback_chain_t *c,
                                       const ca_chat_msg_t *messages, size_t count,
                                       const ca_generation_options_t *opts) {
    if (!c) return cl_strdup(CHAIN_SENTINEL);
    for (size_t i = 0; i < c->count; ++i) {
        if (!chain_is_ready(&c->gens[i])) continue;
        char *r = ca_chat_gen_generate(&c->gens[i], messages, count, opts);
        if (r) return r;
        /* generate returned NULL => throw => fall through to next */
    }
    return cl_strdup(CHAIN_SENTINEL);
}

/* Streaming with fail-soft-frame filtering. We buffer only the first frame per
 * generator to decide readiness (matches the C# "commit on first real frame"). */
typedef struct {
    ca_gen_chunk_fn on_chunk;
    void           *user;
    bool            yielded;
    bool            declined;   /* first frame was fail-soft */
    long            count;
    bool            stopped;
} chain_stream_ctx;

static bool chain_stream_relay(void *user, const char *chunk) {
    chain_stream_ctx *ctx = (chain_stream_ctx *)user;
    if (!ctx->yielded && is_fail_soft_frame(chunk)) {
        ctx->declined = true;
        return false; /* stop this generator's stream */
    }
    ctx->yielded = true;
    ctx->count++;
    if (ctx->on_chunk) {
        bool cont = ctx->on_chunk(ctx->user, chunk);
        if (!cont) { ctx->stopped = true; return false; }
    }
    return true;
}

long ca_cloud_fallback_chain_stream(ca_cloud_fallback_chain_t *c,
                                    const ca_chat_msg_t *messages, size_t count,
                                    const ca_generation_options_t *opts,
                                    ca_gen_chunk_fn on_chunk, void *chunk_user) {
    if (!c) { if (on_chunk) on_chunk(chunk_user, CHAIN_SENTINEL); return 1; }
    for (size_t i = 0; i < c->count; ++i) {
        if (!chain_is_ready(&c->gens[i])) continue;
        chain_stream_ctx ctx = { on_chunk, chunk_user, false, false, 0, false };
        long r = ca_chat_gen_stream(&c->gens[i], messages, count, opts, chain_stream_relay, &ctx);
        if (ctx.declined && !ctx.yielded) continue;   /* fail-soft: try next */
        if (r < 0 && !ctx.yielded) continue;          /* faulted before yield: try next */
        if (ctx.yielded) return ctx.count;             /* committed */
    }
    if (on_chunk) on_chunk(chunk_user, CHAIN_SENTINEL);
    return 1;
}

/* ===========================================================================
 * BackupBrainOrchestrator
 * =========================================================================== */

void ca_backup_brain_policy_init(ca_backup_brain_policy_t *p) {
    if (!p) return;
    p->degraded_after_failures = 2;
    p->cool_down_ms = 30LL * 1000;
    p->max_retries_per_turn = 3;
}

void ca_brain_status_free(ca_brain_status_t *s) {
    if (!s) return;
    free(s->label); s->label = NULL;
}
void ca_brain_status_free_array(ca_brain_status_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_brain_status_free(&arr[i]);
    free(arr);
}

typedef struct {
    ca_chat_gen_iface_t brain;
    int                 consecutive;
    int64_t             degraded_since_ms;
    bool                is_degraded;
} brain_entry;

struct ca_backup_brain_orchestrator {
    brain_entry             *brains;
    size_t                   count;
    ca_backup_brain_policy_t policy;
    bool                     own;
};

ca_backup_brain_orchestrator_t *ca_backup_brain_orchestrator_create(
    const ca_chat_gen_iface_t *brains, size_t count,
    const ca_backup_brain_policy_t *policy, bool own) {
    if (count == 0) return NULL;
    ca_backup_brain_orchestrator_t *o = (ca_backup_brain_orchestrator_t *)calloc(1, sizeof(*o));
    if (!o) return NULL;
    o->brains = (brain_entry *)calloc(count, sizeof(brain_entry));
    if (!o->brains) { free(o); return NULL; }
    for (size_t i = 0; i < count; ++i) o->brains[i].brain = brains[i];
    o->count = count;
    if (policy) o->policy = *policy;
    else ca_backup_brain_policy_init(&o->policy);
    o->own = own;
    return o;
}
void ca_backup_brain_orchestrator_destroy(ca_backup_brain_orchestrator_t *o) {
    if (!o) return;
    if (o->own)
        for (size_t i = 0; i < o->count; ++i)
            if (o->brains[i].brain.destroy) o->brains[i].brain.destroy(o->brains[i].brain.self);
    free(o->brains);
    free(o);
}

static ca_brain_health_t entry_health(const brain_entry *e, int64_t now, int64_t cool_down) {
    if (!e->is_degraded) return CA_BRAIN_HEALTHY;
    if (now - e->degraded_since_ms >= cool_down) return CA_BRAIN_COOLING_DOWN; /* half-open */
    return CA_BRAIN_DEGRADED;
}
static void entry_success(brain_entry *e) { e->consecutive = 0; e->is_degraded = false; }
static void entry_failure(brain_entry *e, int threshold, int64_t now) {
    e->consecutive++;
    if (e->consecutive >= threshold) { e->is_degraded = true; e->degraded_since_ms = now; }
}

/* pick first available (Healthy/CoolingDown) not in `tried`; else first untried.
 */
static brain_entry *pick_available(ca_backup_brain_orchestrator_t *o, int64_t now,
                                   const bool *tried) {
    int64_t cd = o->policy.cool_down_ms;
    for (size_t i = 0; i < o->count; ++i) {
        if (tried[i]) continue;
        ca_brain_health_t h = entry_health(&o->brains[i], now, cd);
        if (h == CA_BRAIN_HEALTHY || h == CA_BRAIN_COOLING_DOWN) return &o->brains[i];
    }
    for (size_t i = 0; i < o->count; ++i)
        if (!tried[i]) return &o->brains[i];
    return NULL;
}

char *ca_backup_brain_orchestrator_generate(ca_backup_brain_orchestrator_t *o,
                                            const ca_chat_msg_t *messages, size_t count,
                                            const ca_generation_options_t *opts,
                                            int64_t now_ms) {
    if (!o) return cl_strdup("[All brains failed.]");
    int max_retries = o->policy.max_retries_per_turn;
    if ((size_t)max_retries > o->count) max_retries = (int)o->count;
    bool *tried = (bool *)calloc(o->count, sizeof(bool));
    if (!tried) return cl_strdup("[All brains failed.]");

    char *result = NULL;
    for (int attempt = 0; attempt < max_retries; ++attempt) {
        brain_entry *pick = pick_available(o, now_ms, tried);
        if (!pick) break;
        size_t idx = (size_t)(pick - o->brains);
        tried[idx] = true;
        char *r = ca_chat_gen_generate(&pick->brain, messages, count, opts);
        if (r) { entry_success(pick); result = r; break; }
        entry_failure(pick, o->policy.degraded_after_failures, now_ms);
    }
    free(tried);
    return result ? result : cl_strdup("[All brains failed.]");
}

ca_brain_status_t *ca_backup_brain_orchestrator_statuses(
    ca_backup_brain_orchestrator_t *o, int64_t now_ms, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!o || o->count == 0) return NULL;
    ca_brain_status_t *arr = (ca_brain_status_t *)calloc(o->count, sizeof(*arr));
    if (!arr) return NULL;
    for (size_t i = 0; i < o->count; ++i) {
        brain_entry *e = &o->brains[i];
        const char *label = ca_chat_gen_engine_label(&e->brain);
        arr[i].label = cl_strdup(label ? label : "brain");
        arr[i].health = entry_health(e, now_ms, o->policy.cool_down_ms);
        arr[i].consecutive_failures = e->consecutive;
    }
    if (out_count) *out_count = o->count;
    return arr;
}
