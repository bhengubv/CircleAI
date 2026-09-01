#ifndef CIRCLE_AI_OPS_TRANSPORT_H
#define CIRCLE_AI_OPS_TRANSPORT_H

/*
 * ops_transport.h - CircleAI.Operator, CircleAI.Networking.NearLink,
 * CircleAI.Security.Defense and the rest of CircleAI.Cast (C11).
 *
 * Rolling a model out and being able to roll it back; short-range radio between
 * two devices in the same room; noticing that something on the network is
 * behaving badly; and putting a document on the television.
 *
 * A DEPLOYMENT THAT CANNOT BE ROLLED BACK IS NOT A DEPLOYMENT. The operator
 * half keeps the previous model until the new one has proven itself, because on
 * a device the failure mode is not a bad response - it is a model that will not
 * load at all, on a phone that is now somebody's only phone.
 *
 * THE DEFENCE HALF OBSERVES AND ESCALATES TO A PERSON. It does not block, does
 * not disconnect, and does not change a device's radios or settings. Every
 * escalation ends at somebody who can decide.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "circle_ai/cast.h"

#ifdef __cplusplus
extern "C" {
#endif

/* -- operating a model ---------------------------------------------------- */

typedef enum {
    CA_MODEL_LIFECYCLE_STAGED = 0,
    CA_MODEL_LIFECYCLE_VERIFYING,
    /* Serving some traffic alongside the incumbent. */
    CA_MODEL_LIFECYCLE_CANARY,
    CA_MODEL_LIFECYCLE_ACTIVE,
    /* Superseded but still on disk. NOT the same as retired: this is the one
     * that gets rolled back to, and deleting it here is what turns a bad
     * deployment into an outage. */
    CA_MODEL_LIFECYCLE_SUPERSEDED,
    CA_MODEL_LIFECYCLE_RETIRED,
    CA_MODEL_LIFECYCLE_FAILED
} ca_model_lifecycle_phase_t;

const char *ca_model_lifecycle_phase_name(ca_model_lifecycle_phase_t phase);

typedef struct {
    char *model_id;
    char *version;
    ca_model_lifecycle_phase_t phase;
    int64_t staged_unix;
    int64_t activated_unix;
    /* What this replaced. Carried so a rollback needs no external record and
     * works on a device that has been offline the whole time. */
    char *supersedes_version;
    char *note;
} ca_model_deployment_t;

void ca_model_deployment_free(ca_model_deployment_t *deployment);

typedef struct {
    char *model_id;
    char *active_version;
    bool healthy;
    int64_t last_checked_unix;
    int consecutive_failures;
    char *detail;
} ca_model_status_t;

void ca_model_status_free(ca_model_status_t *status);

typedef struct ca_model_operator {
    void *state;
    bool (*stage)(void *state, const ca_model_deployment_t *deployment);
    /* Promotes staged to active. Fails when the model has not verified - a
     * promotion that skipped verification is exactly the deployment that
     * cannot load. */
    bool (*activate)(void *state, const char *model_id, const char *version,
                     char **out_error);
    /* Back to the superseded version. Must work with no network, because the
     * situation it exists for is a device that cannot load its model. */
    bool (*rollback)(void *state, const char *model_id, char **out_error);
    ca_model_status_t *(*status)(void *state, const char *model_id);
    void (*free_fn)(void *state);
} ca_model_operator_t;

void ca_model_operator_free(ca_model_operator_t *op);

ca_model_operator_t *ca_model_operator_new(void);

/* Stages nothing, activates nothing, and reports every model unhealthy. The
 * default: a host with no operator wired should not appear to have a working
 * deployment pipeline. */
ca_model_operator_t *ca_null_model_operator_new(void);

typedef struct ca_deployment_observer {
    void *state;
    void (*on_phase_change)(void *state, const char *model_id, const char *version,
                            ca_model_lifecycle_phase_t from,
                            ca_model_lifecycle_phase_t to);
    void (*free_fn)(void *state);
} ca_deployment_observer_t;

void ca_deployment_observer_free(ca_deployment_observer_t *observer);
ca_deployment_observer_t *ca_null_deployment_observer_new(void);

/* -- NearLink ------------------------------------------------------------- */

typedef enum {
    CA_NEAR_LINK_UNPAIRED = 0,
    CA_NEAR_LINK_PAIRING,
    CA_NEAR_LINK_PAIRED,
    /* Paired before, not in range now. Distinct from UNPAIRED because the keys
     * are still good and re-pairing would ask somebody to confirm a device they
     * already trusted. */
    CA_NEAR_LINK_PAIRED_AWAY,
    CA_NEAR_LINK_REJECTED
} ca_near_link_pairing_state_t;

const char *ca_near_link_pairing_state_name(ca_near_link_pairing_state_t state);

/*
 * How hard the radio is allowed to work.
 *
 * The radio STAYS UP in every profile. Reachability is the product: a device
 * that saved battery by becoming unreachable has stopped doing the one thing
 * a mesh peer is for. These profiles trade duty cycle and throughput, never
 * presence.
 */
