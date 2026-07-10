/*
 * realtime.c — CircleAI.Realtime + CircleAI.Realtime.Cloud (C11 port).
 *
 * CircleAI.Realtime       : enums, records, RealtimeEvent union,
 *                           Loopback + Null session/service.
 * CircleAI.Realtime.Cloud : IRealtimeTransport(Factory) vtables +
 *                           NullRealtimeTransportFactory.
 *
 * Pure C11 + libc + libm. Linear FIFO cursors (unbounded, no drops), no pthreads.
 */

#include "circle_ai/realtime.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <math.h>
#include <time.h>

/* ── helpers ────────────────────────────────────────────────────────────── */

static char *rt_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *rt_strdup_empty(const char *s) { return rt_strdup(s ? s : ""); }

static bool rt_is_ws(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (!isspace((unsigned char)*p)) return false;
    return true;
}

/* Unix ms UTC now (DateTimeOffset.UtcNow surrogate). Monotone-ish, not the wire
 * contract — the C# stamps events with the wall clock at emit time. */
static int64_t rt_now_ms(void) {
    return (int64_t)time(NULL) * 1000;
}

/* Guid.NewGuid():N surrogate — 32 lowercase hex, unique within the process. */
static char *rt_new_loop_id(void) {
    static uint64_t counter = 0;
    counter++;
    uint64_t a = (uint64_t)time(NULL);
    uint64_t b = counter * 0x9E3779B97F4A7C15ULL + a;
    char hex[33];
    snprintf(hex, sizeof(hex), "%08x%08x%08x%08x",
             (unsigned)(a & 0xffffffff), (unsigned)(b & 0xffffffff),
             (unsigned)((b >> 16) & 0xffffffff), (unsigned)((a >> 8) & 0xffffffff));
    size_t n = 5 /* "loop-" */ + 33;
    char *out = (char *)malloc(n);
    if (out) snprintf(out, n, "loop-%s", hex);
    return out;
}

int ca_rt_sample_rate_of(ca_rt_audio_format_t f) {
    switch (f) {
        case CA_RT_FMT_PCM16K:  return 16000;
        case CA_RT_FMT_PCM24K:  return 24000;
        case CA_RT_FMT_MULAW8K: return 8000;
        default:                return 16000;
    }
}

/* ── records ────────────────────────────────────────────────────────────── */

void ca_rt_tool_free(ca_rt_tool_t *t) {
    if (!t) return;
    free(t->name);
    free(t->description);
    free(t->json_schema);
    t->name = t->description = t->json_schema = NULL;
}

void ca_rt_session_config_free(ca_rt_session_config_t *c) {
    if (!c) return;
    free(c->model);
    free(c->voice_id);
    free(c->system_prompt);
    free(c->language_hint);
    for (size_t i = 0; i < c->tool_count; ++i) ca_rt_tool_free(&c->tools[i]);
    free(c->tools);
    c->model = c->voice_id = c->system_prompt = c->language_hint = NULL;
    c->tools = NULL;
    c->tool_count = 0;
}

void ca_rt_audio_frame_free(ca_rt_audio_frame_t *f) {
    if (!f) return;
    free(f->pcm);
    f->pcm = NULL;
    f->pcm_len = 0;
}

void ca_rt_event_free(ca_rt_event_t *e) {
    if (!e) return;
    free(e->text);
    free(e->call_id);
    free(e->tool_name);
    free(e->arguments_json);
    free(e->message);
    e->text = e->call_id = e->tool_name = e->arguments_json = e->message = NULL;
}

/* ── unbounded FIFO of audio frames ─────────────────────────────────────── */

typedef struct {
    ca_rt_audio_frame_t *items;
    size_t head, count, cap;
} frame_fifo_t;

