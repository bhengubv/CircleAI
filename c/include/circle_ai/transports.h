#ifndef CIRCLE_AI_TRANSPORTS_H
#define CIRCLE_AI_TRANSPORTS_H

/*
 * transports.h - CircleAI.Networking.* , CircleAI.AetherNet,
 * CircleAI.Agents.Peer and CircleAI.Federation (C11).
 *
 * Every way one device reaches another, behind one seam, plus the two things
 * built directly on top of it: agents talking to agents, and models averaged
 * across devices that never share their data.
 *
 * ONE TRANSPORT SEAM, MANY RADIOS. Bluetooth, TCP, Wi-Fi Direct, WebSocket,
 * gRPC, MQTT and AetherNet all present the same four operations. That is not
 * tidiness - it is what lets the layer above choose a path at runtime based on
 * what is actually up, which on a phone changes minute to minute.
 *
 * CAPABILITY IS MEASURED, NEVER ASSUMED. Each transport reports real numbers,
 * because the ones that matter here were measured and are not what anybody
 * guesses: Wi-Fi Direct carries about 50 messages a second in both directions,
 * which is enough for voice; BLE carries about 9 a second one way, which is
 * enough for signalling and nothing else. Code that assumed BLE could carry
 * audio is code that fails on the device, not in review.
 *
 * NO LAN ASSUMPTION ANYWHERE. Two devices reach each other over a direct radio
 * link or through a node both have added. Nothing here presumes a shared subnet,
 * a router, or an address that anybody else can route to.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* -- the shared seam ------------------------------------------------------ */

typedef struct {
    char *endpoint_id;
    char *display_name;
    char *address;
    char *transport_id;
} ca_endpoint_descriptor_t;

void ca_endpoint_descriptor_free(ca_endpoint_descriptor_t *descriptor);

/*
 * What a link can actually carry.
 *
 * Measured on real hardware, not derived from a specification. The
 * specification for BLE promises far more than 9 messages a second; the phones
 * in this project deliver 9, one way, and a design that believed the
 * specification put voice on a link that cannot carry it.
 */
typedef struct {
    char *transport_id;
    double messages_per_second_out;
    double messages_per_second_in;
    size_t max_payload_bytes;
    /* Whether both directions work at that rate. BLE here is effectively
     * one-way, and a caller that assumes symmetry designs a handshake that
     * deadlocks. */
    bool bidirectional;
    bool supports_voice;
} ca_capability_profile_t;

void ca_capability_profile_free(ca_capability_profile_t *profile);

typedef struct {
    int64_t at_unix;
    int64_t bytes;
    int64_t elapsed_ms;
} ca_throughput_sample_t;

double ca_throughput_bytes_per_second(const ca_throughput_sample_t *samples,
                                      size_t count);

/*
 * Link state.
 *
 * CHECK THE LEVEL ON THE WAY IN, not just the change. These are edge-triggered
 * in every platform API underneath, so a component that starts up while a link
 * is already established never sees the transition and concludes it is down.
 * That failure looks exactly like a broken radio.
 */
typedef enum {
    CA_LINK_DOWN = 0,
    CA_LINK_CONNECTING,
    CA_LINK_UP,
    CA_LINK_DEGRADED,
    CA_LINK_FAILED
} ca_link_state_t;

const char *ca_link_state_name(ca_link_state_t state);

typedef struct ca_network_transport {
    void *state;
    const char *(*transport_id)(void *state);
    bool (*is_available)(void *state);
    ca_capability_profile_t (*capabilities)(void *state);
    ca_link_state_t (*link_state)(void *state, const char *endpoint_id);
    bool (*send)(void *state, const char *endpoint_id, const uint8_t *payload,
                 size_t len);
    void (*set_receive_handler)(void *state,
                                void (*on_receive)(void *handler_state,
                                                   const char *endpoint_id,
                                                   const uint8_t *payload,
                                                   size_t len),
                                void *handler_state);
    void (*free_fn)(void *state);
} ca_network_transport_t;

void ca_network_transport_free(ca_network_transport_t *transport);

typedef struct ca_peer_discovery {
    void *state;
    ca_endpoint_descriptor_t *(*discover)(void *state, int timeout_ms,
                                          size_t *out_count);
    void (*free_fn)(void *state);
} ca_peer_discovery_t;

void ca_peer_discovery_free(ca_peer_discovery_t *discovery);

