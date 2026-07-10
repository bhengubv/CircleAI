/*
 * test_net_nearlink.c — CircleAI.Networking.NearLink (net_nearlink.h).
 *
 * Verifies:
 *   Records   : device / session new+copy (deep)
 *   Registry  : Register LWW, GetDevice, Devices ordered by FriendlyName,
 *               SetPairingState/PairingState default Unpaired, OpenSession/
 *               GetSession/CloseSession/ActiveSessions, RecordThroughput +
 *               AvgRssi (default -127 when none)
 *   Adapter+Transport : Kind==NearLink, IsAvailable mirrors adapter, StartAsync
 *               wires the writer, adapter.deliver pushes inbound, ReceiveAsync
 *               drains, SendAsync delegates to adapter.send, StopAsync stops +
 *               completes channel
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <stdlib.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

#define T0 1700000000000LL

static void test_records(void) {
    ca_nearlink_device_t *d = ca_nearlink_device_new("d-1", "Watch", "huawei",
                                                     "1.2.3");
    assert(d && strcmp(d->device_id, "d-1") == 0 &&
           strcmp(d->friendly_name, "Watch") == 0 &&
           strcmp(d->manufacturer_id, "huawei") == 0 &&
           strcmp(d->firmware_version, "1.2.3") == 0);
    ca_nearlink_device_t *dc = ca_nearlink_device_copy(d);
    assert(dc && dc->device_id != d->device_id &&
           strcmp(dc->friendly_name, "Watch") == 0);
    ca_nearlink_device_destroy(dc);
    ca_nearlink_device_destroy(d);

    ca_nearlink_session_t *s = ca_nearlink_session_new(
        "s-1", "d-1", CA_NEARLINK_POWER_HIGH_THROUGHPUT, T0);
    assert(s && strcmp(s->session_id, "s-1") == 0 &&
           strcmp(s->device_id, "d-1") == 0 &&
           s->power_profile == CA_NEARLINK_POWER_HIGH_THROUGHPUT &&
           s->started_unix_ms == T0);
    ca_nearlink_session_t *sc = ca_nearlink_session_copy(s);
    assert(sc && sc->session_id != s->session_id &&
           sc->power_profile == CA_NEARLINK_POWER_HIGH_THROUGHPUT);
    ca_nearlink_session_destroy(sc);
    ca_nearlink_session_destroy(s);
}

static void test_registry(void) {
    ca_nearlink_registry_t *r = ca_nearlink_registry_create();
    assert(r);

    /* insert out of order; Devices sorts by FriendlyName. */
    ca_nearlink_device_t *z = ca_nearlink_device_new("d-z", "Zephyr", "m", "1");
    ca_nearlink_device_t *a = ca_nearlink_device_new("d-a", "Apex", "m", "1");
    assert(ca_nearlink_registry_register(r, z) == 0);
    assert(ca_nearlink_registry_register(r, a) == 0);
    /* LWW re-register. */
    assert(ca_nearlink_registry_register(r, a) == 0);
    ca_nearlink_device_destroy(z);
    ca_nearlink_device_destroy(a);

    ca_nearlink_device_t *got = ca_nearlink_registry_get_device(r, "d-a");
    assert(got && strcmp(got->friendly_name, "Apex") == 0);
    ca_nearlink_device_destroy(got);
    assert(ca_nearlink_registry_get_device(r, "nope") == NULL);

    ca_nearlink_device_t **all = NULL;
    size_t n = 0;
    assert(ca_nearlink_registry_devices(r, &all, &n) == 0 && n == 2);
    assert(strcmp(all[0]->friendly_name, "Apex") == 0);
    assert(strcmp(all[1]->friendly_name, "Zephyr") == 0);
    for (size_t i = 0; i < n; ++i) ca_nearlink_device_destroy(all[i]);
    free(all);

    /* Pairing state default + set. */
    assert(ca_nearlink_registry_pairing_state(r, "d-a") ==
           CA_NEARLINK_PAIRING_UNPAIRED);
    ca_nearlink_registry_set_pairing_state(r, "d-a", CA_NEARLINK_PAIRING_PAIRED);
    assert(ca_nearlink_registry_pairing_state(r, "d-a") ==
           CA_NEARLINK_PAIRING_PAIRED);
    ca_nearlink_registry_set_pairing_state(r, "d-a",
                                          CA_NEARLINK_PAIRING_PAIRING_FAILED);
    assert(ca_nearlink_registry_pairing_state(r, "d-a") ==
           CA_NEARLINK_PAIRING_PAIRING_FAILED);

    /* Sessions. */
    ca_nearlink_session_t *s1 = ca_nearlink_session_new(
        "s-1", "d-a", CA_NEARLINK_POWER_BALANCED, T0);
    ca_nearlink_session_t *s2 = ca_nearlink_session_new(
        "s-2", "d-z", CA_NEARLINK_POWER_LOW_ENERGY, T0 + 1);
    assert(ca_nearlink_registry_open_session(r, s1) == 0);
    assert(ca_nearlink_registry_open_session(r, s2) == 0);
    ca_nearlink_session_destroy(s1);
    ca_nearlink_session_destroy(s2);

    ca_nearlink_session_t *gs = ca_nearlink_registry_get_session(r, "s-1");
    assert(gs && strcmp(gs->device_id, "d-a") == 0);
    ca_nearlink_session_destroy(gs);

    ca_nearlink_session_t **active = NULL;
    size_t an = 0;
    assert(ca_nearlink_registry_active_sessions(r, &active, &an) == 0 &&
           an == 2);
    for (size_t i = 0; i < an; ++i) ca_nearlink_session_destroy(active[i]);
    free(active);

    ca_nearlink_registry_close_session(r, "s-1");
    assert(ca_nearlink_registry_get_session(r, "s-1") == NULL);
    assert(ca_nearlink_registry_active_sessions(r, &active, &an) == 0 &&
           an == 1);
    assert(strcmp(active[0]->session_id, "s-2") == 0);
    for (size_t i = 0; i < an; ++i) ca_nearlink_session_destroy(active[i]);
    free(active);

    /* AvgRssi: default -127 when none, then mean of samples. */
    assert(ca_nearlink_registry_avg_rssi(r, "d-a") == -127.0);
    ca_nearlink_registry_record_throughput(r, "d-a", 100.0, 50.0, -40, T0);
    ca_nearlink_registry_record_throughput(r, "d-a", 200.0, 60.0, -60, T0 + 1);
    ca_nearlink_registry_record_throughput(r, "d-z", 10.0, 5.0, -90, T0 + 2);
    assert(ca_nearlink_registry_avg_rssi(r, "d-a") == -50.0);
    assert(ca_nearlink_registry_avg_rssi(r, "d-z") == -90.0);
    assert(ca_nearlink_registry_avg_rssi(r, "unknown") == -127.0);

    ca_nearlink_registry_destroy(r);
}

