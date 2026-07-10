#ifndef CIRCLE_AI_TELEPHONY_H
#define CIRCLE_AI_TELEPHONY_H

/*
 * telephony.h — CircleAI.Telephony (C11 port).
 *
 * Ports the carrier-agnostic telephony contract surface 1:1:
 *
 *   Primitives.cs:
 *     Enums   : CallDirection { Inbound, Outbound };
 *               CallStatus { Ringing, Active, EndedByCaller, EndedByCallee,
 *                            EndedByAgent, Voicemail, Failed, Transferred };
 *               CallMediaFormat { Mulaw8000, Alaw8000, Pcm16000, Pcm24000 };
 *               TransferMode { Cold, Warm }.
 *     Records : CallInfo, CallSnapshot, AudioFrame, DtmfEvent, ProvisionedNumber.
 *
 *   Contracts.cs / IMediaStream.cs:
 *     OutboundDialOptions;
 *     ITelephonyCarrier  (vtable) — ProvisionNumber / ConfigureInboundWebhook /
 *                         Dial / ListNumbers + CarrierId + IsConfigured;
 *     ICallSession       (opaque) — the in-memory TestCallSession + the carrier
 *                         session (built by the bindings) both wear this handle;
 *                         audio-in/out + DTMF-in/out + Transfer + HangUp +
 *                         StatusChanged pub/sub;
 *     IMediaStream       (opaque) — host media channel; ships the in-memory
 *                         ManualMediaStream (test/host seam) + the PendingMedia
 *                         stream the bindings return before a WebSocket attaches;
 *     IInboundCallDispatcher — in-memory + Null;
 *     IDtmfSendable      — optional out-of-band DTMF flag on a media stream.
 *
 *   ToolCalling.cs:
 *     ToolDefinition / ToolInvocation / ToolResult;
 *     IToolCallRegistry — DefaultToolCallRegistry (local handler fn OR injected
 *                         HTTP webhook poster vtable — no real network).
 *
 *   DtmfToneGenerator.cs:
 *     Generate / GenerateSequence — deterministic dual-tone PCM-16 synthesis.
 *
 *   NullImplementations.cs / ServiceCollectionExtensions.cs:
 *     NullTelephonyCarrier / NullInboundCallDispatcher / CarrierFallback.
 *
 * The C# duplex Channels (unbounded) become linear FIFO cursors drained with
 * *_next — writes are retained until read, never dropped. StatusChanged is a
 * snapshot-then-invoke broadcast (subscriber list copied before callbacks, so a
 * handler may unsubscribe mid-fire). No pthreads.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. PCM-16
 * samples are little-endian. Offsets/Durations are TimeSpan ticks (100ns);
 * timestamps DateTimeOffset as Unix ms UTC. Costs are fixed-point micro-units
 * (see ca_tel_decimal_t) to model C# decimal deterministically.
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
 * decimal — C# `decimal` surrogate for money fields (monthly cost, cost-so-far).
 * Stored as a signed count of 1e-6 units so exact small values round-trip and
 * comparisons stay deterministic.
 * =========================================================================== */

typedef int64_t ca_tel_decimal_t;   /* value * 1'000'000 */
#define CA_TEL_DECIMAL_SCALE 1000000LL

/* ===========================================================================
 * Enums
 * =========================================================================== */

typedef enum {
    CA_TEL_DIR_INBOUND = 0,
    CA_TEL_DIR_OUTBOUND = 1
} ca_tel_call_direction_t;

typedef enum {
    CA_TEL_STATUS_RINGING = 0,
    CA_TEL_STATUS_ACTIVE,
    CA_TEL_STATUS_ENDED_BY_CALLER,
    CA_TEL_STATUS_ENDED_BY_CALLEE,
    CA_TEL_STATUS_ENDED_BY_AGENT,
    CA_TEL_STATUS_VOICEMAIL,
    CA_TEL_STATUS_FAILED,
    CA_TEL_STATUS_TRANSFERRED
} ca_tel_call_status_t;

typedef enum {
    CA_TEL_FMT_MULAW8000 = 0,  /* µ-law 8 kHz mono */
    CA_TEL_FMT_ALAW8000  = 1,  /* A-law 8 kHz mono */
    CA_TEL_FMT_PCM16000  = 2,  /* PCM-16 16 kHz mono */
    CA_TEL_FMT_PCM24000  = 3   /* PCM-16 24 kHz mono */
} ca_tel_media_format_t;

