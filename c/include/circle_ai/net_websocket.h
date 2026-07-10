#ifndef CIRCLE_AI_NET_WEBSOCKET_H
#define CIRCLE_AI_NET_WEBSOCKET_H

/*
 * net_websocket.h — CircleAI.Networking.WebSocket (C11 port).
 *
 * The full-duplex WebSocket network transport. Ports
 * CircleAI.Networking.WebSocket 1:1:
 *
 *   Enums     : WebSocketLinkState (Closed/Connecting/Open/CloseSent/
 *               CloseReceived/Closed_Error), WebSocketMessageType (Text/Binary/
 *               Ping/Pong/Close)
 *   Records   : WebSocketEndpointDescriptor (Uri + optional Headers dict +
 *               PingInterval + Subprotocols list), WebSocketFrameSummary
 *   Registry  : InMemoryWebSocketSessionRegistry — Register/Get + SetState/State
 *               (Closed when unknown) + RecordFrame + TotalBytes + FrameCount(by
 *               type).
 *   Adapter   : IWebSocketAdapter — the injected ClientWebSocket seam. connect()
 *               opens the socket (and gets a feed handle to push received binary
 *               frames upward); send() writes a binary frame; close() sends a
 *               NormalClosure. Modelled as a vtable. Ships a deterministic
 *               in-memory adapter.
 *   Transport : WebSocketTransport — INetworkTransport over ClientWebSocket.
 *               Kind==WebSocket, IsAvailable == socket Open. StartAsync connects;
 *               SendAsync sends the payload bytes as one binary frame; StopAsync
 *               closes (NormalClosure) then completes the inbound channel.
 *               ReceiveAsync drains the UNBOUNDED inbound FIFO the adapter feeds
 *               (each received binary frame becomes NetworkPayload.Create(bytes);
 *               a Close frame stops the pump).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no hashtable, no pthreads. Durations are milliseconds; timestamps Unix
 * ms UTC, passed in.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "networking.h"   /* ca_network_transport_t, ca_network_payload_t,
                             ca_net_metadata_pair_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Enums
 * =========================================================================== */

typedef enum {
    CA_WS_STATE_CLOSED          = 0,
    CA_WS_STATE_CONNECTING      = 1,
    CA_WS_STATE_OPEN            = 2,
    CA_WS_STATE_CLOSE_SENT      = 3,
    CA_WS_STATE_CLOSE_RECEIVED  = 4,
    CA_WS_STATE_CLOSED_ERROR    = 5   /* Closed_Error */
} ca_ws_link_state_t;

typedef enum {
    CA_WS_MSG_TEXT   = 0,
    CA_WS_MSG_BINARY = 1,
    CA_WS_MSG_PING   = 2,
    CA_WS_MSG_PONG   = 3,
    CA_WS_MSG_CLOSE  = 4
} ca_ws_message_type_t;

/* ===========================================================================
 * WebSocketEndpointDescriptor(Uri, Headers?, PingInterval, Subprotocols)
 *
 * Headers is an optional IReadOnlyDictionary (has_headers gate models null).
 * Subprotocols is an IReadOnlyList<string> (owned array; may be empty).
 * =========================================================================== */

typedef struct {
    char                   *uri;          /* owned, non-null (Uri.ToString()) */
    bool                    has_headers;
    ca_net_metadata_pair_t *headers;      /* owned array (valid iff has_headers) */
    size_t                  header_count;
    int64_t                 ping_interval_ms; /* TimeSpan PingInterval in ms */
    char                  **subprotocols; /* owned array of owned strings */
    size_t                  subprotocol_count;
} ca_ws_endpoint_descriptor_t;

ca_ws_endpoint_descriptor_t *ca_ws_endpoint_descriptor_new(
    const char *uri, bool has_headers, const ca_net_metadata_pair_t *headers,
    size_t header_count, int64_t ping_interval_ms,
    const char *const *subprotocols, size_t subprotocol_count);
void ca_ws_endpoint_descriptor_destroy(ca_ws_endpoint_descriptor_t *e);
ca_ws_endpoint_descriptor_t *ca_ws_endpoint_descriptor_copy(
    const ca_ws_endpoint_descriptor_t *e);

/* ===========================================================================
 * WebSocketFrameSummary(SessionId, Type, Bytes, AtUtc)
 * =========================================================================== */

typedef struct {
    char                *session_id;   /* owned */
    ca_ws_message_type_t type;
    int                  bytes;
    int64_t              at_unix_ms;
} ca_ws_frame_summary_t;

/* ===========================================================================
 * InMemoryWebSocketSessionRegistry
 *
 * Register: LWW by SessionId. Get: fresh copy or NULL. SetState/State: Closed
 * when unknown. RecordFrame: append. TotalBytes(id): sum of Bytes for the
 * session. FrameCount(id, type): number of frames of `type` for the session.
 * =========================================================================== */

