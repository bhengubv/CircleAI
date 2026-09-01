#ifndef CIRCLE_AI_REALTIME_H
#define CIRCLE_AI_REALTIME_H

/*
 * realtime.h - CircleAI.Realtime (C11).
 *
 * Carrier-agnostic contracts for a streaming realtime conversation.
 *
 * FIVE VENDORS IMPLEMENT THESE and every one of them frames its WebSocket
 * differently. The point of the contract is that the loop above it never learns
 * which vendor it is talking to — a translation layer per vendor, one loop.
 *
 * AUDIO AND EVENTS ARE SEPARATE STREAMS, deliberately. Audio must keep flowing
 * while a transcript is being revised, and interleaving them into one channel
 * means a slow consumer of transcripts stalls the sound.
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

/* A marker so a host can confirm this module is linked in at all.
 *
 * "The realtime module is missing" and "it is present and misconfigured" are
 * indistinguishable otherwise, and the first is a build problem while the
 * second is a wiring one. */
const char *ca_realtime_package_marker(void);

typedef enum {
    CA_REALTIME_PCM_16K = 0,
    CA_REALTIME_PCM_24K,
    /* 8 kHz G.711 mu-law: what a telephone carrier actually delivers. */
    CA_REALTIME_MULAW_8K
} ca_realtime_audio_format_t;

int ca_realtime_audio_format_sample_rate(ca_realtime_audio_format_t format);

/* Which way a piece of audio or transcript is going. Needed because a
 * transcript with no direction is a line of dialogue with no speaker. */
typedef enum {
    CA_REALTIME_INBOUND = 0,
    CA_REALTIME_OUTBOUND
} ca_realtime_direction_t;

typedef struct {
    char *name;
    char *description;
    char *json_schema;
} ca_realtime_tool_t;

void ca_realtime_tool_free(ca_realtime_tool_t *tool);

typedef struct {
    char *model;
    char *voice_id;
    char *system_prompt;
    ca_realtime_audio_format_t audio_format;
    /* A hint, not a setting: the vendor may ignore it, and a caller that treats
     * it as a guarantee mislabels every transcript when the vendor is wrong. */
    char *language_hint;
    ca_realtime_tool_t *tools;
    size_t tool_count;
} ca_realtime_session_config_t;

void ca_realtime_session_config_free(ca_realtime_session_config_t *config);

typedef struct {
    uint8_t *pcm;
    size_t pcm_len;
    ca_realtime_audio_format_t format;
    /* Milliseconds from the start of the session. Carried with the frame
     * because a caller reassembling audio cannot recover it from arrival
     * order — packets arrive late and out of sequence on a real network. */
    int64_t offset_ms;
} ca_realtime_audio_frame_t;

void ca_realtime_audio_frame_free(ca_realtime_audio_frame_t *frame);

/* ── events ───────────────────────────────────────────────────────────────── */

/* C has no sealed hierarchy, so the union is a tag plus the fields any member
 * needs. Reading a field the tag does not cover is the caller's error, and the
 * accessors below are the reason there is no need to. */
typedef enum {
    CA_REALTIME_EVENT_SPEECH_STARTED = 0,
    CA_REALTIME_EVENT_SPEECH_ENDED,
    CA_REALTIME_EVENT_TRANSCRIPT_DELTA,
    CA_REALTIME_EVENT_TRANSCRIPT_FINAL,
    CA_REALTIME_EVENT_TOOL_CALL,
    CA_REALTIME_EVENT_TURN_COMPLETE,
    CA_REALTIME_EVENT_SESSION_ERROR
} ca_realtime_event_kind_t;

typedef struct {
    ca_realtime_event_kind_t kind;
    int64_t at_unix_ms;

    /* transcript delta and final */
    char *text;
    ca_realtime_direction_t direction;

    /* tool call */
    char *call_id;
    char *tool_name;
    char *arguments_json;

    /* session error */
    char *message;
} ca_realtime_event_t;

void ca_realtime_event_free(ca_realtime_event_t *event);

const char *ca_realtime_event_kind_name(ca_realtime_event_kind_t kind);

/* ── the session ──────────────────────────────────────────────────────────── */

typedef struct ca_realtime_session {
    void *state;

    const char *(*session_id)(void *state);

    /* Pulls the next audio frame, or NULL when the stream has ended. Caller
     * frees. Blocking is the implementation's business. */
    ca_realtime_audio_frame_t *(*receive_audio)(void *state);

    /* Pulls the next event, or NULL when the stream has ended. Caller frees. */
    ca_realtime_event_t *(*receive_event)(void *state);

    bool (*send_audio)(void *state, const ca_realtime_audio_frame_t *frame);
    bool (*send_text)(void *state, const char *text);
    bool (*send_tool_result)(void *state, const char *call_id, const char *result_json);

    /* Stops the model mid-answer. THE most important call here: it is what a
     * barge-in becomes, and a session that cannot be interrupted talks over
     * the person it is meant to be listening to. */
    bool (*cancel_response)(void *state);

    void (*close_fn)(void *state);
} ca_realtime_session_t;

void ca_realtime_session_close(ca_realtime_session_t *session);

/* Accepts everything and produces nothing. For a build with no vendor wired:
 * the loop runs and stays silent rather than crashing at the first turn. */
ca_realtime_session_t *ca_null_realtime_session_new(const char *session_id);

/* ── the service ──────────────────────────────────────────────────────────── */

typedef struct ca_realtime_service {
    void *state;
    const char *(*provider_id)(void *state);
    /* False when credentials or a model are missing. Asked BEFORE a call is
     * placed, so a misconfiguration is a startup problem and not a caller
     * listening to silence. */
    bool (*is_configured)(void *state);
    ca_realtime_session_t *(*start_session)(void *state,
                                            const ca_realtime_session_config_t *config);
    void (*free_fn)(void *state);
} ca_realtime_service_t;

void ca_realtime_service_free(ca_realtime_service_t *service);

/*
 * Echoes audio back and emits a transcript for whatever text is sent.
 *
 * Not a toy: it is how the loop above is tested without a vendor, a network or
 * a bill, and every behaviour that matters — barge-in, turn completion, tool
 * calls — is exercisable through it.
 */
ca_realtime_service_t *ca_loopback_realtime_service_new(void);

ca_realtime_session_t *ca_loopback_realtime_session_new(const char *session_id);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_REALTIME_H */
