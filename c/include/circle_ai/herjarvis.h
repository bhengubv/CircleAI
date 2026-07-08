#ifndef CIRCLE_AI_HERJARVIS_H
#define CIRCLE_AI_HERJARVIS_H

/*
 * herjarvis.h — CircleAI HER/Jarvis companion contracts (C11 port).
 *
 * The remaining HER/Jarvis contracts plus their in-memory, deterministic
 * implementations, ported 1:1 from the C# reference (HerJarvisContracts.cs +
 * HerJarvisRealImplementations.cs). The reasoning quartet (WorldModel /
 * PredictiveEngine / InnerMonologue / TheoryOfMind, contracts 5/10/13/14) lives
 * in companion_reason.h; this header covers the rest:
 *
 *    1. IAlwaysOnPresence   → HeartbeatAlwaysOnPresence   (tick-driven heartbeat)
 *    2. IFusedPerception    → ChannelFusedPerception      (publish/drain queue)
 *    4. IContinuousLearner  → EwaContinuousLearner        (EWA reward per id)
 *    6. IGoalPursuer        → InMemoryGoalPursuer         (goal + milestone plan)
 *    8. IVoiceIdentity      → EnergyBandVoiceIdentity     (mean-MFCC + cosine)
 *    9. ICalibratedConfidence → HistoricalCalibratedConfidence (k-NN calibration)
 *   11. IEmotionSensor      → KeywordEmotionSensor        (keyword arousal/valence)
 *   12. ISkillAcquisition   → DemoStoreSkillAcquisition   (demo store + name)
 *   17. IBioSignalStream    → ChannelBioSignalStream      (publish/drain queue)
 *   18. IPhysicalActuator   → RegistryPhysicalActuator    (per-device handlers)
 *   19. IAgentPeerNetwork   → MailboxAgentPeerNetwork     (per-agent mailbox)
 *   20. IFederatedFineTuner → InMemoryFederatedFineTuner  (job runner + status)
 *   21. IFirstTokenOptimizer→ SlidingP50FirstTokenOptimizer (p50 window)
 *   22. ICryptoDelegation   → (HMAC-SHA256) delegation sign + verify
 *   23. ICodeGenerationLoop → SyntaxCheckingCodeGenerationLoop (balance check)
 *   24. ISelfImprovementLoop→ TrackingSelfImprovementLoop (bench-score tracker)
 *                             SelfBenchSelfImprovementLoop (A/B regression gate)
 *   + IVoiceListener        → VoiceCompanionListener      (pipeline→session bridge)
 *
 * Where the C# binds a native/ONNX/cloud backing (voice fingerprint, fine-tune
 * trainer, code generator/test-runner, bench runner, the voice pipeline, and the
 * ECDSA key) the port keeps the same seam as an INJECTED function pointer and
 * ships a working deterministic default so tests + hosts both get behaviour.
 *
 * Memory ownership follows the SDK contract (as memory_brain.h): owning structs
 * hold strdup'd copies with a matching *_free; returned arrays are deep copies
 * the caller frees; array-returning errors are NULL + *out_count == SIZE_MAX
 * (distinct from an empty NULL + 0). No pthreads; queues are single-thread
 * publish/drain.
 *
 * Pure C11 + libc. Links against -lm.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * 1. HeartbeatAlwaysOnPresence — tick-driven heartbeat with start/stop.
 * ===========================================================================
 *
 * The C# drives a System.Threading.Timer; the port keeps the same observable
 * contract (IsRunning + a monotonic Heartbeats counter) without threads: the
 * host pumps ca_always_on_presence_tick() from its own loop / clock. start
 * seeds one immediate heartbeat (Timer dueTime = Zero); tick adds one while
 * running; stop halts. Heartbeats is monotonic across stop/start.
 */

typedef struct ca_always_on_presence ca_always_on_presence_t;

ca_always_on_presence_t *ca_always_on_presence_create(void);
void ca_always_on_presence_destroy(ca_always_on_presence_t *p);

bool ca_always_on_presence_is_running(const ca_always_on_presence_t *p);
int64_t ca_always_on_presence_heartbeats(const ca_always_on_presence_t *p);

