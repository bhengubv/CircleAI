/*
 * personal_mental.c — CircleAI.Personal.Mental (C11 port of
 * PersonalMentalPrimitives.cs).
 *
 * InMemoryMentalHealthBoard: moods in an appended list, journal entries +
 * strategies in id-keyed linear stores. Per-user instance only. AvgMood7Day
 * returns NaN when the 7-day window is empty. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/personal_mental.h"
#include "board_common.h"

#include <math.h>

/* ── MoodLog ────────────────────────────────────────────────────────────── */

void ca_mental_mood_log_free(ca_mental_mood_log_t *m) {
    if (!m) return;
    free(m->note);
    m->note = NULL;
}
void ca_mental_mood_log_free_array(ca_mental_mood_log_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_mental_mood_log_free(&arr[i]);
    free(arr);
}

static bool mood_log_copy(ca_mental_mood_log_t *dst,
                          const ca_mental_mood_log_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->mood      = src->mood;
    dst->at_utc_ms = src->at_utc_ms;
    dst->has_note  = src->has_note;
    if (src->has_note) {
        dst->note = cab_strdup_empty(src->note);
        if (!dst->note) return false;
    }
    return true;
}

/* ── JournalEntry ───────────────────────────────────────────────────────── */

void ca_mental_journal_entry_free(ca_mental_journal_entry_t *e) {
    if (!e) return;
    free(e->entry_id);
    free(e->title);
    free(e->body);
    e->entry_id = e->title = e->body = NULL;
}
void ca_mental_journal_entry_free_array(ca_mental_journal_entry_t *arr,
                                        size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_mental_journal_entry_free(&arr[i]);
    free(arr);
}

static bool journal_entry_copy(ca_mental_journal_entry_t *dst,
                               const ca_mental_journal_entry_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->entry_id = cab_strdup_empty(src->entry_id);
    dst->title    = cab_strdup_empty(src->title);
    dst->body     = cab_strdup_empty(src->body);
    dst->at_utc_ms = src->at_utc_ms;
    if (!dst->entry_id || !dst->title || !dst->body) {
        ca_mental_journal_entry_free(dst);
        return false;
    }
    return true;
}

/* ── CopingStrategy ─────────────────────────────────────────────────────── */

void ca_mental_strategy_free(ca_mental_strategy_t *s) {
    if (!s) return;
    free(s->strategy_id);
    free(s->title);
    free(s->description);
    cab_strv_free(s->tags, s->tag_count);
    s->strategy_id = s->title = s->description = NULL;
    s->tags = NULL;
    s->tag_count = 0;
}
void ca_mental_strategy_free_array(ca_mental_strategy_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_mental_strategy_free(&arr[i]);
    free(arr);
}

static bool strategy_copy(ca_mental_strategy_t *dst,
                          const ca_mental_strategy_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->strategy_id = cab_strdup_empty(src->strategy_id);
    dst->title       = cab_strdup_empty(src->title);
    dst->description = cab_strdup_empty(src->description);
    if (!dst->strategy_id || !dst->title || !dst->description) {
        ca_mental_strategy_free(dst);
        return false;
    }
    if (!cab_strv_copy(&dst->tags, src->tags, src->tag_count)) {
        ca_mental_strategy_free(dst);
        return false;
    }
    dst->tag_count = src->tag_count;
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_mental_board {
    ca_mental_mood_log_t      *moods;
    size_t                     mood_count, mood_cap;
    ca_mental_journal_entry_t *entries;
    size_t                     entry_count, entry_cap;
    ca_mental_strategy_t      *strats;
    size_t                     strat_count, strat_cap;
};

ca_mental_board_t *ca_mental_board_create(void) {
    return (ca_mental_board_t *)calloc(1, sizeof(ca_mental_board_t));
}
void ca_mental_board_destroy(ca_mental_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->mood_count; ++i)  ca_mental_mood_log_free(&b->moods[i]);
    for (size_t i = 0; i < b->entry_count; ++i) ca_mental_journal_entry_free(&b->entries[i]);
    for (size_t i = 0; i < b->strat_count; ++i) ca_mental_strategy_free(&b->strats[i]);
    free(b->moods);
    free(b->entries);
    free(b->strats);
    free(b);
}

int ca_mental_board_log_mood(ca_mental_board_t *b,
                             const ca_mental_mood_log_t *m) {
    if (!b || !m) return -1;
    ca_mental_mood_log_t copy;
    if (!mood_log_copy(&copy, m)) return -1;
    if (b->mood_count == b->mood_cap) {
        size_t nc = b->mood_cap ? b->mood_cap * 2 : 4;
        void *n = realloc(b->moods, nc * sizeof(*b->moods));
        if (!n) { ca_mental_mood_log_free(&copy); return -1; }
        b->moods = (ca_mental_mood_log_t *)n;
        b->mood_cap = nc;
    }
    b->moods[b->mood_count++] = copy;
    return 0;
}

/* Collect indices of moods within the 7-day window, ascending by AtUtc. Returns
 * an owned index array (*out_n); NULL when none (or on OOM -> *out_n SIZE_MAX). */
