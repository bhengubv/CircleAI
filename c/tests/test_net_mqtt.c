/*
 * test_net_mqtt.c — CircleAI.Networking.Mqtt (net_mqtt.h).
 *
 * Verifies:
 *   Records   : topic/retained/client-descriptor new+copy (deep payload)
 *   Broker    : Connect LWW + ConnectedClients, Disconnect, Subscribe dedupe,
 *               Matches (# multi-level, + single-level, exact-length tail),
 *               PublishRetained/GetRetained LWW, MatchingSubscribers
 *   Transport : Kind==Mqtt, IsAvailable mirrors client, StartAsync connects +
 *               subscribes circle/payloads/{id}/#, SendAsync topic + QoS rules,
 *               adapter.deliver -> inbound, ReceiveAsync drains, StopAsync
 *               disconnects + completes channel
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <stdlib.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

#define T0 1700000000000LL

static void test_records(void) {
    ca_mqtt_topic_descriptor_t *td =
        ca_mqtt_topic_descriptor_new("a/b", CA_MQTT_QOS_EXACTLY_ONCE);
    assert(td && strcmp(td->topic, "a/b") == 0 &&
           td->qos == CA_MQTT_QOS_EXACTLY_ONCE);
    ca_mqtt_topic_descriptor_t *tdc = ca_mqtt_topic_descriptor_copy(td);
    assert(tdc && tdc->topic != td->topic &&
           strcmp(tdc->topic, "a/b") == 0);
    ca_mqtt_topic_descriptor_destroy(tdc);
    ca_mqtt_topic_descriptor_destroy(td);

    const uint8_t body[] = { 9, 8, 7 };
    ca_mqtt_retained_message_t *rm =
        ca_mqtt_retained_message_new("topic/x", body, 3, T0);
    assert(rm && strcmp(rm->topic, "topic/x") == 0 && rm->payload_len == 3 &&
           rm->payload[2] == 7 && rm->retained_at_unix_ms == T0);
    ca_mqtt_retained_message_t *rmc = ca_mqtt_retained_message_copy(rm);
    assert(rmc && rmc->payload != rm->payload && rmc->payload[0] == 9);
    ca_mqtt_retained_message_destroy(rmc);
    ca_mqtt_retained_message_destroy(rm);

    ca_mqtt_client_descriptor_t *cd =
        ca_mqtt_client_descriptor_new("c1", "broker.local", 8883, true, 60000);
    assert(cd && strcmp(cd->client_id, "c1") == 0 &&
           strcmp(cd->host, "broker.local") == 0 && cd->port == 8883 &&
           cd->use_tls && cd->keep_alive_ms == 60000);
    ca_mqtt_client_descriptor_t *cdc = ca_mqtt_client_descriptor_copy(cd);
    assert(cdc && cdc->client_id != cd->client_id &&
           strcmp(cdc->host, "broker.local") == 0);
    ca_mqtt_client_descriptor_destroy(cdc);
    ca_mqtt_client_descriptor_destroy(cd);
}

static void test_matches(void) {
    /* '#' matches everything from that level. */
    assert(ca_mqtt_broker_matches("a/b/c", "a/#"));
    assert(ca_mqtt_broker_matches("a", "#"));
    assert(ca_mqtt_broker_matches("a/b/c", "#"));
    /* '+' matches exactly one level. */
    assert(ca_mqtt_broker_matches("a/b/c", "a/+/c"));
    assert(!ca_mqtt_broker_matches("a/b/c/d", "a/+/c"));
    /* exact match requires equal length (the t.Length == f.Length tail). */
    assert(ca_mqtt_broker_matches("a/b", "a/b"));
    assert(!ca_mqtt_broker_matches("a/b", "a/b/c"));
    assert(!ca_mqtt_broker_matches("a/b/c", "a/b"));
    /* filter longer than topic without '#' => false (i >= t.Length). */
    assert(!ca_mqtt_broker_matches("a", "a/+"));
    /* empty topic or filter => false. */
    assert(!ca_mqtt_broker_matches("", "#"));
    assert(!ca_mqtt_broker_matches("a", ""));
}

