#ifndef CIRCLE_AI_NET_NEARLINK_H
#define CIRCLE_AI_NET_NEARLINK_H

/*
 * net_nearlink.h — CircleAI.Networking.NearLink (C11 port).
 *
 * The Huawei SLE / NearLink network transport. Ports
 * CircleAI.Networking.NearLink 1:1:
 *
 *   Enums     : NearLinkPairingState (Unpaired/Pairing/Paired/PairingFailed),
 *               NearLinkPowerProfile (LowEnergy/Balanced/HighThroughput)
 *   Records   : NearLinkDevice, NearLinkSession, NearLinkThroughputSample
 *   Registry  : InMemoryNearLinkRegistry — Register + GetDevice + Devices
 *               (ordered by FriendlyName), SetPairingState/PairingState
 *               (Unpaired when unknown), OpenSession/GetSession/CloseSession/
 *               ActiveSessions, RecordThroughput + AvgRssi (defaults to -127 dBm
 *               when no samples).
 *   Adapter   : INearLinkAdapter — the injected NearLink SDK seam. StartAsync
 *               gets a writer into the transport's inbound channel; SendAsync
 *               sends; StopAsync tears down. Modelled as a vtable. Ships a
 *               deterministic in-memory adapter.
 *   Transport : NearLinkTransport — INetworkTransport over NearLink. Kind==
 *               NearLink, IsAvailable mirrors the adapter. StartAsync starts the
 *               adapter with the inbound writer; SendAsync delegates to
 *               adapter.SendAsync; StopAsync stops the adapter then completes the
 *               inbound channel. ReceiveAsync drains the UNBOUNDED inbound FIFO.
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
 * Enums
 * =========================================================================== */

typedef enum {
    CA_NEARLINK_PAIRING_UNPAIRED       = 0,
    CA_NEARLINK_PAIRING_PAIRING        = 1,
    CA_NEARLINK_PAIRING_PAIRED         = 2,
    CA_NEARLINK_PAIRING_PAIRING_FAILED = 3
} ca_nearlink_pairing_state_t;

typedef enum {
    CA_NEARLINK_POWER_LOW_ENERGY      = 0,
    CA_NEARLINK_POWER_BALANCED        = 1,
    CA_NEARLINK_POWER_HIGH_THROUGHPUT = 2
} ca_nearlink_power_profile_t;

/* ===========================================================================
 * NearLinkDevice(DeviceId, FriendlyName, ManufacturerId, FirmwareVersion)
 * =========================================================================== */

typedef struct {
    char *device_id;        /* owned, non-null */
    char *friendly_name;    /* owned, non-null */
    char *manufacturer_id;  /* owned, non-null */
    char *firmware_version; /* owned, non-null */
} ca_nearlink_device_t;

ca_nearlink_device_t *ca_nearlink_device_new(const char *device_id,
                                             const char *friendly_name,
                                             const char *manufacturer_id,
                                             const char *firmware_version);
void ca_nearlink_device_destroy(ca_nearlink_device_t *d);
ca_nearlink_device_t *ca_nearlink_device_copy(const ca_nearlink_device_t *d);

/* ===========================================================================
 * NearLinkSession(SessionId, DeviceId, PowerProfile, StartedUtc)
 * =========================================================================== */

typedef struct {
    char                       *session_id;    /* owned, non-null */
    char                       *device_id;     /* owned, non-null */
    ca_nearlink_power_profile_t power_profile;
    int64_t                     started_unix_ms;
} ca_nearlink_session_t;

ca_nearlink_session_t *ca_nearlink_session_new(
    const char *session_id, const char *device_id,
    ca_nearlink_power_profile_t power_profile, int64_t started_unix_ms);
void ca_nearlink_session_destroy(ca_nearlink_session_t *s);
ca_nearlink_session_t *ca_nearlink_session_copy(
    const ca_nearlink_session_t *s);

/* ===========================================================================
 * NearLinkThroughputSample(DeviceId, KbpsRead, KbpsWrite, RssiDbm, AtUtc)
 * =========================================================================== */

typedef struct {
    char   *device_id;   /* owned, non-null */
    double  kbps_read;
    double  kbps_write;
    int     rssi_dbm;
    int64_t at_unix_ms;
} ca_nearlink_throughput_sample_t;

/* ===========================================================================
 * InMemoryNearLinkRegistry
 * =========================================================================== */

typedef struct ca_nearlink_registry ca_nearlink_registry_t;

ca_nearlink_registry_t *ca_nearlink_registry_create(void);
void ca_nearlink_registry_destroy(ca_nearlink_registry_t *r);

/* Register(d) — LWW by DeviceId. -1 on NULL. 0 on success. */
int ca_nearlink_registry_register(ca_nearlink_registry_t *r,
                                  const ca_nearlink_device_t *d);
/* GetDevice(id) — fresh copy or NULL. */
ca_nearlink_device_t *ca_nearlink_registry_get_device(
    const ca_nearlink_registry_t *r, const char *device_id);