typedef enum {
    CA_TEL_TRANSFER_COLD = 0,
    CA_TEL_TRANSFER_WARM = 1
} ca_tel_transfer_mode_t;

/* Sample rate (Hz) implied by SendDtmfAsync's switch: Pcm16000->16000,
 * Pcm24000->24000, else (Mulaw/Alaw)->8000. */
int ca_tel_sample_rate_of(ca_tel_media_format_t f);

/* ===========================================================================
 * CallInfo — captured once at call start, immutable.
 * =========================================================================== */

typedef struct {
    char                   *call_id;      /* owned, non-null */
    ca_tel_call_direction_t direction;
    char                   *from;         /* owned, non-null (E.164) */
    char                   *to;           /* owned, non-null (E.164) */
    char                   *carrier_id;   /* owned, non-null */
    ca_tel_media_format_t   media_format;
    int64_t                 started_at_utc_ms;
} ca_tel_call_info_t;

ca_tel_call_info_t *ca_tel_call_info_new(
    const char *call_id, ca_tel_call_direction_t direction,
    const char *from, const char *to, const char *carrier_id,
    ca_tel_media_format_t media_format, int64_t started_at_utc_ms);
void ca_tel_call_info_destroy(ca_tel_call_info_t *c);
ca_tel_call_info_t *ca_tel_call_info_copy(const ca_tel_call_info_t *c);

/* ===========================================================================
 * AudioFrame(Pcm, Format, Offset)
 * =========================================================================== */

typedef struct {
    uint8_t              *pcm;          /* owned (may be NULL iff pcm_len 0) */
    size_t                pcm_len;
    ca_tel_media_format_t format;
    int64_t               offset_ticks; /* TimeSpan ticks (100ns) */
} ca_tel_audio_frame_t;

void ca_tel_audio_frame_free(ca_tel_audio_frame_t *f);

/* ===========================================================================
 * DtmfEvent(Digit, Duration, Offset)
 * =========================================================================== */

typedef struct {
    char    digit;          /* 0-9 * # A-D */
    int64_t duration_ticks; /* TimeSpan ticks (100ns) */
    int64_t offset_ticks;   /* TimeSpan ticks (100ns) */
} ca_tel_dtmf_event_t;

/* ===========================================================================
 * ProvisionedNumber
 * =========================================================================== */

typedef struct {
    char            *phone_number;         /* owned */
    char            *carrier_id;           /* owned */
    int64_t          provisioned_at_utc_ms;
    ca_tel_decimal_t monthly_recurring_cost;
} ca_tel_provisioned_number_t;

void ca_tel_provisioned_number_free(ca_tel_provisioned_number_t *p);
void ca_tel_provisioned_number_free_array(ca_tel_provisioned_number_t *arr,
                                          size_t count);

/* ===========================================================================
 * CallSnapshot
 * =========================================================================== */

typedef struct {
    ca_tel_call_info_t  *info;             /* owned */
    ca_tel_call_status_t status;
    int64_t              duration_ticks;   /* TimeSpan ticks */
    ca_tel_decimal_t     cost_so_far;
    char                *transfer_target;  /* owned or NULL */
} ca_tel_call_snapshot_t;

ca_tel_call_snapshot_t *ca_tel_call_snapshot_new(
    const ca_tel_call_info_t *info, ca_tel_call_status_t status,
    int64_t duration_ticks, ca_tel_decimal_t cost_so_far,
    const char *transfer_target);
void ca_tel_call_snapshot_destroy(ca_tel_call_snapshot_t *s);

/* ===========================================================================
 * OutboundDialOptions
 * =========================================================================== */

typedef struct {
    bool               detect_answering_machine;
    int                ring_timeout_seconds;  /* default 30 */
    char              *caller_id_override;     /* owned or NULL */
    char             **follow_me_numbers;      /* owned array or NULL */
    size_t             follow_me_count;
} ca_tel_dial_options_t;

