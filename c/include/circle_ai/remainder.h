#ifndef CIRCLE_AI_REMAINDER_H
#define CIRCLE_AI_REMAINDER_H

/*
 * remainder.h - the last of it (C11).
 *
 * The tail: a personal LoRA, the mesh-gated session, skill packs, node trust,
 * the ambient monitor, image codecs, and the handful of adapters and small
 * types that had no other home.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "circle_ai/domain_boards.h"

#ifdef __cplusplus
extern "C" {
#endif

/* -- the personal LoRA ---------------------------------------------------- */

typedef struct {
    char *adapter_id;
    char *base_model_id;
    int rank;
    int64_t trained_at_unix;
    int64_t bytes;
    /* How many examples it was fitted on. An adapter trained on eleven examples
     * and one trained on four thousand are different things, and only one of
     * them should be allowed to change how an assistant writes. */
    int example_count;
} ca_lo_ra_adapter_state_t;

void ca_lo_ra_adapter_state_free(ca_lo_ra_adapter_state_t *state);

typedef struct {
    char *adapter_id;
    double final_loss;
    int epochs;
    int64_t duration_ms;
    char *note;
} ca_lo_ra_training_summary_t;

void ca_lo_ra_training_summary_free(ca_lo_ra_training_summary_t *summary);

typedef struct ca_personal_lo_ra {
    void *state;
    const ca_lo_ra_adapter_state_t *(*current)(void *state);
    /*
     * Training happens ON DEVICE or not at all.
     *
     * The examples are somebody's own writing. Shipping them somewhere to fit
     * an adapter is the one thing a personal model must never do - and it is
     * the thing that would be easiest to do, because it would work better.
     */
    ca_lo_ra_training_summary_t *(*train)(void *state, const char **examples,
                                          size_t count);
    void (*free_fn)(void *state);
} ca_personal_lo_ra_t;

void ca_personal_lo_ra_free(ca_personal_lo_ra_t *lora);

ca_personal_lo_ra_t *ca_personal_lo_ra_new(void);
ca_personal_lo_ra_t *ca_null_personal_lo_ra_new(void);

/* -- mesh-gated sessions -------------------------------------------------- */

/*
 * Why a companion session was refused over the mesh.
 *
 * C has no exceptions, so MeshSecurityBlockedException is this code. It is
 * deliberately specific: "blocked" without a reason cannot be shown to the
 * person whose request was refused, and a mesh feature nobody can explain is a
 * mesh feature people switch off.
 */
typedef enum {
    CA_MESH_SECURITY_OK = 0,
    CA_MESH_SECURITY_BLOCKED_NOT_MUTUALLY_ADDED,
    CA_MESH_SECURITY_BLOCKED_UNSEALED_LINK,
    CA_MESH_SECURITY_BLOCKED_NO_CONSENT,
    CA_MESH_SECURITY_BLOCKED_UNTRUSTED_NODE,
    CA_MESH_SECURITY_BLOCKED_RATE_LIMITED
} ca_mesh_security_blocked_t;

const char *ca_mesh_security_blocked_message(ca_mesh_security_blocked_t reason);

typedef struct ca_mesh_gated_companion_session ca_mesh_gated_companion_session_t;

/*
 * A companion session reachable from the mesh, behind the gate.
 *
 * Wraps an existing session and refuses before anything reaches it. The order
 * matters: checking after the model has already seen the prompt means the
 * refusal protects nothing.
 */
ca_mesh_gated_companion_session_t *ca_mesh_gated_companion_session_new(
    void *inner_session, void *security_gate);

void ca_mesh_gated_companion_session_free(ca_mesh_gated_companion_session_t *session);

ca_mesh_security_blocked_t ca_mesh_gated_companion_session_send(
    ca_mesh_gated_companion_session_t *session, const char *peer_id,
    const char *message, char **out_response);

/* -- node trust ----------------------------------------------------------- */

typedef struct {
    char *node_id;
    char *aether_tag;
    /* Both sides added each other. The only state in which anything is
     * exchanged - trust that one side can assert alone is not trust. */
    bool mutually_added;
    int64_t first_seen_unix;
    int64_t last_seen_unix;
    /* Set when a node's key changed. A key change on a known node is either a
     * reinstall or an impersonation, and it must be surfaced rather than
     * silently accepted. */
    bool key_changed;
    char *note;
} ca_node_trust_entry_t;

