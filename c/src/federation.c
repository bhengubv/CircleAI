/*
 * federation.c — CircleAI.Federation (C11 port).
 *
 * FederatedAveraging: sample-size-weighted mean over payloads read as
 * little-endian IEEE-754 float[]. InMemoryFederationAggregator: rounds + their
 * accepted deltas in linear arrays, commit runs the injected validator and
 * averages (median-payload fallback on encoding mismatch). Dispatcher composes
 * verify + dedup + submit.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/federation.h"
#include "circle_ai/security.h" /* ca_uuid_v4, CA_UUID_STR_LEN */
#include "board_common.h"

#include <string.h>

/* ── little-endian float32 read/write ───────────────────────────────────── */

static float read_f32_le(const uint8_t *p) {
    uint32_t bits = (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
                    ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
    float f;
    memcpy(&f, &bits, sizeof(f));
    return f;
}
static void write_f32_le(uint8_t *p, float f) {
    uint32_t bits;
    memcpy(&bits, &f, sizeof(bits));
    p[0] = (uint8_t)(bits & 0xFF);
    p[1] = (uint8_t)((bits >> 8) & 0xFF);
    p[2] = (uint8_t)((bits >> 16) & 0xFF);
    p[3] = (uint8_t)((bits >> 24) & 0xFF);
}

/* ── byte-blob helper ───────────────────────────────────────────────────── */

/* Copy `n` bytes (n may be 0 -> NULL). false on OOM. */
static bool bytes_copy(uint8_t **out, const uint8_t *src, size_t n) {
    *out = NULL;
    if (n == 0) return true;
    uint8_t *b = (uint8_t *)malloc(n);
    if (!b) return false;
    if (src) memcpy(b, src, n);
    else memset(b, 0, n);
    *out = b;
    return true;
}

/* ── ModelDelta ─────────────────────────────────────────────────────────── */

void ca_fed_delta_free(ca_fed_delta_t *d) {
    if (!d) return;
    free(d->id);
    free(d->round_id);
    free(d->contributor_uhid);
    free(d->model_id);
    free(d->from_version);
    free(d->delta_payload);
    free(d->signature);
    memset(d, 0, sizeof(*d));
}

static bool delta_copy(ca_fed_delta_t *dst, const ca_fed_delta_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->sample_count    = src->sample_count;
    dst->submitted_at_ms = src->submitted_at_ms;
    dst->id               = cab_strdup_empty(src->id);
    dst->round_id         = cab_strdup_empty(src->round_id);
    dst->contributor_uhid = cab_strdup_empty(src->contributor_uhid);
    dst->model_id         = cab_strdup_empty(src->model_id);
    dst->from_version     = cab_strdup_empty(src->from_version);
    bool ok = dst->id && dst->round_id && dst->contributor_uhid &&
              dst->model_id && dst->from_version;
    if (ok) ok = bytes_copy(&dst->delta_payload, src->delta_payload, src->delta_payload_len);
    if (ok) { dst->delta_payload_len = src->delta_payload_len;
              ok = bytes_copy(&dst->signature, src->signature, src->signature_len); }
    if (ok) dst->signature_len = src->signature_len;
    if (!ok) { ca_fed_delta_free(dst); return false; }
    return true;
}

/* ── FederationRound ────────────────────────────────────────────────────── */

void ca_fed_round_free(ca_fed_round_t *r) {
    if (!r) return;
    free(r->id);
    free(r->model_id);
    free(r->from_version);
    free(r->to_version);
    memset(r, 0, sizeof(*r));
}
static bool round_copy(ca_fed_round_t *dst, const ca_fed_round_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->min_participants          = src->min_participants;
    dst->max_participants          = src->max_participants;
    dst->current_participant_count = src->current_participant_count;
    dst->status                    = src->status;
    dst->opened_at_ms              = src->opened_at_ms;
    dst->has_committed_at          = src->has_committed_at;
    dst->committed_at_ms           = src->committed_at_ms;
    dst->id           = cab_strdup_empty(src->id);
    dst->model_id     = cab_strdup_empty(src->model_id);
    dst->from_version = cab_strdup_empty(src->from_version);
    dst->to_version   = cab_strdup_empty(src->to_version);
    if (!dst->id || !dst->model_id || !dst->from_version || !dst->to_version) {
        ca_fed_round_free(dst);
        return false;
    }
    return true;
}

/* ── FederatedAveraging ─────────────────────────────────────────────────── */

int ca_fed_average(const ca_fed_delta_t *deltas, size_t count,
                   uint8_t **out, size_t *out_len) {
    if (out) *out = NULL;
    if (out_len) *out_len = 0;
    if (!deltas || count == 0 || !out || !out_len) return -1;

    size_t expected = deltas[0].delta_payload_len;
    if (expected == 0) return -1;
    if (expected % sizeof(float) != 0) return -1;
    for (size_t i = 1; i < count; ++i)
        if (deltas[i].delta_payload_len != expected) return -1;

    size_t float_count = expected / sizeof(float);
    long long total_samples = 0;
    for (size_t i = 0; i < count; ++i) {
        if (deltas[i].sample_count < 0) return -1;
        total_samples += deltas[i].sample_count;
    }
    if (total_samples == 0) return -1;

    double *acc = (double *)calloc(float_count, sizeof(double));
    if (!acc) return -1;
    for (size_t d = 0; d < count; ++d) {
        double weight = (double)deltas[d].sample_count / (double)total_samples;
        const uint8_t *pay = deltas[d].delta_payload;
        for (size_t i = 0; i < float_count; ++i)
            acc[i] += (double)read_f32_le(pay + i * sizeof(float)) * weight;
    }

    uint8_t *res = (uint8_t *)malloc(expected);
    if (!res) { free(acc); return -1; }
    for (size_t i = 0; i < float_count; ++i)
        write_f32_le(res + i * sizeof(float), (float)acc[i]);
    free(acc);
    *out = res;
    *out_len = expected;
    return 0;
}

uint8_t *ca_fed_encode_floats(const float *values, size_t n, size_t *out_len) {
    if (!out_len) return NULL;
    *out_len = 0;
    if (!values && n) return NULL;
    size_t len = n * sizeof(float);
    uint8_t *b = (uint8_t *)malloc(len ? len : 1);
    if (!b) return NULL;
    for (size_t i = 0; i < n; ++i) write_f32_le(b + i * sizeof(float), values[i]);
    *out_len = len;
    return b;
}

float *ca_fed_decode_floats(const uint8_t *payload, size_t len, size_t *out_count) {
    if (!out_count) return NULL;
    *out_count = 0;
    if ((!payload && len) || len % sizeof(float) != 0) return NULL;
    size_t n = len / sizeof(float);
    float *v = (float *)malloc(n ? n * sizeof(float) : 1);
    if (!v) return NULL;
    for (size_t i = 0; i < n; ++i) v[i] = read_f32_le(payload + i * sizeof(float));
    *out_count = n;
    return v;
}

/* ── InMemoryFederationAggregator ───────────────────────────────────────── */

typedef struct {
    ca_fed_round_t  snapshot;      /* owned */
    ca_fed_delta_t *deltas;        /* owned */
    size_t          d_count, d_cap;
    uint8_t        *committed_payload; /* owned, NULL until committed */
    size_t          committed_len;
} round_state_t;

struct ca_fed_aggregator {
    round_state_t     *rounds;
    size_t             count, cap;
    ca_fed_validator_fn validator;
    void              *validator_ctx;
};

ca_fed_aggregator_t *ca_fed_aggregator_create(ca_fed_validator_fn validator,
                                              void *ctx) {
    if (!validator) return NULL;
    ca_fed_aggregator_t *a =
        (ca_fed_aggregator_t *)calloc(1, sizeof(ca_fed_aggregator_t));
    if (!a) return NULL;
    a->validator     = validator;
    a->validator_ctx = ctx;
    return a;
}

static void round_state_free(round_state_t *rs) {
    ca_fed_round_free(&rs->snapshot);
    for (size_t i = 0; i < rs->d_count; ++i) ca_fed_delta_free(&rs->deltas[i]);
    free(rs->deltas);
    free(rs->committed_payload);
}

void ca_fed_aggregator_destroy(ca_fed_aggregator_t *a) {
    if (!a) return;
    for (size_t i = 0; i < a->count; ++i) round_state_free(&a->rounds[i]);
    free(a->rounds);
    free(a);
}

static round_state_t *find_round(const ca_fed_aggregator_t *a,
                                 const char *round_id) {
    for (size_t i = 0; i < a->count; ++i)
        if (cab_ord_eq(a->rounds[i].snapshot.id, round_id))
            return &a->rounds[i];
    return NULL;
}

int ca_fed_aggregator_open_round(ca_fed_aggregator_t *a, const char *model_id,
                                 const char *from_version,
                                 const char *to_version, int min_participants,
                                 int max_participants, int64_t now_ms,
                                 ca_fed_round_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!a || !out) return -1;
    if (cab_is_ws(model_id) || cab_is_ws(from_version) || cab_is_ws(to_version))
        return -1;
    if (min_participants <= 0 || max_participants < min_participants) return -1;

    char uuid[CA_UUID_STR_LEN];
    ca_uuid_v4(uuid);

    ca_fed_round_t r;
    memset(&r, 0, sizeof(r));
    r.id           = (char *)uuid;
    r.model_id     = (char *)model_id;
    r.from_version = (char *)from_version;
    r.to_version   = (char *)to_version;
    r.min_participants = min_participants;
    r.max_participants = max_participants;
    r.current_participant_count = 0;
    r.status = CA_FED_ROUND_OPEN;
    r.opened_at_ms = now_ms;
    r.has_committed_at = false;

    if (a->count == a->cap) {
        size_t nc = a->cap ? a->cap * 2 : 4;
        void *n = realloc(a->rounds, nc * sizeof(*a->rounds));
        if (!n) return -1;
        a->rounds = (round_state_t *)n;
        a->cap = nc;
    }
    round_state_t *rs = &a->rounds[a->count];
    memset(rs, 0, sizeof(*rs));
    if (!round_copy(&rs->snapshot, &r)) return -1;
    a->count++;

    return round_copy(out, &rs->snapshot) ? 0 : -1;
}

