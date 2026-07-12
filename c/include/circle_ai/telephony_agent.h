#ifndef CIRCLE_AI_TELEPHONY_AGENT_H
#define CIRCLE_AI_TELEPHONY_AGENT_H

/*
 * telephony_agent.h — CircleAI.Telephony voice-agent layer (C11 port).
 *
 * The pure-logic voice-agent classes that sit ON TOP of the carrier contract
 * surface in telephony.h. Everything here is deterministic in-memory logic;
 * the async / TTS / HTTP / tunnel boundaries the C# code hides behind
 * delegates become explicit ca_ fn-ptr seams a host supplies.
 *
 * Ports 1:1:
 *
 *   BargeInController.cs      — BargeInState / BargeInTransition / BargeInOptions
 *                               / BargeInController (pause/resume/cancel FSM).
 *   IvrLoopDetector.cs        — IvrRound / IvrLoopVerdict / IvrLoopDetector.
 *   ToolCircuitBreaker.cs     — ToolCallPolicy / ToolBreakerState /
 *                               CircuitBreakerToolRegistry (decorates a
 *                               ca_tel_tool_registry_t with per-tool timeout +
 *                               breaker; the "timeout" seam is a caller flag).
 *   Guardrails.cs             — GuardrailRule / GuardrailAction / GuardrailResult
 *                               / Guardrails + CommonGuardrails. Regex is modelled
 *                               as a matcher fn-ptr per rule (no libregex dep) so
 *                               the host wires .NET-equivalent matching; the two
 *                               built-in PII rules ship deterministic C matchers.
 *   ReassuranceFiller.cs      — ReassuranceVocabulary / ReassuranceFillerOptions +
 *                               the rotation logic (NextShort/NextLong). The async
 *                               run-with-filler loop is host-driven; we port the
 *                               deterministic phrase rotation.
 *   SpeculativeGenerator.cs   — the branch bookkeeping FSM (Speculate / Commit /
 *                               Abort decisions) over a caller-supplied generator
 *                               seam; "is this final a continuation of the active
 *                               partial" is the ported logic.
 *   FalseInterruptionTracker  — InterruptionStats + counter logic keyed off
 *                               BargeInTransition.To.
 *   HoldMusicMixer.cs         — MixFrame: loop background PCM-16, duck under speech.
 *   WarmTransferOrchestrator  — WarmTransferRequest/Result + the 4-step sequence
 *                               (dial→brief→bridge→hangup) over the carrier vtable
 *                               and a TTS seam.
 *   ConsultEscalation.cs      — ConsultRequest/Answer + EscalateAsync channel walk
 *                               (first non-null wins) over a channel fn-ptr seam.
 *   AgentHandoff.cs           — CallAgent / HandoffResult + catalog + handoff FSM
 *                               with greeting via TTS seam.
 *   LlmJudge.cs               — JudgeDimension / JudgeVerdict + prompt build +
 *                               verdict parse over a completion seam. JSON parse is
 *                               a small tolerant scanner (extract {..}, pull scores).
 *   EvalSession.cs            — EvalTurn / EvalTurnResult / EvalRunResult + RunAsync
 *                               (missing-keyword scan) over a turn-handler seam.
 *   SentenceChunker.cs        — streaming sentence splitter (terminal punctuation +
 *                               min length).
 *   LatencyTracker.cs         — sliding-window per-stage percentiles.
 *   DashboardData.cs          — the row/summary/snapshot records + composed source.
 *   FirstMessagePreamble.cs   — the race+render decision (model-ready vs latency
 *                               window) + template render via the resolver.
 *   StereoCallRecorder.cs     — interleave caller(L)/agent(R) PCM-16 into a stereo
 *                               WAV byte buffer (grows in memory; header backfilled).
 *   AnsweringMachineDetector  — frame-by-frame heuristic AMD.
 *   Telemetry.cs              — VoiceLoopTelemetry span names + outcome tagging as a
 *                               tiny in-memory span model (no OTel dep).
 *   StreamingToolProgress.cs  — ToolProgressUpdate + throttled/recording sinks +
 *                               runner over a streaming-handler seam.
 *   VoiceLoopAsTool.cs        — VoiceLoopToolRequest/Result + Descriptor + invoke
 *                               over a runner seam (with max-duration timeout flag).
 *   PromptVariableResolver.cs — {{var}} substitution over static + provider seams.
 *   LocalDevTunnel.cs         — ILocalDevTunnel shapes (Null/Static/Cloudflare/Ngrok)
 *                               over a resolver fn-ptr seam (the HTTP/tunnel boundary).
 *   McpToolImporter.cs        — parse an MCP tools/list JSON body + register each as
 *                               a webhook tool; the HTTP fetch is a caller seam.
 *
 * Conventions (match telephony.h): ca_ prefix, _t types, opaque handles,
 * strdup-owning fields with matching *_free / *_destroy, deep-copy getters, errors
 * via NULL / count SIZE_MAX / rc -1. Durations are TimeSpan ticks (100ns);
 * timestamps DateTimeOffset as Unix ms UTC (int64). PCM-16 little-endian.
 *
 * Pure C11 + libc + libm. No pthreads (single-threaded deterministic).
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "circle_ai/telephony.h"

#ifdef __cplusplus
extern "C" {
#endif

/* TimeSpan tick helpers (100ns units) — shared by the options records. */
#define CA_TELA_TICKS_PER_MS 10000LL
#define CA_TELA_TICKS_PER_SEC 10000000LL

/* Forward decl — the preamble's speak() takes a resolver before its full typedef
 * (defined in the PromptVariableResolver section) is introduced. */
struct ca_tela_prompt_resolver;

/* ===========================================================================
 * BargeInController — pause/resume/cancel FSM for mid-turn interruption.
 * =========================================================================== */

typedef enum {
    CA_TELA_BARGE_SPEAKING = 0,  /* AI is speaking */
    CA_TELA_BARGE_PAUSED,        /* caller interrupted; deciding */
    CA_TELA_BARGE_CANCELLED,     /* confirmed real interruption — turn dropped */
    CA_TELA_BARGE_RESUMED        /* false alarm — resumed speaking */
} ca_tela_barge_state_t;

/* One state transition (owns `reason`). */
typedef struct {
    ca_tela_barge_state_t from;
    ca_tela_barge_state_t to;
    int64_t               at_utc_ms;
    char                 *reason;   /* owned */
} ca_tela_barge_transition_t;

void ca_tela_barge_transition_free(ca_tela_barge_transition_t *t);
ca_tela_barge_transition_t *ca_tela_barge_transition_copy(
    const ca_tela_barge_transition_t *t);

typedef struct ca_tela_barge_controller ca_tela_barge_controller_t;

/* pause_after_ticks<=0 -> default 100ms; cancel_after_ticks<=0 -> default 600ms.
 * The C# `clock` seam is a caller-supplied "now" passed on each observe call, so
 * behaviour stays deterministic. NULL on OOM. */
ca_tela_barge_controller_t *ca_tela_barge_controller_create(
    int64_t pause_after_ticks, int64_t cancel_after_ticks);
void ca_tela_barge_controller_destroy(ca_tela_barge_controller_t *c);

