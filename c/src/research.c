/*
 * research.c — CircleAI.Research (C11 port).
 *
 * Corpus keyed by PaperId; search scores Title(+3)/Abstract(+1)/any-Author(+1)
 * with an OrdinalIgnoreCase substring test, keeps score>0, orders by score desc
 * (stable), takes top_k. Retrieval keyed by PaperId. Citation graph stores
 * citations flat and filters forward/backward on read. Deterministic linear
 * arrays. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/research.h"
#include "board_common.h"

/* ── ResearchPaper ──────────────────────────────────────────────────────── */

void ca_research_paper_free(ca_research_paper_t *p) {
    if (!p) return;
    free(p->paper_id);
    free(p->title);
    cab_strv_free(p->authors, p->author_count);
    free(p->abstract_text);
    free(p->doi);
    memset(p, 0, sizeof(*p));
}
void ca_research_paper_free_array(ca_research_paper_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_research_paper_free(&arr[i]);
    free(arr);
}
static bool paper_copy(ca_research_paper_t *dst, const ca_research_paper_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->published_at_utc_ms = src->published_at_utc_ms;
    dst->paper_id      = cab_strdup_empty(src->paper_id);
    dst->title         = cab_strdup_empty(src->title);
    dst->abstract_text = cab_strdup_empty(src->abstract_text);
    dst->doi           = src->doi ? cab_strdup(src->doi) : NULL;
    if (!dst->paper_id || !dst->title || !dst->abstract_text ||
        (src->doi && !dst->doi)) {
        ca_research_paper_free(dst); return false;
    }
    if (!cab_strv_copy(&dst->authors, src->authors, src->author_count)) {
        ca_research_paper_free(dst); return false;
    }
    dst->author_count = src->author_count;
    return true;
}

/* ── Citation ───────────────────────────────────────────────────────────── */

void ca_citation_free(ca_citation_t *c) {
    if (!c) return;
    free(c->from_paper_id);
    free(c->to_paper_id);
    free(c->context);
    c->from_paper_id = c->to_paper_id = c->context = NULL;
}
void ca_citation_free_array(ca_citation_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_citation_free(&arr[i]);
    free(arr);
}
static bool citation_copy(ca_citation_t *dst, const ca_citation_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->from_paper_id = cab_strdup_empty(src->from_paper_id);
    dst->to_paper_id   = cab_strdup_empty(src->to_paper_id);
    dst->context       = cab_strdup_empty(src->context);
    if (!dst->from_paper_id || !dst->to_paper_id || !dst->context) {
        ca_citation_free(dst); return false;
    }
    return true;
}

/* ── InMemoryResearchCorpus ─────────────────────────────────────────────── */

struct ca_research_corpus {
    ca_research_paper_t *items;
    size_t               count, cap;
};

ca_research_corpus_t *ca_research_corpus_create(void) {
    return (ca_research_corpus_t *)calloc(1, sizeof(ca_research_corpus_t));
}
void ca_research_corpus_destroy(ca_research_corpus_t *c) {
    if (!c) return;
    for (size_t i = 0; i < c->count; ++i) ca_research_paper_free(&c->items[i]);
    free(c->items);
    free(c);
}
const char *ca_research_corpus_backend_id(const ca_research_corpus_t *c) {
    (void)c; return "in-memory";
}

int ca_research_corpus_add(ca_research_corpus_t *c, const ca_research_paper_t *paper) {
    if (!c || !paper || !paper->paper_id) return -1;
    for (size_t i = 0; i < c->count; ++i) {
        if (cab_ord_eq(c->items[i].paper_id, paper->paper_id)) {
            ca_research_paper_t copy;
            if (!paper_copy(&copy, paper)) return -1;
            ca_research_paper_free(&c->items[i]);
            c->items[i] = copy;
            return 0;
        }
    }
    ca_research_paper_t copy;
    if (!paper_copy(&copy, paper)) return -1;
    if (c->count == c->cap) {
        size_t nc = c->cap ? c->cap * 2 : 4;
        void *n = realloc(c->items, nc * sizeof(*c->items));
        if (!n) { ca_research_paper_free(&copy); return -1; }
        c->items = (ca_research_paper_t *)n;
        c->cap = nc;
    }
    c->items[c->count++] = copy;
    return 0;
}