/* Start (idempotent). Emits one immediate heartbeat on the first start. */
void ca_always_on_presence_start(ca_always_on_presence_t *p);
/* Stop (idempotent). */
void ca_always_on_presence_stop(ca_always_on_presence_t *p);
/* One heartbeat if running (no-op when stopped). Returns the new count. */
int64_t ca_always_on_presence_tick(ca_always_on_presence_t *p);

/* ===========================================================================
 * 2. ChannelFusedPerception — publish/drain fused-percept queue.
 * ===========================================================================
 *
 * FusedPercept(At, Vision?, Audio?, Text?, Sensors[]). The C# uses an unbounded
 * Channel; the port is an in-order FIFO drained by ca_*_read (mirrors
 * WaitToRead+TryRead without blocking: read returns false when empty).
 */

typedef struct {
    int64_t at_ms;              /* DateTimeOffset → Unix ms UTC */
    char   *vision;             /* owned, or NULL */
    char   *audio;              /* owned, or NULL */
    char   *text;               /* owned, or NULL */
    char  **sensor_keys;        /* owned array, or NULL */
    double *sensor_values;      /* owned array, or NULL */
    size_t  sensor_count;
} ca_fused_percept_t;

void ca_fused_percept_free(ca_fused_percept_t *p);

typedef struct ca_fused_perception ca_fused_perception_t;

ca_fused_perception_t *ca_fused_perception_create(void);
void ca_fused_perception_destroy(ca_fused_perception_t *fp);

/* Publish a copy of the percept (ArgumentNullException → no-op on NULL). */
void ca_fused_perception_publish(ca_fused_perception_t *fp, const ca_fused_percept_t *p);
/* Mark the stream complete (drain still returns buffered items). */
void ca_fused_perception_complete(ca_fused_perception_t *fp);
/* Drain one percept into *out (deep copy the caller frees with
 * ca_fused_percept_free). Returns true if one was read, false when empty. */
bool ca_fused_perception_read(ca_fused_perception_t *fp, ca_fused_percept_t *out);

/* ===========================================================================
 * 4. EwaContinuousLearner — exponentially-weighted average reward per id.
 * ===========================================================================
 *
 * new_avg = prev_avg*(1-alpha) + reward*alpha; first sample seeds avg=reward,
 * weight 1. Weight counts observations. alpha in (0,1] (default 0.2).
 */

typedef struct ca_continuous_learner ca_continuous_learner_t;

/* alpha in (0,1]; NULL for out-of-range alpha. */
ca_continuous_learner_t *ca_continuous_learner_create(double alpha);
void ca_continuous_learner_destroy(ca_continuous_learner_t *l);

/* Register one reward for an interaction id. Blank id is a no-op. context_json
 * is accepted (mirrors the signature) but unused by this backing. */
void ca_continuous_learner_register(ca_continuous_learner_t *l,
                                    const char *interaction_id, double reward,
                                    const char *context_json);

/* Average reward of an id into *out. Returns true if the id is known. */
bool ca_continuous_learner_average(const ca_continuous_learner_t *l,
                                   const char *interaction_id, double *out);
/* Observation count for an id (0 if unknown). */
int64_t ca_continuous_learner_observations(const ca_continuous_learner_t *l,
                                           const char *interaction_id);

/* ===========================================================================
 * 6. InMemoryGoalPursuer — long-horizon goal + milestone plan + replan.
 * ===========================================================================
 *
 * LongHorizonGoal(Id, Description, DeadlineUtc, PlanJson, ProgressFraction).
 * Register builds a milestone plan JSON: milestones = clamp(totalDays/14, 2, 8),
 * evenly spaced deadlines; progress 0. Replan rebuilds the plan from "now".
 * Times are Unix ms UTC; the plan's ISO-8601 due strings are rendered from ms.
 */

typedef struct {
    char   *id;                 /* owned (32-hex, no dashes) */
    char   *description;        /* owned */
    int64_t deadline_ms;
    char   *plan_json;          /* owned */
    double  progress_fraction;
} ca_long_horizon_goal_t;

void ca_long_horizon_goal_free(ca_long_horizon_goal_t *g);

typedef struct ca_goal_pursuer ca_goal_pursuer_t;

ca_goal_pursuer_t *ca_goal_pursuer_create(void);
void ca_goal_pursuer_destroy(ca_goal_pursuer_t *gp);

