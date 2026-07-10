/*
 * telephony.c — CircleAI.Telephony (C11 port).
 *
 * Carrier-agnostic telephony contract surface: primitives, DtmfToneGenerator,
 * IMediaStream (Manual + Pending), ICallSession (Test + carrier Media),
 * IInboundCallDispatcher (InMemory + Null), IToolCallRegistry
 * (DefaultToolCallRegistry), ITelephonyCarrier (Null + Fallback + binding wrap).
 *
 * Pure C11 + libc + libm. Linear FIFO cursors (unbounded, no drops), no pthreads.
 * StatusChanged is snapshot-then-invoke (subscriber list copied before callbacks).
 */

#include "circle_ai/telephony.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <math.h>
#include <time.h>

/* M_PI is not guaranteed under strict -std=c11 (it's an X/Open extension). */
#define TEL_PI 3.14159265358979323846

/* ── helpers ────────────────────────────────────────────────────────────── */

static char *tel_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *tel_strdup_empty(const char *s) { return tel_strdup(s ? s : ""); }

static bool tel_is_ws(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (!isspace((unsigned char)*p)) return false;
    return true;
}

/* case-insensitive equality (StringComparer.OrdinalIgnoreCase, ASCII). */
static bool tel_ieq(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return false;
        ++a; ++b;
    }
    return *a == *b;
}

static int64_t tel_now_ms(void) {
    return (int64_t)time(NULL) * 1000;
}

/* Guid.NewGuid():N surrogate — 32 lowercase hex, unique within the process. */
static char *tel_new_guid_n(void) {
    static uint64_t counter = 0;
    counter++;
    uint64_t a = (uint64_t)time(NULL);
    uint64_t b = counter * 0x9E3779B97F4A7C15ULL + a;
    char *out = (char *)malloc(33);
    if (out) snprintf(out, 33, "%08x%08x%08x%08x",
                      (unsigned)(a & 0xffffffff), (unsigned)(b & 0xffffffff),
                      (unsigned)((b >> 16) & 0xffffffff), (unsigned)((a >> 8) & 0xffffffff));
    return out;
}

/* internal carrier REST helpers (defined at the bottom; used by the session). */
int ca_tel_carrier_end_call_internal(ca_tel_carrier_t *c, const char *call_id);
int ca_tel_carrier_transfer_call_internal(ca_tel_carrier_t *c, const char *call_id,
                                          const char *target);

int ca_tel_sample_rate_of(ca_tel_media_format_t f) {
    switch (f) {
        case CA_TEL_FMT_PCM16000: return 16000;
        case CA_TEL_FMT_PCM24000: return 24000;
        case CA_TEL_FMT_MULAW8000:
        case CA_TEL_FMT_ALAW8000:
        default:                  return 8000;
    }
}

/* ===========================================================================
 * CallInfo
 * =========================================================================== */

