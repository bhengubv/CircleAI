#ifndef CIRCLE_AI_TELEPHONY_CALL_H
#define CIRCLE_AI_TELEPHONY_CALL_H

/*
 * telephony_call.h - CircleAI.Telephony (C11): the machinery of a live call.
 *
 * An assistant on a phone line has constraints nothing else in this codebase
 * has. There is no screen, so the only interface is what was just said. There is
 * no scrollback, so a mistake cannot be re-read. And there is a person waiting
 * in real time, so every millisecond between the end of their sentence and the
 * start of the reply is a millisecond they spend wondering if the line dropped.
 *
 * Nearly everything here exists to serve that last fact: chunk sentences so
 * speech can start before generation finishes, speak a filler while a tool runs,
 * speculate on the likely answer, detect an answering machine before wasting
 * thirty seconds on it, and notice when an IVR has us going in circles.
 *
 * MONEY IS IN MICRO-UNITS AS INTEGERS. A call costs fractions of a cent and the
 * total is summed over thousands of calls; float money is how a total stops
 * matching the sum of its parts.
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

/* -- primitives ----------------------------------------------------------- */

/* What the carrier hands over and expects back. PCM16 is the only format any
 * of the maths here operates on; the others are named so a bridge can say what
 * it is doing rather than passing an untyped buffer. */
typedef enum {
    CA_CALL_MEDIA_PCM16 = 0,
    CA_CALL_MEDIA_MULAW,
    CA_CALL_MEDIA_ALAW,
    CA_CALL_MEDIA_OPUS
} ca_call_media_format_t;

const char *ca_call_media_format_name(ca_call_media_format_t format);
int ca_call_media_format_default_sample_rate(ca_call_media_format_t format);

typedef struct {
    char *to_e164;
    char *from_e164;
    /* Seconds. Zero means the carrier's default, which is not the same as "no
     * limit" - an unbounded outbound call is an unbounded bill. */
    int ring_timeout_seconds;
    bool record;
    bool detect_answering_machine;
    char *caller_id_name;
} ca_outbound_dial_options_t;

void ca_outbound_dial_options_free(ca_outbound_dial_options_t *options);

/* -- the carrier seam ----------------------------------------------------- */

typedef struct ca_telephony_carrier {
    void *state;
    const char *(*backend_id)(void *state);
    /* Returns a call id, or NULL. Caller frees. */
    char *(*dial)(void *state, const ca_outbound_dial_options_t *options);
    bool (*hangup)(void *state, const char *call_id);
    bool (*send_audio)(void *state, const char *call_id,
                       const uint8_t *pcm, size_t len);
    void (*free_fn)(void *state);
} ca_telephony_carrier_t;

void ca_telephony_carrier_free(ca_telephony_carrier_t *carrier);

/* Backend id "null": dials nobody, answers nothing. The default, so that a host
 * with no carrier wired gets a call that never connects rather than a crash -
 * and so that no test can accidentally place a real call. */
ca_telephony_carrier_t *ca_null_telephony_carrier_new(void);

typedef struct ca_inbound_call_dispatcher {
    void *state;
    const char *(*backend_id)(void *state);
    /* Called for each inbound call. Returning false rejects it. */
    bool (*dispatch)(void *state, const char *call_id, const char *from_e164);
    void (*free_fn)(void *state);
} ca_inbound_call_dispatcher_t;

void ca_inbound_call_dispatcher_free(ca_inbound_call_dispatcher_t *dispatcher);
ca_inbound_call_dispatcher_t *ca_null_inbound_call_dispatcher_new(void);

/* Sending DTMF through the audio path rather than out of band. Split out
 * because carriers disagree about whether they support signalled DTMF at all,
 * and a call that silently fails to press "2" looks like an IVR that ignored
 * us. */
typedef struct ca_dtmf_sendable {
    void *state;
    bool (*send_digits)(void *state, const char *digits);
    void (*free_fn)(void *state);
} ca_dtmf_sendable_t;

void ca_dtmf_sendable_free(ca_dtmf_sendable_t *sendable);

/* -- DTMF generation ------------------------------------------------------ */

/*
 * The dual-tone pair for a digit, in hertz. False for anything that is not
 * 0-9, A-D, * or #.
 *
 * The grid is fixed by ITU Q.23 and is not ours to tune: low row 697/770/852/941
 * against high column 1209/1336/1477/1633. The frequencies were chosen so that
 * no tone is a harmonic of another, which is what lets a receiver pick them out
 * of speech - and it is why a "close enough" table does not work.
 */
bool ca_dtmf_frequencies(char digit, int *out_low_hz, int *out_high_hz);

