/*
 * wearable_biosignals.c — CircleAI.Wearable.Biosignals (C11 port).
 *
 * BiosignalSample + Create (confidence clamp); IBiosignalSource with two impls:
 * NullBiosignalSource (empty) and RecordedBiosignalSource (replays a fixed sample
 * list via a cursor, SupportedKinds = distinct kinds first-seen). Pure C11 + libc.
 */

#include "circle_ai/wearable_biosignals.h"
#include "board_common.h"

#include <math.h> /* INFINITY (Accumulator min/max seeds) */

/* ── BiosignalSample ────────────────────────────────────────────────────── */

void ca_biosignal_sample_free(ca_biosignal_sample_t *s) {
    if (!s) return;
    free(s->id);
    free(s->unit);
    s->id = s->unit = NULL;
}
void ca_biosignal_sample_free_array(ca_biosignal_sample_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_biosignal_sample_free(&arr[i]);
    free(arr);
}

static bool sample_copy(ca_biosignal_sample_t *dst,
                        const ca_biosignal_sample_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->id             = cab_strdup_empty(src->id);
    dst->kind           = src->kind;
    dst->value          = src->value;
    dst->unit           = cab_strdup_empty(src->unit);
    dst->confidence     = src->confidence;
    dst->is_cumulative  = src->is_cumulative;
    dst->measured_at_ms = src->measured_at_ms;
    if (!dst->id || !dst->unit) {
        ca_biosignal_sample_free(dst);
        return false;
    }
    return true;
}

int ca_bio_sample_make(ca_biosignal_sample_t *out, const char *id,
                       ca_biosignal_kind_t kind, float value, const char *unit,
                       float confidence, bool is_cumulative,
                       int64_t measured_at_ms) {
    if (!out) return -1;
    memset(out, 0, sizeof(*out));
    /* Math.Clamp(confidence, 0f, 1f). */
    if (confidence < 0.0f) confidence = 0.0f;
    else if (confidence > 1.0f) confidence = 1.0f;
    out->id             = cab_strdup_empty(id);
    out->kind           = kind;
    out->value          = value;
    out->unit           = cab_strdup_empty(unit);
    out->confidence     = confidence;
    out->is_cumulative  = is_cumulative;
    out->measured_at_ms = measured_at_ms;
    if (!out->id || !out->unit) {
        ca_biosignal_sample_free(out);
        return -1;
    }
    return 0;
}

/* ── IBiosignalSource ───────────────────────────────────────────────────── */

struct ca_biosignal_source {
    bool                   is_null;
    ca_biosignal_sample_t *samples;   /* owned deep copies (recorded source) */
    size_t                 sample_count;
    ca_biosignal_kind_t   *kinds;     /* distinct, first-seen order */
    size_t                 kind_count;
};