/* Register a goal. now_ms is the current instant (the C# reads UtcNow; the port
 * takes it so plans are deterministic). Writes *out (deep copy the caller frees)
 * and returns true. Returns false on a blank description, a deadline <= now, or
 * NULL out. */
bool ca_goal_pursuer_register(ca_goal_pursuer_t *gp, const char *description,
                              int64_t deadline_ms, int64_t now_ms,
                              ca_long_horizon_goal_t *out);

/* Fetch the current goal by id into *out (deep copy). Returns true if found. */
bool ca_goal_pursuer_current(const ca_goal_pursuer_t *gp, const char *id,
                             ca_long_horizon_goal_t *out);

/* Rebuild the plan from now_ms. Returns true; false on an unknown id. */
bool ca_goal_pursuer_replan(ca_goal_pursuer_t *gp, const char *id, int64_t now_ms);

/* Set progress fraction (clamped-check [0,1]). Returns true; false on an unknown
 * id or an out-of-range fraction. */
bool ca_goal_pursuer_progress(ca_goal_pursuer_t *gp, const char *id, double fraction);

/* ===========================================================================
 * 8. EnergyBandVoiceIdentity — mean-MFCC fingerprint + cosine similarity.
 * ===========================================================================
 *
 * Full MFCC pipeline (pre-emphasis 0.97 → 25ms/10ms Hamming frames → direct DFT
 * power spectrum → 26 mel filters → log → 13-coefficient DCT-II → mean over
 * frames). Enroll stores a fingerprint per user; Identify returns the best user
 * whose cosine similarity exceeds 0.85, else "unknown". PCM is 16-bit LE.
 */

typedef struct ca_voice_identity ca_voice_identity_t;

ca_voice_identity_t *ca_voice_identity_create(void);
void ca_voice_identity_destroy(ca_voice_identity_t *v);

/* Enroll audio for a user. audio_pcm16 is little-endian 16-bit PCM; byte_len is
 * its length in BYTES. Blank userId or NULL audio is a no-op. */
void ca_voice_identity_enroll(ca_voice_identity_t *v, const char *user_id,
                              const uint8_t *audio_pcm16, size_t byte_len,
                              int sample_rate_hz);

/* Identify the speaker. Returns a fresh strdup'd user id (caller frees), or NULL
 * when no enrolled voice exceeds the 0.85 similarity threshold. */
char *ca_voice_identity_identify(const ca_voice_identity_t *v,
                                 const uint8_t *audio_pcm16, size_t byte_len,
                                 int sample_rate_hz);

/* Exposed for tests: mean-MFCC of a PCM buffer into a caller-provided 13-float
 * array. Returns the coefficient count written (13), or 0 on bad args. */
size_t ca_voice_identity_mfcc(const uint8_t *audio_pcm16, size_t byte_len,
                              int sample_rate_hz, double out_coeffs[13]);

/* ===========================================================================
 * 9. HistoricalCalibratedConfidence — raw score → k-NN calibrated band.
 * ===========================================================================
 *
 * ConfidenceBand(Lower, Upper). RawScore = clamp(log(len)/10 + (hasContext?0.1)
 * - hedgePenalty, 0, 1) where hedgePenalty = min(0.5, hedgeCount*0.1) over the
 * words maybe/perhaps/might/possibly/unclear/"don't know". With < 5 recorded
 * outcomes calibrated = raw; else calibrated = fraction-correct of the 5 nearest
 * raw scores. halfBand = max(0.05, 0.25 - calibrated*0.2); band clamps to [0,1].
 */

typedef struct { double lower; double upper; } ca_confidence_band_t;

typedef struct ca_calibrated_confidence ca_calibrated_confidence_t;

ca_calibrated_confidence_t *ca_calibrated_confidence_create(void);
void ca_calibrated_confidence_destroy(ca_calibrated_confidence_t *c);

/* Record a (rawScore, wasCorrect) calibration sample (rawScore clamped [0,1]). */
void ca_calibrated_confidence_record(ca_calibrated_confidence_t *c,
                                     double raw_score, bool was_correct);

/* Evaluate an answer against optional context JSON. Writes *out and returns
 * true; false on NULL answer/out. */
bool ca_calibrated_confidence_evaluate(const ca_calibrated_confidence_t *c,
                                       const char *answer, const char *context_json,
                                       ca_confidence_band_t *out);