/*
 * One digit as PCM-16 mono little-endian. Caller frees; *out_len is bytes.
 *
 * The two sines are summed and HALVED, not just added: two full-amplitude tones
 * sum to twice full scale and clip, and a clipped DTMF tone carries harmonics
 * that a strict receiver rejects.
 */
uint8_t *ca_dtmf_tone_generator_digit(char digit, int sample_rate_hz, int duration_ms,
                               float amplitude, size_t *out_len);

/* A whole string with silence between digits. The inter-digit gap is not
 * cosmetic - without it a receiver reads "11" as one long 1. */
uint8_t *ca_dtmf_tone_generator_sequence(const char *digits, int sample_rate_hz,
                                        int tone_duration_ms, int inter_digit_gap_ms,
                                        float amplitude, size_t *out_len);

/* -- answering-machine detection ------------------------------------------ */

typedef enum {
    CA_AMD_UNKNOWN = 0,
    CA_AMD_HUMAN,
    CA_AMD_ANSWERING_MACHINE
} ca_amd_verdict_t;

const char *ca_amd_verdict_name(ca_amd_verdict_t verdict);

/*
 * Thresholds, all in milliseconds.
 *
 * The whole heuristic rests on one observation: a person answering says two
 * words and stops, a machine plays a greeting. So it is the LENGTH OF THE FIRST
 * CONTIGUOUS BURST that separates them, not its content - which means this runs
 * on the frames already arriving, with no model and no carrier fee.
 */
typedef struct {
    /* Longer than this and it is a greeting, not a hello. */
    int human_max_first_utterance_ms;   /* 1800 */
    /* Shorter than this is not enough to decide - a click, a breath. */
    int human_min_first_utterance_ms;   /* 300 */
    /* Stop accumulating. An undecided call is answered as a human, because
     * hanging up on a person is worse than talking to a machine. */
    int max_observation_window_ms;      /* 3500 */
    int silence_frame_threshold_ms;     /* 250 */
} ca_amd_options_t;

ca_amd_options_t ca_amd_options_default(void);

typedef struct ca_answering_machine_detector ca_answering_machine_detector_t;

ca_answering_machine_detector_t *ca_answering_machine_detector_new(
    const ca_amd_options_t *options);

void ca_answering_machine_detector_free(ca_answering_machine_detector_t *detector);

/* Feed one PCM-16 mono frame; returns the verdict so far. Once it settles it
 * STAYS settled: a detector that changes its mind mid-greeting produces a call
 * that starts talking over the beep. */
ca_amd_verdict_t ca_answering_machine_detector_observe(
    ca_answering_machine_detector_t *detector, const uint8_t *pcm_frame,
    size_t len, int sample_rate_hz);

ca_amd_verdict_t ca_answering_machine_detector_verdict(
    const ca_answering_machine_detector_t *detector);

/* -- IVR loop detection --------------------------------------------------- */

typedef struct {
    char *speech;        /* what the IVR said */
    char *dtmf_pressed;  /* what we sent back, or NULL */
    int64_t at_unix;
} ca_ivr_round_t;

void ca_ivr_round_free(ca_ivr_round_t *round);

typedef struct {
    bool is_looping;
    /* How long the repeating cycle is, in rounds. Reported because "stuck" and
     * "stuck bouncing between two menus" want different recoveries. */
    int loop_length;
    char *reason;
} ca_ivr_loop_verdict_t;

void ca_ivr_loop_verdict_free(ca_ivr_loop_verdict_t *verdict);

typedef struct ca_ivr_loop_detector ca_ivr_loop_detector_t;

/* Defaults: 32 rounds tracked, 2 repeats to call it a loop, 0.85 similarity.
 *
 * Similarity rather than equality because an IVR rarely repeats itself
 * byte-for-byte - the transcript differs by a word, a number, a filler - and an
 * exact-match detector never fires on the real thing. */
ca_ivr_loop_detector_t *ca_ivr_loop_detector_new(int max_rounds_to_track,
                                                 int min_rounds_for_loop,
                                                 double similarity_threshold);

void ca_ivr_loop_detector_free(ca_ivr_loop_detector_t *detector);

bool ca_ivr_loop_detector_observe(ca_ivr_loop_detector_t *detector,
                                  const ca_ivr_round_t *round,
                                  ca_ivr_loop_verdict_t *out_verdict);

bool ca_ivr_loop_detector_current_verdict(const ca_ivr_loop_detector_t *detector,
                                          ca_ivr_loop_verdict_t *out_verdict);

void ca_ivr_loop_detector_reset(ca_ivr_loop_detector_t *detector);

/* -- sentence chunking ---------------------------------------------------- */

typedef struct ca_sentence_chunker ca_sentence_chunker_t;

