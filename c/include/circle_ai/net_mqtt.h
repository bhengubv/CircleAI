#ifndef CIRCLE_AI_NET_MQTT_H
#define CIRCLE_AI_NET_MQTT_H

/*
 * net_mqtt.h — CircleAI.Networking.Mqtt (C11 port).
 *
 * The MQTT network transport. Ports CircleAI.Networking.Mqtt 1:1:
 *
 *   Enum      : MqttQos (AtMostOnce/AtLeastOnce/ExactlyOnce = 0/1/2)
 *   Records   : MqttTopicDescriptor, MqttRetainedMessage, MqttClientDescriptor
 *   Broker    : InMemoryMqttBroker — Connect/Disconnect + ConnectedClients,
 *               Subscribe, Matches (MQTT topic-filter wildcard match: '#'
 *               multi-level, '+' single-level), PublishRetained/GetRetained,
 *               MatchingSubscribers.
 *   Client    : IMqttClientAdapter — the injected MQTTnet IMqttClient seam
 *               (connect / subscribe / publish / disconnect + an inbound writer
 *               the adapter uses to push received application messages upward).
 *               Modelled as a vtable. Ships a deterministic in-memory adapter.
 *   Transport : MqttNetworkTransport — INetworkTransport over MQTT. Kind==Mqtt,
 *               IsAvailable mirrors the client's connected flag. StartAsync
 *               connects then subscribes to circle/payloads/{localClientId}/#.
 *               SendAsync publishes to circle/payloads/{destinationId} (or
 *               circle/payloads/broadcast when no destination) with QoS
 *               ExactlyOnce iff Priority>=High else AtLeastOnce. ReceiveAsync
 *               drains the UNBOUNDED inbound FIFO the adapter feeds (each received
 *               message becomes NetworkPayload.Create(bytes)).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no hashtable, no pthreads. Timestamps are Unix ms UTC, passed in.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "networking.h"   /* ca_network_transport_t, ca_network_payload_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * MqttQos
 * =========================================================================== */

typedef enum {
    CA_MQTT_QOS_AT_MOST_ONCE  = 0,
    CA_MQTT_QOS_AT_LEAST_ONCE = 1,
    CA_MQTT_QOS_EXACTLY_ONCE  = 2
} ca_mqtt_qos_t;

/* ===========================================================================
 * MqttTopicDescriptor(Topic, Qos)
 * =========================================================================== */

typedef struct {
    char         *topic;   /* owned, non-null */
    ca_mqtt_qos_t qos;
} ca_mqtt_topic_descriptor_t;

ca_mqtt_topic_descriptor_t *ca_mqtt_topic_descriptor_new(const char *topic,
                                                         ca_mqtt_qos_t qos);
void ca_mqtt_topic_descriptor_destroy(ca_mqtt_topic_descriptor_t *d);
ca_mqtt_topic_descriptor_t *ca_mqtt_topic_descriptor_copy(
    const ca_mqtt_topic_descriptor_t *d);

/* ===========================================================================
 * MqttRetainedMessage(Topic, Payload, RetainedAtUtc)
 * =========================================================================== */

typedef struct {
    char    *topic;              /* owned, non-null */
    uint8_t *payload;            /* owned copy */
    size_t   payload_len;
    int64_t  retained_at_unix_ms;
} ca_mqtt_retained_message_t;

ca_mqtt_retained_message_t *ca_mqtt_retained_message_new(
    const char *topic, const uint8_t *payload, size_t payload_len,
    int64_t retained_at_unix_ms);
void ca_mqtt_retained_message_destroy(ca_mqtt_retained_message_t *m);
ca_mqtt_retained_message_t *ca_mqtt_retained_message_copy(
    const ca_mqtt_retained_message_t *m);

/* ===========================================================================
 * MqttClientDescriptor(ClientId, Host, Port, UseTls, KeepAlive)
 * =========================================================================== */

typedef struct {
    char   *client_id;   /* owned, non-null */
    char   *host;        /* owned, non-null */
    int     port;
    bool    use_tls;
    int64_t keep_alive_ms;   /* TimeSpan KeepAlive in ms */
} ca_mqtt_client_descriptor_t;

