/*
 * testing.c — CircleAI.Testing (C11 port).
 *
 * Contracts.cs / InMemoryTesting.cs / NullImplementations.cs / TestingHelpers.cs:
 *   SnapshotDiff, InMemory/Null GoldenStore, LineDiff/Null SnapshotComparer,
 *   DeterministicIds.FromSeed, FrozenClock.
 *
 * Pure C11 + libc. Linear arrays (no hashtable), no pthreads.
 */

#include "circle_ai/testing.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* ── shared helpers (copied from media.c's md_* helpers) ──────────────────── */

static char *sb_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* string.IsNullOrWhiteSpace. */
static bool sb_is_ws(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (!isspace((unsigned char)*p)) return false;
    return true;
}

/* ===========================================================================
 * SnapshotDiff
 * =========================================================================== */

void ca_snapshot_diff_free(ca_snapshot_diff_t *d) {
    if (!d) return;
    free(d->diff);
    d->diff = NULL;
}

/* ===========================================================================
 * IGoldenStore — InMemoryGoldenStore + NullGoldenStore
 * =========================================================================== */

typedef struct {
    char *test_id;   /* owned key (Ordinal) */
    char *golden;    /* owned value */
} golden_entry_t;

struct ca_golden_store {
    bool            is_null;
    golden_entry_t *items;
    size_t          count, cap;
};

ca_golden_store_t *ca_golden_store_inmemory_create(void) {
    return (ca_golden_store_t *)calloc(1, sizeof(ca_golden_store_t));
}
ca_golden_store_t *ca_golden_store_null_create(void) {
    ca_golden_store_t *s = (ca_golden_store_t *)calloc(1, sizeof(*s));
    if (s) s->is_null = true;
    return s;
}
void ca_golden_store_destroy(ca_golden_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) {
        free(store->items[i].test_id);
        free(store->items[i].golden);
    }
    free(store->items);
    free(store);
}
const char *ca_golden_store_backend_id(const ca_golden_store_t *store) {
    if (!store) return NULL;
    return store->is_null ? "null" : "in-memory";
}

/* Find index of an entry by testId (Ordinal). SIZE_MAX if absent. */
static size_t golden_index_of(const ca_golden_store_t *store, const char *id) {
    for (size_t i = 0; i < store->count; ++i)
        if (strcmp(store->items[i].test_id, id) == 0) return i;
    return (size_t)-1;
}

char *ca_golden_store_read(ca_golden_store_t *store, const char *test_id) {
    if (!store) return NULL;
    if (store->is_null) return NULL;          /* NullGoldenStore -> null */
    /* InMemoryGoldenStore.ReadAsync throws ArgumentException on a bad testId; we
     * surface that as NULL (indistinguishable from "absent" for the caller). */
    if (sb_is_ws(test_id)) return NULL;
    size_t idx = golden_index_of(store, test_id);
    if (idx == (size_t)-1) return NULL;       /* TryGetValue -> false -> null */
    return sb_strdup(store->items[idx].golden);
}

int ca_golden_store_write(ca_golden_store_t *store, const char *test_id,
                          const char *golden) {
    if (!store) return -1;
    if (store->is_null) return 0;             /* NullGoldenStore -> CompletedTask */
    if (sb_is_ws(test_id)) return -1;         /* ArgumentException("testId required") */
    if (!golden) return -1;                   /* ArgumentNullException(golden) */

    char *gcopy = sb_strdup(golden);
    if (!gcopy) return -1;

    size_t idx = golden_index_of(store, test_id);
    if (idx != (size_t)-1) {
        /* Dictionary set: replace the value in place. */
        free(store->items[idx].golden);
        store->items[idx].golden = gcopy;
        return 0;
    }
    char *idcopy = sb_strdup(test_id);
    if (!idcopy) { free(gcopy); return -1; }

    if (store->count == store->cap) {
        size_t nc = store->cap ? store->cap * 2 : 4;
        void *n = realloc(store->items, nc * sizeof(*store->items));
        if (!n) { free(gcopy); free(idcopy); return -1; }
        store->items = (golden_entry_t *)n;
        store->cap = nc;
    }
    store->items[store->count].test_id = idcopy;
    store->items[store->count].golden  = gcopy;
    store->count++;
    return 0;
}