/* Devices — owned array of owned copies ordered by FriendlyName (ordinal).
 * Empty => *out=NULL,*count=0; on error *out=NULL,*count=SIZE_MAX. 0/-1. */
int ca_nearlink_registry_devices(const ca_nearlink_registry_t *r,
                                 ca_nearlink_device_t ***out, size_t *count);

void ca_nearlink_registry_set_pairing_state(ca_nearlink_registry_t *r,
                                            const char *device_id,
                                            ca_nearlink_pairing_state_t s);
/* Unpaired when unknown. */
ca_nearlink_pairing_state_t ca_nearlink_registry_pairing_state(
    const ca_nearlink_registry_t *r, const char *device_id);

/* OpenSession(s) — LWW by SessionId. -1 on NULL. */
int ca_nearlink_registry_open_session(ca_nearlink_registry_t *r,
                                      const ca_nearlink_session_t *s);
/* GetSession(id) — fresh copy or NULL. */
ca_nearlink_session_t *ca_nearlink_registry_get_session(
    const ca_nearlink_registry_t *r, const char *session_id);
void ca_nearlink_registry_close_session(ca_nearlink_registry_t *r,
                                        const char *session_id);
/* ActiveSessions — owned array of owned copies in insertion order.
 * Empty => *out=NULL,*count=0; on error *out=NULL,*count=SIZE_MAX. 0/-1. */
int ca_nearlink_registry_active_sessions(const ca_nearlink_registry_t *r,
                                         ca_nearlink_session_t ***out,
                                         size_t *count);

int ca_nearlink_registry_record_throughput(ca_nearlink_registry_t *r,
                                            const char *device_id,
                                            double kbps_read, double kbps_write,
                                            int rssi_dbm, int64_t at_unix_ms);
/* AvgRssi(deviceId) — mean RSSI over samples for the device; -127.0 when none
 * (DefaultIfEmpty(-127).Average()). */
double ca_nearlink_registry_avg_rssi(const ca_nearlink_registry_t *r,
                                     const char *device_id);

/* ===========================================================================
 * INearLinkAdapter — injected platform NearLink seam (vtable).
 *
 * The transport passes a writer (its inbound sink) to start(); the adapter uses
 * ca_nearlink_inbound_write(writer, payload) to push mesh-received traffic up.
 *   is_available()  : IsAvailable.
 *   start(writer)   : StartAsync — begin producing inbound; 0/-1.
 *   stop()          : StopAsync — stop producing; 0/-1.
 *   send(payload)   : SendAsync — send a payload; 0/-1.
 * =========================================================================== */

typedef struct ca_nearlink_inbound_writer ca_nearlink_inbound_writer_t;
int ca_nearlink_inbound_write(ca_nearlink_inbound_writer_t *writer,
                              const ca_network_payload_t *payload);

typedef struct {
    void *self;
    bool (*is_available)(void *self);
    int  (*start)(void *self, ca_nearlink_inbound_writer_t *writer);
    int  (*stop)(void *self);
    int  (*send)(void *self, const ca_network_payload_t *payload);
} ca_nearlink_adapter_t;

/* ===========================================================================
 * Deterministic in-memory INearLinkAdapter for tests / hosts.
 *
 * is_available is settable. start() records the writer; while started, any
 * payload handed to ca_mem_nearlink_adapter_deliver is pushed to the transport's
 * inbound channel. send() echoes the sent payload into a "sent" counter.
 * =========================================================================== */

typedef struct ca_mem_nearlink_adapter ca_mem_nearlink_adapter_t;

ca_mem_nearlink_adapter_t *ca_mem_nearlink_adapter_create(bool is_available);
void ca_mem_nearlink_adapter_destroy(ca_mem_nearlink_adapter_t *a);
void ca_mem_nearlink_adapter_set_available(ca_mem_nearlink_adapter_t *a,
                                           bool v);
ca_nearlink_adapter_t ca_mem_nearlink_adapter_as_adapter(
    ca_mem_nearlink_adapter_t *a);
/* Deliver an inbound payload upward (only while started). Returns 0/-1. */
int ca_mem_nearlink_adapter_deliver(ca_mem_nearlink_adapter_t *a,
                                    const ca_network_payload_t *payload);
size_t ca_mem_nearlink_adapter_sent_count(const ca_mem_nearlink_adapter_t *a);

/* ===========================================================================
 * NearLinkTransport
 * =========================================================================== */

typedef struct ca_nearlink_transport ca_nearlink_transport_t;

ca_nearlink_transport_t *ca_nearlink_transport_create(
    ca_nearlink_adapter_t adapter);
void ca_nearlink_transport_destroy(ca_nearlink_transport_t *t);
ca_network_transport_t ca_nearlink_transport_as_transport(
    ca_nearlink_transport_t *t);
/* Number of inbound payloads currently queued (undrained). */
size_t ca_nearlink_transport_pending(const ca_nearlink_transport_t *t);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_NET_NEARLINK_H */