static void test_broker(void) {
    ca_mqtt_broker_t *b = ca_mqtt_broker_create();
    assert(b);

    ca_mqtt_client_descriptor_t *c1 =
        ca_mqtt_client_descriptor_new("c1", "h", 1883, false, 30000);
    ca_mqtt_client_descriptor_t *c2 =
        ca_mqtt_client_descriptor_new("c2", "h", 1883, false, 30000);
    assert(ca_mqtt_broker_connect(b, c1) == 0);
    assert(ca_mqtt_broker_connect(b, c2) == 0);
    /* re-connect same id is LWW, not a duplicate. */
    assert(ca_mqtt_broker_connect(b, c1) == 0);
    ca_mqtt_client_descriptor_destroy(c1);
    ca_mqtt_client_descriptor_destroy(c2);

    ca_mqtt_client_descriptor_t **clients = NULL;
    size_t n = 0;
    assert(ca_mqtt_broker_connected_clients(b, &clients, &n) == 0 && n == 2);
    for (size_t i = 0; i < n; ++i) ca_mqtt_client_descriptor_destroy(clients[i]);
    free(clients);

    ca_mqtt_broker_disconnect(b, "c2");
    assert(ca_mqtt_broker_connected_clients(b, &clients, &n) == 0 && n == 1);
    assert(strcmp(clients[0]->client_id, "c1") == 0);
    for (size_t i = 0; i < n; ++i) ca_mqtt_client_descriptor_destroy(clients[i]);
    free(clients);

    /* Subscribe + dedupe + whitespace guard. */
    assert(ca_mqtt_broker_subscribe(b, "c1", "sensors/+/temp") == 0);
    assert(ca_mqtt_broker_subscribe(b, "c1", "sensors/+/temp") == 0); /* dedup */
    assert(ca_mqtt_broker_subscribe(b, "c1", "alerts/#") == 0);
    assert(ca_mqtt_broker_subscribe(b, "", "x") == -1);
    assert(ca_mqtt_broker_subscribe(b, "c1", "   ") == -1);

    char **subs = NULL;
    size_t sn = 0;
    assert(ca_mqtt_broker_matching_subscribers(b, "sensors/kitchen/temp",
                                               &subs, &sn) == 0);
    assert(sn == 1 && strcmp(subs[0], "c1") == 0);
    for (size_t i = 0; i < sn; ++i) free(subs[i]);
    free(subs);

    assert(ca_mqtt_broker_matching_subscribers(b, "alerts/fire", &subs, &sn) ==
           0);
    assert(sn == 1 && strcmp(subs[0], "c1") == 0);
    for (size_t i = 0; i < sn; ++i) free(subs[i]);
    free(subs);

    /* nothing matches -> empty (out NULL, count 0). */
    assert(ca_mqtt_broker_matching_subscribers(b, "unrelated/topic", &subs,
                                               &sn) == 0);
    assert(sn == 0 && subs == NULL);

    /* Retained store LWW. */
    const uint8_t p1[] = { 1 };
    const uint8_t p2[] = { 2, 2 };
    ca_mqtt_retained_message_t *m1 =
        ca_mqtt_retained_message_new("cfg/a", p1, 1, T0);
    ca_mqtt_retained_message_t *m2 =
        ca_mqtt_retained_message_new("cfg/a", p2, 2, T0 + 5);
    assert(ca_mqtt_broker_publish_retained(b, m1) == 0);
    assert(ca_mqtt_broker_publish_retained(b, m2) == 0); /* overwrite */
    ca_mqtt_retained_message_destroy(m1);
    ca_mqtt_retained_message_destroy(m2);
    ca_mqtt_retained_message_t *got = ca_mqtt_broker_get_retained(b, "cfg/a");
    assert(got && got->payload_len == 2 && got->retained_at_unix_ms == T0 + 5);
    ca_mqtt_retained_message_destroy(got);
    assert(ca_mqtt_broker_get_retained(b, "nope") == NULL);

    ca_mqtt_broker_destroy(b);
}