/* State getter. */
ca_tela_barge_state_t ca_tela_barge_controller_state(
    const ca_tela_barge_controller_t *c);

/* OnPlaybackStart — reset to Speaking, clear the speech-start marker. */
void ca_tela_barge_controller_on_playback_start(ca_tela_barge_controller_t *c);

/* OnCallerSpeech(now_utc_ms): may return a freshly-owned transition (free with
 * ca_tela_barge_transition_free) or NULL when no transition. `*out` set to NULL
 * when none. Returns 0 always (never fails allocation-fatal — NULL transition on
 * OOM). */
ca_tela_barge_transition_t *ca_tela_barge_controller_on_caller_speech(
    ca_tela_barge_controller_t *c, int64_t now_utc_ms);

/* OnCallerSilence(now_utc_ms): resume from Paused (returns a Resumed transition)
 * else NULL. Owned or NULL. */
ca_tela_barge_transition_t *ca_tela_barge_controller_on_caller_silence(
    ca_tela_barge_controller_t *c, int64_t now_utc_ms);

/* ShouldEmitAudio — state == Speaking. */
bool ca_tela_barge_controller_should_emit_audio(const ca_tela_barge_controller_t *c);
/* WasBargedIn — state == Cancelled. */
bool ca_tela_barge_controller_was_barged_in(const ca_tela_barge_controller_t *c);

/* ===========================================================================
 * IvrLoopDetector — detect a stuck IVR navigation cycle.
 * =========================================================================== */

/* One observed round (owns speech + optional dtmf). */
typedef struct {
    char   *speech;        /* owned */
    char   *dtmf_pressed;  /* owned or NULL */
    int64_t at_utc_ms;
} ca_tela_ivr_round_t;

/* Loop verdict (owns reason). */
typedef struct {
    bool  is_looping;
    int   loop_length;
    char *reason;          /* owned */
} ca_tela_ivr_verdict_t;

void ca_tela_ivr_verdict_free(ca_tela_ivr_verdict_t *v);

typedef struct ca_tela_ivr_detector ca_tela_ivr_detector_t;

/* max_rounds<=0 -> 32; min_rounds<=0 -> 2; similarity in (0,1], <=0 -> 0.85. */
ca_tela_ivr_detector_t *ca_tela_ivr_detector_create(
    int max_rounds_to_track, int min_rounds_for_loop, double similarity_threshold);
void ca_tela_ivr_detector_destroy(ca_tela_ivr_detector_t *d);

/* Observe(round): append + return the current verdict (freshly owned; free with
 * ca_tela_ivr_verdict_free). speech required; dtmf may be NULL. NULL on bad args
 * / OOM. */
ca_tela_ivr_verdict_t *ca_tela_ivr_detector_observe(
    ca_tela_ivr_detector_t *d, const char *speech, const char *dtmf_pressed,
    int64_t at_utc_ms);
/* CurrentVerdict — no append. Owned or NULL. */
ca_tela_ivr_verdict_t *ca_tela_ivr_detector_current(ca_tela_ivr_detector_t *d);
/* Reset — drop history. */
void ca_tela_ivr_detector_reset(ca_tela_ivr_detector_t *d);

/* ===========================================================================
 * CircuitBreakerToolRegistry — per-tool timeout + breaker over a tool registry.
 *
 * The C# wraps an IToolCallRegistry and adds a wall-clock timeout + a 3-state
 * breaker per tool. Here we decorate a ca_tel_tool_registry_t. The "timeout"
 * cannot be measured against a real clock deterministically, so the caller signals
 * a timeout by passing `now_utc_ms` on each invoke (drives Open->HalfOpen) and an
 * explicit `simulate_timeout` flag to force the timeout path (records a failure +
 * returns the timeout ToolResult without calling the inner registry).
 * =========================================================================== */

typedef enum {
    CA_TELA_BREAKER_CLOSED = 0,
    CA_TELA_BREAKER_OPEN,
    CA_TELA_BREAKER_HALF_OPEN
} ca_tela_breaker_state_t;

/* Per-tool policy. timeout_ticks<=0 -> 5s; failure_threshold<=0 -> 3;
 * open_duration_ticks<=0 -> 30s. */
typedef struct {
    int64_t timeout_ticks;
    int     failure_threshold;
    int64_t open_duration_ticks;
} ca_tela_tool_policy_t;

/* Record defaults (5s / 3 / 30s). */
ca_tela_tool_policy_t ca_tela_tool_policy_default(void);

typedef struct ca_tela_cb_registry ca_tela_cb_registry_t;

/* Decorate `inner` (borrowed — must outlive the decorator). `default_policy` NULL
 * -> record defaults. NULL on bad args / OOM. */
ca_tela_cb_registry_t *ca_tela_cb_registry_create(
    ca_tel_tool_registry_t *inner, const ca_tela_tool_policy_t *default_policy);
void ca_tela_cb_registry_destroy(ca_tela_cb_registry_t *r);

/* SetPolicy(toolName, policy) — per-tool override (case-insensitive). 0 / -1. */
int ca_tela_cb_registry_set_policy(ca_tela_cb_registry_t *r, const char *tool_name,
                                   const ca_tela_tool_policy_t *policy);

/* GetState(toolName, now) — the breaker state as of `now`. */
ca_tela_breaker_state_t ca_tela_cb_registry_get_state(
    const ca_tela_cb_registry_t *r, const char *tool_name, int64_t now_utc_ms);

/* Pass-throughs to the inner registry. */
int ca_tela_cb_registry_register_local(ca_tela_cb_registry_t *r,
                                       const ca_tel_tool_definition_t *def,
                                       ca_tel_local_tool_handler_fn handler,
                                       void *handler_ctx);
int ca_tela_cb_registry_register_webhook(ca_tela_cb_registry_t *r,
                                         const ca_tel_tool_definition_t *def,
                                         const char *webhook_url);

/* InvokeAsync(invocation, now, simulate_timeout):
 *   - if the breaker is Open at `now`: return the circuit-broken ToolResult
 *     (Succeeded=false) WITHOUT calling inner.
 *   - else if simulate_timeout: record a failure at `now`, return the timeout
 *     ToolResult, WITHOUT calling inner.
 *   - else: invoke inner; success -> RecordSuccess, non-success -> RecordFailure.
 * Result freshly owned (free with ca_tel_tool_result_free). NULL on bad args/OOM. */
ca_tel_tool_result_t *ca_tela_cb_registry_invoke(
    ca_tela_cb_registry_t *r, const ca_tel_tool_invocation_t *invocation,
    int64_t now_utc_ms, bool simulate_timeout);

/* ===========================================================================
 * Guardrails — pre-TTS phrase blocking.
 *
 * Regex is abstracted to a matcher fn per rule: match returns true if the rule
 * fires on `text`; redact writes a freshly-owned redacted copy into *out (only
 * called for Redact rules). A rule with a NULL matcher never fires. The two PII
 * commons ship deterministic C matchers; competitor matching is a literal
 * case-insensitive word scan built from the supplied names.
 * =========================================================================== */

