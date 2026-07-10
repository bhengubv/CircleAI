#ifndef CIRCLE_AI_REALTIME_H
#define CIRCLE_AI_REALTIME_H

/*
 * realtime.h — CircleAI.Realtime + CircleAI.Realtime.Cloud (C11 port).
 *
 * Ports the carrier-agnostic streaming-realtime-AI surface 1:1:
 *
 *   CircleAI.Realtime (Contracts.cs + LoopbackRealtimeService.cs +
 *   NullImplementations.cs):
 *     Enums     : RealtimeAudioFormat { Pcm16k, Pcm24k, Mulaw8k };
 *                 RealtimeDirection { Inbound, Outbound }.
 *     Records   : RealtimeSessionConfig(Model, VoiceId?, SystemPrompt?,
 *                 AudioFormat=Pcm24k, LanguageHint?, Tools?);
 *                 RealtimeTool(Name, Description, JsonSchema);
 *                 RealtimeAudioFrame(Pcm, Format, Offset).
 *     Events    : RealtimeEvent union — SpeechStarted, SpeechEnded,
 *                 TranscriptDelta(Delta,Direction), TranscriptFinal(Text,
 *                 Direction), ToolCall(CallId,ToolName,ArgumentsJson),
 *                 TurnComplete, SessionError(Message); all carry At.
 *     Session   : IRealtimeSession — LoopbackRealtimeSession + NullRealtimeSession.
 *     Service   : IRealtimeService — LoopbackRealtimeService + NullRealtimeService.
 *
 *   CircleAI.Realtime.Cloud (IRealtimeTransport.cs):
 *     Transport : IRealtimeTransport (injected WebSocket-style vtable);
 *                 IRealtimeTransportFactory + NullRealtimeTransportFactory
 *                 (connect fails — no host wired).
 *
 * The Loopback session echoes inbound audio back as outbound, raises
 * SpeechStarted/Ended from an RMS silence detector, and answers SendText with a
 * silence-PCM "TTS" stream (~80ms/word) plus TranscriptDelta/Final/TurnComplete.
 * The C# Channels are unbounded — writes are retained until read — so the audio
 * and event streams here are unbounded FIFO cursors drained with *_next.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no hashtable, no pthreads. PCM-16 samples are little-endian. Offsets
 * are TimeSpan ticks (100ns); At is DateTimeOffset as Unix ms UTC.
 *
 * Pure C11 + libc + libm.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Enums
 * =========================================================================== */

typedef enum {
    CA_RT_FMT_PCM16K = 0,  /* 16-bit PCM mono 16 kHz */
    CA_RT_FMT_PCM24K = 1,  /* 16-bit PCM mono 24 kHz */
    CA_RT_FMT_MULAW8K = 2  /* G.711 μ-law mono 8 kHz */
} ca_rt_audio_format_t;

typedef enum {
    CA_RT_DIR_INBOUND = 0,
    CA_RT_DIR_OUTBOUND = 1
} ca_rt_direction_t;

/* Sample rate for a format (Pcm16k->16000, Pcm24k->24000, Mulaw8k->8000, else
 * 16000). Public because tests + hosts size buffers with it. */
int ca_rt_sample_rate_of(ca_rt_audio_format_t f);

/* ===========================================================================
 * RealtimeTool(Name, Description, JsonSchema)
 * =========================================================================== */

typedef struct {
    char *name;         /* owned */
    char *description;  /* owned */
    char *json_schema;  /* owned */
} ca_rt_tool_t;

void ca_rt_tool_free(ca_rt_tool_t *t);

/* ===========================================================================
 * RealtimeSessionConfig
 *
 * Optional strings (VoiceId, SystemPrompt, LanguageHint) are NULL when the C#
 * value is null. Tools is an owned array (may be NULL/empty). AudioFormat
 * defaults to Pcm24k; use ca_rt_session_config_default to construct with the
 * record's default values.
 * =========================================================================== */

typedef struct {
    char                *model;         /* owned, non-null */
    char                *voice_id;      /* owned or NULL */
    char                *system_prompt; /* owned or NULL */
    ca_rt_audio_format_t audio_format;
    char                *language_hint; /* owned or NULL */
    ca_rt_tool_t        *tools;         /* owned array or NULL */
    size_t               tool_count;
} ca_rt_session_config_t;

void ca_rt_session_config_free(ca_rt_session_config_t *c);

/* ===========================================================================
 * RealtimeAudioFrame(Pcm, Format, Offset)
 * =========================================================================== */