/*
 * Emits whole sentences from a token stream so speech can start before the
 * model has finished. This is the single largest win on time-to-first-audio.
 *
 * `min_sentence_length` (default 4) is what stops "Mr." and "1." becoming
 * sentences. Terminal punctuation includes the FULLWIDTH forms - a Japanese or
 * Chinese reply ends in U+3002, and a chunker that only knows "." never emits
 * anything until the flush.
 */
ca_sentence_chunker_t *ca_sentence_chunker_new(int min_sentence_length);
void ca_sentence_chunker_free(ca_sentence_chunker_t *chunker);

/* Push a token; returns a heap array of *out_count complete sentences (often
 * zero). Caller frees the array and each string. */
char **ca_sentence_chunker_push_token(ca_sentence_chunker_t *chunker,
                                      const char *token, size_t *out_count);

/* Whatever is left, punctuated or not. A reply that ends without a full stop
 * must still be spoken. Caller frees. */
char *ca_sentence_chunker_flush(ca_sentence_chunker_t *chunker);

/* -- what a call costs ---------------------------------------------------- */

typedef struct {
    /* All micro-units of the billing currency. */
    int64_t carrier_per_minute_micro;
    int64_t stt_per_minute_micro;
    int64_t tts_per_thousand_chars_micro;
    int64_t llm_per_thousand_input_tokens_micro;
    int64_t llm_per_thousand_output_tokens_micro;
    char *currency;
} ca_call_pricing_t;

void ca_call_pricing_free(ca_call_pricing_t *pricing);

typedef struct {
    int64_t carrier_micro;
    int64_t stt_micro;
    int64_t tts_micro;
    int64_t llm_micro;
    int64_t total_micro;
} ca_call_cost_breakdown_t;

typedef struct ca_call_cost_calculator ca_call_cost_calculator_t;

ca_call_cost_calculator_t *ca_call_cost_calculator_new(const ca_call_pricing_t *pricing);
void ca_call_cost_calculator_free(ca_call_cost_calculator_t *calculator);

void ca_call_cost_add_carrier_time(ca_call_cost_calculator_t *calculator, int64_t ms);
void ca_call_cost_add_stt_time(ca_call_cost_calculator_t *calculator, int64_t ms);
void ca_call_cost_add_tts_characters(ca_call_cost_calculator_t *calculator, int chars);
void ca_call_cost_add_llm_tokens(ca_call_cost_calculator_t *calculator,
                                 int input_tokens, int output_tokens);

/* The breakdown, not just a total. A call that is expensive because of TTS and
 * one that is expensive because of carrier minutes need opposite fixes, and a
 * single number cannot tell them apart. */
ca_call_cost_breakdown_t ca_call_cost_current_breakdown(
    const ca_call_cost_calculator_t *calculator);

void ca_call_cost_reset(ca_call_cost_calculator_t *calculator);

/* -- speech lifecycle ----------------------------------------------------- */

/* What just happened on the line. One enum rather than a class per event
 * because C has no inheritance and the consumer is a switch either way. */
typedef enum {
    CA_SPEECH_LIFECYCLE_CALLER_SPEECH_STARTED = 0,
    CA_SPEECH_LIFECYCLE_CALLER_SPEECH_ENDED,
    CA_SPEECH_LIFECYCLE_AGENT_THINKING,
    CA_SPEECH_LIFECYCLE_AGENT_SPEAKING_STARTED,
    CA_SPEECH_LIFECYCLE_AGENT_SPEAKING_FINISHED,
    CA_SPEECH_LIFECYCLE_TRANSCRIPT_INTERIM,
    CA_SPEECH_LIFECYCLE_TRANSCRIPT_FINAL,
    CA_SPEECH_LIFECYCLE_SPEECH_ERROR
} ca_speech_lifecycle_kind_t;

const char *ca_speech_lifecycle_kind_name(ca_speech_lifecycle_kind_t kind);

typedef struct {
    ca_speech_lifecycle_kind_t kind;
    char *call_id;
    int64_t at_unix_ms;
    /* Set for the transcript kinds. */
    char *text;
    /* Set for CA_SPEECH_LIFECYCLE_SPEECH_ERROR. */
    char *error;
    /* Interim transcripts carry no confidence in most engines; negative means
     * the engine did not say. Zero is a real answer meaning "no idea", and the
     * two must not be confused. */
    double confidence;
} ca_speech_lifecycle_event_t;

void ca_speech_lifecycle_event_free(ca_speech_lifecycle_event_t *event);

/* The second-generation final transcript, carried alongside the first because
 * hosts subscribed to the original are still running. Named for the version so
 * that the migration is visible rather than a silent shape change. */
typedef struct {
    char *call_id;
    char *text;
    double confidence;
    int64_t started_unix_ms;
    int64_t ended_unix_ms;
    char *language;
    /* Word-level timings, when the engine gives them. */
    char **words;
    int64_t *word_offsets_ms;
    size_t word_count;
} ca_transcript_final_event_v2_t;

