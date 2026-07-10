/*
 * test_net_bluetooth.c — CircleAI.Networking.Bluetooth (net_bluetooth.h).
 *
 * Verifies:
 *   Records   : endpoint / capability profile new+copy (deep service arrays)
 *   Presets   : Le5 / Le4 / Classic values
 *   Registry  : Register LWW, GetEndpoint, AllEndpoints ordered by Name,
 *               SetState/State default, RecordThroughput + AvgKbpsRead
 *   Adapter+Transport : Kind==Bluetooth, IsAvailable mirrors adapter, StartAsync
 *               wires the inbound writer, adapter.deliver pushes inbound,
 *               ReceiveAsync drains FIFO, SendAsync delegates to adapter.write,
 *               StopAsync stops adapter + completes channel
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <stdlib.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

#define T0 1700000000000LL

static void test_records_and_presets(void) {
    const char *svc[] = { "GATT", "L2CAP" };
    ca_bt_endpoint_descriptor_t *e = ca_bt_endpoint_descriptor_new(
        "dev-1", "Watch", "AA:BB:CC", svc, 2);
    assert(e);
    assert(strcmp(e->device_id, "dev-1") == 0);
    assert(strcmp(e->name, "Watch") == 0);
    assert(strcmp(e->mac_address, "AA:BB:CC") == 0);
    assert(e->advertised_count == 2 && strcmp(e->advertised_services[1], "L2CAP") == 0);
    ca_bt_endpoint_descriptor_t *ec = ca_bt_endpoint_descriptor_copy(e);
    assert(ec && ec->advertised_services != e->advertised_services);
    assert(strcmp(ec->advertised_services[0], "GATT") == 0);
    ca_bt_endpoint_descriptor_destroy(ec);
    ca_bt_endpoint_descriptor_destroy(e);

    ca_bt_capability_profile_t *le5 = ca_bt_capability_profiles_le5();
    assert(le5 && le5->max_mtu_bytes == 247 && le5->supports_secure_connections &&
           le5->supports_high_speed && le5->compatible_count == 2 &&
           strcmp(le5->compatible_profiles[0], "GATT") == 0 &&
           strcmp(le5->compatible_profiles[1], "L2CAP") == 0);
    ca_bt_capability_profile_t *le4 = ca_bt_capability_profiles_le4();
    assert(le4 && le4->max_mtu_bytes == 23 && !le4->supports_high_speed &&
           le4->compatible_count == 1);
    ca_bt_capability_profile_t *cl = ca_bt_capability_profiles_classic();
    assert(cl && cl->max_mtu_bytes == 1024 && cl->compatible_count == 2 &&
           strcmp(cl->compatible_profiles[1], "RFCOMM") == 0);

    ca_bt_capability_profile_t *cp = ca_bt_capability_profile_copy(le5);
    assert(cp && cp->compatible_profiles != le5->compatible_profiles);
    ca_bt_capability_profile_destroy(cp);
    ca_bt_capability_profile_destroy(le5);
    ca_bt_capability_profile_destroy(le4);
    ca_bt_capability_profile_destroy(cl);
}

static void test_registry(void) {
    ca_bt_registry_t *r = ca_bt_registry_create();
    assert(r);

    /* insert in a non-sorted order; AllEndpoints must sort by Name. */
    ca_bt_endpoint_descriptor_t *z = ca_bt_endpoint_descriptor_new(
        "d-z", "Zephyr", "00:z", NULL, 0);
    ca_bt_endpoint_descriptor_t *a = ca_bt_endpoint_descriptor_new(
        "d-a", "Apex", "00:a", NULL, 0);
    assert(ca_bt_registry_register(r, z) == 0);
    assert(ca_bt_registry_register(r, a) == 0);
    ca_bt_endpoint_descriptor_destroy(z);
    ca_bt_endpoint_descriptor_destroy(a);

    ca_bt_endpoint_descriptor_t *got = ca_bt_registry_get_endpoint(r, "d-a");
    assert(got && strcmp(got->name, "Apex") == 0);
    ca_bt_endpoint_descriptor_destroy(got);
    assert(ca_bt_registry_get_endpoint(r, "nope") == NULL);

    ca_bt_endpoint_descriptor_t **all = NULL;
    size_t n = 0;
    assert(ca_bt_registry_all_endpoints(r, &all, &n) == 0);
    assert(n == 2);
    assert(strcmp(all[0]->name, "Apex") == 0);   /* sorted by Name */
    assert(strcmp(all[1]->name, "Zephyr") == 0);
    for (size_t i = 0; i < n; ++i) ca_bt_endpoint_descriptor_destroy(all[i]);
    free(all);

    /* State: default Disconnected, settable. */
    assert(ca_bt_registry_state(r, "d-a") == CA_BT_STATE_DISCONNECTED);
    ca_bt_registry_set_state(r, "d-a", CA_BT_STATE_CONNECTED);
    assert(ca_bt_registry_state(r, "d-a") == CA_BT_STATE_CONNECTED);
    ca_bt_registry_set_state(r, "d-a", CA_BT_STATE_FAILED);
    assert(ca_bt_registry_state(r, "d-a") == CA_BT_STATE_FAILED);

    /* Throughput avg read. */
    assert(ca_bt_registry_avg_kbps_read(r, "d-a") == 0.0);
    ca_bt_registry_record_throughput(r, "d-a", 100.0, 50.0, T0);
    ca_bt_registry_record_throughput(r, "d-a", 200.0, 60.0, T0 + 1);
    ca_bt_registry_record_throughput(r, "d-z", 999.0, 1.0, T0 + 2);
    assert(ca_bt_registry_avg_kbps_read(r, "d-a") == 150.0);
    assert(ca_bt_registry_avg_kbps_read(r, "d-z") == 999.0);

    ca_bt_registry_destroy(r);
}

