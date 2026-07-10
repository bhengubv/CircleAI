#ifndef CIRCLE_AI_AETHER_H
#define CIRCLE_AI_AETHER_H

/*
 * aether.h — CircleAI.Aether contracts (C11 port).
 *
 * The one-way boundary between the Aether mesh and BhenguAI. Ports the public
 * enums, event/result records and interfaces of CircleAI.Aether:
 *
 *   Contract 1 (Telemetry):
 *     AetherNodeEvent/Health, AetherTransportEvent, AetherRouteEvent,
 *     AetherSecurityEvent, AetherNetworkEvent (+ their *Kind enums,
 *     AetherThreatLevel, AetherTransportKind)
 *     IAetherTelemetryObserver / IAetherTelemetry (+ NullAetherTelemetry)
 *   Contract 2 (Presence):   AetherInstallLevel, IAetherContext
 *   Contract 3 (Intelligence): NetworkHealthReport, ThreatAssessment,
 *                              RoutingAdvice, TrustScoreUpdate, IAetherIntelligence
 *   Contract 4 (Security Layer): SecurityDirectiveKind, SecurityDirective,
 *                              SecurityPosture, ISecurityDirectiveConsumer,
 *                              IAISecurityLayer
 *   Contract 5 (Auth Challenge): AuthChallengeReason, AuthMethod,
 *                              AuthChallengeResult, IAuthChallenge
 *
 * Interfaces are modelled as vtable structs (self + function pointers). The
 * module ships working in-memory implementations of the interfaces that carry
 * state — an in-memory telemetry hub (fan-out to observers), a config-driven
 * IAetherContext, and a deterministic scripted IAuthChallenge — so nothing is
 * a stub. IAISecurityLayer / IAetherIntelligence concrete bindings live in the
 * AetherNet security module (aethernet_security.h), which wires them to the
 * peer_security engine, mirroring CircleAI.Security.AetherNet.
 *
 * Conventions: ca_ prefix, _t types, opaque create/destroy handles,
 * strdup-owning fields with matching *_free, deep-copy getters, errors via
 * NULL / count SIZE_MAX. In-memory + deterministic; no pthreads; linear arrays.
 *
 * All timestamps are Unix milliseconds UTC (mirrors DateTimeOffset).
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Shared enums
 * =========================================================================== */

/* AetherThreatLevel — protocol-level severity before any AI reasoning. */
typedef enum {
    CA_AETHER_THREAT_NONE     = 0,
    CA_AETHER_THREAT_LOW      = 1,
    CA_AETHER_THREAT_MEDIUM   = 2,
    CA_AETHER_THREAT_HIGH     = 3,
    CA_AETHER_THREAT_CRITICAL = 4
} ca_aether_threat_level_t;

/* ===========================================================================
 * Contract 1 — Telemetry events
 * =========================================================================== */

/* AetherNodeEventKind */
typedef enum {
    CA_AETHER_NODE_JOINED         = 0,
    CA_AETHER_NODE_LEFT           = 1,
    CA_AETHER_NODE_HEALTH_CHANGED = 2
} ca_aether_node_event_kind_t;

/* AetherNodeHealth — point-in-time health snapshot for a node. */
typedef struct {
    double  trust_score;   /* 0.0..1.0 */
    bool    is_reachable;
    int64_t latency_ms;    /* TimeSpan Latency as ms */
    int     hop_count;
} ca_aether_node_health_t;

/* IsValid: TrustScore within [0,1]. */
bool ca_aether_node_health_is_valid(const ca_aether_node_health_t *h);

/* AetherNodeEvent */
typedef struct {
    char                       *node_id;    /* owned */
    ca_aether_node_event_kind_t kind;
    ca_aether_node_health_t     health;
    int64_t                     occurred_at_ms;
} ca_aether_node_event_t;

ca_aether_node_event_t *ca_aether_node_event_create(
    const char *node_id, ca_aether_node_event_kind_t kind,
    ca_aether_node_health_t health, int64_t occurred_at_ms);
