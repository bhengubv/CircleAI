/*
 * video.c — CircleAI.Video (C11 port).
 *
 * Ports NullVideoGenerator, NullStyleScript, InMemoryStyleReference (+ a
 * NullStyleReference), the IVideoGenerator / IStyleScript seam dispatchers, and
 * all the record free/copy helpers. The real on-device generators are injected
 * behind the IVideoGenerator vtable.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/video.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>

static char *vd_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *vd_strdup_empty(const char *s) { return vd_strdup(s ? s : ""); }

/* OrdinalIgnoreCase equality. */
static bool ci_eq(const char *a, const char *b) {
    if (!a || !b) return a == b;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return false;
        a++; b++;
    }
    return *a == *b;
}

/* ===========================================================================
 * VideoResolution presets
 * =========================================================================== */

ca_video_resolution_t ca_video_resolution_p480(void)  { ca_video_resolution_t r = { 720, 480 };   return r; }
ca_video_resolution_t ca_video_resolution_p720(void)  { ca_video_resolution_t r = { 1280, 720 };  return r; }
ca_video_resolution_t ca_video_resolution_p1080(void) { ca_video_resolution_t r = { 1920, 1080 }; return r; }

/* ===========================================================================
 * StyleReferenceFrame helpers (internal deep copy/free)
 * =========================================================================== */

static void frame_free(ca_style_reference_frame_t *f) {
    if (!f) return;
    free(f->image_bytes);
    free(f->mime_type);
    free(f->caption);
    f->image_bytes = NULL;
    f->mime_type = NULL;
    f->caption = NULL;
    f->image_len = 0;
}
static int frame_copy(ca_style_reference_frame_t *dst, const ca_style_reference_frame_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->mime_type = vd_strdup_empty(src->mime_type);
    dst->caption = src->caption ? vd_strdup(src->caption) : NULL;
    if (!dst->mime_type || (src->caption && !dst->caption)) { frame_free(dst); return -1; }
    if (src->image_bytes && src->image_len) {
        dst->image_bytes = (uint8_t *)malloc(src->image_len);
        if (!dst->image_bytes) { frame_free(dst); return -1; }
        memcpy(dst->image_bytes, src->image_bytes, src->image_len);
        dst->image_len = src->image_len;
    }
    return 0;
}

/* ===========================================================================
 * StyleReference
 * =========================================================================== */

void ca_style_reference_free(ca_style_reference_t *s) {
    if (!s) return;
    free(s->id);
    free(s->display_name);
    free(s->short_description);
    free(s->attribution.source);
    free(s->attribution.license);
    free(s->attribution.url);
    free(s->voice_persona_id);
    for (size_t i = 0; i < s->frame_count; ++i) frame_free(&s->frames[i]);
    free(s->frames);
    memset(s, 0, sizeof(*s));
}
void ca_style_reference_free_array(ca_style_reference_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_style_reference_free(&arr[i]);
    free(arr);
}
int ca_style_reference_copy(ca_style_reference_t *dst, const ca_style_reference_t *src) {
    if (!dst || !src) return -1;
    memset(dst, 0, sizeof(*dst));
    dst->id = vd_strdup_empty(src->id);
    dst->display_name = vd_strdup_empty(src->display_name);
    dst->short_description = vd_strdup_empty(src->short_description);
    dst->attribution.source = vd_strdup_empty(src->attribution.source);
    dst->attribution.license = vd_strdup_empty(src->attribution.license);
    dst->attribution.url = src->attribution.url ? vd_strdup(src->attribution.url) : NULL;
    dst->voice_persona_id = src->voice_persona_id ? vd_strdup(src->voice_persona_id) : NULL;
    if (!dst->id || !dst->display_name || !dst->short_description ||
        !dst->attribution.source || !dst->attribution.license ||
        (src->attribution.url && !dst->attribution.url) ||
        (src->voice_persona_id && !dst->voice_persona_id)) {
        ca_style_reference_free(dst);
        return -1;
    }
    if (src->frames && src->frame_count) {
        dst->frames = (ca_style_reference_frame_t *)calloc(src->frame_count, sizeof(*dst->frames));
        if (!dst->frames) { ca_style_reference_free(dst); return -1; }
        for (size_t i = 0; i < src->frame_count; ++i) {
            if (frame_copy(&dst->frames[i], &src->frames[i]) != 0) {
                /* free the ones done so far via free_array semantics */
                dst->frame_count = i;
                ca_style_reference_free(dst);
                return -1;
            }
        }
        dst->frame_count = src->frame_count;
    }
    return 0;
}