/* Construct with record defaults (RingTimeoutSeconds=30, rest cleared). */
ca_tel_dial_options_t *ca_tel_dial_options_new(void);
void ca_tel_dial_options_destroy(ca_tel_dial_options_t *o);
void ca_tel_dial_options_set_caller_id(ca_tel_dial_options_t *o, const char *cid);
int  ca_tel_dial_options_add_follow_me(ca_tel_dial_options_t *o, const char *num);

/* ===========================================================================
 * DtmfToneGenerator — deterministic dual-tone PCM-16 synthesis.
 * =========================================================================== */

/* Generate(digit, sampleRateHz, durationMs=150, amplitude=0.5): PCM-16 mono LE.
 * Returns a freshly-owned buffer of `*out_len` bytes (samples*2), NULL on bad
 * args (sampleRateHz<=0, durationMs<=0, unsupported digit) or OOM. */
uint8_t *ca_tel_dtmf_generate(char digit, int sample_rate_hz, int duration_ms,
                              float amplitude, size_t *out_len);

/* GenerateSequence(digits, sampleRateHz, toneDurationMs=150, interDigitGapMs=50,
 * amplitude=0.5): each tone followed by gap silence except after the last. Empty
 * digits -> zero-length (out_len=0, returns a valid 0-length pointer sentinel).
 * NULL on bad args / OOM. */
uint8_t *ca_tel_dtmf_generate_sequence(const char *digits, int sample_rate_hz,
                                       int tone_duration_ms, int inter_digit_gap_ms,
                                       float amplitude, size_t *out_len);

/* ===========================================================================
 * IMediaStream — host media channel.
 *
 * Two concrete shapes ship here:
 *   ManualMediaStream : an in-memory channel a test/host drives — inject inbound
 *                       audio/DTMF, capture outbound audio, flip status. Supports
 *                       out-of-band DTMF (IDtmfSendable) — captured to SentDtmf.
 *   PendingMediaStream : the "dial accepted, WebSocket not yet attached" stream
 *                        the carrier bindings return. Yields no audio; SendAudio
 *                        errors; EndAsync flips to EndedByAgent + fires status.
 *
 * The C# duplex Channels are unbounded: inbound audio/DTMF are FIFO cursors
 * drained with *_receive_audio_next / *_receive_dtmf_next. StatusChanged is a
 * snapshot-then-invoke broadcast.
 * =========================================================================== */

typedef struct ca_tel_media_stream ca_tel_media_stream_t;

/* StatusChanged handler. ctx is the value passed at subscribe time. */
typedef void (*ca_tel_status_handler_fn)(void *ctx, ca_tel_call_status_t status);

/* Opaque subscription token (dispose to unsubscribe). */
typedef struct ca_tel_status_sub ca_tel_status_sub_t;

/* ── ManualMediaStream ──────────────────────────────────────────────────── */

/* Create over a copy of `info` (required). CurrentStatus starts Ringing (the C#
 * default a real host reports before "connected"); use *_set_status to advance.
 * supports_native_dtmf models a host that layers IDtmfSendable. NULL on OOM. */
ca_tel_media_stream_t *ca_tel_manual_media_create(const ca_tel_call_info_t *info,
                                                  ca_tel_call_status_t initial_status,
                                                  bool supports_native_dtmf);

/* PendingMediaStream(info): the pre-attach stream. CurrentStatus Ringing. */
ca_tel_media_stream_t *ca_tel_pending_media_create(const ca_tel_call_info_t *info);

void ca_tel_media_stream_destroy(ca_tel_media_stream_t *m);

/* CallInfo (borrowed). */
const ca_tel_call_info_t *ca_tel_media_stream_info(const ca_tel_media_stream_t *m);
/* CurrentStatus. */
ca_tel_call_status_t ca_tel_media_stream_status(const ca_tel_media_stream_t *m);
/* IDtmfSendable — does this stream accept out-of-band DTMF? */
bool ca_tel_media_stream_supports_native_dtmf(const ca_tel_media_stream_t *m);

/* SendAudioAsync(frame): ManualMediaStream captures a deep copy (see
 * *_sent_audio_*); PendingMediaStream returns -1 (the C# InvalidOperationException
 * "cannot send before attach"). frame required (pcm may be NULL only when
 * pcm_len==0). 0 / -1. */
int ca_tel_media_stream_send_audio(ca_tel_media_stream_t *m,
                                   const ca_tel_audio_frame_t *frame);