typedef struct {
    uint8_t             *pcm;        /* owned (may be NULL when len 0) */
    size_t               pcm_len;
    ca_rt_audio_format_t format;
    int64_t              offset_ticks; /* TimeSpan ticks (100ns) */
} ca_rt_audio_frame_t;

void ca_rt_audio_frame_free(ca_rt_audio_frame_t *f);

/* ===========================================================================
 * RealtimeEvent (discriminated union)
 * =========================================================================== */

typedef enum {
    CA_RT_EVT_SPEECH_STARTED = 0,
    CA_RT_EVT_SPEECH_ENDED,
    CA_RT_EVT_TRANSCRIPT_DELTA,
    CA_RT_EVT_TRANSCRIPT_FINAL,
    CA_RT_EVT_TOOL_CALL,
    CA_RT_EVT_TURN_COMPLETE,
    CA_RT_EVT_SESSION_ERROR
} ca_rt_event_type_t;

/* One event. `at_utc_ms` carries DateTimeOffset At for every variant. String
 * payloads are owned and populated only for the variants that carry them:
 *   TRANSCRIPT_DELTA / TRANSCRIPT_FINAL : text (+ direction)
 *   TOOL_CALL                           : call_id, tool_name, arguments_json
 *   SESSION_ERROR                       : message
 * Other variants leave the string fields NULL. */
typedef struct {
    ca_rt_event_type_t type;
    int64_t            at_utc_ms;
    ca_rt_direction_t  direction;       /* transcript variants only */
    char              *text;            /* delta/final text; owned or NULL */
    char              *call_id;         /* tool-call; owned or NULL */
    char              *tool_name;       /* tool-call; owned or NULL */
    char              *arguments_json;  /* tool-call; owned or NULL */
    char              *message;         /* session-error; owned or NULL */
} ca_rt_event_t;

void ca_rt_event_free(ca_rt_event_t *e);

/* ===========================================================================
 * IRealtimeSession — Loopback + Null
 *
 * The C# duplex Channels become two unbounded FIFO cursors drained with
 * *_receive_audio_next / *_receive_event_next. Sends complete synchronously.
 * =========================================================================== */

typedef struct ca_rt_session ca_rt_session_t;

/* Optional TTS seam: synthesise outbound audio for text into a freshly owned
 * buffer (*out_pcm, *out_len). Returns 0 on success, -1 on error. Default is the
 * built-in silence synthesiser (see LoopbackTextToAudio). */
typedef int (*ca_rt_text_to_audio_fn)(void *ctx, const char *text,
                                      ca_rt_audio_format_t format,
                                      uint8_t **out_pcm, size_t *out_len);

/* NullRealtimeSession — SessionId "null"; both streams yield nothing; sends are
 * no-ops. */
ca_rt_session_t *ca_rt_null_session_create(void);

/* LoopbackRealtimeSession(config). BORROWS nothing — deep-copies the config it
 * needs (AudioFormat). SessionId = "loop-<32 hex>". With text_to_audio NULL the
 * built-in silence synthesiser is used. NULL on bad args / OOM. */
ca_rt_session_t *ca_rt_loopback_session_create(const ca_rt_session_config_t *config,
                                               ca_rt_text_to_audio_fn text_to_audio,
                                               void *tts_ctx);

void ca_rt_session_destroy(ca_rt_session_t *s);

/* SessionId (borrowed). */
const char *ca_rt_session_id(const ca_rt_session_t *s);

/* SendAudioAsync(frame): drives the speech/silence transition events and echoes
 * the frame to the outbound audio stream (loopback). frame required (its pcm may
 * be NULL only when pcm_len==0). 0 / -1. No-op (0) on the Null session. */
int ca_rt_session_send_audio(ca_rt_session_t *s, const ca_rt_audio_frame_t *frame);

/* SendTextAsync(text): emits TranscriptDelta(Outbound), synthesises audio (if
 * non-empty pushes an outbound frame and advances the offset), then
 * TranscriptFinal(Outbound) + TurnComplete. text required. 0 / -1. */
int ca_rt_session_send_text(ca_rt_session_t *s, const char *text);

/* SendToolResultAsync(callId, resultJson): emits a TranscriptDelta(Outbound)
 * "[tool <callId>: <resultJson truncated to 60 chars + …>]". callId required
 * (non-whitespace); resultJson required (non-null). 0 / -1. */
