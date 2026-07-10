#ifndef CIRCLE_AI_PEER_SECURITY_H
#define CIRCLE_AI_PEER_SECURITY_H

/*
 * peer_security.h — transport-agnostic peer security layer (C11 port).
 *
 * Ports the transport-neutral half of CircleAI.Security:
 *   Peer* enums + records                 (PeerSecurityTypes.cs)
 *   IPeerDirectiveConsumer                (PeerSecurityTypes.cs)
 *   IPeerSecurityLayer / IPeerIntelligence(PeerSecurityTypes.cs)
 *   IPeerSecurityEventFeed                (PeerSecurityTypes.cs)
 *   ThreatDetector                        (ThreatDetector.cs)
 *   SecurityOptions                       (SecurityOptions.cs)
 *   NodeTrustEntry / NodeTrustRegistry    (NodeTrustRegistry.cs)
 *   DirectivePublisher                    (DirectivePublisher.cs)
 *   SecurityLayerService (IPeerSecurityLayer) (AISecurityLayerService.cs)
 *   PeerIntelligenceService (IPeerIntelligence)(AetherIntelligenceService.cs)
 *
 * Conventions: ca_ prefix, _t types, opaque create/destroy handles,
 * strdup-owning fields with matching *_free, deep-copy getters, errors via
 * NULL / negative rc. In-memory + deterministic; no pthreads; linear arrays.
 *
 * Streaming: the registry's trust-score updates are an UNBOUNDED growable log
 * drained cursor-wise (mirrors the C# unbounded Channel<PeerTrustScoreUpdate>).
 * Publishing never blocks; a reader opened later still replays every update.
 * DirectivePublisher is a true fan-out — every subscriber receives every
 * directive; the subscriber list is snapshotted before dispatch so a callback
 * may unsubscribe mid-fan-out without corrupting iteration.
 *
 * The passive recovery loop is exposed as an explicit tick
 * (ca_security_layer_service_recover_tick) rather than a background thread, so
 * tests drive it deterministically. StartAsync/StopAsync toggle the active
 * flag that GetPosture reports.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Enumerations — PeerSecurityTypes.cs
 * =========================================================================== */

typedef enum {
    CA_PEER_EVENT_AUTH_ATTEMPT       = 0,
    CA_PEER_EVENT_ROUTING_ANOMALY    = 1,
    CA_PEER_EVENT_BEHAVIOUR_CHANGE   = 2,
    CA_PEER_EVENT_ENCRYPTION_EVENT   = 3,
    CA_PEER_EVENT_INTRUSION_SIGNAL   = 4,
    CA_PEER_EVENT_PRIVILEGE_ATTEMPT  = 5,
    CA_PEER_EVENT_CONNECTION_ANOMALY = 6,
    CA_PEER_EVENT_DATA_EXFILTRATION  = 7,
    CA_PEER_EVENT_DENIAL_OF_SERVICE  = 8,
    CA_PEER_EVENT_UNKNOWN            = 9
} ca_peer_security_event_kind_t;

typedef enum {
    CA_PEER_THREAT_NONE     = 0,
    CA_PEER_THREAT_LOW      = 1,
    CA_PEER_THREAT_MEDIUM   = 2,
    CA_PEER_THREAT_HIGH     = 3,
    CA_PEER_THREAT_CRITICAL = 4
} ca_peer_threat_level_t;

typedef enum {
    CA_PEER_DIRECTIVE_ELEVATE_MONITORING = 0,
    CA_PEER_DIRECTIVE_AVOID_NODE         = 1,
    CA_PEER_DIRECTIVE_QUARANTINE_NODE    = 2,
    CA_PEER_DIRECTIVE_RELEASE_NODE       = 3
} ca_peer_directive_kind_t;

/* ===========================================================================
 * Records — value structs with owning string fields + *_free helpers.
 * =========================================================================== */

/* PeerSecurityEvent */
typedef struct {
    char                          *node_id;      /* owned */
    ca_peer_security_event_kind_t  kind;
    ca_peer_threat_level_t         threat_level;
    char                          *description;  /* owned */
    char                          *transport_id; /* owned */
    int64_t                        occurred_at_ms;
} ca_peer_security_event_t;

/* Create a deep-owning event. Any string may be NULL (stored as ""). NULL on
 * OOM. Free with ca_peer_security_event_free (frees fields; not the struct if
 * stack-allocated — see *_destroy for heap variant). */
ca_peer_security_event_t *ca_peer_security_event_create(
    const char *node_id, ca_peer_security_event_kind_t kind,
    ca_peer_threat_level_t threat_level, const char *description,
    const char *transport_id, int64_t occurred_at_ms);