ca_tel_call_info_t *ca_tel_call_info_new(
    const char *call_id, ca_tel_call_direction_t direction,
    const char *from, const char *to, const char *carrier_id,
    ca_tel_media_format_t media_format, int64_t started_at_utc_ms) {
    ca_tel_call_info_t *c = (ca_tel_call_info_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->call_id      = tel_strdup_empty(call_id);
    c->from         = tel_strdup_empty(from);
    c->to           = tel_strdup_empty(to);
    c->carrier_id   = tel_strdup_empty(carrier_id);
    c->direction    = direction;
    c->media_format = media_format;
    c->started_at_utc_ms = started_at_utc_ms;
    if (!c->call_id || !c->from || !c->to || !c->carrier_id) {
        ca_tel_call_info_destroy(c);
        return NULL;
    }
    return c;
}
void ca_tel_call_info_destroy(ca_tel_call_info_t *c) {
    if (!c) return;
    free(c->call_id); free(c->from); free(c->to); free(c->carrier_id);
    free(c);
}
ca_tel_call_info_t *ca_tel_call_info_copy(const ca_tel_call_info_t *c) {
    if (!c) return NULL;
    return ca_tel_call_info_new(c->call_id, c->direction, c->from, c->to,
                                c->carrier_id, c->media_format, c->started_at_utc_ms);
}

/* ===========================================================================
 * AudioFrame / DtmfEvent / ProvisionedNumber
 * =========================================================================== */

void ca_tel_audio_frame_free(ca_tel_audio_frame_t *f) {
    if (!f) return;
    free(f->pcm);
    f->pcm = NULL;
    f->pcm_len = 0;
}

void ca_tel_provisioned_number_free(ca_tel_provisioned_number_t *p) {
    if (!p) return;
    free(p->phone_number);
    free(p->carrier_id);
    p->phone_number = p->carrier_id = NULL;
}
void ca_tel_provisioned_number_free_array(ca_tel_provisioned_number_t *arr,
                                          size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_tel_provisioned_number_free(&arr[i]);
    free(arr);
}

/* deep-copy a frame's pcm into a fresh owned frame value. false on OOM. */
static bool frame_copy(ca_tel_audio_frame_t *dst, const ca_tel_audio_frame_t *src) {
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

/* ===========================================================================
 * CallSnapshot
 * =========================================================================== */

ca_tel_call_snapshot_t *ca_tel_call_snapshot_new(
    const ca_tel_call_info_t *info, ca_tel_call_status_t status,
    int64_t duration_ticks, ca_tel_decimal_t cost_so_far,
    const char *transfer_target) {
    if (!info) return NULL;
    ca_tel_call_snapshot_t *s = (ca_tel_call_snapshot_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->info = ca_tel_call_info_copy(info);
    if (!s->info) { free(s); return NULL; }
    s->status = status;
    s->duration_ticks = duration_ticks;
    s->cost_so_far = cost_so_far;
    if (transfer_target) {
        s->transfer_target = tel_strdup(transfer_target);
        if (!s->transfer_target) { ca_tel_call_snapshot_destroy(s); return NULL; }
    }
    return s;
}
void ca_tel_call_snapshot_destroy(ca_tel_call_snapshot_t *s) {
    if (!s) return;
    ca_tel_call_info_destroy(s->info);
    free(s->transfer_target);
    free(s);
}

/* ===========================================================================
 * OutboundDialOptions
 * =========================================================================== */

ca_tel_dial_options_t *ca_tel_dial_options_new(void) {
    ca_tel_dial_options_t *o = (ca_tel_dial_options_t *)calloc(1, sizeof(*o));
    if (!o) return NULL;
    o->ring_timeout_seconds = 30;   /* record default */
    return o;
}
void ca_tel_dial_options_destroy(ca_tel_dial_options_t *o) {
    if (!o) return;
    free(o->caller_id_override);
    for (size_t i = 0; i < o->follow_me_count; ++i) free(o->follow_me_numbers[i]);
    free(o->follow_me_numbers);
    free(o);
}
void ca_tel_dial_options_set_caller_id(ca_tel_dial_options_t *o, const char *cid) {
    if (!o) return;
    free(o->caller_id_override);
    o->caller_id_override = cid ? tel_strdup(cid) : NULL;
}
int ca_tel_dial_options_add_follow_me(ca_tel_dial_options_t *o, const char *num) {
    if (!o || !num) return -1;
    char **na = (char **)realloc(o->follow_me_numbers,
                                 (o->follow_me_count + 1) * sizeof(char *));
    if (!na) return -1;
    o->follow_me_numbers = na;
    char *dup = tel_strdup(num);
    if (!dup) return -1;
    o->follow_me_numbers[o->follow_me_count++] = dup;
    return 0;
}

/* ===========================================================================
 * DtmfToneGenerator
 * =========================================================================== */

/* Standard DTMF frequencies (low row × high column). */
static bool dtmf_freqs(char digit, int *low, int *high) {
    char d = (char)toupper((unsigned char)digit);
    switch (d) {
        case '1': *low = 697; *high = 1209; return true;
        case '2': *low = 697; *high = 1336; return true;
        case '3': *low = 697; *high = 1477; return true;
        case 'A': *low = 697; *high = 1633; return true;
        case '4': *low = 770; *high = 1209; return true;
        case '5': *low = 770; *high = 1336; return true;
        case '6': *low = 770; *high = 1477; return true;
        case 'B': *low = 770; *high = 1633; return true;
        case '7': *low = 852; *high = 1209; return true;
        case '8': *low = 852; *high = 1336; return true;
        case '9': *low = 852; *high = 1477; return true;
        case 'C': *low = 852; *high = 1633; return true;
        case '*': *low = 941; *high = 1209; return true;
        case '0': *low = 941; *high = 1336; return true;
        case '#': *low = 941; *high = 1477; return true;
        case 'D': *low = 941; *high = 1633; return true;
        default: return false;
    }
}

static void wr16_le(uint8_t *p, int16_t v) {
    p[0] = (uint8_t)((uint16_t)v & 0xFF);
    p[1] = (uint8_t)(((uint16_t)v >> 8) & 0xFF);
}

uint8_t *ca_tel_dtmf_generate(char digit, int sample_rate_hz, int duration_ms,
                              float amplitude, size_t *out_len) {
    if (out_len) *out_len = 0;
    if (sample_rate_hz <= 0) return NULL;
    if (duration_ms   <= 0) return NULL;
    int low, high;
    if (!dtmf_freqs(digit, &low, &high)) return NULL;

    int samples = sample_rate_hz * duration_ms / 1000;
    size_t nbytes = (size_t)samples * 2;
    uint8_t *buf = (uint8_t *)malloc(nbytes == 0 ? 1 : nbytes);
    if (!buf) return NULL;
    for (int i = 0; i < samples; ++i) {
        double t = (double)i / (double)sample_rate_hz;
        double s = 0.5 * (double)amplitude *
                   (sin(2.0 * TEL_PI * (double)low * t) +
                    sin(2.0 * TEL_PI * (double)high * t));
        if (s < -1.0) s = -1.0; else if (s > 1.0) s = 1.0;
        wr16_le(buf + i * 2, (int16_t)(s * 32767.0));   /* short.MaxValue */
    }
    if (out_len) *out_len = nbytes;
    return buf;
}

uint8_t *ca_tel_dtmf_generate_sequence(const char *digits, int sample_rate_hz,
                                       int tone_duration_ms, int inter_digit_gap_ms,
                                       float amplitude, size_t *out_len) {
    if (out_len) *out_len = 0;
    if (!digits || digits[0] == '\0') {
        /* Array.Empty<byte>() — return a valid non-NULL 0-length sentinel. */
        uint8_t *empty = (uint8_t *)malloc(1);
        if (empty && out_len) *out_len = 0;
        return empty;
    }
    if (sample_rate_hz <= 0 || tone_duration_ms <= 0) return NULL;
    if (inter_digit_gap_ms < 0) return NULL;

    int gap_samples = sample_rate_hz * inter_digit_gap_ms / 1000;
    size_t gap_bytes = (size_t)gap_samples * 2;
    size_t dcount = strlen(digits);

    /* First pass: compute total size (all tones same length; gaps between). */
    int tone_samples = sample_rate_hz * tone_duration_ms / 1000;
    size_t tone_bytes = (size_t)tone_samples * 2;
    /* validate every digit up front (Generate throws on unsupported) */
    for (size_t i = 0; i < dcount; ++i) {
        int lo, hi;
        if (!dtmf_freqs(digits[i], &lo, &hi)) return NULL;
    }
    size_t total = dcount * tone_bytes + (dcount > 0 ? (dcount - 1) * gap_bytes : 0);
    uint8_t *buf = (uint8_t *)malloc(total == 0 ? 1 : total);
    if (!buf) return NULL;

    size_t off = 0;
    for (size_t i = 0; i < dcount; ++i) {
        size_t tlen = 0;
        uint8_t *tone = ca_tel_dtmf_generate(digits[i], sample_rate_hz,
                                             tone_duration_ms, amplitude, &tlen);
        if (!tone) { free(buf); return NULL; }
        memcpy(buf + off, tone, tlen);
        off += tlen;
        free(tone);
        if (i + 1 < dcount && gap_bytes > 0) {
            memset(buf + off, 0, gap_bytes);   /* gap silence */
            off += gap_bytes;
        }
    }
    if (out_len) *out_len = total;
    return buf;
}

/* ===========================================================================
 * unbounded FIFO of audio frames
 * =========================================================================== */

typedef struct {
    ca_tel_audio_frame_t *items;
    size_t head, count, cap;
} audio_fifo_t;

static bool audio_fifo_push(audio_fifo_t *q, ca_tel_audio_frame_t item) {
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
            q->items = (ca_tel_audio_frame_t *)ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = item;
    return true;
}
static bool audio_fifo_pop(audio_fifo_t *q, ca_tel_audio_frame_t *out) {
    if (q->head >= q->count) return false;
    *out = q->items[q->head];
    memset(&q->items[q->head], 0, sizeof(q->items[q->head]));
    q->head++;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return true;
}
static void audio_fifo_free(audio_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i) ca_tel_audio_frame_free(&q->items[i]);
    free(q->items);
    q->items = NULL; q->head = q->count = q->cap = 0;
}

/* ── unbounded FIFO of DTMF events ──────────────────────────────────────── */

typedef struct {
    ca_tel_dtmf_event_t *items;
    size_t head, count, cap;
} dtmf_fifo_t;

static bool dtmf_fifo_push(dtmf_fifo_t *q, ca_tel_dtmf_event_t item) {
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
            q->items = (ca_tel_dtmf_event_t *)ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = item;
    return true;
}
static bool dtmf_fifo_pop(dtmf_fifo_t *q, ca_tel_dtmf_event_t *out) {
    if (q->head >= q->count) return false;
    *out = q->items[q->head];
    q->head++;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return true;
}
static void dtmf_fifo_free(dtmf_fifo_t *q) {
    free(q->items);
    q->items = NULL; q->head = q->count = q->cap = 0;
}

/* ── owned string list (SentDtmf) ───────────────────────────────────────── */

typedef struct {
    char  **items;
    size_t  count, cap;
} strlist_t;

static bool strlist_push(strlist_t *l, const char *s) {
    if (l->count == l->cap) {
        size_t nc = l->cap ? l->cap * 2 : 4;
        void *ni = realloc(l->items, nc * sizeof(char *));
        if (!ni) return false;
        l->items = (char **)ni; l->cap = nc;
    }
    char *dup = tel_strdup_empty(s);
    if (!dup) return false;
    l->items[l->count++] = dup;
    return true;
}
static void strlist_free(strlist_t *l) {
    for (size_t i = 0; i < l->count; ++i) free(l->items[i]);
    free(l->items);
    l->items = NULL; l->count = l->cap = 0;
}
static char **strlist_copy(const strlist_t *l, size_t *count) {
    if (count) *count = l->count;
    if (l->count == 0) return NULL;
    char **out = (char **)calloc(l->count, sizeof(char *));
    if (!out) { if (count) *count = 0; return NULL; }
    for (size_t i = 0; i < l->count; ++i) {
        out[i] = tel_strdup_empty(l->items[i]);
        if (!out[i]) {
            for (size_t j = 0; j < i; ++j) free(out[j]);
            free(out);
            if (count) *count = 0;
            return NULL;
        }
    }
    return out;
}

/* ===========================================================================
 * StatusChanged subscriber registry (snapshot-then-invoke broadcast)
 * =========================================================================== */

struct ca_tel_status_sub {
    struct status_pubsub    *owner;   /* NULL once detached/torn down */
    ca_tel_status_handler_fn handler;
    void                    *ctx;
};

typedef struct status_pubsub {
    ca_tel_status_sub_t **subs;
    size_t                count, cap;
} status_pubsub_t;

static ca_tel_status_sub_t *status_pubsub_subscribe(status_pubsub_t *ps,
                                                    ca_tel_status_handler_fn h,
                                                    void *ctx) {
    if (!ps || !h) return NULL;
    ca_tel_status_sub_t *sub = (ca_tel_status_sub_t *)calloc(1, sizeof(*sub));
    if (!sub) return NULL;
    sub->owner = ps; sub->handler = h; sub->ctx = ctx;
    if (ps->count == ps->cap) {
        size_t nc = ps->cap ? ps->cap * 2 : 4;
        void *ni = realloc(ps->subs, nc * sizeof(*ps->subs));
        if (!ni) { free(sub); return NULL; }
        ps->subs = (ca_tel_status_sub_t **)ni; ps->cap = nc;
    }
    ps->subs[ps->count++] = sub;
    return sub;
}

static void status_pubsub_remove(status_pubsub_t *ps, ca_tel_status_sub_t *sub) {
    if (!ps || !sub) return;
    for (size_t i = 0; i < ps->count; ++i) {
        if (ps->subs[i] == sub) {
            memmove(&ps->subs[i], &ps->subs[i + 1],
                    (ps->count - i - 1) * sizeof(*ps->subs));
            ps->count--;
            return;
        }
    }
}

/* Snapshot the subscriber list, then invoke each outside the snapshot so a
 * handler that unsubscribes mid-fire is safe. */
static void status_pubsub_fire(status_pubsub_t *ps, ca_tel_call_status_t status) {
    if (!ps || ps->count == 0) return;
    size_t n = ps->count;
    ca_tel_status_sub_t **snap = (ca_tel_status_sub_t **)malloc(n * sizeof(*snap));
    if (!snap) return;
    memcpy(snap, ps->subs, n * sizeof(*snap));
    for (size_t i = 0; i < n; ++i) {
        /* still live? (a prior handler may have removed it) */
        bool live = false;
        for (size_t j = 0; j < ps->count; ++j) if (ps->subs[j] == snap[i]) { live = true; break; }
        if (live && snap[i]->handler) snap[i]->handler(snap[i]->ctx, status);
    }
    free(snap);
}

static void status_pubsub_free(status_pubsub_t *ps) {
    if (!ps) return;
    for (size_t i = 0; i < ps->count; ++i) {
        ps->subs[i]->owner = NULL;   /* detach: token becomes inert */
    }
    free(ps->subs);
    ps->subs = NULL; ps->count = ps->cap = 0;
}

void ca_tel_status_unsubscribe(ca_tel_status_sub_t *sub) {
    if (!sub) return;
    if (sub->owner) status_pubsub_remove(sub->owner, sub);
    free(sub);
}

/* ===========================================================================
 * IMediaStream — Manual + Pending
 * =========================================================================== */

typedef enum { MEDIA_MANUAL, MEDIA_PENDING } media_kind_t;

struct ca_tel_media_stream {
    media_kind_t         kind;
    ca_tel_call_info_t  *info;            /* owned */
    ca_tel_call_status_t status;
    bool                 native_dtmf;     /* IDtmfSendable */
    audio_fifo_t         inbound_audio;
    dtmf_fifo_t          inbound_dtmf;
    audio_fifo_t         outbound_audio;  /* SentAudioFrames */
    strlist_t            sent_dtmf;        /* SentDtmf */
    status_pubsub_t      status_subs;
};

static ca_tel_media_stream_t *media_alloc(media_kind_t kind,
                                          const ca_tel_call_info_t *info,
                                          ca_tel_call_status_t initial,
                                          bool native_dtmf) {
    if (!info) return NULL;
    ca_tel_media_stream_t *m = (ca_tel_media_stream_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    m->kind = kind;
    m->info = ca_tel_call_info_copy(info);
    if (!m->info) { free(m); return NULL; }
    m->status = initial;
    m->native_dtmf = native_dtmf;
    return m;
}

ca_tel_media_stream_t *ca_tel_manual_media_create(const ca_tel_call_info_t *info,
                                                  ca_tel_call_status_t initial_status,
                                                  bool supports_native_dtmf) {
    return media_alloc(MEDIA_MANUAL, info, initial_status, supports_native_dtmf);
}
ca_tel_media_stream_t *ca_tel_pending_media_create(const ca_tel_call_info_t *info) {
    return media_alloc(MEDIA_PENDING, info, CA_TEL_STATUS_RINGING, false);
}

void ca_tel_media_stream_destroy(ca_tel_media_stream_t *m) {
    if (!m) return;
    status_pubsub_free(&m->status_subs);
    audio_fifo_free(&m->inbound_audio);
    dtmf_fifo_free(&m->inbound_dtmf);
    audio_fifo_free(&m->outbound_audio);
    strlist_free(&m->sent_dtmf);
    ca_tel_call_info_destroy(m->info);
    free(m);
}

const ca_tel_call_info_t *ca_tel_media_stream_info(const ca_tel_media_stream_t *m) {
    return m ? m->info : NULL;
}
ca_tel_call_status_t ca_tel_media_stream_status(const ca_tel_media_stream_t *m) {
    return m ? m->status : CA_TEL_STATUS_FAILED;
}
bool ca_tel_media_stream_supports_native_dtmf(const ca_tel_media_stream_t *m) {
    return m ? m->native_dtmf : false;
}

int ca_tel_media_stream_send_audio(ca_tel_media_stream_t *m,
                                   const ca_tel_audio_frame_t *frame) {
    if (!m || !frame) return -1;
    if (frame->pcm_len > 0 && !frame->pcm) return -1;
    if (m->kind == MEDIA_PENDING) return -1;   /* InvalidOperationException */
    ca_tel_audio_frame_t cp;
    if (!frame_copy(&cp, frame)) return -1;
    if (!audio_fifo_push(&m->outbound_audio, cp)) { ca_tel_audio_frame_free(&cp); return -1; }
    return 0;
}

int ca_tel_media_stream_send_dtmf(ca_tel_media_stream_t *m, const char *digits) {
    if (!m) return -1;
    if (m->kind != MEDIA_MANUAL || !m->native_dtmf) return -1;  /* not IDtmfSendable */
    if (!digits) return -1;
    if (!strlist_push(&m->sent_dtmf, digits)) return -1;
    return 0;
}

static void media_set_status_internal(ca_tel_media_stream_t *m,
                                      ca_tel_call_status_t status) {
    if (m->status == status) return;
    m->status = status;
    status_pubsub_fire(&m->status_subs, status);
}

int ca_tel_media_stream_end(ca_tel_media_stream_t *m) {
    if (!m) return -1;
    /* PendingMediaStream.EndAsync: fires even from Ringing (sets EndedByAgent). */
    if (m->status != CA_TEL_STATUS_ENDED_BY_AGENT) {
        m->status = CA_TEL_STATUS_ENDED_BY_AGENT;
        status_pubsub_fire(&m->status_subs, m->status);
    }
    if (m->kind == MEDIA_MANUAL) {
        /* complete inbound streams */
        /* (already-drained cursors; nothing extra to signal beyond emptiness) */
    }
    return 0;
}

int ca_tel_manual_media_inject_audio(ca_tel_media_stream_t *m,
                                     const ca_tel_audio_frame_t *frame) {
    if (!m || m->kind != MEDIA_MANUAL || !frame) return -1;
    if (frame->pcm_len > 0 && !frame->pcm) return -1;
    ca_tel_audio_frame_t cp;
    if (!frame_copy(&cp, frame)) return -1;
    if (!audio_fifo_push(&m->inbound_audio, cp)) { ca_tel_audio_frame_free(&cp); return -1; }
    return 0;
}
int ca_tel_manual_media_inject_dtmf(ca_tel_media_stream_t *m,
                                    const ca_tel_dtmf_event_t *ev) {
    if (!m || m->kind != MEDIA_MANUAL || !ev) return -1;
    if (!dtmf_fifo_push(&m->inbound_dtmf, *ev)) return -1;
    return 0;
}
void ca_tel_manual_media_end_inbound(ca_tel_media_stream_t *m) {
    (void)m;   /* cursors simply run dry; explicit for API symmetry */
}

void ca_tel_media_stream_set_status(ca_tel_media_stream_t *m,
                                    ca_tel_call_status_t status) {
    if (!m) return;
    media_set_status_internal(m, status);
}

bool ca_tel_media_stream_receive_audio_next(ca_tel_media_stream_t *m,
                                            ca_tel_audio_frame_t *out) {
    if (!m || !out) return false;
    return audio_fifo_pop(&m->inbound_audio, out);
}
size_t ca_tel_media_stream_audio_pending(const ca_tel_media_stream_t *m) {
    return m ? (m->inbound_audio.count - m->inbound_audio.head) : 0;
}
bool ca_tel_media_stream_receive_dtmf_next(ca_tel_media_stream_t *m,
                                           ca_tel_dtmf_event_t *out) {
    if (!m || !out) return false;
    return dtmf_fifo_pop(&m->inbound_dtmf, out);
}
size_t ca_tel_media_stream_dtmf_pending(const ca_tel_media_stream_t *m) {
    return m ? (m->inbound_dtmf.count - m->inbound_dtmf.head) : 0;
}

/* copy the outbound-audio capture into a fresh owned array */
static ca_tel_audio_frame_t *audio_fifo_copy_all(const audio_fifo_t *q, size_t *count) {
    size_t n = q->count - q->head;
    if (count) *count = n;
    if (n == 0) return NULL;
    ca_tel_audio_frame_t *out = (ca_tel_audio_frame_t *)calloc(n, sizeof(*out));
    if (!out) { if (count) *count = 0; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!frame_copy(&out[i], &q->items[q->head + i])) {
            for (size_t j = 0; j < i; ++j) ca_tel_audio_frame_free(&out[j]);
            free(out);
            if (count) *count = 0;
            return NULL;
        }
    }
    return out;
}

ca_tel_audio_frame_t *ca_tel_media_stream_sent_audio(const ca_tel_media_stream_t *m,
                                                     size_t *count) {
    if (!m) { if (count) *count = 0; return NULL; }
    return audio_fifo_copy_all(&m->outbound_audio, count);
}
size_t ca_tel_media_stream_sent_audio_count(const ca_tel_media_stream_t *m) {
    return m ? (m->outbound_audio.count - m->outbound_audio.head) : 0;
}
char **ca_tel_media_stream_sent_dtmf(const ca_tel_media_stream_t *m, size_t *count) {
    if (!m) { if (count) *count = 0; return NULL; }
    return strlist_copy(&m->sent_dtmf, count);
}
size_t ca_tel_media_stream_sent_dtmf_count(const ca_tel_media_stream_t *m) {
    return m ? m->sent_dtmf.count : 0;
}

ca_tel_status_sub_t *ca_tel_media_stream_subscribe_status(
    ca_tel_media_stream_t *m, ca_tel_status_handler_fn handler, void *ctx) {
    if (!m) return NULL;
    return status_pubsub_subscribe(&m->status_subs, handler, ctx);
}

/* ===========================================================================
 * ICallSession — TestCallSession + MediaCallSession
 * =========================================================================== */

typedef enum { SESSION_TEST, SESSION_MEDIA } session_kind_t;

struct ca_tel_call_session {
    session_kind_t       kind;

    /* TEST */
    ca_tel_call_info_t  *info;            /* owned (test) */
    ca_tel_call_status_t status;          /* test _status */
    audio_fifo_t         inbound_audio;   /* test injected */
    dtmf_fifo_t          inbound_dtmf;
    audio_fifo_t         outbound_audio;  /* test SentAudioFrames */
    strlist_t            sent_dtmf;        /* test/media SentDtmf */

    /* MEDIA */
    ca_tel_media_stream_t *media;         /* owned (media session) */
    ca_tel_carrier_t      *carrier;       /* borrowed */
    ca_tel_call_status_t   latched;       /* media _status (Ringing seed) */
    ca_tel_status_sub_t   *media_sub;     /* subscription to media status */

    status_pubsub_t      status_subs;     /* our StatusChanged */
};

/* forward: media status folding + carrier vtable dispatch */
static void media_session_on_media_status(void *ctx, ca_tel_call_status_t status);

ca_tel_call_session_t *ca_tel_test_call_session_create(const ca_tel_call_info_t *info) {
    ca_tel_call_session_t *s = (ca_tel_call_session_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->kind = SESSION_TEST;
    if (info) {
        s->info = ca_tel_call_info_copy(info);
    } else {
        /* C# default CallInfo */
        char *guid = tel_new_guid_n();
        if (!guid) { free(s); return NULL; }
        s->info = ca_tel_call_info_new(guid, CA_TEL_DIR_INBOUND,
                                       "+15555550100", "+15555550200",
                                       "test", CA_TEL_FMT_PCM16000, tel_now_ms());
        free(guid);
    }
    if (!s->info) { free(s); return NULL; }
    s->status = CA_TEL_STATUS_ACTIVE;   /* TestCallSession default */
    return s;
}

ca_tel_call_session_t *ca_tel_media_call_session_create(ca_tel_media_stream_t *media,
                                                        ca_tel_carrier_t *carrier) {
    if (!media || !carrier) return NULL;
    ca_tel_call_session_t *s = (ca_tel_call_session_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->kind = SESSION_MEDIA;
    s->media = media;          /* ownership transferred */
    s->carrier = carrier;      /* borrowed */
    s->latched = CA_TEL_STATUS_RINGING;
    /* Subscribe to media StatusChanged synchronously before returning so a
     * status flip published right after construction is observed, not lost. */
    s->media_sub = ca_tel_media_stream_subscribe_status(media,
                                                        media_session_on_media_status, s);
    return s;
}

void ca_tel_call_session_destroy(ca_tel_call_session_t *s) {
    if (!s) return;
    status_pubsub_free(&s->status_subs);
    if (s->kind == SESSION_MEDIA) {
        if (s->media_sub) ca_tel_status_unsubscribe(s->media_sub);
        ca_tel_media_stream_destroy(s->media);
    } else {
        ca_tel_call_info_destroy(s->info);
        audio_fifo_free(&s->inbound_audio);
        dtmf_fifo_free(&s->inbound_dtmf);
        audio_fifo_free(&s->outbound_audio);
    }
    strlist_free(&s->sent_dtmf);
    free(s);
}

const ca_tel_call_info_t *ca_tel_call_session_info(const ca_tel_call_session_t *s) {
    if (!s) return NULL;
    return s->kind == SESSION_MEDIA ? ca_tel_media_stream_info(s->media) : s->info;
}

/* MediaCallSession.Status fold: when media is still Ringing but we've latched a
 * different status, report the latch; else report the media's status. */
ca_tel_call_status_t ca_tel_call_session_status(const ca_tel_call_session_t *s) {
    if (!s) return CA_TEL_STATUS_FAILED;
    if (s->kind == SESSION_TEST) return s->status;
    ca_tel_call_status_t ms = ca_tel_media_stream_status(s->media);
    if (ms == CA_TEL_STATUS_RINGING && s->latched != CA_TEL_STATUS_RINGING)
        return s->latched;
    return ms;
}

/* our StatusChanged emit (deduped by latch for media, by _status for test) */
static void session_set_status(ca_tel_call_session_t *s, ca_tel_call_status_t status) {
    if (s->kind == SESSION_MEDIA) {
        if (s->latched == status) return;
        s->latched = status;
    } else {
        if (s->status == status) return;
        s->status = status;
    }
    status_pubsub_fire(&s->status_subs, status);
}

static void media_session_on_media_status(void *ctx, ca_tel_call_status_t status) {
    ca_tel_call_session_t *s = (ca_tel_call_session_t *)ctx;
    session_set_status(s, status);
}

bool ca_tel_call_session_receive_audio_next(ca_tel_call_session_t *s,
                                            ca_tel_audio_frame_t *out) {
    if (!s || !out) return false;
    if (s->kind == SESSION_MEDIA)
        return ca_tel_media_stream_receive_audio_next(s->media, out);
    return audio_fifo_pop(&s->inbound_audio, out);
}
size_t ca_tel_call_session_audio_pending(const ca_tel_call_session_t *s) {
    if (!s) return 0;
    if (s->kind == SESSION_MEDIA) return ca_tel_media_stream_audio_pending(s->media);
    return s->inbound_audio.count - s->inbound_audio.head;
}
bool ca_tel_call_session_receive_dtmf_next(ca_tel_call_session_t *s,
                                           ca_tel_dtmf_event_t *out) {
    if (!s || !out) return false;
    if (s->kind == SESSION_MEDIA)
        return ca_tel_media_stream_receive_dtmf_next(s->media, out);
    return dtmf_fifo_pop(&s->inbound_dtmf, out);
}
size_t ca_tel_call_session_dtmf_pending(const ca_tel_call_session_t *s) {
    if (!s) return 0;
    if (s->kind == SESSION_MEDIA) return ca_tel_media_stream_dtmf_pending(s->media);
    return s->inbound_dtmf.count - s->inbound_dtmf.head;
}

int ca_tel_call_session_send_audio(ca_tel_call_session_t *s,
                                   const ca_tel_audio_frame_t *frame) {
    if (!s || !frame) return -1;
    if (frame->pcm_len > 0 && !frame->pcm) return -1;
    if (s->kind == SESSION_MEDIA)
        return ca_tel_media_stream_send_audio(s->media, frame);
    ca_tel_audio_frame_t cp;
    if (!frame_copy(&cp, frame)) return -1;
    if (!audio_fifo_push(&s->outbound_audio, cp)) { ca_tel_audio_frame_free(&cp); return -1; }
    return 0;
}

int ca_tel_call_session_send_dtmf(ca_tel_call_session_t *s, const char *digits) {
    if (!s) return -1;
    if (!digits || digits[0] == '\0') return 0;   /* no-op success */
    if (s->kind == SESSION_TEST) {
        if (!strlist_push(&s->sent_dtmf, digits)) return -1;
        return 0;
    }
    /* MEDIA: native out-of-band DTMF when supported, else in-band tones. */
    if (ca_tel_media_stream_supports_native_dtmf(s->media))
        return ca_tel_media_stream_send_dtmf(s->media, digits);

    const ca_tel_call_info_t *info = ca_tel_media_stream_info(s->media);
    int sr = ca_tel_sample_rate_of(info->media_format);
    /* SendThroughSessionAsync: generate a full sequence, send as one frame, format
     * derived from sample rate (8000->Mulaw, 16000->Pcm16000, 24000->Pcm24000). */
    size_t pcm_len = 0;
    uint8_t *pcm = ca_tel_dtmf_generate_sequence(digits, sr, 150, 50, 0.5f, &pcm_len);
    if (!pcm) return -1;
    ca_tel_media_format_t fmt = (sr == 8000)  ? CA_TEL_FMT_MULAW8000 :
                                (sr == 16000) ? CA_TEL_FMT_PCM16000 :
                                (sr == 24000) ? CA_TEL_FMT_PCM24000 :
                                                CA_TEL_FMT_PCM16000;
    ca_tel_audio_frame_t f;
    memset(&f, 0, sizeof(f));
    f.pcm = pcm; f.pcm_len = pcm_len; f.format = fmt; f.offset_ticks = 0;
    int rc = ca_tel_media_stream_send_audio(s->media, &f);
    ca_tel_audio_frame_free(&f);
    return rc;
}

int ca_tel_call_session_transfer(ca_tel_call_session_t *s, const char *target,
                                 ca_tel_transfer_mode_t mode, const char *briefing) {
    (void)briefing;
    if (!s) return -1;
    if (s->kind == SESSION_TEST) {
        /* TriggerStatusChange(Transferred) regardless of mode/briefing. */
        session_set_status(s, CA_TEL_STATUS_TRANSFERRED);
        return 0;
    }
    /* MEDIA: warm w/o briefing pipeline falls through to cold transfer; our port
     * has no TTS pipeline wired on the session, so both modes issue the carrier
     * cold transfer. */
    (void)mode;
    if (tel_is_ws(target)) return -1;
    int rc = ca_tel_carrier_transfer_call_internal(s->carrier, ca_tel_call_session_info(s)->call_id, target);
    if (rc != 0) return -1;
    session_set_status(s, CA_TEL_STATUS_TRANSFERRED);
    return 0;
}

int ca_tel_call_session_hangup(ca_tel_call_session_t *s) {
    if (!s) return -1;
    if (s->kind == SESSION_TEST) {
        session_set_status(s, CA_TEL_STATUS_ENDED_BY_AGENT);
        /* EndInboundStreams: cursors run dry (nothing to signal). */
        return 0;
    }
    /* MEDIA: latch EndedByAgent, end media (best-effort), call carrier EndCall. */
    session_set_status(s, CA_TEL_STATUS_ENDED_BY_AGENT);
    ca_tel_media_stream_end(s->media);   /* may already be closed; ignore rc */
    ca_tel_carrier_end_call_internal(s->carrier, ca_tel_call_session_info(s)->call_id);
    return 0;
}

/* ── TestCallSession drive/capture ──────────────────────────────────────── */

int ca_tel_test_call_session_inject_audio(ca_tel_call_session_t *s,
                                          const ca_tel_audio_frame_t *frame) {
    if (!s || s->kind != SESSION_TEST || !frame) return -1;
    if (frame->pcm_len > 0 && !frame->pcm) return -1;
    ca_tel_audio_frame_t cp;
    if (!frame_copy(&cp, frame)) return -1;
    if (!audio_fifo_push(&s->inbound_audio, cp)) { ca_tel_audio_frame_free(&cp); return -1; }
    return 0;
}
int ca_tel_test_call_session_inject_dtmf(ca_tel_call_session_t *s,
                                         const ca_tel_dtmf_event_t *ev) {
    if (!s || s->kind != SESSION_TEST || !ev) return -1;
    if (!dtmf_fifo_push(&s->inbound_dtmf, *ev)) return -1;
    return 0;
}
void ca_tel_test_call_session_end_inbound(ca_tel_call_session_t *s) {
    (void)s;   /* cursors run dry */
}
void ca_tel_test_call_session_trigger_status(ca_tel_call_session_t *s,
                                             ca_tel_call_status_t status) {
    if (!s || s->kind != SESSION_TEST) return;
    /* TriggerStatusChange always sets + fires (even if unchanged the C# invokes
     * the handler with the new status). Mirror that: force-fire. */
    s->status = status;
    status_pubsub_fire(&s->status_subs, status);
}

ca_tel_audio_frame_t *ca_tel_call_session_sent_audio(const ca_tel_call_session_t *s,
                                                     size_t *count) {
    if (!s) { if (count) *count = 0; return NULL; }
    if (s->kind == SESSION_MEDIA) return ca_tel_media_stream_sent_audio(s->media, count);
    return audio_fifo_copy_all(&s->outbound_audio, count);
}
size_t ca_tel_call_session_sent_audio_count(const ca_tel_call_session_t *s) {
    if (!s) return 0;
    if (s->kind == SESSION_MEDIA) return ca_tel_media_stream_sent_audio_count(s->media);
    return s->outbound_audio.count - s->outbound_audio.head;
}
char **ca_tel_call_session_sent_dtmf(const ca_tel_call_session_t *s, size_t *count) {
    if (!s) { if (count) *count = 0; return NULL; }
    if (s->kind == SESSION_MEDIA) return ca_tel_media_stream_sent_dtmf(s->media, count);
    return strlist_copy(&s->sent_dtmf, count);
}
size_t ca_tel_call_session_sent_dtmf_count(const ca_tel_call_session_t *s) {
    if (!s) return 0;
    if (s->kind == SESSION_MEDIA) return ca_tel_media_stream_sent_dtmf_count(s->media);
    return s->sent_dtmf.count;
}

ca_tel_status_sub_t *ca_tel_call_session_subscribe_status(
    ca_tel_call_session_t *s, ca_tel_status_handler_fn handler, void *ctx) {
    if (!s) return NULL;
    return status_pubsub_subscribe(&s->status_subs, handler, ctx);
}

/* ===========================================================================
 * IInboundCallDispatcher — InMemory + Null
 * =========================================================================== */

struct ca_tel_dispatcher_sub {
    struct ca_tel_dispatcher *owner;   /* NULL once torn down */
    ca_tel_inbound_handler_fn handler;
    void                     *ctx;
};

struct ca_tel_dispatcher {
    bool   is_null;
    char  *carrier_id;                 /* owned */
    ca_tel_dispatcher_sub_t **subs;
    size_t sub_count, sub_cap;
    /* retained published sessions (borrowed pointers) for replay on subscribe */
    ca_tel_call_session_t **retained;
    size_t retained_count, retained_cap;
};

ca_tel_dispatcher_t *ca_tel_null_dispatcher_create(void) {
    ca_tel_dispatcher_t *d = (ca_tel_dispatcher_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->is_null = true;
    d->carrier_id = tel_strdup("null");
    if (!d->carrier_id) { free(d); return NULL; }
    return d;
}
ca_tel_dispatcher_t *ca_tel_inmemory_dispatcher_create(const char *carrier_id) {
    ca_tel_dispatcher_t *d = (ca_tel_dispatcher_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->is_null = false;
    d->carrier_id = tel_strdup_empty(carrier_id);
    if (!d->carrier_id) { free(d); return NULL; }
    return d;
}
void ca_tel_dispatcher_destroy(ca_tel_dispatcher_t *d) {
    if (!d) return;
    for (size_t i = 0; i < d->sub_count; ++i) {
        d->subs[i]->owner = NULL;   /* detach */
    }
    free(d->subs);
    free(d->retained);
    free(d->carrier_id);
    free(d);
}
const char *ca_tel_dispatcher_carrier_id(const ca_tel_dispatcher_t *d) {
    return d ? d->carrier_id : NULL;
}

ca_tel_dispatcher_sub_t *ca_tel_dispatcher_subscribe(
    ca_tel_dispatcher_t *d, ca_tel_inbound_handler_fn handler, void *ctx) {
    if (!d || !handler) return NULL;
    ca_tel_dispatcher_sub_t *sub = (ca_tel_dispatcher_sub_t *)calloc(1, sizeof(*sub));
    if (!sub) return NULL;
    sub->owner = d; sub->handler = handler; sub->ctx = ctx;
    if (d->is_null) return sub;   /* live token, never fires */

    if (d->sub_count == d->sub_cap) {
        size_t nc = d->sub_cap ? d->sub_cap * 2 : 4;
        void *ni = realloc(d->subs, nc * sizeof(*d->subs));
        if (!ni) { free(sub); return NULL; }
        d->subs = (ca_tel_dispatcher_sub_t **)ni; d->sub_cap = nc;
    }
    d->subs[d->sub_count++] = sub;
    /* Replay retained sessions in order so a session published before this
     * subscriber attached is still observed (unbounded-channel semantics). */
    for (size_t i = 0; i < d->retained_count; ++i)
        handler(ctx, d->retained[i]);
    return sub;
}
void ca_tel_dispatcher_unsubscribe(ca_tel_dispatcher_sub_t *sub) {
    if (!sub) return;
    ca_tel_dispatcher_t *d = sub->owner;
    if (d) {
        for (size_t i = 0; i < d->sub_count; ++i) {
            if (d->subs[i] == sub) {
                memmove(&d->subs[i], &d->subs[i + 1],
                        (d->sub_count - i - 1) * sizeof(*d->subs));
                d->sub_count--;
                break;
            }
        }
    }
    free(sub);
}

int ca_tel_dispatcher_publish(ca_tel_dispatcher_t *d, ca_tel_call_session_t *session) {
    if (!d || d->is_null || !session) return 0;
    /* retain for future subscribers */
    if (d->retained_count == d->retained_cap) {
        size_t nc = d->retained_cap ? d->retained_cap * 2 : 4;
        void *ni = realloc(d->retained, nc * sizeof(*d->retained));
        if (ni) { d->retained = (ca_tel_call_session_t **)ni; d->retained_cap = nc; }
    }
    if (d->retained_count < d->retained_cap)
        d->retained[d->retained_count++] = session;

    /* snapshot current subscribers, then fire outside the snapshot */
    size_t n = d->sub_count;
    if (n == 0) return 0;
    ca_tel_dispatcher_sub_t **snap =
        (ca_tel_dispatcher_sub_t **)malloc(n * sizeof(*snap));
    if (!snap) return 0;
    memcpy(snap, d->subs, n * sizeof(*snap));
    int fired = 0;
    for (size_t i = 0; i < n; ++i) {
        bool live = false;
        for (size_t j = 0; j < d->sub_count; ++j) if (d->subs[j] == snap[i]) { live = true; break; }
        if (live && snap[i]->handler) { snap[i]->handler(snap[i]->ctx, session); fired++; }
    }
    free(snap);
    return fired;
}

/* ===========================================================================
 * IToolCallRegistry — DefaultToolCallRegistry
 * =========================================================================== */

void ca_tel_tool_definition_free(ca_tel_tool_definition_t *d) {
    if (!d) return;
    free(d->name); free(d->description); free(d->arguments_json_schema);
    d->name = d->description = d->arguments_json_schema = NULL;
}
void ca_tel_tool_result_free(ca_tel_tool_result_t *r) {
    if (!r) return;
    free(r->call_id); free(r->result_json); free(r->error);
    r->call_id = r->result_json = r->error = NULL;
}

typedef struct {
    ca_tel_tool_definition_t     def;    /* owned copy */
    ca_tel_local_tool_handler_fn local;  /* NULL when webhook */
    void                        *local_ctx;
    char                        *webhook;/* owned or NULL when local */
} tool_entry_t;

struct ca_tel_tool_registry {
    tool_entry_t           *entries;
    size_t                  count, cap;
    ca_tel_webhook_poster_fn poster;
    void                    *poster_ctx;
};

ca_tel_tool_registry_t *ca_tel_tool_registry_create(ca_tel_webhook_poster_fn poster,
                                                    void *poster_ctx) {
    ca_tel_tool_registry_t *r = (ca_tel_tool_registry_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->poster = poster;
    r->poster_ctx = poster_ctx;
    return r;
}
void ca_tel_tool_registry_destroy(ca_tel_tool_registry_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->count; ++i) {
        ca_tel_tool_definition_free(&r->entries[i].def);
        free(r->entries[i].webhook);
    }
    free(r->entries);
    free(r);
}

static bool tool_def_copy(ca_tel_tool_definition_t *dst,
                          const ca_tel_tool_definition_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->name = tel_strdup_empty(src->name);
    dst->description = tel_strdup_empty(src->description);
    dst->arguments_json_schema = tel_strdup_empty(src->arguments_json_schema);
    if (!dst->name || !dst->description || !dst->arguments_json_schema) {
        ca_tel_tool_definition_free(dst);
        return false;
    }
    return true;
}

/* find index of a tool by name (case-insensitive), or SIZE_MAX. */
static size_t tool_find(const ca_tel_tool_registry_t *r, const char *name) {
    for (size_t i = 0; i < r->count; ++i)
        if (tel_ieq(r->entries[i].def.name, name)) return i;
    return SIZE_MAX;
}

/* upsert a tool entry (LWW). Takes ownership of the entry's copied fields. */
static int tool_upsert(ca_tel_tool_registry_t *r, const ca_tel_tool_definition_t *def,
                       ca_tel_local_tool_handler_fn local, void *local_ctx,
                       const char *webhook) {
    ca_tel_tool_definition_t copy;
    if (!tool_def_copy(&copy, def)) return -1;
    char *wh = NULL;
    if (webhook) { wh = tel_strdup(webhook); if (!wh) { ca_tel_tool_definition_free(&copy); return -1; } }

    size_t idx = tool_find(r, def->name);
    if (idx != SIZE_MAX) {
        ca_tel_tool_definition_free(&r->entries[idx].def);
        free(r->entries[idx].webhook);
        r->entries[idx].def = copy;
        r->entries[idx].local = local;
        r->entries[idx].local_ctx = local_ctx;
        r->entries[idx].webhook = wh;
        return 0;
    }
    if (r->count == r->cap) {
        size_t nc = r->cap ? r->cap * 2 : 4;
        void *ni = realloc(r->entries, nc * sizeof(*r->entries));
        if (!ni) { ca_tel_tool_definition_free(&copy); free(wh); return -1; }
        r->entries = (tool_entry_t *)ni; r->cap = nc;
    }
    r->entries[r->count].def = copy;
    r->entries[r->count].local = local;
    r->entries[r->count].local_ctx = local_ctx;
    r->entries[r->count].webhook = wh;
    r->count++;
    return 0;
}

int ca_tel_tool_registry_register_local(ca_tel_tool_registry_t *r,
                                        const ca_tel_tool_definition_t *definition,
                                        ca_tel_local_tool_handler_fn handler,
                                        void *handler_ctx) {
    if (!r || !definition || !handler) return -1;
    if (tel_is_ws(definition->name)) return -1;   /* "Tool name is required" */
    return tool_upsert(r, definition, handler, handler_ctx, NULL);
}

static bool url_is_absolute(const char *u) {
    if (!u) return false;
    /* absolute if it has a scheme (scheme:...) with an alpha first char */
    const char *p = u;
    if (!isalpha((unsigned char)*p)) return false;
    for (; *p; ++p) {
        if (*p == ':') return p != u;
        if (!(isalnum((unsigned char)*p) || *p == '+' || *p == '-' || *p == '.')) return false;
    }
    return false;
}

int ca_tel_tool_registry_register_webhook(ca_tel_tool_registry_t *r,
                                          const ca_tel_tool_definition_t *definition,
                                          const char *webhook_url) {
    if (!r || !definition || !webhook_url) return -1;
    if (!url_is_absolute(webhook_url)) return -1;  /* "must be absolute" */
    if (tel_is_ws(definition->name)) return -1;    /* "Tool name is required" */
    return tool_upsert(r, definition, NULL, NULL, webhook_url);
}

ca_tel_tool_definition_t *ca_tel_tool_registry_definitions(
    const ca_tel_tool_registry_t *r, size_t *count) {
    if (!r) { if (count) *count = 0; return NULL; }
    if (count) *count = r->count;
    if (r->count == 0) return NULL;
    ca_tel_tool_definition_t *out =
        (ca_tel_tool_definition_t *)calloc(r->count, sizeof(*out));
    if (!out) { if (count) *count = 0; return NULL; }
    for (size_t i = 0; i < r->count; ++i) {
        if (!tool_def_copy(&out[i], &r->entries[i].def)) {
            for (size_t j = 0; j < i; ++j) ca_tel_tool_definition_free(&out[j]);
            free(out);
            if (count) *count = 0;
            return NULL;
        }
    }
    return out;
}
size_t ca_tel_tool_registry_definition_count(const ca_tel_tool_registry_t *r) {
    return r ? r->count : 0;
}

/* build a fresh ToolResult */
static ca_tel_tool_result_t *tool_result_make(const char *call_id, bool ok,
                                              const char *result_json,
                                              const char *error) {
    ca_tel_tool_result_t *r = (ca_tel_tool_result_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->call_id = tel_strdup_empty(call_id);
    r->succeeded = ok;
    r->result_json = tel_strdup_empty(result_json ? result_json : "{}");
    if (error) { r->error = tel_strdup(error); if (!r->error) { ca_tel_tool_result_free(r); free(r); return NULL; } }
    if (!r->call_id || !r->result_json) { ca_tel_tool_result_free(r); free(r); return NULL; }
    return r;
}

/* Truncate(s, max): s if <= max else first max chars + "…" (UTF-8 E2 80 A6). */
static char *truncate_ellipsis(const char *s, size_t max) {
    size_t len = s ? strlen(s) : 0;
    if (len <= max) return tel_strdup_empty(s);
    char *out = (char *)malloc(max + 3 + 1);
    if (!out) return NULL;
    memcpy(out, s, max);
    out[max] = (char)0xE2; out[max+1] = (char)0x80; out[max+2] = (char)0xA6;
    out[max+3] = '\0';
    return out;
}

/* Compose the webhook envelope JSON:
 *   {"call_id":"<id>","tool":"<name>","arguments":<argsJson>}
 * call_id/tool are JSON-string-escaped; arguments inlined raw (JsonDocument.Parse
 * RootElement re-serialised — we pass through the caller's JSON verbatim, which
 * matches the round-trip for well-formed compact JSON). */
static char *json_escape(const char *s) {
    if (!s) return tel_strdup("");
    size_t cap = strlen(s) * 2 + 1;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    size_t j = 0;
    for (const char *p = s; *p; ++p) {
        unsigned char c = (unsigned char)*p;
        if (c == '"' || c == '\\') { out[j++] = '\\'; out[j++] = (char)c; }
        else if (c == '\n') { out[j++] = '\\'; out[j++] = 'n'; }
        else if (c == '\r') { out[j++] = '\\'; out[j++] = 'r'; }
        else if (c == '\t') { out[j++] = '\\'; out[j++] = 't'; }
        else out[j++] = (char)c;
    }
    out[j] = '\0';
    return out;
}

static char *webhook_envelope(const char *call_id, const char *tool,
                              const char *args_json) {
    char *eid = json_escape(call_id);
    char *etool = json_escape(tool);
    const char *args = (args_json && args_json[0]) ? args_json : "{}";
    if (!eid || !etool) { free(eid); free(etool); return NULL; }
    size_t need = strlen("{\"call_id\":\"\",\"tool\":\"\",\"arguments\":}") +
                  strlen(eid) + strlen(etool) + strlen(args) + 1;
    char *out = (char *)malloc(need);
    if (out) snprintf(out, need, "{\"call_id\":\"%s\",\"tool\":\"%s\",\"arguments\":%s}",
                      eid, etool, args);
    free(eid); free(etool);
    return out;
}

ca_tel_tool_result_t *ca_tel_tool_registry_invoke(
    ca_tel_tool_registry_t *r, const ca_tel_tool_invocation_t *inv) {
    if (!r || !inv) return NULL;

    size_t idx = tool_find(r, inv->tool_name);
    if (idx == SIZE_MAX) {
        size_t need = strlen("Tool '' is not registered.") +
                      (inv->tool_name ? strlen(inv->tool_name) : 0) + 1;
        char *msg = (char *)malloc(need);
        if (!msg) return NULL;
        snprintf(msg, need, "Tool '%s' is not registered.",
                 inv->tool_name ? inv->tool_name : "");
        ca_tel_tool_result_t *res = tool_result_make(inv->call_id, false, "{}", msg);
        free(msg);
        return res;
    }

    tool_entry_t *e = &r->entries[idx];
    if (e->local) {
        char *result = NULL;
        int rc = e->local(e->local_ctx, inv->arguments_json ? inv->arguments_json : "", &result);
        if (rc != 0) {
            /* thrown-exception path — Succeeded=false with a generic message. */
            free(result);
            ca_tel_tool_result_t *res = tool_result_make(inv->call_id, false, "{}",
                                                         "Tool invocation failed.");
            return res;
        }
        ca_tel_tool_result_t *res = tool_result_make(inv->call_id, true,
                                                     result ? result : "{}", NULL);
        free(result);
        return res;
    }

    if (e->webhook) {
        char *body = webhook_envelope(inv->call_id, inv->tool_name, inv->arguments_json);
        if (!body) return NULL;
        int status = 0;
        char *resp = NULL;
        int rc = -1;
        if (r->poster)
            rc = r->poster(r->poster_ctx, e->webhook, body, &status, &resp);
        free(body);
        if (rc != 0) {
            /* HttpRequestException before a response (connection failure). */
            free(resp);
            return tool_result_make(inv->call_id, false, "{}",
                                    "Connection error invoking webhook.");
        }
        bool is2xx = (status >= 200 && status < 300);
        if (!is2xx) {
            char *trunc = truncate_ellipsis(resp ? resp : "", 240);
            free(resp);
            if (!trunc) return NULL;
            /* "Webhook <status>: <trunc>" */
            size_t need = strlen("Webhook : ") + 16 + strlen(trunc) + 1;
            char *msg = (char *)malloc(need);
            if (!msg) { free(trunc); return NULL; }
            snprintf(msg, need, "Webhook %d: %s", status, trunc);
            free(trunc);
            ca_tel_tool_result_t *res = tool_result_make(inv->call_id, false, "{}", msg);
            free(msg);
            return res;
        }
        /* success: string.IsNullOrWhiteSpace(body) ? "{}" : body */
        const char *rj = (resp && !tel_is_ws(resp)) ? resp : "{}";
        ca_tel_tool_result_t *res = tool_result_make(inv->call_id, true, rj, NULL);
        free(resp);
        return res;
    }

    /* registered without handler or webhook */
    {
        size_t need = strlen("Tool '' is registered without a local handler or webhook.") +
                      (inv->tool_name ? strlen(inv->tool_name) : 0) + 1;
        char *msg = (char *)malloc(need);
        if (!msg) return NULL;
        snprintf(msg, need, "Tool '%s' is registered without a local handler or webhook.",
                 inv->tool_name ? inv->tool_name : "");
        ca_tel_tool_result_t *res = tool_result_make(inv->call_id, false, "{}", msg);
        free(msg);
        return res;
    }
}

/* ===========================================================================
 * ITelephonyCarrier — wrap + Null + Fallback
 * =========================================================================== */

typedef enum { CARRIER_BINDING, CARRIER_NULL, CARRIER_FALLBACK } carrier_kind_t;

struct ca_tel_carrier {
    carrier_kind_t          kind;
    /* BINDING */
    void                   *self;
    ca_tel_carrier_vtable_t vtable;
    /* FALLBACK */
    ca_tel_carrier_t      **children;   /* owned */
    size_t                  child_count;
    ca_tel_carrier_t       *null_fallback; /* owned; used when none configured */
    char                   *fallback_id;   /* "fallback(<n>)" */
};

ca_tel_carrier_t *ca_tel_carrier_wrap(void *self,
                                      const ca_tel_carrier_vtable_t *vtable) {
    if (!vtable) return NULL;
    ca_tel_carrier_t *c = (ca_tel_carrier_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->kind = CARRIER_BINDING;
    c->self = self;
    c->vtable = *vtable;
    return c;
}

void ca_tel_carrier_destroy(ca_tel_carrier_t *c) {
    if (!c) return;
    if (c->kind == CARRIER_BINDING) {
        if (c->vtable.destroy) c->vtable.destroy(c->self);
    } else if (c->kind == CARRIER_FALLBACK) {
        for (size_t i = 0; i < c->child_count; ++i) ca_tel_carrier_destroy(c->children[i]);
        free(c->children);
        ca_tel_carrier_destroy(c->null_fallback);
        free(c->fallback_id);
    }
    free(c);
}

/* ── Null carrier ───────────────────────────────────────────────────────── */

static const char *null_carrier_id(void *self) { (void)self; return "null"; }
static bool null_carrier_is_configured(void *self) { (void)self; return false; }
static int null_carrier_provision(void *self, const char *cc, const char *ac,
                                  ca_tel_provisioned_number_t *out) {
    (void)self; (void)cc; (void)ac; (void)out;
    return -1;   /* InvalidOperationException */
}
static int null_carrier_configure(void *self, const char *pn, const char *wh) {
    (void)self; (void)pn; (void)wh;
    return 0;    /* ValueTask.CompletedTask */
}
static ca_tel_call_session_t *null_carrier_dial(void *self, ca_tel_carrier_t *carrier,
                                                const char *from, const char *to,
                                                const char *url,
                                                const ca_tel_dial_options_t *o) {
    (void)self; (void)carrier; (void)from; (void)to; (void)url; (void)o;
    return NULL;  /* InvalidOperationException */
}
static ca_tel_provisioned_number_t *null_carrier_list(void *self, size_t *count) {
    (void)self;
    if (count) *count = 0;
    return NULL;  /* Array.Empty */
}

static const ca_tel_carrier_vtable_t NULL_CARRIER_VTABLE = {
    null_carrier_id, null_carrier_is_configured, null_carrier_provision,
    null_carrier_configure, null_carrier_dial, null_carrier_list,
    NULL, NULL, NULL
};

ca_tel_carrier_t *ca_tel_null_carrier_create(void) {
    ca_tel_carrier_t *c = (ca_tel_carrier_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->kind = CARRIER_NULL;
    c->vtable = NULL_CARRIER_VTABLE;
    return c;
}

/* ── Fallback carrier ───────────────────────────────────────────────────── */

ca_tel_carrier_t *ca_tel_carrier_fallback_create(ca_tel_carrier_t **carriers,
                                                 size_t count) {
    ca_tel_carrier_t *c = (ca_tel_carrier_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->kind = CARRIER_FALLBACK;
    c->null_fallback = ca_tel_null_carrier_create();
    if (!c->null_fallback) { free(c); return NULL; }
    if (count > 0) {
        c->children = (ca_tel_carrier_t **)calloc(count, sizeof(*c->children));
        if (!c->children) { ca_tel_carrier_destroy(c->null_fallback); free(c); return NULL; }
        for (size_t i = 0; i < count; ++i) c->children[i] = carriers[i];
        c->child_count = count;
    }
    char buf[32];
    snprintf(buf, sizeof(buf), "fallback(%zu)", count);
    c->fallback_id = tel_strdup(buf);
    if (!c->fallback_id) { ca_tel_carrier_destroy(c); return NULL; }
    return c;
}

/* pick the first configured child, else the null fallback. */
static ca_tel_carrier_t *fallback_pick(ca_tel_carrier_t *c) {
    for (size_t i = 0; i < c->child_count; ++i)
        if (ca_tel_carrier_is_configured(c->children[i])) return c->children[i];
    return c->null_fallback;
}

/* ── Carrier dispatch ───────────────────────────────────────────────────── */

const char *ca_tel_carrier_id(ca_tel_carrier_t *c) {
    if (!c) return NULL;
    if (c->kind == CARRIER_FALLBACK) return c->fallback_id;
    return c->vtable.carrier_id ? c->vtable.carrier_id(c->self) : NULL;
}
bool ca_tel_carrier_is_configured(ca_tel_carrier_t *c) {
    if (!c) return false;
    if (c->kind == CARRIER_FALLBACK) {
        for (size_t i = 0; i < c->child_count; ++i)
            if (ca_tel_carrier_is_configured(c->children[i])) return true;
        return false;
    }
    return c->vtable.is_configured ? c->vtable.is_configured(c->self) : false;
}
int ca_tel_carrier_provision_number(ca_tel_carrier_t *c, const char *cc,
                                    const char *ac, ca_tel_provisioned_number_t *out) {
    if (!c || !out) return -1;
    if (c->kind == CARRIER_FALLBACK) return ca_tel_carrier_provision_number(fallback_pick(c), cc, ac, out);
    if (!c->vtable.provision_number) return -1;
    return c->vtable.provision_number(c->self, cc, ac, out);
}
int ca_tel_carrier_configure_inbound(ca_tel_carrier_t *c, const char *pn,
                                     const char *wh) {
    if (!c) return -1;
    if (c->kind == CARRIER_FALLBACK) return ca_tel_carrier_configure_inbound(fallback_pick(c), pn, wh);
    if (!c->vtable.configure_inbound) return -1;
    return c->vtable.configure_inbound(c->self, pn, wh);
}
ca_tel_call_session_t *ca_tel_carrier_dial(ca_tel_carrier_t *c, const char *from,
                                           const char *to, const char *url,
                                           const ca_tel_dial_options_t *o) {
    if (!c) return NULL;
    if (c->kind == CARRIER_FALLBACK) {
        ca_tel_carrier_t *picked = fallback_pick(c);
        return picked->vtable.dial ? picked->vtable.dial(picked->self, picked, from, to, url, o) : NULL;
    }
    if (!c->vtable.dial) return NULL;
    return c->vtable.dial(c->self, c, from, to, url, o);
}
ca_tel_provisioned_number_t *ca_tel_carrier_list_numbers(ca_tel_carrier_t *c,
                                                         size_t *count) {
    if (!c) { if (count) *count = 0; return NULL; }
    if (c->kind == CARRIER_FALLBACK) return ca_tel_carrier_list_numbers(fallback_pick(c), count);
    if (!c->vtable.list_numbers) { if (count) *count = 0; return NULL; }
    return c->vtable.list_numbers(c->self, count);
}

/* internal REST helpers used by the media session (not in public header) */
int ca_tel_carrier_end_call_internal(ca_tel_carrier_t *c, const char *call_id);
int ca_tel_carrier_transfer_call_internal(ca_tel_carrier_t *c, const char *call_id,
                                          const char *target);

int ca_tel_carrier_end_call_internal(ca_tel_carrier_t *c, const char *call_id) {
    if (!c) return -1;
    if (c->kind == CARRIER_FALLBACK) return ca_tel_carrier_end_call_internal(fallback_pick(c), call_id);
    if (!c->vtable.end_call) return 0;   /* Null carrier: no-op */
    return c->vtable.end_call(c->self, call_id);
}
int ca_tel_carrier_transfer_call_internal(ca_tel_carrier_t *c, const char *call_id,
                                          const char *target) {
    if (!c) return -1;
    if (c->kind == CARRIER_FALLBACK) return ca_tel_carrier_transfer_call_internal(fallback_pick(c), call_id, target);
    if (!c->vtable.transfer_call) return -1;
    return c->vtable.transfer_call(c->self, call_id, target);
}