void ca_aether_node_event_destroy(ca_aether_node_event_t *e);
ca_aether_node_event_t *ca_aether_node_event_copy(const ca_aether_node_event_t *e);
/* IsExit: Kind == Left. */
bool ca_aether_node_event_is_exit(const ca_aether_node_event_t *e);

/* AetherTransportKind */
typedef enum {
    CA_AETHER_TRANSPORT_WIFI      = 0,
    CA_AETHER_TRANSPORT_BLUETOOTH = 1,
    CA_AETHER_TRANSPORT_LORA      = 2,
    CA_AETHER_TRANSPORT_NFC       = 3,
    CA_AETHER_TRANSPORT_CELLULAR  = 4,
    CA_AETHER_TRANSPORT_ETHERNET  = 5,
    CA_AETHER_TRANSPORT_UNKNOWN   = 6
} ca_aether_transport_kind_t;

/* AetherTransportEventKind */
typedef enum {
    CA_AETHER_TRANSPORT_SELECTED         = 0,
    CA_AETHER_TRANSPORT_CHANGED          = 1,
    CA_AETHER_TRANSPORT_LATENCY_MEASURED = 2,
    CA_AETHER_TRANSPORT_PACKET_LOSS      = 3
} ca_aether_transport_event_kind_t;

/* AetherTransportEvent. Latency / PacketLossRate are optional (has_* gates). */
typedef struct {
    char                            *node_id;   /* owned */
    ca_aether_transport_event_kind_t kind;
    ca_aether_transport_kind_t       transport;
    bool                             has_latency;
    int64_t                          latency_ms;
    bool                             has_packet_loss;
    double                           packet_loss_rate;
    int64_t                          occurred_at_ms;
} ca_aether_transport_event_t;

void ca_aether_transport_event_destroy(ca_aether_transport_event_t *e);
ca_aether_transport_event_t *ca_aether_transport_event_copy(
    const ca_aether_transport_event_t *e);
/* ExceedsLoss(threshold): PacketLossRate set AND > threshold. */
bool ca_aether_transport_event_exceeds_loss(
    const ca_aether_transport_event_t *e, double threshold);

/* AetherRouteEventKind */
typedef enum {
    CA_AETHER_ROUTE_DISCOVERED = 0,
    CA_AETHER_ROUTE_CHANGED    = 1,
    CA_AETHER_ROUTE_FAILED     = 2
} ca_aether_route_event_kind_t;

/* AetherRouteEvent. Path is an owned array of owned strings. */
typedef struct {
    char                       *source_node_id;      /* owned */
    char                       *destination_node_id; /* owned */
    char                      **path;                /* owned array of owned */
    size_t                      path_count;
    ca_aether_route_event_kind_t kind;
    char                       *failure_reason;      /* owned, may be NULL */
    int64_t                     occurred_at_ms;
} ca_aether_route_event_t;

ca_aether_route_event_t *ca_aether_route_event_create(
    const char *source_node_id, const char *destination_node_id,
    const char *const *path, size_t path_count,
    ca_aether_route_event_kind_t kind, const char *failure_reason,
    int64_t occurred_at_ms);
void ca_aether_route_event_destroy(ca_aether_route_event_t *e);
ca_aether_route_event_t *ca_aether_route_event_copy(const ca_aether_route_event_t *e);
/* HopCount == Path.Count. */
size_t ca_aether_route_event_hop_count(const ca_aether_route_event_t *e);
/* IsFailed: Kind == Failed. */
bool ca_aether_route_event_is_failed(const ca_aether_route_event_t *e);

/* AetherSecurityEventKind */
typedef enum {
    CA_AETHER_SEC_NODE_AUTH_ATTEMPT     = 0,
    CA_AETHER_SEC_ROUTING_ANOMALY       = 1,
    CA_AETHER_SEC_NODE_BEHAVIOUR_CHANGE = 2,
    CA_AETHER_SEC_ENCRYPTION_EVENT      = 3,
    CA_AETHER_SEC_INTRUSION_SIGNAL      = 4,
    CA_AETHER_SEC_PRIVILEGE_ATTEMPT     = 5
} ca_aether_security_event_kind_t;