typedef enum {
    CA_TELA_GUARD_REPLACE = 0,  /* block the whole turn -> fallback message */
    CA_TELA_GUARD_REDACT,       /* redact matched spans */
    CA_TELA_GUARD_WARN          /* pass through, flag only */
} ca_tela_guard_action_t;

/* matcher(ctx, text) -> did the rule fire?  */
typedef bool (*ca_tela_guard_match_fn)(void *ctx, const char *text);
/* redactor(ctx, text) -> freshly-owned redacted copy (NULL on OOM). Only used for
 * REDACT rules. */
typedef char *(*ca_tela_guard_redact_fn)(void *ctx, const char *text);

/* Outcome of running the guardrails on one draft (owns final_text + the triggered
 * name array). */
typedef struct {
    char  *final_text;       /* owned */
    bool   was_modified;
    bool   was_blocked;
    char **triggered_rules;  /* owned array of owned names (may be NULL when 0) */
    size_t triggered_count;
} ca_tela_guard_result_t;

void ca_tela_guard_result_free(ca_tela_guard_result_t *r);

typedef struct ca_tela_guardrails ca_tela_guardrails_t;

/* default_fallback NULL -> "I'm sorry, I can't help with that right now.". */
ca_tela_guardrails_t *ca_tela_guardrails_create(const char *default_fallback);
void ca_tela_guardrails_destroy(ca_tela_guardrails_t *g);

/* Add a rule. `name` required. For REPLACE, fallback_message (or NULL -> the
 * engine default) is spoken. For REDACT, `redactor` produces the redacted text
 * (NULL redactor -> matched text left as-is, i.e. a no-op redact that still flags).
 * `match` NULL -> the rule never fires. `ctx` borrowed (must outlive the engine).
 * Rules fire in add order. 0 / -1. */
int ca_tela_guardrails_add_rule(ca_tela_guardrails_t *g, const char *name,
                                ca_tela_guard_action_t action,
                                const char *fallback_message,
                                ca_tela_guard_match_fn match,
                                ca_tela_guard_redact_fn redactor, void *ctx);

/* Apply(draft) -> result (freshly owned; free with ca_tela_guard_result_free).
 * NULL/empty draft -> unmodified passthrough result. NULL only on OOM. */
ca_tela_guard_result_t *ca_tela_guardrails_apply(ca_tela_guardrails_t *g,
                                                 const char *draft);

/* ── CommonGuardrails — the built-in matchers/redactors ─────────────────────
 * These are stateless (ctx ignored); wire them directly into add_rule.        */

/* Credit-card: 13-19 digits (spaces/hyphens allowed) as a REDACT rule. */
bool  ca_tela_common_credit_card_match(void *ctx, const char *text);
char *ca_tela_common_credit_card_redact(void *ctx, const char *text);
/* SSN: ddd-dd-dddd as a REPLACE rule (match only; block). */
bool  ca_tela_common_ssn_match(void *ctx, const char *text);

/* Competitor matcher factory: builds an owned matcher context that fires when any
 * of `competitors` appears as a whole (ASCII) word, case-insensitive. Use with
 * action REPLACE. Free the returned ctx with ca_tela_common_competitor_free after
 * the guardrails engine is destroyed. NULL on OOM. */
typedef struct ca_tela_competitor_ctx ca_tela_competitor_ctx_t;
ca_tela_competitor_ctx_t *ca_tela_common_competitor_create(
    const char *const *competitors, size_t count);
void ca_tela_common_competitor_free(ca_tela_competitor_ctx_t *c);
bool ca_tela_common_competitor_match(void *ctx, const char *text);

/* ===========================================================================
 * ReassuranceFiller — phrase-rotation logic for the awkward-silence filler.
 *
 * The async run-with-filler loop is host-owned; the deterministic piece is the
 * vocabulary + round-robin selection.
 * =========================================================================== */

typedef struct ca_tela_reassurance ca_tela_reassurance_t;

/* Create with the default English vocabulary (4 short + 4 long). NULL on OOM. */
ca_tela_reassurance_t *ca_tela_reassurance_create_default(void);
/* Create with a custom vocabulary (copies the strings). Empty lists fall back to
 * "One moment." / "Almost there." like the C#. NULL on OOM. */
ca_tela_reassurance_t *ca_tela_reassurance_create(
    const char *const *short_fillers, size_t short_count,
    const char *const *long_fillers, size_t long_count);
void ca_tela_reassurance_destroy(ca_tela_reassurance_t *r);

/* Option accessors (defaults: short after 600ms, long every 3s). */
int64_t ca_tela_reassurance_short_after_ticks(const ca_tela_reassurance_t *r);
int64_t ca_tela_reassurance_long_every_ticks(const ca_tela_reassurance_t *r);
void    ca_tela_reassurance_set_short_after_ticks(ca_tela_reassurance_t *r, int64_t t);
void    ca_tela_reassurance_set_long_every_ticks(ca_tela_reassurance_t *r, int64_t t);

/* NextShort / NextLong — round-robin the pools (Interlocked.Increment parity).
 * Returns a borrowed pointer into the engine (valid until destroy). */
const char *ca_tela_reassurance_next_short(ca_tela_reassurance_t *r);
const char *ca_tela_reassurance_next_long(ca_tela_reassurance_t *r);

/* ===========================================================================
 * SpeculativeGenerator — branch bookkeeping for speculative decoding.
 *
 * The generator is a caller seam: given a transcript, produce a response string.
 * The ported logic is *which* transcript to (re)generate against and when to
 * reuse the in-flight draft. Deterministic + synchronous: Speculate stores the
 * partial + eagerly runs the generator to capture the draft; Commit reuses it
 * when the final equals the active partial, else regenerates.
 * =========================================================================== */

/* generator(ctx, transcript) -> freshly-owned response (NULL to model an error /
 * cancellation — treated as "no usable draft"). */
typedef char *(*ca_tela_generator_fn)(void *ctx, const char *transcript);

typedef struct ca_tela_speculator ca_tela_speculator_t;

/* min_partial_length<=0 -> 8. NULL on OOM. */
ca_tela_speculator_t *ca_tela_speculator_create(int min_partial_length);
void ca_tela_speculator_destroy(ca_tela_speculator_t *s);

/* The active branch's partial transcript (borrowed) or NULL when none. */
const char *ca_tela_speculator_active_partial(const ca_tela_speculator_t *s);

/* Speculate(partial): if whitespace or shorter than min, no-op. If the new partial
 * merely extends the active one (case-insensitive prefix), keep the active branch.
 * Otherwise start a fresh branch (runs `generator` now to capture the draft).
 * 0 on success (including the no-ops), -1 on OOM. */
int ca_tela_speculator_speculate(ca_tela_speculator_t *s, const char *partial,
                                 ca_tela_generator_fn generator, void *gen_ctx);

/* CommitAsync(final): whitespace -> "" . If the active partial equals the final
 * (case-insensitive) and a draft was captured, return the draft. Else regenerate
 * with the full final transcript. Freshly-owned result. NULL on OOM. */
char *ca_tela_speculator_commit(ca_tela_speculator_t *s, const char *final_transcript,
                                ca_tela_generator_fn generator, void *gen_ctx);