/* IDtmfSendable.SendDtmfAsync(digits): ManualMediaStream (when it supports native
 * DTMF) appends the string to SentDtmf and returns 0; a stream that does NOT
 * support native DTMF returns -1 so the session falls back to in-band tones.
 * Pending always -1. */
int ca_tel_media_stream_send_dtmf(ca_tel_media_stream_t *m, const char *digits);

/* EndAsync: flips CurrentStatus to EndedByAgent and fires StatusChanged, then
 * completes the inbound streams. 0 / -1. */
int ca_tel_media_stream_end(ca_tel_media_stream_t *m);

/* Inject one inbound audio frame (ManualMediaStream). Deep-copied. 0 / -1. */
int ca_tel_manual_media_inject_audio(ca_tel_media_stream_t *m,
                                     const ca_tel_audio_frame_t *frame);
/* Inject one inbound DTMF event (ManualMediaStream). 0 / -1. */
int ca_tel_manual_media_inject_dtmf(ca_tel_media_stream_t *m,
                                    const ca_tel_dtmf_event_t *ev);
/* Complete the inbound audio + DTMF streams cleanly. */
void ca_tel_manual_media_end_inbound(ca_tel_media_stream_t *m);

/* Set CurrentStatus and fire StatusChanged (idempotent: no fire when unchanged,
 * matching a real host that only reports transitions). */
void ca_tel_media_stream_set_status(ca_tel_media_stream_t *m,
                                    ca_tel_call_status_t status);

/* ReceiveAudioAsync -> drain next inbound frame into *out (freshly owned; free
 * with ca_tel_audio_frame_free). true if produced, false when empty. */
bool ca_tel_media_stream_receive_audio_next(ca_tel_media_stream_t *m,
                                            ca_tel_audio_frame_t *out);
size_t ca_tel_media_stream_audio_pending(const ca_tel_media_stream_t *m);

/* ReceiveDtmfAsync -> drain next inbound DTMF into *out. true / false. */
bool ca_tel_media_stream_receive_dtmf_next(ca_tel_media_stream_t *m,
                                           ca_tel_dtmf_event_t *out);
size_t ca_tel_media_stream_dtmf_pending(const ca_tel_media_stream_t *m);

/* Captured outbound audio (SentAudioFrames). Returns count; *out (owned array,
 * free with ca_tel_audio_frame_free per item + free the array) or NULL. */
ca_tel_audio_frame_t *ca_tel_media_stream_sent_audio(const ca_tel_media_stream_t *m,
                                                     size_t *count);
size_t ca_tel_media_stream_sent_audio_count(const ca_tel_media_stream_t *m);

/* Captured outbound DTMF strings (from native SendDtmf). *count set; returns an
 * owned NULL-terminated-per-item array of owned strings, or NULL. */
char **ca_tel_media_stream_sent_dtmf(const ca_tel_media_stream_t *m, size_t *count);
size_t ca_tel_media_stream_sent_dtmf_count(const ca_tel_media_stream_t *m);

/* Subscribe / unsubscribe to StatusChanged. Returns an owned token or NULL. */
ca_tel_status_sub_t *ca_tel_media_stream_subscribe_status(
    ca_tel_media_stream_t *m, ca_tel_status_handler_fn handler, void *ctx);
void ca_tel_status_unsubscribe(ca_tel_status_sub_t *sub);

/* ===========================================================================
 * ICallSession — the agent-facing live call handle.
 *
 * Two concrete shapes:
 *   TestCallSession    : the in-memory harness session (TestCallSession.cs) —
 *                        inject inbound audio/DTMF, capture outbound, drive
 *                        lifecycle. Default status Active. Standalone (no media
 *                        stream).
 *   MediaCallSession   : the carrier binding session — wraps an IMediaStream and
 *                        an ITelephonyCarrier and mirrors Twilio/Telnyx/Plivo
 *                        CallSession semantics. Built by the bindings; see
 *                        ca_tel_media_call_session_create.
 * =========================================================================== */

typedef struct ca_tel_call_session ca_tel_call_session_t;

/* Forward decl — defined below. */
typedef struct ca_tel_carrier ca_tel_carrier_t;

