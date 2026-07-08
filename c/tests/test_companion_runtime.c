/*
 * test_companion_runtime.c — CircleAI.Memory.Runtime (C11 port).
 *
 * Verifies CompanionRuntime + CompanionRuntimeOptions against the C# spec:
 *   - default option values (TimeSpan → ms)
 *   - Start runs the catch-up OnDemand consolidation pass + starts sync engine
 *   - run_tick mirrors RunPeriodic's body (produces a daily summary)
 *   - ConsolidateNow runs an OnDemand pass
 *   - IngestMedia forwards to the ingester; without one it returns false
 *   - SyncNow no-ops without a sync engine, broadcasts with one
 *   - Stop disposes the sync engine
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* 2026-06-08 09:00 UTC — strictly after the seeded 2026-06-06 episodes, so the
 * daily consolidation's "today-exclusion" rule summarises that past day. */
static int64_t fixed_clock(void *u) { (void)u; return 1780909200000LL; }
static int64_t fake_now(void *u) { (void)u; return 50000; }

/* Compute a Unix-ms timestamp for a civil date/time (UTC). */
static int64_t iso_ms(int y, int mo, int d, int h, int mi, int s) {
    /* days-from-civil (Hinnant) */
    y -= (mo <= 2);
    int era = (y >= 0 ? y : y - 399) / 400;
    unsigned yoe = (unsigned)(y - era * 400);
    unsigned doy = (153u * (unsigned)(mo > 2 ? mo - 3 : mo + 9) + 2) / 5 + (unsigned)d - 1;
    unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    long long days = (long long)era * 146097 + (long long)doe - 719468;
    return (days * 86400 + h * 3600 + mi * 60 + s) * 1000LL;
}

static ca_episodic_entry_t mk_ep(const char *id, int64_t ts) {
    ca_episodic_entry_t e; memset(&e, 0, sizeof(e));
    e.id = (char *)id;
    e.recorded_at_ms = ts;
    e.user_text = (char *)"u";
    e.assistant_text = (char *)"a";
    return e;
}