/* Abort — drop any active branch. */
void ca_tela_speculator_abort(ca_tela_speculator_t *s);

/* ===========================================================================
 * FalseInterruptionTracker — barge-in false-alarm counters.
 * =========================================================================== */

typedef struct {
    int64_t total_pause_events;
    int64_t confirmed_barge_ins;
    int64_t false_alarms;
    float   false_alarm_rate;
} ca_tela_interruption_stats_t;

typedef struct ca_tela_false_interruption_tracker ca_tela_false_interruption_tracker_t;

ca_tela_false_interruption_tracker_t *ca_tela_false_interruption_tracker_create(void);
void ca_tela_false_interruption_tracker_destroy(
    ca_tela_false_interruption_tracker_t *t);

/* Record one transition (keyed off its `to` state: Paused/Cancelled/Resumed). */
void ca_tela_false_interruption_tracker_record(
    ca_tela_false_interruption_tracker_t *t, const ca_tela_barge_transition_t *tr);
/* Record by state directly (convenience). */
void ca_tela_false_interruption_tracker_record_state(
    ca_tela_false_interruption_tracker_t *t, ca_tela_barge_state_t to);
ca_tela_interruption_stats_t ca_tela_false_interruption_tracker_stats(
    const ca_tela_false_interruption_tracker_t *t);
void ca_tela_false_interruption_tracker_reset(
    ca_tela_false_interruption_tracker_t *t);

/* ===========================================================================
 * HoldMusicMixer — loop background PCM-16, duck under speech.
 * =========================================================================== */

typedef struct ca_tela_hold_mixer ca_tela_hold_mixer_t;

/* background_loop copied (must be >=2 bytes / one sample). gains in [0,1]; pass
 * <0 to take the record defaults (0.6 background, 0.15 ducked). NULL on bad args
 * / OOM. */
ca_tela_hold_mixer_t *ca_tela_hold_mixer_create(const uint8_t *background_loop,
                                                size_t loop_len,
                                                float background_gain,
                                                float ducked_gain);
void ca_tela_hold_mixer_destroy(ca_tela_hold_mixer_t *m);
void ca_tela_hold_mixer_reset(ca_tela_hold_mixer_t *m);

/* MixFrame: mix `speech` (may be NULL/0 for plain background) over the looped
 * background into `dest`. When speech is present, dest must be >= speech_len and
 * that many bytes are produced; with no speech, dest_len bytes of background are
 * produced. Returns bytes written, or SIZE_MAX on bad args (dest<2, or dest shorter
 * than the speech frame). */
size_t ca_tela_hold_mixer_mix_frame(ca_tela_hold_mixer_t *m,
                                    const uint8_t *speech, size_t speech_len,
                                    uint8_t *dest, size_t dest_len);

/* ===========================================================================
 * WarmTransferOrchestrator — park→dial→brief→bridge→hangup over the carrier.
 *
 * TTS is a seam: tts(ctx, text) -> freshly-owned PCM bytes (NULL/len 0 = "no
 * audio", not an error). The carrier is a ca_tel_carrier_t (dial + the session's
 * transfer/hangup). No logger — failures surface via the result's reason.
 * =========================================================================== */

/* TTS seam: writes freshly-owned PCM into *out_pcm (may be NULL for empty) and its
 * length into *out_len; returns 0. Return -1 to model a synthesiser exception. */
typedef int (*ca_tela_tts_fn)(void *ctx, const char *text,
                              uint8_t **out_pcm, size_t *out_len);

typedef struct {
    bool  succeeded;
    char *failure_reason;               /* owned or NULL */
    ca_tel_call_session_t *bridge_session; /* owned by caller on success, else NULL */
} ca_tela_warm_transfer_result_t;

void ca_tela_warm_transfer_result_free(ca_tela_warm_transfer_result_t *r);

/* ExecuteAsync(source, target, briefing, bridge_stream_url, carrier, tts):
 * dials the target via `carrier`, speaks the briefing on the bridge leg, issues a
 * cold transfer of `source` to the target, hangs up the bridge leg. On success
 * result.bridge_session is the (now-hung-up) bridge leg the caller owns. On any
 * step failure, the bridge leg (if dialled) is hung up + destroyed and reason is
 * set. Returns a freshly-owned result (never NULL for valid args; NULL only on
 * OOM / NULL carrier|source|tts). */
ca_tela_warm_transfer_result_t *ca_tela_warm_transfer_execute(
    ca_tel_call_session_t *source, const char *target_number,
    const char *briefing_text, const char *bridge_stream_url,
    ca_tel_carrier_t *carrier, ca_tela_tts_fn tts, void *tts_ctx);

/* ===========================================================================
 * ConsultEscalation — walk human-expert channels until one answers.
 * =========================================================================== */

typedef struct {
    char *call_id;      /* owned */
    char *question;     /* owned */
    char *context_json; /* owned */
    char *urgency;      /* owned ("normal"/"high") */
} ca_tela_consult_request_t;

typedef struct {
    char *answer;      /* owned */
    bool  confidence;  /* true = expert-confirmed */
    char *notes;       /* owned or NULL */
} ca_tela_consult_answer_t;

void ca_tela_consult_answer_free(ca_tela_consult_answer_t *a);

/* Channel seam: ask(ctx, request, timeout_ticks) -> a freshly-owned answer via
 * *out (NULL when the channel declined / timed out) + returns 0; return -1 to model
 * the channel throwing (escalator swallows it and moves on). `name` (borrowed) is
 * used only for parity with the C# logging and channel identity. */
typedef int (*ca_tela_consult_ask_fn)(void *ctx,
                                      const ca_tela_consult_request_t *request,
                                      int64_t timeout_ticks,
                                      ca_tela_consult_answer_t **out);

typedef struct {
    const char            *name;  /* borrowed */
    ca_tela_consult_ask_fn ask;
    void                  *ctx;   /* borrowed */
} ca_tela_consult_channel_t;

typedef struct ca_tela_consult_escalator ca_tela_consult_escalator_t;

/* Copies the channel table (the fn/ctx/name pointers). NULL on bad args / OOM. */
ca_tela_consult_escalator_t *ca_tela_consult_escalator_create(
    const ca_tela_consult_channel_t *channels, size_t count);
void ca_tela_consult_escalator_destroy(ca_tela_consult_escalator_t *e);

/* EscalateAsync(request, timeout_per_channel): first channel to return a non-null
 * answer wins; a channel returning -1 is skipped. Writes a freshly-owned answer
 * into *out (NULL if none) and returns 0. Returns -1 on bad args. */
int ca_tela_consult_escalator_escalate(ca_tela_consult_escalator_t *e,
                                       const char *call_id, const char *question,
                                       const char *context_json, const char *urgency,
                                       int64_t timeout_per_channel_ticks,
                                       ca_tela_consult_answer_t **out);

/* ===========================================================================
 * AgentHandoff — swap AI persona mid-call.
 * =========================================================================== */

typedef struct {
    char *agent_id;      /* owned */
    char *display_name;  /* owned */
    char *system_prompt; /* owned */
    char *greeting_text; /* owned or NULL */
} ca_tela_call_agent_t;

