#ifndef CIRCLE_AI_NET_BLUETOOTH_H
#define CIRCLE_AI_NET_BLUETOOTH_H

/*
 * net_bluetooth.h — CircleAI.Networking.Bluetooth (C11 port).
 *
 * The BLE GATT network transport. Ports CircleAI.Networking.Bluetooth 1:1:
 *
 *   Enum      : BluetoothConnectionState
 *   Records   : BluetoothEndpointDescriptor, BluetoothCapabilityProfile,
 *               BluetoothThroughputSample
 *   Presets   : BluetoothCapabilityProfiles (Le5 / Le4 / Classic)
 *   Registry  : InMemoryBluetoothTransportRegistry (endpoints + states + throughput)
 *   Adapter   : IBleGattAdapter — the injected platform GATT seam. StartAsync gets
 *               a writer into the transport's inbound channel; WriteAsync sends;
 *               StopAsync tears down. Modelled as a vtable.
 *   Transport : BluetoothNetworkTransport — INetworkTransport over BLE GATT. Wires
 *               the adapter to an UNBOUNDED inbound FIFO drained by ReceiveAsync.
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
 * BluetoothConnectionState
 * =========================================================================== */

typedef enum {
    CA_BT_STATE_DISCONNECTED = 0,
    CA_BT_STATE_DISCOVERING  = 1,
    CA_BT_STATE_CONNECTING   = 2,
    CA_BT_STATE_CONNECTED    = 3,
    CA_BT_STATE_FAILED       = 4
} ca_bt_connection_state_t;

/* ===========================================================================
 * BluetoothEndpointDescriptor
 * =========================================================================== */

typedef struct {
    char  *device_id;            /* owned, non-null */
    char  *name;                 /* owned, non-null */
    char  *mac_address;          /* owned, non-null */
    char **advertised_services;  /* owned array of owned strings */
    size_t advertised_count;
} ca_bt_endpoint_descriptor_t;

ca_bt_endpoint_descriptor_t *ca_bt_endpoint_descriptor_new(
    const char *device_id, const char *name, const char *mac_address,
    const char *const *advertised_services, size_t advertised_count);
void ca_bt_endpoint_descriptor_destroy(ca_bt_endpoint_descriptor_t *e);
ca_bt_endpoint_descriptor_t *ca_bt_endpoint_descriptor_copy(
    const ca_bt_endpoint_descriptor_t *e);

/* ===========================================================================
 * BluetoothCapabilityProfile
 * =========================================================================== */

typedef struct {
    int    max_mtu_bytes;
    bool   supports_secure_connections;
    bool   supports_high_speed;
    char **compatible_profiles;  /* owned array of owned strings */
    size_t compatible_count;
} ca_bt_capability_profile_t;

ca_bt_capability_profile_t *ca_bt_capability_profile_new(
    int max_mtu_bytes, bool supports_secure_connections,
    bool supports_high_speed, const char *const *compatible_profiles,
    size_t compatible_count);
void ca_bt_capability_profile_destroy(ca_bt_capability_profile_t *p);
ca_bt_capability_profile_t *ca_bt_capability_profile_copy(
    const ca_bt_capability_profile_t *p);

/* BluetoothCapabilityProfiles — well-known presets (fresh owned copies).
 *   Le5     : 247, secure, high-speed, {"GATT","L2CAP"}
 *   Le4     : 23,  secure, no high-speed, {"GATT"}
 *   Classic : 1024, secure, no high-speed, {"SPP","RFCOMM"} */
ca_bt_capability_profile_t *ca_bt_capability_profiles_le5(void);
ca_bt_capability_profile_t *ca_bt_capability_profiles_le4(void);
ca_bt_capability_profile_t *ca_bt_capability_profiles_classic(void);

/* ===========================================================================
 * BluetoothThroughputSample
 * =========================================================================== */

typedef struct {
    char   *device_id;   /* owned */
    double  kbps_read;
    double  kbps_write;
    int64_t at_unix_ms;
} ca_bt_throughput_sample_t;

/* ===========================================================================
 * InMemoryBluetoothTransportRegistry
 *
 * Register: LWW by DeviceId. AllEndpoints: snapshot ordered by Name (ordinal).
 * SetState/State: connection state (Disconnected when unknown). RecordThroughput:
 * append. AvgKbpsRead: mean read throughput for a device (0.0 when none).
 * =========================================================================== */

typedef struct ca_bt_registry ca_bt_registry_t;

ca_bt_registry_t *ca_bt_registry_create(void);
void ca_bt_registry_destroy(ca_bt_registry_t *r);

int ca_bt_registry_register(ca_bt_registry_t *r,
                            const ca_bt_endpoint_descriptor_t *e);
