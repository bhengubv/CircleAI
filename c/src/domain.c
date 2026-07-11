/*
 * domain.c — CircleAI.Domain (C11 port).
 *
 * Deterministic in-memory backings for every domain-specialist seam. The Food
 * fallback embedding uses a deterministic hash over the lowercased name (the C#
 * code uses String.GetHashCode, which is not portable; the fallback vector's
 * contract is "deterministic per name"). The financial agent decomposes the
 * question, groups by source, and summarises each cluster. Pure C11 + libc
 * (+ libm for the LoRA loss curve). No pthreads.
 */

#include "circle_ai/domain.h"
#include "board_common.h"
#include <math.h>
#include <stdio.h>

/* ── shared kv helpers ──────────────────────────────────────────────────── */

static void dkv_free(ca_domain_kv_t *kv, size_t n) {
    if (!kv) return;
    for (size_t i = 0; i < n; ++i) { free(kv[i].key); free(kv[i].value); }
    free(kv);
}
static bool dkv_copy(ca_domain_kv_t **out, const ca_domain_kv_t *src, size_t n) {
    *out = NULL;
    if (n == 0) return true;
    ca_domain_kv_t *v = (ca_domain_kv_t *)calloc(n, sizeof(*v));
    if (!v) return false;
    for (size_t i = 0; i < n; ++i) {
        v[i].key   = cab_strdup_empty(src ? src[i].key : NULL);
        v[i].value = cab_strdup_empty(src ? src[i].value : NULL);
        if (!v[i].key || !v[i].value) { dkv_free(v, i + 1); return false; }
    }
    *out = v;
    return true;
}

/* ═══ Ingredient ═════════════════════════════════════════════════════════ */

void ca_ingredient_free(ca_ingredient_t *i) {
    if (!i) return;
    free(i->name); free(i->canonical); free(i->quantity);
    i->name = i->canonical = i->quantity = NULL;
}
void ca_ingredient_free_array(ca_ingredient_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_ingredient_free(&arr[i]);
    free(arr);
}
static bool ingredient_copy(ca_ingredient_t *dst, const ca_ingredient_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->name = cab_strdup_empty(src->name);
    dst->canonical = src->canonical ? cab_strdup(src->canonical) : NULL;
    dst->quantity = src->quantity ? cab_strdup(src->quantity) : NULL;
    if (!dst->name || (src->canonical && !dst->canonical) ||
        (src->quantity && !dst->quantity)) { ca_ingredient_free(dst); return false; }
    return true;
}

/* ── InMemoryFoodEmbeddings ─────────────────────────────────────────────── */

typedef struct { char *name; float *vec; size_t len; } food_embed_t;
typedef struct { char *name; ca_ingredient_t *subs; size_t count, cap; } food_sub_t;

struct ca_food_embeddings {
    food_embed_t *embeds; size_t embed_count, embed_cap;
    food_sub_t   *subs;   size_t sub_count, sub_cap;
};

ca_food_embeddings_t *ca_food_embeddings_create(void) {
    return (ca_food_embeddings_t *)calloc(1, sizeof(ca_food_embeddings_t));
}
void ca_food_embeddings_destroy(ca_food_embeddings_t *f) {
    if (!f) return;
    for (size_t i = 0; i < f->embed_count; ++i) { free(f->embeds[i].name); free(f->embeds[i].vec); }
    free(f->embeds);
    for (size_t i = 0; i < f->sub_count; ++i) {
        free(f->subs[i].name);
        ca_ingredient_free_array(f->subs[i].subs, f->subs[i].count);
    }
    free(f->subs);
    free(f);
}
const char *ca_food_embeddings_backend_id(const ca_food_embeddings_t *f) {
    (void)f; return "in-memory";
}

int ca_food_embeddings_register_embedding(ca_food_embeddings_t *f, const char *name,
                                          const float *vec, size_t len) {
    if (!f || cab_is_ws(name) || (!vec && len > 0)) return -1;
    float *copy = NULL;
    if (len > 0) { copy = (float *)malloc(len * sizeof(float)); if (!copy) return -1; memcpy(copy, vec, len * sizeof(float)); }
    for (size_t i = 0; i < f->embed_count; ++i) {
        if (cab_ci_eq(f->embeds[i].name, name)) {
            free(f->embeds[i].vec);
            f->embeds[i].vec = copy; f->embeds[i].len = len;
            return 0;
        }
    }
    if (f->embed_count == f->embed_cap) {
        size_t nc = f->embed_cap ? f->embed_cap * 2 : 4;
        void *n = realloc(f->embeds, nc * sizeof(food_embed_t));
        if (!n) { free(copy); return -1; }
        f->embeds = (food_embed_t *)n; f->embed_cap = nc;
    }
    char *nm = cab_strdup_empty(name);
    if (!nm) { free(copy); return -1; }
    f->embeds[f->embed_count].name = nm;
    f->embeds[f->embed_count].vec = copy;
    f->embeds[f->embed_count].len = len;
    f->embed_count++;
    return 0;
}

int ca_food_embeddings_register_substitute(ca_food_embeddings_t *f, const char *name,
                                           const ca_ingredient_t *alt) {
    if (!f || cab_is_ws(name) || !alt) return -1;
    food_sub_t *bucket = NULL;
    for (size_t i = 0; i < f->sub_count; ++i)
        if (cab_ci_eq(f->subs[i].name, name)) { bucket = &f->subs[i]; break; }
    if (!bucket) {
        if (f->sub_count == f->sub_cap) {
            size_t nc = f->sub_cap ? f->sub_cap * 2 : 4;
            void *n = realloc(f->subs, nc * sizeof(food_sub_t));
            if (!n) return -1;
            f->subs = (food_sub_t *)n; f->sub_cap = nc;
        }
        char *nm = cab_strdup_empty(name);
        if (!nm) return -1;
        bucket = &f->subs[f->sub_count];
        memset(bucket, 0, sizeof(*bucket));
        bucket->name = nm;
        f->sub_count++;
    }
    ca_ingredient_t copy;
    if (!ingredient_copy(&copy, alt)) return -1;
    if (bucket->count == bucket->cap) {
        size_t nc = bucket->cap ? bucket->cap * 2 : 4;
        void *n = realloc(bucket->subs, nc * sizeof(ca_ingredient_t));
        if (!n) { ca_ingredient_free(&copy); return -1; }
        bucket->subs = (ca_ingredient_t *)n; bucket->cap = nc;
    }
    bucket->subs[bucket->count++] = copy;
    return 0;
}