/* ── TestCallSession ────────────────────────────────────────────────────── */

/* TestCallSession(info=NULL): when info is NULL the C# default is used
 * (random CallId, Inbound, +15555550100 -> +15555550200, carrier "test",
 * Pcm16000). Status starts Active. NULL on OOM. */
ca_tel_call_session_t *ca_tel_test_call_session_create(const ca_tel_call_info_t *info);

/* ── MediaCallSession (carrier binding session) ─────────────────────────── */

/* Wrap `media` (ownership transferred — destroyed with the session) as an
 * ICallSession bound to `carrier`. Mirrors {Twilio,Telnyx,Plivo}CallSession:
 * Status folds the media's status with a locally-latched _status (Ringing seed);
 * Transfer/HangUp drive the carrier's REST helpers via its vtable; SendDtmf uses
 * the media's native DTMF when supported else in-band tones. `carrier` is
 * borrowed (must outlive the session). NULL on bad args / OOM. */
ca_tel_call_session_t *ca_tel_media_call_session_create(ca_tel_media_stream_t *media,
                                                        ca_tel_carrier_t *carrier);

void ca_tel_call_session_destroy(ca_tel_call_session_t *s);

/* Info (borrowed). For MediaCallSession this is the media stream's CallInfo. */
const ca_tel_call_info_t *ca_tel_call_session_info(const ca_tel_call_session_t *s);
/* Status. */
ca_tel_call_status_t ca_tel_call_session_status(const ca_tel_call_session_t *s);

/* ReceiveAudioAsync -> drain next inbound frame. true / false. */
bool ca_tel_call_session_receive_audio_next(ca_tel_call_session_t *s,
                                            ca_tel_audio_frame_t *out);
size_t ca_tel_call_session_audio_pending(const ca_tel_call_session_t *s);

/* ReceiveDtmfAsync -> drain next inbound DTMF. true / false. */
bool ca_tel_call_session_receive_dtmf_next(ca_tel_call_session_t *s,
                                           ca_tel_dtmf_event_t *out);
size_t ca_tel_call_session_dtmf_pending(const ca_tel_call_session_t *s);

/* SendAudioAsync(frame). 0 / -1. */
int ca_tel_call_session_send_audio(ca_tel_call_session_t *s,
                                   const ca_tel_audio_frame_t *frame);

/* SendDtmfAsync(digits). Empty/NULL digits is a no-op success (0). For the media
 * session: native DTMF when the stream supports it, else in-band tones appended
 * to outbound audio at the format's sample rate. 0 / -1. */
int ca_tel_call_session_send_dtmf(ca_tel_call_session_t *s, const char *digits);

/* TransferAsync(target, mode, briefing=NULL). TestCallSession: flips status to
 * Transferred regardless of mode. MediaCallSession: issues the carrier's transfer
 * (cold TwiML/REST) and latches Transferred. 0 / -1. */
int ca_tel_call_session_transfer(ca_tel_call_session_t *s, const char *target,
                                 ca_tel_transfer_mode_t mode, const char *briefing);

/* HangUpAsync. TestCallSession: status EndedByAgent + completes inbound streams.
 * MediaCallSession: latches EndedByAgent, ends the media, calls carrier EndCall.
 * 0 / -1. */
int ca_tel_call_session_hangup(ca_tel_call_session_t *s);

/* ── TestCallSession drive/capture surface ──────────────────────────────── */

/* InjectInboundAudio(frame). Deep-copied. 0 / -1. */
int ca_tel_test_call_session_inject_audio(ca_tel_call_session_t *s,
                                          const ca_tel_audio_frame_t *frame);
/* InjectInboundDtmf(ev). 0 / -1. */
int ca_tel_test_call_session_inject_dtmf(ca_tel_call_session_t *s,
                                         const ca_tel_dtmf_event_t *ev);
/* EndInboundStreams(). */
void ca_tel_test_call_session_end_inbound(ca_tel_call_session_t *s);
/* TriggerStatusChange(newStatus): sets status and fires StatusChanged. */
void ca_tel_test_call_session_trigger_status(ca_tel_call_session_t *s,
                                             ca_tel_call_status_t status);

/* SentAudioFrames — captured outbound audio (deep copies). count set; owned
 * array or NULL. */