static size_t *collect_last7(const ca_mental_board_t *b, int64_t now_ms,
                             size_t *out_n) {
    int64_t cutoff = now_ms - CA_MENTAL_7DAY_MS;
    if (b->mood_count == 0) { *out_n = 0; return NULL; }
    size_t *idx = (size_t *)malloc(b->mood_count * sizeof(size_t));
    if (!idx) { *out_n = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->mood_count; ++i)
        if (b->moods[i].at_utc_ms >= cutoff) idx[n++] = i;
    /* stable ascending sort by AtUtc. */
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->moods[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->moods[idx[j - 1]].at_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
    if (n == 0) { free(idx); *out_n = 0; return NULL; }
    *out_n = n;
    return idx;
}

ca_mental_mood_log_t *ca_mental_board_last_7_days(const ca_mental_board_t *b,
                                                  int64_t now_ms,
                                                  size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }

    size_t n = 0;
    size_t *idx = collect_last7(b, now_ms, &n);
    if (n == 0 || n == (size_t)-1) { *out_count = n; return NULL; }

    ca_mental_mood_log_t *out = (ca_mental_mood_log_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!mood_log_copy(&out[i], &b->moods[idx[i]])) {
            ca_mental_mood_log_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_mental_board_add_entry(ca_mental_board_t *b,
                              const ca_mental_journal_entry_t *e) {
    if (!b || !e) return -1;
    /* ArgumentException("EntryId required") on null/whitespace. */
    if (cab_is_ws(e->entry_id)) return 2;
    for (size_t i = 0; i < b->entry_count; ++i) {
        if (cab_ord_eq(b->entries[i].entry_id, e->entry_id)) {
            ca_mental_journal_entry_t copy;
            if (!journal_entry_copy(&copy, e)) return -1;
            ca_mental_journal_entry_free(&b->entries[i]);
            b->entries[i] = copy;
            return 0;
        }
    }
    ca_mental_journal_entry_t copy;
    if (!journal_entry_copy(&copy, e)) return -1;
    if (b->entry_count == b->entry_cap) {
        size_t nc = b->entry_cap ? b->entry_cap * 2 : 4;
        void *n = realloc(b->entries, nc * sizeof(*b->entries));
        if (!n) { ca_mental_journal_entry_free(&copy); return -1; }
        b->entries = (ca_mental_journal_entry_t *)n;
        b->entry_cap = nc;
    }
    b->entries[b->entry_count++] = copy;
    return 0;
}

/* Stable descending sort of collected entry indices by at_utc_ms. */
static void entry_sort_desc(const ca_mental_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->entries[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->entries[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_mental_journal_entry_t *ca_mental_board_entries(const ca_mental_board_t *b,
                                                   size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->entry_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->entry_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < b->entry_count; ++i) idx[i] = i;
    entry_sort_desc(b, idx, b->entry_count);

    ca_mental_journal_entry_t *out =
        (ca_mental_journal_entry_t *)calloc(b->entry_count, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < b->entry_count; ++i) {
        if (!journal_entry_copy(&out[i], &b->entries[idx[i]])) {
            ca_mental_journal_entry_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = b->entry_count;
    return out;
}

int ca_mental_board_register_strategy(ca_mental_board_t *b,
                                      const ca_mental_strategy_t *s) {
    if (!b || !s) return -1;
    for (size_t i = 0; i < b->strat_count; ++i) {
        if (cab_ord_eq(b->strats[i].strategy_id, s->strategy_id)) {
            ca_mental_strategy_t copy;
            if (!strategy_copy(&copy, s)) return -1;
            ca_mental_strategy_free(&b->strats[i]);
            b->strats[i] = copy;
            return 0;
        }
    }
    ca_mental_strategy_t copy;
    if (!strategy_copy(&copy, s)) return -1;
    if (b->strat_count == b->strat_cap) {
        size_t nc = b->strat_cap ? b->strat_cap * 2 : 4;
        void *n = realloc(b->strats, nc * sizeof(*b->strats));
        if (!n) { ca_mental_strategy_free(&copy); return -1; }
        b->strats = (ca_mental_strategy_t *)n;
        b->strat_cap = nc;
    }
    b->strats[b->strat_count++] = copy;
    return 0;
}

ca_mental_strategy_t *ca_mental_board_strategies_by_tag(const ca_mental_board_t *b,
                                                        const char *tag,
                                                        size_t *out_count) {
    if (!out_count) return NULL;
    /* ArgumentException on null/whitespace tag -> SIZE_MAX. */
    if (!b || cab_is_ws(tag)) { *out_count = (size_t)-1; return NULL; }
    if (b->strat_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->strat_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->strat_count; ++i)
        if (cab_strv_ci_contains(b->strats[i].tags, b->strats[i].tag_count, tag))
            idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_mental_strategy_t *out = (ca_mental_strategy_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!strategy_copy(&out[i], &b->strats[idx[i]])) {
            ca_mental_strategy_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

double ca_mental_board_avg_mood_7day(const ca_mental_board_t *b, int64_t now_ms) {
    if (!b) return NAN;
    size_t n = 0;
    size_t *idx = collect_last7(b, now_ms, &n);
    if (n == 0 || n == (size_t)-1) { free(idx); return NAN; }
    double sum = 0.0;
    for (size_t i = 0; i < n; ++i) sum += (double)(int)b->moods[idx[i]].mood;
    free(idx);
    return sum / (double)n;
}
