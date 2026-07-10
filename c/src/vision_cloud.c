/*
 * vision_cloud.c — CircleAI.Vision.Cloud IImageGenerator (C11 port).
 *
 * Ports NullImageGenerator + ImageGeneratorFallbackChain, the IImageGenerator
 * seam dispatchers, and a deterministic fake generator for tests. The OpenAI /
 * Stability HTTP generators are injected behind the seam.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/vision_cloud.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

static char *vc_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *vc_strdup_empty(const char *s) { return vc_strdup(s ? s : ""); }

/* Math.Clamp(v, lo, hi). */
static int clamp_int(int v, int lo, int hi) {
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

/* ===========================================================================
 * ImageGenerationRequest
 * =========================================================================== */

void ca_image_generation_request_init(ca_image_generation_request_t *req,
                                      const char *prompt) {
    if (!req) return;
    req->prompt = prompt;
    req->negative_prompt = NULL;
    req->size = 1024;
    req->count = 1;
    req->style = NULL;
}

/* ===========================================================================
 * ImageArtifact
 * =========================================================================== */

void ca_image_artifact_free(ca_image_artifact_t *a) {
    if (!a) return;
    free(a->generator_id);
    free(a->prompt);
    free(a->mime_type);
    free(a->url);
    free(a->bytes);
    a->generator_id = a->prompt = a->mime_type = a->url = NULL;
    a->bytes = NULL;
    a->byte_count = 0;
}
void ca_image_artifact_free_array(ca_image_artifact_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_image_artifact_free(&arr[i]);
    free(arr);
}
int ca_image_artifact_copy(ca_image_artifact_t *dst, const ca_image_artifact_t *src) {
    if (!dst || !src) return -1;
    memset(dst, 0, sizeof(*dst));
    dst->generator_id = vc_strdup_empty(src->generator_id);
    dst->prompt = vc_strdup_empty(src->prompt);
    dst->mime_type = vc_strdup_empty(src->mime_type);
    dst->url = src->url ? vc_strdup(src->url) : NULL;
    dst->generated_at_utc_ms = src->generated_at_utc_ms;
    if (!dst->generator_id || !dst->prompt || !dst->mime_type ||
        (src->url && !dst->url)) {
        ca_image_artifact_free(dst);
        return -1;
    }
    if (src->bytes && src->byte_count) {
        dst->bytes = (uint8_t *)malloc(src->byte_count);
        if (!dst->bytes) { ca_image_artifact_free(dst); return -1; }
        memcpy(dst->bytes, src->bytes, src->byte_count);
        dst->byte_count = src->byte_count;
    }
    return 0;
}

/* ===========================================================================
 * IImageGenerator seam dispatchers
 * =========================================================================== */

const char *ca_image_generator_id(const ca_image_generator_t *g) {
    if (!g || !g->generator_id) return "null";
    return g->generator_id(g->self);
}
const char *ca_image_generator_display_label(const ca_image_generator_t *g) {
    if (!g || !g->display_label) return "";
    return g->display_label(g->self);
}
bool ca_image_generator_is_configured(const ca_image_generator_t *g) {
    if (!g || !g->is_configured) return false;
    return g->is_configured(g->self);
}
const char *ca_image_generator_status_message(const ca_image_generator_t *g) {
    if (!g || !g->status_message) return "";
    return g->status_message(g->self);
}
ca_image_artifact_t *ca_image_generator_generate(const ca_image_generator_t *g,
                                                 const ca_image_generation_request_t *req,
                                                 size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!g || !g->generate) return NULL;
    return g->generate(g->self, req, out_count);
}

/* ===========================================================================
 * NullImageGenerator
 * =========================================================================== */

static const char *nullig_id(void *self) { (void)self; return "null"; }
static const char *nullig_label(void *self) { (void)self; return "No image generator"; }
static bool        nullig_configured(void *self) { (void)self; return false; }
static const char *nullig_status(void *self) {
    (void)self;
    return "No image generator wired. Configure OpenAI:ApiKey or Stability:ApiKey to enable.";
}
static ca_image_artifact_t *nullig_generate(void *self,
                                            const ca_image_generation_request_t *req,
                                            size_t *out_count) {
    (void)self; (void)req;
    if (out_count) *out_count = 0;
    return NULL;   /* Array.Empty<ImageArtifact>() */
}
ca_image_generator_t ca_null_image_generator(void) {
    ca_image_generator_t g;
    g.self = NULL;
    g.generator_id = nullig_id;
    g.display_label = nullig_label;
    g.is_configured = nullig_configured;
    g.status_message = nullig_status;
    g.generate = nullig_generate;
    g.destroy = NULL;
    return g;
}