typedef struct ca_payload_optimiser {
    void *state;
    /* Shapes a payload for what the link can carry - which is why it takes the
     * profile rather than a fixed size. Caller frees. */
    uint8_t *(*optimise)(void *state, const uint8_t *payload, size_t len,
                         const ca_capability_profile_t *profile, size_t *out_len);
    void (*free_fn)(void *state);
} ca_payload_optimiser_t;

void ca_payload_optimiser_free(ca_payload_optimiser_t *optimiser);

/* -- Bluetooth ------------------------------------------------------------ */

typedef enum {
    CA_BLUETOOTH_DISCONNECTED = 0,
    CA_BLUETOOTH_CONNECTING,
    CA_BLUETOOTH_CONNECTED,
    CA_BLUETOOTH_BONDED,
    CA_BLUETOOTH_LOST
} ca_bluetooth_connection_state_t;

const char *ca_bluetooth_connection_state_name(ca_bluetooth_connection_state_t state);

typedef struct {
    char *device_id;
    char *display_name;
    char *service_uuid;
    char *characteristic_uuid;
    int rssi_dbm;
    ca_bluetooth_connection_state_t connection;
} ca_bluetooth_endpoint_descriptor_t;

void ca_bluetooth_endpoint_descriptor_free(ca_bluetooth_endpoint_descriptor_t *descriptor);

typedef struct {
    int64_t at_unix;
    int64_t bytes;
    int64_t elapsed_ms;
    int rssi_dbm;
    int mtu;
} ca_bluetooth_throughput_sample_t;

/*
 * The measured BLE profile: about 9 messages a second, ONE WAY.
 *
 * Signalling only. Enough to say "I am here" and to carry a handshake; not
 * enough for audio, and not enough to be the primary path for anything a person
 * is waiting on.
 */
ca_capability_profile_t ca_bluetooth_capability_profile_measured(void);
ca_capability_profile_t ca_bluetooth_capability_profiles_signalling(void);
ca_capability_profile_t ca_bluetooth_capability_profiles_bulk(void);

/*
 * ONE GATT OPERATION IN FLIGHT AT A TIME.
 *
 * The stack underneath permits exactly one, and doing work before responding to
 * a write deadlocks the peer - it is waiting for the response, and this side is
 * waiting for whatever the work needs. It presents as a link that connects and
 * then goes silent, which reads as a hardware fault.
 *
 * So: respond first, then do the work.
 */
ca_network_transport_t *ca_bluetooth_network_transport_new(void *adapter);

typedef struct ca_bluetooth_transport_registry ca_bluetooth_transport_registry_t;

ca_bluetooth_transport_registry_t *ca_bluetooth_transport_registry_new(void);
void ca_bluetooth_transport_registry_free(ca_bluetooth_transport_registry_t *registry);

bool ca_bluetooth_transport_registry_add(
    ca_bluetooth_transport_registry_t *registry,
    const ca_bluetooth_endpoint_descriptor_t *descriptor);

size_t ca_bluetooth_transport_registry_count(
    const ca_bluetooth_transport_registry_t *registry);

/* -- Wi-Fi Direct --------------------------------------------------------- */

/*
 * About 50 messages a second, both ways. Voice rides this.
 *
 * TWO THINGS THAT COST DAYS, WRITTEN DOWN:
 *
 * Never stop peer discovery before connecting. Calling stopPeerDiscovery()
 * first drops the peer that was just found, and the connect then fails against
 * a device the stack no longer knows about.
 *
 * Derive the group credentials, do not discover them. The group name and
 * passphrase are computed from the host's public key on both sides. Service
 * discovery was measured dead on these devices, and passing credentials over
 * the link deadlocks - each side waits for the other to speak first.
 */
ca_capability_profile_t ca_wifi_capability_profile_measured(void);

ca_network_transport_t *ca_wi_fi_network_transport_new(void *adapter);
ca_peer_discovery_t *ca_wi_fi_peer_discovery_new(void *adapter);

/* Both sides compute the same group name and passphrase from the host's public
 * key. Caller frees. */
char *ca_wi_fi_derive_group_name(const uint8_t *host_public_key, size_t len);
char *ca_wi_fi_derive_passphrase(const uint8_t *host_public_key, size_t len);

/* -- TCP ------------------------------------------------------------------ */

typedef struct {
    int discovery;
    int transport;
    int media_host;
} ca_tcp_known_ports_t;