/* One key/value metadata pair (IReadOnlyDictionary<string,string>). */
typedef struct {
    char *key;   /* owned */
    char *value; /* owned */
} ca_aether_metadata_pair_t;

/* AetherSecurityEvent. Metadata is an owned array of owned key/value pairs. */
typedef struct {
    char                           *node_id;      /* owned */
    ca_aether_security_event_kind_t kind;
    ca_aether_threat_level_t        threat_level;
    char                           *description;  /* owned */
    ca_aether_metadata_pair_t      *metadata;     /* owned array */
    size_t                          metadata_count;
    int64_t                         occurred_at_ms;
} ca_aether_security_event_t;

ca_aether_security_event_t *ca_aether_security_event_create(
    const char *node_id, ca_aether_security_event_kind_t kind,
    ca_aether_threat_level_t threat_level, const char *description,
    const ca_aether_metadata_pair_t *metadata, size_t metadata_count,
    int64_t occurred_at_ms);
void ca_aether_security_event_destroy(ca_aether_security_event_t *e);
ca_aether_security_event_t *ca_aether_security_event_copy(
    const ca_aether_security_event_t *e);
/* IsHighSeverity: ThreatLevel High or Critical. */
bool ca_aether_security_event_is_high_severity(
    const ca_aether_security_event_t *e);
/* Lookup a metadata value (ordinal). Borrowed pointer or NULL. */
const char *ca_aether_security_event_metadata(
    const ca_aether_security_event_t *e, const char *key);

/* AetherNetworkEventKind */
typedef enum {
    CA_AETHER_NET_TOPOLOGY_CHANGED    = 0,
    CA_AETHER_NET_CONGESTION_DETECTED = 1,
    CA_AETHER_NET_PARTITION_DETECTED  = 2
} ca_aether_network_event_kind_t;

/* AetherNetworkEvent */
typedef struct {
    ca_aether_network_event_kind_t kind;
    int                            node_count;
    int                            active_route_count;
    double                         congestion_level;
    int64_t                        occurred_at_ms;
} ca_aether_network_event_t;

/* IsHighCongestion: CongestionLevel > 0.75. */
bool ca_aether_network_event_is_high_congestion(
    const ca_aether_network_event_t *e);

/* ===========================================================================
 * Contract 1 — IAetherTelemetryObserver / IAetherTelemetry
 * =========================================================================== */

/* IAetherTelemetryObserver — a vtable of the five event callbacks. Any handler
 * may be NULL (that event kind is ignored for the observer). Events passed to
 * a callback are borrowed for the duration of the call. */
typedef struct {
    void *self;
    void (*on_node_event)(void *self, const ca_aether_node_event_t *e);
    void (*on_transport_event)(void *self, const ca_aether_transport_event_t *e);
    void (*on_route_event)(void *self, const ca_aether_route_event_t *e);
    void (*on_security_event)(void *self, const ca_aether_security_event_t *e);
    void (*on_network_event)(void *self, const ca_aether_network_event_t *e);
} ca_aether_telemetry_observer_t;

/* IAetherTelemetry — vtable. subscribe returns an opaque subscription token
 * (NULL on failure); unsubscribe disposes it (idempotent). Implementations own
 * the fan-out. */
typedef struct ca_aether_subscription ca_aether_subscription_t;

typedef struct {
    void *self;
    ca_aether_subscription_t *(*subscribe)(
        void *self, const ca_aether_telemetry_observer_t *observer);
    void (*unsubscribe)(void *self, ca_aether_subscription_t *sub);
} ca_aether_telemetry_t;

