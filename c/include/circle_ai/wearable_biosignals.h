#ifndef CIRCLE_AI_WEARABLE_BIOSIGNALS_H
#define CIRCLE_AI_WEARABLE_BIOSIGNALS_H

/*
 * wearable_biosignals.h — CircleAI.Wearable.Biosignals (C11 port of BiosignalKind.cs
 * + BiosignalSample.cs + IBiosignalSource.cs + NullBiosignalSource.cs +
 * RecordedBiosignalSource.cs).
 *
 *   Enum   : BiosignalKind { HeartRate=0, HeartRateVariability=1,
 *                   OxygenSaturation=2, Accelerometer=3, BodyTemperature=4,
 *                   SleepStage=5, Steps=6, GalvanicSkinResponse=7, Unknown=8 }
 *            (integer values stable across ports — do NOT renumber).
 *   Record : BiosignalSample(Guid Id, BiosignalKind Kind, float Value, Unit,
 *                   float Confidence, bool IsCumulative, DateTimeOffset MeasuredAt).
 *            Create(kind, value, unit, confidence=1, isCumulative=false) clamps
 *            Confidence to [0,1] and (in C#) stamps a new Guid + UtcNow. Since a
 *            Guid/UtcNow can't be reproduced deterministically, the port takes the
 *            id + timestamp explicitly (ca_bio_sample_make) while preserving the
 *            confidence clamp; the raw struct is used directly elsewhere.
 *   Source : IBiosignalSource — a streaming source vtable. Ships:
 *              NullBiosignalSource  — SupportedKinds empty; IsSupported false;
 *                                     Stream yields nothing.
 *              RecordedBiosignalSource(samples) — SupportedKinds = distinct kinds
 *                                     seen (first-seen order); IsSupported checks
 *                                     that set; Stream replays the samples in
 *                                     order. The C# IAsyncEnumerable is drained
 *                                     here via a replay cursor (*_next).
 *   Mapper : BiosignalAffectMapper.Apply(sample, affect) — deterministic,
 *                   fixture-validated projection of a sample onto AffectState
 *                   (ca_affect_state_t from memory.h). Confidence < 0.5 → no-op;
 *                   otherwise the HeartRate / HRV / SpO2 rule sheet mutates the
 *                   axes (all clamped to [0,1]) and stamps LastUpdatedUtc.
 *   Aggregator : BiosignalAggregator over an IBiosignalSource — a single-shot
 *                   sliding-window snapshot. SnapshotAsync(window) drains the
 *                   source's stream, keeping samples with MeasuredAt >= (now -
 *                   window), and computes per-kind BiosignalStats(count,min,max,
 *                   mean) into a BiosignalSnapshot(stats, GeneratedAt). The C#
 *                   time-bound read (CancelAfter(window) + deadline break) is a
 *                   live-source artifact; over the deterministic replay cursor the
 *                   port takes generated_at_ms explicitly and filters by cutoff.
 *
 * MeasuredAt as Unix ms UTC. Confidence/Value are float (C# float). Id is an owned
 * string (the Guid rendered by the caller); Unit is an owned string.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields, deep
 * copy getters, errors via NULL / count SIZE_MAX. Linear arrays, no pthreads.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "circle_ai/memory.h" /* ca_affect_state_t (BiosignalAffectMapper target) */

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CA_BIOSIGNAL_HEART_RATE = 0,
    CA_BIOSIGNAL_HEART_RATE_VARIABILITY = 1,
    CA_BIOSIGNAL_OXYGEN_SATURATION = 2,
    CA_BIOSIGNAL_ACCELEROMETER = 3,
    CA_BIOSIGNAL_BODY_TEMPERATURE = 4,
    CA_BIOSIGNAL_SLEEP_STAGE = 5,
    CA_BIOSIGNAL_STEPS = 6,
    CA_BIOSIGNAL_GALVANIC_SKIN_RESPONSE = 7,
    CA_BIOSIGNAL_UNKNOWN = 8
} ca_biosignal_kind_t;

/* BiosignalSample(Guid Id, BiosignalKind Kind, float Value, Unit, float
 * Confidence, bool IsCumulative, DateTimeOffset MeasuredAt). */
typedef struct {
    char   *id;            /* owned, non-null (the Guid string) */
    ca_biosignal_kind_t kind;
    float   value;
    char   *unit;          /* owned, non-null */
    float   confidence;
    bool    is_cumulative;
    int64_t measured_at_ms;
} ca_biosignal_sample_t;

void ca_biosignal_sample_free(ca_biosignal_sample_t *s);
void ca_biosignal_sample_free_array(ca_biosignal_sample_t *arr, size_t count);

/* Deterministic analog of BiosignalSample.Create: fills *out (owning) with the
 * given id + timestamp, Confidence clamped to [0,1]. id + unit are copied. Returns
 * 0 on success, -1 on bad args / OOM. */
int ca_bio_sample_make(ca_biosignal_sample_t *out, const char *id,
                       ca_biosignal_kind_t kind, float value, const char *unit,
                       float confidence, bool is_cumulative,
                       int64_t measured_at_ms);

/* ── IBiosignalSource ───────────────────────────────────────────────────── */

typedef struct ca_biosignal_source ca_biosignal_source_t;
typedef struct ca_biosignal_stream ca_biosignal_stream_t;