ca_tel_audio_frame_t *ca_tel_call_session_sent_audio(const ca_tel_call_session_t *s,
                                                     size_t *count);
size_t ca_tel_call_session_sent_audio_count(const ca_tel_call_session_t *s);

/* SentDtmf — outbound DTMF strings the AI emitted. count set; owned array of owned
 * strings or NULL. */
char **ca_tel_call_session_sent_dtmf(const ca_tel_call_session_t *s, size_t *count);
size_t ca_tel_call_session_sent_dtmf_count(const ca_tel_call_session_t *s);

/* Subscribe / unsubscribe to StatusChanged. Owned token or NULL. */
ca_tel_status_sub_t *ca_tel_call_session_subscribe_status(
    ca_tel_call_session_t *s, ca_tel_status_handler_fn handler, void *ctx);

/* ===========================================================================
 * IInboundCallDispatcher — the carrier-fed inbound handler.
 *
 * InMemoryInboundCallDispatcher lets a host Publish a session; each subscribed
 * handler receives it. NullInboundCallDispatcher never fires. Because the C#
 * Channel is unbounded and a subscriber attaching after a publish must still be
 * able to observe it in a race-free way, the in-memory dispatcher delivers a
 * published session SYNCHRONOUSLY to every current subscriber AND buffers it for
 * subscribers that attach later (bounded-retain replay), so a session published
 * before Subscribe is not lost.
 * =========================================================================== */

typedef struct ca_tel_dispatcher ca_tel_dispatcher_t;
typedef struct ca_tel_dispatcher_sub ca_tel_dispatcher_sub_t;

/* Handler receives a BORROWED session (owned by the publisher). */
typedef void (*ca_tel_inbound_handler_fn)(void *ctx, ca_tel_call_session_t *session);

/* NullInboundCallDispatcher — CarrierId "null"; Subscribe returns a live token
 * that never fires. */
ca_tel_dispatcher_t *ca_tel_null_dispatcher_create(void);

/* InMemoryInboundCallDispatcher(carrierId). NULL on OOM. */
ca_tel_dispatcher_t *ca_tel_inmemory_dispatcher_create(const char *carrier_id);

void ca_tel_dispatcher_destroy(ca_tel_dispatcher_t *d);

/* CarrierId (borrowed). */
const char *ca_tel_dispatcher_carrier_id(const ca_tel_dispatcher_t *d);

/* Subscribe(handler) -> owned token (dispose to unsubscribe). On subscribe, any
 * sessions already published are replayed to this handler in order. Null
 * dispatcher returns a token that never fires. NULL on OOM. */
ca_tel_dispatcher_sub_t *ca_tel_dispatcher_subscribe(
    ca_tel_dispatcher_t *d, ca_tel_inbound_handler_fn handler, void *ctx);
void ca_tel_dispatcher_unsubscribe(ca_tel_dispatcher_sub_t *sub);

/* Publish a session to all current subscribers (and retain for future ones).
 * Returns the number of subscribers notified now. Null dispatcher: 0. */
int ca_tel_dispatcher_publish(ca_tel_dispatcher_t *d, ca_tel_call_session_t *session);

/* ===========================================================================
 * IToolCallRegistry — DefaultToolCallRegistry.
 *
 * ToolDefinition/Invocation/Result records + a registry that dispatches a tool
 * call to a local handler fn OR an injected HTTP webhook poster. Case-insensitive
 * tool names (StringComparer.OrdinalIgnoreCase); last registration wins.
 * =========================================================================== */

typedef struct {
    char *name;                  /* owned */
    char *description;           /* owned */
    char *arguments_json_schema; /* owned */
} ca_tel_tool_definition_t;

void ca_tel_tool_definition_free(ca_tel_tool_definition_t *d);

typedef struct {
    char *call_id;        /* owned */
    char *tool_name;      /* owned */
    char *arguments_json; /* owned */
} ca_tel_tool_invocation_t;

typedef struct {
    char *call_id;      /* owned */
    bool  succeeded;
    char *result_json;  /* owned */
    char *error;        /* owned or NULL */
} ca_tel_tool_result_t;

void ca_tel_tool_result_free(ca_tel_tool_result_t *r);