void ca_transcript_final_event_v2_free(ca_transcript_final_event_v2_t *event);

/*
 * One constructor per kind, because which fields are meaningful DEPENDS on the
 * kind. A caller that zeroes the struct and sets `kind` by hand produces a
 * transcript event with no text - indistinguishable from a caller who said
 * nothing - and nothing downstream can tell those apart. Caller frees.
 */
ca_speech_lifecycle_event_t *ca_caller_speech_started_event_new(const char *call_id,
                                                                int64_t at_unix_ms);

ca_speech_lifecycle_event_t *ca_caller_speech_ended_event_new(const char *call_id,
                                                              int64_t at_unix_ms);

ca_speech_lifecycle_event_t *ca_agent_thinking_event_new(const char *call_id,
                                                         int64_t at_unix_ms);

ca_speech_lifecycle_event_t *ca_agent_speaking_started_event_new(const char *call_id,
                                                                 int64_t at_unix_ms);

ca_speech_lifecycle_event_t *ca_agent_speaking_finished_event_new(const char *call_id,
                                                                  int64_t at_unix_ms);

/* Interim transcripts are REPLACED, not appended - each one supersedes the last
 * for that utterance. A consumer that appends renders the sentence growing by
 * duplication. */
ca_speech_lifecycle_event_t *ca_transcript_interim_event_new(const char *call_id,
                                                             const char *text,
                                                             double confidence,
                                                             int64_t at_unix_ms);

ca_speech_lifecycle_event_t *ca_speech_error_event_new(const char *call_id,
                                                       const char *error,
                                                       int64_t at_unix_ms);

typedef struct ca_speech_subscription ca_speech_subscription_t;

/* Unsubscribing must be possible from inside a handler - a component that
 * decides "I have heard enough" cancels while the bus is mid-publish. */
void ca_speech_subscription_cancel(ca_speech_subscription_t *subscription);

typedef struct ca_speech_lifecycle_bus ca_speech_lifecycle_bus_t;

ca_speech_lifecycle_bus_t *ca_speech_lifecycle_bus_new(void);
void ca_speech_lifecycle_bus_free(ca_speech_lifecycle_bus_t *bus);

ca_speech_subscription_t *ca_speech_lifecycle_bus_subscribe(
    ca_speech_lifecycle_bus_t *bus,
    void (*handler)(void *state, const ca_speech_lifecycle_event_t *event),
    void *state);

/* A throwing subscriber must not stop the others. On a live call the events are
 * how anything knows to stop talking; one bad handler silencing the bus turns a
 * bug in a metrics sink into an assistant that talks over the caller. */
void ca_speech_lifecycle_bus_publish(ca_speech_lifecycle_bus_t *bus,
                                     const ca_speech_lifecycle_event_t *event);

/* -- latency and telemetry ------------------------------------------------ */

/* The stages between "caller stopped talking" and "assistant started talking".
 * Named individually because the fix for each is different and a single
 * end-to-end number tells you only that it is too slow. */
typedef enum {
    CA_LATENCY_STAGE_ENDPOINTING = 0,
    CA_LATENCY_STAGE_TRANSCRIPTION,
    CA_LATENCY_STAGE_INFERENCE,
    CA_LATENCY_STAGE_TOOL_CALL,
    CA_LATENCY_STAGE_SYNTHESIS,
    CA_LATENCY_STAGE_PLAYBACK
} ca_latency_stage_t;

const char *ca_latency_stage_name(ca_latency_stage_t stage);

typedef struct ca_voice_loop_telemetry ca_voice_loop_telemetry_t;

ca_voice_loop_telemetry_t *ca_voice_loop_telemetry_new(void);
void ca_voice_loop_telemetry_free(ca_voice_loop_telemetry_t *telemetry);

void ca_voice_loop_telemetry_record(ca_voice_loop_telemetry_t *telemetry,
                                    ca_latency_stage_t stage, double milliseconds);

/* Percentiles, not a mean. The mean turn latency of a call is close to useless -
 * what a caller notices is the worst turn, and a p95 is the number that moves
 * when a call feels bad. */
double ca_voice_loop_telemetry_percentile(const ca_voice_loop_telemetry_t *telemetry,
                                          ca_latency_stage_t stage, double percentile);

size_t ca_voice_loop_telemetry_sample_count(const ca_voice_loop_telemetry_t *telemetry,
                                            ca_latency_stage_t stage);

/* -- filling the silence -------------------------------------------------- */

/* Words to say while something slow happens. A vocabulary rather than one
 * string: hearing the identical filler three times in a call is worse than
 * silence, because it is audibly a recording. */