/* Deterministic FNV-1a over the lowercased name (portable stand-in for
 * String.GetHashCode used by the fallback vector). */
static unsigned fnv1a_ci(const char *s) {
    unsigned h = 2166136261u;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p) {
        h ^= (unsigned char)tolower(*p);
        h *= 16777619u;
    }
    return h;
}

float *ca_food_embeddings_embed(const ca_food_embeddings_t *f,
                                const ca_ingredient_t *ingredient, size_t *out_len) {
    if (!out_len) return NULL;
    if (!f || !ingredient || !ingredient->name) { *out_len = (size_t)-1; return NULL; }
    for (size_t i = 0; i < f->embed_count; ++i) {
        if (cab_ci_eq(f->embeds[i].name, ingredient->name)) {
            size_t len = f->embeds[i].len;
            float *v = (float *)malloc((len ? len : 1) * sizeof(float));
            if (!v) { *out_len = (size_t)-1; return NULL; }
            if (len) memcpy(v, f->embeds[i].vec, len * sizeof(float));
            *out_len = len;
            return v;
        }
    }
    /* fallback: 8-dim hash vector, each dim = ((h >> (k*4)) & 0xF) / 15 */
    unsigned h = fnv1a_ci(ingredient->name);
    float *v = (float *)malloc(8 * sizeof(float));
    if (!v) { *out_len = (size_t)-1; return NULL; }
    for (int k = 0; k < 8; ++k) v[k] = (float)((h >> (k * 4)) & 0xF) / 15.0f;
    *out_len = 8;
    return v;
}