int ca_fed_aggregator_submit(ca_fed_aggregator_t *a, const ca_fed_delta_t *delta) {
    if (!a || !delta) return -1;
    round_state_t *rs = find_round(a, delta->round_id);
    if (!rs) return -1; /* KeyNotFoundException: round not open */

    if (delta->delta_payload_len == 0) return 0; /* empty payload no-op */

    if (rs->snapshot.status != CA_FED_ROUND_OPEN) return -1; /* not accepting */
    if (rs->d_count >= (size_t)rs->snapshot.max_participants) return -1; /* full */

    if (rs->d_count == rs->d_cap) {
        size_t nc = rs->d_cap ? rs->d_cap * 2 : 4;
        void *n = realloc(rs->deltas, nc * sizeof(*rs->deltas));
        if (!n) return -1;
        rs->deltas = (ca_fed_delta_t *)n;
        rs->d_cap = nc;
    }
    ca_fed_delta_t copy;
    if (!delta_copy(&copy, delta)) return -1;
    rs->deltas[rs->d_count++] = copy;
    rs->snapshot.current_participant_count = (int)rs->d_count;
    return 0;
}

/* Median payload by SampleCount (ordered ascending, take index count/2). Copies
 * that delta's payload into out / out_len. false on OOM. */
static bool fallback_median_payload(const round_state_t *rs, uint8_t **out,
                                    size_t *out_len,
                                    const size_t *valid_idx, size_t valid_n) {
    /* order valid indices ascending by SampleCount (stable insertion) */
    size_t *idx = (size_t *)malloc(valid_n * sizeof(size_t));
    if (!idx) return false;
    memcpy(idx, valid_idx, valid_n * sizeof(size_t));
    for (size_t i = 1; i < valid_n; ++i) {
        size_t key = idx[i];
        int kv = rs->deltas[key].sample_count;
        size_t j = i;
        while (j > 0 && rs->deltas[idx[j - 1]].sample_count > kv) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
    const ca_fed_delta_t *med = &rs->deltas[idx[valid_n / 2]];
    free(idx);
    return bytes_copy(out, med->delta_payload, med->delta_payload_len) &&
           (*out_len = med->delta_payload_len, true);
}

int ca_fed_aggregator_try_commit(ca_fed_aggregator_t *a, const char *round_id,
                                 int64_t now_ms, uint8_t **out, size_t *out_len) {
    if (out) *out = NULL;
    if (out_len) *out_len = 0;
    if (!a || !round_id || !out || !out_len) return -1;
    round_state_t *rs = find_round(a, round_id);
    if (!rs) return -1; /* KeyNotFoundException: round unknown */

    if (rs->snapshot.status == CA_FED_ROUND_COMMITTED) {
        /* idempotent: re-return committed payload */
        if (!bytes_copy(out, rs->committed_payload, rs->committed_len)) return -1;
        *out_len = rs->committed_len;
        return 0;
    }
    if (rs->snapshot.status == CA_FED_ROUND_ABORTED) return 0; /* null */

    /* collect valid delta indices */
    size_t *valid = rs->d_count ? (size_t *)malloc(rs->d_count * sizeof(size_t)) : NULL;
    if (rs->d_count && !valid) return -1;
    size_t valid_n = 0;
    for (size_t i = 0; i < rs->d_count; ++i)
        if (a->validator(a->validator_ctx, &rs->deltas[i])) valid[valid_n++] = i;

    if (valid_n < (size_t)rs->snapshot.min_participants) {
        free(valid);
        return 0; /* null: not enough valid deltas */
    }

    rs->snapshot.status = CA_FED_ROUND_AGGREGATING;

    /* Build a contiguous array of the valid deltas for averaging. */
    ca_fed_delta_t *subset =
        (ca_fed_delta_t *)malloc(valid_n * sizeof(*subset));
    if (!subset) { free(valid); return -1; }
    for (size_t i = 0; i < valid_n; ++i) subset[i] = rs->deltas[valid[i]];

    uint8_t *agg = NULL;
    size_t agg_len = 0;
    int rc = ca_fed_average(subset, valid_n, &agg, &agg_len);
    free(subset);
    if (rc != 0) {
        /* encoding inconsistent -> median fallback */
        if (!fallback_median_payload(rs, &agg, &agg_len, valid, valid_n)) {
            free(valid);
            return -1;
        }
    }
    free(valid);

    free(rs->committed_payload);
    rs->committed_payload = agg;
    rs->committed_len = agg_len;
    rs->snapshot.status = CA_FED_ROUND_COMMITTED;
    rs->snapshot.has_committed_at = true;
    rs->snapshot.committed_at_ms = now_ms;

    if (!bytes_copy(out, agg, agg_len)) return -1;
    *out_len = agg_len;
    return 0;
}

bool ca_fed_aggregator_get_round(const ca_fed_aggregator_t *a,
                                 const char *round_id, ca_fed_round_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!a || !round_id || !out) return false;
    round_state_t *rs = find_round(a, round_id);
    if (!rs) return false;
    return round_copy(out, &rs->snapshot);
}