typedef struct {
    char **phrases;
    size_t phrase_count;
    char *language;
} ca_reassurance_vocabulary_t;

void ca_reassurance_vocabulary_free(ca_reassurance_vocabulary_t *vocabulary);

typedef struct {
    /* Do not fill before this - most turns are fast enough that a filler would
     * arrive after the real answer. */
    int min_delay_before_filler_ms;   /* 700 */
    int max_fillers_per_turn;         /* 2 */
    bool avoid_repeating_last;
} ca_reassurance_filler_options_t;

ca_reassurance_filler_options_t ca_reassurance_filler_options_default(void);

typedef struct ca_reassurance_filler ca_reassurance_filler_t;

ca_reassurance_filler_t *ca_reassurance_filler_new(
    const ca_reassurance_vocabulary_t *vocabulary,
    const ca_reassurance_filler_options_t *options);

void ca_reassurance_filler_free(ca_reassurance_filler_t *filler);

/* NULL when it is too early to fill or the turn's budget is spent. Borrowed. */
const char *ca_reassurance_filler_next(ca_reassurance_filler_t *filler,
                                       int64_t elapsed_ms);

void ca_reassurance_filler_turn_finished(ca_reassurance_filler_t *filler);

typedef struct {
    /* Whether to speak first at all. On an INBOUND call the caller has already
     * started talking; a preamble there means talking over them. */
    bool speak_first;
    char *text;
    int max_length_chars;
} ca_first_message_preamble_options_t;

void ca_first_message_preamble_options_free(ca_first_message_preamble_options_t *options);

typedef struct ca_first_message_preamble ca_first_message_preamble_t;

ca_first_message_preamble_t *ca_first_message_preamble_new(
    const ca_first_message_preamble_options_t *options);

void ca_first_message_preamble_free(ca_first_message_preamble_t *preamble);

const char *ca_first_message_preamble_text(const ca_first_message_preamble_t *preamble);

/* Music while somebody waits. Mixed rather than switched: cutting the assistant
 * out and the music in leaves a gap that sounds like a dropped call. */
typedef struct ca_hold_music_mixer ca_hold_music_mixer_t;

ca_hold_music_mixer_t *ca_hold_music_mixer_new(const uint8_t *loop_pcm, size_t len,
                                               int sample_rate_hz);

void ca_hold_music_mixer_free(ca_hold_music_mixer_t *mixer);

/* Mixes the loop under `speech` at `music_gain` and writes to `out`, which must
 * hold `len` bytes. */
bool ca_hold_music_mixer_mix(ca_hold_music_mixer_t *mixer, const uint8_t *speech,
                             size_t len, float music_gain, uint8_t *out);

/* -- guardrails ----------------------------------------------------------- */

/* What to do when a rule matches. REDACT and BLOCK are genuinely different:
 * redaction lets the call continue with the number removed, blocking stops the
 * sentence being spoken at all. */
typedef enum {
    CA_GUARDRAIL_ALLOW = 0,
    CA_GUARDRAIL_REDACT,
    CA_GUARDRAIL_BLOCK,
    CA_GUARDRAIL_ESCALATE
} ca_guardrail_action_t;

const char *ca_guardrail_action_name(ca_guardrail_action_t action);

typedef struct {
    char *name;
    char *pattern;          /* POSIX ERE */
    ca_guardrail_action_t action;
    char *replacement;      /* for CA_GUARDRAIL_REDACT */
} ca_guardrail_rule_t;

void ca_guardrail_rule_free(ca_guardrail_rule_t *rule);

typedef struct {
    ca_guardrail_action_t action;
    /* The draft after redaction. Owned. */
    char *text;
    char *triggered_rule;
} ca_guardrail_result_t;

void ca_guardrail_result_free(ca_guardrail_result_t *result);

typedef struct ca_guardrails ca_guardrails_t;

ca_guardrails_t *ca_guardrails_new(const ca_guardrail_rule_t *rules, size_t count);
void ca_guardrails_free(ca_guardrails_t *guardrails);

/* Rules apply in order and BLOCK short-circuits. Order matters: a redaction
 * after a block would rewrite text that is never spoken, and a block after a
 * redaction would test text that no longer contains what it looks for. */
bool ca_guardrails_apply(ca_guardrails_t *guardrails, const char *draft,
                         ca_guardrail_result_t *out_result);

/* The rules worth having by default. Returned as owned copies so a caller can
 * amend one without editing everybody's. */
ca_guardrail_rule_t *ca_common_guardrails_credit_card_redactor(void);
ca_guardrail_rule_t *ca_common_guardrails_ssn_blocker(void);
ca_guardrail_rule_t *ca_common_guardrails_competitor_mention(const char **competitors,
                                                             size_t count);

