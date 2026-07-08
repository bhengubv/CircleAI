/*
 * test_consolidation.c — Hierarchical memory-consolidation subsystem (C11 port).
 *
 * Mirrors the verified TypeScript suite tests/consolidation.test.ts 1:1, with a
 * fixed injected clock and hand-built episodic entries so every deterministic
 * formula is asserted exactly. Covers: civil-date helpers, full cosine, daily
 * summary formulas (topic weights / dispersion / topicConcentration / salience),
 * daily-pass production + idempotency + today-exclusion, high-salience → core
 * promotion (≥0.80), weekly clustering's 2-day threshold + centroid, retention
 * pruning (7/30/365), the monthly persona delta (new-topic detection +
 * idempotency), OnDemand running every tier, and full-cosine store ranking.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* ── epoch-ms of a UTC instant, via days-from-civil (no timegm dependency) ── */
static int64_t civil_days(int y, int m, int d) {
    y -= (m <= 2);
    int64_t era = (y >= 0 ? y : y - 399) / 400;
    int64_t yoe = (int64_t)y - era * 400;
    int64_t doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
    int64_t doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + doe - 719468;
}
static int64_t iso_ms(int y, int mo, int d, int h, int mi, int s) {
    int64_t days = civil_days(y, mo, d);
    return ((days * 86400) + h * 3600 + mi * 60 + s) * 1000LL;
}

/* ── fixed clock ── */
static int64_t g_fixed_ms = 0;
static int64_t fixed_clock(void *user) { (void)user; return g_fixed_ms; }

/* ── episodic entry builder (embedding + optional single tag) ──
 *
 * Each entry needs its OWN 1-element tag key/value arrays: several entries can
 * live simultaneously in a stack array passed straight to summarize_day (no deep
 * copy before the read). A fixed pool of holders (static lifetime, no leaks)
 * gives each mk_entry call a distinct slot. Tag keys/values are string literals. */
#define TAG_POOL 256
static char *g_tag_k[TAG_POOL][1];
static char *g_tag_v[TAG_POOL][1];
static size_t g_tag_next = 0;

static ca_episodic_entry_t mk_entry(const char *id, int64_t recorded,
                                    const float *emb, size_t emb_len,
                                    const char *tag_key, const char *tag_val,
                                    const char *user_text, const char *assistant_text) {
    ca_episodic_entry_t e;
    memset(&e, 0, sizeof(e));
    e.id = (char *)id;
    e.recorded_at_ms = recorded;
    e.user_text = (char *)(user_text ? user_text : "u");
    e.assistant_text = (char *)(assistant_text ? assistant_text : "a");
    e.embedding = (float *)emb;
    e.embedding_len = emb ? emb_len : 0;
    if (tag_key) {
        size_t slot = g_tag_next++ % TAG_POOL;
        g_tag_k[slot][0] = (char *)tag_key;
        g_tag_v[slot][0] = (char *)tag_val;
        e.tag_keys = g_tag_k[slot];
        e.tag_values = g_tag_v[slot];
        e.tag_count = 1;
    }
    return e;
}

static ca_civil_date_t cd(int y, int m, int d) { ca_civil_date_t x = { y, m, d }; return x; }
static bool cd_eq(ca_civil_date_t a, ca_civil_date_t b) { return ca_civil_date_compare(a, b) == 0; }