void ca_node_trust_entry_free(ca_node_trust_entry_t *entry);

/* -- redaction ------------------------------------------------------------ */

/*
 * Writes evidence with the sensitive parts already removed.
 *
 * REDACTION HAPPENS ON THE WAY IN, at the writer, not at the reader. A log that
 * stores the real value and hides it at display time is a log that leaks the
 * moment somebody opens the file with anything else - and "anything else"
 * includes the next person to write a debugging script.
 */
typedef struct ca_redacted_evidence_writer ca_redacted_evidence_writer_t;

ca_redacted_evidence_writer_t *ca_redacted_evidence_json_converter_new(void);
void ca_redacted_evidence_writer_free(ca_redacted_evidence_writer_t *writer);

/* Caller frees. Fields named in the redaction set never appear in the output at
 * all - not as a mask, not as a hash, not as a length. */
char *ca_redacted_evidence_write(ca_redacted_evidence_writer_t *writer,
                                 const char *evidence_json);

bool ca_redacted_evidence_add_redacted_field(ca_redacted_evidence_writer_t *writer,
                                             const char *field_name);

/* -- skill packs ---------------------------------------------------------- */

typedef struct {
    char *pack_id;
    char *name;
    char *version;
    char *publisher;
    char **skill_ids;
    size_t skill_count;
    char *sha256;
    /* What the pack needs to be able to do. Declared UP FRONT so somebody can
     * decide before installing, rather than discovering it when a skill reaches
     * for something. */
    char **required_capabilities;
    size_t required_capability_count;
} ca_skill_pack_manifest_t;

void ca_skill_pack_manifest_free(ca_skill_pack_manifest_t *manifest);

typedef struct ca_skill_pack_loader ca_skill_pack_loader_t;

/* Reads the manifest, verifies the hash, and loads nothing that fails either.
 * A skill pack is instructions an assistant will follow; one that arrived
 * damaged is one whose instructions nobody wrote. */
ca_skill_pack_loader_t *ca_skill_pack_loader_new(void);
void ca_skill_pack_loader_free(ca_skill_pack_loader_t *loader);

ca_skill_pack_manifest_t *ca_skill_pack_loader_load(ca_skill_pack_loader_t *loader,
                                                    const char *pack_path,
                                                    char **out_error);

/* The packs this project ships or knows about, by id. A closed list: a pack
 * from anywhere else is installed deliberately, by somebody, from a file. */
size_t ca_known_skill_packs_count(void);
const char *ca_known_skill_packs_at(size_t index);

/* -- federation participants ---------------------------------------------- */

typedef enum {
    CA_DELTA_DISPATCH_ACCEPTED = 0,
    CA_DELTA_DISPATCH_DEFERRED,
    CA_DELTA_DISPATCH_REJECTED_TOO_FEW_PARTICIPANTS,
    CA_DELTA_DISPATCH_REJECTED_STALE_ROUND,
    CA_DELTA_DISPATCH_FAILED
} ca_delta_dispatch_outcome_t;

const char *ca_delta_dispatch_outcome_name(ca_delta_dispatch_outcome_t outcome);

typedef struct ca_federation_participant {
    void *state;
    const char *(*participant_id)(void *state);
    /* How much this participant's update should count - normally its example
     * count. Reported by the participant and BOUNDED by the aggregator, because
     * a weight nobody checks is a weight worth lying about. */
    double (*weight)(void *state);
    bool (*local_update)(void *state, size_t dims, float *out);
    void (*free_fn)(void *state);
} ca_federation_participant_t;

void ca_federation_participant_free(ca_federation_participant_t *participant);

/* -- AetherNet bridges ---------------------------------------------------- */

typedef struct ca_aether_net_inbound_directive_bridge ca_aether_net_inbound_directive_bridge_t;

/*
 * Directives arriving from the mesh, on their way to a decision.
 *
 * DATA, NOT INSTRUCTIONS. Nothing here executes a directive because it arrived;
 * it is surfaced to a person or handed to a policy that decides. A mesh peer
 * that could instruct this device is a mesh peer that owns it.
 */