size_t ca_fed_aggregator_round_count(const ca_fed_aggregator_t *a) {
    return a ? a->count : 0;
}

/* ── IFederationDeltaDispatcher ─────────────────────────────────────────── */

int ca_fed_dispatcher_verify_and_submit(ca_fed_aggregator_t *a,
                                        const ca_fed_delta_t *delta,
                                        ca_fed_dispatch_outcome_t *outcome) {
    if (!a || !delta || !outcome) return -1;

    round_state_t *rs = find_round(a, delta->round_id);
    if (!rs) { *outcome = CA_FED_ROUND_UNKNOWN; return 0; }
    if (rs->snapshot.status != CA_FED_ROUND_OPEN) { *outcome = CA_FED_ROUND_CLOSED; return 0; }

    if (!a->validator(a->validator_ctx, delta)) {
        *outcome = CA_FED_SIGNATURE_INVALID;
        return 0;
    }
    /* dedup by delta Id within the round */
    for (size_t i = 0; i < rs->d_count; ++i) {
        if (cab_ord_eq(rs->deltas[i].id, delta->id)) {
            *outcome = CA_FED_DUPLICATE;
            return 0;
        }
    }
    if (ca_fed_aggregator_submit(a, delta) != 0) return -1;
    *outcome = CA_FED_ACCEPTED;
    return 0;
}

/* ── IFederationParticipant ─────────────────────────────────────────────── */

int ca_fed_participant_produce(const ca_fed_participant_t *p,
                               const ca_fed_round_t *round, ca_fed_delta_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!p || !p->produce || !round || !out) return -1;
    return p->produce(p->ctx, round, out);
}
bool ca_fed_participant_apply(const ca_fed_participant_t *p,
                              const char *model_id, const char *new_version,
                              const uint8_t *payload, size_t payload_len) {
    if (!p || !p->apply) return false;
    return p->apply(p->ctx, model_id, new_version, payload, payload_len);
}