/* The ports this project uses. Named rather than scattered as literals: a port
 * guessed in one component and hardcoded in another is a pair that works until
 * one of them is changed. */
ca_tcp_known_ports_t ca_tcp_known_ports(void);

ca_network_transport_t *ca_tcp_network_transport_new(void);

typedef struct ca_tcp_connection_registry ca_tcp_connection_registry_t;

ca_tcp_connection_registry_t *ca_tcp_connection_registry_new(void);
void ca_tcp_connection_registry_free(ca_tcp_connection_registry_t *registry);

/* -- WebSocket ------------------------------------------------------------ */

typedef enum {
    CA_WEB_SOCKET_MESSAGE_TEXT = 0,
    CA_WEB_SOCKET_MESSAGE_BINARY,
    CA_WEB_SOCKET_MESSAGE_PING,
    CA_WEB_SOCKET_MESSAGE_PONG,
    CA_WEB_SOCKET_MESSAGE_CLOSE
} ca_web_socket_message_type_t;

const char *ca_web_socket_message_type_name(ca_web_socket_message_type_t type);

typedef enum {
    CA_WEB_SOCKET_LINK_CLOSED = 0,
    CA_WEB_SOCKET_LINK_CONNECTING,
    CA_WEB_SOCKET_LINK_OPEN,
    CA_WEB_SOCKET_LINK_CLOSING,
    CA_WEB_SOCKET_LINK_FAULTED
} ca_web_socket_link_state_t;

const char *ca_web_socket_link_state_name(ca_web_socket_link_state_t state);

typedef struct {
    char *url;
    char **headers;
    size_t header_count;
    char *sub_protocol;
} ca_web_socket_endpoint_descriptor_t;

void ca_web_socket_endpoint_descriptor_free(ca_web_socket_endpoint_descriptor_t *d);

typedef struct {
    ca_web_socket_message_type_t type;
    size_t payload_bytes;
    bool final_fragment;
    int64_t at_unix_ms;
} ca_web_socket_frame_summary_t;

/*
 * A SUMMARY, never the payload.
 *
 * Frame-level diagnostics are exactly where a transport quietly starts logging
 * what people said. Sizes and types answer every question a transport bug
 * raises; the contents answer none of them.
 */
ca_network_transport_t *ca_web_socket_transport_new(
    const ca_web_socket_endpoint_descriptor_t *descriptor, void *socket);

typedef struct ca_web_socket_session_registry ca_web_socket_session_registry_t;

ca_web_socket_session_registry_t *ca_web_socket_session_registry_new(void);
void ca_web_socket_session_registry_free(ca_web_socket_session_registry_t *registry);

/* -- gRPC, MQTT, HTTP ----------------------------------------------------- */

ca_network_transport_t *ca_grpc_network_transport_new(const char *target, void *channel);

ca_network_transport_t *ca_mqtt_network_transport_new(const char *broker_url,
                                                      const char *client_id,
                                                      void *client);

/* Which family a status code belongs to. A family rather than a code, because
 * every caller here branches on the family and writing 2xx checks by hand is
 * how a 204 ends up treated as a failure. */
typedef enum {
    CA_HTTP_STATUS_INFORMATIONAL = 1,
    CA_HTTP_STATUS_SUCCESS = 2,
    CA_HTTP_STATUS_REDIRECTION = 3,
    CA_HTTP_STATUS_CLIENT_ERROR = 4,
    CA_HTTP_STATUS_SERVER_ERROR = 5,
    CA_HTTP_STATUS_UNKNOWN = 0
} ca_http_status_family_t;

ca_http_status_family_t ca_http_status_family(int status_code);
const char *ca_http_status_family_name(ca_http_status_family_t family);

/* -- AetherNet ------------------------------------------------------------ */

/*
 * The sealed mesh transport.
 *
 * An AetherTag belongs to the DEVICE, the way an address does - not to an app
 * and not to an account. Everything here asks the node for identity rather than
 * holding a key of its own, which is what lets one identity work across apps
 * without any of them being able to impersonate the device.
 */
ca_network_transport_t *ca_aether_network_transport_new(void *node);
ca_peer_discovery_t *ca_aether_peer_discovery_new(void *node);

typedef struct ca_aether_net_registry ca_aether_net_registry_t;

ca_aether_net_registry_t *ca_aether_net_registry_new(void);
void ca_aether_net_registry_free(ca_aether_net_registry_t *registry);