/* ===========================================================================
 * 11. KeywordEmotionSensor — keyword arousal/valence over fused JSON (stateless).
 * ===========================================================================
 *
 * EmotionFrame(Label, Arousal, Valence). Counts word matches for six labelled
 * patterns (joy/anger/sad/fear/surprise/calm); the frame is the count-weighted
 * arousal/valence with the highest-count label. No hits → ("neutral", 0, 0).
 */

typedef struct {
    char  *label;               /* owned */
    double arousal;
    double valence;
} ca_emotion_frame_t;

void ca_emotion_frame_free(ca_emotion_frame_t *f);

/* Sense emotion from fused JSON. Writes *out and returns true; false on NULL
 * fused/out. Caller frees *out with ca_emotion_frame_free. */
bool ca_emotion_sensor_sense(const char *fused_json, ca_emotion_frame_t *out);

/* ===========================================================================
 * 12. DemoStoreSkillAcquisition — demonstration store with name extraction.
 * ===========================================================================
 *
 * AcquiredSkill(Id, Name, DescriptionJson). Acquire stores a demo keyed by a
 * fresh 32-hex id; name = the JSON "name" string field, else "skill-"+id[..6].
 * List returns all skills sorted by name (Ordinal).
 */

typedef struct {
    char *id;                   /* owned (32-hex) */
    char *name;                 /* owned */
    char *description_json;     /* owned */
} ca_acquired_skill_t;

void ca_acquired_skill_free(ca_acquired_skill_t *s);
void ca_acquired_skill_free_array(ca_acquired_skill_t *arr, size_t count);

typedef struct ca_skill_acquisition ca_skill_acquisition_t;

ca_skill_acquisition_t *ca_skill_acquisition_create(void);
void ca_skill_acquisition_destroy(ca_skill_acquisition_t *sa);

/* Acquire a skill from a demonstration JSON. Writes *out (deep copy) and returns
 * true; false on NULL demonstration/out. The id is generated deterministically
 * from an internal counter so tests are reproducible. */
bool ca_skill_acquisition_acquire(ca_skill_acquisition_t *sa,
                                  const char *demonstration_json,
                                  ca_acquired_skill_t *out);

/* Snapshot all acquired skills, sorted by name. Returns a fresh array (caller
 * frees with ca_acquired_skill_free_array); *out_count set (0 → NULL). */
ca_acquired_skill_t *ca_skill_acquisition_list(const ca_skill_acquisition_t *sa,
                                               size_t *out_count);

/* ===========================================================================
 * 17. ChannelBioSignalStream — publish/drain bio-signal queue.
 * ===========================================================================
 *
 * BioSignal(Kind, Value, At). Same publish/drain contract as fused perception.
 */

typedef struct {
    char   *kind;               /* owned */
    double  value;
    int64_t at_ms;
} ca_bio_signal_t;

void ca_bio_signal_free(ca_bio_signal_t *s);

typedef struct ca_bio_signal_stream ca_bio_signal_stream_t;

ca_bio_signal_stream_t *ca_bio_signal_stream_create(void);
void ca_bio_signal_stream_destroy(ca_bio_signal_stream_t *bs);

void ca_bio_signal_stream_publish(ca_bio_signal_stream_t *bs, const ca_bio_signal_t *s);
void ca_bio_signal_stream_complete(ca_bio_signal_stream_t *bs);
bool ca_bio_signal_stream_read(ca_bio_signal_stream_t *bs, ca_bio_signal_t *out);

/* ===========================================================================
 * 18. RegistryPhysicalActuator — per-device command dispatch.
 * ===========================================================================
 *
 * PhysicalCommand(DeviceId, Action, Args[]). PhysicalCommandResult(Succeeded,
 * Error?). Register a handler per device id; Invoke dispatches, or fails with
 * "Unknown device '<id>'" when no handler is registered.
 */

typedef struct {
    const char        *device_id;
    const char        *action;
    const char *const *arg_keys;
    const char *const *arg_values;
    size_t             arg_count;
} ca_physical_command_t;

typedef struct {
    bool  succeeded;
    char *error;                /* owned, or NULL */
} ca_physical_command_result_t;