ca_aether_net_inbound_directive_bridge_t *ca_aether_net_inbound_directive_bridge_new(
    void *node);

void ca_aether_net_inbound_directive_bridge_free(
    ca_aether_net_inbound_directive_bridge_t *bridge);

size_t ca_aether_net_inbound_directive_bridge_pending(
    const ca_aether_net_inbound_directive_bridge_t *bridge);

typedef struct ca_aether_net_telemetry_adapter ca_aether_net_telemetry_adapter_t;

/* Counts and durations over the mesh - never content, never who said what. */
ca_aether_net_telemetry_adapter_t *ca_aether_net_telemetry_adapter_new(void *node);
void ca_aether_net_telemetry_adapter_free(ca_aether_net_telemetry_adapter_t *adapter);

/* -- small types that had no other home ----------------------------------- */

typedef struct {
    char *capability_id;
    char *name;
    char *description;
} ca_agent_capability_t;

void ca_agent_capability_free(ca_agent_capability_t *capability);

typedef struct {
    char *decision_id;
    char *summary;
    char *rationale;
    double confidence;
    int64_t at_unix;
    /* Whether a person has to approve before it takes effect. TRUE for anything
     * that spends money or contacts somebody - the two things an autonomous
     * business decision must never do on its own. */
    bool requires_approval;
} ca_autonomous_decision_t;

void ca_autonomous_decision_free(ca_autonomous_decision_t *decision);

typedef enum {
    CA_PRESENCE_OFFLINE = 0,
    CA_PRESENCE_ONLINE,
    CA_PRESENCE_AWAY,
    CA_PRESENCE_BUSY,
    /* Present but not to be interrupted. Distinct from BUSY, which is about
     * availability; this is about attention. */
    CA_PRESENCE_FOCUSED
} ca_presence_state_t;

const char *ca_presence_state_name(ca_presence_state_t state);

typedef struct {
    char *label;
    double intensity;
    double confidence;
} ca_face_expression_classification_t;

void ca_face_expression_classification_free(ca_face_expression_classification_t *c);

/* Facial metrics to a label. Reported with confidence and never as a fact about
 * how somebody FEELS - an expression is a face, and the inference from one to
 * the other is exactly where this kind of feature does harm. */
bool ca_face_expression_classify(const double *metrics, size_t count,
                                 ca_face_expression_classification_t *out);

/* Heart rate variability and skin conductance into the same arousal/valence
 * frame the face and the text produce, so three sources can disagree visibly. */
bool ca_biosignal_affect_mapper_map(double heart_rate_variability,
                                    double skin_conductance,
                                    double *out_arousal, double *out_valence);

typedef struct ca_multiplayer_peer_identity {
    void *state;
    /* Borrowed. A per-SESSION identity, not the device's AetherTag: a game
     * lobby has no business learning the identity a person's other apps use. */
    const char *(*session_peer_id)(void *state);
    const char *(*display_name)(void *state);
    void (*free_fn)(void *state);
} ca_multiplayer_peer_identity_t;

void ca_multiplayer_peer_identity_free(ca_multiplayer_peer_identity_t *identity);

/* The domain keys sync uses, in one place. Two components spelling the same
 * domain differently is a sync that silently never converges. */
size_t ca_sync_domain_keys_count(void);
const char *ca_sync_domain_keys_at(size_t index);
const char *ca_sync_domain_keys_conversations(void);
const char *ca_sync_domain_keys_persona(void);
const char *ca_sync_domain_keys_adapters(void);

/* -- runtime registry ----------------------------------------------------- */

typedef struct ca_native_runtime_registry ca_native_runtime_registry_t;

/* What is installed, per architecture and OS. Keyed on both, because a runtime
 * built for arm64 Linux does not run on arm64 Android and a registry that keys
 * on architecture alone hands over the wrong one. */
ca_native_runtime_registry_t *ca_native_runtime_registry_new(void);
void ca_native_runtime_registry_free(ca_native_runtime_registry_t *registry);

bool ca_native_runtime_registry_add(ca_native_runtime_registry_t *registry,
                                    const char *runtime_id, const char *install_path,
                                    int architecture, int operating_system);