void ca_tela_call_agent_free(ca_tela_call_agent_t *a);
ca_tela_call_agent_t *ca_tela_call_agent_copy(const ca_tela_call_agent_t *a);

typedef struct {
    bool  succeeded;
    char *failure_reason;             /* owned or NULL */
    ca_tela_call_agent_t *active_agent; /* owned or NULL */
} ca_tela_handoff_result_t;

void ca_tela_handoff_result_free(ca_tela_handoff_result_t *r);

typedef struct ca_tela_handoff ca_tela_handoff_t;

ca_tela_handoff_t *ca_tela_handoff_create(void);
void ca_tela_handoff_destroy(ca_tela_handoff_t *h);

/* CurrentAgent (freshly-owned copy or NULL). */
ca_tela_call_agent_t *ca_tela_handoff_current_agent(const ca_tela_handoff_t *h);

/* RegisterAgent — copies. agent_id required. 0 / -1. */
int ca_tela_handoff_register_agent(ca_tela_handoff_t *h, const char *agent_id,
                                   const char *display_name,
                                   const char *system_prompt,
                                   const char *greeting_text);
/* Catalog size + borrowed lookup by id (case-insensitive) or NULL. */
size_t ca_tela_handoff_agent_count(const ca_tela_handoff_t *h);
const ca_tela_call_agent_t *ca_tela_handoff_find_agent(const ca_tela_handoff_t *h,
                                                       const char *agent_id);

/* SetInitialAgent(agentId): sets current without a greeting. 0 / -1 (unknown id). */
int ca_tela_handoff_set_initial_agent(ca_tela_handoff_t *h, const char *agent_id);

/* HandoffAsync(session, targetAgentId, tts): switches current to the target and,
 * if it has a greeting, synthesises it and sends it on `session`. Same-agent
 * handoff is a success no-op. Unknown target -> failure result. Greeting/TTS
 * failure is swallowed (handoff still succeeds), matching the C#. Freshly-owned
 * result. NULL on bad args / OOM. */
ca_tela_handoff_result_t *ca_tela_handoff_handoff(
    ca_tela_handoff_t *h, ca_tel_call_session_t *session, const char *target_agent_id,
    ca_tela_tts_fn tts, void *tts_ctx);

/* ===========================================================================
 * LlmJudge — LLM-as-judge rubric scoring.
 * =========================================================================== */

typedef struct {
    char *name;        /* owned */
    char *description; /* owned */
} ca_tela_judge_dimension_t;

/* One score entry (name borrowed from the verdict's owned copy). */
typedef struct {
    char *name;   /* owned */
    int   score;  /* 0..10 */
} ca_tela_judge_score_t;

typedef struct {
    ca_tela_judge_score_t *scores;  /* owned array */
    size_t                 score_count;
    char                  *overall;   /* owned ("pass"/"borderline"/"fail") */
    char                  *reasoning; /* owned */
} ca_tela_judge_verdict_t;

void ca_tela_judge_verdict_free(ca_tela_judge_verdict_t *v);
/* Lookup a dimension's score (SIZE_MAX-safe): returns true + writes *out when found. */
bool ca_tela_judge_verdict_score(const ca_tela_judge_verdict_t *v, const char *name,
                                 int *out_score);

/* Completion seam: complete(ctx, prompt) -> freshly-owned raw model text (NULL to
 * model an error — parse then yields the borderline fallback verdict). */
typedef char *(*ca_tela_judge_completion_fn)(void *ctx, const char *prompt);

/* Build the rubric prompt for (user, assistant, dims). Freshly-owned. NULL on OOM. */
char *ca_tela_judge_build_prompt(const char *user_utterance,
                                 const char *assistant_response,
                                 const ca_tela_judge_dimension_t *dims,
                                 size_t dim_count);

/* JudgeAsync: build the prompt, call `completion`, parse the verdict. On a NULL
 * completion result or unparseable JSON -> every dim scored 0, overall
 * "borderline", reasoning "Judge response could not be parsed.". Freshly-owned.
 * NULL on bad args / OOM. */
ca_tela_judge_verdict_t *ca_tela_judge_run(const char *user_utterance,
                                           const char *assistant_response,
                                           const ca_tela_judge_dimension_t *dims,
                                           size_t dim_count,
                                           ca_tela_judge_completion_fn completion,
                                           void *completion_ctx);

/* ===========================================================================
 * EvalSession — scripted-conversation harness.
 *
 * Turn handler seam: handle(ctx, user_transcript, &out_response, &elapsed_ticks).
 * The handler measures its own latency (returns it) so the port stays
 * deterministic. Return 0 with an owned *out_response; -1 to abort the run.
 * =========================================================================== */

typedef int (*ca_tela_eval_turn_fn)(void *ctx, const char *user_transcript,
                                    char **out_response, int64_t *out_elapsed_ticks);

typedef struct {
    char    *assistant_response; /* owned */
    char   **missing_keywords;   /* owned array of owned (may be NULL) */
    size_t   missing_count;
    int64_t  latency_ticks;
} ca_tela_eval_turn_result_t;

typedef struct {
    ca_tela_eval_turn_result_t *turns;  /* owned array */
    size_t                      turn_count;
    bool                        all_keywords_hit;
    int64_t                     total_latency_ticks;
} ca_tela_eval_run_result_t;

void ca_tela_eval_run_result_free(ca_tela_eval_run_result_t *r);

/* One scripted turn: transcript + optional expected keywords (borrowed). */
typedef struct {
    const char        *user_transcript;
    const char *const *expected_keywords; /* borrowed or NULL */
    size_t             expected_count;
} ca_tela_eval_turn_t;

/* RunAsync(script[]): run each turn through `handler`, collect responses + missing
 * keywords (case-insensitive substring). Freshly-owned result. NULL on bad args /
 * OOM / a handler returning -1. */
ca_tela_eval_run_result_t *ca_tela_eval_run(const ca_tela_eval_turn_t *script,
                                            size_t turn_count,
                                            ca_tela_eval_turn_fn handler, void *ctx);

/* ===========================================================================
 * SentenceChunker — streaming sentence splitter.
 * =========================================================================== */

typedef struct ca_tela_chunker ca_tela_chunker_t;

/* min_sentence_length<=0 -> 4. NULL on OOM. */
ca_tela_chunker_t *ca_tela_chunker_create(int min_sentence_length);
void ca_tela_chunker_destroy(ca_tela_chunker_t *c);

/* PushToken(token) -> any complete sentences now ready, as a freshly-owned array
 * of owned strings via *out (NULL when none) + count via return. Empty/NULL token
 * -> 0. Returns SIZE_MAX on OOM. */
size_t ca_tela_chunker_push_token(ca_tela_chunker_t *c, const char *token,
                                  char ***out);
/* Flush -> whatever remains buffered (freshly owned, may be ""). NULL on OOM. */
char *ca_tela_chunker_flush(ca_tela_chunker_t *c);

/* ===========================================================================
 * LatencyTracker — sliding-window per-stage percentiles.
 * =========================================================================== */