ca_ingredient_t *ca_food_embeddings_substitutes(const ca_food_embeddings_t *f,
                                                const ca_ingredient_t *ingredient,
                                                int top_k, size_t *out_count) {
    if (!out_count) return NULL;
    if (!f || !ingredient || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    food_sub_t *bucket = NULL;
    for (size_t i = 0; i < f->sub_count; ++i)
        if (cab_ci_eq(f->subs[i].name, ingredient->name)) { bucket = &f->subs[i]; break; }
    if (!bucket || bucket->count == 0) { *out_count = 0; return NULL; }
    size_t take = (size_t)top_k < bucket->count ? (size_t)top_k : bucket->count;
    ca_ingredient_t *out = (ca_ingredient_t *)calloc(take, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < take; ++i) {
        if (!ingredient_copy(&out[i], &bucket->subs[i])) {
            ca_ingredient_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = take;
    return out;
}

const char *ca_domain_null_food_embeddings_backend_id(void) { return "null"; }

/* ═══ Finance ════════════════════════════════════════════════════════════ */

void ca_finance_snippet_free(ca_finance_snippet_t *s) {
    if (!s) return;
    free(s->text); free(s->source);
    s->text = s->source = NULL;
}
void ca_finance_snippet_free_array(ca_finance_snippet_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_finance_snippet_free(&arr[i]);
    free(arr);
}
static bool snippet_copy(ca_finance_snippet_t *dst, const ca_finance_snippet_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->score = src->score;
    dst->text = cab_strdup_empty(src->text);
    dst->source = cab_strdup_empty(src->source);
    if (!dst->text || !dst->source) { ca_finance_snippet_free(dst); return false; }
    return true;
}

struct ca_finance_retrieval {
    ca_finance_snippet_t *items; size_t count, cap;
};

ca_finance_retrieval_t *ca_finance_retrieval_create(void) {
    return (ca_finance_retrieval_t *)calloc(1, sizeof(ca_finance_retrieval_t));
}
void ca_finance_retrieval_destroy(ca_finance_retrieval_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->count; ++i) ca_finance_snippet_free(&r->items[i]);
    free(r->items);
    free(r);
}
const char *ca_finance_retrieval_backend_id(const ca_finance_retrieval_t *r) {
    (void)r; return "in-memory";
}
int ca_finance_retrieval_add(ca_finance_retrieval_t *r, const ca_finance_snippet_t *s) {
    if (!r || !s) return -1;
    ca_finance_snippet_t copy;
    if (!snippet_copy(&copy, s)) return -1;
    if (r->count == r->cap) {
        size_t nc = r->cap ? r->cap * 2 : 4;
        void *n = realloc(r->items, nc * sizeof(*r->items));
        if (!n) { ca_finance_snippet_free(&copy); return -1; }
        r->items = (ca_finance_snippet_t *)n; r->cap = nc;
    }
    r->items[r->count++] = copy;
    return 0;
}

/* Return sorted indices of snippets whose Text Contains query (CI), by Score
 * desc (stable), truncated to top_k. *out_n set. Caller frees the returned
 * index array. */
static size_t *finance_match_indices(const ca_finance_retrieval_t *r,
                                     const char *query, int top_k, size_t *out_n) {
    size_t *idx = (size_t *)malloc((r->count ? r->count : 1) * sizeof(size_t));
    if (!idx) return NULL;
    size_t n = 0;
    for (size_t i = 0; i < r->count; ++i)
        if (cab_ci_contains(r->items[i].text, query)) idx[n++] = i;
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        float ks = r->items[key].score;
        size_t j = i;
        while (j > 0 && r->items[idx[j - 1]].score < ks) { idx[j] = idx[j - 1]; j--; }
        idx[j] = key;
    }
    if ((size_t)top_k < n) n = (size_t)top_k;
    *out_n = n;
    return idx;
}

ca_finance_snippet_t *ca_finance_retrieval_retrieve(const ca_finance_retrieval_t *r,
                                                    const char *query, int top_k,
                                                    size_t *out_count) {
    if (!out_count) return NULL;
    if (!r || !query || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    size_t *idx = finance_match_indices(r, query, top_k, &n);
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_finance_snippet_t *out = (ca_finance_snippet_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!snippet_copy(&out[i], &r->items[idx[i]])) {
            ca_finance_snippet_free_array(out, i);
            free(idx); *out_count = (size_t)-1; return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

const char *ca_domain_null_finance_retrieval_backend_id(void) { return "null"; }

void ca_finance_finding_free(ca_finance_finding_t *f) {
    if (!f) return;
    free(f->subject); free(f->summary);
    cab_strv_free(f->citations, f->citation_count);
    memset(f, 0, sizeof(*f));
}
void ca_finance_finding_free_array(ca_finance_finding_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_finance_finding_free(&arr[i]);
    free(arr);
}

const char *ca_financial_agent_backend_id(void) { return "multi-pass"; }
const char *ca_domain_null_financial_agent_backend_id(void) { return "null"; }

static char *trim_dup_dom(const char *s) {
    while (*s == ' ' || *s == '\t' || *s == '\n' || *s == '\r') s++;
    size_t n = strlen(s);
    while (n > 0 && (s[n - 1] == ' ' || s[n - 1] == '\t' ||
                     s[n - 1] == '\n' || s[n - 1] == '\r')) n--;
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    memcpy(out, s, n); out[n] = '\0';
    return out;
}

/* Decompose a question into distinct sub-questions (mirrors C# Decompose). */
static char **decompose(const char *question, size_t *out_n) {
    char **subs = NULL; size_t n = 0, cap = 0;
    #define PUSH_SUB(str) do { \
        char *v = (str); if (!v) { cab_strv_free(subs, n); return NULL; } \
        bool dup = false; for (size_t _i = 0; _i < n; ++_i) if (cab_ord_eq(subs[_i], v)) { dup = true; break; } \
        if (dup) { free(v); } else { \
            if (n == cap) { size_t nc = cap ? cap * 2 : 4; char **nv = realloc(subs, nc * sizeof(char*)); if (!nv) { free(v); cab_strv_free(subs, n); return NULL; } subs = nv; cap = nc; } \
            subs[n++] = v; } \
    } while (0)

    PUSH_SUB(cab_strdup_empty(question));
    /* contains " and " (CI) -> split, add trimmed parts len>6 */
    if (cab_ci_contains(question, " and ")) {
        const char *p = question;
        while (*p) {
            const char *hit = NULL;
            /* find CI " and " */
            for (const char *q = p; *q; ++q) {
                if (cab_ci_cmp_prefix(q, " and ")) { hit = q; break; }
            }
            const char *seg_end = hit ? hit : (p + strlen(p));
            size_t seglen = (size_t)(seg_end - p);
            char *seg = (char *)malloc(seglen + 1);
            if (!seg) { cab_strv_free(subs, n); return NULL; }
            memcpy(seg, p, seglen); seg[seglen] = '\0';
            char *tr = trim_dup_dom(seg);
            free(seg);
            if (!tr) { cab_strv_free(subs, n); return NULL; }
            if (strlen(tr) > 6) PUSH_SUB(tr); else free(tr);
            if (!hit) break;
            p = hit + 5; /* strlen(" and ") */
        }
    }
    /* length > 60 -> add first comma-segment trimmed */
    if (strlen(question) > 60) {
        const char *comma = strchr(question, ',');
        size_t len = comma ? (size_t)(comma - question) : strlen(question);
        char *seg = (char *)malloc(len + 1);
        if (!seg) { cab_strv_free(subs, n); return NULL; }
        memcpy(seg, question, len); seg[len] = '\0';
        char *tr = trim_dup_dom(seg);
        free(seg);
        if (!tr) { cab_strv_free(subs, n); return NULL; }
        PUSH_SUB(tr);
    }
    #undef PUSH_SUB
    *out_n = n;
    return subs;
}

ca_finance_finding_t *ca_financial_agent_research(const ca_finance_retrieval_t *retr,
                                                  const char *question,
                                                  size_t *out_count) {
    if (!out_count) return NULL;
    if (!retr || !question) { *out_count = (size_t)-1; return NULL; }

    size_t sub_n = 0;
    char **subs = decompose(question, &sub_n);
    if (!subs) { *out_count = (size_t)-1; return NULL; }

    ca_finance_finding_t *findings = NULL; size_t fn = 0, fcap = 0;
    bool ok = true;

    for (size_t s = 0; ok && s < sub_n; ++s) {
        size_t hit_n = 0;
        size_t *idx = finance_match_indices(retr, subs[s], 5, &hit_n);
        if (!idx) { ok = false; break; }
        if (hit_n == 0) { free(idx); continue; }
        /* group by source, preserving first-seen order */
        for (size_t a = 0; ok && a < hit_n; ++a) {
            const char *src = retr->items[idx[a]].source;
            /* skip if this source already processed for this sub */
            bool seen = false;
            for (size_t b = 0; b < a; ++b)
                if (cab_ord_eq(retr->items[idx[b]].source, src)) { seen = true; break; }
            if (seen) continue;
            /* summary = top-3 texts of this source (already Score-desc within idx),
             * joined by " | ". */
            char *summary = cab_strdup_empty("");
            if (!summary) { ok = false; break; }
            int taken = 0;
            for (size_t b = 0; b < hit_n && taken < 3; ++b) {
                if (!cab_ord_eq(retr->items[idx[b]].source, src)) continue;
                const char *txt = retr->items[idx[b]].text;
                size_t need = strlen(summary) + (taken ? 3 : 0) + strlen(txt) + 1;
                char *ns = (char *)malloc(need);
                if (!ns) { free(summary); ok = false; break; }
                if (taken) snprintf(ns, need, "%s | %s", summary, txt);
                else snprintf(ns, need, "%s", txt);
                free(summary); summary = ns; taken++;
            }
            if (!ok) break;
            /* append finding */
            if (fn == fcap) {
                size_t nc = fcap ? fcap * 2 : 4;
                void *n = realloc(findings, nc * sizeof(ca_finance_finding_t));
                if (!n) { free(summary); ok = false; break; }
                findings = (ca_finance_finding_t *)n; fcap = nc;
            }
            ca_finance_finding_t *fd = &findings[fn];
            memset(fd, 0, sizeof(*fd));
            fd->subject = cab_strdup_empty(subs[s]);
            fd->summary = summary; /* transfer */
            fd->citations = (char **)calloc(1, sizeof(char *));
            if (!fd->subject || !fd->citations) { ca_finance_finding_free(fd); ok = false; break; }
            fd->citations[0] = cab_strdup_empty(src);
            if (!fd->citations[0]) { ca_finance_finding_free(fd); ok = false; break; }
            fd->citation_count = 1;
            fn++;
        }
        free(idx);
    }

    cab_strv_free(subs, sub_n);
    if (!ok) { ca_finance_finding_free_array(findings, fn); *out_count = (size_t)-1; return NULL; }
    if (fn == 0) { free(findings); *out_count = 0; return NULL; }
    *out_count = fn;
    return findings;
}

/* ═══ Presentations ══════════════════════════════════════════════════════ */

void ca_generated_presentation_free(ca_generated_presentation_t *p) {
    if (!p) return;
    if (p->slides) {
        for (size_t i = 0; i < p->slide_count; ++i) {
            free(p->slides[i].title);
            free(p->slides[i].body);
            cab_strv_free(p->slides[i].bullets, p->slides[i].bullet_count);
        }
        free(p->slides);
    }
    free(p->theme);
    free(p->format);
    memset(p, 0, sizeof(*p));
}

const char *ca_presentation_generator_backend_id(void) { return "template"; }
const char *ca_domain_null_presentation_generator_backend_id(void) { return "null"; }

static bool slide_set(ca_slide_outline_t *s, const char *title, const char *body,
                      const char *const *bullets, size_t bn) {
    memset(s, 0, sizeof(*s));
    s->title = cab_strdup_empty(title);
    s->body = cab_strdup_empty(body);
    if (!s->title || !s->body) return false;
    if (bn > 0) {
        if (!cab_strv_copy(&s->bullets, (char *const *)bullets, bn)) return false;
        s->bullet_count = bn;
    }
    return true;
}

bool ca_presentation_generate(const char *topic, int target_slide_count,
                              const char *theme, ca_generated_presentation_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out || cab_is_ws(topic) || target_slide_count <= 0) return false;

    out->theme = cab_strdup_empty(theme ? theme : "default");
    out->format = cab_strdup_empty("markdown");
    if (!out->theme || !out->format) { ca_generated_presentation_free(out); return false; }

    out->slides = (ca_slide_outline_t *)calloc((size_t)target_slide_count, sizeof(ca_slide_outline_t));
    if (!out->slides) { ca_generated_presentation_free(out); return false; }
    size_t k = 0;

    /* slide 0: topic overview */
    {
        char b0[256], b1[64] = "Why it matters", b2[64] = "What we'll cover";
        snprintf(b0, sizeof(b0), "What is %s", topic);
        const char *bullets[] = { b0, b1, b2 };
        if (!slide_set(&out->slides[k], topic, "Overview", bullets, 3)) { ca_generated_presentation_free(out); return false; }
        k++;
    }
    /* middle parts: i = 2 .. target-1 */
    for (int i = 2; i < target_slide_count; ++i) {
        char title[256], body[256];
        snprintf(title, sizeof(title), "%s — Part %d", topic, i - 1);
        snprintf(body, sizeof(body), "Detail for part %d", i - 1);
        const char *bullets[] = { "Point A", "Point B", "Point C" };
        if (!slide_set(&out->slides[k], title, body, bullets, 3)) { ca_generated_presentation_free(out); return false; }
        k++;
    }
    /* conclusion */
    {
        char body[256];
        snprintf(body, sizeof(body), "Summary of %s", topic);
        const char *bullets[] = { "Recap", "Next steps", "Questions" };
        if (!slide_set(&out->slides[k], "Conclusion", body, bullets, 3)) { ca_generated_presentation_free(out); return false; }
        k++;
    }
    out->slide_count = k;
    return true;
}

bool ca_domain_null_presentation_generate(const char *topic, int target_slide_count,
                                          const char *theme,
                                          ca_generated_presentation_t *out) {
    (void)topic; (void)target_slide_count;
    if (out) memset(out, 0, sizeof(*out));
    if (!out) return false;
    out->slides = NULL; out->slide_count = 0;
    out->theme = cab_strdup_empty(theme ? theme : "default");
    out->format = cab_strdup_empty("json");
    if (!out->theme || !out->format) { ca_generated_presentation_free(out); return false; }
    return true;
}

/* ═══ Job search ═════════════════════════════════════════════════════════ */

void ca_job_application_draft_free(ca_job_application_draft_t *d) {
    if (!d) return;
    free(d->resume_text); free(d->cover_letter_text);
    cab_strv_free(d->key_matches, d->key_match_count);
    memset(d, 0, sizeof(*d));
}

const char *ca_job_search_pipeline_backend_id(void) { return "template"; }
const char *ca_domain_null_job_search_pipeline_backend_id(void) { return "null"; }

/* Extract distinct lowercased keywords (len>3), split on ws + , . ; : ( ). */
static char **extract_keywords(const char *text, size_t *out_n) {
    char **v = NULL; size_t n = 0, cap = 0;
    const char *p = text;
    while (*p) {
        while (*p && (*p == ' ' || *p == '\n' || *p == '\r' || *p == '\t' ||
                      *p == ',' || *p == '.' || *p == ';' || *p == ':' ||
                      *p == '(' || *p == ')')) p++;
        const char *s = p;
        while (*p && !(*p == ' ' || *p == '\n' || *p == '\r' || *p == '\t' ||
                       *p == ',' || *p == '.' || *p == ';' || *p == ':' ||
                       *p == '(' || *p == ')')) p++;
        size_t wl = (size_t)(p - s);
        if (wl <= 3) continue;
        char *w = (char *)malloc(wl + 1);
        if (!w) { cab_strv_free(v, n); return NULL; }
        for (size_t i = 0; i < wl; ++i) w[i] = (char)tolower((unsigned char)s[i]);
        w[wl] = '\0';
        bool dup = false;
        for (size_t i = 0; i < n; ++i) if (cab_ord_eq(v[i], w)) { dup = true; break; }
        if (dup) { free(w); continue; }
        if (n == cap) { size_t nc = cap ? cap * 2 : 8; char **nv = realloc(v, nc * sizeof(char*)); if (!nv) { free(w); cab_strv_free(v, n); return NULL; } v = nv; cap = nc; }
        v[n++] = w;
    }
    *out_n = n;
    return v;
}

bool ca_job_search_draft(const char *role_description,
                         const char *candidate_profile_text,
                         ca_job_application_draft_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out || !role_description || !candidate_profile_text) return false;

    size_t rn = 0, cn = 0;
    char **rw = extract_keywords(role_description, &rn);
    char **cw = extract_keywords(candidate_profile_text, &cn);
    if (!rw || !cw) { cab_strv_free(rw, rn); cab_strv_free(cw, cn); return false; }

    /* matches = roleWords intersect candWords (CI), take 10, role order */
    char **matches = NULL; size_t mn = 0;
    matches = (char **)calloc(rn ? rn : 1, sizeof(char *));
    if (!matches) { cab_strv_free(rw, rn); cab_strv_free(cw, cn); return false; }
    for (size_t i = 0; i < rn && mn < 10; ++i) {
        bool in_cand = false;
        for (size_t j = 0; j < cn; ++j) if (cab_ci_eq(rw[i], cw[j])) { in_cand = true; break; }
        if (!in_cand) continue;
        bool dup = false;
        for (size_t j = 0; j < mn; ++j) if (cab_ci_eq(matches[j], rw[i])) { dup = true; break; }
        if (dup) continue;
        matches[mn] = cab_strdup_empty(rw[i]);
        if (!matches[mn]) { cab_strv_free(matches, mn); cab_strv_free(rw, rn); cab_strv_free(cw, cn); return false; }
        mn++;
    }
    cab_strv_free(rw, rn); cab_strv_free(cw, cn);

    /* join helpers */
    char *joined_all = cab_strdup_empty("");
    for (size_t i = 0; joined_all && i < mn; ++i) {
        size_t need = strlen(joined_all) + (i ? 2 : 0) + strlen(matches[i]) + 1;
        char *ns = (char *)malloc(need);
        if (!ns) { free(joined_all); joined_all = NULL; break; }
        if (i) snprintf(ns, need, "%s, %s", joined_all, matches[i]);
        else snprintf(ns, need, "%s", matches[i]);
        free(joined_all); joined_all = ns;
    }
    char *joined3 = cab_strdup_empty("");
    for (size_t i = 0; joined3 && i < mn && i < 3; ++i) {
        size_t need = strlen(joined3) + (i ? 2 : 0) + strlen(matches[i]) + 1;
        char *ns = (char *)malloc(need);
        if (!ns) { free(joined3); joined3 = NULL; break; }
        if (i) snprintf(ns, need, "%s, %s", joined3, matches[i]);
        else snprintf(ns, need, "%s", matches[i]);
        free(joined3); joined3 = ns;
    }
    char *prof_tr = trim_dup_dom(candidate_profile_text);
    if (!joined_all || !joined3 || !prof_tr) {
        free(joined_all); free(joined3); free(prof_tr);
        cab_strv_free(matches, mn); return false;
    }

    size_t rl = strlen(prof_tr) + strlen("\n\nMatched skills: ") + strlen(joined_all) + 1;
    out->resume_text = (char *)malloc(rl);
    size_t cl = strlen("Dear Hiring Team,\n\nI am applying because my background (") +
                strlen(joined3) + strlen(") fits the role.\n\nRegards.") + 1;
    out->cover_letter_text = (char *)malloc(cl);
    if (out->resume_text) snprintf(out->resume_text, rl, "%s\n\nMatched skills: %s", prof_tr, joined_all);
    if (out->cover_letter_text) snprintf(out->cover_letter_text, cl,
        "Dear Hiring Team,\n\nI am applying because my background (%s) fits the role.\n\nRegards.", joined3);
    free(joined_all); free(joined3); free(prof_tr);

    if (!out->resume_text || !out->cover_letter_text) {
        cab_strv_free(matches, mn); ca_job_application_draft_free(out); return false;
    }
    out->key_matches = mn ? matches : (free(matches), NULL);
    out->key_match_count = mn;
    return true;
}

bool ca_domain_null_job_search_draft(const char *role, const char *profile,
                                     ca_job_application_draft_t *out) {
    (void)role; (void)profile;
    if (out) memset(out, 0, sizeof(*out));
    if (!out) return false;
    out->resume_text = cab_strdup_empty("");
    out->cover_letter_text = cab_strdup_empty("");
    if (!out->resume_text || !out->cover_letter_text) { ca_job_application_draft_free(out); return false; }
    out->key_matches = NULL; out->key_match_count = 0;
    return true;
}

/* ═══ Memory upgrades ════════════════════════════════════════════════════ */

void ca_domain_memory_item_free(ca_domain_memory_item_t *i) {
    if (!i) return;
    free(i->id); free(i->text);
    dkv_free(i->metadata, i->metadata_count);
    memset(i, 0, sizeof(*i));
}
static bool mem_item_copy(ca_domain_memory_item_t *dst,
                          const ca_domain_memory_item_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->id = cab_strdup_empty(src->id);
    dst->text = cab_strdup_empty(src->text);
    if (!dst->id || !dst->text) { ca_domain_memory_item_free(dst); return false; }
    if (!dkv_copy(&dst->metadata, src->metadata, src->metadata_count)) {
        ca_domain_memory_item_free(dst); return false;
    }
    dst->metadata_count = src->metadata_count;
    return true;
}

void ca_domain_memory_hit_free(ca_domain_memory_hit_t *h) {
    if (!h) return;
    ca_domain_memory_item_free(&h->item);
}
void ca_domain_memory_hit_free_array(ca_domain_memory_hit_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_domain_memory_hit_free(&arr[i]);
    free(arr);
}

struct ca_mempalace_store {
    ca_domain_memory_item_t *items; size_t count, cap;
};

ca_mempalace_store_t *ca_mempalace_store_create(void) {
    return (ca_mempalace_store_t *)calloc(1, sizeof(ca_mempalace_store_t));
}
void ca_mempalace_store_destroy(ca_mempalace_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_domain_memory_item_free(&s->items[i]);
    free(s->items);
    free(s);
}
const char *ca_mempalace_store_backend_id(const ca_mempalace_store_t *s) {
    (void)s; return "in-memory";
}
int ca_mempalace_store_upsert(ca_mempalace_store_t *s,
                              const ca_domain_memory_item_t *item) {
    if (!s || !item || cab_is_ws(item->id)) return -1;
    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ord_eq(s->items[i].id, item->id)) {
            ca_domain_memory_item_t copy;
            if (!mem_item_copy(&copy, item)) return -1;
            ca_domain_memory_item_free(&s->items[i]);
            s->items[i] = copy;
            return 0;
        }
    }
    ca_domain_memory_item_t copy;
    if (!mem_item_copy(&copy, item)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->items, nc * sizeof(*s->items));
        if (!n) { ca_domain_memory_item_free(&copy); return -1; }
        s->items = (ca_domain_memory_item_t *)n; s->cap = nc;
    }
    s->items[s->count++] = copy;
    return 0;
}