int ca_rt_session_send_tool_result(ca_rt_session_t *s, const char *call_id,
                                   const char *result_json);

/* CancelResponseAsync: emits TurnComplete. 0 / -1. */
int ca_rt_session_cancel_response(ca_rt_session_t *s);

/* Drain the next inbound/outbound audio frame into *out (freshly owned; free with
 * ca_rt_audio_frame_free). Returns true if a frame was produced, false when the
 * stream is empty. */
bool ca_rt_session_receive_audio_next(ca_rt_session_t *s, ca_rt_audio_frame_t *out);
/* Buffered (undrained) audio frames. */
size_t ca_rt_session_audio_pending(const ca_rt_session_t *s);

/* Drain the next event into *out (freshly owned; free with ca_rt_event_free).
 * Returns true if an event was produced, false when the stream is empty. */
bool ca_rt_session_receive_event_next(ca_rt_session_t *s, ca_rt_event_t *out);
/* Buffered (undrained) events. */
size_t ca_rt_session_event_pending(const ca_rt_session_t *s);

/* ===========================================================================
 * IRealtimeService — Loopback + Null
 * =========================================================================== */

typedef struct ca_rt_service ca_rt_service_t;

/* LoopbackRealtimeService() (ProviderId "loopback"; IsConfigured true). With
 * text_to_audio NULL the built-in silence synthesiser is used for every session
 * it starts. NULL on OOM. */
ca_rt_service_t *ca_rt_loopback_service_create(ca_rt_text_to_audio_fn text_to_audio,
                                              void *tts_ctx);
/* NullRealtimeService (ProviderId "null"; IsConfigured false; StartSession
 * errors). */
ca_rt_service_t *ca_rt_null_service_create(void);
void ca_rt_service_destroy(ca_rt_service_t *svc);

const char *ca_rt_service_provider_id(const ca_rt_service_t *svc);
bool        ca_rt_service_is_configured(const ca_rt_service_t *svc);

/* StartSessionAsync(config) -> a fresh owned session (caller destroys). Returns
 * NULL on bad args, OOM, or when the service refuses (NullRealtimeService throws
 * "no vendor wired" -> NULL here). config required. */
ca_rt_session_t *ca_rt_service_start_session(ca_rt_service_t *svc,
                                             const ca_rt_session_config_t *config);

/* ===========================================================================
 * CircleAI.Realtime.Cloud — IRealtimeTransport + factory
 *
 * The host-supplied WebSocket transport is an injected vtable. Incoming text /
 * binary frames are modelled as pull cursors (recv_text_next / recv_binary_next)
 * the host feeds; sends + close forward to the host. IsOpen reports the socket.
 * =========================================================================== */

typedef struct {
    void *self;
    /* SendTextAsync(text). 0 / -1. */
    int (*send_text)(void *self, const char *text);
    /* SendBinaryAsync(bytes,len). 0 / -1. */
    int (*send_binary)(void *self, const uint8_t *bytes, size_t len);
    /* ReceiveTextAsync -> next frame: writes a freshly owned string into
     * *out_text and returns true; false at end of stream. */
    bool (*recv_text_next)(void *self, char **out_text);
    /* ReceiveBinaryAsync -> next frame: writes freshly owned bytes into
     * *out_bytes (+ *out_len) and returns true; false at end of stream. */
    bool (*recv_binary_next)(void *self, uint8_t **out_bytes, size_t *out_len);
    /* CloseAsync. 0 / -1. */
    int (*close)(void *self);
    /* IsOpen. */
    bool (*is_open)(void *self);
    /* DisposeAsync — release the transport. May be NULL. */
    void (*dispose)(void *self);
} ca_rt_transport_t;

typedef struct {
    void *self;
    /* ConnectAsync(endpoint, headers) -> fills *out (a transport vtable) and
     * returns 0; returns -1 when the factory refuses (NullRealtimeTransport-
     * Factory always refuses). headers is a parallel (key,value) array of
     * `header_count` entries (may be NULL/0). endpoint is the target URI string. */
    int (*connect)(void *self, const char *endpoint,
                   const char *const *header_keys,
                   const char *const *header_values, size_t header_count,
                   ca_rt_transport_t *out);
} ca_rt_transport_factory_t;

/* NullRealtimeTransportFactory — Connect always fails (returns -1); models the
 * C# InvalidOperationException "no factory registered". */
ca_rt_transport_factory_t ca_rt_null_transport_factory(void);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_REALTIME_H */