/* Stable stage-name constants (parity with LatencyStage). */
extern const char *const CA_TELA_STAGE_ASR_FIRST_WORD;
extern const char *const CA_TELA_STAGE_ASR_FINAL;
extern const char *const CA_TELA_STAGE_LLM_FIRST_TOKEN;
extern const char *const CA_TELA_STAGE_LLM_FULL_RESPONSE;
extern const char *const CA_TELA_STAGE_TTS_FIRST_AUDIO;
extern const char *const CA_TELA_STAGE_TTS_FULL_AUDIO;
extern const char *const CA_TELA_STAGE_END_TO_END;

typedef struct {
    char   *stage;   /* owned */
    int     samples;
    int64_t min_ticks;
    int64_t p50_ticks;
    int64_t p95_ticks;
    int64_t p99_ticks;
    int64_t max_ticks;
} ca_tela_latency_snapshot_t;

void ca_tela_latency_snapshot_free(ca_tela_latency_snapshot_t *s);
void ca_tela_latency_snapshot_free_array(ca_tela_latency_snapshot_t *arr, size_t n);

typedef struct ca_tela_latency_tracker ca_tela_latency_tracker_t;

/* window_size<=0 -> 256 (the C# throws; we clamp to the record default). NULL on
 * OOM. */
ca_tela_latency_tracker_t *ca_tela_latency_tracker_create(int window_size);
void ca_tela_latency_tracker_destroy(ca_tela_latency_tracker_t *t);

/* Record(stage, ticks): negative ticks ignored; blank stage ignored. */
void ca_tela_latency_tracker_record(ca_tela_latency_tracker_t *t, const char *stage,
                                    int64_t latency_ticks);

/* Snapshot(stage) -> freshly-owned snapshot (free with
 * ca_tela_latency_snapshot_free) or NULL when the stage is unknown/empty. */
ca_tela_latency_snapshot_t *ca_tela_latency_tracker_snapshot(
    const ca_tela_latency_tracker_t *t, const char *stage);
/* SnapshotAll -> owned array + count via *out_count. NULL when none. */
ca_tela_latency_snapshot_t *ca_tela_latency_tracker_snapshot_all(
    const ca_tela_latency_tracker_t *t, size_t *out_count);
void ca_tela_latency_tracker_reset(ca_tela_latency_tracker_t *t, const char *stage);
void ca_tela_latency_tracker_reset_all(ca_tela_latency_tracker_t *t);

/* ===========================================================================
 * DashboardData — the dashboard row/summary/snapshot value types + composed
 * source. The C# Func<> feeds become caller-supplied row arrays.
 * =========================================================================== */

typedef struct {
    char                *call_id;   /* owned */
    char                *carrier;   /* owned */
    char                *from;      /* owned */
    char                *to;        /* owned */
    ca_tel_call_status_t status;
    int64_t              started_at_utc_ms;
    int64_t              duration_ticks;
    ca_tel_decimal_t     cost_so_far;
} ca_tela_live_call_row_t;

typedef struct {
    char                *call_id;      /* owned */
    char                *carrier;      /* owned */
    char                *from;         /* owned */
    char                *to;           /* owned */
    ca_tel_call_status_t final_status;
    int64_t              ended_at_utc_ms;
    int64_t              duration_ticks;
    ca_tel_decimal_t     total_cost;
} ca_tela_recent_call_row_t;

typedef struct {
    char *agent_label;         /* owned */
    char *health;              /* owned ("Healthy"/"Degraded"/"CoolingDown") */
    int   consecutive_failures;
} ca_tela_agent_health_row_t;

typedef struct {
    int              live_call_count;
    ca_tel_decimal_t current_spend_usd;
    int              calls_last_24h;
    float            pause_false_alarm_rate;
} ca_tela_dashboard_summary_t;

/* Full snapshot — owns all four row arrays + the latency-snapshot array. */
typedef struct {
    ca_tela_dashboard_summary_t summary;
    ca_tela_live_call_row_t    *live_calls;
    size_t                      live_count;
    ca_tela_recent_call_row_t  *recent_calls;
    size_t                      recent_count;
    ca_tela_agent_health_row_t *agent_health;
    size_t                      agent_count;
    ca_tela_latency_snapshot_t *latency_by_stage;
    size_t                      latency_count;
} ca_tela_dashboard_snapshot_t;

void ca_tela_dashboard_snapshot_free(ca_tela_dashboard_snapshot_t *s);

/* Build a snapshot from caller arrays (all deep-copied). Any array may be NULL
 * when its count is 0. Freshly owned. NULL on OOM. */
ca_tela_dashboard_snapshot_t *ca_tela_dashboard_snapshot_build(
    ca_tela_dashboard_summary_t summary,
    const ca_tela_live_call_row_t *live, size_t live_count,
    const ca_tela_recent_call_row_t *recent, size_t recent_count,
    const ca_tela_agent_health_row_t *health, size_t health_count,
    const ca_tela_latency_snapshot_t *latency, size_t latency_count);

/* ===========================================================================
 * FirstMessagePreamble — the race+render decision.
 *
 * The C# races modelReady vs a latency window. The ported decision is a pure
 * predicate: given whether the model became ready within the window, decide
 * whether to speak the preamble, and if so render + synthesise + send it.
 * =========================================================================== */

typedef struct ca_tela_preamble ca_tela_preamble_t;

/* template required; max_latency_ticks<=0 -> 250ms. The resolver is a
 * PromptVariableResolver (borrowed; may be NULL -> the template renders with
 * {{vars}} left as the resolver's default-missing = ""). NULL on bad args / OOM. */
ca_tela_preamble_t *ca_tela_preamble_create(const char *template_text,
                                            int64_t max_latency_ticks);
void ca_tela_preamble_destroy(ca_tela_preamble_t *p);
int64_t ca_tela_preamble_max_latency_ticks(const ca_tela_preamble_t *p);

/* SpeakAsync decision: model_ready_within_window mirrors "modelReady won the race
 * AND completed successfully" — when true, skip (return 0, spoke nothing). Else
 * render the template via `resolver` (may be NULL), synthesise via `tts`, and send
 * on `session`. Returns 1 if the preamble was spoken, 0 if skipped (either the
 * race, an empty render, or empty audio), -1 on error (bad args / TTS/-send
 * failure). */
int ca_tela_preamble_speak(ca_tela_preamble_t *p, ca_tel_call_session_t *session,
                           struct ca_tela_prompt_resolver *resolver,
                           bool model_ready_within_window,
                           ca_tela_tts_fn tts, void *tts_ctx);

/* ===========================================================================
 * StereoCallRecorder — interleave caller(L)/agent(R) PCM-16 to a stereo WAV.
 *
 * Ports the on-disk Stream to a growable in-memory byte buffer; the 44-byte
 * header is reserved on first write and backfilled at finalize (CanSeek==true
 * always for the in-memory buffer).
 * =========================================================================== */

typedef struct ca_tela_stereo_recorder ca_tela_stereo_recorder_t;

/* sample_rate_hz>0 required. NULL on bad args / OOM. */
ca_tela_stereo_recorder_t *ca_tela_stereo_recorder_create(int sample_rate_hz);
void ca_tela_stereo_recorder_destroy(ca_tela_stereo_recorder_t *r);

