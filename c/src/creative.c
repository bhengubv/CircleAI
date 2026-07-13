/*
 * creative.c — CircleAI.Creative (C11 port of CreativePrimitives.cs).
 *
 * InMemoryCreativeBoard: works (WorkId keyed), inspiration (append list),
 * critiques (append list). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/creative.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_creative_work_free(ca_creative_work_t *w) {
    if (!w) return;
    free(w->work_id);
    free(w->title);
    free(w->medium);
    free(w->author);
    cab_strv_free(w->tags, w->tag_count);
    w->work_id = w->title = w->medium = w->author = NULL;
    w->tags = NULL;
    w->tag_count = 0;
}
void ca_creative_work_free_array(ca_creative_work_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_creative_work_free(&arr[i]);
    free(arr);
}

static bool work_copy(ca_creative_work_t *dst, const ca_creative_work_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->work_id = cab_strdup_empty(src->work_id);
    dst->title   = cab_strdup_empty(src->title);
    dst->medium  = cab_strdup_empty(src->medium);
    dst->author  = cab_strdup_empty(src->author);
    dst->created_utc_ms = src->created_utc_ms;
    bool ok = dst->work_id && dst->title && dst->medium && dst->author;
    if (ok) ok = cab_strv_copy(&dst->tags, src->tags, src->tag_count);
    if (ok) dst->tag_count = src->tag_count;
    if (!ok) { ca_creative_work_free(dst); return false; }
    return true;
}

void ca_creative_inspiration_free(ca_creative_inspiration_t *i) {
    if (!i) return;
    free(i->inspiration_id);
    free(i->prompt_text);
    free(i->source_url);
    i->inspiration_id = i->prompt_text = i->source_url = NULL;
}
void ca_creative_inspiration_free_array(ca_creative_inspiration_t *arr,
                                        size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_creative_inspiration_free(&arr[i]);
    free(arr);
}

static bool inspiration_copy(ca_creative_inspiration_t *dst,
                             const ca_creative_inspiration_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->inspiration_id = cab_strdup_empty(src->inspiration_id);
    dst->prompt_text    = cab_strdup_empty(src->prompt_text);
    dst->source_url     = cab_strdup_empty(src->source_url);
    dst->seen_utc_ms    = src->seen_utc_ms;
    if (!dst->inspiration_id || !dst->prompt_text || !dst->source_url) {
        ca_creative_inspiration_free(dst);
        return false;
    }
    return true;
}

static bool critique_copy(ca_creative_critique_t *dst,
                          const ca_creative_critique_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->critique_id = cab_strdup_empty(src->critique_id);
    dst->work_id     = cab_strdup_empty(src->work_id);
    dst->reviewer    = cab_strdup_empty(src->reviewer);
    dst->body        = cab_strdup_empty(src->body);
    dst->score       = src->score;
    if (!dst->critique_id || !dst->work_id || !dst->reviewer || !dst->body) {
        free(dst->critique_id); free(dst->work_id); free(dst->reviewer);
        free(dst->body);
        memset(dst, 0, sizeof(*dst));
        return false;
    }
    return true;
}
static void critique_free(ca_creative_critique_t *c) {
    if (!c) return;
    free(c->critique_id);
    free(c->work_id);
    free(c->reviewer);
    free(c->body);
    c->critique_id = c->work_id = c->reviewer = c->body = NULL;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_creative_board {
    ca_creative_work_t        *works;
    size_t                     w_count, w_cap;
    ca_creative_inspiration_t *inspiration;
    size_t                     i_count, i_cap;
    ca_creative_critique_t    *critiques;
    size_t                     c_count, c_cap;
};

ca_creative_board_t *ca_creative_board_create(void) {
    return (ca_creative_board_t *)calloc(1, sizeof(ca_creative_board_t));
}
void ca_creative_board_destroy(ca_creative_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->w_count; ++i) ca_creative_work_free(&b->works[i]);
    for (size_t i = 0; i < b->i_count; ++i) ca_creative_inspiration_free(&b->inspiration[i]);
    for (size_t i = 0; i < b->c_count; ++i) critique_free(&b->critiques[i]);
    free(b->works);
    free(b->inspiration);
    free(b->critiques);
    free(b);
}

int ca_creative_board_add_work(ca_creative_board_t *b,
                               const ca_creative_work_t *w) {
    if (!b || !w) return -1;
    for (size_t i = 0; i < b->w_count; ++i) {
        if (cab_ord_eq(b->works[i].work_id, w->work_id)) {
            ca_creative_work_t copy;
            if (!work_copy(&copy, w)) return -1;
            ca_creative_work_free(&b->works[i]);
            b->works[i] = copy;
            return 0;
        }
    }
    ca_creative_work_t copy;
    if (!work_copy(&copy, w)) return -1;
    if (b->w_count == b->w_cap) {
        size_t nc = b->w_cap ? b->w_cap * 2 : 4;
        void *n = realloc(b->works, nc * sizeof(*b->works));
        if (!n) { ca_creative_work_free(&copy); return -1; }
        b->works = (ca_creative_work_t *)n;
        b->w_cap = nc;
    }
    b->works[b->w_count++] = copy;
    return 0;
}

bool ca_creative_board_get_work(const ca_creative_board_t *b, const char *id,
                                ca_creative_work_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->w_count; ++i)
        if (cab_ord_eq(b->works[i].work_id, id))
            return work_copy(out, &b->works[i]);
    return false;
}

ca_creative_work_t *ca_creative_board_works_by_tag(const ca_creative_board_t *b,
                                                   const char *tag,
                                                   size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !tag) { *out_count = (size_t)-1; return NULL; }
    if (b->w_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->w_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->w_count; ++i)
        if (cab_strv_ci_contains(b->works[i].tags, b->works[i].tag_count, tag))
            idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_creative_work_t *out = (ca_creative_work_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!work_copy(&out[i], &b->works[idx[i]])) {
            ca_creative_work_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_creative_board_record_inspiration(ca_creative_board_t *b,
                                         const ca_creative_inspiration_t *i) {
    if (!b || !i) return -1;
    ca_creative_inspiration_t copy;
    if (!inspiration_copy(&copy, i)) return -1;
    if (b->i_count == b->i_cap) {
        size_t nc = b->i_cap ? b->i_cap * 2 : 4;
        void *n = realloc(b->inspiration, nc * sizeof(*b->inspiration));
        if (!n) { ca_creative_inspiration_free(&copy); return -1; }
        b->inspiration = (ca_creative_inspiration_t *)n;
        b->i_cap = nc;
    }
    b->inspiration[b->i_count++] = copy;
    return 0;
}

/* Stable descending sort of collected indices by SeenUtc. */
static void inspiration_sort_desc(const ca_creative_board_t *b, size_t *idx,
                                  size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->inspiration[key].seen_utc_ms;
        size_t j = i;
        while (j > 0 && b->inspiration[idx[j - 1]].seen_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_creative_inspiration_t *ca_creative_board_recent_inspiration(
    const ca_creative_board_t *b, int limit, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || limit <= 0) { *out_count = (size_t)-1; return NULL; }
    if (b->i_count == 0) { *out_count = 0; return NULL; }

    size_t n = b->i_count;
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    inspiration_sort_desc(b, idx, n);
    if ((size_t)limit < n) n = (size_t)limit;

    ca_creative_inspiration_t *out =
        (ca_creative_inspiration_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!inspiration_copy(&out[i], &b->inspiration[idx[i]])) {
            ca_creative_inspiration_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_creative_board_add_critique(ca_creative_board_t *b,
                                   const ca_creative_critique_t *c) {
    if (!b || !c) return -1;
    ca_creative_critique_t copy;
    if (!critique_copy(&copy, c)) return -1;
    if (b->c_count == b->c_cap) {
        size_t nc = b->c_cap ? b->c_cap * 2 : 4;
        void *n = realloc(b->critiques, nc * sizeof(*b->critiques));
        if (!n) { critique_free(&copy); return -1; }
        b->critiques = (ca_creative_critique_t *)n;
        b->c_cap = nc;
    }
    b->critiques[b->c_count++] = copy;
    return 0;
}

double ca_creative_board_avg_score(const ca_creative_board_t *b,
                                   const char *work_id) {
    if (!b || !work_id) return 0.0;
    double sum = 0.0;
    size_t n = 0;
    for (size_t i = 0; i < b->c_count; ++i)
        if (cab_ord_eq(b->critiques[i].work_id, work_id)) {
            sum += (double)b->critiques[i].score;
            n++;
        }
    /* DefaultIfEmpty(0).Average(): 0.0 when empty. */
    return n == 0 ? 0.0 : sum / (double)n;
}

size_t ca_creative_board_work_count(const ca_creative_board_t *b) {
    return b ? b->w_count : 0;
}

bool ca_creative_board_remove_work(ca_creative_board_t *b, const char *work_id) {
    if (!b || !work_id) return false;
    size_t at = b->w_count;
    for (size_t i = 0; i < b->w_count; ++i)
        if (cab_ord_eq(b->works[i].work_id, work_id)) { at = i; break; }
    if (at == b->w_count) return false;   /* TryRemove failed */

    ca_creative_work_free(&b->works[at]);
    for (size_t i = at; i + 1 < b->w_count; ++i)
        b->works[i] = b->works[i + 1];
    b->w_count--;

    /* _critiques.RemoveAll(c => c.WorkId == workId) — Ordinal, compact in place. */
    size_t w = 0;
    for (size_t i = 0; i < b->c_count; ++i) {
        if (cab_ord_eq(b->critiques[i].work_id, work_id)) {
            free(b->critiques[i].critique_id);
            free(b->critiques[i].work_id);
            free(b->critiques[i].reviewer);
            free(b->critiques[i].body);
        } else {
            if (w != i) b->critiques[w] = b->critiques[i];
            w++;
        }
    }
    b->c_count = w;
    return true;
}

/* Collect works matching an Author/Medium filter (OrdinalIgnoreCase) into a fresh
 * owned array ordered by CreatedUtc descending (stable). by_author gates which
 * field is compared. */
static ca_creative_work_t *works_by_field(const ca_creative_board_t *b,
                                          bool by_author, const char *value,
                                          size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !value) { *out_count = (size_t)-1; return NULL; }
    if (b->w_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->w_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->w_count; ++i) {
        const char *field = by_author ? b->works[i].author : b->works[i].medium;
        if (cab_ci_eq(field, value)) idx[n++] = i;
    }
    if (n == 0) { free(idx); *out_count = 0; return NULL; }

    /* OrderByDescending(CreatedUtc), stable insertion sort. */
    for (size_t i = 1; i < n; ++i) {
        size_t cur = idx[i];
        int64_t key = b->works[cur].created_utc_ms;
        size_t j = i;
        while (j > 0 && b->works[idx[j - 1]].created_utc_ms < key) {
            idx[j] = idx[j - 1]; --j;
        }
        idx[j] = cur;
    }

    ca_creative_work_t *out = (ca_creative_work_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!work_copy(&out[i], &b->works[idx[i]])) {
            ca_creative_work_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

ca_creative_work_t *ca_creative_board_works_by_author(
    const ca_creative_board_t *b, const char *author, size_t *out_count) {
    return works_by_field(b, true, author, out_count);
}

ca_creative_work_t *ca_creative_board_works_by_medium(
    const ca_creative_board_t *b, const char *medium, size_t *out_count) {
    return works_by_field(b, false, medium, out_count);
}

/* Find a work by WorkId (Ordinal); NULL when absent. */
static const ca_creative_work_t *work_find(const ca_creative_board_t *b,
                                           const char *work_id) {
    for (size_t i = 0; i < b->w_count; ++i)
        if (cab_ord_eq(b->works[i].work_id, work_id)) return &b->works[i];
    return NULL;
}

bool ca_creative_board_top_rated_work(const ca_creative_board_t *b,
                                      ca_creative_work_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !out) return false;
    if (b->c_count == 0) return false;

    /* Group critiques by WorkId (Ordinal, first-appearance order); accumulate
     * sum + count to average. */
    typedef struct { const char *work_id; double sum; size_t count; } grp_t;
    grp_t *g = (grp_t *)malloc(b->c_count * sizeof(*g));
    if (!g) return false;
    size_t gc = 0;
    for (size_t i = 0; i < b->c_count; ++i) {
        const char *wid = b->critiques[i].work_id;
        size_t k;
        for (k = 0; k < gc; ++k)
            if (cab_ord_eq(g[k].work_id, wid)) break;
        if (k == gc) { g[gc].work_id = wid; g[gc].sum = 0.0; g[gc].count = 0; gc++; }
        g[k].sum += (double)b->critiques[i].score;
        g[k].count++;
    }

    /* OrderByDescending(Avg), stable insertion sort (first-appearance ties). */
    for (size_t i = 1; i < gc; ++i) {
        grp_t cur = g[i];
        double cur_avg = cur.sum / (double)cur.count;
        size_t j = i;
        while (j > 0 && (g[j - 1].sum / (double)g[j - 1].count) < cur_avg) {
            g[j] = g[j - 1]; --j;
        }
        g[j] = cur;
    }

    /* FirstOrDefault(w => w is not null): first group whose work still exists. */
    const ca_creative_work_t *found = NULL;
    for (size_t i = 0; i < gc; ++i) {
        const ca_creative_work_t *w = work_find(b, g[i].work_id);
        if (w) { found = w; break; }
    }
    free(g);
    if (!found) return false;
    return work_copy(out, found);
}

void ca_creative_tags_free(char **tags, size_t count) {
    cab_strv_free(tags, count);
}

char **ca_creative_board_all_tags(const ca_creative_board_t *b,
                                  size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }

    /* Count total tags to bound the distinct set. */
    size_t total = 0;
    for (size_t i = 0; i < b->w_count; ++i) total += b->works[i].tag_count;
    if (total == 0) { *out_count = 0; return NULL; }

    char **tags = (char **)calloc(total, sizeof(*tags));
    if (!tags) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    /* Distinct(OrdinalIgnoreCase): keep the first-seen spelling of each tag. */
    for (size_t i = 0; i < b->w_count; ++i) {
        for (size_t t = 0; t < b->works[i].tag_count; ++t) {
            const char *tag = b->works[i].tags[t];
            bool seen = false;
            for (size_t k = 0; k < n; ++k)
                if (cab_ci_eq(tags[k], tag)) { seen = true; break; }
            if (seen) continue;
            char *dup = cab_strdup_empty(tag);
            if (!dup) { cab_strv_free(tags, n); *out_count = (size_t)-1; return NULL; }
            tags[n++] = dup;
        }
    }
    if (n == 0) { free(tags); *out_count = 0; return NULL; }

    /* OrderBy(t, OrdinalIgnoreCase), stable insertion sort. */
    for (size_t i = 1; i < n; ++i) {
        char *cur = tags[i];
        size_t j = i;
        while (j > 0 && cab_ci_cmp(tags[j - 1], cur) > 0) {
            tags[j] = tags[j - 1]; --j;
        }
        tags[j] = cur;
    }
    *out_count = n;
    return tags;
}
