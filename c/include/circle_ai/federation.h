#ifndef CIRCLE_AI_FEDERATION_H
#define CIRCLE_AI_FEDERATION_H

/*
 * federation.h — CircleAI.Federation (C11 port of ModelDelta.cs /
 * FederationRound.cs / IFederationParticipant.cs / IFederationDeltaDispatcher.cs
 * / IFederationAggregator.cs / FederatedAveraging.cs /
 * InMemoryFederationAggregator.cs).
 *
 *   Enums   : RoundStatus { Open, Aggregating, Committed, Aborted };
 *             DeltaDispatchOutcome { Accepted=0, SignatureInvalid=1,
 *               Duplicate=2, RoundUnknown=3, RoundClosed=4 }.
 *   Records : ModelDelta(Guid Id, Guid RoundId, ContributorUhid, ModelId,
 *               FromVersion, byte[] DeltaPayload, int SampleCount,
 *               byte[] Signature, DateTimeOffset SubmittedAt);
 *             FederationRound(Guid Id, ModelId, FromVersion, ToVersion,
 *               MinParticipants, MaxParticipants, CurrentParticipantCount,
 *               RoundStatus Status, OpenedAt, CommittedAt?).
 *   Helper  : FederatedAveraging.Average(deltas) — sample-size-weighted mean of
 *               the payloads read as little-endian IEEE-754 float[]; plus
 *               EncodeFloats / DecodeFloats.
 *   Aggreg. : IFederationAggregator -> InMemoryFederationAggregator(validator):
 *               OpenRound(modelId, from, to, min, max) mints a Guid round;
 *               SubmitDelta rejects unknown round (error), no-ops on empty
 *               payload, rejects a closed / full round; TryCommit runs the
 *               injected signature validator over the deltas, needs >= Min valid,
 *               averages them (falling back to the median payload by SampleCount
 *               when encodings are inconsistent), flips to Committed and returns
 *               the aggregated payload (idempotent). GetRound -> snapshot.
 *   Dispatch: IFederationDeltaDispatcher -> verify (validator) + dedup (by delta
 *               Id within the round) + submit in one call, returning a
 *               DeltaDispatchOutcome (no throw on rejection).
 *   Particip: IFederationParticipant (vtable) — ProduceDelta(round) -> delta;
 *               ApplyAggregatedModel(modelId, newVersion, payload) -> bool.
 *
 * Guid Id/RoundId are caller-supplied strings; OpenRound mints one via
 * ca_uuid_v4. Signatures are opaque bytes verified by the injected validator.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, owning byte/str fields with
 * matching *_free, deep-copy getters, errors via NULL. CommittedAt nullable via
 * has_*. *At as int64 Unix ms UTC. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CA_FED_ROUND_OPEN        = 0,
    CA_FED_ROUND_AGGREGATING = 1,
    CA_FED_ROUND_COMMITTED   = 2,
    CA_FED_ROUND_ABORTED     = 3
} ca_fed_round_status_t;

typedef enum {
    CA_FED_ACCEPTED         = 0,
    CA_FED_SIGNATURE_INVALID = 1,
    CA_FED_DUPLICATE        = 2,
    CA_FED_ROUND_UNKNOWN    = 3,
    CA_FED_ROUND_CLOSED     = 4
} ca_fed_dispatch_outcome_t;

/* ModelDelta(...). Payload/Signature are owned byte blobs. */
typedef struct {
    char    *id;              /* owned, non-null (Guid string) */
    char    *round_id;        /* owned, non-null (Guid string) */
    char    *contributor_uhid;/* owned, non-null */
    char    *model_id;        /* owned, non-null */
    char    *from_version;    /* owned, non-null */
    uint8_t *delta_payload;   /* owned (may be NULL when len 0) */
    size_t   delta_payload_len;
    int      sample_count;
    uint8_t *signature;       /* owned (may be NULL when len 0) */
    size_t   signature_len;
    int64_t  submitted_at_ms;
} ca_fed_delta_t;

void ca_fed_delta_free(ca_fed_delta_t *d);

/* FederationRound(...). CommittedAt nullable via has_committed_at. */
typedef struct {
    char                 *id;           /* owned, non-null (Guid string) */
    char                 *model_id;     /* owned, non-null */
    char                 *from_version; /* owned, non-null */
    char                 *to_version;   /* owned, non-null */
    int                   min_participants;
    int                   max_participants;
    int                   current_participant_count;
    ca_fed_round_status_t status;
    int64_t               opened_at_ms;
    bool                  has_committed_at; /* false == C# null CommittedAt */
    int64_t               committed_at_ms;
} ca_fed_round_t;

void ca_fed_round_free(ca_fed_round_t *r);

/* ── FederatedAveraging ─────────────────────────────────────────────────── */

