#ifndef CIRCLE_AI_AUTONOMOUS_BIZ_H
#define CIRCLE_AI_AUTONOMOUS_BIZ_H

/*
 * autonomous_biz.h — CircleAI.AutonomousBiz (C11 port of Contracts.cs +
 * InMemoryAutonomousBiz.cs + NullImplementations.cs).
 *
 *   Records : TreasurySnapshot(decimal Balance, Currency, DateTimeOffset AtUtc);
 *             RevenueEvent(EventId, decimal Amount, Currency, Source,
 *                          DateTimeOffset AtUtc);
 *             AutonomousDecision(DecisionId, Rationale, ChosenAction,
 *                                DateTimeOffset AtUtc).
 *   Revenue : IRevenueLoop -> InMemoryRevenueLoop — Publish(e) appends to a kept
 *               history and fans out to subscribers (snapshot first); Subscribe
 *               (handler) -> token; Read(since) events with AtUtc >= since.
 *               BackendId "in-memory". Null loop -> Subscribe noop, Read empty.
 *   Treasury: ITreasury -> InMemoryTreasury(loop, currency="ZAR") — GetSnapshot
 *               sums Amount over the loop's events whose Currency matches
 *               (case-insensitive), stamping now. BackendId "in-memory". Null ->
 *               {0, "ZAR", MinValue}.
 *   Decision: IDecisionLog -> InMemoryDecisionLog — Append(d), Read(limit=100)
 *               newest-first by AtUtc, Take(limit) (limit <= 0 is an error).
 *               BackendId "in-memory". Null -> Append no-op, Read empty.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Money as
 * ca_abiz_decimal_t (int64 scaled 1e6). AtUtc as int64 Unix ms UTC. Subscriber
 * fan-out snapshots the list first. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef int64_t ca_abiz_decimal_t;
#define CA_ABIZ_DECIMAL_SCALE 1000000LL

/* TreasurySnapshot(Balance, Currency, AtUtc). */
typedef struct {
    ca_abiz_decimal_t balance;
    char             *currency;  /* owned, non-null */
    int64_t           at_utc_ms;
} ca_abiz_treasury_snapshot_t;

void ca_abiz_treasury_snapshot_free(ca_abiz_treasury_snapshot_t *s);

/* RevenueEvent(EventId, Amount, Currency, Source, AtUtc). */
typedef struct {
    char             *event_id;  /* owned, non-null */
    ca_abiz_decimal_t amount;
    char             *currency;  /* owned, non-null */
    char             *source;    /* owned, non-null */
    int64_t           at_utc_ms;
} ca_abiz_revenue_event_t;

void ca_abiz_revenue_event_free(ca_abiz_revenue_event_t *e);
void ca_abiz_revenue_event_free_array(ca_abiz_revenue_event_t *arr, size_t count);

/* AutonomousDecision(DecisionId, Rationale, ChosenAction, AtUtc). */
typedef struct {
    char   *decision_id;   /* owned, non-null */
    char   *rationale;     /* owned, non-null */
    char   *chosen_action; /* owned, non-null */
    int64_t at_utc_ms;
} ca_abiz_decision_t;

void ca_abiz_decision_free(ca_abiz_decision_t *d);
void ca_abiz_decision_free_array(ca_abiz_decision_t *arr, size_t count);

/* ── IRevenueLoop -> InMemoryRevenueLoop ────────────────────────────────── */

typedef struct ca_abiz_revenue_loop ca_abiz_revenue_loop_t;
typedef struct ca_abiz_revenue_sub ca_abiz_revenue_sub_t;

/* Revenue subscriber. Receives a borrowed event (valid for the call only). */
typedef void (*ca_abiz_revenue_handler_fn)(void *ctx,
                                           const ca_abiz_revenue_event_t *e);

ca_abiz_revenue_loop_t *ca_abiz_revenue_loop_create(void); /* NULL on OOM */
void ca_abiz_revenue_loop_destroy(ca_abiz_revenue_loop_t *l);
const char *ca_abiz_revenue_loop_backend_id(const ca_abiz_revenue_loop_t *l);

/* Publish(e) — appends to history + fans out (snapshot first). Returns the
 * subscriber count notified, or -1 on bad args/OOM. */
int ca_abiz_revenue_loop_publish(ca_abiz_revenue_loop_t *l,
                                 const ca_abiz_revenue_event_t *e);
/* Subscribe(handler) -> owned token (dispose to unsubscribe). NULL on bad
 * args/OOM. */
ca_abiz_revenue_sub_t *ca_abiz_revenue_loop_subscribe(ca_abiz_revenue_loop_t *l,
                                                      ca_abiz_revenue_handler_fn h,
                                                      void *ctx);
void ca_abiz_revenue_loop_unsubscribe(ca_abiz_revenue_loop_t *l,
                                      ca_abiz_revenue_sub_t *sub);
/* Read(since) -> fresh owned array (*out_count) with AtUtc >= since, in publish
 * order. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_abiz_revenue_event_t *ca_abiz_revenue_loop_read(const ca_abiz_revenue_loop_t *l,
                                                   int64_t since_ms,
                                                   size_t *out_count);
/* Drain the next buffered event from a subscription's cursor into *out (freshly
 * owned; free with ca_abiz_revenue_event_free). true if produced. */
bool ca_abiz_revenue_sub_next(ca_abiz_revenue_sub_t *sub,
                              ca_abiz_revenue_event_t *out);
size_t ca_abiz_revenue_sub_pending(const ca_abiz_revenue_sub_t *sub);

const char *ca_abiz_null_revenue_loop_backend_id(void); /* "null" */

/* ── ITreasury -> InMemoryTreasury ──────────────────────────────────────── */

/* GetSnapshot over `loop`'s events, summing Amount where Currency matches
 * `currency` (case-insensitive); stamps `now_ms` as AtUtc. currency NULL ->
 * "ZAR". Fills *out (owned; free with ca_abiz_treasury_snapshot_free). 0 on
 * success, -1 on bad args/OOM. BackendId "in-memory". */
int ca_abiz_treasury_snapshot(const ca_abiz_revenue_loop_t *loop,
                              const char *currency, int64_t now_ms,
                              ca_abiz_treasury_snapshot_t *out);
const char *ca_abiz_treasury_backend_id(void); /* "in-memory" */

/* Null treasury: {0, "ZAR", DateTimeOffset.MinValue}. 0 / -1 on OOM. */
int ca_abiz_null_treasury_snapshot(ca_abiz_treasury_snapshot_t *out);
const char *ca_abiz_null_treasury_backend_id(void); /* "null" */

/* ── IDecisionLog -> InMemoryDecisionLog ────────────────────────────────── */

typedef struct ca_abiz_decision_log ca_abiz_decision_log_t;

ca_abiz_decision_log_t *ca_abiz_decision_log_create(void); /* NULL on OOM */
void ca_abiz_decision_log_destroy(ca_abiz_decision_log_t *l);
const char *ca_abiz_decision_log_backend_id(const ca_abiz_decision_log_t *l);

/* Append(d). 0 / -1. */
int ca_abiz_decision_log_append(ca_abiz_decision_log_t *l,
                                const ca_abiz_decision_t *d);
/* Read(limit) newest-first by AtUtc, Take(limit). NULL + 0 empty; NULL +
 * SIZE_MAX on error (limit <= 0). */
ca_abiz_decision_t *ca_abiz_decision_log_read(const ca_abiz_decision_log_t *l,
                                              int limit, size_t *out_count);

const char *ca_abiz_null_decision_log_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_AUTONOMOUS_BIZ_H */
