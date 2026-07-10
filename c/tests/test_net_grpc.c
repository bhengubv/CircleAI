/*
 * test_net_grpc.c — CircleAI.Networking.Grpc (net_grpc.h).
 *
 * Verifies:
 *   Records : channel descriptor / retry policy / call summary new+copy
 *   Presets : GrpcRetryPolicies Default / Aggressive / NoRetry
 *   Metrics : RegisterChannel LWW, GetChannel, SetState/State default,
 *             LogCall returns "grpc-N" monotonic, RecentCalls desc by AtUtc
 *   Transport : Kind==Grpc, IsAvailable==running (start/stop), SendAsync returns
 *             CA_GRPC_SEND_NOT_SUPPORTED, ReceiveAsync empty, channel view
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <stdlib.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

#define T0 1700000000000LL

static void test_records_and_presets(void) {
    ca_grpc_channel_descriptor_t *d = ca_grpc_channel_descriptor_new(
        "dns:///svc:443", true, 4 * 1024 * 1024, 1 * 1024 * 1024, 30000);
    assert(d && strcmp(d->target, "dns:///svc:443") == 0 && d->use_tls &&
           d->max_receive_bytes == 4 * 1024 * 1024 &&
           d->keep_alive_interval_ms == 30000);
    ca_grpc_channel_descriptor_t *dc = ca_grpc_channel_descriptor_copy(d);
    assert(dc && dc != d && strcmp(dc->target, d->target) == 0);
    ca_grpc_channel_descriptor_destroy(dc);
    ca_grpc_channel_descriptor_destroy(d);

    ca_grpc_retry_policy_t *def = ca_grpc_retry_policies_default();
    assert(def && def->max_attempts == 3 && def->initial_backoff_ms == 100 &&
           def->max_backoff_ms == 2000 && def->multiplier == 2.0 &&
           def->retryable_count == 2 &&
           strcmp(def->retryable_status_codes[0], "UNAVAILABLE") == 0 &&
           strcmp(def->retryable_status_codes[1], "DEADLINE_EXCEEDED") == 0);
    ca_grpc_retry_policy_t *agg = ca_grpc_retry_policies_aggressive();
    assert(agg && agg->max_attempts == 6 && agg->initial_backoff_ms == 50 &&
           agg->max_backoff_ms == 5000 && agg->retryable_count == 3 &&
           strcmp(agg->retryable_status_codes[2], "RESOURCE_EXHAUSTED") == 0);
    ca_grpc_retry_policy_t *no = ca_grpc_retry_policies_no_retry();
    assert(no && no->max_attempts == 1 && no->initial_backoff_ms == 0 &&
           no->max_backoff_ms == 0 && no->multiplier == 1.0 &&
           no->retryable_count == 0 && no->retryable_status_codes == NULL);

    ca_grpc_retry_policy_t *pc = ca_grpc_retry_policy_copy(def);
    assert(pc && pc->retryable_status_codes != def->retryable_status_codes &&
           strcmp(pc->retryable_status_codes[0], "UNAVAILABLE") == 0);
    ca_grpc_retry_policy_destroy(pc);
    ca_grpc_retry_policy_destroy(def);
    ca_grpc_retry_policy_destroy(agg);
    ca_grpc_retry_policy_destroy(no);
}

static void test_metrics(void) {
    ca_grpc_metrics_t *m = ca_grpc_metrics_create();
    assert(m);

    ca_grpc_channel_descriptor_t *d = ca_grpc_channel_descriptor_new(
        "svc", false, 100, 100, 1000);
    assert(ca_grpc_metrics_register_channel(m, "ch1", d) == 0);
    ca_grpc_channel_descriptor_destroy(d);

    ca_grpc_channel_descriptor_t *g = ca_grpc_metrics_get_channel(m, "ch1");
    assert(g && strcmp(g->target, "svc") == 0);
    ca_grpc_channel_descriptor_destroy(g);
    assert(ca_grpc_metrics_get_channel(m, "nope") == NULL);

    /* State default Idle, settable. */
    assert(ca_grpc_metrics_state(m, "ch1") == CA_GRPC_STATE_IDLE);
    ca_grpc_metrics_set_state(m, "ch1", CA_GRPC_STATE_READY);
    assert(ca_grpc_metrics_state(m, "ch1") == CA_GRPC_STATE_READY);
    ca_grpc_metrics_set_state(m, "ch1", CA_GRPC_STATE_TRANSIENT_FAILURE);
    assert(ca_grpc_metrics_state(m, "ch1") == CA_GRPC_STATE_TRANSIENT_FAILURE);

    /* LogCall returns "grpc-1", "grpc-2", ... monotonic. */
    char id[32];
    assert(ca_grpc_metrics_log_call(m, "/svc/A", 1, 12, "OK", T0 + 5, id,
                                    sizeof(id)) == id);
    assert(strcmp(id, "grpc-1") == 0);
    assert(ca_grpc_metrics_log_call(m, "/svc/B", 2, 30, "UNAVAILABLE", T0 + 20,
                                    id, sizeof(id)) == id);
    assert(strcmp(id, "grpc-2") == 0);
    assert(ca_grpc_metrics_log_call(m, "/svc/C", 1, 8, "OK", T0 + 10, id,
                                    sizeof(id)) == id);
    assert(strcmp(id, "grpc-3") == 0);

    /* RecentCalls desc by AtUtc: B(T0+20), C(T0+10), A(T0+5). */
    size_t n = 0;
    ca_grpc_call_summary_t *calls = ca_grpc_metrics_recent_calls(m, 2, &n);
    assert(calls && n == 2);
    assert(strcmp(calls[0].method, "/svc/B") == 0);
    assert(strcmp(calls[1].method, "/svc/C") == 0);
    ca_grpc_call_summary_free_array(calls, n);

    calls = ca_grpc_metrics_recent_calls(m, 50, &n);
    assert(calls && n == 3);
    assert(strcmp(calls[0].method, "/svc/B") == 0);
    assert(strcmp(calls[2].method, "/svc/A") == 0);
    ca_grpc_call_summary_free_array(calls, n);

    ca_grpc_metrics_destroy(m);
}

