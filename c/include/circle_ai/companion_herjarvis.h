#ifndef CIRCLE_AI_COMPANION_HERJARVIS_H
#define CIRCLE_AI_COMPANION_HERJARVIS_H

/*
 * companion_herjarvis.h - CircleAI.Companion (C11).
 *
 * The twenty-four capabilities an assistant would need to be a companion rather
 * than a command line: staying present, fusing what it sees and hears, learning
 * continuously, holding a model of the world and of the person, pursuing a goal
 * over weeks, knowing who is speaking, saying how sure it is, guessing what
 * somebody else believes, acquiring a skill, thinking to itself, anticipating a
 * need, keeping a knowledge graph, acting on the physical world, talking to
 * other agents, tuning itself, delegating authority, writing code, and getting
 * better at all of it.
 *
 * EVERY ONE IS A SEAM WITH A REAL DEFAULT BEHIND IT. Not stubs: each has an
 * implementation that does the modest version of the thing honestly - a
 * frequency table where a world model would go, an adjacency list where a graph
 * database would go. The seam is what makes the ambitious version substitutable;
 * the default is what makes the seam provably the right shape.
 *
 * WHAT THIS FILE WILL NOT DO. Nothing here reaches a network by itself, and
 * nothing persists what it hears unless a host hands it somewhere to put it. A
 * companion that quietly accumulates is a recorder, and the difference between
 * the two is exactly this: who chose.
 *
 * In C an interface is a struct of function pointers and the I prefix goes.
 * Where the C# names its default for the mechanism - AdjacencyPersonalKnowledge-
 * Graph, EwaContinuousLearner - the C keeps that word, because it is the honest
 * description of what the default actually does.
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

/* -- 1. presence ---------------------------------------------------------- */

typedef struct ca_always_on_presence {
    void *state;
    bool (*is_present)(void *state);
    int64_t (*last_heartbeat_unix)(void *state);
    void (*free_fn)(void *state);
} ca_always_on_presence_t;

void ca_always_on_presence_free(ca_always_on_presence_t *presence);

/* A heartbeat, because presence is the absence of a gap. `interval_ms` is how
 * often one is expected; two missed in a row is absent. One missed is a
 * scheduler hiccup, and treating it as absence makes the companion appear to
 * come and go on a busy phone. */
ca_always_on_presence_t *ca_heartbeat_always_on_presence_new(int64_t interval_ms);
void ca_heartbeat_always_on_presence_beat(ca_always_on_presence_t *presence,
                                          int64_t at_unix_ms);

/* -- 2. perception -------------------------------------------------------- */

typedef struct {
    int64_t at_unix;
    /* Any of these may be NULL - that is the normal case, not degradation. */
    char *vision;
    char *audio;
    char *text;
    char **sensor_names;
    double *sensor_values;
    size_t sensor_count;
} ca_fused_percept_t;

void ca_fused_percept_free(ca_fused_percept_t *percept);

typedef struct ca_fused_perception {
    void *state;
    /* Latest fused percept, or NULL. Caller frees. */
    ca_fused_percept_t *(*latest)(void *state);
    void (*free_fn)(void *state);
} ca_fused_perception_t;

void ca_fused_perception_free(ca_fused_perception_t *perception);

/* Fuses from a channel of observations, newest wins per modality within a
 * window. Modalities arrive at wildly different rates - vision by the frame,
 * text by the sentence - and a fuser that waits for all three emits nothing. */
ca_fused_perception_t *ca_channel_fused_perception_new(int64_t window_ms);

/* -- 3. identity across devices ------------------------------------------- */

typedef struct ca_identity_sync {
    void *state;
    bool (*push)(void *state, const char *identity_json);
    /* Caller frees. */
    char *(*pull)(void *state);
    void (*free_fn)(void *state);
} ca_identity_sync_t;

void ca_identity_sync_free(ca_identity_sync_t *sync);

/* JSON on local disk. The default is deliberately the DUMBEST one that works:
 * identity sync is where a companion would otherwise grow a server, and a file
 * on the device keeps the question of who holds it answerable. */
ca_identity_sync_t *ca_json_identity_sync_new(const char *path);

/* -- 4. continuous learning ----------------------------------------------- */