/* ===========================================================================
 * AudioTrack
 * =========================================================================== */

void ca_audio_track_free(ca_audio_track_t *t) {
    if (!t) return;
    free(t->audio_pcm16_mono);
    t->audio_pcm16_mono = NULL;
    t->audio_len = 0;
}

/* ===========================================================================
 * VideoGenerationRequest
 * =========================================================================== */

void ca_video_generation_request_init(ca_video_generation_request_t *req,
                                      const char *prompt,
                                      int64_t duration_ms,
                                      ca_video_resolution_t resolution) {
    if (!req) return;
    memset(req, 0, sizeof(*req));
    req->prompt = prompt;
    req->duration_ms = duration_ms;
    req->resolution = resolution;
    req->frame_rate = 24;
    req->has_style_id = false;
    req->style_id = NULL;
    req->reference_image = NULL;
    req->audio_track = NULL;
    req->has_seed = false;
    req->seed = 0;
}

/* ===========================================================================
 * VideoGenerationResult
 * =========================================================================== */

void ca_video_generation_result_free(ca_video_generation_result_t *r) {
    if (!r) return;
    free(r->video_bytes);
    free(r->mime_type);
    free(r->backend_id);
    r->video_bytes = NULL;
    r->mime_type = NULL;
    r->backend_id = NULL;
    r->video_len = 0;
}

/* ===========================================================================
 * StyleScriptResult
 * =========================================================================== */

void ca_style_script_result_free(ca_style_script_result_t *r) {
    if (!r) return;
    free(r->rewritten_text);
    free(r->style);
    free(r->voice_persona_id);
    r->rewritten_text = NULL;
    r->style = NULL;
    r->voice_persona_id = NULL;
}

/* ===========================================================================
 * IVideoGenerator
 * =========================================================================== */

const char *ca_video_generator_backend_id(const ca_video_generator_t *g) {
    if (!g || !g->backend_id) return "null";
    return g->backend_id(g->self);
}
int ca_video_generator_generate(const ca_video_generator_t *g,
                                const ca_video_generation_request_t *req,
                                ca_video_generation_result_t *out) {
    if (!g || !g->generate || !out) return -1;
    return g->generate(g->self, req, out);
}

static const char *nullvg_backend(void *self) { (void)self; return "null"; }
static int nullvg_generate(void *self, const ca_video_generation_request_t *req,
                           ca_video_generation_result_t *out) {
    (void)self;
    memset(out, 0, sizeof(*out));
    out->video_bytes = NULL;                        /* ReadOnlyMemory<byte>.Empty */
    out->video_len = 0;
    out->mime_type = vd_strdup("video/mp4");
    out->duration_ms = 0;                           /* TimeSpan.Zero */
    out->frame_count = 0;
    out->resolution = req ? req->resolution : ca_video_resolution_p1080();
    out->backend_id = vd_strdup("null");
    if (!out->mime_type || !out->backend_id) {
        ca_video_generation_result_free(out);
        return -1;
    }
    return 0;
}
ca_video_generator_t ca_null_video_generator(void) {
    ca_video_generator_t g;
    g.self = NULL;
    g.backend_id = nullvg_backend;
    g.generate = nullvg_generate;
    return g;
}

/* ===========================================================================
 * IStyleScript
 * =========================================================================== */

const char *ca_style_script_backend_id(const ca_style_script_t *s) {
    if (!s || !s->backend_id) return "null";
    return s->backend_id(s->self);
}
int ca_style_script_rewrite(const ca_style_script_t *s,
                            const ca_style_script_request_t *req,
                            ca_style_script_result_t *out) {
    if (!s || !s->rewrite || !out) return -1;
    return s->rewrite(s->self, req, out);
}

static const char *nullss_backend(void *self) { (void)self; return "null"; }
static int nullss_rewrite(void *self, const ca_style_script_request_t *req,
                          ca_style_script_result_t *out) {
    (void)self;
    memset(out, 0, sizeof(*out));
    out->rewritten_text = vd_strdup_empty(req ? req->source_message : "");   /* echo */
    out->style = vd_strdup_empty(req ? req->style : "");                     /* passthrough */
    out->voice_persona_id = NULL;
    out->estimated_spoken_duration_ms = 0;   /* TimeSpan.Zero */
    if (!out->rewritten_text || !out->style) {
        ca_style_script_result_free(out);
        return -1;
    }
    return 0;
}
ca_style_script_t ca_null_style_script(void) {
    ca_style_script_t s;
    s.self = NULL;
    s.backend_id = nullss_backend;
    s.rewrite = nullss_rewrite;
    return s;
}