void ca_physical_command_result_free(ca_physical_command_result_t *r);

/* Device handler seam. Fills *out (its error string must be malloc'd if set). */
typedef void (*ca_physical_device_handler_fn)(
    void *user, const ca_physical_command_t *cmd, ca_physical_command_result_t *out);

typedef struct ca_physical_actuator ca_physical_actuator_t;

ca_physical_actuator_t *ca_physical_actuator_create(void);
void ca_physical_actuator_destroy(ca_physical_actuator_t *a);

/* Register (or replace) the handler for a device id. Blank id / NULL handler is
 * a no-op. */
void ca_physical_actuator_register(ca_physical_actuator_t *a, const char *device_id,
                                   ca_physical_device_handler_fn handler, void *user);

/* Invoke a command. Writes *out and returns true; false only on NULL a/cmd/out.
 * An unknown device fills a (false, "Unknown device '<id>'") result. */
bool ca_physical_actuator_invoke(const ca_physical_actuator_t *a,
                                 const ca_physical_command_t *cmd,
                                 ca_physical_command_result_t *out);

/* ===========================================================================
 * 19. MailboxAgentPeerNetwork — per-agent in-memory mailbox.
 * ===========================================================================
 *
 * AgentToAgentMessage(FromAgentId, ToAgentId, Payload, At). Send appends to the
 * recipient's mailbox; Receive drains the addressee's mailbox FIFO.
 */

typedef struct {
    char   *from_agent_id;      /* owned */
    char   *to_agent_id;        /* owned */
    char   *payload;            /* owned */
    int64_t at_ms;
} ca_agent_peer_message_t;

void ca_agent_peer_message_free(ca_agent_peer_message_t *m);

typedef struct ca_agent_peer_network ca_agent_peer_network_t;

ca_agent_peer_network_t *ca_agent_peer_network_create(void);
void ca_agent_peer_network_destroy(ca_agent_peer_network_t *n);

/* Send a copy of the message to its ToAgentId mailbox. NULL is a no-op. */
void ca_agent_peer_network_send(ca_agent_peer_network_t *n, const ca_agent_peer_message_t *m);

/* Drain one message addressed to for_agent_id into *out (deep copy). Returns
 * true if one was read, false when the mailbox is empty. Blank id → false. */
bool ca_agent_peer_network_receive(ca_agent_peer_network_t *n, const char *for_agent_id,
                                   ca_agent_peer_message_t *out);

/* ===========================================================================
 * 20. InMemoryFederatedFineTuner — job runner + status tracking.
 * ===========================================================================
 *
 * FineTuneJobStatus(JobId, Progress, Error?). Start launches a job (the trainer
 * seam drives progress 0→1); Status reports the latest. The C# runs the trainer
 * on a task; the port runs it synchronously inside Start (no threads) and the
 * default trainer reports steady progress to 1.0. An unknown job → (id, 0,
 * "unknown job").
 */

typedef struct {
    char  *job_id;              /* owned */
    double progress;
    char  *error;               /* owned, or NULL */
} ca_finetune_status_t;

void ca_finetune_status_free(ca_finetune_status_t *s);

/* Progress-reporting sink handed to the trainer. */
typedef void (*ca_finetune_progress_fn)(void *sink, double progress);

/* Trainer seam. Invoke report(sink, p) with p in [0,1] as training advances.
 * Return NULL on success or a malloc'd error string on failure. */
typedef char *(*ca_finetune_trainer_fn)(
    void *user, const char *base_model, const char *training_data_path,
    ca_finetune_progress_fn report, void *sink);

typedef struct ca_federated_finetuner ca_federated_finetuner_t;

/* trainer may be NULL → the default steady-progress trainer. */
ca_federated_finetuner_t *ca_federated_finetuner_create(ca_finetune_trainer_fn trainer,
                                                        void *trainer_user);
void ca_federated_finetuner_destroy(ca_federated_finetuner_t *ft);

/* Start a job. Runs the trainer to completion, then returns a fresh strdup'd job
 * id (caller frees), or NULL on a blank baseModel/trainingDataPath. */
char *ca_federated_finetuner_start(ca_federated_finetuner_t *ft,
                                   const char *base_model, const char *training_data_path);

/* Status of a job into *out (deep copy). Returns true always; an unknown job
 * yields (job_id, 0, "unknown job"). */
