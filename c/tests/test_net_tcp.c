/*
 * test_net_tcp.c — CircleAI.Networking.Tcp (net_tcp.h).
 *
 * Verifies:
 *   Record    : endpoint new+copy (deep)
 *   Ports     : TcpKnownPorts constants
 *   Registry  : Register LWW, Get, SetState/State default Disconnected,
 *               RecordSample + TotalBytesSent
 *   Transport : client mode — Kind==Tcp, IsAvailable == connected, StartAsync
 *               connects, SendAsync writes 4-byte LE length prefix + body and the
 *               loopback de-frames it back into a payload with identical bytes,
 *               framing survives a multi-frame back-to-back feed, StopAsync
 *               closes + completes; listener mode — IsAvailable false, SendAsync
 *               fails
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <stdlib.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

#define T0 1700000000000LL

static void test_record_and_ports(void) {
    ca_tcp_endpoint_descriptor_t *e =
        ca_tcp_endpoint_descriptor_new("host.local", 8883, true, false, 5000);
    assert(e && strcmp(e->host, "host.local") == 0 && e->port == 8883 &&
           e->no_delay && !e->keep_alive && e->connect_timeout_ms == 5000);
    ca_tcp_endpoint_descriptor_t *ec = ca_tcp_endpoint_descriptor_copy(e);
    assert(ec && ec->host != e->host && strcmp(ec->host, "host.local") == 0);
    ca_tcp_endpoint_descriptor_destroy(ec);
    ca_tcp_endpoint_descriptor_destroy(e);

    assert(CA_TCP_PORT_HTTP == 80 && CA_TCP_PORT_HTTPS == 443 &&
           CA_TCP_PORT_SSH == 22 && CA_TCP_PORT_MQTT == 1883 &&
           CA_TCP_PORT_MQTT_SSL == 8883 && CA_TCP_PORT_IMAP_SSL == 993 &&
           CA_TCP_PORT_POP3_SSL == 995);
}

static void test_registry(void) {
    ca_tcp_registry_t *r = ca_tcp_registry_create();
    assert(r);

    ca_tcp_endpoint_descriptor_t *d1 =
        ca_tcp_endpoint_descriptor_new("h1", 1, false, false, 0);
    ca_tcp_endpoint_descriptor_t *d2 =
        ca_tcp_endpoint_descriptor_new("h2", 2, false, false, 0);
    assert(ca_tcp_registry_register(r, "e1", d1) == 0);
    assert(ca_tcp_registry_register(r, "e2", d2) == 0);
    assert(ca_tcp_registry_register(r, "e1", d2) == 0); /* LWW */
    ca_tcp_endpoint_descriptor_destroy(d1);
    ca_tcp_endpoint_descriptor_destroy(d2);

    ca_tcp_endpoint_descriptor_t *got = ca_tcp_registry_get(r, "e1");
    assert(got && strcmp(got->host, "h2") == 0); /* overwritten */
    ca_tcp_endpoint_descriptor_destroy(got);
    assert(ca_tcp_registry_get(r, "nope") == NULL);

    assert(ca_tcp_registry_state(r, "e1") == CA_TCP_STATE_DISCONNECTED);
    ca_tcp_registry_set_state(r, "e1", CA_TCP_STATE_CONNECTED);
    assert(ca_tcp_registry_state(r, "e1") == CA_TCP_STATE_CONNECTED);
    ca_tcp_registry_set_state(r, "e1", CA_TCP_STATE_FAILED);
    assert(ca_tcp_registry_state(r, "e1") == CA_TCP_STATE_FAILED);

    assert(ca_tcp_registry_total_bytes_sent(r, "e1") == 0);
    ca_tcp_registry_record_sample(r, "e1", 100, 10, T0);
    ca_tcp_registry_record_sample(r, "e1", 250, 20, T0 + 1);
    ca_tcp_registry_record_sample(r, "e2", 999, 1, T0 + 2);
    assert(ca_tcp_registry_total_bytes_sent(r, "e1") == 350);
    assert(ca_tcp_registry_total_bytes_sent(r, "e2") == 999);

    ca_tcp_registry_destroy(r);
}

