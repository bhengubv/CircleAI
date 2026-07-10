#ifndef CIRCLE_AI_NET_TCP_H
#define CIRCLE_AI_NET_TCP_H

/*
 * net_tcp.h — CircleAI.Networking.Tcp (C11 port).
 *
 * The raw-TCP network transport. Ports CircleAI.Networking.Tcp 1:1:
 *
 *   Enum      : TcpConnectionState (Disconnected/Connecting/Connected/Closing/
 *               Failed)
 *   Records   : TcpEndpointDescriptor, TcpThroughputSample
 *   Constants : TcpKnownPorts (Http/Https/Ssh/Smtp/Imap/ImapSsl/Pop3/Pop3Ssl/
 *               Mqtt/MqttSsl)
 *   Registry  : InMemoryTcpConnectionRegistry — Register/Get + SetState/State
 *               (Disconnected when unknown) + RecordSample/TotalBytesSent.
 *   Stream    : ITcpStreamAdapter — the injected NetworkStream seam. write()
 *               sends framed bytes on the wire; the adapter feeds received bytes
 *               back with ca_tcp_stream_feed(). Modelled as a vtable. Ships a
 *               deterministic in-memory adapter that loops written frames back.
 *   Transport : TcpNetworkTransport — INetworkTransport over TCP. Acts as client
 *               when a remote endpoint is set (StartAsync connects the adapter,
 *               IsAvailable == connected); acts as a listener when only a listen
 *               port is set (StartAsync starts listening, IsAvailable == false as
 *               there is no client stream). SendAsync writes a 4-byte
 *               LITTLE-ENDIAN length prefix then the payload bytes (BitConverter
 *               on a little-endian host). The receive pump de-frames whole
 *               [len][data] frames the adapter feeds and yields each as
 *               NetworkPayload.Create(bytes). SendAsync before connect throws
 *               InvalidOperationException (surfaced here as -1).
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

/* ===========================================================================
 * TcpConnectionState
 * =========================================================================== */

typedef enum {
    CA_TCP_STATE_DISCONNECTED = 0,
    CA_TCP_STATE_CONNECTING   = 1,
    CA_TCP_STATE_CONNECTED    = 2,
    CA_TCP_STATE_CLOSING      = 3,
    CA_TCP_STATE_FAILED       = 4
} ca_tcp_connection_state_t;

/* ===========================================================================
 * TcpKnownPorts
 * =========================================================================== */

enum {
    CA_TCP_PORT_HTTP     = 80,
    CA_TCP_PORT_HTTPS    = 443,
    CA_TCP_PORT_SSH      = 22,
    CA_TCP_PORT_SMTP     = 25,
    CA_TCP_PORT_IMAP     = 143,
    CA_TCP_PORT_IMAP_SSL = 993,
    CA_TCP_PORT_POP3     = 110,
    CA_TCP_PORT_POP3_SSL = 995,
    CA_TCP_PORT_MQTT     = 1883,
    CA_TCP_PORT_MQTT_SSL = 8883
};

/* ===========================================================================
 * TcpEndpointDescriptor(Host, Port, NoDelay, KeepAlive, ConnectTimeout)
 * =========================================================================== */

typedef struct {
    char   *host;              /* owned, non-null */
    int     port;
    bool    no_delay;
    bool    keep_alive;
    int64_t connect_timeout_ms; /* TimeSpan ConnectTimeout in ms */
} ca_tcp_endpoint_descriptor_t;

ca_tcp_endpoint_descriptor_t *ca_tcp_endpoint_descriptor_new(
    const char *host, int port, bool no_delay, bool keep_alive,
    int64_t connect_timeout_ms);
void ca_tcp_endpoint_descriptor_destroy(ca_tcp_endpoint_descriptor_t *e);
ca_tcp_endpoint_descriptor_t *ca_tcp_endpoint_descriptor_copy(
    const ca_tcp_endpoint_descriptor_t *e);

/* ===========================================================================
 * TcpThroughputSample(EndpointId, BytesSent, BytesReceived, AtUtc)
 * =========================================================================== */

typedef struct {
    char   *endpoint_id;    /* owned */
    int64_t bytes_sent;
    int64_t bytes_received;
    int64_t at_unix_ms;
} ca_tcp_throughput_sample_t;

/* ===========================================================================
 * InMemoryTcpConnectionRegistry
 *
 * Register: LWW by id. Get: fresh copy or NULL. SetState/State: Disconnected
 * when unknown. RecordSample: append. TotalBytesSent(id): sum of BytesSent for
 * the endpoint (0 when none).
 * =========================================================================== */

typedef struct ca_tcp_registry ca_tcp_registry_t;

ca_tcp_registry_t *ca_tcp_registry_create(void);
void ca_tcp_registry_destroy(ca_tcp_registry_t *r);

int ca_tcp_registry_register(ca_tcp_registry_t *r, const char *id,
                             const ca_tcp_endpoint_descriptor_t *d);