void ca_peer_security_event_destroy(ca_peer_security_event_t *e); /* frees struct */
/* Deep copy into a heap struct. */
ca_peer_security_event_t *ca_peer_security_event_copy(
    const ca_peer_security_event_t *e);

/* PeerDirective */
typedef struct {
    ca_peer_directive_kind_t kind;
    char                    *target_node_id; /* owned */
    double                   trust_score;
    ca_peer_threat_level_t   threat_level;
    char                    *reason;          /* owned */
    bool                     has_duration;
    int64_t                  duration_ms;     /* valid iff has_duration */
    int64_t                  issued_at_ms;
} ca_peer_directive_t;

void ca_peer_directive_destroy(ca_peer_directive_t *d);
ca_peer_directive_t *ca_peer_directive_copy(const ca_peer_directive_t *d);

/* PeerTrustScoreUpdate */
typedef struct {
    char   *node_id;        /* owned */
    double  previous_score;
    double  new_score;
    char   *reason;         /* owned */
    int64_t changed_at_ms;
} ca_peer_trust_score_update_t;

/* Fields-only free — the struct is caller-owned (filled by
 * ca_trust_update_reader_next); frees node_id/reason but not the struct. */
void ca_peer_trust_score_update_destroy(ca_peer_trust_score_update_t *u);

/* PeerSecurityPosture */
typedef struct {
    ca_peer_threat_level_t overall_threat_level;
    int                    quarantined_peer_count;
    int                    monitored_peer_count;
    bool                   is_active;
    int64_t                generated_at_ms;
} ca_peer_security_posture_t;

/* PeerNetworkHealthReport */
typedef struct {
    double  overall_score;
    int     trusted_peer_count;
    int     suspicious_peer_count;
    char   *summary;        /* owned */
    int64_t generated_at_ms;
} ca_peer_network_health_report_t;

void ca_peer_network_health_report_destroy(ca_peer_network_health_report_t *r);

/* PeerThreatAssessment */
typedef struct {
    char                  *node_id;     /* owned */
    double                 confidence;
    ca_peer_threat_level_t threat_level;
    char                 **indicators;  /* owned array of owned strings */
    size_t                 indicator_count;
    int64_t                assessed_at_ms;
} ca_peer_threat_assessment_t;

void ca_peer_threat_assessment_destroy(ca_peer_threat_assessment_t *a);

/* PeerRoutingAdvice */
typedef struct {
    char   *destination_node_id;   /* owned */
    char  **recommended_path;      /* owned array of owned strings */
    size_t  recommended_path_count;
    char  **avoid_node_ids;        /* owned array of owned strings */
    size_t  avoid_node_count;
    double  confidence;
    char   *reasoning;             /* owned */
    int64_t generated_at_ms;
} ca_peer_routing_advice_t;

void ca_peer_routing_advice_destroy(ca_peer_routing_advice_t *a);

/* ===========================================================================
 * ThreatDetector — stateless helpers (ThreatDetector.cs)
 * =========================================================================== */

/* BaseWeight(kind) * ThreatMultiplier(level). 0 when level == None. */
double ca_threat_detector_compute_degradation(const ca_peer_security_event_t *e);

/*
 * DetectIndicators over events[] within window_ms of now_ms. Writes up to
 * *io_count indicator strings (deep-copied, caller frees each + the array is
 * caller-provided). Returns the number of indicators produced (0..6). The
 * indicator tags and their ordering match the C# exactly.
 *
 * Caller passes an out[] of at least 6 char* slots and sets *io_count to its
 * capacity; on return *io_count holds the number written.
 */
size_t ca_threat_detector_detect_indicators(
    const ca_peer_security_event_t *const *events, size_t event_count,
    int64_t window_ms, int64_t now_ms,
    char **out, size_t out_cap);

/* ===========================================================================
 * SecurityOptions — SecurityOptions.cs
 * =========================================================================== */

typedef struct {
    double  elevate_monitoring_threshold; /* 0.75 */
    double  avoid_node_threshold;         /* 0.50 */
    double  quarantine_threshold;         /* 0.25 */
    double  recovery_rate_per_second;     /* 0.001 */
    int64_t event_window_ms;              /* 300000 (5 min) */
    int     max_events_per_node;          /* 100 */
    double  initial_trust_score;          /* 1.0 */
} ca_security_options_t;

/* Populate with the C# defaults. */
void ca_security_options_init_defaults(ca_security_options_t *opts);