/* --- In-memory telemetry hub (Aether-side publisher) ---
 * A working IAetherTelemetry: fan-out to every subscribed observer. The
 * subscriber list is snapshotted before each publish so a callback may
 * unsubscribe mid-fan-out without corrupting iteration (same discipline as the
 * peer DirectivePublisher). This is the concrete feed the security layer/tests
 * subscribe to. */
typedef struct ca_aether_telemetry_hub ca_aether_telemetry_hub_t;

ca_aether_telemetry_hub_t *ca_aether_telemetry_hub_create(void);
void ca_aether_telemetry_hub_destroy(ca_aether_telemetry_hub_t *hub);
/* Borrowed vtable view (valid for the hub's lifetime). */
ca_aether_telemetry_t ca_aether_telemetry_hub_as_telemetry(
    ca_aether_telemetry_hub_t *hub);
int ca_aether_telemetry_hub_subscriber_count(const ca_aether_telemetry_hub_t *hub);
/* Publish helpers — fan an event out to all current subscribers. */
void ca_aether_telemetry_hub_publish_node(
    ca_aether_telemetry_hub_t *hub, const ca_aether_node_event_t *e);
void ca_aether_telemetry_hub_publish_transport(
    ca_aether_telemetry_hub_t *hub, const ca_aether_transport_event_t *e);
void ca_aether_telemetry_hub_publish_route(
    ca_aether_telemetry_hub_t *hub, const ca_aether_route_event_t *e);
void ca_aether_telemetry_hub_publish_security(
    ca_aether_telemetry_hub_t *hub, const ca_aether_security_event_t *e);
void ca_aether_telemetry_hub_publish_network(
    ca_aether_telemetry_hub_t *hub, const ca_aether_network_event_t *e);

/* --- NullAetherTelemetry --- borrowed singleton vtable; subscribe returns a
 * non-NULL no-op token, no events are ever emitted. */
ca_aether_telemetry_t ca_null_aether_telemetry(void);

/* ===========================================================================
 * Contract 2 — Presence and Capability
 * =========================================================================== */

/* AetherInstallLevel */
typedef enum {
    CA_AETHER_INSTALL_NONE = 0,
    CA_AETHER_INSTALL_APP  = 1,
    CA_AETHER_INSTALL_OS   = 2
} ca_aether_install_level_t;

/* A semantic version (Version). -1 in a component marks it unset/absent. */
typedef struct {
    int major;
    int minor;
    int build;    /* -1 when not specified */
    int revision; /* -1 when not specified */
} ca_aether_version_t;

/* Compare a<=>b like System.Version (unset components treated as 0). */
int ca_aether_version_compare(ca_aether_version_t a, ca_aether_version_t b);

/* IAetherContext — vtable exposing presence/version/capability getters. */
typedef struct {
    void *self;
    ca_aether_install_level_t (*install_level)(void *self);
    bool (*is_available)(void *self);
    /* runtime_version / minimum_required: return true + fill *out when present;
     * false when null (Version? absent). */
    bool (*runtime_version)(void *self, ca_aether_version_t *out);
    bool (*minimum_required)(void *self, ca_aether_version_t *out);
    bool (*is_sufficient)(void *self);
    bool (*requires_auth)(void *self);
    bool (*is_enabled)(void *self);
} ca_aether_context_t;

/* --- In-memory IAetherContext --- config-driven, mirrors the derived-flag
 * semantics (IsAvailable, IsSufficient, RequiresAuth, IsEnabled) exactly. */
typedef struct ca_aether_context_impl ca_aether_context_impl_t;

/* Build a context. has_runtime/has_minimum gate the optional versions;
 * `enabled` is the raw toggle. Derived flags follow the C# rules:
 *   IsAvailable  = InstallLevel != None && enabled
 *   IsSufficient = !has_minimum || (has_runtime && runtime >= minimum)
 *   RequiresAuth = InstallLevel == OS
 *   IsEnabled    = InstallLevel != None && enabled
 * NULL on OOM. */