/* LocalToolHandler(argumentsJson) -> resultJson. Writes a freshly-owned result
 * string into *out_result and returns 0 on success; returns -1 to model a thrown
 * exception (registry surfaces Succeeded=false with a generic message). A NULL
 * *out_result on success is treated as "{}" (mirrors `resultJson ?? "{}"`). */
typedef int (*ca_tel_local_tool_handler_fn)(void *ctx, const char *arguments_json,
                                            char **out_result);

/* Injected webhook poster (the HttpClient seam — no real network). POST the
 * envelope JSON body to `url`. Writes the HTTP status into *out_status and a
 * freshly-owned response body into *out_body, returns 0. Return -1 to model an
 * HttpRequestException thrown before any response (connection failure). */
typedef int (*ca_tel_webhook_poster_fn)(void *ctx, const char *url,
                                        const char *json_body,
                                        int *out_status, char **out_body);

typedef struct ca_tel_tool_registry ca_tel_tool_registry_t;

/* DefaultToolCallRegistry(poster). `poster` is the injected webhook seam (may be
 * NULL — then webhook tools fail with a connection error). NULL on OOM. */
ca_tel_tool_registry_t *ca_tel_tool_registry_create(ca_tel_webhook_poster_fn poster,
                                                    void *poster_ctx);
void ca_tel_tool_registry_destroy(ca_tel_tool_registry_t *r);

/* RegisterLocal(definition, handler). definition.Name required (non-whitespace).
 * 0 / -1 (bad args). */
int ca_tel_tool_registry_register_local(ca_tel_tool_registry_t *r,
                                        const ca_tel_tool_definition_t *definition,
                                        ca_tel_local_tool_handler_fn handler,
                                        void *handler_ctx);
/* RegisterWebhook(definition, url). url must be absolute (http/https scheme);
 * definition.Name required. 0 / -1 (bad args). */
int ca_tel_tool_registry_register_webhook(ca_tel_tool_registry_t *r,
                                          const ca_tel_tool_definition_t *definition,
                                          const char *webhook_url);

/* Definitions — all registered definitions. count set; owned array (free each
 * with ca_tel_tool_definition_free then free the array) or NULL. Order matches
 * insertion (linear). */
ca_tel_tool_definition_t *ca_tel_tool_registry_definitions(
    const ca_tel_tool_registry_t *r, size_t *count);
size_t ca_tel_tool_registry_definition_count(const ca_tel_tool_registry_t *r);

/* InvokeAsync(invocation) -> result (freshly owned; free with
 * ca_tel_tool_result_free). Never returns NULL for a valid call — an unregistered
 * tool yields Succeeded=false, ResultJson="{}", Error set. NULL only on bad args
 * / OOM. The webhook envelope is
 *   {"call_id":"<id>","tool":"<name>","arguments":<argsJson>}
 * with `arguments` inlined as raw JSON. */
ca_tel_tool_result_t *ca_tel_tool_registry_invoke(
    ca_tel_tool_registry_t *r, const ca_tel_tool_invocation_t *invocation);

/* ===========================================================================
 * ITelephonyCarrier — the carrier abstraction (vtable).
 *
 * The bindings (Twilio/Telnyx/Plivo) and NullTelephonyCarrier / CarrierFallback
 * all wear this handle. Operations return status codes; DialAsync returns a
 * fresh ICallSession the caller owns.
 * =========================================================================== */

/* ca_tel_carrier_t declared above (forward). */