typedef struct ca_continuous_learner {
    void *state;
    void (*observe)(void *state, const char *signal, double value);
    double (*estimate)(void *state, const char *signal);
    void (*free_fn)(void *state);
} ca_continuous_learner_t;

void ca_continuous_learner_free(ca_continuous_learner_t *learner);

/*
 * Exponentially weighted average per signal.
 *
 * EWA rather than a full history because a companion learns from a person who
 * CHANGES. An average over everything ever observed converges on who somebody
 * used to be and then defends that estimate against the evidence; a decaying
 * one forgets at a stated rate. `alpha` is that rate, and it being visible in
 * the constructor is the point.
 */
ca_continuous_learner_t *ca_ewa_continuous_learner_new(double alpha);

/* -- 5. world model ------------------------------------------------------- */

typedef struct {
    char *outcome;
    double probability;
    char **supporting_factors;
    size_t factor_count;
} ca_causal_prediction_t;

void ca_causal_prediction_free(ca_causal_prediction_t *prediction);

typedef struct ca_world_model {
    void *state;
    void (*record)(void *state, const char *event_name, const char *context_json);
    /* Caller frees. */
    ca_causal_prediction_t *(*predict)(void *state, const char *context_json);
    void (*free_fn)(void *state);
} ca_world_model_t;

void ca_world_model_free(ca_world_model_t *model);

/* Frequency counting. Not a causal model and it does not claim to be - the
 * supporting factors it lists are co-occurrences, which is why they are
 * returned for a person to judge rather than acted on. */
ca_world_model_t *ca_frequency_world_model_new(void);

/* -- 6. long-horizon goals ------------------------------------------------ */

typedef struct {
    char *id;
    char *description;
    int64_t deadline_unix;
    char *plan_json;
    double progress_fraction;
} ca_long_horizon_goal_t;

void ca_long_horizon_goal_free(ca_long_horizon_goal_t *goal);

typedef struct ca_goal_pursuer {
    void *state;
    bool (*adopt)(void *state, const ca_long_horizon_goal_t *goal);
    bool (*advance)(void *state, const char *goal_id, double fraction);
    ca_long_horizon_goal_t *(*list)(void *state, size_t *out_count);
    void (*free_fn)(void *state);
} ca_goal_pursuer_t;

void ca_goal_pursuer_free(ca_goal_pursuer_t *pursuer);
ca_goal_pursuer_t *ca_goal_pursuer_new(void);

/* -- 7. episodic memory --------------------------------------------------- */

typedef struct {
    char *id;
    int64_t at_unix;
    char *title;
    char *content_json;
} ca_episode_record_t;

void ca_episode_record_free(ca_episode_record_t *record);

typedef struct ca_episodic_memory {
    void *state;
    bool (*append)(void *state, const ca_episode_record_t *record);
    /* Heap array of *out_count, most relevant first. */
    ca_episode_record_t *(*recall)(void *state, const char *query, size_t top_k,
                                   size_t *out_count);
    void (*free_fn)(void *state);
} ca_episodic_memory_t;

void ca_episodic_memory_free(ca_episodic_memory_t *memory);

/* Term-frequency scoring. Enough to be useful and cheap enough to run on every
 * turn, which matters more here than ranking quality: recall that is too slow
 * to run gets run never. */
ca_episodic_memory_t *ca_tf_episodic_memory_new(void);

/* -- 8. who is speaking --------------------------------------------------- */

typedef struct ca_voice_identity {
    void *state;
    bool (*enrol)(void *state, const char *identity_id, const float *samples,
                  size_t count, int sample_rate_hz);
    /* Borrowed id, or NULL when it does not recognise the voice. NULL is the
     * SAFE answer and must stay easy to return: a companion that guesses which
     * household member is speaking will eventually read one person's messages
     * to another. */
    const char *(*identify)(void *state, const float *samples, size_t count,
                            int sample_rate_hz, double *out_confidence);
    void (*free_fn)(void *state);
} ca_voice_identity_t;

void ca_voice_identity_free(ca_voice_identity_t *identity);

/* Energy across mel bands. A weak identifier by design - it separates the
 * people in one house, and it cannot be mistaken for authentication. */
ca_voice_identity_t *ca_energy_band_voice_identity_new(void);

/* -- 9. saying how sure it is --------------------------------------------- */

