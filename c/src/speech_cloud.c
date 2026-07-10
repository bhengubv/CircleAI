/*
 * speech_cloud.c — CircleAI.Speech.Cloud IVoiceIntentRouter (C11 port).
 *
 * Ports KeywordVoiceIntentRouter.cs + NullVoiceIntentRouter. The router tries
 * each intent's matcher against the trimmed transcript in order; the first hit
 * wins and its named captures become the match's Captures; if nothing matches
 * (or the transcript is empty/whitespace) the fallback intent is returned with
 * empty captures.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/speech_cloud.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>

static char *scl_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *scl_strdup_empty(const char *s) { return scl_strdup(s ? s : ""); }

/* ── VoiceIntentMatch ───────────────────────────────────────────────────── */

void ca_voice_intent_match_free(ca_voice_intent_match_t *m) {
    if (!m) return;
    free(m->intent_name);
    free(m->transcript);
    for (size_t i = 0; i < m->capture_count; ++i) {
        free(m->captures[i].key);
        free(m->captures[i].value);
    }
    free(m->captures);
    m->intent_name = m->transcript = NULL;
    m->captures = NULL;
    m->capture_count = 0;
}
const char *ca_voice_intent_match_capture(const ca_voice_intent_match_t *m,
                                          const char *key) {
    if (!m || !key) return NULL;
    for (size_t i = 0; i < m->capture_count; ++i)
        if (m->captures[i].key && strcmp(m->captures[i].key, key) == 0)
            return m->captures[i].value;
    return NULL;
}

/* ── capture accumulator ────────────────────────────────────────────────── */

struct ca_intent_captures {
    ca_intent_capture_t *items;
    size_t               count, cap;
};

int ca_intent_captures_add(ca_intent_captures_t *acc, const char *key,
                           const char *value) {
    if (!acc || !key) return -1;
    /* Skip empty/NULL values (mirrors C# !string.IsNullOrEmpty(g.Value)). */
    if (!value || value[0] == '\0') return 0;
    /* Ordinal dictionary assignment: last write wins for a duplicate key. */
    for (size_t i = 0; i < acc->count; ++i) {
        if (strcmp(acc->items[i].key, key) == 0) {
            char *nv = scl_strdup(value);
            if (!nv) return -1;
            free(acc->items[i].value);
            acc->items[i].value = nv;
            return 0;
        }
    }
    if (acc->count == acc->cap) {
        size_t nc = acc->cap ? acc->cap * 2 : 4;
        void *n = realloc(acc->items, nc * sizeof(*acc->items));
        if (!n) return -1;
        acc->items = (ca_intent_capture_t *)n;
        acc->cap = nc;
    }
    acc->items[acc->count].key = scl_strdup(key);
    acc->items[acc->count].value = scl_strdup(value);
    if (!acc->items[acc->count].key || !acc->items[acc->count].value) {
        free(acc->items[acc->count].key);
        free(acc->items[acc->count].value);
        return -1;
    }
    acc->count++;
    return 0;
}

/* ── built-in substring matcher ─────────────────────────────────────────── */

typedef struct {
    char *needle;        /* owned, lowered at build for CI compare source */
    char *capture_name;  /* owned, NULL == no capture */
} substr_matcher_t;

/* case-insensitive search; returns pointer to first occurrence in hay or NULL. */
static const char *ci_find(const char *hay, const char *needle) {
    if (!hay || !needle) return NULL;
    if (*needle == '\0') return hay;
    size_t nl = strlen(needle);
    for (const char *h = hay; *h; ++h) {
        size_t k = 0;
        while (k < nl && h[k] &&
               tolower((unsigned char)h[k]) == tolower((unsigned char)needle[k]))
            k++;
        if (k == nl) return h;
    }
    return NULL;
}
static bool substr_match(void *self, const char *transcript,
                         ca_intent_captures_t *captures) {
    substr_matcher_t *m = (substr_matcher_t *)self;
    const char *hit = ci_find(transcript, m->needle);
    if (!hit) return false;
    if (m->capture_name) {
        const char *tail = hit + strlen(m->needle);
        /* trim leading + trailing whitespace of the tail (Regex .Trim()). */
        while (*tail && isspace((unsigned char)*tail)) tail++;
        size_t tl = strlen(tail);
        while (tl > 0 && isspace((unsigned char)tail[tl - 1])) tl--;
        if (tl > 0) {
            char *val = (char *)malloc(tl + 1);
            if (val) {
                memcpy(val, tail, tl);
                val[tl] = '\0';
                ca_intent_captures_add(captures, m->capture_name, val);
                free(val);
            }
        }
    }
    return true;
}

/* ── router ─────────────────────────────────────────────────────────────── */

typedef struct {
    char               *name;    /* owned */
    ca_intent_matcher_t matcher; /* borrowed vtable OR built-in */
    substr_matcher_t   *builtin; /* owned when this intent uses the built-in */
} intent_entry_t;

struct ca_voice_intent_router {
    char           *fallback;  /* owned */
    intent_entry_t *intents;
    size_t          count, cap;
};

ca_voice_intent_router_t *ca_keyword_voice_intent_router_create(
    const char *fallback_intent_name) {
    ca_voice_intent_router_t *r = (ca_voice_intent_router_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    const char *fb = (fallback_intent_name && fallback_intent_name[0])
                         ? fallback_intent_name : "ask-ai";
    r->fallback = scl_strdup(fb);
    if (!r->fallback) { free(r); return NULL; }
    return r;
}
void ca_voice_intent_router_destroy(ca_voice_intent_router_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->count; ++i) {
        free(r->intents[i].name);
        if (r->intents[i].builtin) {
            free(r->intents[i].builtin->needle);
            free(r->intents[i].builtin->capture_name);
            free(r->intents[i].builtin);
        }
    }
    free(r->intents);
    free(r->fallback);
    free(r);
}