/* ===========================================================================
 * ISnapshotComparer — LineDiffSnapshotComparer + NullSnapshotComparer
 * =========================================================================== */

struct ca_snapshot_comparer {
    bool               is_null;
    ca_golden_store_t *store;   /* borrowed (line-diff only) */
};

ca_snapshot_comparer_t *ca_snapshot_comparer_linediff_create(ca_golden_store_t *store) {
    if (!store) return NULL;   /* ArgumentNullException(store) */
    ca_snapshot_comparer_t *c = (ca_snapshot_comparer_t *)calloc(1, sizeof(*c));
    if (c) c->store = store;
    return c;
}
ca_snapshot_comparer_t *ca_snapshot_comparer_null_create(void) {
    ca_snapshot_comparer_t *c = (ca_snapshot_comparer_t *)calloc(1, sizeof(*c));
    if (c) c->is_null = true;
    return c;
}
void ca_snapshot_comparer_destroy(ca_snapshot_comparer_t *cmp) {
    /* The store is borrowed — not freed here. */
    free(cmp);
}
const char *ca_snapshot_comparer_backend_id(const ca_snapshot_comparer_t *cmp) {
    if (!cmp) return NULL;
    return cmp->is_null ? "null" : "line-diff";
}

/*
 * Normalise(s): CRLF -> LF, lone CR -> LF, split on '\n', TrimEnd each line,
 * re-join with '\n'. Returns a freshly-allocated string (caller frees), NULL on
 * OOM. Length is bounded by strlen(s) (we only ever drop bytes), so a single
 * strlen(s)+1 buffer always fits.
 */
static char *linediff_normalise(const char *s) {
    size_t n = strlen(s);
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;

    size_t w = 0;                 /* write cursor */
    size_t line_start = 0;        /* index in out where the current line began */
    for (size_t i = 0; i < n; ++i) {
        char c = s[i];
        if (c == '\r') {
            if (i + 1 < n && s[i + 1] == '\n') i++;  /* consume the CRLF pair */
            c = '\n';
        }
        if (c == '\n') {
            /* TrimEnd the line just completed. */
            while (w > line_start && isspace((unsigned char)out[w - 1])) w--;
            out[w++] = '\n';
            line_start = w;
        } else {
            out[w++] = c;
        }
    }
    /* TrimEnd the final (unterminated) line. */
    while (w > line_start && isspace((unsigned char)out[w - 1])) w--;
    out[w] = '\0';
    return out;
}

/*
 * BuildDiff(expected, actual): split both on '\n'; for each line index up to
 * max(len_e, len_a), when the lines differ (Ordinal) append "-<e>\n+<a>\n".
 * Operates directly over the NUL-terminated normalised strings. Returns a freshly-
 * allocated string (caller frees), NULL on OOM.
 */
static char *linediff_build_diff(const char *expected, const char *actual) {
    size_t cap = 16, len = 0;
    char *buf = (char *)malloc(cap);
    if (!buf) return NULL;
    buf[0] = '\0';

    const char *ep = expected, *ap = actual;
    bool e_done = false, a_done = false;
    while (!e_done || !a_done) {
        /* Current line spans [line, seg) in each string. */
        const char *e_line = ep, *a_line = ap;
        size_t e_len = 0, a_len = 0;
        if (!e_done) {
            while (ep[e_len] && ep[e_len] != '\n') e_len++;
        }
        if (!a_done) {
            while (ap[a_len] && ap[a_len] != '\n') a_len++;
        }

        bool equal = (e_len == a_len) && (memcmp(e_line, a_line, e_len) == 0);
        if (!equal) {
            /* Need room for '-' + e_len + '\n' + '+' + a_len + '\n' + NUL. */
            size_t need = len + e_len + a_len + 5;
            if (need > cap) {
                while (cap < need) cap *= 2;
                char *nb = (char *)realloc(buf, cap);
                if (!nb) { free(buf); return NULL; }
                buf = nb;
            }
            buf[len++] = '-';
            memcpy(buf + len, e_line, e_len); len += e_len;
            buf[len++] = '\n';
            buf[len++] = '+';
            memcpy(buf + len, a_line, a_len); len += a_len;
            buf[len++] = '\n';
            buf[len] = '\0';
        }

        /* Advance to the next line, or mark the side done. A trailing '\n' means
         * one more (empty) line follows — mirror C# String.Split. */
        if (!e_done) {
            ep += e_len;
            if (*ep == '\n') ep++; else e_done = true;
        }
        if (!a_done) {
            ap += a_len;
            if (*ap == '\n') ap++; else a_done = true;
        }
    }
    return buf;
}