typedef struct {
    double lower;
    double upper;
} ca_confidence_band_t;

typedef struct ca_calibrated_confidence {
    void *state;
    void (*observe)(void *state, double stated_confidence, bool was_correct);
    /* The band a stated confidence should really be read as. */
    ca_confidence_band_t (*calibrate)(void *state, double stated_confidence);
    void (*free_fn)(void *state);
} ca_calibrated_confidence_t;

void ca_calibrated_confidence_free(ca_calibrated_confidence_t *confidence);

/*
 * Calibration from what actually happened.
 *
 * A model's own confidence is a number it produces, not a measurement, and it
 * is consistently overconfident. Widening it against history is the difference
 * between "I am 90% sure" meaning something and it being a verbal tic.
 */
ca_calibrated_confidence_t *ca_historical_calibrated_confidence_new(void);

/* -- 10. theory of mind --------------------------------------------------- */

typedef struct {
    char *target_identifier;
    char *likely_belief_json;
    double confidence;
} ca_other_mind_estimate_t;

void ca_other_mind_estimate_free(ca_other_mind_estimate_t *estimate);

typedef struct ca_theory_of_mind {
    void *state;
    ca_other_mind_estimate_t *(*estimate)(void *state, const char *target_identifier);
    void (*note)(void *state, const char *target_identifier, const char *observation);
    void (*free_fn)(void *state);
} ca_theory_of_mind_t;

void ca_theory_of_mind_free(ca_theory_of_mind_t *theory);
ca_theory_of_mind_t *ca_belief_tracker_theory_of_mind_new(void);

/* -- 11. emotion ---------------------------------------------------------- */

typedef struct {
    char *label;
    double arousal;
    double valence;
} ca_emotion_frame_t;

void ca_emotion_frame_free(ca_emotion_frame_t *frame);

typedef struct ca_emotion_sensor {
    void *state;
    ca_emotion_frame_t *(*sense)(void *state, const char *text);
    void (*free_fn)(void *state);
} ca_emotion_sensor_t;

void ca_emotion_sensor_free(ca_emotion_sensor_t *sensor);

/* Keywords. Crude, and crude is the right default: an emotion reading that is
 * wrong in a confident-sounding way changes how the companion speaks to
 * somebody having a bad day. */
ca_emotion_sensor_t *ca_keyword_emotion_sensor_new(void);

/* Facial metrics into the same arousal/valence frame, so a face and a sentence
 * can disagree and the disagreement is visible. */
void ca_face_affect_mapper_apply(const double *facial_metrics, size_t metric_count,
                                 ca_emotion_frame_t *out_affect);

typedef struct ca_face_companion_bridge ca_face_companion_bridge_t;

/* Wires a face source into a companion session's affect. Kept as a bridge
 * rather than a dependency so a build with no camera links nothing. */
ca_face_companion_bridge_t *ca_face_companion_bridge_new(ca_emotion_sensor_t *sensor);
void ca_face_companion_bridge_free(ca_face_companion_bridge_t *bridge);

/* -- 12. acquiring a skill ------------------------------------------------ */

typedef struct {
    char *id;
    char *name;
    char *description_json;
} ca_acquired_skill_t;

void ca_acquired_skill_free(ca_acquired_skill_t *skill);

typedef struct ca_skill_acquisition {
    void *state;
    bool (*acquire)(void *state, const char *skill_id);
    ca_acquired_skill_t *(*list)(void *state, size_t *out_count);
    void (*free_fn)(void *state);
} ca_skill_acquisition_t;

void ca_skill_acquisition_free(ca_skill_acquisition_t *acquisition);

/* Acquires from a local demo store. Named for what it is: no network, no
 * marketplace, and no pretence that skills arrive from anywhere yet. */
ca_skill_acquisition_t *ca_demo_store_skill_acquisition_new(void);

/* -- 13. thinking to itself ----------------------------------------------- */

typedef struct {
    char *thought;
    int64_t at_unix;
} ca_self_reflection_t;

void ca_self_reflection_free(ca_self_reflection_t *reflection);

typedef struct ca_inner_monologue {
    void *state;
    ca_self_reflection_t *(*reflect)(void *state, const char *situation);
    void (*free_fn)(void *state);
} ca_inner_monologue_t;