static intent_entry_t *router_reserve(ca_voice_intent_router_t *r) {
    if (r->count == r->cap) {
        size_t nc = r->cap ? r->cap * 2 : 4;
        void *n = realloc(r->intents, nc * sizeof(*r->intents));
        if (!n) return NULL;
        r->intents = (intent_entry_t *)n;
        r->cap = nc;
    }
    intent_entry_t *e = &r->intents[r->count];
    memset(e, 0, sizeof(*e));
    return e;
}

int ca_keyword_voice_intent_router_add(ca_voice_intent_router_t *r,
                                       const char *name,
                                       ca_intent_matcher_t matcher) {
    if (!r || !name) return -1;
    intent_entry_t *e = router_reserve(r);
    if (!e) return -1;
    e->name = scl_strdup(name);
    if (!e->name) return -1;
    e->matcher = matcher;
    e->builtin = NULL;
    r->count++;
    return 0;
}

int ca_keyword_voice_intent_router_add_substring(ca_voice_intent_router_t *r,
                                                 const char *name,
                                                 const char *needle,
                                                 const char *capture_name) {
    if (!r || !name || !needle) return -1;
    intent_entry_t *e = router_reserve(r);
    if (!e) return -1;
    e->name = scl_strdup(name);
    substr_matcher_t *bm = (substr_matcher_t *)calloc(1, sizeof(*bm));
    if (!e->name || !bm) { free(e->name); free(bm); return -1; }
    bm->needle = scl_strdup(needle);
    bm->capture_name = capture_name ? scl_strdup(capture_name) : NULL;
    if (!bm->needle || (capture_name && !bm->capture_name)) {
        free(bm->needle); free(bm->capture_name); free(bm); free(e->name);
        return -1;
    }
    e->builtin = bm;
    e->matcher.self = bm;
    e->matcher.match = substr_match;
    r->count++;
    return 0;
}

const char *ca_voice_intent_router_backend_id(const ca_voice_intent_router_t *r) {
    (void)r;
    return "keyword";
}

/* Build the fallback match into *out. */
static int emit_fallback(const char *fallback, const char *transcript,
                         ca_voice_intent_match_t *out) {
    memset(out, 0, sizeof(*out));
    out->intent_name = scl_strdup(fallback);
    out->transcript = scl_strdup_empty(transcript);
    out->captures = NULL;
    out->capture_count = 0;
    if (!out->intent_name || !out->transcript) {
        ca_voice_intent_match_free(out);
        return -1;
    }
    return 0;
}

int ca_voice_intent_router_route(ca_voice_intent_router_t *r,
                                 const char *transcript,
                                 ca_voice_intent_match_t *out) {
    if (!r || !out) return -1;

    /* Trim leading + trailing whitespace (transcript?.Trim() ?? ""). */
    const char *s = transcript ? transcript : "";
    size_t a = 0, b = strlen(s);
    while (a < b && isspace((unsigned char)s[a])) a++;
    while (b > a && isspace((unsigned char)s[b - 1])) b--;
    size_t tlen = b - a;

    if (tlen == 0) {
        /* Empty transcript -> fallback, empty transcript string. */
        return emit_fallback(r->fallback, "", out);
    }

    char *text = (char *)malloc(tlen + 1);
    if (!text) return -1;
    memcpy(text, s + a, tlen);
    text[tlen] = '\0';

    for (size_t i = 0; i < r->count; ++i) {
        intent_entry_t *e = &r->intents[i];
        if (!e->matcher.match) continue;
        ca_intent_captures_t acc;
        memset(&acc, 0, sizeof(acc));
        bool hit = e->matcher.match(e->matcher.self, text, &acc);
        if (!hit) {
            for (size_t k = 0; k < acc.count; ++k) {
                free(acc.items[k].key);
                free(acc.items[k].value);
            }
            free(acc.items);
            continue;
        }
        memset(out, 0, sizeof(*out));
        out->intent_name = scl_strdup(e->name);
        out->transcript = text;   /* transfer ownership */
        out->captures = acc.items;
        out->capture_count = acc.count;
        if (!out->intent_name) {
            ca_voice_intent_match_free(out);
            return -1;
        }
        return 0;
    }

    /* Nothing matched -> fallback with the trimmed transcript. */
    int rc = emit_fallback(r->fallback, text, out);
    free(text);
    return rc;
}

/* ── NullVoiceIntentRouter ──────────────────────────────────────────────── */

struct ca_null_voice_intent_router { int _; };

ca_null_voice_intent_router_t *ca_null_voice_intent_router_create(void) {
    return (ca_null_voice_intent_router_t *)calloc(1, sizeof(ca_null_voice_intent_router_t));
}
void ca_null_voice_intent_router_destroy(ca_null_voice_intent_router_t *r) { free(r); }
const char *ca_null_voice_intent_router_backend_id(const ca_null_voice_intent_router_t *r) {
    (void)r;
    return "null";
}
int ca_null_voice_intent_router_route(ca_null_voice_intent_router_t *r,
                                      const char *transcript,
                                      ca_voice_intent_match_t *out) {
    (void)r;
    if (!out) return -1;
    memset(out, 0, sizeof(*out));
    out->intent_name = scl_strdup("ask-ai");
    out->transcript = scl_strdup_empty(transcript);
    out->captures = NULL;
    out->capture_count = 0;
    if (!out->intent_name || !out->transcript) {
        ca_voice_intent_match_free(out);
        return -1;
    }
    return 0;
}