static void test_transport(void) {
    ca_mem_mqtt_adapter_t *ad = ca_mem_mqtt_adapter_create(/*connected*/ false);
    assert(ad);
    ca_mqtt_transport_t *t =
        ca_mqtt_transport_create(ca_mem_mqtt_adapter_as_adapter(ad), "node-7");
    assert(t);
    ca_network_transport_t nt = ca_mqtt_transport_as_transport(t);

    assert(nt.kind(nt.self) == CA_TRANSPORT_MQTT);
    assert(nt.is_available(nt.self) == false);

    /* Before connect, deliver fails. */
    const uint8_t body[] = { 4, 5, 6 };
    assert(ca_mem_mqtt_adapter_deliver(ad, body, 3) == -1);

    /* Start connects (IsConnected true) and subscribes to the wildcard topic. */
    assert(nt.start(nt.self) == 0);
    assert(nt.is_available(nt.self) == true);
    assert(strcmp(ca_mem_mqtt_adapter_last_subscription(ad),
                  "circle/payloads/node-7/#") == 0);

    /* deliver now enqueues an inbound payload. */
    assert(ca_mem_mqtt_adapter_deliver(ad, body, 3) == 0);
    assert(ca_mqtt_transport_pending(t) == 1);
    ca_network_payload_t *out = NULL;
    assert(nt.receive_next(nt.self, &out) && out);
    assert(out->data_len == 3 && out->data[0] == 4);
    ca_network_payload_destroy(out);
    assert(nt.receive_next(nt.self, &out) == false);

    /* SendAsync with a destination -> circle/payloads/{dest}, QoS by priority. */
    ca_network_payload_t *hi = ca_network_payload_create(
        body, 3, "peerX", CA_MSG_PRIORITY_HIGH, NULL, false, 0, T0, NULL);
    assert(hi);
    assert(nt.send(nt.self, hi) == 0);
    assert(strcmp(ca_mem_mqtt_adapter_last_topic(ad), "circle/payloads/peerX") ==
           0);
    assert(ca_mem_mqtt_adapter_last_qos(ad) == CA_MQTT_QOS_EXACTLY_ONCE);
    ca_network_payload_destroy(hi);

    /* Normal priority -> AtLeastOnce; no destination -> broadcast topic. */
    ca_network_payload_t *lo = ca_network_payload_create(
        body, 3, NULL, CA_MSG_PRIORITY_NORMAL, NULL, false, 0, T0, NULL);
    assert(lo);
    assert(nt.send(nt.self, lo) == 0);
    assert(strcmp(ca_mem_mqtt_adapter_last_topic(ad),
                  "circle/payloads/broadcast") == 0);
    assert(ca_mem_mqtt_adapter_last_qos(ad) == CA_MQTT_QOS_AT_LEAST_ONCE);
    ca_network_payload_destroy(lo);

    assert(ca_mem_mqtt_adapter_publish_count(ad) == 2);

    /* StopAsync disconnects and completes the channel. */
    assert(nt.stop(nt.self) == 0);
    assert(nt.is_available(nt.self) == false);
    assert(ca_mem_mqtt_adapter_deliver(ad, body, 3) == -1);

    ca_mqtt_transport_destroy(t);
    ca_mem_mqtt_adapter_destroy(ad);

    /* empty client id -> NULL transport. */
    ca_mem_mqtt_adapter_t *ad2 = ca_mem_mqtt_adapter_create(false);
    assert(ca_mqtt_transport_create(ca_mem_mqtt_adapter_as_adapter(ad2), "") ==
           NULL);
    ca_mem_mqtt_adapter_destroy(ad2);
}

int main(void) {
    test_records();
    test_matches();
    test_broker();
    test_transport();
    return 0;
}
