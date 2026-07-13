#ifndef CIRCLE_AI_NET_GRPC_H
#define CIRCLE_AI_NET_GRPC_H

/*
 * net_grpc.h — CircleAI.Networking.Grpc (C11 port).
 *
 * The gRPC channel network transport. Ports CircleAI.Networking.Grpc 1:1:
 *
 *   Enum      : GrpcChannelState
 *   Records   : GrpcChannelDescriptor, GrpcRetryPolicy, GrpcCallSummary
 *   Presets   : GrpcRetryPolicies (Default / Aggressive / NoRetry)
 *   Metrics   : InMemoryGrpcCallMetrics (channels + states + call log; LogCall
 *               returns a monotonic "grpc-N" id)
 *   Transport : GrpcNetworkTransport — INetworkTransport over a gRPC channel.
 *               Kind==Grpc, IsAvailable==running. StartAsync/StopAsync flip the
 *               running flag. SendAsync is NOT a generic send path — it returns
 *               the "not supported" signal (callers use the channel directly for
 *               typed proto clients). ReceiveAsync yields nothing.
 *
 * The GrpcChannel itself (a native/managed object) is the injected dependency;
 * here the transport is constructed from an address + a descriptor and exposes
 * that descriptor as its "channel" view. Send returns CA_GRPC_SEND_NOT_SUPPORTED.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no hashtable, no pthreads. Durations are milliseconds; timestamps Unix
 * ms UTC, passed in.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "networking.h"   /* ca_network_transport_t, ca_network_payload_t */