/* CI IndexOf: first byte index of `q` (trimmed) in `body`, or -1. */
static long ci_index_of(const char *body, const char *q) {
    if (!body || !q) return -1;
    size_t bl = strlen(body), ql = strlen(q);
    if (ql == 0 || bl == 0) return -1;
    for (size_t i = 0; i + ql <= bl; ++i) {
        size_t k = 0;
        while (k < ql && tolower((unsigned char)body[i + k]) == tolower((unsigned char)q[k])) k++;
        if (k == ql) return (long)i;
    }
    return -1;
}

/* Score body vs query = 1/(1+firstIndex) or 0 when absent/empty. */
static float mem_score(const char *body, const char *query) {
    if (cab_is_ws(body) || cab_is_ws(query)) {
        /* mirror string.IsNullOrEmpty checks (not whitespace); but empty->0 */
    }
    if (!body || !query || body[0] == '\0' || query[0] == '\0') return 0.0f;
    char *q = trim_dup_dom(query);
    if (!q) return 0.0f;
    long idx = ci_index_of(body, q);
    free(q);
    return idx < 0 ? 0.0f : 1.0f / (1.0f + (float)idx);
}

/* Build a scored+sorted hit array from item indices. */
static ca_domain_memory_hit_t *mem_recall_from(const ca_domain_memory_item_t *items,
                                               size_t item_count,
                                               const char *query, int top_k,
                                               size_t *out_count) {
    size_t *idx = (size_t *)malloc((item_count ? item_count : 1) * sizeof(size_t));
    float  *sc  = (float *)malloc((item_count ? item_count : 1) * sizeof(float));
    if (!idx || !sc) { free(idx); free(sc); *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < item_count; ++i) {
        float s = mem_score(items[i].text, query);
        if (s > 0.0f) { idx[n] = i; sc[n] = s; n++; }
    }
    for (size_t i = 1; i < n; ++i) {
        size_t ki = idx[i]; float ks = sc[i]; size_t j = i;
        while (j > 0 && sc[j - 1] < ks) { idx[j] = idx[j - 1]; sc[j] = sc[j - 1]; j--; }
        idx[j] = ki; sc[j] = ks;
    }
    if ((size_t)top_k < n) n = (size_t)top_k;
    if (n == 0) { free(idx); free(sc); *out_count = 0; return NULL; }
    ca_domain_memory_hit_t *out = (ca_domain_memory_hit_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); free(sc); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!mem_item_copy(&out[i].item, &items[idx[i]])) {
            ca_domain_memory_hit_free_array(out, i);
            free(idx); free(sc); *out_count = (size_t)-1; return NULL;
        }
        out[i].score = sc[i];
    }
    free(idx); free(sc);
    *out_count = n;
    return out;
}