/* ===========================================================================
 * NodeTrustRegistry + NodeTrustEntry — NodeTrustRegistry.cs
 * =========================================================================== */

typedef struct ca_node_trust_registry ca_node_trust_registry_t;

/* Registry copies the options by value. NULL on OOM. */
ca_node_trust_registry_t *ca_node_trust_registry_create(
    const ca_security_options_t *options);
void ca_node_trust_registry_destroy(ca_node_trust_registry_t *reg);

/* Returns/creates the entry's trust score for node_id (initial score for a
 * fresh node). */
double ca_node_trust_registry_get_or_create(ca_node_trust_registry_t *reg,
                                             const char *node_id);

/* Current trust score for node_id, or initial_trust_score if unknown. */
double ca_node_trust_registry_get_trust_score(
    const ca_node_trust_registry_t *reg, const char *node_id);

/* Copies all tracked node ids into a newly-allocated array (caller frees each
 * string + the array). Returns count; writes NULL/0 on empty or OOM. */
size_t ca_node_trust_registry_all_node_ids(
    const ca_node_trust_registry_t *reg, char ***out_ids);

/*
 * ApplyDegradation for security_event by degradation_amount. Clamps score to
 * [0,1], appends the event to bounded per-node history, publishes a trust
 * update when the score moved by > 0.0001. Writes previous/current scores.
 * Returns 0 on success, -1 on bad args / OOM.
 */
int ca_node_trust_registry_apply_degradation(
    ca_node_trust_registry_t       *reg,
    const ca_peer_security_event_t *security_event,
    double                          degradation_amount,
    double *out_previous, double *out_current);

/* Passive recovery of every peer by recovery_rate_per_second * elapsed_ms.
 * Peers already at 1.0 are skipped; each recovered peer publishes an update. */
void ca_node_trust_registry_apply_recovery(ca_node_trust_registry_t *reg,
                                            int64_t elapsed_ms);

/*
 * Recent events for node_id within event_window_ms of now_ms. Writes a
 * newly-allocated array of deep-copied event pointers (caller destroys each
 * with ca_peer_security_event_destroy + frees the array). Returns count.
 */
size_t ca_node_trust_registry_get_recent_events(
    const ca_node_trust_registry_t *reg, const char *node_id, int64_t now_ms,
    ca_peer_security_event_t ***out_events);

/*
 * TrustScoreUpdates reader — cursor-drained unbounded stream. Every published
 * update (including those before the reader opened) is replayed in order.
 */
typedef struct ca_trust_update_reader ca_trust_update_reader_t;
ca_trust_update_reader_t *ca_node_trust_registry_open_reader(
    ca_node_trust_registry_t *reg);
void ca_trust_update_reader_destroy(ca_trust_update_reader_t *r);
/* Copies the next unread update into *out (deep). Returns true if produced. */
bool ca_trust_update_reader_next(ca_trust_update_reader_t *r,
                                 ca_peer_trust_score_update_t *out);

/* ===========================================================================
 * DirectivePublisher — DirectivePublisher.cs (fan-out)
 * =========================================================================== */

typedef struct ca_directive_publisher ca_directive_publisher_t;

/* Consumer callback (IPeerDirectiveConsumer.OnDirective). The directive is
 * owned by the publisher for the duration of the call; deep-copy if retained. */
typedef void (*ca_peer_directive_consumer_fn)(void *user,
                                              const ca_peer_directive_t *directive);

ca_directive_publisher_t *ca_directive_publisher_create(void);
void ca_directive_publisher_destroy(ca_directive_publisher_t *pub);

/* Subscribe. Returns an opaque token to pass to unsubscribe, or NULL on OOM /
 * NULL consumer. Idempotent unsubscribe. */
typedef struct ca_directive_subscription ca_directive_subscription_t;
ca_directive_subscription_t *ca_directive_publisher_subscribe(
    ca_directive_publisher_t *pub, ca_peer_directive_consumer_fn consumer,
    void *user);
void ca_directive_publisher_unsubscribe(ca_directive_publisher_t *pub,
                                        ca_directive_subscription_t *sub);

/* Fan directive out to all current subscribers (snapshot then fire). */
void ca_directive_publisher_publish(ca_directive_publisher_t *pub,
                                    const ca_peer_directive_t *directive);

int ca_directive_publisher_subscriber_count(const ca_directive_publisher_t *pub);

/* ===========================================================================
 * SecurityLayerService — IPeerSecurityLayer (AISecurityLayerService.cs)
 * =========================================================================== */

typedef struct ca_security_layer_service ca_security_layer_service_t;