ca_aether_context_impl_t *ca_aether_context_impl_create(
    ca_aether_install_level_t level,
    bool has_runtime, ca_aether_version_t runtime,
    bool has_minimum, ca_aether_version_t minimum,
    bool enabled);
void ca_aether_context_impl_destroy(ca_aether_context_impl_t *c);
/* Toggle the enabled flag (an OS-managed instance can be switched off). */
void ca_aether_context_impl_set_enabled(ca_aether_context_impl_t *c, bool enabled);
/* Borrowed vtable view. */
ca_aether_context_t ca_aether_context_impl_as_context(ca_aether_context_impl_t *c);

/* ===========================================================================
 * Contract 3 — Intelligence Output
 * =========================================================================== */

/* NetworkHealthReport */
typedef struct {
    double  overall_score;
    int     trusted_node_count;
    int     suspicious_node_count;
    char   *summary;        /* owned */
    int64_t generated_at_ms;
} ca_aether_network_health_report_t;

void ca_aether_network_health_report_destroy(
    ca_aether_network_health_report_t *r);
/* IsValid: OverallScore within [0,1]. */
bool ca_aether_network_health_report_is_valid(
    const ca_aether_network_health_report_t *r);

/* ThreatAssessment */
typedef struct {
    char                    *node_id;     /* owned */
    double                   threat_confidence;
    ca_aether_threat_level_t level;
    char                   **indicators;  /* owned array of owned */
    size_t                   indicator_count;
    int64_t                  assessed_at_ms;
} ca_aether_threat_assessment_t;

void ca_aether_threat_assessment_destroy(ca_aether_threat_assessment_t *a);
/* IsValid: ThreatConfidence within [0,1]. */
bool ca_aether_threat_assessment_is_valid(
    const ca_aether_threat_assessment_t *a);

/* RoutingAdvice */
typedef struct {
    char   *destination_node_id;    /* owned */
    char  **recommended_path;       /* owned array of owned */
    size_t  recommended_path_count;
    char  **avoid_nodes;            /* owned array of owned */
    size_t  avoid_node_count;
    double  confidence;
    char   *reasoning;              /* owned */
    int64_t generated_at_ms;
} ca_aether_routing_advice_t;

void ca_aether_routing_advice_destroy(ca_aether_routing_advice_t *a);

/* TrustScoreUpdate */
typedef struct {
    char   *node_id;        /* owned */
    double  previous_score;
    double  current_score;
    char   *reason;         /* owned */
    int64_t updated_at_ms;
} ca_aether_trust_score_update_t;

/* Fields-only free (struct is caller-owned, filled by a reader). */
void ca_aether_trust_score_update_destroy(ca_aether_trust_score_update_t *u);
/* HasChanged: |Current - Previous| > 0.001. */
bool ca_aether_trust_score_update_has_changed(
    const ca_aether_trust_score_update_t *u);
/* IsDegraded: Current < Previous. */
bool ca_aether_trust_score_update_is_degraded(
    const ca_aether_trust_score_update_t *u);

/* Opaque cursor over a trust-score update stream (StreamTrustScoresAsync). */
typedef struct ca_aether_trust_score_reader ca_aether_trust_score_reader_t;
void ca_aether_trust_score_reader_destroy(ca_aether_trust_score_reader_t *r);
/* Copy the next update into *out (deep). Returns true if produced. */
bool ca_aether_trust_score_reader_next(
    ca_aether_trust_score_reader_t *r, ca_aether_trust_score_update_t *out);

/* Construct a reader from caller-supplied callbacks over a user context. The
 * intelligence binding uses this to wrap an underlying peer stream: `next`
 * fills *out with the next update (returns false when drained); `destroy`
 * releases the user context (may be NULL). The reader owns the context via
 * `destroy`. NULL on OOM / NULL next. */
ca_aether_trust_score_reader_t *ca_aether_trust_score_reader_create(
    void *user,
    bool (*next)(void *user, ca_aether_trust_score_update_t *out),
    void (*destroy)(void *user));

