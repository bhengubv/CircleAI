#ifndef CIRCLE_AI_COMPANION_RUNTIME_H
#define CIRCLE_AI_COMPANION_RUNTIME_H

/*
 * companion_runtime.h — CircleAI.Memory.Runtime (C11 port).
 *
 * Ports CompanionRuntime.cs + CompanionRuntimeOptions.cs — the host
 * orchestrator that owns the memory pipeline lifecycle (consolidator, sync
 * engine, multimodal ingester) and ticks consolidation passes.
 *
 * The C# runtime is an IHostedService that spins background Task.Delay loops.
 * With no threads in the C port, the periodic loops collapse to an explicit
 * per-iteration entry point the host drives from its own scheduler:
 *   ca_companion_runtime_run_tick(rt, kind, &outcome)
 * mirrors RunPeriodic's body exactly (tick the consolidator, swallow failures).
 * Start/Stop keep their real work (start/dispose the sync engine, run the
 * catch-up consolidation pass). ConsolidateNow/IngestMedia/SyncNow are the
 * public helpers, faithfully ported.
 *
 * Intervals in CompanionRuntimeOptions are kept as milliseconds (TimeSpan → ms)
 * so a host scheduler can honour them; the runtime exposes them for that
 * scheduler and uses the "> 0 enables it" semantics from the C#.
 *
 * Dependencies are borrowed (the caller owns them): the consolidator is
 * required; the sync engine + ingester are optional (NULL → that subsystem is
 * gracefully skipped, matching the nullable C# ctor args).
 *
 * Pure C11 + libc.
 */

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#include "circle_ai/consolidation.h"   /* ca_memory_consolidator_t, ca_sleep_kind_t, outcome */
#include "circle_ai/multimodal.h"      /* ca_multimodal_ingester_t, modality, ingest opts/result */
#include "circle_ai/companion_sync.h"  /* ca_companion_state_sync_engine_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * CompanionRuntimeOptions — CompanionRuntimeOptions.cs
 *
 * Defaults (as TimeSpan → ms):
 *   DailyTickInterval     = 6h   = 21600000
 *   WeeklyTickInterval    = 24h  = 86400000
 *   MonthlyTickInterval   = 48h  = 172800000
 *   SyncBroadcastInterval = 5min = 300000
 *   InitialDelay          = 30s  = 30000
 *   CatchUpOnStart        = true
 * A ZERO interval disables that automatic tier (matches "> TimeSpan.Zero").
 * =========================================================================== */

typedef struct {
    int64_t daily_tick_interval_ms;
    int64_t weekly_tick_interval_ms;
    int64_t monthly_tick_interval_ms;
    int64_t sync_broadcast_interval_ms;
    int64_t initial_delay_ms;
    bool    catch_up_on_start;
} ca_companion_runtime_options_t;

/* Fill with the C# defaults. */
ca_companion_runtime_options_t ca_companion_runtime_options_default(void);

/* ===========================================================================
 * CompanionRuntime — CompanionRuntime.cs
 * =========================================================================== */

typedef struct ca_companion_runtime ca_companion_runtime_t;

/* Create the runtime. consolidator is REQUIRED (NULL → NULL return, matching
 * ArgumentNullException). options may be NULL → defaults. sync_engine and
 * ingester may be NULL (that subsystem is skipped). All deps are borrowed. */
ca_companion_runtime_t *ca_companion_runtime_create(
    ca_memory_consolidator_t *consolidator,
    const ca_companion_runtime_options_t *options,
    ca_companion_state_sync_engine_t *sync_engine,
    ca_multimodal_ingester_t *ingester);
void ca_companion_runtime_destroy(ca_companion_runtime_t *rt);

/* StartAsync — start the sync engine (if wired) and, when CatchUpOnStart, run
 * an OnDemand consolidation pass. Returns true. When out_catchup != NULL and a
 * catch-up pass ran, the outcome is written there (kind is left OnDemand and
 * counts zeroed when no catch-up ran). */
bool ca_companion_runtime_start(ca_companion_runtime_t *rt,
                                ca_consolidation_outcome_t *out_catchup);

/* StopAsync — dispose the sync engine (if wired). Idempotent. */
void ca_companion_runtime_stop(ca_companion_runtime_t *rt);

/* One iteration of RunPeriodic for a tier: ticks the consolidator for kind and
 * writes the outcome to *out (may be NULL). Failures are swallowed (returns
 * false on a NULL runtime only). This is the seam a host scheduler calls at
 * the tier's configured interval. */
bool ca_companion_runtime_run_tick(ca_companion_runtime_t *rt,
                                   ca_sleep_kind_t kind,
                                   ca_consolidation_outcome_t *out);

/* ConsolidateNowAsync — trigger an OnDemand pass, writing the outcome to *out
 * (may be NULL). */
bool ca_companion_runtime_consolidate_now(ca_companion_runtime_t *rt,
                                          ca_consolidation_outcome_t *out);

/* IngestMediaAsync — forward to the registered ingester. Returns false when no
 * ingester was wired (C# throws InvalidOperationException) or on ingest
 * failure. *out is filled on success (caller frees with
 * ca_ingestion_result_free). opts may be NULL. */
bool ca_companion_runtime_ingest_media(ca_companion_runtime_t *rt,
                                       ca_media_modality_t modality,
                                       const uint8_t *bytes, size_t len,
                                       const ca_ingest_options_t *opts,
                                       ca_ingestion_result_t *out);

/* SyncNowAsync — force an immediate sync broadcast. No-op (returns true) when
 * sync isn't wired. */
bool ca_companion_runtime_sync_now(ca_companion_runtime_t *rt);

/* Accessor for the options (so a host scheduler can read the intervals). */
ca_companion_runtime_options_t ca_companion_runtime_get_options(
    const ca_companion_runtime_t *rt);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COMPANION_RUNTIME_H */