/* ===========================================================================
 * Deterministic fake generator
 * =========================================================================== */

struct ca_fake_image_generator {
    char   *id;
    char   *label;
    bool    configured;
    int64_t clock_ms;
    char   *status;   /* cached */
};

ca_fake_image_generator_t *ca_fake_image_generator_create(const char *generator_id,
                                                          const char *display_label,
                                                          bool configured,
                                                          int64_t fixed_clock_ms) {
    ca_fake_image_generator_t *g =
        (ca_fake_image_generator_t *)calloc(1, sizeof(*g));
    if (!g) return NULL;
    g->id = vc_strdup_empty(generator_id);
    g->label = vc_strdup_empty(display_label);
    g->configured = configured;
    g->clock_ms = fixed_clock_ms;
    /* Ready · <id>  /  <id> not configured — a simple deterministic status. */
    const char *idv = g->id ? g->id : "";
    size_t need = strlen(idv) + 32;
    g->status = (char *)malloc(need);
    if (!g->id || !g->label || !g->status) {
        ca_fake_image_generator_destroy(g);
        return NULL;
    }
    if (configured) snprintf(g->status, need, "Ready · %s", idv);
    else            snprintf(g->status, need, "%s not configured", idv);
    return g;
}
void ca_fake_image_generator_destroy(ca_fake_image_generator_t *g) {
    if (!g) return;
    free(g->id);
    free(g->label);
    free(g->status);
    free(g);
}

static const char *fakeig_id(void *self) {
    return ((ca_fake_image_generator_t *)self)->id;
}
static const char *fakeig_label(void *self) {
    return ((ca_fake_image_generator_t *)self)->label;
}
static bool fakeig_configured(void *self) {
    return ((ca_fake_image_generator_t *)self)->configured;
}
static const char *fakeig_status(void *self) {
    return ((ca_fake_image_generator_t *)self)->status;
}
static ca_image_artifact_t *fakeig_generate(void *self,
                                            const ca_image_generation_request_t *req,
                                            size_t *out_count) {
    ca_fake_image_generator_t *g = (ca_fake_image_generator_t *)self;
    if (out_count) *out_count = 0;
    if (!g->configured) return NULL;                 /* fail-soft empty */
    int reqcount = req ? req->count : 1;
    int n = clamp_int(reqcount, 1, 4);               /* Math.Clamp(Count,1,4) */
    const char *prompt = (req && req->prompt) ? req->prompt : "";
    ca_image_artifact_t *arr =
        (ca_image_artifact_t *)calloc((size_t)n, sizeof(*arr));
    if (!arr) { if (out_count) *out_count = (size_t)-1; return NULL; }
    for (int i = 0; i < n; ++i) {
        ca_image_artifact_t *a = &arr[i];
        a->generator_id = vc_strdup(g->id);
        a->prompt = vc_strdup(prompt);
        a->mime_type = vc_strdup("image/png");
        size_t need = strlen(g->id ? g->id : "") + strlen(prompt) + 32;
        a->url = (char *)malloc(need);
        a->generated_at_utc_ms = g->clock_ms;
        if (!a->generator_id || !a->prompt || !a->mime_type || !a->url) {
            ca_image_artifact_free_array(arr, (size_t)n);
            if (out_count) *out_count = (size_t)-1;
            return NULL;
        }
        snprintf(a->url, need, "mem://%s/%s/%d", g->id ? g->id : "", prompt, i);
    }
    if (out_count) *out_count = (size_t)n;
    return arr;
}
ca_image_generator_t ca_fake_image_generator_as_iface(ca_fake_image_generator_t *g) {
    ca_image_generator_t i;
    i.self = g;
    i.generator_id = fakeig_id;
    i.display_label = fakeig_label;
    i.is_configured = fakeig_configured;
    i.status_message = fakeig_status;
    i.generate = fakeig_generate;
    i.destroy = NULL;
    return i;
}

/* ===========================================================================
 * ImageGeneratorFallbackChain
 * =========================================================================== */

struct ca_image_generator_fallback_chain {
    ca_image_generator_t *chain;
    size_t                count;
    bool                  own;
    char                 *label_cache;   /* "Fallback (<n>)" */
    char                 *status_cache;  /* recomputed each StatusMessage call */
};