bool ca_federated_finetuner_status(const ca_federated_finetuner_t *ft,
                                   const char *job_id, ca_finetune_status_t *out);

/* ===========================================================================
 * 21. SlidingP50FirstTokenOptimizer — sliding-window p50 latency.
 * ===========================================================================
 *
 * FirstTokenBudget(TargetMs, CurrentP50Ms). Records first-token latencies into a
 * fixed window; p50 = sorted[count/2] (0 when empty). targetMs, windowSize > 0.
 */

typedef struct { int target_ms; int current_p50_ms; } ca_first_token_budget_t;

typedef struct ca_first_token_optimizer ca_first_token_optimizer_t;

/* target_ms and window_size must be > 0 (defaults 100, 256); NULL otherwise. */
ca_first_token_optimizer_t *ca_first_token_optimizer_create(int target_ms, int window_size);
void ca_first_token_optimizer_destroy(ca_first_token_optimizer_t *o);

/* Record one first-token latency (ms >= 0; negative is ignored). */
void ca_first_token_optimizer_record(ca_first_token_optimizer_t *o, int ms);

/* Current budget into *out. Returns true; false on NULL o/out. */
bool ca_first_token_optimizer_current(const ca_first_token_optimizer_t *o,
                                      ca_first_token_budget_t *out);

/* ===========================================================================
 * 22. Crypto delegation — HMAC-SHA256 delegation credential sign + verify.
 * ===========================================================================
 *
 * DelegationCredential(Issuer, SubjectId, Scope, ExpiresAtUtc, Signature). The
 * C# signs the canonical string "issuer|subject|scope|expiresISO" with ECDSA
 * P-256. Without a bundled asymmetric stack, the C port signs the identical
 * canonical string with HMAC-SHA256 over an injected (or generated) secret key
 * — same Issue/Verify contract, same canonical form, Base64 signature. Verify
 * checks issuer, expiry, and the MAC.
 */

typedef struct {
    char   *issuer;             /* owned */
    char   *subject_id;         /* owned */
    char   *scope;              /* owned */
    int64_t expires_at_ms;
    char   *signature_b64;      /* owned */
} ca_delegation_credential_t;

void ca_delegation_credential_free(ca_delegation_credential_t *c);

typedef struct ca_crypto_delegation ca_crypto_delegation_t;

/* Create a signer. issuer defaults to "circleai-companion" when NULL/blank. key
 * (secret bytes) may be NULL → a fixed internal key is used (deterministic).
 * Returns NULL only on allocation failure. */
ca_crypto_delegation_t *ca_crypto_delegation_create(const char *issuer,
                                                    const uint8_t *key, size_t key_len);
void ca_crypto_delegation_destroy(ca_crypto_delegation_t *d);

/* Issue a credential for a subject/scope with a lifetime (ms). now_ms is the
 * current instant. Writes *out (deep copy) and returns true; false on a blank
 * subject/scope, a non-positive lifetime, or NULL out. */
bool ca_crypto_delegation_issue(const ca_crypto_delegation_t *d,
                                const char *subject_id, const char *scope,
                                int64_t lifetime_ms, int64_t now_ms,
                                ca_delegation_credential_t *out);

/* Verify a credential at now_ms. Returns true iff issuer matches, not expired,
 * and the signature verifies. */
bool ca_crypto_delegation_verify(const ca_crypto_delegation_t *d,
                                 const ca_delegation_credential_t *cred, int64_t now_ms);

/* ===========================================================================
 * 23. SyntaxCheckingCodeGenerationLoop — generate + balance-check + test.
 * ===========================================================================
 *
 * CodeGenJob(Id, Prompt, OutputSnippet, TestsPass, DeployHint?). Run generates a
 * snippet (default: the "(3.3.0) generated from: <prompt>\nreturn 0;" echo),
 * checks bracket balance, runs the test seam (default: balance == pass), and
 * emits a deploy hint ("stage as nuget" if the snippet contains "public class",
 * else "run inline") only when tests pass.
 */

typedef struct {
    char *id;                   /* owned (32-hex) */
    char *prompt;               /* owned */
    char *output_snippet;       /* owned */
    bool  tests_pass;
    char *deploy_hint;          /* owned, or NULL */
} ca_codegen_job_t;