int main(void) {
    /* ═════════════════ day helpers ═════════════════ */
    {
        assert(cd_eq(ca_civil_date_from_ms(iso_ms(2026, 6, 8, 23, 59, 59)), cd(2026, 6, 8)));
        assert(cd_eq(ca_civil_date_from_ms(iso_ms(2026, 1, 5, 0, 0, 0)), cd(2026, 1, 5)));

        assert(cd_eq(ca_civil_date_monday_of(cd(2026, 6, 8)), cd(2026, 6, 8)));  /* Mon → itself */
        assert(cd_eq(ca_civil_date_monday_of(cd(2026, 6, 14)), cd(2026, 6, 8))); /* Sun → prior Mon */
        assert(cd_eq(ca_civil_date_monday_of(cd(2026, 6, 10)), cd(2026, 6, 8))); /* Wed → Mon */

        assert(cd_eq(ca_civil_date_add_days(cd(2026, 6, 1), -1), cd(2026, 5, 31)));
        assert(cd_eq(ca_civil_date_add_days(cd(2026, 6, 30), 1), cd(2026, 7, 1)));

        assert(cd_eq(ca_civil_date_month_first(cd(2026, 6, 17)), cd(2026, 6, 1)));

        char buf[16];
        ca_civil_date_to_string(cd(2026, 6, 8), buf, sizeof(buf));
        assert(strcmp(buf, "2026-06-08") == 0);
    }

    /* ═════════════════ cosineFull ═════════════════ */
    {
        float a[2] = {1, 0}, b[2] = {0, 1}, c3[3] = {1, 0, 0}, z[2] = {0, 0};
        float p3[2] = {3, 0}, p7[2] = {7, 0};
        assert(ca_cosine_full(a, 2, a, 2) == 1);
        assert(ca_cosine_full(a, 2, b, 2) == 0);
        assert(fabs(ca_cosine_full(p3, 2, p7, 2) - 1) < 1e-12);
        assert(ca_cosine_full(a, 2, c3, 3) == 0);  /* length mismatch */
        assert(ca_cosine_full(z, 2, a, 2) == 0);   /* zero vector */
    }

    /* ═════════════════ summarizeDay formulas ═════════════════ */
    {
        g_fixed_ms = iso_ms(2026, 6, 2, 0, 0, 0);
        ca_heuristic_summarizer_t *s = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        float e10[2] = {1, 0}, e01[2] = {0, 1};
        ca_episodic_entry_t entries[3] = {
            mk_entry("a", iso_ms(2026, 6, 1, 12, 0, 0), e10, 2, "topic", "finance", NULL, NULL),
            mk_entry("b", iso_ms(2026, 6, 1, 12, 0, 0), e01, 2, "topic", "health", NULL, NULL),
            mk_entry("c", iso_ms(2026, 6, 1, 12, 0, 0), e10, 2, "topic", "finance", NULL, NULL),
        };
        ca_daily_summary_t d;
        ca_heuristic_summarizer_summarize_day(s, cd(2026, 6, 1), entries, 3, &d);
        assert(d.episode_count == 3);
        double fin = 0, hea = 0;
        assert(ca_topic_weights_get(&d.topic_weights, "finance", &fin) && fin == 2);
        assert(ca_topic_weights_get(&d.topic_weights, "health", &hea) && hea == 1);
        /* dispersion = (1 + 0 + 1)/3 = 2/3 */
        assert(fabs(d.topic_dispersion - 2.0 / 3.0) < 1e-12);
        /* salience = 0.1*0.4 + (2/3)*0.3 + (2/3)*0.3 = 0.44 */
        assert(fabs(d.salience - 0.44) < 1e-12);
        assert(strncmp(d.summary, "On 2026-06-01 you had 3 exchanges.",
                       strlen("On 2026-06-01 you had 3 exchanges.")) == 0);
        assert(strstr(d.summary, "Top topics: finance, health.") != NULL);
        ca_daily_summary_free(&d);
        ca_heuristic_summarizer_destroy(s);
    }

    /* pipe-delimited "topics" split + lowercase/trim */
    {
        ca_heuristic_summarizer_t *s = ca_heuristic_summarizer_create(0, 0, NULL, NULL);
        ca_episodic_entry_t e = mk_entry("x", 0, NULL, 0, "topics", "Finance | Health |finance", NULL, NULL);
        ca_daily_summary_t d;
        ca_heuristic_summarizer_summarize_day(s, cd(2026, 6, 1), &e, 1, &d);
        double fin = 0, hea = 0;
        assert(ca_topic_weights_get(&d.topic_weights, "finance", &fin) && fin == 2);
        assert(ca_topic_weights_get(&d.topic_weights, "health", &hea) && hea == 1);
        ca_daily_summary_free(&d);
        ca_heuristic_summarizer_destroy(s);
    }

    /* topicConcentration 0.5 when no topics; single-entry standout clause */
    {
        ca_heuristic_summarizer_t *s = ca_heuristic_summarizer_create(0, 0, NULL, NULL);
        ca_episodic_entry_t e = mk_entry("x", 0, NULL, 0, NULL, NULL, "u", "a");
        ca_daily_summary_t d;
        ca_heuristic_summarizer_summarize_day(s, cd(2026, 6, 1), &e, 1, &d);
        double expected = (1.0 / 30.0) * 0.4 + 0 * 0.3 + 0.5 * 0.3;
        assert(fabs(d.salience - expected) < 1e-12);
        assert(strcmp(d.summary, "On 2026-06-01 you had 1 exchange. Standout moment: \"u\".") == 0);
        assert(strstr(d.summary, "Top topics") == NULL);
        ca_daily_summary_free(&d);
        ca_heuristic_summarizer_destroy(s);
    }

    /* empty-day summary */
    {
        ca_heuristic_summarizer_t *s = ca_heuristic_summarizer_create(0, 0, NULL, NULL);
        ca_daily_summary_t d;
        ca_heuristic_summarizer_summarize_day(s, cd(2026, 6, 1), NULL, 0, &d);
        assert(d.episode_count == 0);
        assert(strcmp(d.summary, "No exchanges recorded on 2026-06-01.") == 0);
        ca_daily_summary_free(&d);
        ca_heuristic_summarizer_destroy(s);
    }

    /* ═════════════════ daily pass: production + idempotency + today-exclusion ═════════════════ */
    {
        g_fixed_ms = iso_ms(2026, 6, 8, 9, 0, 0);
        ca_episodic_store_t *ep = ca_episodic_store_create(100000);
        ca_daily_store_t *daily = ca_daily_store_create();
        ca_semantic_store_t *sem = ca_semantic_store_create();
        ca_persona_delta_store_t *pd = ca_persona_delta_store_create();
        ca_core_store_t *core = ca_core_store_create();
        ca_persona_store_t *ps = ca_persona_store_create();
        ca_heuristic_summarizer_t *sum = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        ca_memory_consolidator_t *con = ca_memory_consolidator_create(
            ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);

        ca_episodic_entry_t e1 = mk_entry("d1", iso_ms(2026, 6, 6, 10, 0, 0), NULL, 0, "topic", "x", NULL, NULL);
        ca_episodic_entry_t e2 = mk_entry("d2", iso_ms(2026, 6, 6, 11, 0, 0), NULL, 0, "topic", "x", NULL, NULL);
        ca_episodic_store_add(ep, &e1);
        ca_episodic_store_add(ep, &e2);

        ca_consolidation_outcome_t r1;
        ca_memory_consolidator_tick(con, CA_SLEEP_DAILY, &r1);
        assert(r1.daily_summaries_produced == 1);
        ca_daily_summary_t got;
        assert(ca_daily_store_get(daily, cd(2026, 6, 6), &got));
        assert(got.episode_count == 2);
        ca_daily_summary_free(&got);

        ca_consolidation_outcome_t r2;
        ca_memory_consolidator_tick(con, CA_SLEEP_DAILY, &r2);
        assert(r2.daily_summaries_produced == 0);           /* idempotent */
        assert(ca_daily_store_count(daily) == 1);

        ca_memory_consolidator_destroy(con);
        ca_heuristic_summarizer_destroy(sum);
        ca_persona_store_destroy(ps);
        ca_core_store_destroy(core);
        ca_persona_delta_store_destroy(pd);
        ca_semantic_store_destroy(sem);
        ca_daily_store_destroy(daily);
        ca_episodic_store_destroy(ep);
    }

    /* does NOT summarise today's (incomplete) day */
    {
        g_fixed_ms = iso_ms(2026, 6, 8, 9, 0, 0);
        ca_episodic_store_t *ep = ca_episodic_store_create(100000);
        ca_daily_store_t *daily = ca_daily_store_create();
        ca_semantic_store_t *sem = ca_semantic_store_create();
        ca_persona_delta_store_t *pd = ca_persona_delta_store_create();
        ca_core_store_t *core = ca_core_store_create();
        ca_persona_store_t *ps = ca_persona_store_create();
        ca_heuristic_summarizer_t *sum = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        ca_memory_consolidator_t *con = ca_memory_consolidator_create(
            ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);

        ca_episodic_entry_t e = mk_entry("t", iso_ms(2026, 6, 8, 8, 0, 0), NULL, 0, NULL, NULL, NULL, NULL);
        ca_episodic_store_add(ep, &e);
        ca_consolidation_outcome_t r;
        ca_memory_consolidator_tick(con, CA_SLEEP_DAILY, &r);
        assert(r.daily_summaries_produced == 0);
        assert(ca_daily_store_count(daily) == 0);

        ca_memory_consolidator_destroy(con);
        ca_heuristic_summarizer_destroy(sum);
        ca_persona_store_destroy(ps); ca_core_store_destroy(core);
        ca_persona_delta_store_destroy(pd); ca_semantic_store_destroy(sem);
        ca_daily_store_destroy(daily); ca_episodic_store_destroy(ep);
    }

    /* re-summarises a day when new episodes arrive (count mismatch) */
    {
        g_fixed_ms = iso_ms(2026, 6, 8, 9, 0, 0);
        ca_episodic_store_t *ep = ca_episodic_store_create(100000);
        ca_daily_store_t *daily = ca_daily_store_create();
        ca_semantic_store_t *sem = ca_semantic_store_create();
        ca_persona_delta_store_t *pd = ca_persona_delta_store_create();
        ca_core_store_t *core = ca_core_store_create();
        ca_persona_store_t *ps = ca_persona_store_create();
        ca_heuristic_summarizer_t *sum = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        ca_memory_consolidator_t *con = ca_memory_consolidator_create(
            ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);

        ca_episodic_entry_t p1 = mk_entry("p1", iso_ms(2026, 6, 6, 10, 0, 0), NULL, 0, NULL, NULL, NULL, NULL);
        ca_episodic_store_add(ep, &p1);
        ca_consolidation_outcome_t r0;
        ca_memory_consolidator_tick(con, CA_SLEEP_DAILY, &r0);
        ca_daily_summary_t g1; assert(ca_daily_store_get(daily, cd(2026, 6, 6), &g1));
        assert(g1.episode_count == 1); ca_daily_summary_free(&g1);

        ca_episodic_entry_t p2 = mk_entry("p2", iso_ms(2026, 6, 6, 12, 0, 0), NULL, 0, NULL, NULL, NULL, NULL);
        ca_episodic_store_add(ep, &p2);
        ca_consolidation_outcome_t r;
        ca_memory_consolidator_tick(con, CA_SLEEP_DAILY, &r);
        assert(r.daily_summaries_produced == 1);
        ca_daily_summary_t g2; assert(ca_daily_store_get(daily, cd(2026, 6, 6), &g2));
        assert(g2.episode_count == 2); ca_daily_summary_free(&g2);

        ca_memory_consolidator_destroy(con);
        ca_heuristic_summarizer_destroy(sum);
        ca_persona_store_destroy(ps); ca_core_store_destroy(core);
        ca_persona_delta_store_destroy(pd); ca_semantic_store_destroy(sem);
        ca_daily_store_destroy(daily); ca_episodic_store_destroy(ep);
    }

    /* ═════════════════ high-salience day → core promotion (≥0.80) ═════════════════ */
    {
        g_fixed_ms = iso_ms(2026, 6, 8, 9, 0, 0);
        ca_episodic_store_t *ep = ca_episodic_store_create(100000);
        ca_daily_store_t *daily = ca_daily_store_create();
        ca_semantic_store_t *sem = ca_semantic_store_create();
        ca_persona_delta_store_t *pd = ca_persona_delta_store_create();
        ca_core_store_t *core = ca_core_store_create();
        ca_persona_store_t *ps = ca_persona_store_create();
        ca_heuristic_summarizer_t *sum = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        ca_memory_consolidator_t *con = ca_memory_consolidator_create(
            ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);

        static float e10[2] = {1, 0}, e01[2] = {0, 1};
        char ids[30][8];
        for (int i = 0; i < 30; ++i) {
            snprintf(ids[i], sizeof(ids[i]), "h%d", i);
            ca_episodic_entry_t e = mk_entry(ids[i], iso_ms(2026, 6, 6, i % 24, 0, 0),
                                             i < 15 ? e10 : e01, 2, "topic", "finance", NULL, NULL);
            ca_episodic_store_add(ep, &e);
        }
        ca_consolidation_outcome_t r;
        ca_memory_consolidator_tick(con, CA_SLEEP_DAILY, &r);
        assert(r.daily_summaries_produced == 1);
        assert(r.core_promotions == 1);

        size_t cn = 0;
        ca_core_memory_t *all = ca_core_store_list_all(core, &cn);
        assert(cn == 1);
        assert(all[0].kind == CA_CORE_HIGH_SALIENCE);
        assert(all[0].topic && strcmp(all[0].topic, "finance") == 0);
        assert(strcmp(all[0].statement, "\"finance\" mattered enough on 2026-06-06 to be remembered.") == 0);
        assert(all[0].embedding != NULL);
        ca_core_memory_free_array(all, cn);

        ca_memory_consolidator_destroy(con);
        ca_heuristic_summarizer_destroy(sum);
        ca_persona_store_destroy(ps); ca_core_store_destroy(core);
        ca_persona_delta_store_destroy(pd); ca_semantic_store_destroy(sem);
        ca_daily_store_destroy(daily); ca_episodic_store_destroy(ep);
    }

    /* low-salience day is NOT promoted */
    {
        g_fixed_ms = iso_ms(2026, 6, 8, 9, 0, 0);
        ca_episodic_store_t *ep = ca_episodic_store_create(100000);
        ca_daily_store_t *daily = ca_daily_store_create();
        ca_semantic_store_t *sem = ca_semantic_store_create();
        ca_persona_delta_store_t *pd = ca_persona_delta_store_create();
        ca_core_store_t *core = ca_core_store_create();
        ca_persona_store_t *ps = ca_persona_store_create();
        ca_heuristic_summarizer_t *sum = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        ca_memory_consolidator_t *con = ca_memory_consolidator_create(
            ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);
        ca_episodic_entry_t e = mk_entry("x", iso_ms(2026, 6, 6, 10, 0, 0), NULL, 0, "topic", "x", NULL, NULL);
        ca_episodic_store_add(ep, &e);
        ca_consolidation_outcome_t r;
        ca_memory_consolidator_tick(con, CA_SLEEP_DAILY, &r);
        assert(r.core_promotions == 0);
        assert(ca_core_store_count(core) == 0);
        ca_memory_consolidator_destroy(con);
        ca_heuristic_summarizer_destroy(sum);
        ca_persona_store_destroy(ps); ca_core_store_destroy(core);
        ca_persona_delta_store_destroy(pd); ca_semantic_store_destroy(sem);
        ca_daily_store_destroy(daily); ca_episodic_store_destroy(ep);
    }

    /* ═════════════════ weekly clustering: 2-day threshold + salience + centroid ═════════════════ */
    {
        ca_heuristic_summarizer_t *s = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        g_fixed_ms = iso_ms(2026, 6, 8, 0, 0, 0);
        /* Build day1 (finance=1, health=1) and day2 (finance=1) by summarising
         * hand-made entries, so the topic-weight maps are real. */
        ca_episodic_entry_t d1e[2] = {
            mk_entry("d1a", iso_ms(2026, 6, 1, 9, 0, 0), NULL, 0, "topic", "finance", NULL, NULL),
            mk_entry("d1b", iso_ms(2026, 6, 1, 9, 0, 0), NULL, 0, "topic", "health", NULL, NULL),
        };
        ca_episodic_entry_t d2e[1] = {
            mk_entry("d2a", iso_ms(2026, 6, 2, 9, 0, 0), NULL, 0, "topic", "finance", NULL, NULL),
        };
        ca_daily_summary_t day1, day2;
        ca_heuristic_summarizer_summarize_day(s, cd(2026, 6, 1), d1e, 2, &day1);
        ca_heuristic_summarizer_summarize_day(s, cd(2026, 6, 2), d2e, 1, &day2);
        ca_daily_summary_t week[2] = { day1, day2 };

        size_t cn = 0;
        ca_semantic_cluster_t *cl = ca_heuristic_summarizer_consolidate_week(s, cd(2026, 6, 1), week, 2, &cn);
        assert(cn == 1);
        assert(strcmp(cl[0].topic, "finance") == 0);
        assert(cl[0].topic_weight == 2);
        /* salience = min(1, 2/3 + (2/7)*0.25) */
        assert(fabs(cl[0].salience - (2.0 / 3.0 + (2.0 / 7.0) * 0.25)) < 1e-12);
        assert(strcmp(cl[0].summary,
                      "Across 2 days this week you returned to \"finance\" — 3 exchanges in total.") == 0);
        assert(cl[0].source_daily_count == 2);
        ca_semantic_cluster_free_array(cl, cn);
        ca_daily_summary_free(&day1);
        ca_daily_summary_free(&day2);
        ca_heuristic_summarizer_destroy(s);
    }

    /* no clusters when every topic is single-day */
    {
        ca_heuristic_summarizer_t *s = ca_heuristic_summarizer_create(0, 0, NULL, NULL);
        ca_episodic_entry_t a = mk_entry("a", 0, NULL, 0, "topic", "a", NULL, NULL);
        ca_episodic_entry_t b = mk_entry("b", 0, NULL, 0, "topic", "b", NULL, NULL);
        ca_daily_summary_t d1, d2;
        ca_heuristic_summarizer_summarize_day(s, cd(2026, 6, 1), &a, 1, &d1);
        ca_heuristic_summarizer_summarize_day(s, cd(2026, 6, 2), &b, 1, &d2);
        ca_daily_summary_t week[2] = { d1, d2 };
        size_t cn = 0;
        ca_semantic_cluster_t *cl = ca_heuristic_summarizer_consolidate_week(s, cd(2026, 6, 1), week, 2, &cn);
        assert(cn == 0 && cl == NULL);
        ca_daily_summary_free(&d1); ca_daily_summary_free(&d2);
        ca_heuristic_summarizer_destroy(s);
    }

    /* centroid = mean of highlight embeddings ([2,0]+[0,4])/2 = [1,2] */
    {
        ca_heuristic_summarizer_t *s = ca_heuristic_summarizer_create(0, 0, NULL, NULL);
        float h1[2] = {2, 0}, h2[2] = {0, 4};
        ca_episodic_entry_t e1 = mk_entry("h1", iso_ms(2026, 6, 1, 9, 0, 0), h1, 2, "topic", "t", NULL, NULL);
        ca_episodic_entry_t e2 = mk_entry("h2", iso_ms(2026, 6, 2, 9, 0, 0), h2, 2, "topic", "t", NULL, NULL);
        ca_daily_summary_t d1, d2;
        ca_heuristic_summarizer_summarize_day(s, cd(2026, 6, 1), &e1, 1, &d1);
        ca_heuristic_summarizer_summarize_day(s, cd(2026, 6, 2), &e2, 1, &d2);
        ca_daily_summary_t week[2] = { d1, d2 };
        size_t cn = 0;
        ca_semantic_cluster_t *cl = ca_heuristic_summarizer_consolidate_week(s, cd(2026, 6, 1), week, 2, &cn);
        assert(cn == 1);
        assert(cl[0].centroid_len == 2);
        assert(cl[0].centroid_embedding[0] == 1.0f && cl[0].centroid_embedding[1] == 2.0f);
        ca_semantic_cluster_free_array(cl, cn);
        ca_daily_summary_free(&d1); ca_daily_summary_free(&d2);
        ca_heuristic_summarizer_destroy(s);
    }

    /* weekly pass: clusters last completed week + idempotency */
    {
        g_fixed_ms = iso_ms(2026, 6, 8, 9, 0, 0);
        ca_episodic_store_t *ep = ca_episodic_store_create(100000);
        ca_daily_store_t *daily = ca_daily_store_create();
        ca_semantic_store_t *sem = ca_semantic_store_create();
        ca_persona_delta_store_t *pd = ca_persona_delta_store_create();
        ca_core_store_t *core = ca_core_store_create();
        ca_persona_store_t *ps = ca_persona_store_create();
        ca_heuristic_summarizer_t *sum = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        ca_memory_consolidator_t *con = ca_memory_consolidator_create(
            ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);

        /* Two dailies in last week (06-01..06-07) sharing topic finance. */
        ca_episodic_entry_t a = mk_entry("a", iso_ms(2026, 6, 1, 9, 0, 0), NULL, 0, "topic", "finance", NULL, NULL);
        ca_episodic_entry_t b = mk_entry("b", iso_ms(2026, 6, 1, 9, 0, 0), NULL, 0, "topic", "finance", NULL, NULL);
        ca_episodic_entry_t c = mk_entry("c", iso_ms(2026, 6, 3, 9, 0, 0), NULL, 0, "topic", "finance", NULL, NULL);
        ca_daily_summary_t d1, d2;
        ca_heuristic_summarizer_summarize_day(sum, cd(2026, 6, 1), (ca_episodic_entry_t[]){a, b}, 2, &d1);
        ca_heuristic_summarizer_summarize_day(sum, cd(2026, 6, 3), &c, 1, &d2);
        ca_daily_store_upsert(daily, &d1);
        ca_daily_store_upsert(daily, &d2);
        ca_daily_summary_free(&d1); ca_daily_summary_free(&d2);

        ca_consolidation_outcome_t r1;
        ca_memory_consolidator_tick(con, CA_SLEEP_WEEKLY, &r1);
        assert(r1.semantic_clusters_produced == 1);
        assert(ca_semantic_store_count(sem) == 1);

        ca_consolidation_outcome_t r2;
        ca_memory_consolidator_tick(con, CA_SLEEP_WEEKLY, &r2);
        assert(r2.semantic_clusters_produced == 0);  /* getWeek non-empty → skip */
        assert(ca_semantic_store_count(sem) == 1);

        ca_memory_consolidator_destroy(con);
        ca_heuristic_summarizer_destroy(sum);
        ca_persona_store_destroy(ps); ca_core_store_destroy(core);
        ca_persona_delta_store_destroy(pd); ca_semantic_store_destroy(sem);
        ca_daily_store_destroy(daily); ca_episodic_store_destroy(ep);
    }

    /* ═════════════════ retention pruning ═════════════════ */
    {
        /* episodic > 7 days pruned on daily pass. cutoff = now - 7d = 2026-06-01T09:00Z */
        g_fixed_ms = iso_ms(2026, 6, 8, 9, 0, 0);
        ca_episodic_store_t *ep = ca_episodic_store_create(100000);
        ca_daily_store_t *daily = ca_daily_store_create();
        ca_semantic_store_t *sem = ca_semantic_store_create();
        ca_persona_delta_store_t *pd = ca_persona_delta_store_create();
        ca_core_store_t *core = ca_core_store_create();
        ca_persona_store_t *ps = ca_persona_store_create();
        ca_heuristic_summarizer_t *sum = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        ca_memory_consolidator_t *con = ca_memory_consolidator_create(
            ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);
        ca_episodic_entry_t old = mk_entry("old", iso_ms(2026, 5, 20, 0, 0, 0), NULL, 0, NULL, NULL, NULL, NULL);
        ca_episodic_entry_t fresh = mk_entry("fresh", iso_ms(2026, 6, 6, 0, 0, 0), NULL, 0, NULL, NULL, NULL, NULL);
        ca_episodic_store_add(ep, &old);
        ca_episodic_store_add(ep, &fresh);
        ca_consolidation_outcome_t r;
        ca_memory_consolidator_tick(con, CA_SLEEP_DAILY, &r);
        assert(r.episodes_pruned == 1);
        assert(ca_episodic_store_count(ep) == 1);
        ca_memory_consolidator_destroy(con);
        ca_heuristic_summarizer_destroy(sum);
        ca_persona_store_destroy(ps); ca_core_store_destroy(core);
        ca_persona_delta_store_destroy(pd); ca_semantic_store_destroy(sem);
        ca_daily_store_destroy(daily); ca_episodic_store_destroy(ep);
    }
    {
        /* dailies > 30 days pruned on weekly pass. cutoff = 2026-05-09. */
        g_fixed_ms = iso_ms(2026, 6, 8, 9, 0, 0);
        ca_episodic_store_t *ep = ca_episodic_store_create(100000);
        ca_daily_store_t *daily = ca_daily_store_create();
        ca_semantic_store_t *sem = ca_semantic_store_create();
        ca_persona_delta_store_t *pd = ca_persona_delta_store_create();
        ca_core_store_t *core = ca_core_store_create();
        ca_persona_store_t *ps = ca_persona_store_create();
        ca_heuristic_summarizer_t *sum = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        ca_memory_consolidator_t *con = ca_memory_consolidator_create(
            ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);
        ca_daily_summary_t old, keep;
        ca_heuristic_summarizer_summarize_day(sum, cd(2026, 4, 1), NULL, 0, &old);   /* < cutoff → pruned */
        ca_heuristic_summarizer_summarize_day(sum, cd(2026, 6, 3), NULL, 0, &keep);  /* kept */
        ca_daily_store_upsert(daily, &old);
        ca_daily_store_upsert(daily, &keep);
        ca_daily_summary_free(&old); ca_daily_summary_free(&keep);
        ca_consolidation_outcome_t r;
        ca_memory_consolidator_tick(con, CA_SLEEP_WEEKLY, &r);
        assert(r.dailies_pruned == 1);
        ca_daily_summary_t chk;
        assert(!ca_daily_store_get(daily, cd(2026, 4, 1), &chk));
        assert(ca_daily_store_get(daily, cd(2026, 6, 3), &chk)); ca_daily_summary_free(&chk);
        ca_memory_consolidator_destroy(con);
        ca_heuristic_summarizer_destroy(sum);
        ca_persona_store_destroy(ps); ca_core_store_destroy(core);
        ca_persona_delta_store_destroy(pd); ca_semantic_store_destroy(sem);
        ca_daily_store_destroy(daily); ca_episodic_store_destroy(ep);
    }
    {
        /* semantic clusters > 365 days pruned on monthly pass. cutoff = 2025-06-08. */
        g_fixed_ms = iso_ms(2026, 6, 8, 9, 0, 0);
        ca_episodic_store_t *ep = ca_episodic_store_create(100000);
        ca_daily_store_t *daily = ca_daily_store_create();
        ca_semantic_store_t *sem = ca_semantic_store_create();
        ca_persona_delta_store_t *pd = ca_persona_delta_store_create();
        ca_core_store_t *core = ca_core_store_create();
        ca_persona_store_t *ps = ca_persona_store_create();
        ca_heuristic_summarizer_t *sum = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        ca_memory_consolidator_t *con = ca_memory_consolidator_create(
            ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);
        /* Build two clusters via the summarizer, then re-home their week starts. */
        ca_semantic_cluster_t oldc, newc;
        memset(&oldc, 0, sizeof(oldc)); memset(&newc, 0, sizeof(newc));
        oldc.week_starting_monday = cd(2024, 1, 1); oldc.topic = (char *)"t"; oldc.summary = (char *)"";
        newc.week_starting_monday = cd(2026, 5, 4); newc.topic = (char *)"t"; newc.summary = (char *)"";
        ca_semantic_store_add(sem, &oldc);
        ca_semantic_store_add(sem, &newc);
        ca_consolidation_outcome_t r;
        ca_memory_consolidator_tick(con, CA_SLEEP_MONTHLY, &r);
        assert(r.semantics_pruned == 1);
        assert(ca_semantic_store_count(sem) == 1);
        ca_memory_consolidator_destroy(con);
        ca_heuristic_summarizer_destroy(sum);
        ca_persona_store_destroy(ps); ca_core_store_destroy(core);
        ca_persona_delta_store_destroy(pd); ca_semantic_store_destroy(sem);
        ca_daily_store_destroy(daily); ca_episodic_store_destroy(ep);
    }

    /* ═════════════════ monthly persona delta ═════════════════ */
    {
        /* previous month = May 2026 (2026-05-01..2026-05-31). */
        g_fixed_ms = iso_ms(2026, 6, 8, 9, 0, 0);
        ca_episodic_store_t *ep = ca_episodic_store_create(100000);
        ca_daily_store_t *daily = ca_daily_store_create();
        ca_semantic_store_t *sem = ca_semantic_store_create();
        ca_persona_delta_store_t *pd = ca_persona_delta_store_create();
        ca_core_store_t *core = ca_core_store_create();
        ca_persona_store_t *ps = ca_persona_store_create();
        ca_heuristic_summarizer_t *sum = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        ca_memory_consolidator_t *con = ca_memory_consolidator_create(
            ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);

        ca_daily_summary_t may;
        ca_heuristic_summarizer_summarize_day(sum, cd(2026, 5, 15), NULL, 0, &may);
        may.episode_count = 4;
        ca_daily_store_upsert(daily, &may);
        ca_daily_summary_free(&may);

        ca_consolidation_persona_t *after = ca_consolidation_persona_create("default");
        ca_consolidation_persona_set_topic(after, "finance", 3);
        after->total_interactions = 10;
        after->positive_signals = 6;
        after->negative_signals = 1;
        ca_persona_store_save(ps, after);
        ca_consolidation_persona_destroy(after);

        ca_consolidation_outcome_t r1;
        ca_memory_consolidator_tick(con, CA_SLEEP_MONTHLY, &r1);
        assert(r1.persona_deltas_produced == 1);
        size_t dn = 0;
        ca_persona_delta_t *deltas = ca_persona_delta_store_get_for_user(pd, "default", &dn);
        assert(dn == 1);
        double fin = 0;
        assert(ca_topic_weights_get(&deltas[0].new_topics, "finance", &fin) && fin == 3);
        assert(cd_eq(deltas[0].period_start, cd(2026, 5, 15)));
        assert(cd_eq(deltas[0].period_end, cd(2026, 5, 15)));
        assert(strstr(deltas[0].narrative, "New interests appeared: finance.") != NULL);
        ca_persona_delta_free_array(deltas, dn);

        ca_consolidation_outcome_t r2;
        ca_memory_consolidator_tick(con, CA_SLEEP_MONTHLY, &r2);
        assert(r2.persona_deltas_produced == 0);  /* idempotent by month */
        assert(ca_persona_delta_store_count(pd) == 1);

        ca_memory_consolidator_destroy(con);
        ca_heuristic_summarizer_destroy(sum);
        ca_persona_store_destroy(ps); ca_core_store_destroy(core);
        ca_persona_delta_store_destroy(pd); ca_semantic_store_destroy(sem);
        ca_daily_store_destroy(daily); ca_episodic_store_destroy(ep);
    }

    /* no delta when previous month has no dailies */
    {
        g_fixed_ms = iso_ms(2026, 6, 8, 9, 0, 0);
        ca_episodic_store_t *ep = ca_episodic_store_create(100000);
        ca_daily_store_t *daily = ca_daily_store_create();
        ca_semantic_store_t *sem = ca_semantic_store_create();
        ca_persona_delta_store_t *pd = ca_persona_delta_store_create();
        ca_core_store_t *core = ca_core_store_create();
        ca_persona_store_t *ps = ca_persona_store_create();
        ca_heuristic_summarizer_t *sum = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        ca_memory_consolidator_t *con = ca_memory_consolidator_create(
            ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);
        ca_consolidation_outcome_t r;
        ca_memory_consolidator_tick(con, CA_SLEEP_MONTHLY, &r);
        assert(r.persona_deltas_produced == 0);
        assert(ca_persona_delta_store_count(pd) == 0);
        ca_memory_consolidator_destroy(con);
        ca_heuristic_summarizer_destroy(sum);
        ca_persona_store_destroy(ps); ca_core_store_destroy(core);
        ca_persona_delta_store_destroy(pd); ca_semantic_store_destroy(sem);
        ca_daily_store_destroy(daily); ca_episodic_store_destroy(ep);
    }

    /* derivePersonaDelta: new vs strengthened + signal deltas + narrative */
    {
        ca_heuristic_summarizer_t *s = ca_heuristic_summarizer_create(0, 0, NULL, NULL);
        ca_consolidation_persona_t *before = ca_consolidation_persona_create("default");
        ca_consolidation_persona_set_topic(before, "finance", 2);
        before->positive_signals = 1; before->negative_signals = 1; before->total_interactions = 5;
        free(before->verbosity); before->verbosity = strdup("balanced");

        ca_consolidation_persona_t *after = ca_consolidation_persona_create("default");
        ca_consolidation_persona_set_topic(after, "finance", 5);  /* strengthened +3 */
        ca_consolidation_persona_set_topic(after, "travel", 3);   /* new */
        after->positive_signals = 7; after->negative_signals = 2; after->total_interactions = 20;
        free(after->verbosity); after->verbosity = strdup("detailed");

        ca_daily_summary_t day;
        ca_heuristic_summarizer_summarize_day(s, cd(2026, 5, 10), NULL, 0, &day);

        ca_persona_delta_t delta;
        ca_heuristic_summarizer_derive_persona_delta(s, before, after, &day, 1, &delta);
        double tr = 0, fin = 0;
        assert(ca_topic_weights_get(&delta.new_topics, "travel", &tr) && tr == 3);
        assert(!ca_topic_weights_get(&delta.new_topics, "finance", &fin));
        assert(ca_topic_weights_get(&delta.strengthened_topics, "finance", &fin) && fin == 3);
        assert(delta.net_signal_delta == 5);       /* (7-1) - (2-1) */
        assert(delta.interactions_in_period == 15); /* 20-5 */
        assert(strstr(delta.narrative, "Preferred verbosity shifted from balanced to detailed.") != NULL);
        assert(strstr(delta.narrative, "Net feedback was positive (+5).") != NULL);

        ca_persona_delta_free(&delta);
        ca_daily_summary_free(&day);
        ca_consolidation_persona_destroy(before);
        ca_consolidation_persona_destroy(after);
        ca_heuristic_summarizer_destroy(s);
    }

    /* ═════════════════ OnDemand runs every tier ═════════════════ */
    {
        g_fixed_ms = iso_ms(2026, 6, 8, 9, 0, 0);
        ca_episodic_store_t *ep = ca_episodic_store_create(100000);
        ca_daily_store_t *daily = ca_daily_store_create();
        ca_semantic_store_t *sem = ca_semantic_store_create();
        ca_persona_delta_store_t *pd = ca_persona_delta_store_create();
        ca_core_store_t *core = ca_core_store_create();
        ca_persona_store_t *ps = ca_persona_store_create();
        ca_heuristic_summarizer_t *sum = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
        ca_memory_consolidator_t *con = ca_memory_consolidator_create(
            ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);

        /* Daily fuel: completed day this week. */
        ca_episodic_entry_t f1 = mk_entry("f1", iso_ms(2026, 6, 6, 10, 0, 0), NULL, 0, "topic", "finance", NULL, NULL);
        ca_episodic_entry_t f2 = mk_entry("f2", iso_ms(2026, 6, 6, 11, 0, 0), NULL, 0, "topic", "finance", NULL, NULL);
        ca_episodic_store_add(ep, &f1);
        ca_episodic_store_add(ep, &f2);
        /* Weekly fuel: dailies inside last week sharing a topic. */
        ca_episodic_entry_t a = mk_entry("a", iso_ms(2026, 6, 1, 9, 0, 0), NULL, 0, "topic", "finance", NULL, NULL);
        ca_episodic_entry_t b = mk_entry("b", iso_ms(2026, 6, 1, 9, 0, 0), NULL, 0, "topic", "finance", NULL, NULL);
        ca_episodic_entry_t c = mk_entry("c", iso_ms(2026, 6, 2, 9, 0, 0), NULL, 0, "topic", "finance", NULL, NULL);
        ca_daily_summary_t w1, w2;
        ca_heuristic_summarizer_summarize_day(sum, cd(2026, 6, 1), (ca_episodic_entry_t[]){a, b}, 2, &w1);
        ca_heuristic_summarizer_summarize_day(sum, cd(2026, 6, 2), &c, 1, &w2);
        ca_daily_store_upsert(daily, &w1);
        ca_daily_store_upsert(daily, &w2);
        ca_daily_summary_free(&w1); ca_daily_summary_free(&w2);
        /* Monthly fuel: a daily inside May + a persona. */
        ca_daily_summary_t may;
        ca_heuristic_summarizer_summarize_day(sum, cd(2026, 5, 20), NULL, 0, &may);
        may.episode_count = 3;
        ca_daily_store_upsert(daily, &may);
        ca_daily_summary_free(&may);
        ca_consolidation_persona_t *p = ca_consolidation_persona_create("default");
        ca_consolidation_persona_set_topic(p, "finance", 2);
        p->total_interactions = 6;
        ca_persona_store_save(ps, p);
        ca_consolidation_persona_destroy(p);

        ca_consolidation_outcome_t r;
        ca_memory_consolidator_tick(con, CA_SLEEP_ONDEMAND, &r);
        assert(r.kind == CA_SLEEP_ONDEMAND);
        assert(r.daily_summaries_produced >= 1);
        assert(r.semantic_clusters_produced >= 1);
        assert(r.persona_deltas_produced == 1);
        assert(r.ran_at_ms == g_fixed_ms);
        assert(ca_semantic_store_count(sem) >= 1);
        assert(ca_persona_delta_store_count(pd) == 1);

        ca_memory_consolidator_destroy(con);
        ca_heuristic_summarizer_destroy(sum);
        ca_persona_store_destroy(ps); ca_core_store_destroy(core);
        ca_persona_delta_store_destroy(pd); ca_semantic_store_destroy(sem);
        ca_daily_store_destroy(daily); ca_episodic_store_destroy(ep);
    }

    /* ═════════════════ in-memory store cosine ranking + ordering ═════════════════ */
    {
        /* CoreMemoryStore ranks by full cosine to the query centroid. */
        ca_core_store_t *core = ca_core_store_create();
        float e10[2] = {1, 0}, e01[2] = {0, 1}, e11[2] = {1, 1};
        ca_core_memory_t x, y, diag;
        memset(&x, 0, sizeof(x)); memset(&y, 0, sizeof(y)); memset(&diag, 0, sizeof(diag));
        x.id = (char *)"x"; x.statement = (char *)"x"; x.embedding = e10; x.embedding_len = 2;
        y.id = (char *)"y"; y.statement = (char *)"y"; y.embedding = e01; y.embedding_len = 2;
        diag.id = (char *)"d"; diag.statement = (char *)"diag"; diag.embedding = e11; diag.embedding_len = 2;
        ca_core_store_add(core, &x);
        ca_core_store_add(core, &y);
        ca_core_store_add(core, &diag);
        float q[2] = {1, 0};
        size_t rn = 0;
        ca_core_memory_t *ranked = ca_core_store_search(core, q, 2, 3, &rn);
        assert(rn == 3);
        assert(strcmp(ranked[0].statement, "x") == 0);     /* cos 1 */
        assert(strcmp(ranked[2].statement, "y") == 0);     /* cos 0 */
        assert(strcmp(ranked[1].statement, "diag") == 0);  /* cos 0.707 */
        ca_core_memory_free_array(ranked, rn);
        ca_core_store_destroy(core);
    }
    {
        /* CoreMemoryStore falls back to reinforcement order when query null. */
        ca_core_store_t *core = ca_core_store_create();
        ca_core_memory_t a, b;
        memset(&a, 0, sizeof(a)); memset(&b, 0, sizeof(b));
        a.id = (char *)"a"; a.statement = (char *)"a";
        b.id = (char *)"b"; b.statement = (char *)"b";
        ca_core_store_add(core, &a);
        ca_core_store_add(core, &b);
        ca_core_store_reinforce(core, "b");
        ca_core_store_reinforce(core, "b");
        size_t tn = 0;
        ca_core_memory_t *top = ca_core_store_search(core, NULL, 0, 2, &tn);
        assert(tn == 2);
        assert(strcmp(top[0].statement, "b") == 0);
        assert(top[0].reinforcement_count == 2);
        ca_core_memory_free_array(top, tn);
        ca_core_store_destroy(core);
    }
    {
        /* SemanticMemoryStore.getWeek orders by topicWeight desc; search by cosine. */
        ca_semantic_store_t *sem = ca_semantic_store_create();
        float e01[2] = {0, 1}, e10[2] = {1, 0};
        ca_semantic_cluster_t low, high;
        memset(&low, 0, sizeof(low)); memset(&high, 0, sizeof(high));
        low.week_starting_monday = cd(2026, 6, 1); low.topic = (char *)"low"; low.summary = (char *)"";
        low.topic_weight = 1; low.centroid_embedding = e01; low.centroid_len = 2;
        high.week_starting_monday = cd(2026, 6, 1); high.topic = (char *)"high"; high.summary = (char *)"";
        high.topic_weight = 5; high.centroid_embedding = e10; high.centroid_len = 2;
        ca_semantic_store_add(sem, &low);
        ca_semantic_store_add(sem, &high);
        size_t wn = 0;
        ca_semantic_cluster_t *week = ca_semantic_store_get_week(sem, cd(2026, 6, 1), &wn);
        assert(wn == 2);
        assert(strcmp(week[0].topic, "high") == 0);
        assert(strcmp(week[1].topic, "low") == 0);
        ca_semantic_cluster_free_array(week, wn);
        float q[2] = {1, 0};
        size_t sn = 0;
        ca_semantic_cluster_t *ranked = ca_semantic_store_search(sem, q, 2, 2, &sn);
        assert(sn == 2);
        assert(strcmp(ranked[0].topic, "high") == 0);  /* centroid [1,0] cos 1 */
        ca_semantic_cluster_free_array(ranked, sn);
        ca_semantic_store_destroy(sem);
    }
    {
        /* DailyMemoryStore.getRange returns day-ordered inclusive results. */
        ca_daily_store_t *daily = ca_daily_store_create();
        ca_daily_summary_t d3, d1, d10;
        memset(&d3, 0, sizeof(d3)); memset(&d1, 0, sizeof(d1)); memset(&d10, 0, sizeof(d10));
        d3.day = cd(2026, 6, 3); d3.summary = (char *)"";
        d1.day = cd(2026, 6, 1); d1.summary = (char *)"";
        d10.day = cd(2026, 6, 10); d10.summary = (char *)"";
        ca_daily_store_upsert(daily, &d3);
        ca_daily_store_upsert(daily, &d1);
        ca_daily_store_upsert(daily, &d10);
        size_t rn = 0;
        ca_daily_summary_t *range = ca_daily_store_get_range(daily, cd(2026, 6, 1), cd(2026, 6, 5), &rn);
        assert(rn == 2);
        assert(cd_eq(range[0].day, cd(2026, 6, 1)));
        assert(cd_eq(range[1].day, cd(2026, 6, 3)));
        ca_daily_summary_free_array(range, rn);
        ca_daily_store_destroy(daily);
    }

    printf("All consolidation tests passed.\n");
    return 0;
}