ca_tcp_endpoint_descriptor_t *ca_tcp_registry_get(const ca_tcp_registry_t *r,
                                                  const char *id);
void ca_tcp_registry_set_state(ca_tcp_registry_t *r, const char *id,
                               ca_tcp_connection_state_t s);
ca_tcp_connection_state_t ca_tcp_registry_state(const ca_tcp_registry_t *r,
                                                const char *id);
int ca_tcp_registry_record_sample(ca_tcp_registry_t *r, const char *endpoint_id,
                                  int64_t bytes_sent, int64_t bytes_received,
                                  int64_t at_unix_ms);
int64_t ca_tcp_registry_total_bytes_sent(const ca_tcp_registry_t *r,
                                         const char *id);

/* ===========================================================================
 * ITcpStreamAdapter — the injected NetworkStream seam (vtable).
 *
 *   is_connected()          : whether the underlying TcpClient is connected.
 *   connect()               : ConnectAsync — establish the stream; 0/-1. The
 *                             transport hands the adapter a feed handle so the
 *                             adapter can push received bytes upward.
 *   write(data, len)        : WriteAsync — send `len` bytes on the wire; 0/-1.
 *   close()                 : Close the stream/client; 0/-1.
 * The adapter pushes received wire bytes with ca_tcp_stream_feed(feed, ...).
 * =========================================================================== */

typedef struct ca_tcp_stream_feed ca_tcp_stream_feed_t;
/* Push received wire bytes into the transport's receive buffer. The transport
 * de-frames complete [len][data] frames from the accumulated bytes. Deep-copied.
 * Returns 0 on success, -1 on OOM / closed / NULL. */
int ca_tcp_stream_feed(ca_tcp_stream_feed_t *feed, const uint8_t *data,
                       size_t len);

typedef struct {
    void *self;
    bool (*is_connected)(void *self);
    int  (*connect)(void *self, ca_tcp_stream_feed_t *feed);
    int  (*write)(void *self, const uint8_t *data, size_t len);
    int  (*close)(void *self);
} ca_tcp_stream_adapter_t;

/* ===========================================================================
 * Deterministic in-memory ITcpStreamAdapter (loopback).
 *
 * connect() flips connected true and records the feed handle. write() records
 * the bytes AND (when looping) feeds them straight back so a single transport
 * can send-then-receive its own framed traffic. close() flips connected false.
 * ca_mem_tcp_adapter_deliver lets a host push arbitrary raw bytes upward.
 * =========================================================================== */

typedef struct ca_mem_tcp_adapter ca_mem_tcp_adapter_t;

/* loopback: when true, write() feeds the written bytes back into the transport. */
ca_mem_tcp_adapter_t *ca_mem_tcp_adapter_create(bool start_connected,
                                                bool loopback);
void ca_mem_tcp_adapter_destroy(ca_mem_tcp_adapter_t *a);
void ca_mem_tcp_adapter_set_connected(ca_mem_tcp_adapter_t *a, bool v);
ca_tcp_stream_adapter_t ca_mem_tcp_adapter_as_adapter(ca_mem_tcp_adapter_t *a);
/* Feed raw wire bytes upward (only while connected). Returns 0/-1. */
int ca_mem_tcp_adapter_deliver(ca_mem_tcp_adapter_t *a, const uint8_t *data,
                               size_t len);
/* Total bytes handed to write() so far (framed length prefix + body). */
size_t ca_mem_tcp_adapter_bytes_written(const ca_mem_tcp_adapter_t *a);

/* ===========================================================================
 * TcpNetworkTransport
 *
 * Create as a client (remote_host non-null) or a listener (remote_host NULL +
 * listen_port set). At most one mode; if both are NULL/unset StartAsync is a
 * no-op and IsAvailable is false.
 * =========================================================================== */

typedef struct ca_tcp_transport ca_tcp_transport_t;

/* Client-mode ctor: connects the injected stream adapter on StartAsync. */
ca_tcp_transport_t *ca_tcp_transport_create_client(
    ca_tcp_stream_adapter_t adapter, const char *remote_host, int remote_port);
/* Listener-mode ctor: StartAsync begins listening (no client stream). No
 * adapter is used for the listen socket in this in-memory port; SendAsync fails
 * (-1) as there is no connected stream, exactly as the C# (stream is null). */
ca_tcp_transport_t *ca_tcp_transport_create_listener(int listen_port);

void ca_tcp_transport_destroy(ca_tcp_transport_t *t);
ca_network_transport_t ca_tcp_transport_as_transport(ca_tcp_transport_t *t);
/* Number of fully-de-framed inbound payloads currently queued (undrained). */
size_t ca_tcp_transport_pending(const ca_tcp_transport_t *t);
/* Whether the transport is a listener (vs a client). */
bool ca_tcp_transport_is_listener(const ca_tcp_transport_t *t);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_NET_TCP_H */