/* -- tools on a call ------------------------------------------------------ */

typedef struct ca_tool_call_registry {
    void *state;
    bool (*register_local)(void *state, const char *name, const char *schema_json,
                           char *(*handler)(void *handler_state, const char *args_json),
                           void *handler_state);
    bool (*register_webhook)(void *state, const char *name, const char *schema_json,
                             const char *webhook_url);
    /* Returns the tool result as JSON, or NULL. Caller frees. */
    char *(*invoke)(void *state, const char *name, const char *args_json);
    size_t (*count)(void *state);
    void (*free_fn)(void *state);
} ca_tool_call_registry_t;

void ca_tool_call_registry_free(ca_tool_call_registry_t *registry);
ca_tool_call_registry_t *ca_tool_call_registry_new(void);

typedef enum {
    CA_TOOL_BREAKER_CLOSED = 0,
    CA_TOOL_BREAKER_OPEN,
    CA_TOOL_BREAKER_HALF_OPEN
} ca_tool_breaker_state_t;

const char *ca_tool_breaker_state_name(ca_tool_breaker_state_t state);

typedef struct {
    int failure_threshold;
    int64_t open_duration_ms;
    int64_t timeout_ms;
} ca_tool_call_policy_t;

ca_tool_call_policy_t ca_tool_call_policy_default(void);

/*
 * Wraps a registry so a failing tool stops being called.
 *
 * On a phone call this matters more than in a request handler: a tool that
 * takes thirty seconds to time out is thirty seconds of a person listening to
 * nothing, and retrying it three times is a minute and a half. Open the breaker
 * and answer without it.
 *
 * Takes ownership of `inner`.
 */
ca_tool_call_registry_t *ca_circuit_breaker_tool_registry_new(
    ca_tool_call_registry_t *inner);

void ca_circuit_breaker_tool_registry_set_policy(ca_tool_call_registry_t *registry,
                                                 const char *tool_name,
                                                 const ca_tool_call_policy_t *policy);

ca_tool_breaker_state_t ca_circuit_breaker_tool_registry_state(
    const ca_tool_call_registry_t *registry, const char *tool_name);

/* -- telling the caller a tool is running --------------------------------- */

typedef struct {
    char *tool_name;
    char *message;
    /* 0..1, or negative when the tool cannot say. A fake progress bar on a
     * phone call is worse than none: the caller hears a number and expects it
     * to mean something. */
    double fraction;
} ca_tool_progress_update_t;

void ca_tool_progress_update_free(ca_tool_progress_update_t *update);

typedef struct ca_tool_progress_sink {
    void *state;
    void (*report)(void *state, const ca_tool_progress_update_t *update);
    void (*free_fn)(void *state);
} ca_tool_progress_sink_t;

void ca_tool_progress_sink_free(ca_tool_progress_sink_t *sink);

/* Keeps updates for later inspection. What tests assert against. */
ca_tool_progress_sink_t *ca_recording_tool_progress_sink_new(void);
size_t ca_recording_tool_progress_sink_count(const ca_tool_progress_sink_t *sink);

/* Says the update out loud, throttled. Without the throttle a chatty tool turns
 * into an assistant that narrates a progress bar. */
ca_tool_progress_sink_t *ca_spoken_tool_progress_sink_new(
    void (*speak)(void *state, const char *text), void *state,
    int64_t min_interval_ms);

typedef struct ca_streaming_tool_runner ca_streaming_tool_runner_t;

ca_streaming_tool_runner_t *ca_streaming_tool_runner_new(
    ca_tool_call_registry_t *registry, ca_tool_progress_sink_t *sink);

void ca_streaming_tool_runner_free(ca_streaming_tool_runner_t *runner);

char *ca_streaming_tool_runner_invoke(ca_streaming_tool_runner_t *runner,
                                      const char *tool_name, const char *args_json);

/* -- speculation ---------------------------------------------------------- */

typedef struct {
    char *predicted_input;
    char *response;
    double probability;
} ca_speculative_branch_t;

void ca_speculative_branch_free(ca_speculative_branch_t *branch);

typedef struct ca_speculative_generator ca_speculative_generator_t;

/*
 * Starts generating the likely reply before the caller has finished speaking.
 *
 * Worth it because the alternative is dead air: most turns in a scripted call
 * are predictable ("yes", "no", a date), and being wrong costs only the tokens.
 * Being right removes the entire inference stage from the caller's experience.
 *
 * Branches are DISCARDED, never spoken, when the real utterance does not match -
 * speaking a speculated answer to a question that was not asked is the one
 * failure mode that makes this unusable.
 */
ca_speculative_generator_t *ca_speculative_generator_new(size_t max_branches);
void ca_speculative_generator_free(ca_speculative_generator_t *generator);

