/*
 * companion_runtime.c — CircleAI.Memory.Runtime (C11 port).
 *
 * Ports CompanionRuntime.cs + CompanionRuntimeOptions.cs. The background
 * Task.Delay loops collapse to ca_companion_runtime_run_tick (RunPeriodic's
 * per-iteration body); Start/Stop keep the real lifecycle work.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/companion_runtime.h"

#include <stdlib.h>
#include <string.h>

ca_companion_runtime_options_t ca_companion_runtime_options_default(void) {
    ca_companion_runtime_options_t o;
    o.daily_tick_interval_ms     = 6LL  * 3600 * 1000; /* 21600000 */
    o.weekly_tick_interval_ms    = 24LL * 3600 * 1000; /* 86400000 */
    o.monthly_tick_interval_ms   = 48LL * 3600 * 1000; /* 172800000 */
    o.sync_broadcast_interval_ms = 5LL  * 60   * 1000; /* 300000 */
    o.initial_delay_ms           = 30LL * 1000;        /* 30000 */
    o.catch_up_on_start          = true;
    return o;
}

struct ca_companion_runtime {
    ca_memory_consolidator_t         *consolidator;   /* borrowed, required */
    ca_companion_state_sync_engine_t *sync_engine;    /* borrowed, optional */
    ca_multimodal_ingester_t         *ingester;       /* borrowed, optional */
    ca_companion_runtime_options_t    options;
    bool                              started;
    bool                              sync_disposed;
};

ca_companion_runtime_t *ca_companion_runtime_create(
    ca_memory_consolidator_t *consolidator,
    const ca_companion_runtime_options_t *options,
    ca_companion_state_sync_engine_t *sync_engine,
    ca_multimodal_ingester_t *ingester) {
    if (!consolidator) return NULL; /* ArgumentNullException.ThrowIfNull */
    ca_companion_runtime_t *rt = (ca_companion_runtime_t *)calloc(1, sizeof(*rt));
    if (!rt) return NULL;
    rt->consolidator = consolidator;
    rt->sync_engine = sync_engine;
    rt->ingester = ingester;
    rt->options = options ? *options : ca_companion_runtime_options_default();
    return rt;
}

void ca_companion_runtime_destroy(ca_companion_runtime_t *rt) {
    if (!rt) return;
    /* DisposeAsync → StopAsync (disposes the sync engine). */
    ca_companion_runtime_stop(rt);
    free(rt);
}

bool ca_companion_runtime_start(ca_companion_runtime_t *rt,
                                ca_consolidation_outcome_t *out_catchup) {
    if (!rt) return false;
    if (out_catchup) memset(out_catchup, 0, sizeof(*out_catchup));

    if (rt->sync_engine) {
        ca_sync_engine_start(rt->sync_engine);
    }

    if (rt->options.catch_up_on_start) {
        ca_consolidation_outcome_t oc; memset(&oc, 0, sizeof(oc));
        ca_memory_consolidator_tick(rt->consolidator, CA_SLEEP_ONDEMAND, &oc);
        if (out_catchup) *out_catchup = oc;
    }
    rt->started = true;
    return true;
}

void ca_companion_runtime_stop(ca_companion_runtime_t *rt) {
    if (!rt) return;
    if (rt->sync_engine && !rt->sync_disposed) {
        ca_sync_engine_destroy(rt->sync_engine);
        rt->sync_engine = NULL;      /* borrowed handle now invalid */
        rt->sync_disposed = true;
    }
    rt->started = false;
}

bool ca_companion_runtime_run_tick(ca_companion_runtime_t *rt,
                                   ca_sleep_kind_t kind,
                                   ca_consolidation_outcome_t *out) {
    if (!rt) return false;
    ca_consolidation_outcome_t oc; memset(&oc, 0, sizeof(oc));
    ca_memory_consolidator_tick(rt->consolidator, kind, &oc);
    if (out) *out = oc;
    return true;
}

bool ca_companion_runtime_consolidate_now(ca_companion_runtime_t *rt,
                                          ca_consolidation_outcome_t *out) {
    if (!rt) return false;
    ca_consolidation_outcome_t oc; memset(&oc, 0, sizeof(oc));
    ca_memory_consolidator_tick(rt->consolidator, CA_SLEEP_ONDEMAND, &oc);
    if (out) *out = oc;
    return true;
}

bool ca_companion_runtime_ingest_media(ca_companion_runtime_t *rt,
                                       ca_media_modality_t modality,
                                       const uint8_t *bytes, size_t len,
                                       const ca_ingest_options_t *opts,
                                       ca_ingestion_result_t *out) {
    if (!rt) return false;
    if (!rt->ingester) return false; /* C#: InvalidOperationException */
    return ca_multimodal_ingester_ingest(rt->ingester, modality, bytes, len, opts, out);
}

bool ca_companion_runtime_sync_now(ca_companion_runtime_t *rt) {
    if (!rt) return false;
    if (!rt->sync_engine) return true; /* Task.CompletedTask */
    return ca_sync_engine_sync_now(rt->sync_engine);
}

ca_companion_runtime_options_t ca_companion_runtime_get_options(
    const ca_companion_runtime_t *rt) {
    if (!rt) return ca_companion_runtime_options_default();
    return rt->options;
}