/* WriteCallerFrame / WriteAgentFrame: PCM-16 mono; <2 bytes ignored. 0 / -1 (OOM
 * or already finalized). */
int ca_tela_stereo_recorder_write_caller(ca_tela_stereo_recorder_t *r,
                                         const uint8_t *pcm, size_t len);
int ca_tela_stereo_recorder_write_agent(ca_tela_stereo_recorder_t *r,
                                        const uint8_t *pcm, size_t len);
/* Finalize — backfill the WAV header. Idempotent. */
void ca_tela_stereo_recorder_finalize(ca_tela_stereo_recorder_t *r);

/* Borrowed view of the WAV bytes so far (finalize first for a valid header).
 * *out_len set. */
const uint8_t *ca_tela_stereo_recorder_data(const ca_tela_stereo_recorder_t *r,
                                            size_t *out_len);

/* ===========================================================================
 * AnsweringMachineDetector — frame-by-frame heuristic AMD.
 * =========================================================================== */

typedef enum {
    CA_TELA_AMD_UNKNOWN = 0,
    CA_TELA_AMD_HUMAN,
    CA_TELA_AMD_ANSWERING_MACHINE
} ca_tela_amd_verdict_t;

typedef struct ca_tela_amd ca_tela_amd_t;

/* Any threshold <=0 takes its record default (1800 / 300 / 3500 / 250 ms). NULL
 * on OOM. */
ca_tela_amd_t *ca_tela_amd_create(int human_max_first_ms, int human_min_first_ms,
                                  int max_observation_ms, int silence_threshold_ms);
void ca_tela_amd_destroy(ca_tela_amd_t *a);

ca_tela_amd_verdict_t ca_tela_amd_current(const ca_tela_amd_t *a);
/* Observe(pcmFrame, sampleRateHz): feed one PCM-16 mono frame; returns the updated
 * verdict. sampleRateHz<=0 -> current (the C# throws; we keep it non-fatal via the
 * bad-arg guard returning current). <2 bytes -> current. */
ca_tela_amd_verdict_t ca_tela_amd_observe(ca_tela_amd_t *a, const uint8_t *pcm,
                                          size_t len, int sample_rate_hz);
void ca_tela_amd_reset(ca_tela_amd_t *a);

/* ===========================================================================
 * VoiceLoopTelemetry — span-name constants + a tiny in-memory span model.
 *
 * No OpenTelemetry dependency; a span is a name + tags + status the host can
 * inspect/export. StartTurn/Asr/Llm/Tts create a span with the C#'s tags;
 * RecordOutcome tags success/failure + status.
 * =========================================================================== */

extern const char *const CA_TELA_TELEMETRY_SOURCE_NAME; /* "CircleAI.Telephony.VoiceLoop" */

typedef enum {
    CA_TELA_SPAN_STATUS_UNSET = 0,
    CA_TELA_SPAN_STATUS_OK,
    CA_TELA_SPAN_STATUS_ERROR
} ca_tela_span_status_t;

typedef struct {
    char *key;   /* owned */
    char *value; /* owned or NULL */
} ca_tela_span_tag_t;

typedef struct ca_tela_span ca_tela_span_t;

/* Start spans (names: voice_loop.turn / .asr / .llm / .tts). NULL on OOM. */
ca_tela_span_t *ca_tela_telemetry_start_turn(const char *call_id);
ca_tela_span_t *ca_tela_telemetry_start_asr(const char *backend);
ca_tela_span_t *ca_tela_telemetry_start_llm(const char *provider, const char *model);
ca_tela_span_t *ca_tela_telemetry_start_tts(const char *backend, const char *voice_id);
void ca_tela_span_destroy(ca_tela_span_t *s);

const char *ca_tela_span_name(const ca_tela_span_t *s);
ca_tela_span_status_t ca_tela_span_status(const ca_tela_span_t *s);
size_t ca_tela_span_tag_count(const ca_tela_span_t *s);
/* Borrowed tag value by key, or NULL. */
const char *ca_tela_span_tag(const ca_tela_span_t *s, const char *key);

/* RecordOutcome(span, success, errorReason): tags outcome + status (and
 * error.message when failing with a reason). */
void ca_tela_telemetry_record_outcome(ca_tela_span_t *s, bool success,
                                      const char *error_reason);

/* ===========================================================================
 * StreamingToolProgress — progress updates + throttled/recording sinks.
 * =========================================================================== */

typedef struct {
    char   *call_id;         /* owned */
    float   percent_complete;
    char   *status_text;     /* owned or NULL */
    int64_t emitted_at_utc_ms;
} ca_tela_tool_progress_t;

void ca_tela_tool_progress_free(ca_tela_tool_progress_t *u);

/* ── RecordingToolProgressSink ─────────────────────────────────────────────
 * Buffers every update for observability (no speaking).                       */

typedef struct ca_tela_recording_sink ca_tela_recording_sink_t;

ca_tela_recording_sink_t *ca_tela_recording_sink_create(void);
void ca_tela_recording_sink_destroy(ca_tela_recording_sink_t *s);
/* Emit — buffers a copy. 0 / -1. */
int ca_tela_recording_sink_emit(ca_tela_recording_sink_t *s,
                                const ca_tela_tool_progress_t *update);
size_t ca_tela_recording_sink_count(const ca_tela_recording_sink_t *s);
/* Updates — freshly-owned array of copies via *out_count. NULL when none. Free
 * each with ca_tela_tool_progress_free then free the array. */
ca_tela_tool_progress_t *ca_tela_recording_sink_updates(
    const ca_tela_recording_sink_t *s, size_t *out_count);

/* ── SpokenToolProgressSink ────────────────────────────────────────────────
 * Throttles updates (>= min_interval apart) and speaks each via TTS to a
 * session. `now` is caller-supplied per emit for deterministic throttling.    */

typedef struct ca_tela_spoken_sink ca_tela_spoken_sink_t;

/* min_interval_ticks<=0 -> 2s. session + tts borrowed. NULL on bad args / OOM. */
ca_tela_spoken_sink_t *ca_tela_spoken_sink_create(ca_tel_call_session_t *session,
                                                  ca_tela_tts_fn tts, void *tts_ctx,
                                                  int64_t min_interval_ticks);
void ca_tela_spoken_sink_destroy(ca_tela_spoken_sink_t *s);
/* Emit(update, now): blank status_text -> no-op success. If >= min_interval since
 * the last spoken update, synthesise + send + advance the clock. Returns 1 if
 * spoken, 0 if throttled/blank, -1 on error. */
int ca_tela_spoken_sink_emit(ca_tela_spoken_sink_t *s,
                             const ca_tela_tool_progress_t *update, int64_t now_utc_ms);

/* ── StreamingToolRunner ───────────────────────────────────────────────────
 * Runs a streaming handler against a sink and folds the outcome into a
 * ToolResult. The handler seam receives the recording sink (the deterministic
 * one) so any updates it emits are captured.                                  */

/* streaming handler: handle(ctx, argumentsJson, recording_sink, &out_result) -> 0
 * with an owned result JSON; -1 to model a thrown exception (runner yields
 * Succeeded=false with a generic message). */