bool ca_research_corpus_get(const ca_research_corpus_t *c, const char *paper_id,
                            ca_research_paper_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!c || cab_is_ws(paper_id) || !out) return false;
    for (size_t i = 0; i < c->count; ++i)
        if (cab_ord_eq(c->items[i].paper_id, paper_id))
            return paper_copy(out, &c->items[i]);
    return false;
}

static int paper_score(const ca_research_paper_t *p, const char *q) {
    int s = 0;
    if (cab_ci_contains(p->title, q))         s += 3;
    if (cab_ci_contains(p->abstract_text, q)) s += 1;
    for (size_t i = 0; i < p->author_count; ++i)
        if (cab_ci_contains(p->authors[i], q)) { s += 1; break; }
    return s;
}

ca_research_paper_t *ca_research_corpus_search(const ca_research_corpus_t *c,
                                               const char *query, int top_k,
                                               size_t *out_count) {
    if (!out_count) return NULL;
    if (!c || !query || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    if (c->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(c->count * sizeof(size_t));
    int    *sc  = (int *)malloc(c->count * sizeof(int));
    if (!idx || !sc) { free(idx); free(sc); *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < c->count; ++i) {
        int s = paper_score(&c->items[i], query);
        if (s > 0) { idx[n] = i; sc[n] = s; n++; }
    }
    /* stable sort by score desc (insertion) */
    for (size_t i = 1; i < n; ++i) {
        size_t ki = idx[i]; int ks = sc[i];
        size_t j = i;
        while (j > 0 && sc[j - 1] < ks) {
            idx[j] = idx[j - 1]; sc[j] = sc[j - 1]; j--;
        }
        idx[j] = ki; sc[j] = ks;
    }
    if ((size_t)top_k < n) n = (size_t)top_k;
    free(sc);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_research_paper_t *out = (ca_research_paper_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!paper_copy(&out[i], &c->items[idx[i]])) {
            ca_research_paper_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

const char *ca_research_null_corpus_backend_id(void) { return "null"; }

/* ── InMemoryPaperRetrieval ─────────────────────────────────────────────── */

typedef struct {
    char    *paper_id; /* owned */
    uint8_t *bytes;    /* owned, or NULL when len == 0 */
    size_t   len;
} research_fulltext_t;

struct ca_paper_retrieval {
    research_fulltext_t *items;
    size_t               count, cap;
};

ca_paper_retrieval_t *ca_paper_retrieval_create(void) {
    return (ca_paper_retrieval_t *)calloc(1, sizeof(ca_paper_retrieval_t));
}
void ca_paper_retrieval_destroy(ca_paper_retrieval_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->count; ++i) {
        free(r->items[i].paper_id);
        free(r->items[i].bytes);
    }
    free(r->items);
    free(r);
}
const char *ca_paper_retrieval_backend_id(const ca_paper_retrieval_t *r) {
    (void)r; return "in-memory";
}

static bool fulltext_set(research_fulltext_t *dst, const char *paper_id,
                         const uint8_t *bytes, size_t len) {
    char *id = cab_strdup_empty(paper_id);
    if (!id) return false;
    uint8_t *buf = NULL;
    if (len > 0) {
        buf = (uint8_t *)malloc(len);
        if (!buf) { free(id); return false; }
        if (bytes) memcpy(buf, bytes, len);
        else memset(buf, 0, len);
    }
    free(dst->paper_id);
    free(dst->bytes);
    dst->paper_id = id;
    dst->bytes = buf;
    dst->len = len;
    return true;
}

int ca_paper_retrieval_add(ca_paper_retrieval_t *r, const char *paper_id,
                           const uint8_t *bytes, size_t len) {
    if (!r || cab_is_ws(paper_id)) return -1;
    for (size_t i = 0; i < r->count; ++i)
        if (cab_ord_eq(r->items[i].paper_id, paper_id))
            return fulltext_set(&r->items[i], paper_id, bytes, len) ? 0 : -1;
    if (r->count == r->cap) {
        size_t nc = r->cap ? r->cap * 2 : 4;
        void *n = realloc(r->items, nc * sizeof(*r->items));
        if (!n) return -1;
        r->items = (research_fulltext_t *)n;
        r->cap = nc;
    }
    research_fulltext_t slot = {0};
    if (!fulltext_set(&slot, paper_id, bytes, len)) return -1;
    r->items[r->count++] = slot;
    return 0;
}

uint8_t *ca_paper_retrieval_fetch(const ca_paper_retrieval_t *r,
                                  const char *paper_id, size_t *out_len) {
    if (!out_len) return NULL;
    if (!r || cab_is_ws(paper_id)) { *out_len = (size_t)-1; return NULL; }
    for (size_t i = 0; i < r->count; ++i) {
        if (!cab_ord_eq(r->items[i].paper_id, paper_id)) continue;
        size_t len = r->items[i].len;
        if (len == 0) { *out_len = 0; return NULL; } /* empty payload present */
        uint8_t *buf = (uint8_t *)malloc(len);
        if (!buf) { *out_len = (size_t)-1; return NULL; }
        memcpy(buf, r->items[i].bytes, len);
        *out_len = len;
        return buf;
    }
    *out_len = 0;
    return NULL;
}

const char *ca_research_null_retrieval_backend_id(void) { return "null"; }

/* ── InMemoryCitationGraph ──────────────────────────────────────────────── */

struct ca_citation_graph {
    ca_citation_t *items;
    size_t         count, cap;
};

ca_citation_graph_t *ca_citation_graph_create(void) {
    return (ca_citation_graph_t *)calloc(1, sizeof(ca_citation_graph_t));
}
void ca_citation_graph_destroy(ca_citation_graph_t *g) {
    if (!g) return;
    for (size_t i = 0; i < g->count; ++i) ca_citation_free(&g->items[i]);
    free(g->items);
    free(g);
}
const char *ca_citation_graph_backend_id(const ca_citation_graph_t *g) {
    (void)g; return "in-memory";
}

int ca_citation_graph_link(ca_citation_graph_t *g, const ca_citation_t *c) {
    if (!g || !c) return -1;
    ca_citation_t copy;
    if (!citation_copy(&copy, c)) return -1;
    if (g->count == g->cap) {
        size_t nc = g->cap ? g->cap * 2 : 4;
        void *n = realloc(g->items, nc * sizeof(*g->items));
        if (!n) { ca_citation_free(&copy); return -1; }
        g->items = (ca_citation_t *)n;
        g->cap = nc;
    }
    g->items[g->count++] = copy;
    return 0;
}

/* dir 0 = forward (match FromPaperId), 1 = backward (match ToPaperId). */
static ca_citation_t *citation_filter(const ca_citation_graph_t *g,
                                      const char *paper_id, int dir,
                                      size_t *out_count) {
    if (!out_count) return NULL;
    if (!g || cab_is_ws(paper_id)) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < g->count; ++i) {
        const char *key = dir == 0 ? g->items[i].from_paper_id
                                    : g->items[i].to_paper_id;
        if (cab_ord_eq(key, paper_id)) n++;
    }
    if (n == 0) { *out_count = 0; return NULL; }
    ca_citation_t *out = (ca_citation_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    size_t k = 0;
    for (size_t i = 0; i < g->count; ++i) {
        const char *key = dir == 0 ? g->items[i].from_paper_id
                                    : g->items[i].to_paper_id;
        if (!cab_ord_eq(key, paper_id)) continue;
        if (!citation_copy(&out[k], &g->items[i])) {
            ca_citation_free_array(out, k);
            *out_count = (size_t)-1;
            return NULL;
        }
        k++;
    }
    *out_count = n;
    return out;
}

ca_citation_t *ca_citation_graph_forward(const ca_citation_graph_t *g,
                                         const char *paper_id, size_t *out_count) {
    return citation_filter(g, paper_id, 0, out_count);
}
ca_citation_t *ca_citation_graph_backward(const ca_citation_graph_t *g,
                                          const char *paper_id, size_t *out_count) {
    return citation_filter(g, paper_id, 1, out_count);
}

const char *ca_research_null_citation_graph_backend_id(void) { return "null"; }
