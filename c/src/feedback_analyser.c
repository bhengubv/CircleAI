/*
 * feedback_analyser.c — persona-adaptation deltas from feedback signals (C11).
 *
 * Ported from CircleAI.Memory.FeedbackAnalyser (C#) mirroring the verified
 * TypeScript reference 1:1. In-memory: dynamic arrays + linear search. The FP32
 * delta constants (-0.1f, +0.05f) are C `float` so they are byte-identical to
 * the C# `float` literals.
 */

#include "circle_ai/feedback_analyser.h"

#include <stdlib.h>
#include <string.h>

/* ── small shared helpers (file-local) ── */

static char *fb_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static void fb_copy_signal(ca_feedback_signal_rec_t *dst, const ca_feedback_signal_rec_t *src) {
    dst->id             = fb_dup(src->id);
    dst->recorded_at_ms = src->recorded_at_ms;
    dst->user_text      = fb_dup(src->user_text);
    dst->assistant_text = fb_dup(src->assistant_text);
    dst->polarity       = src->polarity;
}

/* ===========================================================================
 * FeedbackSignal
 * =========================================================================== */

void ca_feedback_signal_free(ca_feedback_signal_rec_t *sig) {
    if (!sig) return;
    free(sig->id);
    free(sig->user_text);
    free(sig->assistant_text);
    sig->id = sig->user_text = sig->assistant_text = NULL;
}

void ca_feedback_signal_free_array(ca_feedback_signal_rec_t *sigs, size_t count) {
    if (!sigs) return;
    for (size_t i = 0; i < count; ++i) ca_feedback_signal_free(&sigs[i]);
    free(sigs);
}

/* ===========================================================================
 * InMemoryFeedbackStore
 * =========================================================================== */

struct ca_feedback_store {
    ca_feedback_signal_rec_t *items;
    size_t                count;
    size_t                cap;
    size_t                max_signals;
};

ca_feedback_store_t *ca_feedback_store_create(size_t max_signals) {
    if (max_signals == 0) return NULL;
    ca_feedback_store_t *s = (ca_feedback_store_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->max_signals = max_signals;
    return s;
}

void ca_feedback_store_destroy(ca_feedback_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) ca_feedback_signal_free(&store->items[i]);
    free(store->items);
    free(store);
}

static bool fb_store_reserve(ca_feedback_store_t *s, size_t need) {
    if (need <= s->cap) return true;
    size_t ncap = s->cap ? s->cap * 2 : 8;
    while (ncap < need) ncap *= 2;
    ca_feedback_signal_rec_t *n = (ca_feedback_signal_rec_t *)realloc(s->items, ncap * sizeof(*n));
    if (!n) return false;
    s->items = n;
    s->cap = ncap;
    return true;
}

bool ca_feedback_store_add(ca_feedback_store_t *store, const ca_feedback_signal_rec_t *sig) {
    if (!store || !sig) return false;
    if (!fb_store_reserve(store, store->count + 1)) return false;
    fb_copy_signal(&store->items[store->count], sig);
    store->count++;
    /* FIFO eviction once over capacity. */
    if (store->count > store->max_signals) {
        ca_feedback_signal_free(&store->items[0]);
        memmove(&store->items[0], &store->items[1],
                (store->count - 1) * sizeof(store->items[0]));
        store->count--;
    }
    return true;
}

size_t ca_feedback_store_count(const ca_feedback_store_t *store) {
    return store ? store->count : 0;
}

/* stable insertion sort by recorded_at desc (newest-first). */
static void fb_sort_desc(ca_feedback_signal_rec_t *a, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        ca_feedback_signal_rec_t key = a[i];
        size_t j = i;
        while (j > 0 && a[j - 1].recorded_at_ms < key.recorded_at_ms) {
            a[j] = a[j - 1];
            --j;
        }
        a[j] = key;
    }
}