bool ca_speculative_generator_add_branch(ca_speculative_generator_t *generator,
                                         const ca_speculative_branch_t *branch);

/* The response for an utterance that matches a branch, or NULL. Borrowed. */
const char *ca_speculative_generator_resolve(ca_speculative_generator_t *generator,
                                             const char *actual_input);

void ca_speculative_generator_discard(ca_speculative_generator_t *generator);

/* -- handing the call to somebody else ------------------------------------ */

typedef struct {
    char *call_id;
    char *target_e164;
    /* What the receiving human is told before the caller is connected. The
     * whole point of a WARM transfer: without it the caller repeats everything. */
    char *context_summary;
    int timeout_seconds;
} ca_warm_transfer_request_t;

void ca_warm_transfer_request_free(ca_warm_transfer_request_t *request);

typedef struct ca_warm_transfer_orchestrator {
    void *state;
    bool (*transfer)(void *state, const ca_warm_transfer_request_t *request);
    void (*free_fn)(void *state);
} ca_warm_transfer_orchestrator_t;

void ca_warm_transfer_orchestrator_free(ca_warm_transfer_orchestrator_t *orchestrator);

ca_warm_transfer_orchestrator_t *ca_warm_transfer_orchestrator_new(
    ca_telephony_carrier_t *carrier);

typedef struct ca_agent_handoff_orchestrator {
    void *state;
    /* Moves a live call from one agent configuration to another without
     * dropping it. Returns false if the target agent does not exist - the call
     * stays where it is rather than ending. */
    bool (*handoff)(void *state, const char *call_id, const char *target_agent_id,
                    const char *reason);
    void (*free_fn)(void *state);
} ca_agent_handoff_orchestrator_t;

void ca_agent_handoff_orchestrator_free(ca_agent_handoff_orchestrator_t *orchestrator);
ca_agent_handoff_orchestrator_t *ca_agent_handoff_orchestrator_new(void);

/* An escalation channel that posts to a webhook. Separate from the transfer
 * orchestrator because escalating is not always a phone call - often it is a
 * message to a human who will call back. */
typedef struct ca_consult_channel {
    void *state;
    bool (*consult)(void *state, const char *call_id, const char *question);
    void (*free_fn)(void *state);
} ca_consult_channel_t;

void ca_consult_channel_free(ca_consult_channel_t *channel);

ca_consult_channel_t *ca_http_webhook_consult_channel_new(
    const char *webhook_url,
    bool (*post)(void *state, const char *url, const char *body), void *state);

/* -- the voice loop as a tool --------------------------------------------- */

typedef struct {
    char *to_e164;
    char *objective;
    char **facts;
    size_t fact_count;
    int max_duration_seconds;
} ca_voice_loop_tool_request_t;

void ca_voice_loop_tool_request_free(ca_voice_loop_tool_request_t *request);

typedef struct {
    bool succeeded;
    char *outcome;
    char *transcript;
    int64_t duration_ms;
    int64_t cost_micro;
} ca_voice_loop_tool_result_t;

void ca_voice_loop_tool_result_free(ca_voice_loop_tool_result_t *result);

/* An outbound call, exposed to an agent as a tool it can invoke.
 *
 * The most dangerous tool in the system: it takes an action in the world that
 * cannot be undone, at somebody else's phone. Every call it places carries a
 * duration cap and an objective, so a loop that goes wrong ends by itself. */
typedef struct ca_voice_loop_tool {
    void *state;
    ca_voice_loop_tool_result_t *(*run)(void *state,
                                        const ca_voice_loop_tool_request_t *request);
    void (*free_fn)(void *state);
} ca_voice_loop_tool_t;

void ca_voice_loop_tool_free(ca_voice_loop_tool_t *tool);

ca_voice_loop_tool_t *ca_voice_loop_as_tool_new(ca_telephony_carrier_t *carrier);

/* -- numbers, recording, dashboards, dev tunnels -------------------------- */

typedef struct ca_phone_number_provisioner ca_phone_number_provisioner_t;

ca_phone_number_provisioner_t *ca_phone_number_provisioner_new(
    ca_telephony_carrier_t *carrier);

void ca_phone_number_provisioner_free(ca_phone_number_provisioner_t *provisioner);

/* Caller frees. NULL when nothing is available in that area code, which is a
 * normal answer and not an error. */
char *ca_phone_number_provisioner_acquire(ca_phone_number_provisioner_t *provisioner,
                                          const char *country_iso_alpha2,
                                          const char *area_code);

bool ca_phone_number_provisioner_release(ca_phone_number_provisioner_t *provisioner,
                                         const char *e164);

typedef struct ca_stereo_call_recorder ca_stereo_call_recorder_t;