/* Vtable a binding implements. All ops receive the carrier's `self`. */
typedef struct {
    /* CarrierId (borrowed, stable). */
    const char *(*carrier_id)(void *self);
    /* IsConfigured. */
    bool (*is_configured)(void *self);
    /* ProvisionNumberAsync(countryCode, areaCode?) -> *out (freshly owned) + 0;
     * -1 on failure (unconfigured, no availability, transport error). */
    int (*provision_number)(void *self, const char *country_code,
                            const char *area_code,
                            ca_tel_provisioned_number_t *out);
    /* ConfigureInboundWebhookAsync(phoneNumber, webhookUrl). 0 / -1. */
    int (*configure_inbound)(void *self, const char *phone_number,
                            const char *webhook_url);
    /* DialAsync(from, to, streamUrl, options?) -> a fresh owned ICallSession, or
     * NULL on failure. `carrier` is the wrapping handle (so the session can call
     * back into REST helpers). */
    ca_tel_call_session_t *(*dial)(void *self, ca_tel_carrier_t *carrier,
                                   const char *from, const char *to,
                                   const char *stream_url,
                                   const ca_tel_dial_options_t *options);
    /* ListNumbersAsync -> *out_arr (owned, may be NULL when 0) + count; count
     * SIZE_MAX on hard error (the C# returns empty on non-2xx, so this is only
     * for OOM / bad args). */
    ca_tel_provisioned_number_t *(*list_numbers)(void *self, size_t *count);
    /* Internal REST helpers used by the session on transfer/hangup. May be NULL
     * (Null carrier). end_call(callId); transfer_call(callId, target). 0 / -1. */
    int (*end_call)(void *self, const char *call_id);
    int (*transfer_call)(void *self, const char *call_id, const char *target);
    /* Release `self`. May be NULL. */
    void (*destroy)(void *self);
} ca_tel_carrier_vtable_t;

/* Wrap a binding impl (`self` + vtable) as a carrier handle. Takes ownership of
 * `self` (destroyed via vtable.destroy on ca_tel_carrier_destroy). NULL on OOM. */
ca_tel_carrier_t *ca_tel_carrier_wrap(void *self,
                                      const ca_tel_carrier_vtable_t *vtable);
void ca_tel_carrier_destroy(ca_tel_carrier_t *c);

/* NullTelephonyCarrier — CarrierId "null"; IsConfigured false; ProvisionNumber
 * and Dial fail (-1 / NULL); ConfigureInboundWebhook is a no-op success;
 * ListNumbers is empty. */
ca_tel_carrier_t *ca_tel_null_carrier_create(void);

/* CarrierFallback(carriers[]): picks the first configured carrier for each op
 * (or the Null carrier when none configured). Takes ownership of the passed
 * carrier handles (destroyed with the fallback). CarrierId "fallback(<n>)".
 * NULL on OOM. */
ca_tel_carrier_t *ca_tel_carrier_fallback_create(ca_tel_carrier_t **carriers,
                                                 size_t count);

/* Carrier operations (dispatch to the vtable). */
const char *ca_tel_carrier_id(ca_tel_carrier_t *c);
bool        ca_tel_carrier_is_configured(ca_tel_carrier_t *c);
int         ca_tel_carrier_provision_number(ca_tel_carrier_t *c,
                                            const char *country_code,
                                            const char *area_code,
                                            ca_tel_provisioned_number_t *out);
int         ca_tel_carrier_configure_inbound(ca_tel_carrier_t *c,
                                             const char *phone_number,
                                             const char *webhook_url);
ca_tel_call_session_t *ca_tel_carrier_dial(ca_tel_carrier_t *c,
                                           const char *from, const char *to,
                                           const char *stream_url,
                                           const ca_tel_dial_options_t *options);
ca_tel_provisioned_number_t *ca_tel_carrier_list_numbers(ca_tel_carrier_t *c,
                                                         size_t *count);

/* ===========================================================================
 * Injected HTTP transport for the carrier bindings.
 *
 * The bindings speak REST but perform NO real network — every request goes
 * through this vtable, which a test/host supplies. The binding builds the exact
 * method + path + body + auth header the C# adapter would, hands them here, and
 * parses the returned status + JSON body. This is the "real HTTP carrier is an
 * injected dependency" seam.
 * =========================================================================== */

typedef struct {
    void *self;
    /* Perform one request. `method` is "GET"/"POST"/"PATCH"/"DELETE".
     * `path` is the request path (already relative to the carrier base URL, with
     * query string). `auth_header` is the full Authorization header value the
     * binding computed (e.g. "Basic <b64>" / "Bearer <key>") or NULL.
     * `content_type` + `body` describe the request entity (body may be NULL for
     * GET/DELETE). Writes the HTTP status into *out_status and a freshly-owned
     * response body string into *out_body (may be set to NULL for empty), and
     * returns 0. Return -1 to model a transport exception thrown before any
     * response. */
    int (*request)(void *self, const char *method, const char *path,
                   const char *auth_header, const char *content_type,
                   const char *body, int *out_status, char **out_body);
} ca_tel_http_t;

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_TELEPHONY_H */
