/*
 * test_net_websocket.c — CircleAI.Networking.WebSocket (net_websocket.h).
 *
 * Verifies:
 *   Record    : endpoint descriptor new+copy with optional headers dict +
 *               subprotocols list (deep)
 *   Registry  : Register LWW, Get, SetState/State default Closed (incl.
 *               Closed_Error distinct member), RecordFrame + TotalBytes +
 *               FrameCount by type
 *   Transport : Kind==WebSocket, IsAvailable == Open, StartAsync connects,
 *               SendAsync sends a binary frame (loopback -> inbound),
 *               ReceiveAsync drains, a delivered Close frame stops further
 *               inbound, StopAsync closes + completes
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <stdlib.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

#define T0 1700000000000LL

static void test_record(void) {
    ca_net_metadata_pair_t hdrs[] = {
        { (char *)"Authorization", (char *)"Bearer x" },
        { (char *)"X-Trace", (char *)"42" },
    };
    const char *subs[] = { "circle.v1", "circle.v2" };
    ca_ws_endpoint_descriptor_t *e = ca_ws_endpoint_descriptor_new(
        "wss://host/ws", /*has_headers*/ true, hdrs, 2, 30000, subs, 2);
    assert(e && strcmp(e->uri, "wss://host/ws") == 0 && e->has_headers &&
           e->header_count == 2 &&
           strcmp(e->headers[0].key, "Authorization") == 0 &&
           strcmp(e->headers[1].value, "42") == 0 &&
           e->ping_interval_ms == 30000 && e->subprotocol_count == 2 &&
           strcmp(e->subprotocols[1], "circle.v2") == 0);
    ca_ws_endpoint_descriptor_t *ec = ca_ws_endpoint_descriptor_copy(e);
    assert(ec && ec->headers != e->headers &&
           ec->subprotocols != e->subprotocols &&
           strcmp(ec->headers[0].value, "Bearer x") == 0 &&
           strcmp(ec->subprotocols[0], "circle.v1") == 0);
    ca_ws_endpoint_descriptor_destroy(ec);
    ca_ws_endpoint_descriptor_destroy(e);

    /* No headers (null dict), no subprotocols. */
    ca_ws_endpoint_descriptor_t *e2 = ca_ws_endpoint_descriptor_new(
        "ws://h", /*has_headers*/ false, NULL, 0, 0, NULL, 0);
    assert(e2 && !e2->has_headers && e2->headers == NULL &&
           e2->header_count == 0 && e2->subprotocol_count == 0);
    ca_ws_endpoint_descriptor_t *e2c = ca_ws_endpoint_descriptor_copy(e2);
    assert(e2c && !e2c->has_headers);
    ca_ws_endpoint_descriptor_destroy(e2c);
    ca_ws_endpoint_descriptor_destroy(e2);
}