void ca_inner_monologue_free(ca_inner_monologue_t *monologue);
ca_inner_monologue_t *ca_template_inner_monologue_new(void);

typedef struct ca_reasoning_loop_inner_monologue ca_reasoning_loop_inner_monologue_t;

/*
 * The monologue wired into the reasoning loop: think, act, observe, think again.
 *
 * `max_steps` is not a tuning knob, it is a TERMINATION GUARANTEE. A loop that
 * reflects on its own reflection has no natural stopping point, and the failure
 * is not a crash - it is a companion that stays busy and never answers.
 */
ca_reasoning_loop_inner_monologue_t *ca_reasoning_loop_inner_monologue_new(
    ca_inner_monologue_t *monologue, int max_steps);

void ca_reasoning_loop_inner_monologue_free(ca_reasoning_loop_inner_monologue_t *loop);

/* -- 14. anticipation ----------------------------------------------------- */

typedef struct {
    char *description;
    int64_t expected_by_unix;
    double probability;
} ca_anticipated_need_t;

void ca_anticipated_need_free(ca_anticipated_need_t *need);

typedef struct ca_predictive_engine {
    void *state;
    ca_anticipated_need_t *(*anticipate)(void *state, size_t *out_count);
    void (*observe)(void *state, const char *event_name, int64_t at_unix);
    void (*free_fn)(void *state);
} ca_predictive_engine_t;

void ca_predictive_engine_free(ca_predictive_engine_t *engine);

/* Histogram over time-of-day and weekday. Deliberately legible: somebody must
 * be able to see WHY the companion expected something, or an anticipation is
 * indistinguishable from surveillance. */
ca_predictive_engine_t *ca_histogram_predictive_engine_new(void);

/* Something the companion decided to raise on its own. Every one carries the
 * reason and the trigger, because unprompted speech is the thing that most
 * needs to be accountable. */
typedef struct {
    char *id;
    char *summary;
    char *reason;
    char *trigger;
    int64_t at_unix;
    double confidence;
} ca_companion_proactive_event_t;

void ca_companion_proactive_event_free(ca_companion_proactive_event_t *event);

/* -- 15. the personal knowledge graph ------------------------------------- */

typedef struct {
    char *id;
    char *kind;
    char *name;
    char **property_keys;
    char **property_values;
    size_t property_count;
} ca_knowledge_node_t;

void ca_knowledge_node_free(ca_knowledge_node_t *node);

typedef struct {
    char *from_id;
    char *to_id;
    char *relation;
} ca_knowledge_relation_t;

void ca_knowledge_relation_free(ca_knowledge_relation_t *relation);

typedef struct ca_personal_knowledge_graph {
    void *state;
    bool (*upsert_node)(void *state, const ca_knowledge_node_t *node);
    bool (*relate)(void *state, const ca_knowledge_relation_t *relation);
    ca_knowledge_node_t *(*neighbours)(void *state, const char *node_id,
                                       size_t *out_count);
    size_t (*node_count)(void *state);
    void (*free_fn)(void *state);
} ca_personal_knowledge_graph_t;

void ca_personal_knowledge_graph_free(ca_personal_knowledge_graph_t *graph);

/* Adjacency lists in memory. A personal graph is thousands of nodes, not
 * millions - the graph database this looks like it wants would cost more to
 * operate than the whole companion. */
ca_personal_knowledge_graph_t *ca_adjacency_personal_knowledge_graph_new(void);

typedef struct ca_knowledge_graph_extractor {
    void *state;
    /* Nodes and relations found in one turn. Either array may be empty; most
     * turns contain no durable fact at all, and an extractor that always finds
     * something fills the graph with noise. */
    bool (*extract)(void *state, const char *text,
                    ca_knowledge_node_t **out_nodes, size_t *out_node_count,
                    ca_knowledge_relation_t **out_relations, size_t *out_relation_count);
    void (*free_fn)(void *state);
} ca_knowledge_graph_extractor_t;

void ca_knowledge_graph_extractor_free(ca_knowledge_graph_extractor_t *extractor);

/* Patterns and proper nouns. No model, so it runs on every turn on a phone. */
ca_knowledge_graph_extractor_t *ca_heuristic_knowledge_graph_extractor_new(void);