/* NullBiosignalSource() — supports nothing, streams nothing. NULL on OOM. */
ca_biosignal_source_t *ca_biosignal_null_source_create(void);

/* RecordedBiosignalSource(samples, count) — deep-copies the samples; SupportedKinds
 * becomes the distinct kinds (first-seen order). NULL on bad args / OOM. */
ca_biosignal_source_t *ca_biosignal_recorded_source_create(
    const ca_biosignal_sample_t *samples, size_t count);

void ca_biosignal_source_destroy(ca_biosignal_source_t *src);

/* SupportedKinds -> fresh owned array (first-seen order; empty for the null
 * source). NULL + 0 empty; NULL + SIZE_MAX on error. Free with free(). */
ca_biosignal_kind_t *ca_biosignal_source_supported_kinds(
    const ca_biosignal_source_t *src, size_t *out_count);

/* IsSupportedAsync(kind) — whether this source can produce that kind. */
bool ca_biosignal_source_is_supported(const ca_biosignal_source_t *src,
                                      ca_biosignal_kind_t kind);

/* StreamAsync() -> a replay cursor over the source's samples. NULL on OOM.
 * The null source yields an empty cursor. */
ca_biosignal_stream_t *ca_biosignal_source_stream(
    const ca_biosignal_source_t *src);
void ca_biosignal_stream_destroy(ca_biosignal_stream_t *st);

/* Drain the next sample into *out (freshly owned; free with
 * ca_biosignal_sample_free). Returns true when a sample was produced, false when
 * the cursor is exhausted (or on OOM mid-copy). */
bool ca_biosignal_stream_next(ca_biosignal_stream_t *st,
                              ca_biosignal_sample_t *out);

/* ── BiosignalAffectMapper ──────────────────────────────────────────────────
 * Deterministic projection of a BiosignalSample onto an AffectState. Mutates
 * *affect in place; all resulting axes are clamped to [0,1]. Rule sheet:
 *   Confidence < 0.5           → no mutation at all (early return; timestamp
 *                                 is NOT touched, mirroring the C# guard).
 *   HeartRate  > 130 bpm       → Energy += 0.10, Uncertainty += 0.05
 *   HeartRate  > 100 bpm       → Energy += 0.05
 *   HeartRate  <  50 bpm       → Energy -= 0.05
 *   HRV        <  20 ms        → Uncertainty += 0.05, Rapport -= 0.02
 *   HRV        >  60 ms        → Engagement += 0.02
 *   SpO2       <  90 %         → Uncertainty += 0.10
 *   SleepStage / others        → no mutation (still stamps LastUpdatedUtc).
 * When any rule branch is reached (Confidence >= 0.5), LastUpdatedUtc is set to
 * now_ms (the C# stamps DateTimeOffset.UtcNow after the switch). now_ms is taken
 * explicitly to stay deterministic. No-op on NULL sample/affect. */
void ca_biosignal_affect_apply(const ca_biosignal_sample_t *sample,
                               ca_affect_state_t *affect, int64_t now_ms);

/* ── BiosignalAggregator ────────────────────────────────────────────────────
 * BiosignalStats(int SampleCount, float Min, float Max, float Mean). */
typedef struct {
    int   sample_count;
    float min;
    float max;
    float mean;
} ca_biosignal_stats_t;

/* One per-kind entry of a snapshot (the C# IReadOnlyDictionary<Kind,Stats>
 * carried as a parallel-keyed array). */
typedef struct {
    ca_biosignal_kind_t  kind;
    ca_biosignal_stats_t stats;
} ca_biosignal_kind_stats_t;

/* BiosignalSnapshot(IReadOnlyDictionary<Kind,Stats> Stats, DateTimeOffset
 * GeneratedAt). Owns its entries block. */
typedef struct {
    ca_biosignal_kind_stats_t *entries; /* owned (NULL when count==0) */
    size_t                     count;
    int64_t                    generated_at_ms;
} ca_biosignal_snapshot_t;

void ca_biosignal_snapshot_free(ca_biosignal_snapshot_t *snap);

/* Look up the stats for `kind` in a snapshot: writes *out and returns true when
 * present, false when that kind had no in-window samples (C# dictionary miss). */
bool ca_biosignal_snapshot_get(const ca_biosignal_snapshot_t *snap,
                               ca_biosignal_kind_t kind,
                               ca_biosignal_stats_t *out);

/* BiosignalAggregator.SnapshotAsync(source, window_ms, now_ms): drains `source`'s
 * replay stream once, keeping samples with MeasuredAt >= (now_ms - window_ms), and
 * computes per-kind min/max/mean/count into *out. Kinds with no in-window sample
 * are absent (matching the C# dictionary). GeneratedAt := now_ms. window_ms must
 * be > 0 (C# ArgumentOutOfRangeException). Entry order is first-seen kind order.
 * Returns 0 on success (0 on OOM the *out is zeroed and -1 returned), -1 on bad
 * args / window_ms <= 0 / OOM. */
int ca_biosignal_aggregator_snapshot(const ca_biosignal_source_t *source,
                                     int64_t window_ms, int64_t now_ms,
                                     ca_biosignal_snapshot_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_WEARABLE_BIOSIGNALS_H */