ca_feedback_signal_rec_t *ca_feedback_store_get_recent(const ca_feedback_store_t *store,
                                                   int count, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store || store->count == 0 || count <= 0) return NULL;

    /* Copy all, sort desc, truncate to count. */
    size_t n = store->count;
    ca_feedback_signal_rec_t *tmp = (ca_feedback_signal_rec_t *)malloc(n * sizeof(*tmp));
    if (!tmp) return NULL;
    for (size_t i = 0; i < n; ++i) fb_copy_signal(&tmp[i], &store->items[i]);
    fb_sort_desc(tmp, n);

    size_t take = (size_t)count < n ? (size_t)count : n;
    ca_feedback_signal_rec_t *out = (ca_feedback_signal_rec_t *)malloc(take * sizeof(*out));
    if (!out) { ca_feedback_signal_free_array(tmp, n); return NULL; }
    for (size_t i = 0; i < take; ++i) out[i] = tmp[i];       /* move ownership */
    for (size_t i = take; i < n; ++i) ca_feedback_signal_free(&tmp[i]);
    free(tmp);

    if (out_count) *out_count = take;
    return out;
}

bool ca_feedback_store_positive_ratio(const ca_feedback_store_t *store, double *out) {
    if (!store || store->count == 0) return false;
    size_t pos = 0;
    for (size_t i = 0; i < store->count; ++i)
        if (store->items[i].polarity == CA_FEEDBACK_POLARITY_POSITIVE) pos++;
    if (out) *out = (double)pos / (double)store->count;
    return true;
}

/* ===========================================================================
 * PersonaAdaptation
 * =========================================================================== */

void ca_persona_adaptation_free(ca_persona_adaptation_t *a) {
    if (!a) return;
    if (a->preferred_topics) {
        for (size_t i = 0; i < a->topic_count; ++i) free(a->preferred_topics[i]);
        free(a->preferred_topics);
    }
    a->preferred_topics = NULL;
    a->topic_count = 0;
}

/* ===========================================================================
 * FeedbackAnalyser
 * =========================================================================== */

struct ca_feedback_analyser {
    int window_size;
};

ca_feedback_analyser_t *ca_feedback_analyser_create(int window_size) {
    if (window_size < 1) return NULL;
    ca_feedback_analyser_t *a = (ca_feedback_analyser_t *)calloc(1, sizeof(*a));
    if (!a) return NULL;
    a->window_size = window_size;
    return a;
}

void ca_feedback_analyser_destroy(ca_feedback_analyser_t *a) {
    free(a);
}

void ca_feedback_analyser_analyse(const ca_feedback_analyser_t *a,
                                  const ca_feedback_signal_rec_t *signals, size_t count,
                                  ca_persona_adaptation_t *out) {
    if (!out) return;
    out->verbosity_delta = 0.0f;
    out->formality_delta = 0.0f;
    out->preferred_topics = NULL;
    out->topic_count = 0;
    if (!a) return;

    if (count == 0 || signals == NULL) return; /* empty → all-zero deltas */

    /* Sort a shallow index copy by recorded_at desc, take the window. */
    size_t n = count;
    ca_feedback_signal_rec_t *tmp = (ca_feedback_signal_rec_t *)malloc(n * sizeof(*tmp));
    if (!tmp) return;
    for (size_t i = 0; i < n; ++i) tmp[i] = signals[i];     /* shallow — borrows strings */
    fb_sort_desc(tmp, n);

    size_t window = (size_t)a->window_size < n ? (size_t)a->window_size : n;
    size_t pos = 0, neg = 0;
    for (size_t i = 0; i < window; ++i) {
        if (tmp[i].polarity == CA_FEEDBACK_POLARITY_POSITIVE) pos++;
        else if (tmp[i].polarity == CA_FEEDBACK_POLARITY_NEGATIVE) neg++;
    }
    free(tmp);

    if (window == 0) return;

    /* FP32 ratios exactly like C#: (float)count / total. */
    float negative_ratio = (float)neg / (float)window;
    float positive_ratio = (float)pos / (float)window;

    if (negative_ratio > 0.70f)      out->verbosity_delta = -0.1f;
    else if (positive_ratio > 0.70f) out->verbosity_delta = 0.05f;
    /* formality_delta stays 0; preferred_topics stays empty. */
}

void ca_feedback_analyser_analyse_store(const ca_feedback_analyser_t *a,
                                        const ca_feedback_store_t *store,
                                        ca_persona_adaptation_t *out) {
    if (!out) return;
    if (!store) {
        ca_feedback_analyser_analyse(a, NULL, 0, out);
        return;
    }
    /* ca_feedback_store is defined in this TU, so analyse its held array
     * directly (borrowing the signals — no copy needed). */
    ca_feedback_analyser_analyse(a, store->items, store->count, out);
}