/* Asks a generator. Better recall and materially slower - which is why it is
 * the one the encoder runs OFF the hot path. */
ca_knowledge_graph_extractor_t *ca_llm_knowledge_graph_extractor_new(void *generator);

typedef struct ca_companion_memory_encoder ca_companion_memory_encoder_t;

/*
 * Turn to knowledge graph, on a background queue.
 *
 * Non-blocking on purpose and the reason is the whole design: extraction is
 * slower than a reply, so doing it inline adds its latency to every single
 * turn. The companion answers first and remembers afterwards.
 *
 * The first error encountered while draining is KEPT rather than logged and
 * dropped - a background writer that fails silently produces a companion that
 * forgets, with nothing anywhere saying so.
 */
ca_companion_memory_encoder_t *ca_companion_memory_encoder_new(
    ca_knowledge_graph_extractor_t *extractor,
    ca_personal_knowledge_graph_t *graph, size_t queue_capacity);

void ca_companion_memory_encoder_free(ca_companion_memory_encoder_t *encoder);

/* Returns immediately. False when the queue is full, which is a real answer:
 * dropping a turn is better than blocking the reply. */
bool ca_companion_memory_encoder_enqueue(ca_companion_memory_encoder_t *encoder,
                                         const char *turn_text);

/* Stops accepting work and drains. */
void ca_companion_memory_encoder_drain(ca_companion_memory_encoder_t *encoder);

/* Borrowed; NULL when nothing has failed. */
const char *ca_companion_memory_encoder_first_error(
    const ca_companion_memory_encoder_t *encoder);

/* A HippoRAG-style store on SQLite: passages plus the graph over them, so
 * recall can walk from a hit to what it connects to. */
typedef struct ca_hippo_rag_store ca_hippo_rag_store_t;

ca_hippo_rag_store_t *ca_sqlite_hippo_rag_store_open(const char *path);
void ca_hippo_rag_store_close(ca_hippo_rag_store_t *store);

bool ca_hippo_rag_store_add(ca_hippo_rag_store_t *store, const char *passage_id,
                            const char *text);

char **ca_hippo_rag_store_search(ca_hippo_rag_store_t *store, const char *query,
                                 size_t top_k, size_t *out_count);

/* -- 16. live world knowledge --------------------------------------------- */

typedef struct {
    char *topic;
    char *summary_json;
    int64_t at_unix;
} ca_world_fact_t;

void ca_world_fact_free(ca_world_fact_t *fact);

typedef struct ca_live_world_knowledge {
    void *state;
    /* NULL when the topic is not tracked. NOT a fetch: this returns what is
     * already known. A companion that silently reaches the internet to answer
     * a question has told somebody's network what its owner asked. */
    ca_world_fact_t *(*latest)(void *state, const char *topic);
    bool (*record)(void *state, const ca_world_fact_t *fact);
    void (*free_fn)(void *state);
} ca_live_world_knowledge_t;

void ca_live_world_knowledge_free(ca_live_world_knowledge_t *knowledge);
ca_live_world_knowledge_t *ca_topic_live_world_knowledge_new(void);

/* -- 17. bio signals ------------------------------------------------------ */

typedef struct {
    char *kind;
    double value;
    int64_t at_unix;
} ca_bio_signal_t;

void ca_bio_signal_free(ca_bio_signal_t *signal);

typedef struct ca_bio_signal_stream {
    void *state;
    bool (*push)(void *state, const ca_bio_signal_t *signal);
    ca_bio_signal_t *(*recent)(void *state, const char *kind, size_t *out_count);
    void (*free_fn)(void *state);
} ca_bio_signal_stream_t;

void ca_bio_signal_stream_free(ca_bio_signal_stream_t *stream);
ca_bio_signal_stream_t *ca_channel_bio_signal_stream_new(size_t capacity);

/* -- 18. acting on the physical world ------------------------------------- */

typedef struct {
    char *device_id;
    char *action;
    char **arg_keys;
    char **arg_values;
    size_t arg_count;
} ca_physical_command_t;

void ca_physical_command_free(ca_physical_command_t *command);

typedef struct {
    bool succeeded;
    char *error;
} ca_physical_command_result_t;

void ca_physical_command_result_free(ca_physical_command_result_t *result);