typedef enum {
    CA_NEAR_LINK_POWER_LOW_LATENCY = 0,
    CA_NEAR_LINK_POWER_BALANCED,
    CA_NEAR_LINK_POWER_SAVER
} ca_near_link_power_profile_t;

const char *ca_near_link_power_profile_name(ca_near_link_power_profile_t profile);

typedef struct {
    char *device_id;
    char *display_name;
    ca_near_link_pairing_state_t pairing;
    /* dBm, negative. Signed and in real units rather than a 0-5 bar count,
     * because the decision "is this link good enough for voice" needs the
     * number and bars throw it away. */
    int rssi_dbm;
    int64_t last_seen_unix;
} ca_near_link_device_t;

void ca_near_link_device_free(ca_near_link_device_t *device);

typedef struct {
    int64_t at_unix;
    int64_t bytes;
    int64_t elapsed_ms;
    int rssi_dbm;
} ca_near_link_throughput_sample_t;

/* Measured, never assumed. A transport that assumes its own throughput picks
 * the wrong payload size on the one device where the assumption is wrong, and
 * the symptom is a link that connects and then stalls. */
double ca_near_link_throughput_bytes_per_second(
    const ca_near_link_throughput_sample_t *samples, size_t count);

typedef struct ca_near_link_session ca_near_link_session_t;

void ca_near_link_session_free(ca_near_link_session_t *session);

bool ca_near_link_session_send(ca_near_link_session_t *session,
                               const uint8_t *payload, size_t len);

bool ca_near_link_session_is_up(const ca_near_link_session_t *session);

typedef struct ca_near_link_adapter {
    void *state;
    const char *(*backend_id)(void *state);
    ca_near_link_device_t *(*discover)(void *state, int timeout_ms,
                                       size_t *out_count);
    ca_near_link_session_t *(*open)(void *state, const char *device_id,
                                    ca_near_link_power_profile_t profile);
    void (*free_fn)(void *state);
} ca_near_link_adapter_t;

void ca_near_link_adapter_free(ca_near_link_adapter_t *adapter);

typedef struct ca_near_link_registry ca_near_link_registry_t;

/* Devices this one has paired with. Local only - a pairing list that
 * synchronised anywhere would be a record of which devices are near each
 * other. */
ca_near_link_registry_t *ca_near_link_registry_new(void);
void ca_near_link_registry_free(ca_near_link_registry_t *registry);

bool ca_near_link_registry_remember(ca_near_link_registry_t *registry,
                                    const ca_near_link_device_t *device);

const ca_near_link_device_t *ca_near_link_registry_get(
    const ca_near_link_registry_t *registry, const char *device_id);

size_t ca_near_link_registry_count(const ca_near_link_registry_t *registry);

typedef struct ca_near_link_transport ca_near_link_transport_t;

/*
 * The transport over an adapter.
 *
 * DOES NOT TOUCH DEVICE RADIO STATE. It never enables a radio, never changes a
 * power setting, never toggles anything system-wide - it uses what is on and
 * reports what is not. A library that turns radios on and off changes a device
 * out from under whoever is holding it.
 */
ca_near_link_transport_t *ca_near_link_transport_new(ca_near_link_adapter_t *adapter,
                                                     ca_near_link_registry_t *registry);

void ca_near_link_transport_free(ca_near_link_transport_t *transport);

/* -- network defence ------------------------------------------------------ */

typedef enum {
    CA_THREAT_DIRECTION_INBOUND = 0,
    CA_THREAT_DIRECTION_OUTBOUND,
    CA_THREAT_DIRECTION_LATERAL
} ca_threat_direction_t;

const char *ca_threat_direction_name(ca_threat_direction_t direction);

/* OUTBOUND is the one that matters most on a personal device: something on
 * this phone talking to somewhere it should not is a compromised app, and it is
 * the case a network defence aimed at servers is not looking for. */

typedef enum {
    CA_THREAT_CATEGORY_SCANNING = 0,
    CA_THREAT_CATEGORY_EXFILTRATION,
    CA_THREAT_CATEGORY_COMMAND_AND_CONTROL,
    CA_THREAT_CATEGORY_CREDENTIAL_ACCESS,
    CA_THREAT_CATEGORY_DENIAL_OF_SERVICE,
    CA_THREAT_CATEGORY_ANOMALY
} ca_threat_category_t;

const char *ca_threat_category_name(ca_threat_category_t category);

typedef struct {
    int64_t at_unix;
    char *local_endpoint;
    char *remote_endpoint;
    char *protocol;
    int64_t bytes_out;
    int64_t bytes_in;
    ca_threat_direction_t direction;
} ca_network_observation_t;

void ca_network_observation_free(ca_network_observation_t *observation);

typedef struct ca_network_observation_feed {
    void *state;
    ca_network_observation_t *(*drain)(void *state, size_t *out_count);
    void (*free_fn)(void *state);
} ca_network_observation_feed_t;