#ifdef __cplusplus
extern "C" {
#endif

/* SendAsync return code: gRPC has no generic send path (C# throws
 * NotSupportedException). The transport's send() returns this value. */
#define CA_GRPC_SEND_NOT_SUPPORTED (-2)

/* ===========================================================================
 * GrpcChannelState
 * =========================================================================== */

typedef enum {
    CA_GRPC_STATE_IDLE              = 0,
    CA_GRPC_STATE_CONNECTING        = 1,
    CA_GRPC_STATE_READY             = 2,
    CA_GRPC_STATE_TRANSIENT_FAILURE = 3,
    CA_GRPC_STATE_SHUTDOWN          = 4
} ca_grpc_channel_state_t;

/* ===========================================================================
 * GrpcConnectionState
 *
 * Lifecycle state of a managed gRPC connection as reconnection is driven. A
 * distinct enum from GrpcChannelState (same members) mirroring the C# type.
 * =========================================================================== */

typedef enum {
    CA_GRPC_CONNECTION_IDLE              = 0,
    CA_GRPC_CONNECTION_CONNECTING        = 1,
    CA_GRPC_CONNECTION_READY             = 2,
    CA_GRPC_CONNECTION_TRANSIENT_FAILURE = 3,
    CA_GRPC_CONNECTION_SHUTDOWN          = 4
} ca_grpc_connection_state_t;

/* ===========================================================================
 * GrpcReconnectPolicy(MaxAttempts, InitialBackoff, BackoffMultiplier, MaxBackoff)
 *
 * Reconnection strategy for a managed gRPC channel: how many attempts to make and
 * how to grow the backoff between them. Durations are milliseconds. A value type
 * (plain struct, no owned fields) so it is passed/returned by value.
 * =========================================================================== */

typedef struct {
    int     max_attempts;
    int64_t initial_backoff_ms;
    double  backoff_multiplier;
    int64_t max_backoff_ms;
} ca_grpc_reconnect_policy_t;

/* Default: 5 attempts, 200ms growing ×2 up to a 30s (30000ms) ceiling. */
ca_grpc_reconnect_policy_t ca_grpc_reconnect_policy_default(void);

/* BackoffFor(attempt) — backoff before a given 1-based attempt:
 * InitialBackoff × Multiplier^(attempt-1), capped at MaxBackoff. Attempt 1
 * returns InitialBackoff. Overflow-safe: an infinite or over-cap scaled value
 * returns MaxBackoff. `attempt` is 1-based; attempt < 1 is invalid and returns
 * -1 (the C# throws ArgumentOutOfRangeException). Result in milliseconds. */
int64_t ca_grpc_reconnect_policy_backoff_for(
    const ca_grpc_reconnect_policy_t *p, int attempt);

/* ShouldRetry(attempt) — true when the 1-based attempt is still within budget
 * (attempt < MaxAttempts). */
bool ca_grpc_reconnect_policy_should_retry(
    const ca_grpc_reconnect_policy_t *p, int attempt);

/* ===========================================================================
 * GrpcDeadline — deadline math for gRPC calls (all times Unix ms UTC / ms).
 * =========================================================================== */

/* FromTimeout(timeout, nowUtc) — absolute deadline for a call started at nowUtc
 * with the given timeout. timeout < 0 is invalid: *out_deadline_ms is left
 * untouched and false is returned (the C# throws ArgumentOutOfRangeException).
 * Otherwise writes nowUtc + timeout and returns true. */
bool ca_grpc_deadline_from_timeout(int64_t timeout_ms, int64_t now_utc_ms,
                                   int64_t *out_deadline_ms);

/* Remaining(deadlineUtc, nowUtc) — time left before the deadline, clamped to 0
 * once passed. Milliseconds. */
int64_t ca_grpc_deadline_remaining(int64_t deadline_utc_ms, int64_t now_utc_ms);

/* IsExpired(deadlineUtc, nowUtc) — true once nowUtc has reached/passed it. */
bool ca_grpc_deadline_is_expired(int64_t deadline_utc_ms, int64_t now_utc_ms);

/* ===========================================================================
 * GrpcChannelDescriptor
 * =========================================================================== */

typedef struct {
    char   *target;                   /* owned, non-null */
    bool    use_tls;
    int     max_receive_bytes;
    int     max_send_bytes;
    int64_t keep_alive_interval_ms;   /* TimeSpan KeepAliveInterval */
} ca_grpc_channel_descriptor_t;

ca_grpc_channel_descriptor_t *ca_grpc_channel_descriptor_new(
    const char *target, bool use_tls, int max_receive_bytes,
    int max_send_bytes, int64_t keep_alive_interval_ms);
void ca_grpc_channel_descriptor_destroy(ca_grpc_channel_descriptor_t *d);
ca_grpc_channel_descriptor_t *ca_grpc_channel_descriptor_copy(
    const ca_grpc_channel_descriptor_t *d);

/* ===========================================================================
 * GrpcRetryPolicy
 * =========================================================================== */

typedef struct {
    int     max_attempts;
    int64_t initial_backoff_ms;
    int64_t max_backoff_ms;
    double  multiplier;
    char  **retryable_status_codes; /* owned array of owned strings */
    size_t  retryable_count;
} ca_grpc_retry_policy_t;

ca_grpc_retry_policy_t *ca_grpc_retry_policy_new(
    int max_attempts, int64_t initial_backoff_ms, int64_t max_backoff_ms,
    double multiplier, const char *const *retryable_status_codes,
    size_t retryable_count);
void ca_grpc_retry_policy_destroy(ca_grpc_retry_policy_t *p);
ca_grpc_retry_policy_t *ca_grpc_retry_policy_copy(
    const ca_grpc_retry_policy_t *p);

/* GrpcRetryPolicies presets (fresh owned copies):
 *   Default    : 3, 100ms, 2s, 2.0, {"UNAVAILABLE","DEADLINE_EXCEEDED"}
 *   Aggressive : 6, 50ms,  5s, 2.0, {"UNAVAILABLE","DEADLINE_EXCEEDED",
 *                                    "RESOURCE_EXHAUSTED"}
 *   NoRetry    : 1, 0, 0, 1.0, {} */
ca_grpc_retry_policy_t *ca_grpc_retry_policies_default(void);
ca_grpc_retry_policy_t *ca_grpc_retry_policies_aggressive(void);
ca_grpc_retry_policy_t *ca_grpc_retry_policies_no_retry(void);

/* ===========================================================================
 * GrpcCallSummary
 * =========================================================================== */

typedef struct {
    char   *method;      /* owned */
    int     attempts;
    int64_t latency_ms;  /* TimeSpan Latency */
    char   *status_code; /* owned */
    int64_t at_unix_ms;
} ca_grpc_call_summary_t;

void ca_grpc_call_summary_free(ca_grpc_call_summary_t *c);
void ca_grpc_call_summary_free_array(ca_grpc_call_summary_t *arr, size_t count);

/* ===========================================================================
 * InMemoryGrpcCallMetrics
 *
 * RegisterChannel: LWW by id. GetChannel: fresh copy or NULL. SetState/State:
 * per-id state (Idle when unknown). LogCall: append + return "grpc-N" (N is a
 * monotonic counter starting at 1). RecentCalls(limit): newest `limit` ordered
 * by AtUtc descending.
 * =========================================================================== */

typedef struct ca_grpc_metrics ca_grpc_metrics_t;

ca_grpc_metrics_t *ca_grpc_metrics_create(void);
void ca_grpc_metrics_destroy(ca_grpc_metrics_t *m);

int ca_grpc_metrics_register_channel(ca_grpc_metrics_t *m, const char *id,
                                     const ca_grpc_channel_descriptor_t *d);
ca_grpc_channel_descriptor_t *ca_grpc_metrics_get_channel(
    const ca_grpc_metrics_t *m, const char *id);
void ca_grpc_metrics_set_state(ca_grpc_metrics_t *m, const char *id,
                               ca_grpc_channel_state_t s);
ca_grpc_channel_state_t ca_grpc_metrics_state(const ca_grpc_metrics_t *m,
                                              const char *id);
/* LogCall — append a call summary (deep copy); writes the assigned "grpc-N" id
 * into out_id[32] (caller-provided buffer, >= 32 bytes) and returns out_id, or
 * NULL on error. */
char *ca_grpc_metrics_log_call(ca_grpc_metrics_t *m,
                               const char *method, int attempts,
                               int64_t latency_ms, const char *status_code,
                               int64_t at_unix_ms, char *out_id,
                               size_t out_id_size);
/* RecentCalls(limit) — newest ordered by AtUtc descending. On error *count=
 * SIZE_MAX; free with ca_grpc_call_summary_free_array. */
ca_grpc_call_summary_t *ca_grpc_metrics_recent_calls(
    const ca_grpc_metrics_t *m, int limit, size_t *count);

/* ===========================================================================
 * GrpcNetworkTransport
 * =========================================================================== */

typedef struct ca_grpc_transport ca_grpc_transport_t;

/* Create from an address + descriptor (the descriptor stands in for
 * GrpcChannelOptions; deep-copied). NULL on OOM. */
ca_grpc_transport_t *ca_grpc_transport_create(
    const char *address, const ca_grpc_channel_descriptor_t *descriptor);
void ca_grpc_transport_destroy(ca_grpc_transport_t *t);
ca_network_transport_t ca_grpc_transport_as_transport(ca_grpc_transport_t *t);
/* The underlying channel address (borrowed). */
const char *ca_grpc_transport_address(const ca_grpc_transport_t *t);
/* The channel descriptor view (fresh copy, or NULL if none / OOM). */
ca_grpc_channel_descriptor_t *ca_grpc_transport_channel(
    const ca_grpc_transport_t *t);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_NET_GRPC_H */