const char *ca_native_runtime_registry_resolve(
    const ca_native_runtime_registry_t *registry, const char *runtime_id,
    int architecture, int operating_system);

/* -- image codecs --------------------------------------------------------- */

/*
 * PNG and BMP, encoded and decoded here rather than by a library.
 *
 * TWO THINGS THAT ARE EASY TO GET WRONG AND SILENT WHEN WRONG:
 *
 * A DEFLATE back-reference is copied ONE BYTE AT A TIME, because a run may
 * overlap its own output - a length of 10 at a distance of 1 repeats one byte
 * ten times. A memcpy reads bytes that have not been written yet, and the image
 * decodes to something that looks almost right.
 *
 * Block header fields are LSB-first and Huffman codes are MSB-first, in the same
 * stream. Reading both the same way produces a decoder that works on some
 * images and not others, which reads as corrupt input rather than a bug here.
 */
uint8_t *ca_image_codecs_encode_png(const uint8_t *rgba, int width, int height,
                                    size_t *out_len);

bool ca_image_codecs_decode_png(const uint8_t *png, size_t len, uint8_t **out_rgba,
                                int *out_width, int *out_height);

uint8_t *ca_image_codecs_encode_bmp(const uint8_t *rgba, int width, int height,
                                    size_t *out_len);

/* The Paeth predictor, exposed because it is the one filter people get subtly
 * wrong: it picks the NEAREST of the three candidates to the estimate, and on a
 * tie it prefers left, then above, then upper-left. A different tie-break
 * produces an image that is correct almost everywhere. */
uint8_t ca_image_codecs_paeth(uint8_t left, uint8_t above, uint8_t upper_left);

/* -- HNSW ----------------------------------------------------------------- */

typedef struct ca_hnsw_embedding_store ca_hnsw_embedding_store_t;

/*
 * Approximate nearest neighbours over a navigable small-world graph.
 *
 * APPROXIMATE, and it says so: recall is a function of `ef_search`, and a
 * caller that needs exact answers should scan. The default is tuned for a
 * phone - a graph small enough to hold and fast enough to query on every turn,
 * which matters more here than the last percent of recall.
 */
ca_hnsw_embedding_store_t *ca_hnsw_embedding_store_new(size_t dims, int m,
                                                       int ef_construction);

void ca_hnsw_embedding_store_free(ca_hnsw_embedding_store_t *store);

bool ca_hnsw_embedding_store_add(ca_hnsw_embedding_store_t *store, const char *id,
                                 const float *vector);

/* Writes up to `k` ids into `out_ids`. Returns how many were found. */
size_t ca_hnsw_embedding_store_search(const ca_hnsw_embedding_store_t *store,
                                      const float *query, size_t k, int ef_search,
                                      const char **out_ids, float *out_distances);

size_t ca_hnsw_embedding_store_count(const ca_hnsw_embedding_store_t *store);

/* -- background and monitors ---------------------------------------------- */

typedef struct ca_proactive_scheduler_background_service ca_proactive_scheduler_background_service_t;

/* Runs proactive tasks on a schedule. Starts DISABLED and takes a runner that
 * defaults to doing nothing - two separate switches, because background work
 * that speaks to somebody is the last thing that should start by accident. */
ca_proactive_scheduler_background_service_t *ca_proactive_scheduler_background_service_new(
    void *runner);

void ca_proactive_scheduler_background_service_free(
    ca_proactive_scheduler_background_service_t *service);

bool ca_proactive_scheduler_background_service_start(
    ca_proactive_scheduler_background_service_t *service);

void ca_proactive_scheduler_background_service_stop(
    ca_proactive_scheduler_background_service_t *service);

typedef struct ca_ambient_companion_monitor ca_ambient_companion_monitor_t;

/*
 * Watches the room and decides whether anything is worth saying.
 *
 * Says nothing by default and needs an explicit reason to speak. An ambient
 * monitor that errs towards speaking is an assistant that talks to an empty
 * room, and the second time it happens people turn it off for good.
 */
ca_ambient_companion_monitor_t *ca_ambient_companion_monitor_new(void);
void ca_ambient_companion_monitor_free(ca_ambient_companion_monitor_t *monitor);