/* IAetherIntelligence — vtable. The get_* calls write an owning result and
 * return 0 / -1. stream opens a reader (NULL on failure). */
typedef struct {
    void *self;
    int (*get_network_health)(void *self,
                              ca_aether_network_health_report_t *out);
    int (*assess_threat)(void *self, const char *node_id,
                         ca_aether_threat_assessment_t *out);
    int (*get_routing_advice)(void *self, const char *destination_node_id,
                              ca_aether_routing_advice_t *out);
    ca_aether_trust_score_reader_t *(*stream_trust_scores)(void *self);
} ca_aether_intelligence_t;

/* ===========================================================================
 * Contract 4 — Security Layer
 * =========================================================================== */

/* SecurityDirectiveKind */
typedef enum {
    CA_AETHER_DIRECTIVE_UPDATE_NODE_TRUST  = 0,
    CA_AETHER_DIRECTIVE_AVOID_NODE         = 1,
    CA_AETHER_DIRECTIVE_QUARANTINE_NODE    = 2,
    CA_AETHER_DIRECTIVE_RELEASE_NODE       = 3,
    CA_AETHER_DIRECTIVE_REQUEST_REAUTH     = 4,
    CA_AETHER_DIRECTIVE_ELEVATE_MONITORING = 5
} ca_aether_security_directive_kind_t;

/* SecurityDirective. TrustScoreOverride and Duration are optional. */
typedef struct {
    ca_aether_security_directive_kind_t kind;
    char                               *target_node_id;      /* owned, may NULL */
    bool                                has_trust_score_override;
    double                              trust_score_override;
    ca_aether_threat_level_t            threat_level;
    char                               *reason;              /* owned */
    bool                                has_duration;
    int64_t                             duration_ms;
    int64_t                             issued_at_ms;
} ca_aether_security_directive_t;

/* Build an owning directive (heap). has_* gate the optional fields. NULL OOM. */
ca_aether_security_directive_t *ca_aether_security_directive_create(
    ca_aether_security_directive_kind_t kind, const char *target_node_id,
    bool has_trust_score_override, double trust_score_override,
    ca_aether_threat_level_t threat_level, const char *reason,
    bool has_duration, int64_t duration_ms, int64_t issued_at_ms);
void ca_aether_security_directive_destroy(ca_aether_security_directive_t *d);
ca_aether_security_directive_t *ca_aether_security_directive_copy(
    const ca_aether_security_directive_t *d);
/* HasTarget: TargetNodeId is non-empty / not whitespace. */
bool ca_aether_security_directive_has_target(
    const ca_aether_security_directive_t *d);
/* IsPermanent: Duration is null. */
bool ca_aether_security_directive_is_permanent(
    const ca_aether_security_directive_t *d);

/* SecurityPosture */
typedef struct {
    ca_aether_threat_level_t overall_threat_level;
    int                      quarantined_node_count;
    int                      monitored_node_count;
    bool                     is_active;
    int64_t                  assessed_at_ms;
} ca_aether_security_posture_t;

/* ISecurityDirectiveConsumer — vtable. on_directive receives a borrowed
 * directive (deep-copy to retain). */
typedef struct {
    void *self;
    void (*on_directive)(void *self, const ca_aether_security_directive_t *d);
} ca_aether_security_directive_consumer_t;

/* IAISecurityLayer — vtable. start wires the layer to a telemetry feed;
 * subscribe registers a directive consumer (returns an opaque token, NULL on
 * failure) and unsubscribe disposes it; get_posture writes a snapshot. */
typedef struct ca_aether_directive_subscription ca_aether_directive_subscription_t;

typedef struct {
    void *self;
    void (*start)(void *self, const ca_aether_telemetry_t *telemetry);
    void (*stop)(void *self);
    ca_aether_directive_subscription_t *(*subscribe_to_directives)(
        void *self, const ca_aether_security_directive_consumer_t *consumer);
    void (*unsubscribe_directives)(void *self,
                                   ca_aether_directive_subscription_t *sub);
    void (*get_posture)(void *self, ca_aether_security_posture_t *out);
} ca_aether_ai_security_layer_t;