int main(void) {
    /* ── options defaults ─────────────────────────────────────────── */
    ca_companion_runtime_options_t opt = ca_companion_runtime_options_default();
    assert(opt.daily_tick_interval_ms == 6LL * 3600 * 1000);
    assert(opt.weekly_tick_interval_ms == 24LL * 3600 * 1000);
    assert(opt.monthly_tick_interval_ms == 48LL * 3600 * 1000);
    assert(opt.sync_broadcast_interval_ms == 5LL * 60 * 1000);
    assert(opt.initial_delay_ms == 30LL * 1000);
    assert(opt.catch_up_on_start == true);

    /* ── build a real consolidator ────────────────────────────────── */
    ca_episodic_store_t *ep = ca_episodic_store_create(100000);
    ca_daily_store_t *daily = ca_daily_store_create();
    ca_semantic_store_t *sem = ca_semantic_store_create();
    ca_persona_delta_store_t *pd = ca_persona_delta_store_create();
    ca_core_store_t *core = ca_core_store_create();
    ca_persona_store_t *ps = ca_persona_store_create();
    ca_heuristic_summarizer_t *sum = ca_heuristic_summarizer_create(0, 0, fixed_clock, NULL);
    ca_memory_consolidator_t *con = ca_memory_consolidator_create(
        ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);
    assert(con);

    /* seed two episodes on the same day so a DAILY tick yields one summary */
    ca_episodic_entry_t e1 = mk_ep("d1", iso_ms(2026, 6, 6, 10, 0, 0));
    ca_episodic_entry_t e2 = mk_ep("d2", iso_ms(2026, 6, 6, 11, 0, 0));
    assert(ca_episodic_store_add(ep, &e1));
    assert(ca_episodic_store_add(ep, &e2));

    /* ── sync engine (in-proc) ────────────────────────────────────── */
    ca_inproc_sync_hub_t *hub = ca_inproc_sync_hub_create();
    ca_companion_state_channel_t *ch = ca_inproc_channel_create(hub, "runtime");
    ca_syncable_entry_store_t *st = ca_inmem_syncable_store_create();
    ca_hybrid_logical_clock_t *clk = ca_hlc_create(1, fake_now, NULL);
    ca_companion_state_sync_engine_t *eng = ca_sync_engine_create(
        ca_inproc_channel_iface(ch), ca_inmem_syncable_store_iface(st), clk, fake_now, NULL);
    assert(eng);

    /* ── multimodal ingester ──────────────────────────────────────── */
    ca_multimodal_store_t *mstore = ca_multimodal_store_create();
    ca_captioner_t cap = ca_heuristic_captioner();
    ca_multimodal_ingester_t *ing = ca_multimodal_ingester_create(&cap, 1, mstore);
    assert(ing);

    /* null consolidator → NULL runtime (ArgumentNullException) */
    assert(ca_companion_runtime_create(NULL, &opt, eng, ing) == NULL);

    /* ── full runtime ─────────────────────────────────────────────── */
    ca_companion_runtime_t *rt = ca_companion_runtime_create(con, &opt, eng, ing);
    assert(rt);

    /* Start: catch-up OnDemand consolidation runs (produces the daily summary
     * for 2026-06-06) and the sync engine starts. */
    ca_consolidation_outcome_t catchup; memset(&catchup, 0, sizeof(catchup));
    assert(ca_companion_runtime_start(rt, &catchup));
    assert(catchup.daily_summaries_produced == 1);   /* OnDemand ran the daily tier */
    assert(ca_daily_store_count(daily) == 1);

    /* run_tick mirrors RunPeriodic body — a second DAILY tick is idempotent. */
    ca_consolidation_outcome_t tick; memset(&tick, 0, sizeof(tick));
    assert(ca_companion_runtime_run_tick(rt, CA_SLEEP_DAILY, &tick));
    assert(tick.daily_summaries_produced == 0);       /* already summarised */
    assert(tick.kind == CA_SLEEP_DAILY);

    /* ConsolidateNow → OnDemand pass */
    ca_consolidation_outcome_t now; memset(&now, 0, sizeof(now));
    assert(ca_companion_runtime_consolidate_now(rt, &now));
    assert(now.kind == CA_SLEEP_ONDEMAND);

    /* IngestMedia forwards to the ingester */
    const uint8_t jpg[] = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
    ca_ingestion_result_t ir; memset(&ir, 0, sizeof(ir));
    assert(ca_companion_runtime_ingest_media(rt, CA_MEDIA_IMAGE, jpg, sizeof(jpg), NULL, &ir));
    assert(ir.entry.caption != NULL);
    ca_ingestion_result_free(&ir);

    /* SyncNow broadcasts (engine started) → returns true */
    assert(ca_companion_runtime_sync_now(rt));

    /* Stop disposes the sync engine (idempotent). */
    ca_companion_runtime_stop(rt);
    /* SyncNow after Stop no-ops (engine gone) → true */
    assert(ca_companion_runtime_sync_now(rt));

    ca_companion_runtime_destroy(rt);   /* also calls stop (already stopped) */

    /* ── runtime WITHOUT sync engine + ingester ───────────────────── */
    ca_memory_consolidator_t *con2 = ca_memory_consolidator_create(
        ep, daily, sem, pd, core, ps, sum, NULL, fixed_clock, NULL, NULL);
    ca_companion_runtime_t *rt2 = ca_companion_runtime_create(con2, NULL, NULL, NULL);
    assert(rt2);
    assert(ca_companion_runtime_start(rt2, NULL));
    /* IngestMedia without an ingester → false (C# InvalidOperationException) */
    ca_ingestion_result_t ir2; memset(&ir2, 0, sizeof(ir2));
    assert(ca_companion_runtime_ingest_media(rt2, CA_MEDIA_IMAGE, jpg, sizeof(jpg), NULL, &ir2) == false);
    /* SyncNow without an engine → true (Task.CompletedTask) */
    assert(ca_companion_runtime_sync_now(rt2));
    /* default options when NULL passed */
    ca_companion_runtime_options_t got = ca_companion_runtime_get_options(rt2);
    assert(got.catch_up_on_start == true);
    ca_companion_runtime_destroy(rt2);
    ca_memory_consolidator_destroy(con2);

    /* teardown */
    ca_memory_consolidator_destroy(con);
    ca_heuristic_summarizer_destroy(sum);
    ca_persona_store_destroy(ps);
    ca_core_store_destroy(core);
    ca_persona_delta_store_destroy(pd);
    ca_semantic_store_destroy(sem);
    ca_daily_store_destroy(daily);
    ca_episodic_store_destroy(ep);
    ca_multimodal_ingester_destroy(ing);
    ca_multimodal_store_destroy(mstore);
    /* eng already disposed by runtime stop; do NOT double-free it. */
    ca_hlc_destroy(clk);
    ca_inmem_syncable_store_destroy(st);
    ca_inproc_channel_destroy(ch);
    ca_inproc_sync_hub_destroy(hub);

    printf("test_companion_runtime: all assertions passed\n");
    return 0;
}