static bool frame_fifo_push(frame_fifo_t *q, ca_rt_audio_frame_t item) {
    if (q->count == q->cap) {
        if (q->head > 0) {
            size_t live = q->count - q->head;
            memmove(q->items, q->items + q->head, live * sizeof(*q->items));
            q->count = live; q->head = 0;
        }
        if (q->count == q->cap) {
            size_t nc = q->cap ? q->cap * 2 : 4;
            void *ni = realloc(q->items, nc * sizeof(*q->items));
            if (!ni) return false;
            q->items = (ca_rt_audio_frame_t *)ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = item;
    return true;
}
static bool frame_fifo_pop(frame_fifo_t *q, ca_rt_audio_frame_t *out) {
    if (q->head >= q->count) return false;
    *out = q->items[q->head];
    memset(&q->items[q->head], 0, sizeof(q->items[q->head]));
    q->head++;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return true;
}
static void frame_fifo_free(frame_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i) ca_rt_audio_frame_free(&q->items[i]);
    free(q->items);
    q->items = NULL;
    q->head = q->count = q->cap = 0;
}

/* ── unbounded FIFO of events ───────────────────────────────────────────── */

typedef struct {
    ca_rt_event_t *items;
    size_t head, count, cap;
} event_fifo_t;

static bool event_fifo_push(event_fifo_t *q, ca_rt_event_t item) {
    if (q->count == q->cap) {
        if (q->head > 0) {
            size_t live = q->count - q->head;
            memmove(q->items, q->items + q->head, live * sizeof(*q->items));
            q->count = live; q->head = 0;
        }
        if (q->count == q->cap) {
            size_t nc = q->cap ? q->cap * 2 : 4;
            void *ni = realloc(q->items, nc * sizeof(*q->items));
            if (!ni) return false;
            q->items = (ca_rt_event_t *)ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = item;
    return true;
}
static bool event_fifo_pop(event_fifo_t *q, ca_rt_event_t *out) {
    if (q->head >= q->count) return false;
    *out = q->items[q->head];
    memset(&q->items[q->head], 0, sizeof(q->items[q->head]));
    q->head++;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return true;
}
static void event_fifo_free(event_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i) ca_rt_event_free(&q->items[i]);
    free(q->items);
    q->items = NULL;
    q->head = q->count = q->cap = 0;
}

/* ── event constructors (push helpers) ──────────────────────────────────── */

static void emit_simple(event_fifo_t *q, ca_rt_event_type_t type) {
    ca_rt_event_t e;
    memset(&e, 0, sizeof(e));
    e.type = type;
    e.at_utc_ms = rt_now_ms();
    if (!event_fifo_push(q, e)) ca_rt_event_free(&e);
}
static void emit_transcript(event_fifo_t *q, ca_rt_event_type_t type,
                            const char *text, ca_rt_direction_t dir) {
    ca_rt_event_t e;
    memset(&e, 0, sizeof(e));
    e.type = type;
    e.at_utc_ms = rt_now_ms();
    e.direction = dir;
    e.text = rt_strdup_empty(text);
    if (!e.text || !event_fifo_push(q, e)) ca_rt_event_free(&e);
}

/* ── built-in silence TTS (SilenceTextToAudio) ──────────────────────────── */

/* wordCount = split on ' '/'\t'/'\n' RemoveEmptyEntries. */
static size_t count_words(const char *text) {
    if (!text) return 0;
    size_t words = 0;
    bool in_word = false;
    for (const char *p = text; *p; ++p) {
        char c = *p;
        bool sep = (c == ' ' || c == '\t' || c == '\n');
        if (sep) { in_word = false; }
        else if (!in_word) { in_word = true; words++; }
    }
    return words;
}

/* SilenceTextToAudio: bytes[sampleCount*2] of zeros, sampleCount = sr*durMs/1000,
 * durMs = max(50, words*80). Returns 0 and allocates *out_pcm (may be 0-length ->
 * NULL). */
static int silence_text_to_audio(void *ctx, const char *text,
                                 ca_rt_audio_format_t format,
                                 uint8_t **out_pcm, size_t *out_len) {
    (void)ctx;
    int sr = ca_rt_sample_rate_of(format);
    size_t words = rt_is_ws(text) ? 0 : count_words(text);
    long dur_ms = (long)(words * 80);
    if (dur_ms < 50) dur_ms = 50;
    size_t sample_count = (size_t)((long long)sr * dur_ms / 1000);
    size_t nbytes = sample_count * 2;
    if (nbytes == 0) { *out_pcm = NULL; *out_len = 0; return 0; }
    uint8_t *buf = (uint8_t *)calloc(nbytes, 1);   /* 16-bit silence (zeros) */
    if (!buf) return -1;
    *out_pcm = buf;
    *out_len = nbytes;
    return 0;
}

/* ── IsSilent — RMS over int16 LE, threshold 250, <64 bytes == silent ───── */

static bool is_silent(const uint8_t *pcm, size_t len) {
    if (len < 64) return true;
    long long sum_sq = 0;
    size_t samples = len / 2;
    for (size_t i = 0; i + 1 < len; i += 2) {
        int16_t s = (int16_t)((uint16_t)pcm[i] | ((uint16_t)pcm[i + 1] << 8));
        sum_sq += (long long)s * (long long)s;
    }
    double rms = sqrt((double)sum_sq / (double)samples);
    return rms < 250.0;
}

/* Truncate(s, max): s if <= max else first `max` chars + "…" (U+2026, UTF-8
 * 0xE2 0x80 0xA6). Returns a freshly owned string, or NULL on OOM. */
static char *truncate_ellipsis(const char *s, size_t max) {
    size_t len = s ? strlen(s) : 0;
    if (len <= max) return rt_strdup_empty(s);
    char *out = (char *)malloc(max + 3 + 1);   /* max bytes + 3-byte ellipsis + NUL */
    if (!out) return NULL;
    memcpy(out, s, max);
    out[max]     = (char)0xE2;
    out[max + 1] = (char)0x80;
    out[max + 2] = (char)0xA6;
    out[max + 3] = '\0';
    return out;
}

/* ===========================================================================
 * IRealtimeSession — Loopback + Null
 * =========================================================================== */

struct ca_rt_session {
    bool                   is_null;
    char                  *session_id;   /* owned */
    ca_rt_audio_format_t   format;       /* from config */
    ca_rt_text_to_audio_fn tts;          /* NULL -> built-in silence */
    void                  *tts_ctx;
    int64_t                offset_ticks; /* _offset accumulator (TimeSpan ticks) */
    bool                   speaking;     /* _speaking */
    frame_fifo_t           audio;        /* _audio channel */
    event_fifo_t           events;       /* _events channel */
};

ca_rt_session_t *ca_rt_null_session_create(void) {
    ca_rt_session_t *s = (ca_rt_session_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->is_null = true;
    s->session_id = rt_strdup("null");
    if (!s->session_id) { free(s); return NULL; }
    return s;
}

ca_rt_session_t *ca_rt_loopback_session_create(const ca_rt_session_config_t *config,
                                               ca_rt_text_to_audio_fn text_to_audio,
                                               void *tts_ctx) {
    if (!config) return NULL;
    ca_rt_session_t *s = (ca_rt_session_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->is_null = false;
    s->format  = config->audio_format;
    s->tts     = text_to_audio ? text_to_audio : silence_text_to_audio;
    s->tts_ctx = text_to_audio ? tts_ctx : NULL;
    s->session_id = rt_new_loop_id();
    if (!s->session_id) { free(s); return NULL; }
    return s;
}

void ca_rt_session_destroy(ca_rt_session_t *s) {
    if (!s) return;
    frame_fifo_free(&s->audio);
    event_fifo_free(&s->events);
    free(s->session_id);
    free(s);
}

const char *ca_rt_session_id(const ca_rt_session_t *s) {
    return s ? s->session_id : NULL;
}

/* Deep-copy a frame's pcm into a fresh owned frame value. false on OOM. */
static bool frame_copy(ca_rt_audio_frame_t *dst, const ca_rt_audio_frame_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->format = src->format;
    dst->offset_ticks = src->offset_ticks;
    dst->pcm_len = src->pcm_len;
    if (src->pcm_len > 0) {
        dst->pcm = (uint8_t *)malloc(src->pcm_len);
        if (!dst->pcm) return false;
        if (src->pcm) memcpy(dst->pcm, src->pcm, src->pcm_len);
        else memset(dst->pcm, 0, src->pcm_len);
    }
    return true;
}

int ca_rt_session_send_audio(ca_rt_session_t *s, const ca_rt_audio_frame_t *frame) {
    if (!s || !frame) return -1;
    if (frame->pcm_len > 0 && !frame->pcm) return -1;
    if (s->is_null) return 0;   /* NullRealtimeSession -> CompletedTask */

    bool now_speaking = !is_silent(frame->pcm, frame->pcm_len);
    if (now_speaking != s->speaking) {
        emit_simple(&s->events, now_speaking ? CA_RT_EVT_SPEECH_STARTED
                                             : CA_RT_EVT_SPEECH_ENDED);
        s->speaking = now_speaking;
    }
    /* Loopback: echo received audio back as outbound. */
    ca_rt_audio_frame_t echo;
    if (!frame_copy(&echo, frame)) return -1;
    if (!frame_fifo_push(&s->audio, echo)) { ca_rt_audio_frame_free(&echo); return -1; }
    return 0;
}

int ca_rt_session_send_text(ca_rt_session_t *s, const char *text) {
    if (!s || !text) return -1;
    if (s->is_null) return 0;

    emit_transcript(&s->events, CA_RT_EVT_TRANSCRIPT_DELTA, text, CA_RT_DIR_OUTBOUND);

    uint8_t *pcm = NULL;
    size_t pcm_len = 0;
    if (s->tts(s->tts_ctx, text, s->format, &pcm, &pcm_len) != 0) {
        /* synthesis failed — still surface final + turn-complete? The C# awaits
         * the delegate, so a throw would abort. Mirror by returning -1. */
        return -1;
    }
    if (pcm_len > 0) {
        ca_rt_audio_frame_t f;
        memset(&f, 0, sizeof(f));
        f.pcm = pcm;          /* transfer ownership */
        f.pcm_len = pcm_len;
        f.format = s->format;
        f.offset_ticks = s->offset_ticks;
        if (!frame_fifo_push(&s->audio, f)) { ca_rt_audio_frame_free(&f); return -1; }
        /* _offset += FromMilliseconds(pcm.Length / 2.0 / sr * 1000.0).
         * TimeSpan.FromMilliseconds rounds to the nearest tick (1 tick = 100ns,
         * 10000 ticks/ms). */
        double delta_ms = (double)pcm_len / 2.0 /
                          (double)ca_rt_sample_rate_of(s->format) * 1000.0;
        double delta_ticks = delta_ms * 10000.0;
        s->offset_ticks += (int64_t)llround(delta_ticks);
    } else {
        free(pcm);   /* 0-length: nothing to enqueue */
    }

    emit_transcript(&s->events, CA_RT_EVT_TRANSCRIPT_FINAL, text, CA_RT_DIR_OUTBOUND);
    emit_simple(&s->events, CA_RT_EVT_TURN_COMPLETE);
    return 0;
}

int ca_rt_session_send_tool_result(ca_rt_session_t *s, const char *call_id,
                                   const char *result_json) {
    if (!s) return -1;
    if (rt_is_ws(call_id)) return -1;        /* ArgumentException("callId required") */
    if (!result_json) return -1;             /* ArgumentNullException(resultJson) */
    if (s->is_null) return 0;

    char *trunc = truncate_ellipsis(result_json, 60);
    if (!trunc) return -1;
    /* "[tool <callId>: <trunc>]" */
    size_t need = strlen("[tool ") + strlen(call_id) + strlen(": ") +
                  strlen(trunc) + strlen("]") + 1;
    char *msg = (char *)malloc(need);
    if (!msg) { free(trunc); return -1; }
    snprintf(msg, need, "[tool %s: %s]", call_id, trunc);
    free(trunc);

    ca_rt_event_t e;
    memset(&e, 0, sizeof(e));
    e.type = CA_RT_EVT_TRANSCRIPT_DELTA;
    e.at_utc_ms = rt_now_ms();
    e.direction = CA_RT_DIR_OUTBOUND;
    e.text = msg;   /* transfer ownership */
    if (!event_fifo_push(&s->events, e)) { ca_rt_event_free(&e); return -1; }
    return 0;
}

int ca_rt_session_cancel_response(ca_rt_session_t *s) {
    if (!s) return -1;
    if (s->is_null) return 0;
    emit_simple(&s->events, CA_RT_EVT_TURN_COMPLETE);
    return 0;
}

bool ca_rt_session_receive_audio_next(ca_rt_session_t *s, ca_rt_audio_frame_t *out) {
    if (!s || !out) return false;
    return frame_fifo_pop(&s->audio, out);
}
size_t ca_rt_session_audio_pending(const ca_rt_session_t *s) {
    return s ? (s->audio.count - s->audio.head) : 0;
}
bool ca_rt_session_receive_event_next(ca_rt_session_t *s, ca_rt_event_t *out) {
    if (!s || !out) return false;
    return event_fifo_pop(&s->events, out);
}
size_t ca_rt_session_event_pending(const ca_rt_session_t *s) {
    return s ? (s->events.count - s->events.head) : 0;
}

/* ===========================================================================
 * IRealtimeService — Loopback + Null
 * =========================================================================== */

struct ca_rt_service {
    bool                   is_null;
    ca_rt_text_to_audio_fn tts;
    void                  *tts_ctx;
};

ca_rt_service_t *ca_rt_loopback_service_create(ca_rt_text_to_audio_fn text_to_audio,
                                              void *tts_ctx) {
    ca_rt_service_t *svc = (ca_rt_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->is_null = false;
    svc->tts     = text_to_audio;   /* NULL -> session uses built-in silence */
    svc->tts_ctx = tts_ctx;
    return svc;
}
ca_rt_service_t *ca_rt_null_service_create(void) {
    ca_rt_service_t *svc = (ca_rt_service_t *)calloc(1, sizeof(*svc));
    if (svc) svc->is_null = true;
    return svc;
}
void ca_rt_service_destroy(ca_rt_service_t *svc) { free(svc); }

const char *ca_rt_service_provider_id(const ca_rt_service_t *svc) {
    if (!svc) return NULL;
    return svc->is_null ? "null" : "loopback";
}
bool ca_rt_service_is_configured(const ca_rt_service_t *svc) {
    if (!svc) return false;
    return svc->is_null ? false : true;
}

ca_rt_session_t *ca_rt_service_start_session(ca_rt_service_t *svc,
                                             const ca_rt_session_config_t *config) {
    if (!svc || !config) return NULL;
    if (svc->is_null) return NULL;   /* NullRealtimeService throws -> NULL here */
    return ca_rt_loopback_session_create(config, svc->tts, svc->tts_ctx);
}

/* ===========================================================================
 * CircleAI.Realtime.Cloud — NullRealtimeTransportFactory
 * =========================================================================== */

static int null_factory_connect(void *self, const char *endpoint,
                                const char *const *header_keys,
                                const char *const *header_values,
                                size_t header_count, ca_rt_transport_t *out) {
    (void)self; (void)endpoint; (void)header_keys; (void)header_values;
    (void)header_count;
    if (out) memset(out, 0, sizeof(*out));
    /* InvalidOperationException "No IRealtimeTransportFactory is registered." */
    return -1;
}

ca_rt_transport_factory_t ca_rt_null_transport_factory(void) {
    ca_rt_transport_factory_t f;
    f.self = NULL;
    f.connect = null_factory_connect;
    return f;
}