void ca_codegen_job_free(ca_codegen_job_t *j);

/* Generator seam: return a malloc'd snippet for the prompt. */
typedef char *(*ca_codegen_generator_fn)(void *user, const char *prompt);
/* Test-runner seam: return whether the snippet's tests pass. */
typedef bool (*ca_codegen_test_runner_fn)(void *user, const char *snippet);
/* Deploy-hint seam: return a malloc'd hint (or NULL) for the snippet. */
typedef char *(*ca_codegen_deploy_hint_fn)(void *user, const char *snippet);

typedef struct ca_code_generation_loop ca_code_generation_loop_t;

/* Any seam may be NULL → its deterministic default. */
ca_code_generation_loop_t *ca_code_generation_loop_create(
    ca_codegen_generator_fn generator, void *generator_user,
    ca_codegen_test_runner_fn test_runner, void *test_runner_user,
    ca_codegen_deploy_hint_fn deploy_hint, void *deploy_hint_user);
void ca_code_generation_loop_destroy(ca_code_generation_loop_t *l);

/* Run one code-gen job. Writes *out (deep copy) and returns true; false on a
 * blank prompt or NULL out. */
bool ca_code_generation_loop_run(ca_code_generation_loop_t *l, const char *prompt,
                                 ca_codegen_job_t *out);

/* Exposed for tests + the default runner: bracket-balance check. */
bool ca_code_is_syntactically_balanced(const char *snippet);

/* ===========================================================================
 * 24a. TrackingSelfImprovementLoop — bench-score tracker.
 * ===========================================================================
 *
 * SelfImprovementVerdict(ImprovementsApplied, NewBenchScore). Cycle runs the
 * bench seam; if current >= best it records the new best ("new best" when
 * strictly greater, else "no regression"); otherwise it asks the improvement
 * seam for a proposal and returns that.
 */

typedef struct {
    char  *improvements_applied; /* owned */
    double new_bench_score;
} ca_self_improvement_verdict_t;

void ca_self_improvement_verdict_free(ca_self_improvement_verdict_t *v);

/* Bench seam: return the score in [0,1] for a suite id. */
typedef double (*ca_selfimprove_bench_fn)(void *user, const char *bench_suite_id);
/* Improvement seam: return a malloc'd description of the applied improvement. */
typedef char *(*ca_selfimprove_propose_fn)(void *user, const char *bench_suite_id,
                                           double current);

typedef struct ca_self_improvement_loop ca_self_improvement_loop_t;

/* Either seam may be NULL → its deterministic default. */
ca_self_improvement_loop_t *ca_self_improvement_loop_create(
    ca_selfimprove_bench_fn bench, void *bench_user,
    ca_selfimprove_propose_fn propose, void *propose_user);
void ca_self_improvement_loop_destroy(ca_self_improvement_loop_t *l);

/* Run one improvement cycle. Writes *out (deep copy) and returns true; false on
 * a blank suite id or NULL out. */
bool ca_self_improvement_loop_cycle(ca_self_improvement_loop_t *l,
                                    const char *bench_suite_id,
                                    ca_self_improvement_verdict_t *out);

/* Best score recorded for a suite (0 if none). */
double ca_self_improvement_loop_best_score(const ca_self_improvement_loop_t *l,
                                           const char *bench_suite_id);

/* ===========================================================================
 * 24b. SelfBenchSelfImprovementLoop — A/B regression-gate promotion.
 * ===========================================================================
 *
 * Wraps a bench-suite registry (id → task count) + an A/B runner seam. Cycle
 * fetches the suite; empty → ("skipped: no tasks in suite", 0). Otherwise it
 * builds a baseline + candidate (factory seams), runs the A/B comparison, and
 * promotes the candidate when the gate says so — invoking the promote seam and
 * recording max(best, newScore). Verdict text is "promoted candidate (<reason>)"
 * or "rejected (<reason>)".
 *
 * The A/B verdict carries the candidate mean score, a promote flag, and a
 * reason. RegressionGateConfig is opaque to the loop (the runner applies it);
 * the port exposes the same seam shape.
 */

typedef struct {
    double candidate_mean_score;
    bool   should_promote;
    char  *reason;              /* owned */
} ca_ab_verdict_t;