static void test_transport(void) {
    ca_grpc_channel_descriptor_t *d = ca_grpc_channel_descriptor_new(
        "https://svc:443", true, 100, 100, 5000);
    ca_grpc_transport_t *t = ca_grpc_transport_create("https://svc:443", d);
    assert(t);
    ca_grpc_channel_descriptor_destroy(d);

    ca_network_transport_t nt = ca_grpc_transport_as_transport(t);
    assert(nt.kind(nt.self) == CA_TRANSPORT_GRPC);
    assert(nt.is_available(nt.self) == false); /* not running */
    assert(nt.start(nt.self) == 0);
    assert(nt.is_available(nt.self) == true);
    assert(nt.stop(nt.self) == 0);
    assert(nt.is_available(nt.self) == false);

    /* SendAsync is not a generic send path. */
    const uint8_t body[] = { 1 };
    ca_network_payload_t *p = ca_network_payload_create(
        body, 1, "dst", CA_MSG_PRIORITY_NORMAL, NULL, false, 0, T0, NULL);
    assert(nt.send(nt.self, p) == CA_GRPC_SEND_NOT_SUPPORTED);
    ca_network_payload_destroy(p);

    /* ReceiveAsync yields nothing. */
    ca_network_payload_t *out = NULL;
    assert(nt.receive_next(nt.self, &out) == false);

    /* Channel view. */
    assert(strcmp(ca_grpc_transport_address(t), "https://svc:443") == 0);
    ca_grpc_channel_descriptor_t *cd = ca_grpc_transport_channel(t);
    assert(cd && strcmp(cd->target, "https://svc:443") == 0 && cd->use_tls);
    ca_grpc_channel_descriptor_destroy(cd);

    ca_grpc_transport_destroy(t);
}

int main(void) {
    test_records_and_presets();
    test_metrics();
    test_transport();
    return 0;
}