ca_domain_memory_hit_t *ca_mempalace_store_recall(const ca_mempalace_store_t *s,
                                                  const char *query, int top_k,
                                                  size_t *out_count) {
    if (!out_count) return NULL;
    if (!s || !query || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    return mem_recall_from(s->items, s->count, query, top_k, out_count);
}

const char *ca_domain_null_mempalace_store_backend_id(void) { return "null"; }

struct ca_hipporag_store {
    ca_mempalace_store_t *base;
};

ca_hipporag_store_t *ca_hipporag_store_create(void) {
    ca_hipporag_store_t *s = (ca_hipporag_store_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->base = ca_mempalace_store_create();
    if (!s->base) { free(s); return NULL; }
    return s;
}
void ca_hipporag_store_destroy(ca_hipporag_store_t *s) {
    if (!s) return;
    ca_mempalace_store_destroy(s->base);
    free(s);
}
const char *ca_hipporag_store_backend_id(const ca_hipporag_store_t *s) {
    (void)s; return "in-memory";
}
int ca_hipporag_store_index(ca_hipporag_store_t *s,
                            const ca_domain_memory_item_t *item) {
    if (!s) return -1;
    return ca_mempalace_store_upsert(s->base, item);
}

ca_domain_memory_hit_t *ca_hipporag_store_multihop_recall(const ca_hipporag_store_t *s,
                                                          const char *query, int top_k,
                                                          size_t *out_count) {
    if (!out_count) return NULL;
    if (!s || !query || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    size_t first_n = 0;
    ca_domain_memory_hit_t *first = ca_mempalace_store_recall(s->base, query, top_k, &first_n);
    if (first_n == (size_t)-1) { *out_count = (size_t)-1; return NULL; }
    if (first_n == 0) { *out_count = 0; return first; }

    const char *seed = first[0].item.text;
    size_t second_n = 0;
    ca_domain_memory_hit_t *second = ca_mempalace_store_recall(s->base, seed, top_k, &second_n);
    if (second_n == (size_t)-1) { ca_domain_memory_hit_free_array(first, first_n); *out_count = (size_t)-1; return NULL; }

    /* union by Id (first wins on dup), order by Score desc, take top_k */
    size_t total = first_n + second_n;
    ca_domain_memory_hit_t *merged = (ca_domain_memory_hit_t *)calloc(total ? total : 1, sizeof(*merged));
    if (!merged) {
        ca_domain_memory_hit_free_array(first, first_n);
        ca_domain_memory_hit_free_array(second, second_n);
        *out_count = (size_t)-1; return NULL;
    }
    size_t mn = 0;
    bool ok = true;
    for (size_t i = 0; ok && i < first_n; ++i) {
        if (!mem_item_copy(&merged[mn].item, &first[i].item)) { ok = false; break; }
        merged[mn].score = first[i].score; mn++;
    }
    for (size_t i = 0; ok && i < second_n; ++i) {
        bool dup = false;
        for (size_t j = 0; j < mn; ++j) if (cab_ord_eq(merged[j].item.id, second[i].item.id)) { dup = true; break; }
        if (dup) continue;
        if (!mem_item_copy(&merged[mn].item, &second[i].item)) { ok = false; break; }
        merged[mn].score = second[i].score; mn++;
    }
    ca_domain_memory_hit_free_array(first, first_n);
    ca_domain_memory_hit_free_array(second, second_n);
    if (!ok) { ca_domain_memory_hit_free_array(merged, mn); *out_count = (size_t)-1; return NULL; }

    /* order by score desc (stable) */
    for (size_t i = 1; i < mn; ++i) {
        ca_domain_memory_hit_t key = merged[i];
        size_t j = i;
        while (j > 0 && merged[j - 1].score < key.score) { merged[j] = merged[j - 1]; j--; }
        merged[j] = key;
    }
    if ((size_t)top_k < mn) {
        for (size_t i = (size_t)top_k; i < mn; ++i) ca_domain_memory_hit_free(&merged[i]);
        mn = (size_t)top_k;
    }
    if (mn == 0) { free(merged); *out_count = 0; return NULL; }
    *out_count = mn;
    return merged;
}

const char *ca_domain_null_hipporag_store_backend_id(void) { return "null"; }

/* ═══ Swarm ══════════════════════════════════════════════════════════════ */

void ca_swarm_peer_free(ca_swarm_peer_t *p) {
    if (!p) return;
    free(p->peer_id); free(p->capability);
    p->peer_id = p->capability = NULL;
}
void ca_swarm_peer_free_array(ca_swarm_peer_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_swarm_peer_free(&arr[i]);
    free(arr);
}
static bool peer_copy(ca_swarm_peer_t *dst, const ca_swarm_peer_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->health = src->health;
    dst->peer_id = cab_strdup_empty(src->peer_id);
    dst->capability = cab_strdup_empty(src->capability);
    if (!dst->peer_id || !dst->capability) { ca_swarm_peer_free(dst); return false; }
    return true;
}

struct ca_swarm_coordinator {
    ca_swarm_peer_t *items; size_t count, cap;
};

ca_swarm_coordinator_t *ca_swarm_coordinator_create(void) {
    return (ca_swarm_coordinator_t *)calloc(1, sizeof(ca_swarm_coordinator_t));
}
void ca_swarm_coordinator_destroy(ca_swarm_coordinator_t *c) {
    if (!c) return;
    for (size_t i = 0; i < c->count; ++i) ca_swarm_peer_free(&c->items[i]);
    free(c->items);
    free(c);
}
const char *ca_swarm_coordinator_backend_id(const ca_swarm_coordinator_t *c) {
    (void)c; return "in-memory";
}
int ca_swarm_coordinator_register(ca_swarm_coordinator_t *c,
                                  const ca_swarm_peer_t *peer) {
    if (!c || !peer || !peer->peer_id) return -1;
    for (size_t i = 0; i < c->count; ++i) {
        if (cab_ord_eq(c->items[i].peer_id, peer->peer_id)) {
            ca_swarm_peer_t copy;
            if (!peer_copy(&copy, peer)) return -1;
            ca_swarm_peer_free(&c->items[i]);
            c->items[i] = copy;
            return 0;
        }
    }
    ca_swarm_peer_t copy;
    if (!peer_copy(&copy, peer)) return -1;
    if (c->count == c->cap) {
        size_t nc = c->cap ? c->cap * 2 : 4;
        void *n = realloc(c->items, nc * sizeof(*c->items));
        if (!n) { ca_swarm_peer_free(&copy); return -1; }
        c->items = (ca_swarm_peer_t *)n; c->cap = nc;
    }
    c->items[c->count++] = copy;
    return 0;
}
ca_swarm_peer_t *ca_swarm_coordinator_list_peers(const ca_swarm_coordinator_t *c,
                                                 size_t *out_count) {
    if (!out_count) return NULL;
    if (!c) { *out_count = (size_t)-1; return NULL; }
    if (c->count == 0) { *out_count = 0; return NULL; }
    ca_swarm_peer_t *out = (ca_swarm_peer_t *)calloc(c->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < c->count; ++i) {
        if (!peer_copy(&out[i], &c->items[i])) {
            ca_swarm_peer_free_array(out, i);
            *out_count = (size_t)-1; return NULL;
        }
    }
    *out_count = c->count;
    return out;
}
char *ca_swarm_coordinator_choose_delegate(const ca_swarm_coordinator_t *c,
                                           const char *capability) {
    if (!c || cab_is_ws(capability)) return NULL;
    const ca_swarm_peer_t *best = NULL;
    for (size_t i = 0; i < c->count; ++i) {
        if (!cab_ci_eq(c->items[i].capability, capability)) continue;
        if (!best || c->items[i].health > best->health) best = &c->items[i];
    }
    return best ? cab_strdup_empty(best->peer_id) : NULL;
}

const char *ca_domain_null_swarm_coordinator_backend_id(void) { return "null"; }

/* ═══ Personal LoRA ══════════════════════════════════════════════════════ */

void ca_lora_training_summary_free(ca_lora_training_summary_t *s) {
    if (!s) return;
    free(s->adapter_id);
    s->adapter_id = NULL;
}

typedef struct { char *id; int steps; float loss; } lora_state_t;

struct ca_personal_lora {
    lora_state_t *adapters; size_t count, cap;
    char        **loaded;   size_t loaded_count, loaded_cap;
};

ca_personal_lora_t *ca_personal_lora_create(void) {
    return (ca_personal_lora_t *)calloc(1, sizeof(ca_personal_lora_t));
}
void ca_personal_lora_destroy(ca_personal_lora_t *l) {
    if (!l) return;
    for (size_t i = 0; i < l->count; ++i) free(l->adapters[i].id);
    free(l->adapters);
    cab_strv_free(l->loaded, l->loaded_count);
    free(l);
}
const char *ca_personal_lora_backend_id(const ca_personal_lora_t *l) {
    (void)l; return "in-memory";
}

bool ca_personal_lora_train(ca_personal_lora_t *l, const char *adapter_id,
                            const char *const *samples, size_t sample_count,
                            ca_lora_training_summary_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!l || cab_is_ws(adapter_id) || (!samples && sample_count > 0) ||
        sample_count == 0 || !out) return false;

    int steps = (int)sample_count;
    long total_chars = 0;
    for (size_t i = 0; i < sample_count; ++i)
        total_chars += samples[i] ? (long)strlen(samples[i]) : 0;
    float final_loss = (float)(1.0 / (1.0 + log(1.0 + (double)steps)) +
                               1.0 / (1.0 + (double)total_chars / 1000.0));

    /* record state (replace) */
    lora_state_t *slot = NULL;
    for (size_t i = 0; i < l->count; ++i)
        if (cab_ord_eq(l->adapters[i].id, adapter_id)) { slot = &l->adapters[i]; break; }
    if (!slot) {
        if (l->count == l->cap) {
            size_t nc = l->cap ? l->cap * 2 : 4;
            void *n = realloc(l->adapters, nc * sizeof(lora_state_t));
            if (!n) return false;
            l->adapters = (lora_state_t *)n; l->cap = nc;
        }
        char *id = cab_strdup_empty(adapter_id);
        if (!id) return false;
        slot = &l->adapters[l->count++];
        slot->id = id;
    }
    slot->steps = steps;
    slot->loss = final_loss;

    out->adapter_id = cab_strdup_empty(adapter_id);
    if (!out->adapter_id) return false;
    out->steps_trained = steps;
    out->final_loss = final_loss;
    return true;
}

static bool lora_has_adapter(const ca_personal_lora_t *l, const char *id) {
    for (size_t i = 0; i < l->count; ++i)
        if (cab_ord_eq(l->adapters[i].id, id)) return true;
    return false;
}

int ca_personal_lora_load(ca_personal_lora_t *l, const char *adapter_id) {
    if (!l || cab_is_ws(adapter_id)) return -1;
    if (!lora_has_adapter(l, adapter_id)) return -1; /* not trained */
    for (size_t i = 0; i < l->loaded_count; ++i)
        if (cab_ord_eq(l->loaded[i], adapter_id)) return 0;
    if (l->loaded_count == l->loaded_cap) {
        size_t nc = l->loaded_cap ? l->loaded_cap * 2 : 4;
        char **n = (char **)realloc(l->loaded, nc * sizeof(char *));
        if (!n) return -1;
        l->loaded = n; l->loaded_cap = nc;
    }
    char *id = cab_strdup_empty(adapter_id);
    if (!id) return -1;
    l->loaded[l->loaded_count++] = id;
    return 0;
}
int ca_personal_lora_unload(ca_personal_lora_t *l, const char *adapter_id) {
    if (!l || cab_is_ws(adapter_id)) return -1;
    for (size_t i = 0; i < l->loaded_count; ++i) {
        if (cab_ord_eq(l->loaded[i], adapter_id)) {
            free(l->loaded[i]);
            l->loaded[i] = l->loaded[l->loaded_count - 1];
            l->loaded_count--;
            return 0;
        }
    }
    return 0;
}
bool ca_personal_lora_is_loaded(const ca_personal_lora_t *l, const char *adapter_id) {
    if (!l || !adapter_id) return false;
    for (size_t i = 0; i < l->loaded_count; ++i)
        if (cab_ord_eq(l->loaded[i], adapter_id)) return true;
    return false;
}

const char *ca_domain_null_personal_lora_backend_id(void) { return "null"; }