static void test_registry(void) {
    ca_ws_registry_t *r = ca_ws_registry_create();
    assert(r);

    ca_ws_endpoint_descriptor_t *d1 =
        ca_ws_endpoint_descriptor_new("ws://a", false, NULL, 0, 0, NULL, 0);
    ca_ws_endpoint_descriptor_t *d2 =
        ca_ws_endpoint_descriptor_new("ws://b", false, NULL, 0, 0, NULL, 0);
    assert(ca_ws_registry_register(r, "s1", d1) == 0);
    assert(ca_ws_registry_register(r, "s1", d2) == 0); /* LWW */
    ca_ws_endpoint_descriptor_destroy(d1);
    ca_ws_endpoint_descriptor_destroy(d2);

    ca_ws_endpoint_descriptor_t *got = ca_ws_registry_get(r, "s1");
    assert(got && strcmp(got->uri, "ws://b") == 0);
    ca_ws_endpoint_descriptor_destroy(got);
    assert(ca_ws_registry_get(r, "nope") == NULL);

    assert(ca_ws_registry_state(r, "s1") == CA_WS_STATE_CLOSED);
    ca_ws_registry_set_state(r, "s1", CA_WS_STATE_OPEN);
    assert(ca_ws_registry_state(r, "s1") == CA_WS_STATE_OPEN);
    ca_ws_registry_set_state(r, "s1", CA_WS_STATE_CLOSED_ERROR);
    assert(ca_ws_registry_state(r, "s1") == CA_WS_STATE_CLOSED_ERROR);

    /* Frames: total bytes + count by type. */
    ca_ws_registry_record_frame(r, "s1", CA_WS_MSG_BINARY, 100, T0);
    ca_ws_registry_record_frame(r, "s1", CA_WS_MSG_BINARY, 50, T0 + 1);
    ca_ws_registry_record_frame(r, "s1", CA_WS_MSG_PING, 4, T0 + 2);
    ca_ws_registry_record_frame(r, "s2", CA_WS_MSG_BINARY, 999, T0 + 3);
    assert(ca_ws_registry_total_bytes(r, "s1") == 154);
    assert(ca_ws_registry_frame_count(r, "s1", CA_WS_MSG_BINARY) == 2);
    assert(ca_ws_registry_frame_count(r, "s1", CA_WS_MSG_PING) == 1);
    assert(ca_ws_registry_frame_count(r, "s1", CA_WS_MSG_CLOSE) == 0);
    assert(ca_ws_registry_total_bytes(r, "s2") == 999);

    ca_ws_registry_destroy(r);
}

static void test_transport(void) {
    ca_mem_ws_adapter_t *ad =
        ca_mem_ws_adapter_create(/*open*/ false, /*loopback*/ true);
    assert(ad);
    ca_ws_transport_t *t =
        ca_ws_transport_create(ca_mem_ws_adapter_as_adapter(ad));
    assert(t);
    ca_network_transport_t nt = ca_ws_transport_as_transport(t);

    assert(nt.kind(nt.self) == CA_TRANSPORT_WEBSOCKET);
    assert(nt.is_available(nt.self) == false); /* not connected */

    /* SendAsync before connect fails (ThrowIfNull(_ws)). */
    const uint8_t body[] = { 3, 1, 4, 1, 5 };
    ca_network_payload_t *p = ca_network_payload_create(
        body, sizeof(body), NULL, CA_MSG_PRIORITY_NORMAL, NULL, false, 0, T0,
        NULL);
    assert(p && nt.send(nt.self, p) == -1);

    assert(nt.start(nt.self) == 0);
    assert(nt.is_available(nt.self) == true); /* Open */

    /* SendAsync sends a binary frame; loopback feeds it back as inbound. */
    assert(nt.send(nt.self, p) == 0);
    assert(ca_mem_ws_adapter_send_count(ad) == 1);
    assert(ca_ws_transport_pending(t) == 1);
    ca_network_payload_t *out = NULL;
    assert(nt.receive_next(nt.self, &out) && out);
    assert(out->data_len == sizeof(body) &&
           memcmp(out->data, body, sizeof(body)) == 0);
    ca_network_payload_destroy(out);
    assert(nt.receive_next(nt.self, &out) == false);

    /* Host-delivered inbound frame. */
    const uint8_t inb[] = { 0x42 };
    assert(ca_mem_ws_adapter_deliver(ad, inb, 1) == 0);
    assert(ca_ws_transport_pending(t) == 1);
    assert(nt.receive_next(nt.self, &out) && out->data[0] == 0x42);
    ca_network_payload_destroy(out);

    /* A delivered Close frame stops further inbound (pump breaks). */
    assert(ca_mem_ws_adapter_deliver_close(ad) == 0);
    assert(ca_mem_ws_adapter_deliver(ad, inb, 1) == -1); /* channel closed */

    /* StopAsync closes (CloseSent) and completes the channel. */
    assert(nt.stop(nt.self) == 0);
    assert(nt.is_available(nt.self) == false);

    ca_network_payload_destroy(p);
    ca_ws_transport_destroy(t);
    ca_mem_ws_adapter_destroy(ad);
}

int main(void) {
    test_record();
    test_registry();
    test_transport();
    return 0;
}