typedef struct ca_physical_actuator {
    void *state;
    ca_physical_command_result_t *(*execute)(void *state,
                                             const ca_physical_command_t *command);
    void (*free_fn)(void *state);
} ca_physical_actuator_t;

void ca_physical_actuator_free(ca_physical_actuator_t *actuator);

/*
 * Dispatches only to devices explicitly registered.
 *
 * A REGISTRY AND NOT A DISCOVERY MECHANISM, deliberately. This is the one
 * capability here that changes something in a room somebody is standing in, and
 * the set of things it can touch should be a list a person wrote, not whatever
 * answered a broadcast.
 */
ca_physical_actuator_t *ca_registry_physical_actuator_new(void);

bool ca_registry_physical_actuator_register(ca_physical_actuator_t *actuator,
                                            const char *device_id,
                                            ca_physical_command_result_t *(*handler)(
                                                void *handler_state,
                                                const ca_physical_command_t *command),
                                            void *handler_state);

/* -- 19. talking to other agents ------------------------------------------ */

typedef struct {
    char *from_agent_id;
    char *to_agent_id;
    char *payload;
    int64_t at_unix;
} ca_agent_to_agent_message_t;

void ca_agent_to_agent_message_free(ca_agent_to_agent_message_t *message);

typedef struct ca_agent_peer_network {
    void *state;
    bool (*send)(void *state, const ca_agent_to_agent_message_t *message);
    ca_agent_to_agent_message_t *(*inbox)(void *state, const char *agent_id,
                                          size_t *out_count);
    void (*free_fn)(void *state);
} ca_agent_peer_network_t;

void ca_agent_peer_network_free(ca_agent_peer_network_t *network);

/* Mailboxes, delivered locally. No transport: what one agent says to another is
 * a message somebody's device sends, and the choice of how it travels belongs
 * to the mesh, not here. */
ca_agent_peer_network_t *ca_mailbox_agent_peer_network_new(void);

/* -- 20. federated fine tuning -------------------------------------------- */

typedef struct {
    char *job_id;
    double progress;
    char *error;
} ca_fine_tune_job_status_t;

void ca_fine_tune_job_status_free(ca_fine_tune_job_status_t *status);

typedef struct ca_federated_fine_tuner {
    void *state;
    /* Caller frees. */
    char *(*submit)(void *state, const char *dataset_ref);
    ca_fine_tune_job_status_t *(*status)(void *state, const char *job_id);
    void (*free_fn)(void *state);
} ca_federated_fine_tuner_t;

void ca_federated_fine_tuner_free(ca_federated_fine_tuner_t *tuner);

/* In memory, and it trains nothing. The seam is real; what fills it is somebody
 * else's model and somebody else's data policy, and this codebase sources
 * models rather than training them. */
ca_federated_fine_tuner_t *ca_federated_fine_tuner_new(void);

/* -- 21. first-token budget ----------------------------------------------- */

typedef struct {
    int target_ms;
    int current_p50_ms;
} ca_first_token_budget_t;

typedef struct ca_first_token_optimizer {
    void *state;
    ca_first_token_budget_t (*current)(void *state);
    void (*observe)(void *state, int first_token_ms);
    void (*free_fn)(void *state);
} ca_first_token_optimizer_t;

void ca_first_token_optimizer_free(ca_first_token_optimizer_t *optimizer);

/* A sliding p50, not a mean. The median is what a person experiences; the mean
 * is dragged by the one cold start and reports a system nobody is using. */
ca_first_token_optimizer_t *ca_sliding_p50_first_token_optimizer_new(int target_ms,
                                                                     size_t window);

/* -- 22. delegating authority --------------------------------------------- */

typedef struct {
    char *issuer;
    char *subject_id;
    char *scope;
    int64_t expires_at_unix;
    char *signature;
} ca_delegation_credential_t;

void ca_delegation_credential_free(ca_delegation_credential_t *credential);

typedef struct ca_crypto_delegation {
    void *state;
    ca_delegation_credential_t *(*issue)(void *state, const char *subject_id,
                                         const char *scope, int64_t lifetime_seconds);
    bool (*verify)(void *state, const ca_delegation_credential_t *credential);
    void (*free_fn)(void *state);
} ca_crypto_delegation_t;

void ca_crypto_delegation_free(ca_crypto_delegation_t *delegation);