static void test_transport(void) {
    ca_mem_nearlink_adapter_t *ad =
        ca_mem_nearlink_adapter_create(/*available*/ false);
    assert(ad);
    ca_nearlink_transport_t *t =
        ca_nearlink_transport_create(ca_mem_nearlink_adapter_as_adapter(ad));
    assert(t);
    ca_network_transport_t nt = ca_nearlink_transport_as_transport(t);

    assert(nt.kind(nt.self) == CA_TRANSPORT_NEARLINK);
    assert(nt.is_available(nt.self) == false);
    ca_mem_nearlink_adapter_set_available(ad, true);
    assert(nt.is_available(nt.self) == true);

    const uint8_t body[] = { 7, 8, 9 };
    ca_network_payload_t *p = ca_network_payload_create(
        body, 3, "peer", CA_MSG_PRIORITY_NORMAL, NULL, false, 0, T0, NULL);
    assert(p);

    /* Before start, deliver fails. */
    assert(ca_mem_nearlink_adapter_deliver(ad, p) == -1);

    assert(nt.start(nt.self) == 0);
    assert(ca_mem_nearlink_adapter_deliver(ad, p) == 0);
    assert(ca_nearlink_transport_pending(t) == 1);

    ca_network_payload_t *out = NULL;
    assert(nt.receive_next(nt.self, &out) && out);
    assert(out->data_len == 3 && out->data[0] == 7 &&
           strcmp(out->destination_id, "peer") == 0);
    ca_network_payload_destroy(out);
    assert(nt.receive_next(nt.self, &out) == false);

    /* SendAsync delegates to adapter.SendAsync. */
    assert(ca_mem_nearlink_adapter_sent_count(ad) == 0);
    assert(nt.send(nt.self, p) == 0);
    assert(ca_mem_nearlink_adapter_sent_count(ad) == 1);

    assert(nt.stop(nt.self) == 0);
    assert(ca_mem_nearlink_adapter_deliver(ad, p) == -1); /* stopped */

    ca_network_payload_destroy(p);
    ca_nearlink_transport_destroy(t);
    ca_mem_nearlink_adapter_destroy(ad);
}

int main(void) {
    test_records();
    test_registry();
    test_transport();
    return 0;
}