void ca_ab_verdict_free(ca_ab_verdict_t *v);

/* Suite registry: return the task count for a suite id (0 if unknown/empty). */
typedef size_t (*ca_selfbench_suite_count_fn)(void *user, const char *bench_suite_id);
/* A/B runner: compare baseline vs candidate for a suite; fill *out. The runner
 * owns applying the regression gate. Return true on success. */
typedef bool (*ca_selfbench_ab_run_fn)(void *user, const char *bench_suite_id,
                                       size_t task_count, ca_ab_verdict_t *out);
/* Promote seam: called when the candidate is promoted (may be NULL). */
typedef void (*ca_selfbench_promote_fn)(void *user, const ca_ab_verdict_t *verdict);

typedef struct ca_selfbench_improvement_loop ca_selfbench_improvement_loop_t;

/* suite_count and ab_run are required; promote may be NULL. Returns NULL on a
 * NULL required seam. */
ca_selfbench_improvement_loop_t *ca_selfbench_improvement_loop_create(
    ca_selfbench_suite_count_fn suite_count, void *suite_count_user,
    ca_selfbench_ab_run_fn ab_run, void *ab_run_user,
    ca_selfbench_promote_fn promote, void *promote_user);
void ca_selfbench_improvement_loop_destroy(ca_selfbench_improvement_loop_t *l);

/* Run one cycle. A blank suite id defaults to "default". Writes *out (deep copy)
 * and returns true; false only on NULL out or an ab_run failure. */
bool ca_selfbench_improvement_loop_cycle(ca_selfbench_improvement_loop_t *l,
                                         const char *bench_suite_id,
                                         ca_self_improvement_verdict_t *out);

/* Best score recorded for a suite (0 if none). */
double ca_selfbench_improvement_loop_best_score(const ca_selfbench_improvement_loop_t *l,
                                                const char *bench_suite_id);

/* ===========================================================================
 * IVoiceListener → VoiceCompanionListener — voice pipeline → session bridge.
 * ===========================================================================
 *
 * The C# subscribes to VoicePipeline.Transcribed, raises UtteranceDetected, then
 * dispatches session.SendAsync on the thread-pool and raises ResponseReady with
 * the reply. The port keeps the same event shape via callbacks and drives the
 * session synchronously inside ca_voice_listener_on_transcribed (no threads):
 * the host feeds transcriptions in, the listener raises the two events in order.
 *
 * The session seam is a generate function: given the utterance text it returns a
 * malloc'd reply (or NULL to signal a failure, which is swallowed — matching the
 * C# try/catch that traces and does not raise ResponseReady).
 */

typedef struct {
    const char *text;
    float       confidence;
    int64_t     detected_at_ms;
} ca_utterance_detected_event_t;

typedef struct {
    const char *text;
    const char *original_utterance;
    int64_t     completed_at_ms;
} ca_response_ready_event_t;

typedef void (*ca_utterance_detected_fn)(void *user, const ca_utterance_detected_event_t *e);
typedef void (*ca_response_ready_fn)(void *user, const ca_response_ready_event_t *e);
/* Session seam: return a malloc'd reply for the utterance, or NULL on failure. */
typedef char *(*ca_voice_session_send_fn)(void *user, const char *text);

typedef struct ca_voice_listener ca_voice_listener_t;

/* Create the bridge. session_send is required; the two event callbacks may be
 * NULL (no subscriber). Returns NULL on a NULL session_send. */
ca_voice_listener_t *ca_voice_listener_create(
    ca_voice_session_send_fn session_send, void *session_user,
    ca_utterance_detected_fn on_utterance, void *on_utterance_user,
    ca_response_ready_fn on_response, void *on_response_user);
void ca_voice_listener_destroy(ca_voice_listener_t *l);

/* Feed a completed transcription (as VoicePipeline.Transcribed would). Raises
 * UtteranceDetected, calls the session, and (on a non-NULL reply) raises
 * ResponseReady. now_ms stamps the completed-at time. No-op after destroy-guard
 * or on a disposed listener. Returns true if ResponseReady fired. */
bool ca_voice_listener_on_transcribed(ca_voice_listener_t *l, const char *text,
                                      float confidence, int64_t detected_at_ms,
                                      int64_t now_ms);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_HERJARVIS_H */