/*
 * Records the caller on one channel and the assistant on the other.
 *
 * STEREO IS THE POINT. A mixed mono recording cannot answer "who spoke over
 * whom", which is the question every review of a bad call turns out to be
 * asking. Two channels make interruptions visible in the waveform.
 */
ca_stereo_call_recorder_t *ca_stereo_call_recorder_new(int sample_rate_hz);
void ca_stereo_call_recorder_free(ca_stereo_call_recorder_t *recorder);

bool ca_stereo_call_recorder_write_caller(ca_stereo_call_recorder_t *recorder,
                                          const uint8_t *pcm, size_t len);

bool ca_stereo_call_recorder_write_agent(ca_stereo_call_recorder_t *recorder,
                                         const uint8_t *pcm, size_t len);

/* Interleaved stereo PCM-16 with a WAV header. Caller frees. */
uint8_t *ca_stereo_call_recorder_finish(ca_stereo_call_recorder_t *recorder,
                                        size_t *out_len);

typedef struct ca_dashboard_data_source {
    void *state;
    /* JSON. Caller frees. */
    char *(*summary)(void *state, int64_t from_unix, int64_t to_unix);
    void (*free_fn)(void *state);
} ca_dashboard_data_source_t;

void ca_dashboard_data_source_free(ca_dashboard_data_source_t *source);
ca_dashboard_data_source_t *ca_dashboard_data_source_new(void);

/* A publicly reachable URL for a machine that has none, so a carrier webhook
 * can reach a laptop. */
typedef struct ca_local_dev_tunnel {
    void *state;
    const char *(*public_url)(void *state);
    void (*free_fn)(void *state);
} ca_local_dev_tunnel_t;

void ca_local_dev_tunnel_free(ca_local_dev_tunnel_t *tunnel);

/* NULL URL: there is no tunnel. The default, because a tunnel is a hole into a
 * development machine and one should never appear because nobody configured
 * anything. */
ca_local_dev_tunnel_t *ca_null_local_dev_tunnel_new(void);

/* A URL somebody already established, by whatever means. */
ca_local_dev_tunnel_t *ca_static_local_dev_tunnel_new(const char *public_url);

/* -- prompts, MCP tools, evaluation --------------------------------------- */

typedef struct ca_prompt_variable_resolver ca_prompt_variable_resolver_t;

ca_prompt_variable_resolver_t *ca_prompt_variable_resolver_new(void);
void ca_prompt_variable_resolver_free(ca_prompt_variable_resolver_t *resolver);

void ca_prompt_variable_resolver_set(ca_prompt_variable_resolver_t *resolver,
                                     const char *name, const char *value);

/* Substitutes {{name}}. An UNKNOWN variable is left as-is rather than blanked:
 * a prompt with a visible {{customer_name}} in it is a bug somebody notices,
 * and one with a silent gap is a call where the assistant addresses nobody.
 * Caller frees. */
char *ca_prompt_variable_resolver_resolve(const ca_prompt_variable_resolver_t *resolver,
                                          const char *template_text);

typedef struct {
    char *name;
    char *description;
    char *input_schema_json;
} ca_mcp_tool_descriptor_t;

void ca_mcp_tool_descriptor_free(ca_mcp_tool_descriptor_t *descriptor);

typedef struct ca_mcp_tool_importer {
    void *state;
    /* Heap array of *out_count. */
    ca_mcp_tool_descriptor_t *(*list_tools)(void *state, size_t *out_count);
    void (*free_fn)(void *state);
} ca_mcp_tool_importer_t;

void ca_mcp_tool_importer_free(ca_mcp_tool_importer_t *importer);

ca_mcp_tool_importer_t *ca_http_mcp_tool_importer_new(
    const char *endpoint,
    char *(*get)(void *state, const char *url), void *state);

typedef struct ca_eval_session ca_eval_session_t;

/* A scripted call replayed against the agent, so a prompt change can be
 * measured rather than guessed at. */
ca_eval_session_t *ca_eval_session_new(const char *scenario_id);
void ca_eval_session_free(ca_eval_session_t *session);

void ca_eval_session_add_turn(ca_eval_session_t *session, const char *caller_said,
                              const char *agent_said);

size_t ca_eval_session_turn_count(const ca_eval_session_t *session);

typedef struct ca_llm_judge {
    void *state;
    /* 0..1 with a written reason. The reason is not decoration: a score with no
     * justification cannot be argued with, and every eval eventually comes down
     * to somebody disagreeing with a score. */
    bool (*judge)(void *state, const ca_eval_session_t *session,
                  const char *rubric, double *out_score, char **out_reason);
    void (*free_fn)(void *state);
} ca_llm_judge_t;

void ca_llm_judge_free(ca_llm_judge_t *judge);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_TELEPHONY_CALL_H */