/* ===========================================================================
 * InMemoryStyleReference
 * =========================================================================== */

struct ca_style_reference_store {
    ca_style_reference_t *items;   /* insertion order */
    size_t                count, cap;
};

ca_style_reference_store_t *ca_inmemory_style_reference_create(void) {
    return (ca_style_reference_store_t *)calloc(1, sizeof(ca_style_reference_store_t));
}
void ca_inmemory_style_reference_destroy(ca_style_reference_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) ca_style_reference_free(&store->items[i]);
    free(store->items);
    free(store);
}
const char *ca_inmemory_style_reference_backend_id(const ca_style_reference_store_t *store) {
    (void)store;
    return "in-memory";
}

int ca_inmemory_style_reference_register(ca_style_reference_store_t *store,
                                         const ca_style_reference_t *style) {
    if (!store || !style) return -1;
    /* upsert by Id (OrdinalIgnoreCase, last-write-wins). */
    ca_style_reference_t copy;
    if (ca_style_reference_copy(&copy, style) != 0) return -1;
    for (size_t i = 0; i < store->count; ++i) {
        if (ci_eq(store->items[i].id, copy.id)) {
            ca_style_reference_free(&store->items[i]);
            store->items[i] = copy;   /* move */
            return 0;
        }
    }
    if (store->count == store->cap) {
        size_t nc = store->cap ? store->cap * 2 : 4;
        void *n = realloc(store->items, nc * sizeof(*store->items));
        if (!n) { ca_style_reference_free(&copy); return -1; }
        store->items = (ca_style_reference_t *)n;
        store->cap = nc;
    }
    store->items[store->count++] = copy;   /* move */
    return 0;
}

bool ca_inmemory_style_reference_get(const ca_style_reference_store_t *store,
                                     const char *style_id,
                                     ca_style_reference_t *out) {
    if (!store || !out) return false;
    for (size_t i = 0; i < store->count; ++i) {
        if (ci_eq(store->items[i].id, style_id)) {
            if (ca_style_reference_copy(out, &store->items[i]) != 0) return false;
            return true;
        }
    }
    return false;
}

ca_style_reference_t *ca_inmemory_style_reference_list(
    const ca_style_reference_store_t *store, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store || store->count == 0) return NULL;
    ca_style_reference_t *arr =
        (ca_style_reference_t *)calloc(store->count, sizeof(*arr));
    if (!arr) { if (out_count) *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < store->count; ++i) {
        if (ca_style_reference_copy(&arr[i], &store->items[i]) != 0) {
            ca_style_reference_free_array(arr, i);
            if (out_count) *out_count = (size_t)-1;
            return NULL;
        }
    }
    if (out_count) *out_count = store->count;
    return arr;
}

size_t ca_inmemory_style_reference_count(const ca_style_reference_store_t *store) {
    return store ? store->count : 0;
}

/* ===========================================================================
 * NullStyleReference
 * =========================================================================== */

struct ca_null_style_reference { int _; };

ca_null_style_reference_t *ca_null_style_reference_create(void) {
    return (ca_null_style_reference_t *)calloc(1, sizeof(ca_null_style_reference_t));
}
void ca_null_style_reference_destroy(ca_null_style_reference_t *s) { free(s); }
const char *ca_null_style_reference_backend_id(const ca_null_style_reference_t *s) {
    (void)s;
    return "null";
}
int ca_null_style_reference_register(ca_null_style_reference_t *s,
                                     const ca_style_reference_t *style) {
    (void)s; (void)style;
    return 0;   /* no-op */
}
bool ca_null_style_reference_get(const ca_null_style_reference_t *s,
                                 const char *style_id, ca_style_reference_t *out) {
    (void)s; (void)style_id;
    if (out) memset(out, 0, sizeof(*out));
    return false;   /* always misses */
}
ca_style_reference_t *ca_null_style_reference_list(const ca_null_style_reference_t *s,
                                                   size_t *out_count) {
    (void)s;
    if (out_count) *out_count = 0;
    return NULL;    /* always empty */
}