/* Borrows registry, options, publisher (does not own them). NULL on OOM. */
ca_security_layer_service_t *ca_security_layer_service_create(
    ca_node_trust_registry_t *registry, const ca_security_options_t *options,
    ca_directive_publisher_t *publisher);
void ca_security_layer_service_destroy(ca_security_layer_service_t *svc);

/* StartAsync / StopAsync — toggle the active flag (no background thread). */
void ca_security_layer_service_start(ca_security_layer_service_t *svc);
void ca_security_layer_service_stop(ca_security_layer_service_t *svc);
bool ca_security_layer_service_is_active(const ca_security_layer_service_t *svc);

/*
 * HandlePeerEvent — degrade the peer's trust and evaluate thresholds, issuing
 * at most one directive per event (most-severe wins). No-op when the event's
 * threat level is None.
 */
void ca_security_layer_service_handle_peer_event(
    ca_security_layer_service_t *svc, const ca_peer_security_event_t *e);

/* HandlePeerLeft — retains the trust entry; issues no directive. */
void ca_security_layer_service_handle_peer_left(
    ca_security_layer_service_t *svc, const char *node_id);

/* SubscribeToDirectives — delegates to the publisher. */
ca_directive_subscription_t *ca_security_layer_service_subscribe_directives(
    ca_security_layer_service_t *svc, ca_peer_directive_consumer_fn consumer,
    void *user);

/* Dispose a directive subscription (mirrors the IDisposable returned by
 * SubscribeToDirectives). Delegates to the publisher; idempotent. */
void ca_security_layer_service_unsubscribe_directives(
    ca_security_layer_service_t *svc, ca_directive_subscription_t *sub);

/* GetPostureAsync — snapshot of overall posture. */
void ca_security_layer_service_get_posture(
    const ca_security_layer_service_t *svc, ca_peer_security_posture_t *out);

/* Explicit passive-recovery tick (drives the C# background recovery loop). */
void ca_security_layer_service_recover_tick(ca_security_layer_service_t *svc,
                                            int64_t elapsed_ms);

/* ===========================================================================
 * PeerIntelligenceService — IPeerIntelligence (AetherIntelligenceService.cs)
 * =========================================================================== */

typedef struct ca_peer_intelligence_service ca_peer_intelligence_service_t;

/* Borrows registry + options. NULL on OOM. */
ca_peer_intelligence_service_t *ca_peer_intelligence_service_create(
    ca_node_trust_registry_t *registry, const ca_security_options_t *options);
void ca_peer_intelligence_service_destroy(ca_peer_intelligence_service_t *svc);

/* GetNetworkHealthAsync. Writes an owning report (destroy it). Returns 0 / -1. */
int ca_peer_intelligence_service_get_network_health(
    const ca_peer_intelligence_service_t *svc,
    ca_peer_network_health_report_t *out);

/* AssessThreatAsync for node_id at now_ms. Writes an owning assessment. */
int ca_peer_intelligence_service_assess_threat(
    const ca_peer_intelligence_service_t *svc, const char *node_id,
    int64_t now_ms, ca_peer_threat_assessment_t *out);

/* GetRoutingAdviceAsync toward destination_node_id. Writes owning advice. */
int ca_peer_intelligence_service_get_routing_advice(
    const ca_peer_intelligence_service_t *svc, const char *destination_node_id,
    ca_peer_routing_advice_t *out);

/* StreamTrustScoresAsync — opens a reader over the registry update stream. */
ca_trust_update_reader_t *ca_peer_intelligence_service_stream_trust_scores(
    ca_peer_intelligence_service_t *svc);

/* ===========================================================================
 * IPeerSecurityEventFeed — transport adapter contract (vtable form).
 *
 * A transport implements this to register an event source. The security layer
 * calls `start(self, handler, handler_user)` once; the feed pumps events by
 * invoking handler(handler_user, event) for each. Deterministic feeds return
 * after draining; the C port has no cancellation token (single-threaded).
 * =========================================================================== */

typedef void (*ca_peer_event_handler_fn)(void *user,
                                         const ca_peer_security_event_t *e);

typedef struct {
    void       *self;
    const char *transport_id; /* stable id (e.g. "wifi", "ble", "aether") */
    void (*start)(void *self, ca_peer_event_handler_fn handler, void *handler_user);
} ca_peer_security_event_feed_t;

/* Convenience: pump a feed straight into a security layer service. */
void ca_peer_security_event_feed_pump_into_layer(
    const ca_peer_security_event_feed_t *feed,
    ca_security_layer_service_t *layer);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_PEER_SECURITY_H */