/*
 * ECDSA over the credential's fields.
 *
 * Every credential is SCOPED and EXPIRES, and neither is optional. An
 * unscoped delegation is an account handover, and one with no expiry is a
 * handover that cannot be taken back - which is what delegation is supposed to
 * avoid being.
 *
 * `sign` and `verify_sig` are the host's; this holds no key material.
 */
ca_crypto_delegation_t *ca_ecdsa_crypto_delegation_new(
    const char *issuer,
    char *(*sign)(void *state, const char *payload),
    bool (*verify_sig)(void *state, const char *payload, const char *signature),
    void *state);

/* -- 23. writing code ----------------------------------------------------- */

typedef struct {
    char *id;
    char *prompt;
    char *output_snippet;
    bool tests_pass;
    char *deploy_hint;
} ca_code_gen_job_t;

void ca_code_gen_job_free(ca_code_gen_job_t *job);

typedef struct ca_code_generation_loop {
    void *state;
    ca_code_gen_job_t *(*run)(void *state, const char *prompt);
    void (*free_fn)(void *state);
} ca_code_generation_loop_t;

void ca_code_generation_loop_free(ca_code_generation_loop_t *loop);

/* Generates and SYNTAX-CHECKS, and nothing here deploys.
 *
 * `deploy_hint` is a string for a person to read, not an instruction anything
 * follows. A loop that could write code and also ship it is one bad generation
 * away from doing so, and the gap between those two is the only thing making
 * this safe to run at all. */
ca_code_generation_loop_t *ca_syntax_checking_code_generation_loop_new(void *generator);

/* -- 24. getting better --------------------------------------------------- */

typedef struct {
    char *improvements_applied;
    double new_bench_score;
} ca_self_improvement_verdict_t;

void ca_self_improvement_verdict_free(ca_self_improvement_verdict_t *verdict);

typedef struct ca_self_improvement_loop {
    void *state;
    ca_self_improvement_verdict_t *(*cycle)(void *state, const char *bench_suite_id);
    void (*free_fn)(void *state);
} ca_self_improvement_loop_t;

void ca_self_improvement_loop_free(ca_self_improvement_loop_t *loop);

/* Records what it changed and what the score did. Tracking rather than
 * improving: a loop that reports a better score without saying what it altered
 * cannot be audited, and one that cannot be audited cannot be trusted with the
 * thing it is altering. */
ca_self_improvement_loop_t *ca_tracking_self_improvement_loop_new(void);

/* Runs a bench suite and applies what measurably helped. Nothing is kept on a
 * tie: a change that does not clearly help is a change whose effect nobody
 * understands. */
ca_self_improvement_loop_t *ca_self_bench_self_improvement_loop_new(void);

/* -- capabilities offered outward ----------------------------------------- */

typedef struct ca_external_capability_registry ca_external_capability_registry_t;

/* What this companion will do for another agent - a strict subset of what it
 * can do for its owner, and it is a separate registry rather than a flag so
 * that the two lists cannot drift into each other. */
ca_external_capability_registry_t *ca_external_capability_registry_new(void);
void ca_external_capability_registry_free(ca_external_capability_registry_t *registry);

bool ca_external_capability_registry_offer(ca_external_capability_registry_t *registry,
                                           const char *capability_id,
                                           const char *scope);

bool ca_external_capability_registry_is_offered(
    const ca_external_capability_registry_t *registry, const char *capability_id);

/* -- listening ------------------------------------------------------------ */

typedef struct ca_voice_companion_listener ca_voice_companion_listener_t;

/* The companion attached to the voice loop: wake, listen, answer, remember. */
ca_voice_companion_listener_t *ca_voice_companion_listener_new(
    ca_companion_memory_encoder_t *encoder, ca_emotion_sensor_t *emotion);

void ca_voice_companion_listener_free(ca_voice_companion_listener_t *listener);

bool ca_voice_companion_listener_start(ca_voice_companion_listener_t *listener);
void ca_voice_companion_listener_stop(ca_voice_companion_listener_t *listener);

/* The assembled listener, with the on-device defaults already wired. The one
 * entry point a host needs; everything above is what it is made of. */
ca_voice_companion_listener_t *ca_neuron_voice_create_listener(void);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COMPANION_HERJARVIS_H */