ca_biosignal_source_t *ca_biosignal_null_source_create(void) {
    ca_biosignal_source_t *s =
        (ca_biosignal_source_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->is_null = true;
    return s;
}

ca_biosignal_source_t *ca_biosignal_recorded_source_create(
    const ca_biosignal_sample_t *samples, size_t count) {
    if (!samples && count > 0) return NULL;
    ca_biosignal_source_t *s =
        (ca_biosignal_source_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;

    if (count > 0) {
        s->samples = (ca_biosignal_sample_t *)calloc(count, sizeof(*s->samples));
        if (!s->samples) { free(s); return NULL; }
        for (size_t i = 0; i < count; ++i) {
            if (!sample_copy(&s->samples[i], &samples[i])) {
                ca_biosignal_sample_free_array(s->samples, i);
                free(s);
                return NULL;
            }
        }
        s->sample_count = count;

        /* Distinct kinds in first-seen order (mirrors HashSet insertion + ToArray,
         * whose enumeration order for the small int-set is insertion order). */
        s->kinds = (ca_biosignal_kind_t *)malloc(count * sizeof(*s->kinds));
        if (!s->kinds) {
            ca_biosignal_sample_free_array(s->samples, count);
            free(s);
            return NULL;
        }
        size_t kc = 0;
        for (size_t i = 0; i < count; ++i) {
            ca_biosignal_kind_t k = samples[i].kind;
            bool seen = false;
            for (size_t j = 0; j < kc; ++j)
                if (s->kinds[j] == k) { seen = true; break; }
            if (!seen) s->kinds[kc++] = k;
        }
        s->kind_count = kc;
    }
    return s;
}

void ca_biosignal_source_destroy(ca_biosignal_source_t *src) {
    if (!src) return;
    ca_biosignal_sample_free_array(src->samples, src->sample_count);
    free(src->kinds);
    free(src);
}

ca_biosignal_kind_t *ca_biosignal_source_supported_kinds(
    const ca_biosignal_source_t *src, size_t *out_count) {
    if (!out_count) return NULL;
    if (!src) { *out_count = (size_t)-1; return NULL; }
    if (src->kind_count == 0) { *out_count = 0; return NULL; }
    ca_biosignal_kind_t *out =
        (ca_biosignal_kind_t *)malloc(src->kind_count * sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    memcpy(out, src->kinds, src->kind_count * sizeof(*out));
    *out_count = src->kind_count;
    return out;
}

bool ca_biosignal_source_is_supported(const ca_biosignal_source_t *src,
                                      ca_biosignal_kind_t kind) {
    if (!src) return false;
    for (size_t i = 0; i < src->kind_count; ++i)
        if (src->kinds[i] == kind) return true;
    return false;
}

/* ── stream (replay cursor) ─────────────────────────────────────────────── */

struct ca_biosignal_stream {
    const ca_biosignal_source_t *src;
    size_t                       pos;
};

ca_biosignal_stream_t *ca_biosignal_source_stream(
    const ca_biosignal_source_t *src) {
    if (!src) return NULL;
    ca_biosignal_stream_t *st =
        (ca_biosignal_stream_t *)calloc(1, sizeof(*st));
    if (!st) return NULL;
    st->src = src;
    st->pos = 0;
    return st;
}

void ca_biosignal_stream_destroy(ca_biosignal_stream_t *st) {
    free(st);
}

bool ca_biosignal_stream_next(ca_biosignal_stream_t *st,
                              ca_biosignal_sample_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!st || !out || !st->src) return false;
    if (st->pos >= st->src->sample_count) return false;
    if (!sample_copy(out, &st->src->samples[st->pos])) return false;
    st->pos++;
    return true;
}

/* ── BiosignalAffectMapper ──────────────────────────────────────────────────
 * Pure deterministic rule sheet (Math.Clamp to [0,1] on every touched axis). */

#define BIO_MIN_CONFIDENCE 0.5f

/* Math.Clamp(v, 0f, 1f). */
static float bio_clamp01(float v) {
    if (v < 0.0f) return 0.0f;
    if (v > 1.0f) return 1.0f;
    return v;
}

static void bio_apply_heart_rate(float bpm, ca_affect_state_t *a) {
    if (bpm > 130.0f) {
        a->energy      = bio_clamp01(a->energy      + 0.10f);
        a->uncertainty = bio_clamp01(a->uncertainty + 0.05f);
    } else if (bpm > 100.0f) {
        a->energy = bio_clamp01(a->energy + 0.05f);
    } else if (bpm < 50.0f) {
        a->energy = bio_clamp01(a->energy - 0.05f);
    }
}

static void bio_apply_hrv(float rmssd_ms, ca_affect_state_t *a) {
    if (rmssd_ms < 20.0f) {
        a->uncertainty = bio_clamp01(a->uncertainty + 0.05f);
        a->rapport     = bio_clamp01(a->rapport     - 0.02f);
    } else if (rmssd_ms > 60.0f) {
        a->engagement = bio_clamp01(a->engagement + 0.02f);
    }
}

static void bio_apply_spo2(float percent, ca_affect_state_t *a) {
    if (percent < 90.0f) {
        a->uncertainty = bio_clamp01(a->uncertainty + 0.10f);
    }
}

void ca_biosignal_affect_apply(const ca_biosignal_sample_t *sample,
                               ca_affect_state_t *affect, int64_t now_ms) {
    if (!sample || !affect) return;
    /* Confidence gate — low-confidence samples never mutate state (and, per the
     * C#, return before LastUpdatedUtc is stamped). */
    if (sample->confidence < BIO_MIN_CONFIDENCE) return;

    switch (sample->kind) {
        case CA_BIOSIGNAL_HEART_RATE:
            bio_apply_heart_rate(sample->value, affect);
            break;
        case CA_BIOSIGNAL_HEART_RATE_VARIABILITY:
            bio_apply_hrv(sample->value, affect);
            break;
        case CA_BIOSIGNAL_OXYGEN_SATURATION:
            bio_apply_spo2(sample->value, affect);
            break;
        case CA_BIOSIGNAL_SLEEP_STAGE:
            /* Deep/REM/awake/light — sleep itself is not affect; no mutation. */
            break;
        /* Accelerometer, BodyTemperature, Steps, GSR, Unknown — no rule yet. */
        default:
            break;
    }

    affect->last_updated_at = now_ms; /* C#: affect.LastUpdatedUtc = UtcNow */
}

/* ── BiosignalAggregator ────────────────────────────────────────────────────
 * Single-shot sliding-window snapshot over an IBiosignalSource replay stream. */

void ca_biosignal_snapshot_free(ca_biosignal_snapshot_t *snap) {
    if (!snap) return;
    free(snap->entries);
    snap->entries = NULL;
    snap->count = 0;
}

bool ca_biosignal_snapshot_get(const ca_biosignal_snapshot_t *snap,
                               ca_biosignal_kind_t kind,
                               ca_biosignal_stats_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!snap || !out) return false;
    for (size_t i = 0; i < snap->count; ++i) {
        if (snap->entries[i].kind == kind) {
            *out = snap->entries[i].stats;
            return true;
        }
    }
    return false;
}

/* Running per-kind accumulator (mirrors the C# private Accumulator). */
typedef struct {
    ca_biosignal_kind_t kind;
    int                 count;
    float               min; /* seeded +inf */
    float               max; /* seeded -inf */
    double              sum;
} bio_accumulator_t;

int ca_biosignal_aggregator_snapshot(const ca_biosignal_source_t *source,
                                     int64_t window_ms, int64_t now_ms,
                                     ca_biosignal_snapshot_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!source || !out) return -1;
    if (window_ms <= 0) return -1; /* C# ArgumentOutOfRangeException (window>0) */

    int64_t cutoff = now_ms - window_ms;
    out->generated_at_ms = now_ms;

    ca_biosignal_stream_t *st = ca_biosignal_source_stream(source);
    if (!st) return -1;

    /* At most one accumulator per distinct kind; sample_count bounds them. The
     * null source has sample_count 0, yielding an empty snapshot. */
    size_t cap = source->sample_count;
    bio_accumulator_t *accs = NULL;
    if (cap > 0) {
        accs = (bio_accumulator_t *)malloc(cap * sizeof(*accs));
        if (!accs) { ca_biosignal_stream_destroy(st); return -1; }
    }
    size_t nacc = 0;

    ca_biosignal_sample_t s;
    while (ca_biosignal_stream_next(st, &s)) {
        if (s.measured_at_ms < cutoff) { ca_biosignal_sample_free(&s); continue; }
        bio_accumulator_t *acc = NULL;
        for (size_t i = 0; i < nacc; ++i)
            if (accs[i].kind == s.kind) { acc = &accs[i]; break; }
        if (!acc) {
            acc = &accs[nacc++];
            acc->kind  = s.kind;
            acc->count = 0;
            acc->min   =  (float)INFINITY;
            acc->max   = -(float)INFINITY;
            acc->sum   = 0.0;
        }
        acc->count++;
        if (s.value < acc->min) acc->min = s.value;
        if (s.value > acc->max) acc->max = s.value;
        acc->sum += (double)s.value;
        ca_biosignal_sample_free(&s);
    }
    ca_biosignal_stream_destroy(st);

    if (nacc == 0) { free(accs); return 0; } /* empty dictionary */

    ca_biosignal_kind_stats_t *entries =
        (ca_biosignal_kind_stats_t *)malloc(nacc * sizeof(*entries));
    if (!entries) { free(accs); return -1; }
    for (size_t i = 0; i < nacc; ++i) {
        entries[i].kind = accs[i].kind;
        entries[i].stats.sample_count = accs[i].count;
        entries[i].stats.min  = accs[i].min;
        entries[i].stats.max  = accs[i].max;
        entries[i].stats.mean = accs[i].count == 0
                                    ? 0.0f
                                    : (float)(accs[i].sum / accs[i].count);
    }
    free(accs);
    out->entries = entries;
    out->count = nacc;
    return 0;
}