/* ===========================================================================
 * Contract 5 — Auth Challenge
 * =========================================================================== */

/* AuthChallengeReason */
typedef enum {
    CA_AUTH_REASON_OS_LEVEL_TOGGLE       = 0,
    CA_AUTH_REASON_THREAT_THRESHOLD      = 1,
    CA_AUTH_REASON_PRIVILEGED_OPERATION  = 2,
    CA_AUTH_REASON_PERIODIC_REVALIDATION = 3,
    CA_AUTH_REASON_MANUAL_REQUEST        = 4
} ca_auth_challenge_reason_t;

/* AuthMethod — ordered by strength; higher value is stronger. */
typedef enum {
    CA_AUTH_METHOD_BIOMETRIC                  = 1,
    CA_AUTH_METHOD_DEVICE_ADMIN               = 2,
    CA_AUTH_METHOD_BIOMETRIC_AND_DEVICE_ADMIN = 3,
    CA_AUTH_METHOD_CUSTOM                     = 4
} ca_auth_method_t;

/* AuthChallengeResult */
typedef struct {
    bool             succeeded;
    ca_auth_method_t method_used;
    char            *failure_reason; /* owned, NULL on success */
    int64_t          completed_at_ms;
} ca_auth_challenge_result_t;

void ca_auth_challenge_result_destroy(ca_auth_challenge_result_t *r);
ca_auth_challenge_result_t *ca_auth_challenge_result_copy(
    const ca_auth_challenge_result_t *r);
/* AuthChallengeResult.Success(method) — completed_at from now_ms. */
ca_auth_challenge_result_t ca_auth_challenge_result_success(
    ca_auth_method_t method, int64_t now_ms);
/* AuthChallengeResult.Failure(method, reason) — completed_at from now_ms. */
ca_auth_challenge_result_t ca_auth_challenge_result_failure(
    ca_auth_method_t method, const char *reason, int64_t now_ms);

/* IAuthChallenge — vtable. has_minimum gates the AuthMethod? argument (null =>
 * BiometricAndDeviceAdmin default). Results write into *out (owning). */
typedef struct {
    void *self;
    int (*challenge)(void *self, ca_auth_challenge_reason_t reason,
                     bool has_minimum, ca_auth_method_t minimum_method,
                     const char *prompt, ca_auth_challenge_result_t *out);
    int (*request_os_toggle)(void *self, bool enable,
                             ca_auth_challenge_result_t *out);
} ca_auth_challenge_t;

/* --- Scripted in-memory IAuthChallenge ---
 * Deterministic adapter used where a native biometric API is absent (tests,
 * server). It enforces the platform minimum exactly like the contract states:
 * the effective minimum is max(requested, BiometricAndDeviceAdmin); the
 * challenge SUCCEEDS with `available_method` when available_method >= effective
 * minimum, otherwise FAILS. RequestOsToggle always demands
 * BiometricAndDeviceAdmin at minimum. `available_method` models the strongest
 * credential the device can currently satisfy. */
typedef struct ca_scripted_auth_challenge ca_scripted_auth_challenge_t;

ca_scripted_auth_challenge_t *ca_scripted_auth_challenge_create(
    ca_auth_method_t available_method, int64_t fixed_now_ms);
void ca_scripted_auth_challenge_destroy(ca_scripted_auth_challenge_t *a);
/* Adjust the strongest available credential at runtime. */
void ca_scripted_auth_challenge_set_available(
    ca_scripted_auth_challenge_t *a, ca_auth_method_t available_method);
/* Borrowed vtable view. */
ca_auth_challenge_t ca_scripted_auth_challenge_as_challenge(
    ca_scripted_auth_challenge_t *a);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_AETHER_H */