typedef int (*ca_tela_streaming_tool_fn)(void *ctx, const char *arguments_json,
                                         ca_tela_recording_sink_t *sink,
                                         char **out_result);

/* RunAsync(invocation, handler, sink): freshly-owned ToolResult (free with
 * ca_tel_tool_result_free). NULL on bad args / OOM. */
ca_tel_tool_result_t *ca_tela_streaming_tool_run(
    const ca_tel_tool_invocation_t *invocation, ca_tela_streaming_tool_fn handler,
    void *ctx, ca_tela_recording_sink_t *sink);

/* ===========================================================================
 * VoiceLoopAsTool — expose the voice loop as a callable tool.
 * =========================================================================== */

typedef struct {
    char   *to_number;             /* owned */
    char   *goal;                  /* owned */
    char   *context_json;          /* owned or NULL */
    char   *system_prompt;         /* owned or NULL */
    int64_t max_duration_ticks;    /* 0 -> use the driver default */
} ca_tela_voiceloop_request_t;

typedef struct {
    bool    goal_achieved;
    char   *summary;               /* owned */
    char   *call_id;               /* owned */
    int64_t duration_ticks;
    char   *transcript;            /* owned */
    char   *structured_output_json;/* owned or NULL */
} ca_tela_voiceloop_result_t;

void ca_tela_voiceloop_result_free(ca_tela_voiceloop_result_t *r);

/* Runner seam: run(ctx, request, &out_result) -> 0 with an owned result; -1 to
 * model the timeout path (driver returns the "timed out" result). */
typedef int (*ca_tela_voiceloop_runner_fn)(void *ctx,
                                           const ca_tela_voiceloop_request_t *request,
                                           ca_tela_voiceloop_result_t **out_result);

/* The tool descriptor (parity with Descriptor). Freshly-owned; free with
 * ca_tel_tool_definition_free. */
ca_tel_tool_definition_t ca_tela_voiceloop_descriptor(void);

/* InvokeAsync(request, runner, default_max_duration_ticks): validates to_number +
 * goal (both required), computes the effective max-duration (request or default,
 * default<=0 -> 5min), runs. If the runner returns -1 (timeout) -> a "Call timed
 * out after N minutes." result. Freshly-owned result. NULL on bad args / OOM. */
ca_tela_voiceloop_result_t *ca_tela_voiceloop_invoke(
    const ca_tela_voiceloop_request_t *request, ca_tela_voiceloop_runner_fn runner,
    void *runner_ctx, int64_t default_max_duration_ticks);

/* ===========================================================================
 * PromptVariableResolver — {{var}} substitution.
 * =========================================================================== */

/* Provider seam: provide(ctx, name, &out) -> 0 with an owned value (or *out NULL
 * to mean "resolved to null" -> default-missing); -1 is treated as "no value" too.
 */
typedef int (*ca_tela_prompt_provider_fn)(void *ctx, const char *name, char **out);

typedef struct ca_tela_prompt_resolver ca_tela_prompt_resolver_t;

/* default_missing NULL -> "". NULL on OOM. */
ca_tela_prompt_resolver_t *ca_tela_prompt_resolver_create(const char *default_missing);
void ca_tela_prompt_resolver_destroy(ca_tela_prompt_resolver_t *r);

/* Set(name, value) — static (case-insensitive; last wins). name required. 0 / -1. */
int ca_tela_prompt_resolver_set(ca_tela_prompt_resolver_t *r, const char *name,
                                const char *value);
/* SetProvider(name, provider, ctx) — dynamic. name + provider required. `ctx`
 * borrowed. 0 / -1. */
int ca_tela_prompt_resolver_set_provider(ca_tela_prompt_resolver_t *r,
                                         const char *name,
                                         ca_tela_prompt_provider_fn provider,
                                         void *ctx);
/* RenderAsync(template) -> freshly-owned rendered string. Empty template -> "".
 * Unknown {{var}} -> default-missing. NULL on OOM. */
char *ca_tela_prompt_resolver_render(ca_tela_prompt_resolver_t *r,
                                     const char *template_text);

/* ===========================================================================
 * LocalDevTunnel — public-URL resolver shapes over a resolver seam.
 *
 * The HTTP/tunnel boundary is the resolver fn: resolve(ctx, localPort, &out_url)
 * -> 0 with an owned absolute URL; -1 to model a failure. Null/Static short-circuit
 * without the seam.
 * =========================================================================== */

typedef int (*ca_tela_tunnel_resolve_fn)(void *ctx, int local_port, char **out_url);

typedef struct ca_tela_tunnel ca_tela_tunnel_t;

/* NullLocalDevTunnel — ProviderId "null", IsAvailable false, GetPublicUrl fails. */
ca_tela_tunnel_t *ca_tela_tunnel_create_null(void);
/* StaticLocalDevTunnel(publicUrl) — must be absolute (contains "://"). NULL on bad
 * args / OOM. ProviderId "static". */
ca_tela_tunnel_t *ca_tela_tunnel_create_static(const char *public_url);
/* Cloudflare / Ngrok — over the resolver seam. `resolver` required. ProviderId
 * "cloudflare"/"ngrok". NULL on bad args / OOM. */
ca_tela_tunnel_t *ca_tela_tunnel_create_cloudflare(ca_tela_tunnel_resolve_fn resolver,
                                                   void *ctx);
ca_tela_tunnel_t *ca_tela_tunnel_create_ngrok(ca_tela_tunnel_resolve_fn resolver,
                                              void *ctx);
void ca_tela_tunnel_destroy(ca_tela_tunnel_t *t);

const char *ca_tela_tunnel_provider_id(const ca_tela_tunnel_t *t);
bool ca_tela_tunnel_is_available(const ca_tela_tunnel_t *t);
/* GetPublicUrlAsync(localPort) -> freshly-owned URL via *out + returns 0; -1 on
 * failure (null tunnel, or the resolver failing). */
int ca_tela_tunnel_get_public_url(ca_tela_tunnel_t *t, int local_port, char **out);

/* ===========================================================================
 * McpToolImporter — register MCP tools/list results as webhook tools.
 *
 * The HTTP fetch is the caller's job (the seam): the host performs tools/list and
 * hands the raw JSON response body here; we parse result.tools[] and register each
 * as a webhook tool (URL = endpoint + "?remote_tool=<name>", name optionally
 * prefixed). Mirrors HttpMcpToolImporter's parse + register.
 * =========================================================================== */

/* Parse `tools_list_json` (an MCP JSON-RPC tools/list response body) and register
 * each tool into `registry` as a webhook whose URL forwards to `server_endpoint`
 * (query "remote_tool=<original name>"). `tool_name_prefix` (may be NULL) is
 * prepended to the registered name. Writes the imported ToolDefinitions into *out
 * (owned array; free each with ca_tel_tool_definition_free then the array) and
 * returns the count, or SIZE_MAX on bad args / OOM. A malformed / result-less body
 * imports nothing (count 0, *out NULL). */
size_t ca_tela_mcp_import(ca_tel_tool_registry_t *registry,
                          const char *server_endpoint, const char *tool_name_prefix,
                          const char *tools_list_json,
                          ca_tel_tool_definition_t **out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_TELEPHONY_AGENT_H */