static void test_client_transport_framing(void) {
    ca_mem_tcp_adapter_t *ad =
        ca_mem_tcp_adapter_create(/*connected*/ false, /*loopback*/ true);
    assert(ad);
    ca_tcp_transport_t *t = ca_tcp_transport_create_client(
        ca_mem_tcp_adapter_as_adapter(ad), "10.0.0.5", 9000);
    assert(t && !ca_tcp_transport_is_listener(t));
    ca_network_transport_t nt = ca_tcp_transport_as_transport(t);

    assert(nt.kind(nt.self) == CA_TRANSPORT_TCP);
    assert(nt.is_available(nt.self) == false); /* not connected yet */

    /* SendAsync before connect fails (stream is null -> InvalidOperation). */
    const uint8_t body[] = { 0xDE, 0xAD, 0xBE, 0xEF, 0x01 };
    ca_network_payload_t *p = ca_network_payload_create(
        body, sizeof(body), NULL, CA_MSG_PRIORITY_NORMAL, NULL, false, 0, T0,
        NULL);
    assert(p);
    assert(nt.send(nt.self, p) == -1);

    assert(nt.start(nt.self) == 0);
    assert(nt.is_available(nt.self) == true);

    /* SendAsync writes [4-byte LE len][body]; loopback feeds it back; the pump
     * de-frames it into a payload with identical bytes. */
    assert(nt.send(nt.self, p) == 0);
    /* 4 (len prefix) + 5 (body) bytes written. */
    assert(ca_mem_tcp_adapter_bytes_written(ad) == 9);
    assert(ca_tcp_transport_pending(t) == 1);

    ca_network_payload_t *out = NULL;
    assert(nt.receive_next(nt.self, &out) && out);
    assert(out->data_len == sizeof(body));
    assert(memcmp(out->data, body, sizeof(body)) == 0);
    ca_network_payload_destroy(out);
    assert(nt.receive_next(nt.self, &out) == false);

    /* Multi-frame: deliver two whole frames back-to-back in one feed; both
     * de-frame. Frame A = 1 byte {0xAA}; Frame B = 2 bytes {0xBB,0xCC}. */
    uint8_t two_frames[] = {
        1, 0, 0, 0, 0xAA,           /* len=1, body {0xAA} */
        2, 0, 0, 0, 0xBB, 0xCC      /* len=2, body {0xBB,0xCC} */
    };
    assert(ca_mem_tcp_adapter_deliver(ad, two_frames, sizeof(two_frames)) == 0);
    assert(ca_tcp_transport_pending(t) == 2);
    assert(nt.receive_next(nt.self, &out) && out->data_len == 1 &&
           out->data[0] == 0xAA);
    ca_network_payload_destroy(out);
    assert(nt.receive_next(nt.self, &out) && out->data_len == 2 &&
           out->data[0] == 0xBB && out->data[1] == 0xCC);
    ca_network_payload_destroy(out);

    /* Partial frame is buffered until completed. Feed just the header + 1 of 3
     * body bytes, then the remaining 2. */
    uint8_t partial_hdr[] = { 3, 0, 0, 0, 0x11 };
    assert(ca_mem_tcp_adapter_deliver(ad, partial_hdr, sizeof(partial_hdr)) ==
           0);
    assert(ca_tcp_transport_pending(t) == 0); /* incomplete */
    uint8_t rest[] = { 0x22, 0x33 };
    assert(ca_mem_tcp_adapter_deliver(ad, rest, sizeof(rest)) == 0);
    assert(ca_tcp_transport_pending(t) == 1);
    assert(nt.receive_next(nt.self, &out) && out->data_len == 3 &&
           out->data[0] == 0x11 && out->data[2] == 0x33);
    ca_network_payload_destroy(out);

    assert(nt.stop(nt.self) == 0);
    assert(nt.is_available(nt.self) == false);

    ca_network_payload_destroy(p);
    ca_tcp_transport_destroy(t);
    ca_mem_tcp_adapter_destroy(ad);
}

static void test_listener_transport(void) {
    ca_tcp_transport_t *t = ca_tcp_transport_create_listener(9100);
    assert(t && ca_tcp_transport_is_listener(t));
    ca_network_transport_t nt = ca_tcp_transport_as_transport(t);

    assert(nt.kind(nt.self) == CA_TRANSPORT_TCP);
    assert(nt.is_available(nt.self) == false); /* no client stream */
    assert(nt.start(nt.self) == 0);            /* begins listening */
    assert(nt.is_available(nt.self) == false);

    /* SendAsync fails on a listener (stream is null). */
    const uint8_t body[] = { 1, 2 };
    ca_network_payload_t *p = ca_network_payload_create(
        body, 2, NULL, CA_MSG_PRIORITY_NORMAL, NULL, false, 0, T0, NULL);
    assert(p && nt.send(nt.self, p) == -1);
    ca_network_payload_destroy(p);

    assert(nt.stop(nt.self) == 0);
    ca_tcp_transport_destroy(t);
}

int main(void) {
    test_record_and_ports();
    test_registry();
    test_client_transport_framing();
    test_listener_transport();
    return 0;
}