void ca_network_observation_feed_free(ca_network_observation_feed_t *feed);

typedef struct {
    ca_threat_category_t category;
    ca_threat_direction_t direction;
    ca_threat_severity_t severity;
    char *summary;
    char *evidence;
    double confidence;
    int64_t at_unix;
} ca_threat_signal_t;

void ca_threat_signal_free(ca_threat_signal_t *signal);

/* -- escalation ----------------------------------------------------------- */

typedef struct ca_sos_escalation {
    void *state;
    /* Reaches a PERSON. Returns false when it could not, which the caller must
     * handle rather than assume - an escalation nobody received is the failure
     * this whole path exists to prevent. */
    bool (*escalate)(void *state, const ca_threat_signal_t *signal);
    void (*free_fn)(void *state);
} ca_sos_escalation_t;

void ca_sos_escalation_free(ca_sos_escalation_t *escalation);

/*
 * Escalates nowhere and says so by returning false.
 *
 * THE DEFAULT, and false rather than true on purpose. A null escalation that
 * reported success would make a device look protected while every alert went
 * into nothing - which is worse than no defence at all, because somebody
 * believes in it.
 */
ca_sos_escalation_t *ca_null_sos_escalation_new(void);

/* Calls the host's function. The whole seam: what "reach a person" means - a
 * notification, an SMS, a call to a neighbour - is the host's to decide. */
ca_sos_escalation_t *ca_delegate_sos_escalation_new(
    bool (*deliver)(void *state, const ca_threat_signal_t *signal), void *state);

typedef struct ca_sos_threat_sink ca_sos_threat_sink_t;

/*
 * Collects signals and escalates the ones that warrant it.
 *
 * De-duplicates within a window: the same finding arriving forty times is one
 * situation, and forty alerts is how somebody learns to ignore all of them.
 */
ca_sos_threat_sink_t *ca_sos_threat_sink_new(ca_sos_escalation_t *escalation,
                                             ca_threat_severity_t minimum_severity,
                                             int64_t dedupe_window_seconds);

void ca_sos_threat_sink_free(ca_sos_threat_sink_t *sink);

bool ca_sos_threat_sink_submit(ca_sos_threat_sink_t *sink,
                               const ca_threat_signal_t *signal);

size_t ca_sos_threat_sink_escalated_count(const ca_sos_threat_sink_t *sink);
size_t ca_sos_threat_sink_suppressed_count(const ca_sos_threat_sink_t *sink);

/* -- casting, the rest of it ---------------------------------------------- */

typedef struct ca_cast_engine {
    void *state;
    ca_dlna_cast_target_t *(*discover)(void *state, int timeout_ms,
                                       size_t *out_count);
    ca_dlna_cast_session_t *(*open)(void *state,
                                    const ca_dlna_cast_target_t *target);
    void (*free_fn)(void *state);
} ca_cast_engine_t;

void ca_cast_engine_free(ca_cast_engine_t *engine);

/* Discovery plus control plus a media host, assembled. The one entry point a
 * host needs; everything in cast.h is what it is made of. */
ca_cast_engine_t *ca_dlna_cast_engine_new(ca_ssdp_client_t *ssdp,
                                          ca_upnp_control_point_t *control,
                                          const char *media_host_base);

typedef struct ca_local_media_host {
    void *state;
    /* Publishes bytes at a URL the television can fetch, and returns it. THE
     * RENDERER PULLS - this is the whole reason a local HTTP server has to
     * exist to cast a file that is already on the device. Caller frees. */
    char *(*publish)(void *state, const uint8_t *bytes, size_t len,
                     const char *mime_type);
    bool (*unpublish)(void *state, const char *url);
    void (*free_fn)(void *state);
} ca_local_media_host_t;

void ca_local_media_host_free(ca_local_media_host_t *host);

/*
 * A minimal TCP media host.
 *
 * Binds to the LAN interface only, never to a public one, and serves ONLY what
 * has been published through it - no path is derived from a request. A media
 * host that resolved paths from the URL is a file server for the whole
 * network, reachable by anything on the same wifi.
 */
ca_local_media_host_t *ca_tcp_media_host_new(int port);

typedef struct {
    char *title;
    ca_document_format_t format;
    uint8_t *bytes;
    size_t len;
    int page_count;
} ca_cast_document_t;

void ca_cast_document_free(ca_cast_document_t *document);

typedef struct ca_document_cast_adapter {
    void *state;
    /* Renders a document into something a television can display - usually
     * images, one per page, because televisions render almost no document
     * format and the ones they do render inconsistently. */
    ca_cast_media_t *(*to_media)(void *state, const ca_cast_document_t *document,
                                 int page_index);
    void (*free_fn)(void *state);
} ca_document_cast_adapter_t;

void ca_document_cast_adapter_free(ca_document_cast_adapter_t *adapter);

/* Converts nothing. The default. */
ca_document_cast_adapter_t *ca_null_document_cast_adapter_new(void);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_OPS_TRANSPORT_H */