ca_image_generator_fallback_chain_t *ca_image_generator_fallback_chain_create(
    const ca_image_generator_t *generators, size_t count, bool own) {
    ca_image_generator_fallback_chain_t *c =
        (ca_image_generator_fallback_chain_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->own = own;
    if (count) {
        c->chain = (ca_image_generator_t *)malloc(count * sizeof(*c->chain));
        if (!c->chain) { free(c); return NULL; }
        memcpy(c->chain, generators, count * sizeof(*c->chain));
        c->count = count;
    }
    /* DisplayLabel: "Fallback (<count>)" — fixed for the lifetime. */
    c->label_cache = (char *)malloc(32);
    if (!c->label_cache) { free(c->chain); free(c); return NULL; }
    snprintf(c->label_cache, 32, "Fallback (%zu)", c->count);
    return c;
}
void ca_image_generator_fallback_chain_destroy(ca_image_generator_fallback_chain_t *c) {
    if (!c) return;
    if (c->own) {
        for (size_t i = 0; i < c->count; ++i)
            if (c->chain[i].destroy) c->chain[i].destroy(c->chain[i].self);
    }
    free(c->chain);
    free(c->label_cache);
    free(c->status_cache);
    free(c);
}
size_t ca_image_generator_fallback_chain_count(const ca_image_generator_fallback_chain_t *c) {
    return c ? c->count : 0;
}

static const char *fchain_id(void *self) { (void)self; return "fallback-chain"; }
static const char *fchain_label(void *self) {
    return ((ca_image_generator_fallback_chain_t *)self)->label_cache;
}
static bool fchain_configured(void *self) {
    ca_image_generator_fallback_chain_t *c = (ca_image_generator_fallback_chain_t *)self;
    for (size_t i = 0; i < c->count; ++i)
        if (ca_image_generator_is_configured(&c->chain[i])) return true;
    return false;   /* _chain.Any(g => g.IsConfigured) */
}
static const char *fchain_status(void *self) {
    ca_image_generator_fallback_chain_t *c = (ca_image_generator_fallback_chain_t *)self;
    free(c->status_cache);
    c->status_cache = NULL;
    if (!fchain_configured(self)) {
        c->status_cache = vc_strdup("No configured generator in chain.");
        return c->status_cache ? c->status_cache : "No configured generator in chain.";
    }
    /* "Ready · a → b" over the configured ids in order. */
    size_t need = strlen("Ready · ") + 1;
    for (size_t i = 0; i < c->count; ++i) {
        if (!ca_image_generator_is_configured(&c->chain[i])) continue;
        const char *id = ca_image_generator_id(&c->chain[i]);
        need += strlen(id) + strlen(" \xe2\x86\x92 ");
    }
    c->status_cache = (char *)malloc(need);
    if (!c->status_cache) return "Ready";
    strcpy(c->status_cache, "Ready \xc2\xb7 ");
    bool first = true;
    for (size_t i = 0; i < c->count; ++i) {
        if (!ca_image_generator_is_configured(&c->chain[i])) continue;
        if (!first) strcat(c->status_cache, " \xe2\x86\x92 ");
        strcat(c->status_cache, ca_image_generator_id(&c->chain[i]));
        first = false;
    }
    return c->status_cache;
}
static ca_image_artifact_t *fchain_generate(void *self,
                                            const ca_image_generation_request_t *req,
                                            size_t *out_count) {
    ca_image_generator_fallback_chain_t *c = (ca_image_generator_fallback_chain_t *)self;
    if (out_count) *out_count = 0;
    for (size_t i = 0; i < c->count; ++i) {
        if (!ca_image_generator_is_configured(&c->chain[i])) continue;  /* skip unconfigured */
        size_t n = 0;
        ca_image_artifact_t *r = ca_image_generator_generate(&c->chain[i], req, &n);
        if (n == (size_t)-1) {   /* hard error propagates */
            if (out_count) *out_count = (size_t)-1;
            return NULL;
        }
        if (n > 0) {             /* first non-empty wins */
            if (out_count) *out_count = n;
            return r;
        }
        ca_image_artifact_free_array(r, n);   /* empty — move on */
    }
    return NULL;   /* everyone failed -> empty */
}
ca_image_generator_t ca_image_generator_fallback_chain_as_iface(
    ca_image_generator_fallback_chain_t *c) {
    ca_image_generator_t i;
    i.self = c;
    i.generator_id = fchain_id;
    i.display_label = fchain_label;
    i.is_configured = fchain_configured;
    i.status_message = fchain_status;
    i.generate = fchain_generate;
    i.destroy = NULL;
    return i;
}