ca_mqtt_client_descriptor_t *ca_mqtt_client_descriptor_new(
    const char *client_id, const char *host, int port, bool use_tls,
    int64_t keep_alive_ms);
void ca_mqtt_client_descriptor_destroy(ca_mqtt_client_descriptor_t *d);
ca_mqtt_client_descriptor_t *ca_mqtt_client_descriptor_copy(
    const ca_mqtt_client_descriptor_t *d);

/* ===========================================================================
 * InMemoryMqttBroker
 *
 * Connect: LWW by ClientId. Disconnect: remove. ConnectedClients: snapshot in
 * insertion order (ConcurrentDictionary.Values — unordered in C#; we preserve
 * insertion order deterministically). Subscribe: add topicFilter to the client's
 * filter set (deduped, ordinal). Matches: MQTT wildcard match. PublishRetained:
 * LWW retained store by Topic. GetRetained: copy or NULL. MatchingSubscribers:
 * client ids that have at least one filter matching `topic`, in client
 * insertion order.
 * =========================================================================== */

typedef struct ca_mqtt_broker ca_mqtt_broker_t;

ca_mqtt_broker_t *ca_mqtt_broker_create(void);
void ca_mqtt_broker_destroy(ca_mqtt_broker_t *b);

/* Connect(c) — throws on NULL c in C#; here returns -1 on NULL. 0 on success. */
int ca_mqtt_broker_connect(ca_mqtt_broker_t *b,
                           const ca_mqtt_client_descriptor_t *c);
void ca_mqtt_broker_disconnect(ca_mqtt_broker_t *b, const char *client_id);
/* ConnectedClients — owned array of owned copies. *out=NULL,*count=0 when empty;
 * on error *out=NULL,*count=SIZE_MAX. Returns 0/-1. */
int ca_mqtt_broker_connected_clients(const ca_mqtt_broker_t *b,
                                     ca_mqtt_client_descriptor_t ***out,
                                     size_t *count);

/* Subscribe(clientId, topicFilter). Both must be non-empty/non-whitespace (C#
 * throws ArgumentException otherwise); here returns -1. 0 on success. */
int ca_mqtt_broker_subscribe(ca_mqtt_broker_t *b, const char *client_id,
                             const char *topic_filter);

/* Matches(topic, topicFilter) — MQTT topic-filter match. '#' matches the rest
 * (multi-level), '+' matches exactly one level. Empty topic or filter => false. */
bool ca_mqtt_broker_matches(const char *topic, const char *topic_filter);

/* PublishRetained(m) — LWW by Topic. -1 on NULL m. */
int ca_mqtt_broker_publish_retained(ca_mqtt_broker_t *b,
                                    const ca_mqtt_retained_message_t *m);
/* GetRetained(topic) — fresh copy or NULL. */
ca_mqtt_retained_message_t *ca_mqtt_broker_get_retained(
    const ca_mqtt_broker_t *b, const char *topic);

/* MatchingSubscribers(topic) — owned array of owned client-id strings. Empty =>
 * *out=NULL,*count=0; on error *out=NULL,*count=SIZE_MAX. Returns 0/-1. */
int ca_mqtt_broker_matching_subscribers(const ca_mqtt_broker_t *b,
                                        const char *topic, char ***out,
                                        size_t *count);

/* ===========================================================================
 * IMqttClientAdapter — the injected MQTTnet IMqttClient seam (vtable).
 *
 * The transport hands the adapter an inbound writer on start(); the adapter uses
 * ca_mqtt_inbound_write(writer, bytes, len) to push received application-message
 * payloads upward (each becomes NetworkPayload.Create(bytes)).
 *   is_connected()                 : IMqttClient.IsConnected.
 *   connect()                      : ConnectAsync(options). 0/-1.
 *   subscribe(topic_filter)        : SubscribeAsync(filter). 0/-1.
 *   publish(topic, data, len, qos) : PublishAsync(message). 0/-1.
 *   disconnect()                   : DisconnectAsync(). 0/-1.
 * =========================================================================== */