/* Average(deltas) -> fresh owned little-endian float[] bytes into *out /
 * *out_len. 0 on success, -1 on bad args (null / empty list / empty or
 * non-multiple-of-4 or mismatched payloads / negative sample / zero total
 * sample weight) or OOM. Mirrors FederatedAveraging.Average's ArgumentException
 * cases as failures. */
int ca_fed_average(const ca_fed_delta_t *deltas, size_t count,
                   uint8_t **out, size_t *out_len);

/* Encode a float[] to little-endian bytes. NULL on OOM/bad args. *out_len set. */
uint8_t *ca_fed_encode_floats(const float *values, size_t n, size_t *out_len);
/* Decode little-endian bytes to a float[]. NULL on bad args (len % 4 != 0)/OOM.
 * *out_count set. */
float *ca_fed_decode_floats(const uint8_t *payload, size_t len, size_t *out_count);

/* ── Signature validator (injected) ─────────────────────────────────────── */

/* Returns true when the delta's signature is valid. Pass a "_ => true" analogue
 * in tests. */
typedef bool (*ca_fed_validator_fn)(void *ctx, const ca_fed_delta_t *delta);

/* ── IFederationAggregator -> InMemoryFederationAggregator ──────────────── */

typedef struct ca_fed_aggregator ca_fed_aggregator_t;

/* Construct with a signature validator (required). NULL on bad args/OOM. */
ca_fed_aggregator_t *ca_fed_aggregator_create(ca_fed_validator_fn validator,
                                              void *ctx);
void ca_fed_aggregator_destroy(ca_fed_aggregator_t *a);

/* OpenRound(modelId, from, to, min, max) -> fill *out (owned; free with
 * ca_fed_round_free). A fresh round Guid is minted. now_ms is the OpenedAt
 * clock. 0 on success, -1 on bad args (empty strings / min <= 0 / max < min)
 * or OOM. */
int ca_fed_aggregator_open_round(ca_fed_aggregator_t *a, const char *model_id,
                                 const char *from_version,
                                 const char *to_version, int min_participants,
                                 int max_participants, int64_t now_ms,
                                 ca_fed_round_t *out);

/* SubmitDelta(delta). 0 on success (including the empty-payload no-op), -1 on
 * bad args / unknown round / round-not-Open / round-full. */
int ca_fed_aggregator_submit(ca_fed_aggregator_t *a, const ca_fed_delta_t *delta);

/* TryCommit(roundId, now_ms) -> aggregated payload into *out / *out_len when
 * >= Min valid deltas exist (owned; free with free()). When not enough valid
 * deltas, *out is NULL and *out_len 0 and the return is 0 (the C# returns
 * null). Aborted round -> NULL. Returns 0 on success, -1 on bad args / unknown
 * round / OOM. Idempotent: re-committing returns the same payload. */
int ca_fed_aggregator_try_commit(ca_fed_aggregator_t *a, const char *round_id,
                                 int64_t now_ms, uint8_t **out, size_t *out_len);

/* GetRound(roundId) -> fresh copy into *out, true; false on unknown round /
 * bad args. */
bool ca_fed_aggregator_get_round(const ca_fed_aggregator_t *a,
                                 const char *round_id, ca_fed_round_t *out);

/* Total rounds tracked (diagnostic; RoundCount). */
size_t ca_fed_aggregator_round_count(const ca_fed_aggregator_t *a);

/* ── IFederationDeltaDispatcher ─────────────────────────────────────────── */

/* VerifyAndSubmit(delta): verify signature (via the aggregator's validator),
 * dedup by delta Id within the round, then submit. Writes the outcome into
 * *outcome. Returns 0 on success (even for a rejection outcome), -1 on bad
 * args / OOM. The dispatcher shares the aggregator's validator. */
int ca_fed_dispatcher_verify_and_submit(ca_fed_aggregator_t *a,
                                        const ca_fed_delta_t *delta,
                                        ca_fed_dispatch_outcome_t *outcome);

/* ── IFederationParticipant (injected vtable) ───────────────────────────── */

/* ProduceDelta(round) -> fill *out (owned). 0 / -1. */
typedef int (*ca_fed_produce_fn)(void *ctx, const ca_fed_round_t *round,
                                 ca_fed_delta_t *out);
/* ApplyAggregatedModel(modelId, newVersion, payload) -> bool. */
typedef bool (*ca_fed_apply_fn)(void *ctx, const char *model_id,
                                const char *new_version,
                                const uint8_t *payload, size_t payload_len);

typedef struct {
    ca_fed_produce_fn produce;
    ca_fed_apply_fn   apply;
    void             *ctx;
} ca_fed_participant_t;

int  ca_fed_participant_produce(const ca_fed_participant_t *p,
                                const ca_fed_round_t *round, ca_fed_delta_t *out);
bool ca_fed_participant_apply(const ca_fed_participant_t *p,
                              const char *model_id, const char *new_version,
                              const uint8_t *payload, size_t payload_len);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_FEDERATION_H */