typedef struct ca_aether_net_context_adapter ca_aether_net_context_adapter_t;

/* Supplies mesh facts to the companion - who is reachable, on what link. Facts
 * about LINKS, never about what peers are doing. */
ca_aether_net_context_adapter_t *ca_aether_net_context_adapter_new(void *node);
void ca_aether_net_context_adapter_free(ca_aether_net_context_adapter_t *adapter);

typedef struct ca_aether_net_companion_state_channel ca_aether_net_companion_state_channel_t;

/*
 * Companion state across a person's own devices.
 *
 * SEALED END TO END, and the mesh cannot read it. Relaying nodes forward bytes
 * they cannot open - which is the difference between a mesh that carries your
 * assistant's memory and a mesh that has a copy of it.
 */
ca_aether_net_companion_state_channel_t *ca_aether_net_companion_state_channel_new(
    void *node);

void ca_aether_net_companion_state_channel_free(
    ca_aether_net_companion_state_channel_t *channel);

typedef struct ca_aether_net_directive_sink ca_aether_net_directive_sink_t;

/*
 * Directives arriving from the mesh.
 *
 * TREATED AS DATA, NEVER AS INSTRUCTIONS. A directive is surfaced to a person
 * or handed to a policy that decides; nothing here executes one because it
 * arrived. A mesh peer that could instruct this device is a mesh peer that owns
 * it.
 */
ca_aether_net_directive_sink_t *ca_aether_net_directive_sink_new(void);
void ca_aether_net_directive_sink_free(ca_aether_net_directive_sink_t *sink);

size_t ca_aether_net_directive_sink_pending(const ca_aether_net_directive_sink_t *sink);

/* -- agents over the wire ------------------------------------------------- */

typedef struct ca_agent_peer_protocol {
    void *state;
    bool (*send)(void *state, const char *to_agent_id, const char *payload);
    /* Heap array of *out_count. Caller frees. */
    char **(*receive)(void *state, const char *agent_id, size_t *out_count);
    void (*free_fn)(void *state);
} ca_agent_peer_protocol_t;

void ca_agent_peer_protocol_free(ca_agent_peer_protocol_t *protocol);

ca_agent_peer_protocol_t *ca_agent_peer_protocol_new(void);

typedef struct ca_agent_bus ca_agent_bus_t;

/* Routes between local agents and, over a transport, remote ones. Local and
 * remote are the SAME call on purpose: an agent should not have to know where
 * its peer is, and the day it does is the day the topology is baked in. */
ca_agent_bus_t *ca_agent_bus_new(ca_agent_peer_protocol_t *protocol);
void ca_agent_bus_free(ca_agent_bus_t *bus);

bool ca_agent_bus_publish(ca_agent_bus_t *bus, const char *topic,
                          const char *payload);

/* -- federation ----------------------------------------------------------- */

typedef struct {
    char *round_id;
    int round_number;
    size_t participant_count;
    int64_t started_unix;
    int64_t completed_unix;
    char *model_id;
    char *note;
} ca_federation_round_t;

void ca_federation_round_free(ca_federation_round_t *round);

typedef struct ca_federation_aggregator {
    void *state;
    /* Combines updates into one. Takes WEIGHTS because a device that trained on
     * ten examples must not count as much as one that trained on ten thousand -
     * unweighted averaging lets a single small participant move the model
     * further than everybody else combined. */
    bool (*aggregate)(void *state, const float **updates, const double *weights,
                      size_t participant_count, size_t dims, float *out);
    void (*free_fn)(void *state);
} ca_federation_aggregator_t;

void ca_federation_aggregator_free(ca_federation_aggregator_t *aggregator);

/*
 * FedAvg: the weighted mean of the participants' updates.
 *
 * WHAT LEAVES A DEVICE IS AN UPDATE, NEVER THE DATA. That is the entire point
 * of federating, and it is also not a privacy guarantee on its own - an update
 * carries information about what produced it, and a round with few enough
 * participants can be inverted. So: a MINIMUM PARTICIPANT COUNT is enforced,
 * and a round below it does not run rather than running with weaker protection
 * that nobody is told about.
 */
ca_federation_aggregator_t *ca_federated_averaging_new(size_t minimum_participants);

size_t ca_federated_averaging_minimum_participants(
    const ca_federation_aggregator_t *aggregator);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_TRANSPORTS_H */