ca_bt_endpoint_descriptor_t *ca_bt_registry_get_endpoint(
    const ca_bt_registry_t *r, const char *device_id);
/* AllEndpoints — owned array of owned copies ordered by Name. On error *out=NULL,
 * *count=SIZE_MAX. Empty => *out=NULL,*count=0. */
int ca_bt_registry_all_endpoints(const ca_bt_registry_t *r,
                                 ca_bt_endpoint_descriptor_t ***out,
                                 size_t *count);
void ca_bt_registry_set_state(ca_bt_registry_t *r, const char *device_id,
                              ca_bt_connection_state_t s);
ca_bt_connection_state_t ca_bt_registry_state(const ca_bt_registry_t *r,
                                              const char *device_id);
int ca_bt_registry_record_throughput(ca_bt_registry_t *r, const char *device_id,
                                     double kbps_read, double kbps_write,
                                     int64_t at_unix_ms);
double ca_bt_registry_avg_kbps_read(const ca_bt_registry_t *r,
                                    const char *device_id);

/* ===========================================================================
 * IBleGattAdapter — injected platform GATT seam (vtable).
 *
 * The transport passes a writer (its inbound sink) to start(); the adapter uses
 * ca_bt_inbound_write(writer, payload) to push mesh-received traffic upward.
 *   is_available()          : IsAvailable.
 *   start(writer)           : StartAsync — begin producing inbound; 0 / -1.
 *   stop()                  : StopAsync — stop producing; 0 / -1.
 *   write(payload)          : WriteAsync — send a payload; 0 / -1.
 * =========================================================================== */

/* Opaque inbound writer handed to the adapter's start(). */
typedef struct ca_bt_inbound_writer ca_bt_inbound_writer_t;
/* Push a payload into the transport's inbound channel (deep-copied). Returns 0
 * on success, -1 if the channel is closed / OOM / NULL. */
int ca_bt_inbound_write(ca_bt_inbound_writer_t *writer,
                        const ca_network_payload_t *payload);

typedef struct {
    void *self;
    bool (*is_available)(void *self);
    int  (*start)(void *self, ca_bt_inbound_writer_t *writer);
    int  (*stop)(void *self);
    int  (*write)(void *self, const ca_network_payload_t *payload);
} ca_ble_gatt_adapter_t;

/* ===========================================================================
 * A deterministic in-memory IBleGattAdapter for tests / hosts.
 *
 * is_available is settable. start() records the writer; while started, any
 * payload handed to ca_mem_ble_adapter_deliver is pushed to the transport's
 * inbound channel (the mesh-received seam). write() echoes the sent payload into
 * a "sent" log the host can inspect (a loopback stand-in for the wire).
 * =========================================================================== */

typedef struct ca_mem_ble_adapter ca_mem_ble_adapter_t;

ca_mem_ble_adapter_t *ca_mem_ble_adapter_create(bool is_available);
void ca_mem_ble_adapter_destroy(ca_mem_ble_adapter_t *a);
void ca_mem_ble_adapter_set_available(ca_mem_ble_adapter_t *a, bool v);
/* Borrowed vtable view (valid for the adapter's lifetime). */
ca_ble_gatt_adapter_t ca_mem_ble_adapter_as_adapter(ca_mem_ble_adapter_t *a);
/* Deliver an inbound payload upward (only while started, i.e. after the transport
 * started the adapter). Returns 0 / -1. */
int ca_mem_ble_adapter_deliver(ca_mem_ble_adapter_t *a,
                               const ca_network_payload_t *payload);
/* Number of payloads passed to write() so far. */
size_t ca_mem_ble_adapter_sent_count(const ca_mem_ble_adapter_t *a);

/* ===========================================================================
 * BluetoothNetworkTransport
 *
 * Kind == Bluetooth. IsAvailable mirrors the adapter. StartAsync starts the
 * adapter with the inbound writer; StopAsync stops the adapter then completes
 * the inbound channel. SendAsync delegates to adapter.WriteAsync. ReceiveAsync
 * drains the UNBOUNDED inbound FIFO the adapter feeds.
 * =========================================================================== */

typedef struct ca_bt_transport ca_bt_transport_t;

ca_bt_transport_t *ca_bt_transport_create(ca_ble_gatt_adapter_t adapter);
void ca_bt_transport_destroy(ca_bt_transport_t *t);
ca_network_transport_t ca_bt_transport_as_transport(ca_bt_transport_t *t);
/* Number of inbound payloads currently queued (undrained). */
size_t ca_bt_transport_pending(const ca_bt_transport_t *t);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_NET_BLUETOOTH_H */