typedef struct ca_ws_registry ca_ws_registry_t;

ca_ws_registry_t *ca_ws_registry_create(void);
void ca_ws_registry_destroy(ca_ws_registry_t *r);

int ca_ws_registry_register(ca_ws_registry_t *r, const char *session_id,
                            const ca_ws_endpoint_descriptor_t *d);
ca_ws_endpoint_descriptor_t *ca_ws_registry_get(const ca_ws_registry_t *r,
                                                const char *session_id);
void ca_ws_registry_set_state(ca_ws_registry_t *r, const char *session_id,
                              ca_ws_link_state_t s);
ca_ws_link_state_t ca_ws_registry_state(const ca_ws_registry_t *r,
                                        const char *session_id);
int ca_ws_registry_record_frame(ca_ws_registry_t *r, const char *session_id,
                                ca_ws_message_type_t type, int bytes,
                                int64_t at_unix_ms);
int64_t ca_ws_registry_total_bytes(const ca_ws_registry_t *r,
                                   const char *session_id);
int ca_ws_registry_frame_count(const ca_ws_registry_t *r,
                               const char *session_id,
                               ca_ws_message_type_t type);

/* ===========================================================================
 * IWebSocketAdapter — the injected ClientWebSocket seam (vtable).
 *
 *   state()                 : ClientWebSocket.State mapped to ca_ws_link_state_t.
 *   connect()               : ConnectAsync(uri) — open the socket; 0/-1. The
 *                             transport hands the adapter a feed handle so the
 *                             adapter can push received binary frames upward.
 *   send(data, len)         : SendAsync(Binary, endOfMessage:true); 0/-1.
 *   close()                 : CloseAsync(NormalClosure, "stop"); 0/-1.
 * The adapter pushes a received binary frame with ca_ws_feed(feed, ...), or a
 * close signal with ca_ws_feed_close(feed) (stops the pump).
 * =========================================================================== */

typedef struct ca_ws_feed ca_ws_feed_t;
/* Push a received binary frame (bytes) into the transport's inbound channel.
 * Deep-copied. Returns 0 on success, -1 on OOM / closed / NULL. */
int ca_ws_feed(ca_ws_feed_t *feed, const uint8_t *data, size_t len);
/* Signal a received Close frame — completes the inbound channel (no more frames
 * accepted). Returns 0/-1. */
int ca_ws_feed_close(ca_ws_feed_t *feed);

typedef struct {
    void *self;
    ca_ws_link_state_t (*state)(void *self);
    int (*connect)(void *self, ca_ws_feed_t *feed);
    int (*send)(void *self, const uint8_t *data, size_t len);
    int (*close)(void *self);
} ca_ws_adapter_t;

/* ===========================================================================
 * Deterministic in-memory IWebSocketAdapter (loopback).
 *
 * connect() sets state Open and records the feed handle. send() records the
 * bytes AND (when looping) feeds them straight back as a binary frame. close()
 * sets state CloseSent. ca_mem_ws_adapter_deliver / _deliver_close let a host
 * push frames / a close upward.
 * =========================================================================== */

typedef struct ca_mem_ws_adapter ca_mem_ws_adapter_t;

/* start_open: initial State is Open (vs Closed). loopback: send() echoes bytes
 * back as an inbound binary frame. */
ca_mem_ws_adapter_t *ca_mem_ws_adapter_create(bool start_open, bool loopback);
void ca_mem_ws_adapter_destroy(ca_mem_ws_adapter_t *a);
void ca_mem_ws_adapter_set_state(ca_mem_ws_adapter_t *a, ca_ws_link_state_t s);
ca_ws_adapter_t ca_mem_ws_adapter_as_adapter(ca_mem_ws_adapter_t *a);
/* Feed a received binary frame upward (only while Open). Returns 0/-1. */
int ca_mem_ws_adapter_deliver(ca_mem_ws_adapter_t *a, const uint8_t *data,
                              size_t len);
/* Feed a received Close frame upward. Returns 0/-1. */
int ca_mem_ws_adapter_deliver_close(ca_mem_ws_adapter_t *a);
/* Number of send() calls issued so far. */
size_t ca_mem_ws_adapter_send_count(const ca_mem_ws_adapter_t *a);

/* ===========================================================================
 * WebSocketTransport
 * =========================================================================== */

typedef struct ca_ws_transport ca_ws_transport_t;

ca_ws_transport_t *ca_ws_transport_create(ca_ws_adapter_t adapter);
void ca_ws_transport_destroy(ca_ws_transport_t *t);
ca_network_transport_t ca_ws_transport_as_transport(ca_ws_transport_t *t);
/* Number of inbound payloads currently queued (undrained). */
size_t ca_ws_transport_pending(const ca_ws_transport_t *t);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_NET_WEBSOCKET_H */