bool ca_ambient_companion_monitor_should_speak(ca_ambient_companion_monitor_t *monitor,
                                               int64_t now_unix, char **out_reason);

typedef struct ca_timer_game_loop ca_timer_game_loop_t;

/* A fixed-timestep loop. Fixed because a variable step makes physics
 * non-deterministic, and a game that plays differently on a fast phone is one
 * whose leaderboard means nothing. */
ca_timer_game_loop_t *ca_timer_game_loop_new(int ticks_per_second);
void ca_timer_game_loop_free(ca_timer_game_loop_t *loop);

bool ca_timer_game_loop_advance(ca_timer_game_loop_t *loop, int64_t now_unix_ms,
                                int *out_ticks);

typedef struct ca_static_site_builder ca_static_site_builder_t;

/* Renders a visualisation as files that open with no server. The point is that
 * the output outlives whatever produced it - a dashboard that needs a running
 * process is a dashboard nobody can send to anybody. */
ca_static_site_builder_t *ca_static_site_builder_new(const char *output_directory);
void ca_static_site_builder_free(ca_static_site_builder_t *builder);

bool ca_static_site_builder_add_page(ca_static_site_builder_t *builder,
                                     const char *path, const char *html);

bool ca_static_site_builder_write(ca_static_site_builder_t *builder,
                                  char **out_error);

/* -- the last adapters ---------------------------------------------------- */

/* Safety's snippet carries the escalation rule: some things are not an
 * assistant's to handle, and it has to say which and to whom. */
ca_domain_companion_adapter_t *ca_safety_companion_adapter_new(void *inner_session);

/* Child safety is stricter again - it refuses rather than softens, because a
 * softened answer to a child is still an answer. */
ca_domain_companion_adapter_t *ca_safety_child_companion_adapter_new(void *inner_session);

ca_domain_companion_adapter_t *ca_social_companion_adapter_new(void *inner_session);
ca_domain_companion_adapter_t *ca_tourism_companion_adapter_new(void *inner_session);

/* The internal tooling. Named for what it is so it is obvious in a build where
 * it should not be. */
char *ca_the_geek_network_tools_describe(void);

struct ca_image_generator;
struct ca_image_generator *ca_stability_image_generator_new(const void *options,
                                                            void *http);

/* -- the last four -------------------------------------------------------- */

/*
 * Whether a cast failure means the television ANSWERED AND REFUSED.
 *
 * The C# separates CastControlException from CastException, and the distinction
 * is the useful one: a control failure means the renderer is there and said no
 * — usually an unquoted SOAPACTION or a URI it will not accept — while the rest
 * mean it was never reached. Retrying helps one and not the other.
 */
bool ca_cast_error_is_control(int cast_error);
const char *ca_cast_control_error_message(int cast_error);

/*
 * One participant's contribution to a federated round.
 *
 * A DELTA, never weights. Sending whole weights would mean every round carries
 * the entire model both ways, which no phone connection here can afford — and
 * a delta is also the smaller thing to have to protect.
 */
typedef struct {
    char *participant_id;
    char *round_id;
    char *model_id;
    float *values;
    size_t dims;
    /* How much this should count — normally the example count. Bounded by the
     * aggregator, because a weight nobody checks is a weight worth lying
     * about. */
    double weight;
    int64_t produced_unix;
} ca_model_delta_t;

void ca_model_delta_free(ca_model_delta_t *delta);

/*
 * The assistant, offered to the mesh as a provider.
 *
 * One device answering for another that cannot. Refuses unless both sides added
 * each other and the link is sealed — the same bar as any other offload,
 * because this is the same act seen from the other end.
 */
typedef struct ca_aether_net_ai_provider ca_aether_net_ai_provider_t;

ca_aether_net_ai_provider_t *ca_aether_net_ai_provider_new(void *node,
                                                           void *generator);

void ca_aether_net_ai_provider_free(ca_aether_net_ai_provider_t *provider);

bool ca_aether_net_ai_provider_serve(ca_aether_net_ai_provider_t *provider,
                                     const char *peer_id, const char *prompt,
                                     char **out_response);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_REMAINDER_H */