typedef struct ca_mqtt_inbound_writer ca_mqtt_inbound_writer_t;
/* Push a received application message (bytes) into the transport's inbound
 * channel. Deep-copied. Returns 0 on success, -1 if closed / OOM / NULL. */
int ca_mqtt_inbound_write(ca_mqtt_inbound_writer_t *writer,
                          const uint8_t *data, size_t len);

typedef struct {
    void *self;
    bool (*is_connected)(void *self);
    int  (*connect)(void *self, ca_mqtt_inbound_writer_t *writer);
    int  (*subscribe)(void *self, const char *topic_filter);
    int  (*publish)(void *self, const char *topic, const uint8_t *data,
                    size_t len, ca_mqtt_qos_t qos);
    int  (*disconnect)(void *self);
} ca_mqtt_client_adapter_t;

/* ===========================================================================
 * Deterministic in-memory IMqttClientAdapter for tests / hosts.
 *
 * is_connected is settable AND is flipped true by connect() / false by
 * disconnect() (mirroring MQTTnet, whose IsConnected tracks the session). The
 * last subscribed filter and each published message are logged for inspection;
 * ca_mem_mqtt_adapter_deliver pushes an inbound payload upward while connected
 * (the broker-received seam).
 * =========================================================================== */

typedef struct ca_mem_mqtt_adapter ca_mem_mqtt_adapter_t;

ca_mem_mqtt_adapter_t *ca_mem_mqtt_adapter_create(bool start_connected);
void ca_mem_mqtt_adapter_destroy(ca_mem_mqtt_adapter_t *a);
void ca_mem_mqtt_adapter_set_connected(ca_mem_mqtt_adapter_t *a, bool v);
ca_mqtt_client_adapter_t ca_mem_mqtt_adapter_as_adapter(ca_mem_mqtt_adapter_t *a);
/* Deliver a received message upward (only while connected). Returns 0/-1. */
int ca_mem_mqtt_adapter_deliver(ca_mem_mqtt_adapter_t *a, const uint8_t *data,
                                size_t len);
/* Number of publish() calls issued so far. */
size_t ca_mem_mqtt_adapter_publish_count(const ca_mem_mqtt_adapter_t *a);
/* The topic of the last publish() (borrowed) or NULL. */
const char *ca_mem_mqtt_adapter_last_topic(const ca_mem_mqtt_adapter_t *a);
/* The QoS of the last publish(). */
ca_mqtt_qos_t ca_mem_mqtt_adapter_last_qos(const ca_mem_mqtt_adapter_t *a);
/* The topic filter of the last subscribe() (borrowed) or NULL. */
const char *ca_mem_mqtt_adapter_last_subscription(const ca_mem_mqtt_adapter_t *a);

/* ===========================================================================
 * MqttNetworkTransport
 *
 * Kind == Mqtt. IsAvailable mirrors the client's IsConnected. StartAsync
 * connects the client (handing it the inbound writer) then subscribes to
 * circle/payloads/{localClientId}/#. StopAsync disconnects then completes the
 * inbound channel. SendAsync publishes to circle/payloads/{destinationId} (or
 * circle/payloads/broadcast when the destination is null/empty) with QoS
 * ExactlyOnce iff Priority>=High else AtLeastOnce. ReceiveAsync drains the
 * UNBOUNDED inbound FIFO.
 * =========================================================================== */

typedef struct ca_mqtt_transport ca_mqtt_transport_t;

/* Create over an injected client adapter + the local client id (used to build
 * the subscription topic). client_id must be non-empty. NULL on OOM / empty id. */
ca_mqtt_transport_t *ca_mqtt_transport_create(ca_mqtt_client_adapter_t adapter,
                                              const char *client_id);
void ca_mqtt_transport_destroy(ca_mqtt_transport_t *t);
ca_network_transport_t ca_mqtt_transport_as_transport(ca_mqtt_transport_t *t);
/* Number of inbound payloads currently queued (undrained). */
size_t ca_mqtt_transport_pending(const ca_mqtt_transport_t *t);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_NET_MQTT_H */
