/*
 * test_feedback_analyser.c — FeedbackAnalyser + InMemoryFeedbackStore (C11).
 *
 * Mirrors the verified TypeScript feedback_analyser.test.ts and the C#
 * FeedbackAnalyser rules. The FP32 delta constants (-0.1f, +0.05f) are asserted
 * exactly.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* monotonic default timestamps so ordering is deterministic per call */
static int64_t g_seq = 0;

static ca_feedback_signal_rec_t make(ca_feedback_polarity_t pol, int64_t at_ms, const char *user) {
    ca_feedback_signal_rec_t s = {0};
    s.id = NULL;
    s.recorded_at_ms = at_ms >= 0 ? at_ms : (1700000000000LL + (g_seq++) * 1000);
    s.user_text = (char *)user;      /* borrowed — analyse copies internally */
    s.assistant_text = (char *)"response";
    s.polarity = pol;
    return s;
}

int main(void) {
    /* ── FeedbackAnalyser ── */

    /* rejects a window size below 1 */
    assert(ca_feedback_analyser_create(0) == NULL);

    ca_feedback_analyser_t *a20 = ca_feedback_analyser_create(20);
    assert(a20);

    /* empty signal set → zero deltas */
    {
        ca_persona_adaptation_t out;
        ca_feedback_analyser_analyse(a20, NULL, 0, &out);
        assert(out.verbosity_delta == 0.0f);
        assert(out.formality_delta == 0.0f);
        assert(out.topic_count == 0 && out.preferred_topics == NULL);
        ca_persona_adaptation_free(&out);
    }

    /* > 70% negative → -0.1f  (8 neg + 2 pos) */
    {
        ca_feedback_signal_rec_t sig[10];
        for (int i=0;i<8;++i) sig[i] = make(CA_FEEDBACK_POLARITY_NEGATIVE, -1, "user");
        for (int i=8;i<10;++i) sig[i] = make(CA_FEEDBACK_POLARITY_POSITIVE, -1, "user");
        ca_persona_adaptation_t out;
        ca_feedback_analyser_analyse(a20, sig, 10, &out);
        assert(out.verbosity_delta == -0.1f);
        assert(out.formality_delta == 0.0f);
        assert(out.topic_count == 0);
        ca_persona_adaptation_free(&out);
    }

    /* > 70% positive → +0.05f  (8 pos + 2 neg) */
    {
        ca_feedback_signal_rec_t sig[10];
        for (int i=0;i<8;++i) sig[i] = make(CA_FEEDBACK_POLARITY_POSITIVE, -1, "user");
        for (int i=8;i<10;++i) sig[i] = make(CA_FEEDBACK_POLARITY_NEGATIVE, -1, "user");
        ca_persona_adaptation_t out;
        ca_feedback_analyser_analyse(a20, sig, 10, &out);
        assert(out.verbosity_delta == 0.05f);
        ca_persona_adaptation_free(&out);
    }

    /* balanced → 0 (5 pos + 5 neg) */
    {
        ca_feedback_signal_rec_t sig[10];
        for (int i=0;i<5;++i) sig[i] = make(CA_FEEDBACK_POLARITY_POSITIVE, -1, "user");
        for (int i=5;i<10;++i) sig[i] = make(CA_FEEDBACK_POLARITY_NEGATIVE, -1, "user");
        ca_persona_adaptation_t out;
        ca_feedback_analyser_analyse(a20, sig, 10, &out);
        assert(out.verbosity_delta == 0.0f);
        ca_persona_adaptation_free(&out);
    }

    /* exactly 70% is NOT > 70% (strict) — 7/10 negative with window 10 */
    {
        ca_feedback_analyser_t *a10 = ca_feedback_analyser_create(10);
        ca_feedback_signal_rec_t sig[10];
        for (int i=0;i<7;++i) sig[i] = make(CA_FEEDBACK_POLARITY_NEGATIVE, -1, "user");
        for (int i=7;i<10;++i) sig[i] = make(CA_FEEDBACK_POLARITY_POSITIVE, -1, "user");
        ca_persona_adaptation_t out;
        ca_feedback_analyser_analyse(a10, sig, 10, &out);
        assert(out.verbosity_delta == 0.0f);
        ca_persona_adaptation_free(&out);
        ca_feedback_analyser_destroy(a10);
    }

    /* only the most-recent windowSize (newest-first): older bulk positive, 3
     * newest negative → window 3 is 100% negative → down */
    {
        ca_feedback_analyser_t *a3 = ca_feedback_analyser_create(3);
        ca_feedback_signal_rec_t sig[13];
        for (int i=0;i<10;++i) sig[i] = make(CA_FEEDBACK_POLARITY_POSITIVE, 1000 + i, "user");
        for (int i=0;i<3;++i)  sig[10+i] = make(CA_FEEDBACK_POLARITY_NEGATIVE, 9000000 + i, "user");
        ca_persona_adaptation_t out;
        ca_feedback_analyser_analyse(a3, sig, 13, &out);
        assert(out.verbosity_delta == -0.1f);
        ca_persona_adaptation_free(&out);
        ca_feedback_analyser_destroy(a3);
    }

    /* Correction signals ignored in the ratio: 8 neg + 2 correction = 8/10 → down */
    {
        ca_feedback_signal_rec_t sig[10];
        for (int i=0;i<8;++i) sig[i] = make(CA_FEEDBACK_POLARITY_NEGATIVE, -1, "user");
        for (int i=8;i<10;++i) sig[i] = make(CA_FEEDBACK_POLARITY_CORRECTION, -1, "user");
        ca_persona_adaptation_t out;
        ca_feedback_analyser_analyse(a20, sig, 10, &out);
        assert(out.verbosity_delta == -0.1f);
        ca_persona_adaptation_free(&out);
    }

    ca_feedback_analyser_destroy(a20);

    /* ── InMemoryFeedbackStore ── */

    /* rejects a non-positive maxSignals */
    assert(ca_feedback_store_create(0) == NULL);

    /* rejects a null signal */
    {
        ca_feedback_store_t *store = ca_feedback_store_create(100);
        assert(!ca_feedback_store_add(store, NULL));
        ca_feedback_store_destroy(store);
    }

    /* add increments the count */
    {
        ca_feedback_store_t *store = ca_feedback_store_create(100);
        ca_feedback_signal_rec_t s = make(CA_FEEDBACK_POLARITY_POSITIVE, -1, "user");
        assert(ca_feedback_store_add(store, &s));
        assert(ca_feedback_store_count(store) == 1);
        ca_feedback_store_destroy(store);
    }

    /* getRecent on an empty store returns empty */
    {
        ca_feedback_store_t *store = ca_feedback_store_create(100);
        size_t n;
        ca_feedback_signal_rec_t *r = ca_feedback_store_get_recent(store, 10, &n);
        assert(n == 0 && r == NULL);
        ca_feedback_store_destroy(store);
    }

    /* getRecent returns newest-first */
    {
        ca_feedback_store_t *store = ca_feedback_store_create(100);
        ca_feedback_signal_rec_t older = make(CA_FEEDBACK_POLARITY_POSITIVE, 1000, "old");
        ca_feedback_signal_rec_t newer = make(CA_FEEDBACK_POLARITY_NEGATIVE, 2000, "new");
        assert(ca_feedback_store_add(store, &older));
        assert(ca_feedback_store_add(store, &newer));
        size_t n;
        ca_feedback_signal_rec_t *r = ca_feedback_store_get_recent(store, 10, &n);
        assert(n == 2);
        assert(strcmp(r[0].user_text, "new") == 0);
        ca_feedback_signal_free_array(r, n);
        ca_feedback_store_destroy(store);
    }

    /* positiveRatio: null when empty */
    {
        ca_feedback_store_t *store = ca_feedback_store_create(100);
        double ratio;
        assert(!ca_feedback_store_positive_ratio(store, &ratio));
        ca_feedback_store_destroy(store);
    }

    /* positiveRatio: 1.0 when all positive */
    {
        ca_feedback_store_t *store = ca_feedback_store_create(100);
        ca_feedback_signal_rec_t s1 = make(CA_FEEDBACK_POLARITY_POSITIVE, -1, "u");
        ca_feedback_signal_rec_t s2 = make(CA_FEEDBACK_POLARITY_POSITIVE, -1, "u");
        ca_feedback_store_add(store, &s1);
        ca_feedback_store_add(store, &s2);
        double ratio;
        assert(ca_feedback_store_positive_ratio(store, &ratio));
        assert(ratio == 1.0);
        ca_feedback_store_destroy(store);
    }

    /* positiveRatio: 2/3 for mixed */
    {
        ca_feedback_store_t *store = ca_feedback_store_create(100);
        ca_feedback_signal_rec_t s1 = make(CA_FEEDBACK_POLARITY_POSITIVE, -1, "u");
        ca_feedback_signal_rec_t s2 = make(CA_FEEDBACK_POLARITY_POSITIVE, -1, "u");
        ca_feedback_signal_rec_t s3 = make(CA_FEEDBACK_POLARITY_NEGATIVE, -1, "u");
        ca_feedback_store_add(store, &s1);
        ca_feedback_store_add(store, &s2);
        ca_feedback_store_add(store, &s3);
        double ratio;
        assert(ca_feedback_store_positive_ratio(store, &ratio));
        assert(ratio > 0.66 && ratio < 0.68);
        ca_feedback_store_destroy(store);
    }

    /* FIFO eviction when maxSignals exceeded */
    {
        ca_feedback_store_t *store = ca_feedback_store_create(3);
        for (int i=0;i<5;++i) {
            ca_feedback_signal_rec_t s = make(CA_FEEDBACK_POLARITY_POSITIVE, -1, "u");
            ca_feedback_store_add(store, &s);
        }
        assert(ca_feedback_store_count(store) == 3);
        ca_feedback_store_destroy(store);
    }

    /* analyse a store directly */
    {
        ca_feedback_store_t *store = ca_feedback_store_create(100);
        for (int i=0;i<8;++i) { ca_feedback_signal_rec_t s = make(CA_FEEDBACK_POLARITY_NEGATIVE, -1, "u"); ca_feedback_store_add(store, &s); }
        for (int i=0;i<2;++i) { ca_feedback_signal_rec_t s = make(CA_FEEDBACK_POLARITY_POSITIVE, -1, "u"); ca_feedback_store_add(store, &s); }
        ca_feedback_analyser_t *an = ca_feedback_analyser_create(20);
        ca_persona_adaptation_t out;
        ca_feedback_analyser_analyse_store(an, store, &out);
        assert(out.verbosity_delta == -0.1f);
        ca_persona_adaptation_free(&out);
        ca_feedback_analyser_destroy(an);
        ca_feedback_store_destroy(store);
    }

    printf("test_feedback_analyser: all assertions passed\n");
    return 0;
}