static void test_transport(void) {
    ca_mem_ble_adapter_t *ad = ca_mem_ble_adapter_create(/*available*/ false);
    assert(ad);
    ca_bt_transport_t *t =
        ca_bt_transport_create(ca_mem_ble_adapter_as_adapter(ad));
    assert(t);
    ca_network_transport_t nt = ca_bt_transport_as_transport(t);

    assert(nt.kind(nt.self) == CA_TRANSPORT_BLUETOOTH);
    assert(nt.is_available(nt.self) == false);
    ca_mem_ble_adapter_set_available(ad, true);
    assert(nt.is_available(nt.self) == true);

    /* Before start, the adapter is not started -> deliver fails. */
    const uint8_t body[] = { 4, 5, 6 };
    ca_network_payload_t *p = ca_network_payload_create(
        body, sizeof(body), "peer", CA_MSG_PRIORITY_HIGH, NULL, false, 0, T0,
        NULL);
    assert(p);
    assert(ca_mem_ble_adapter_deliver(ad, p) == -1);

    /* Start wires the writer; deliver now pushes inbound. */
    assert(nt.start(nt.self) == 0);
    assert(ca_mem_ble_adapter_deliver(ad, p) == 0);
    assert(ca_bt_transport_pending(t) == 1);

    ca_network_payload_t *out = NULL;
    assert(nt.receive_next(nt.self, &out) && out);
    assert(strcmp(out->destination_id, "peer") == 0);
    assert(out->data_len == 3 && out->data[0] == 4);
    ca_network_payload_destroy(out);
    assert(nt.receive_next(nt.self, &out) == false);

    /* SendAsync delegates to adapter.WriteAsync (sent counter increments). */
    assert(ca_mem_ble_adapter_sent_count(ad) == 0);
    assert(nt.send(nt.self, p) == 0);
    assert(ca_mem_ble_adapter_sent_count(ad) == 1);

    /* StopAsync stops the adapter and completes the channel. */
    assert(nt.stop(nt.self) == 0);
    assert(ca_mem_ble_adapter_deliver(ad, p) == -1); /* adapter stopped */

    ca_network_payload_destroy(p);
    ca_bt_transport_destroy(t);
    ca_mem_ble_adapter_destroy(ad);
}

int main(void) {
    test_records_and_presets();
    test_registry();
    test_transport();
    return 0;
}