bool ca_snapshot_comparer_compare(ca_snapshot_comparer_t *cmp, const char *test_id,
                                  const char *actual, ca_snapshot_diff_t *out) {
    if (out) { out->equal = false; out->diff = NULL; }
    if (!cmp || !out) return false;

    if (cmp->is_null) {
        out->equal = false;
        out->diff  = sb_strdup("NullSnapshotComparer — no golden store wired.");
        return out->diff != NULL;
    }

    /* line-diff arg guards (ArgumentException / ArgumentNullException). */
    if (sb_is_ws(test_id) || !actual) return false;

    char *golden = ca_golden_store_read(cmp->store, test_id);
    if (!golden) {
        out->equal = false;
        out->diff  = sb_strdup("(no golden)");
        return out->diff != NULL;
    }

    char *a_norm = linediff_normalise(actual);
    char *g_norm = linediff_normalise(golden);
    free(golden);
    if (!a_norm || !g_norm) { free(a_norm); free(g_norm); return false; }

    if (strcmp(a_norm, g_norm) == 0) {
        out->equal = true;
        out->diff  = NULL;
        free(a_norm); free(g_norm);
        return true;
    }

    char *diff = linediff_build_diff(g_norm, a_norm);
    free(a_norm); free(g_norm);
    if (!diff) return false;
    out->equal = false;
    out->diff  = diff;
    return true;
}

/* ===========================================================================
 * DeterministicIds
 * =========================================================================== */

char *ca_deterministic_id_from_seed(const char *seed, const char *prefix) {
    if (sb_is_ws(seed)) return NULL;   /* ArgumentException("seed required") */
    if (!prefix) prefix = "test";      /* C# default parameter */

    /* FNV-1a 32-bit (unchecked uint wraparound). The C# `foreach (var c in seed)`
     * iterates UTF-16 chars; for ASCII seeds each byte is one char, which is the
     * expected/tested case. */
    uint32_t h = 2166136261u;
    for (const unsigned char *p = (const unsigned char *)seed; *p; ++p) {
        h ^= (uint32_t)*p;
        h *= 16777619u;
    }

    /* "<prefix>-<h:x8>" */
    size_t plen = strlen(prefix);
    size_t need = plen + 1 /* '-' */ + 8 /* hex */ + 1 /* NUL */;
    char *out = (char *)malloc(need);
    if (!out) return NULL;
    snprintf(out, need, "%s-%08x", prefix, (unsigned)h);
    return out;
}

/* ===========================================================================
 * FrozenClock
 * =========================================================================== */

void ca_frozen_clock_init(ca_frozen_clock_t *clk, int64_t start_ms) {
    if (clk) clk->now_ms = start_ms;
}
int64_t ca_frozen_clock_now(const ca_frozen_clock_t *clk) {
    return clk ? clk->now_ms : 0;
}
void ca_frozen_clock_advance(ca_frozen_clock_t *clk, int64_t delta_ms) {
    if (clk) clk->now_ms += delta_ms;
}
void ca_frozen_clock_set_to(ca_frozen_clock_t *clk, int64_t to_ms) {
    if (clk) clk->now_ms = to_ms;
}
