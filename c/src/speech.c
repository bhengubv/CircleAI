/*
 * speech.c — CircleAI.Speech contract surface (C11 port).
 *
 * Ports Contracts.cs records + the in-box deterministic implementations
 * (NullImplementations.cs, VoiceActivityDetectors.cs, EchoCancellers.cs,
 * NoiseReducers.cs, EndOfTurnDetectors.cs, AudioFormatConverter.cs) plus a
 * deterministic keyword recognizer / template synthesizer / keyword wake-word
 * detector for the ASR/TTS/wake-word contracts whose real backends are cloud/
 * ONNX injected dependencies.
 *
 * Byte-exact with the C# BinaryPrimitives paths: PCM-16 little-endian, G.711
 * mu-law / a-law encode/decode, and linear-interpolation resample all reproduce
 * the reference arithmetic.
 *
 * Pure C11 + libc + libm.
 */

#include "circle_ai/speech.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <ctype.h>

/* ── shared helpers ─────────────────────────────────────────────────────── */

static char *sp_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *sp_strdup_empty(const char *s) {
    return sp_strdup(s ? s : "");
}

/* PCM-16 little-endian read/write (BinaryPrimitives.Read/WriteInt16LittleEndian). */
static int16_t rd_i16le(const uint8_t *p) {
    return (int16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}
static void wr_i16le(uint8_t *p, int16_t v) {
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)((v >> 8) & 0xFF);
}

/* ── record frees / copies ──────────────────────────────────────────────── */

void ca_transcribed_segment_free(ca_transcribed_segment_t *s) {
    if (!s) return;
    free(s->text);
    free(s->language);
    s->text = s->language = NULL;
}
void ca_transcribed_segment_free_array(ca_transcribed_segment_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_transcribed_segment_free(&arr[i]);
    free(arr);
}
ca_transcribed_segment_t *ca_transcribed_segment_copy(ca_transcribed_segment_t *dst,
                                                      const ca_transcribed_segment_t *src) {
    if (!dst || !src) return dst;
    dst->text        = sp_strdup_empty(src->text);
    dst->offset_ms   = src->offset_ms;
    dst->duration_ms = src->duration_ms;
    dst->language    = sp_strdup(src->language);
    dst->confidence  = src->confidence;
    return dst;
}

void ca_transcription_result_free(ca_transcription_result_t *r) {
    if (!r) return;
    free(r->text);
    free(r->language);
    ca_transcribed_segment_free_array(r->segments, r->segment_count);
    r->text = r->language = NULL;
    r->segments = NULL;
    r->segment_count = 0;
}
ca_transcription_result_t *ca_transcription_result_copy(ca_transcription_result_t *dst,
                                                        const ca_transcription_result_t *src) {
    if (!dst || !src) return dst;
    memset(dst, 0, sizeof(*dst));
    dst->text     = sp_strdup_empty(src->text);
    dst->language = sp_strdup(src->language);
    dst->total_duration_ms = src->total_duration_ms;
    if (src->segment_count && src->segments) {
        dst->segments = (ca_transcribed_segment_t *)calloc(src->segment_count,
                                                           sizeof(*dst->segments));
        if (dst->segments) {
            for (size_t i = 0; i < src->segment_count; ++i)
                ca_transcribed_segment_copy(&dst->segments[i], &src->segments[i]);
            dst->segment_count = src->segment_count;
        }
    }
    return dst;
}

void ca_synthesis_result_free(ca_synthesis_result_t *r) {
    if (!r) return;
    free(r->audio_pcm16_mono);
    r->audio_pcm16_mono = NULL;
    r->audio_len = 0;
}

void ca_ocr_text_block_free(ca_ocr_text_block_t *b) {
    if (!b) return;
    free(b->text);
    free(b->language);
    b->text = b->language = NULL;
}
void ca_ocr_result_free(ca_ocr_result_t *r) {
    if (!r) return;
    free(r->text);
    for (size_t i = 0; i < r->block_count; ++i) ca_ocr_text_block_free(&r->blocks[i]);
    free(r->blocks);
    r->text = NULL;
    r->blocks = NULL;
    r->block_count = 0;
}

void ca_wake_word_event_free(ca_wake_word_event_t *e) {
    if (!e) return;
    free(e->keyword);
    e->keyword = NULL;
}

/* ===========================================================================
 * NullSpeechRecognizer
 * =========================================================================== */

struct ca_null_speech_recognizer { int _; };

ca_null_speech_recognizer_t *ca_null_speech_recognizer_create(void) {
    return (ca_null_speech_recognizer_t *)calloc(1, sizeof(ca_null_speech_recognizer_t));
}
void ca_null_speech_recognizer_destroy(ca_null_speech_recognizer_t *r) { free(r); }

static const char *nullrec_backend_id(void *self) { (void)self; return "null"; }
static int nullrec_transcribe(void *self, const uint8_t *audio, size_t len,
                              int rate, const char *hint,
                              ca_transcription_result_t *out) {
    (void)self; (void)audio; (void)len; (void)rate;
    if (!out) return -1;
    memset(out, 0, sizeof(*out));
    out->text = sp_strdup_empty(NULL);   /* "" */
    out->language = sp_strdup(hint);     /* echoes hint (may be NULL) */
    out->segments = NULL;
    out->segment_count = 0;
    out->total_duration_ms = 0;
    return 0;
}
ca_speech_recognizer_t ca_null_speech_recognizer_as_recognizer(
    ca_null_speech_recognizer_t *r) {
    ca_speech_recognizer_t v;
    v.self = r;
    v.backend_id = nullrec_backend_id;
    v.transcribe = nullrec_transcribe;
    return v;
}

/* ===========================================================================
 * KeywordSpeechRecognizer
 * =========================================================================== */

typedef struct {
    size_t min_samples;
    char  *phrase;   /* owned */
    float  confidence;
} kw_rule_t;

struct ca_keyword_speech_recognizer {
    kw_rule_t *rules;
    size_t     count, cap;
};

ca_keyword_speech_recognizer_t *ca_keyword_speech_recognizer_create(void) {
    return (ca_keyword_speech_recognizer_t *)calloc(1, sizeof(ca_keyword_speech_recognizer_t));
}
void ca_keyword_speech_recognizer_destroy(ca_keyword_speech_recognizer_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->count; ++i) free(r->rules[i].phrase);
    free(r->rules);
    free(r);
}
int ca_keyword_speech_recognizer_add(ca_keyword_speech_recognizer_t *r,
                                     size_t min_samples, const char *phrase,
                                     float confidence) {
    if (!r || !phrase) return -1;
    if (r->count == r->cap) {
        size_t nc = r->cap ? r->cap * 2 : 4;
        void *n = realloc(r->rules, nc * sizeof(*r->rules));
        if (!n) return -1;
        r->rules = (kw_rule_t *)n;
        r->cap = nc;
    }
    r->rules[r->count].min_samples = min_samples;
    r->rules[r->count].phrase = sp_strdup(phrase);
    if (!r->rules[r->count].phrase) return -1;
    r->rules[r->count].confidence = confidence;
    r->count++;
    return 0;
}

static const char *kwrec_backend_id(void *self) { (void)self; return "keyword"; }
static int kwrec_transcribe(void *self, const uint8_t *audio, size_t len,
                            int rate, const char *hint,
                            ca_transcription_result_t *out) {
    ca_keyword_speech_recognizer_t *r = (ca_keyword_speech_recognizer_t *)self;
    if (!r || !out || rate <= 0) return -1;
    memset(out, 0, sizeof(*out));

    (void)audio;
    size_t sample_count = len / 2;
    int64_t total_ms = (int64_t)sample_count * 1000 / rate;

    /* Collect matching phrases (insertion order). */
    size_t matched = 0;
    for (size_t i = 0; i < r->count; ++i)
        if (sample_count >= r->rules[i].min_samples) matched++;

    if (matched > 0) {
        out->segments = (ca_transcribed_segment_t *)calloc(matched, sizeof(*out->segments));
        if (!out->segments) return -1;
    }

    /* Build joined text and per-phrase segments. Each segment spans an equal
     * share of the total duration; offsets accumulate. */
    size_t total_text = 0;
    for (size_t i = 0; i < r->count; ++i)
        if (sample_count >= r->rules[i].min_samples)
            total_text += strlen(r->rules[i].phrase) + 1; /* +space */
    char *text = (char *)malloc(total_text ? total_text : 1);
    if (!text) { ca_transcribed_segment_free_array(out->segments, matched); return -1; }
    text[0] = '\0';

    int64_t per = matched ? total_ms / (int64_t)matched : 0;
    size_t si = 0;
    int64_t offset = 0;
    size_t tlen = 0;
    for (size_t i = 0; i < r->count; ++i) {
        if (sample_count < r->rules[i].min_samples) continue;
        const char *ph = r->rules[i].phrase;
        if (tlen > 0) text[tlen++] = ' ';
        size_t pl = strlen(ph);
        memcpy(text + tlen, ph, pl);
        tlen += pl;
        text[tlen] = '\0';

        ca_transcribed_segment_t *seg = &out->segments[si++];
        seg->text        = sp_strdup(ph);
        seg->offset_ms   = offset;
        seg->duration_ms = per;
        seg->language    = sp_strdup(hint);
        seg->confidence  = r->rules[i].confidence;
        offset += per;
    }
    out->segment_count = matched;
    out->text = text;
    out->language = sp_strdup(hint);
    out->total_duration_ms = total_ms;
    return 0;
}
ca_speech_recognizer_t ca_keyword_speech_recognizer_as_recognizer(
    ca_keyword_speech_recognizer_t *r) {
    ca_speech_recognizer_t v;
    v.self = r;
    v.backend_id = kwrec_backend_id;
    v.transcribe = kwrec_transcribe;
    return v;
}

/* ===========================================================================
 * NullSpeechSynthesizer
 * =========================================================================== */

struct ca_null_speech_synthesizer { int _; };

ca_null_speech_synthesizer_t *ca_null_speech_synthesizer_create(void) {
    return (ca_null_speech_synthesizer_t *)calloc(1, sizeof(ca_null_speech_synthesizer_t));
}
void ca_null_speech_synthesizer_destroy(ca_null_speech_synthesizer_t *s) { free(s); }

static const char *nullsyn_backend_id(void *self) { (void)self; return "null"; }
static int nullsyn_synthesize(void *self, const char *text, const char *voice,
                              const char *hint, ca_synthesis_result_t *out) {
    (void)self; (void)text; (void)voice; (void)hint;
    if (!out) return -1;
    memset(out, 0, sizeof(*out));
    out->audio_pcm16_mono = NULL;
    out->audio_len = 0;
    out->sample_rate_hz = 16000;
    out->duration_ms = 0;
    return 0;
}
ca_speech_synthesizer_t ca_null_speech_synthesizer_as_synthesizer(
    ca_null_speech_synthesizer_t *s) {
    ca_speech_synthesizer_t v;
    v.self = s;
    v.backend_id = nullsyn_backend_id;
    v.synthesize = nullsyn_synthesize;
    return v;
}

/* ===========================================================================
 * TemplateSpeechSynthesizer
 * =========================================================================== */

struct ca_template_speech_synthesizer {
    int sample_rate_hz;
    int samples_per_char;
};

ca_template_speech_synthesizer_t *ca_template_speech_synthesizer_create(
    int sample_rate_hz, int samples_per_char) {
    ca_template_speech_synthesizer_t *s =
        (ca_template_speech_synthesizer_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->sample_rate_hz   = sample_rate_hz > 0 ? sample_rate_hz : 16000;
    s->samples_per_char = samples_per_char > 0 ? samples_per_char : 160;
    return s;
}
void ca_template_speech_synthesizer_destroy(ca_template_speech_synthesizer_t *s) { free(s); }

static const char *tplsyn_backend_id(void *self) { (void)self; return "template"; }
static int tplsyn_synthesize(void *self, const char *text, const char *voice,
                             const char *hint, ca_synthesis_result_t *out) {
    ca_template_speech_synthesizer_t *s = (ca_template_speech_synthesizer_t *)self;
    (void)voice; (void)hint;
    if (!s || !out) return -1;
    memset(out, 0, sizeof(*out));
    const char *t = text ? text : "";
    size_t nchars = strlen(t);
    size_t nsamples = nchars * (size_t)s->samples_per_char;
    out->sample_rate_hz = s->sample_rate_hz;
    if (nsamples == 0) {
        out->audio_pcm16_mono = NULL;
        out->audio_len = 0;
        out->duration_ms = 0;
        return 0;
    }
    uint8_t *buf = (uint8_t *)malloc(nsamples * 2);
    if (!buf) return -1;
    /* Square wave: half-period derived from char code, fixed amplitude. Identical
     * text => identical bytes (deterministic). */
    size_t idx = 0;
    for (size_t c = 0; c < nchars; ++c) {
        unsigned code = (unsigned char)t[c];
        int half_period = 4 + (int)(code % 60);   /* 4..63 samples */
        for (int k = 0; k < s->samples_per_char; ++k) {
            int phase = (k / half_period) & 1;
            int16_t v = phase ? (int16_t)8192 : (int16_t)-8192;
            wr_i16le(buf + idx * 2, v);
            idx++;
        }
    }
    out->audio_pcm16_mono = buf;
    out->audio_len = nsamples * 2;
    out->duration_ms = (int64_t)nsamples * 1000 / s->sample_rate_hz;
    return 0;
}
ca_speech_synthesizer_t ca_template_speech_synthesizer_as_synthesizer(
    ca_template_speech_synthesizer_t *s) {
    ca_speech_synthesizer_t v;
    v.self = s;
    v.backend_id = tplsyn_backend_id;
    v.synthesize = tplsyn_synthesize;
    return v;
}

/* ===========================================================================
 * IWakeWordDetector — Subscribe/Start/Stop + fire fan-out
 * =========================================================================== */

/* A per-subscription FIFO of wake events. */
typedef struct {
    ca_wake_word_event_t *items; /* owned array; each item owns keyword */
    size_t head, count, cap;
} wke_fifo_t;

static void wke_move_destroy(ca_wake_word_event_t *e) {
    if (!e) return;
    free(e->keyword);
    e->keyword = NULL;
}
static bool wke_fifo_push(wke_fifo_t *q, ca_wake_word_event_t item) {
    if (q->count == q->cap) {
        if (q->head > 0) {
            size_t live = q->count - q->head;
            memmove(q->items, q->items + q->head, live * sizeof(*q->items));
            q->count = live;
            q->head = 0;
        }
        if (q->count == q->cap) {
            size_t nc = q->cap ? q->cap * 2 : 4;
            void *ni = realloc(q->items, nc * sizeof(*q->items));
            if (!ni) return false;
            q->items = (ca_wake_word_event_t *)ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = item;
    return true;
}
static bool wke_fifo_pop(wke_fifo_t *q, ca_wake_word_event_t *out) {
    if (q->head >= q->count) return false;
    *out = q->items[q->head];
    memset(&q->items[q->head], 0, sizeof(q->items[q->head]));
    q->head++;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return true;
}
static void wke_fifo_free(wke_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i) wke_move_destroy(&q->items[i]);
    free(q->items);
    q->items = NULL;
    q->head = q->count = q->cap = 0;
}

struct ca_speech_wake_sub {
    ca_speech_wake_detector_t *owner;
    ca_speech_wake_handler_fn  handler;
    void                      *ctx;
    wke_fifo_t                 queue;
    bool                       live;
};

struct ca_speech_wake_detector {
    char                   *backend_id;  /* owned */
    char                   *keyword;     /* owned; NULL for null detector */
    bool                    keyword_mode;
    bool                    listening;
    ca_speech_wake_sub_t  **subs;
    size_t                  count, cap;
};

static ca_speech_wake_detector_t *wake_new(const char *backend_id,
                                           const char *keyword, bool keyword_mode) {
    ca_speech_wake_detector_t *d =
        (ca_speech_wake_detector_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->backend_id = sp_strdup(backend_id);
    d->keyword = keyword ? sp_strdup(keyword) : NULL;
    d->keyword_mode = keyword_mode;
    return d;
}
ca_speech_wake_detector_t *ca_speech_null_wake_detector_create(void) {
    return wake_new("null", NULL, false);
}
ca_speech_wake_detector_t *ca_speech_keyword_wake_detector_create(const char *keyword) {
    return wake_new("keyword", keyword ? keyword : "hey b", true);
}
void ca_speech_wake_detector_destroy(ca_speech_wake_detector_t *d) {
    if (!d) return;
    for (size_t i = 0; i < d->count; ++i) {
        wke_fifo_free(&d->subs[i]->queue);
        free(d->subs[i]);
    }
    free(d->subs);
    free(d->backend_id);
    free(d->keyword);
    free(d);
}
const char *ca_speech_wake_detector_backend_id(const ca_speech_wake_detector_t *d) {
    return d ? d->backend_id : NULL;
}
bool ca_speech_wake_detector_is_listening(const ca_speech_wake_detector_t *d) {
    return d ? d->listening : false;
}
void ca_speech_wake_detector_start(ca_speech_wake_detector_t *d) {
    if (d) d->listening = true;   /* idempotent */
}
void ca_speech_wake_detector_stop(ca_speech_wake_detector_t *d) {
    if (d) d->listening = false;  /* idempotent */
}

ca_speech_wake_sub_t *ca_speech_wake_detector_subscribe(
    ca_speech_wake_detector_t *d, ca_speech_wake_handler_fn handler, void *ctx) {
    if (!d) return NULL;
    if (d->count == d->cap) {
        size_t nc = d->cap ? d->cap * 2 : 4;
        void *ns = realloc(d->subs, nc * sizeof(*d->subs));
        if (!ns) return NULL;
        d->subs = (ca_speech_wake_sub_t **)ns;
        d->cap = nc;
    }
    ca_speech_wake_sub_t *s = (ca_speech_wake_sub_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->owner = d;
    s->handler = handler;
    s->ctx = ctx;
    s->live = true;
    d->subs[d->count++] = s;
    return s;
}
void ca_speech_wake_detector_unsubscribe(ca_speech_wake_detector_t *d,
                                         ca_speech_wake_sub_t *sub) {
    if (!d || !sub) return;
    for (size_t i = 0; i < d->count; ++i) {
        if (d->subs[i] == sub) {
            wke_fifo_free(&sub->queue);
            free(sub);
            d->subs[i] = d->subs[--d->count];
            return;
        }
    }
}
bool ca_speech_wake_sub_next(ca_speech_wake_sub_t *sub, ca_wake_word_event_t *out) {
    if (!sub || !out) return false;
    return wke_fifo_pop(&sub->queue, out);
}
size_t ca_speech_wake_sub_pending(const ca_speech_wake_sub_t *sub) {
    return sub ? (sub->queue.count - sub->queue.head) : 0;
}

/* case-insensitive substring search (String.Contains(..., OrdinalIgnoreCase)). */
static bool ci_contains(const char *hay, const char *needle) {
    if (!hay || !needle) return false;
    if (*needle == '\0') return true;
    size_t nl = strlen(needle);
    for (const char *h = hay; *h; ++h) {
        size_t k = 0;
        while (k < nl && h[k] &&
               tolower((unsigned char)h[k]) == tolower((unsigned char)needle[k]))
            k++;
        if (k == nl) return true;
    }
    return false;
}

size_t ca_speech_wake_detector_feed(ca_speech_wake_detector_t *d,
                                    const char *frame_text, int64_t at_utc_ms) {
    if (!d || !d->listening || !d->keyword_mode) return 0;
    if (!ci_contains(frame_text, d->keyword)) return 0;

    /* Snapshot subs, then deliver: synchronous handler + buffered cursor. There
     * is no lock a handler could re-enter here (single-threaded model), but we
     * still fan out over a stable snapshot count. */
    size_t delivered = 0;
    size_t n = d->count;
    for (size_t i = 0; i < n; ++i) {
        ca_speech_wake_sub_t *s = d->subs[i];
        if (!s->live) continue;
        /* Buffer a copy on the cursor (unbounded). */
        ca_wake_word_event_t item;
        memset(&item, 0, sizeof(item));
        item.keyword = sp_strdup(d->keyword);
        item.confidence = 1.0f;
        item.detected_at_utc_ms = at_utc_ms;
        if (item.keyword && wke_fifo_push(&s->queue, item)) {
            delivered++;
        } else {
            wke_move_destroy(&item);
        }
        /* Fire the handler synchronously with a borrowed event. */
        if (s->handler) {
            ca_wake_word_event_t borrowed;
            borrowed.keyword = d->keyword;
            borrowed.confidence = 1.0f;
            borrowed.detected_at_utc_ms = at_utc_ms;
            s->handler(s->ctx, &borrowed);
        }
    }
    return delivered;
}

/* ===========================================================================
 * IEchoCanceller
 * =========================================================================== */

struct ca_null_echo_canceller { int _; };
ca_null_echo_canceller_t *ca_null_echo_canceller_create(void) {
    return (ca_null_echo_canceller_t *)calloc(1, sizeof(ca_null_echo_canceller_t));
}
void ca_null_echo_canceller_destroy(ca_null_echo_canceller_t *c) { free(c); }
static const char *nullaec_backend_id(void *self) { (void)self; return "null"; }
static int nullaec_cancel(void *self, const uint8_t *near, size_t near_len,
                          const uint8_t *far, size_t far_len, int rate,
                          uint8_t *dst, size_t dst_cap, size_t *written) {
    (void)self; (void)far; (void)far_len; (void)rate;
    if (!near || !dst || dst_cap < near_len) return -1;
    memcpy(dst, near, near_len);
    if (written) *written = near_len;
    return 0;
}
static void nullaec_reset(void *self) { (void)self; }
ca_echo_canceller_t ca_null_echo_canceller_as_canceller(ca_null_echo_canceller_t *c) {
    ca_echo_canceller_t v;
    v.self = c;
    v.backend_id = nullaec_backend_id;
    v.cancel = nullaec_cancel;
    v.reset = nullaec_reset;
    return v;
}

struct ca_nlms_echo_canceller {
    float *w;          /* filter_length taps */
    float *ref_buffer; /* circular */
    int    filter_length;
    float  step_size;
    float  epsilon;
    int    ref_index;
};
ca_nlms_echo_canceller_t *ca_nlms_echo_canceller_create(int filter_length,
                                                        float step_size,
                                                        float epsilon) {
    ca_nlms_echo_canceller_t *c = (ca_nlms_echo_canceller_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->filter_length = filter_length > 0 ? filter_length : 256;
    c->step_size = step_size;
    c->epsilon = epsilon;
    c->w = (float *)calloc((size_t)c->filter_length, sizeof(float));
    c->ref_buffer = (float *)calloc((size_t)c->filter_length, sizeof(float));
    if (!c->w || !c->ref_buffer) {
        free(c->w); free(c->ref_buffer); free(c);
        return NULL;
    }
    return c;
}
void ca_nlms_echo_canceller_destroy(ca_nlms_echo_canceller_t *c) {
    if (!c) return;
    free(c->w);
    free(c->ref_buffer);
    free(c);
}
static const char *nlms_backend_id(void *self) { (void)self; return "nlms"; }
static int nlms_cancel(void *self, const uint8_t *near, size_t near_len,
                       const uint8_t *far, size_t far_len, int rate,
                       uint8_t *dst, size_t dst_cap, size_t *written) {
    ca_nlms_echo_canceller_t *c = (ca_nlms_echo_canceller_t *)self;
    (void)rate;
    if (!c || !near || !far || !dst) return -1;
    if (near_len != far_len) return -1;      /* ArgumentException in C# */
    if (dst_cap < near_len) return -1;

    size_t sample_count = near_len / 2;
    for (size_t n = 0; n < sample_count; ++n) {
        float mic = rd_i16le(near + n * 2) / (float)32767;
        float farS = rd_i16le(far + n * 2) / (float)32767;

        c->ref_buffer[c->ref_index] = farS;

        float echo = 0.0f;
        float power = c->epsilon;
        for (int k = 0; k < c->filter_length; ++k) {
            int r = (c->ref_index - k + c->filter_length) % c->filter_length;
            float x = c->ref_buffer[r];
            echo += c->w[k] * x;
            power += x * x;
        }
        float error = mic - echo;
        float mu = c->step_size / power;
        for (int k = 0; k < c->filter_length; ++k) {
            int r = (c->ref_index - k + c->filter_length) % c->filter_length;
            c->w[k] += mu * error * c->ref_buffer[r];
        }
        c->ref_index = (c->ref_index + 1) % c->filter_length;

        float scaled = error * 32767.0f;
        if (scaled > 32767.0f) scaled = 32767.0f;
        if (scaled < -32768.0f) scaled = -32768.0f;
        wr_i16le(dst + n * 2, (int16_t)scaled);
    }
    if (written) *written = near_len;
    return 0;
}
static void nlms_reset(void *self) {
    ca_nlms_echo_canceller_t *c = (ca_nlms_echo_canceller_t *)self;
    if (!c) return;
    memset(c->w, 0, (size_t)c->filter_length * sizeof(float));
    memset(c->ref_buffer, 0, (size_t)c->filter_length * sizeof(float));
    c->ref_index = 0;
}
ca_echo_canceller_t ca_nlms_echo_canceller_as_canceller(ca_nlms_echo_canceller_t *c) {
    ca_echo_canceller_t v;
    v.self = c;
    v.backend_id = nlms_backend_id;
    v.cancel = nlms_cancel;
    v.reset = nlms_reset;
    return v;
}

struct ca_webrtc_echo_canceller {
    bool                    has_runner;
    ca_aec_model_runner_t   runner;
    ca_nlms_echo_canceller_t *fallback;
    char                   *backend_id; /* owned */
};
ca_webrtc_echo_canceller_t *ca_webrtc_echo_canceller_create(bool has_runner,
                                                            ca_aec_model_runner_t runner) {
    ca_webrtc_echo_canceller_t *c = (ca_webrtc_echo_canceller_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->has_runner = has_runner;
    c->runner = runner;
    c->fallback = ca_nlms_echo_canceller_create(256, 0.4f, 1e-6f);
    c->backend_id = sp_strdup(has_runner ? "webrtc-aec3" : "webrtc-aec3 (fallback)");
    if (!c->fallback || !c->backend_id) {
        ca_nlms_echo_canceller_destroy(c->fallback);
        free(c->backend_id);
        free(c);
        return NULL;
    }
    return c;
}
void ca_webrtc_echo_canceller_destroy(ca_webrtc_echo_canceller_t *c) {
    if (!c) return;
    ca_nlms_echo_canceller_destroy(c->fallback);
    free(c->backend_id);
    free(c);
}
static const char *webrtc_backend_id(void *self) {
    ca_webrtc_echo_canceller_t *c = (ca_webrtc_echo_canceller_t *)self;
    return c ? c->backend_id : NULL;
}
static int webrtc_cancel(void *self, const uint8_t *near, size_t near_len,
                         const uint8_t *far, size_t far_len, int rate,
                         uint8_t *dst, size_t dst_cap, size_t *written) {
    ca_webrtc_echo_canceller_t *c = (ca_webrtc_echo_canceller_t *)self;
    if (!c) return -1;
    if (c->has_runner && c->runner.process) {
        int w = c->runner.process(c->runner.self, near, near_len, far, far_len,
                                  rate, dst, dst_cap);
        if (w < 0) return -1;
        if (written) *written = (size_t)w;
        return 0;
    }
    return nlms_cancel(c->fallback, near, near_len, far, far_len, rate, dst, dst_cap,
                       written);
}
static void webrtc_reset(void *self) {
    ca_webrtc_echo_canceller_t *c = (ca_webrtc_echo_canceller_t *)self;
    if (!c) return;
    nlms_reset(c->fallback);
    if (c->has_runner && c->runner.reset) c->runner.reset(c->runner.self);
}
ca_echo_canceller_t ca_webrtc_echo_canceller_as_canceller(ca_webrtc_echo_canceller_t *c) {
    ca_echo_canceller_t v;
    v.self = c;
    v.backend_id = webrtc_backend_id;
    v.cancel = webrtc_cancel;
    v.reset = webrtc_reset;
    return v;
}

/* ===========================================================================
 * INoiseReducer
 * =========================================================================== */

struct ca_null_noise_reducer { int _; };
ca_null_noise_reducer_t *ca_null_noise_reducer_create(void) {
    return (ca_null_noise_reducer_t *)calloc(1, sizeof(ca_null_noise_reducer_t));
}
void ca_null_noise_reducer_destroy(ca_null_noise_reducer_t *r) { free(r); }
static const char *nullnr_backend_id(void *self) { (void)self; return "null"; }
static bool nullnr_available(void *self) { (void)self; return true; }
static int nullnr_reduce(void *self, const uint8_t *audio, size_t len, int rate,
                         uint8_t *dst, size_t dst_cap, size_t *written) {
    (void)self; (void)rate;
    if (!audio || !dst || dst_cap < len) return -1;
    memcpy(dst, audio, len);
    if (written) *written = len;
    return 0;
}
ca_noise_reducer_t ca_null_noise_reducer_as_reducer(ca_null_noise_reducer_t *r) {
    ca_noise_reducer_t v;
    v.self = r;
    v.backend_id = nullnr_backend_id;
    v.is_available = nullnr_available;
    v.reduce = nullnr_reduce;
    return v;
}

struct ca_spectral_noise_reducer {
    float floor_estimate;
    float attenuation;
};
ca_spectral_noise_reducer_t *ca_spectral_noise_reducer_create(float floor_estimate,
                                                              float attenuation) {
    ca_spectral_noise_reducer_t *r = (ca_spectral_noise_reducer_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->floor_estimate = floor_estimate;
    r->attenuation = attenuation;
    return r;
}
void ca_spectral_noise_reducer_destroy(ca_spectral_noise_reducer_t *r) { free(r); }
static const char *specnr_backend_id(void *self) { (void)self; return "passthrough"; }
static bool specnr_available(void *self) { (void)self; return true; }
static int specnr_reduce(void *self, const uint8_t *audio, size_t len, int rate,
                         uint8_t *dst, size_t dst_cap, size_t *written) {
    ca_spectral_noise_reducer_t *r = (ca_spectral_noise_reducer_t *)self;
    (void)rate;
    if (!r || !audio || !dst || dst_cap < len) return -1;
    size_t n = len / 2;
    int floor_v = (int)(r->floor_estimate * 32767.0f);
    for (size_t i = 0; i < n; ++i) {
        int s = rd_i16le(audio + i * 2);
        int a = s < 0 ? -s : s;
        int16_t o;
        if (a <= floor_v) {
            o = (int16_t)(int)(s * r->attenuation);
        } else {
            o = (int16_t)s;
        }
        wr_i16le(dst + i * 2, o);
    }
    if (written) *written = len;
    return 0;
}
ca_noise_reducer_t ca_spectral_noise_reducer_as_reducer(ca_spectral_noise_reducer_t *r) {
    ca_noise_reducer_t v;
    v.self = r;
    v.backend_id = specnr_backend_id;
    v.is_available = specnr_available;
    v.reduce = specnr_reduce;
    return v;
}

/* Krisp / DeepFilterNet share the same wrapper shape. */
typedef struct {
    bool                       has_runner;
    ca_noise_model_runner_t    runner;
    ca_spectral_noise_reducer_t *fallback;
    char                      *backend_id; /* owned */
} nr_wrapper_t;

static nr_wrapper_t *nr_wrapper_create(bool has_runner, ca_noise_model_runner_t runner,
                                       const char *live_id, const char *fb_id) {
    nr_wrapper_t *w = (nr_wrapper_t *)calloc(1, sizeof(*w));
    if (!w) return NULL;
    w->has_runner = has_runner;
    w->runner = runner;
    w->fallback = ca_spectral_noise_reducer_create(0.008f, 0.25f);
    w->backend_id = sp_strdup(has_runner ? live_id : fb_id);
    if (!w->fallback || !w->backend_id) {
        ca_spectral_noise_reducer_destroy(w->fallback);
        free(w->backend_id);
        free(w);
        return NULL;
    }
    return w;
}
static const char *nrwrap_backend_id(void *self) {
    nr_wrapper_t *w = (nr_wrapper_t *)self;
    return w ? w->backend_id : NULL;
}
static bool nrwrap_available(void *self) { (void)self; return true; }
static int nrwrap_reduce(void *self, const uint8_t *audio, size_t len, int rate,
                         uint8_t *dst, size_t dst_cap, size_t *written) {
    nr_wrapper_t *w = (nr_wrapper_t *)self;
    if (!w) return -1;
    if (w->has_runner && w->runner.process) {
        int r = w->runner.process(w->runner.self, audio, len, rate, dst, dst_cap);
        if (r < 0) return -1;
        if (written) *written = (size_t)r;
        return 0;
    }
    return specnr_reduce(w->fallback, audio, len, rate, dst, dst_cap, written);
}
static ca_noise_reducer_t nrwrap_as_reducer(nr_wrapper_t *w) {
    ca_noise_reducer_t v;
    v.self = w;
    v.backend_id = nrwrap_backend_id;
    v.is_available = nrwrap_available;
    v.reduce = nrwrap_reduce;
    return v;
}

struct ca_krisp_noise_reducer { nr_wrapper_t *w; };
ca_krisp_noise_reducer_t *ca_krisp_noise_reducer_create(bool has_runner,
                                                        ca_noise_model_runner_t runner) {
    ca_krisp_noise_reducer_t *r = (ca_krisp_noise_reducer_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->w = nr_wrapper_create(has_runner, runner, "krisp", "krisp (fallback)");
    if (!r->w) { free(r); return NULL; }
    return r;
}
void ca_krisp_noise_reducer_destroy(ca_krisp_noise_reducer_t *r) {
    if (!r) return;
    if (r->w) {
        ca_spectral_noise_reducer_destroy(r->w->fallback);
        free(r->w->backend_id);
        free(r->w);
    }
    free(r);
}
ca_noise_reducer_t ca_krisp_noise_reducer_as_reducer(ca_krisp_noise_reducer_t *r) {
    return nrwrap_as_reducer(r ? r->w : NULL);
}

struct ca_deepfilternet_noise_reducer { nr_wrapper_t *w; };
ca_deepfilternet_noise_reducer_t *ca_deepfilternet_noise_reducer_create(
    bool has_runner, ca_noise_model_runner_t runner) {
    ca_deepfilternet_noise_reducer_t *r =
        (ca_deepfilternet_noise_reducer_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->w = nr_wrapper_create(has_runner, runner, "deepfilternet", "deepfilternet (fallback)");
    if (!r->w) { free(r); return NULL; }
    return r;
}
void ca_deepfilternet_noise_reducer_destroy(ca_deepfilternet_noise_reducer_t *r) {
    if (!r) return;
    if (r->w) {
        ca_spectral_noise_reducer_destroy(r->w->fallback);
        free(r->w->backend_id);
        free(r->w);
    }
    free(r);
}
ca_noise_reducer_t ca_deepfilternet_noise_reducer_as_reducer(
    ca_deepfilternet_noise_reducer_t *r) {
    return nrwrap_as_reducer(r ? r->w : NULL);
}

/* ===========================================================================
 * IEndOfTurnDetector
 * =========================================================================== */

struct ca_null_eot_detector { int _; };
ca_null_eot_detector_t *ca_null_eot_detector_create(void) {
    return (ca_null_eot_detector_t *)calloc(1, sizeof(ca_null_eot_detector_t));
}
void ca_null_eot_detector_destroy(ca_null_eot_detector_t *d) { free(d); }
static const char *nulleot_backend_id(void *self) { (void)self; return "null"; }
static int nulleot_predict(void *self, const char *partial, int64_t silence,
                           ca_end_of_turn_result_t *out) {
    (void)self; (void)partial; (void)silence;
    if (!out) return -1;
    out->is_complete = true;
    out->confidence = 1.0f;
    out->wait_more_ms = 0;
    return 0;
}
static void nulleot_reset(void *self) { (void)self; }
ca_end_of_turn_detector_t ca_null_eot_detector_as_detector(ca_null_eot_detector_t *d) {
    ca_end_of_turn_detector_t v;
    v.self = d;
    v.backend_id = nulleot_backend_id;
    v.predict = nulleot_predict;
    v.reset = nulleot_reset;
    return v;
}

struct ca_rule_eot_detector {
    int64_t min_silence_ms;
    int64_t hanging_silence_ms;
    int64_t max_silence_ms;
};
ca_rule_eot_detector_t *ca_rule_eot_detector_create(int64_t min_silence_ms,
                                                    int64_t hanging_silence_ms,
                                                    int64_t max_silence_ms) {
    ca_rule_eot_detector_t *d = (ca_rule_eot_detector_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->min_silence_ms     = min_silence_ms     > 0 ? min_silence_ms     : 400;
    d->hanging_silence_ms = hanging_silence_ms > 0 ? hanging_silence_ms : 900;
    d->max_silence_ms     = max_silence_ms     > 0 ? max_silence_ms     : 2500;
    return d;
}
void ca_rule_eot_detector_destroy(ca_rule_eot_detector_t *d) { free(d); }
static const char *ruleeot_backend_id(void *self) { (void)self; return "rules"; }

/* Terminal punctuation: ".", "!", "?" plus the CJK "。！？". Text is byte-scanned
 * for either the ASCII terminators at the tail or the exact 3-byte UTF-8 CJK
 * sequences (EFBC 81 = ！, EFBC9F = ？, E38082 = 。). */
static bool ends_terminal(const char *t, size_t len) {
    if (len == 0) return false;
    char c = t[len - 1];
    if (c == '.' || c == '!' || c == '?') return true;
    if (len >= 3) {
        const unsigned char *u = (const unsigned char *)(t + len - 3);
        /* 。 U+3002 = E3 80 82 ; ！ U+FF01 = EF BC 81 ; ？ U+FF1F = EF BC 9F */
        if (u[0] == 0xE3 && u[1] == 0x80 && u[2] == 0x82) return true;
        if (u[0] == 0xEF && u[1] == 0xBC && u[2] == 0x81) return true;
        if (u[0] == 0xEF && u[1] == 0xBC && u[2] == 0x9F) return true;
    }
    return false;
}

static const char *HANGING_WORDS[] = {
    "and", "but", "so", "or", "because", "if", "when", "while",
    "though", "however", "um", "uh", "like", "you", "the", "a", "an",
};
static const size_t HANGING_COUNT = sizeof(HANGING_WORDS) / sizeof(HANGING_WORDS[0]);

/* lowercase ASCII compare of a token against a known-lowercase word. */
static bool eq_ci_ascii(const char *tok, size_t tlen, const char *low) {
    size_t ll = strlen(low);
    if (tlen != ll) return false;
    for (size_t i = 0; i < tlen; ++i)
        if (tolower((unsigned char)tok[i]) != low[i]) return false;
    return true;
}
static bool ends_hanging(const char *t, size_t len) {
    /* last whitespace-delimited word, trimmed of trailing . , ! ? */
    size_t end = len;
    /* find start of last token */
    size_t i = len;
    while (i > 0 && (t[i-1] == ' ' || t[i-1] == '\t' || t[i-1] == '\n')) i--;
    end = i;
    size_t start = end;
    while (start > 0 && !(t[start-1] == ' ' || t[start-1] == '\t' || t[start-1] == '\n'))
        start--;
    /* TrimEnd('.', ',', '!', '?') */
    size_t we = end;
    while (we > start) {
        char c = t[we-1];
        if (c == '.' || c == ',' || c == '!' || c == '?') we--;
        else break;
    }
    if (we <= start) return false;
    for (size_t k = 0; k < HANGING_COUNT; ++k)
        if (eq_ci_ascii(t + start, we - start, HANGING_WORDS[k])) return true;
    return false;
}

/* trim (leading + trailing) whitespace, returning bounds into the original. */
static void trim_bounds(const char *s, size_t *out_start, size_t *out_len) {
    size_t len = s ? strlen(s) : 0;
    size_t a = 0, b = len;
    while (a < b && isspace((unsigned char)s[a])) a++;
    while (b > a && isspace((unsigned char)s[b-1])) b--;
    *out_start = a;
    *out_len = b - a;
}

static int64_t imax64(int64_t a, int64_t b) { return a > b ? a : b; }

static int ruleeot_predict(void *self, const char *partial, int64_t silence,
                           ca_end_of_turn_result_t *out) {
    ca_rule_eot_detector_t *d = (ca_rule_eot_detector_t *)self;
    if (!d || !out) return -1;

    size_t ts, tl;
    trim_bounds(partial, &ts, &tl);
    const char *text = partial ? partial + ts : "";

    if (silence >= d->max_silence_ms) {
        out->is_complete = true; out->confidence = 0.7f; out->wait_more_ms = 0;
        return 0;
    }
    if (tl == 0) {
        out->is_complete = false; out->confidence = 0.2f;
        out->wait_more_ms = (int)imax64(150, d->min_silence_ms - silence);
        return 0;
    }
    bool term = ends_terminal(text, tl);
    bool hang = ends_hanging(text, tl);

    if (hang) {
        int64_t remaining = d->hanging_silence_ms - silence;
        if (remaining <= 0) {
            out->is_complete = true; out->confidence = 0.6f; out->wait_more_ms = 0;
            return 0;
        }
        out->is_complete = false; out->confidence = 0.4f;
        out->wait_more_ms = (int)remaining;  /* Math.Ceiling of integer ms == ms */
        return 0;
    }
    if (term && silence >= d->min_silence_ms) {
        out->is_complete = true; out->confidence = 0.9f; out->wait_more_ms = 0;
        return 0;
    }
    if (silence >= d->min_silence_ms) {
        out->is_complete = true; out->confidence = 0.75f; out->wait_more_ms = 0;
        return 0;
    }
    out->is_complete = false; out->confidence = 0.6f;
    out->wait_more_ms = (int)imax64(50, d->min_silence_ms - silence);
    return 0;
}
static void ruleeot_reset(void *self) { (void)self; }
ca_end_of_turn_detector_t ca_rule_eot_detector_as_detector(ca_rule_eot_detector_t *d) {
    ca_end_of_turn_detector_t v;
    v.self = d;
    v.backend_id = ruleeot_backend_id;
    v.predict = ruleeot_predict;
    v.reset = ruleeot_reset;
    return v;
}

struct ca_smart_turn_detector {
    bool                     has_runner;
    ca_turn_model_runner_t   runner;
    ca_rule_eot_detector_t  *fallback;
    float                    threshold;
    char                    *backend_id; /* owned */
};
ca_smart_turn_detector_t *ca_smart_turn_detector_create(bool has_runner,
                                                        ca_turn_model_runner_t runner,
                                                        float threshold) {
    ca_smart_turn_detector_t *d = (ca_smart_turn_detector_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->has_runner = has_runner;
    d->runner = runner;
    d->fallback = ca_rule_eot_detector_create(0, 0, 0);
    d->threshold = threshold;
    d->backend_id = sp_strdup(has_runner ? "smart-turn-v2" : "smart-turn (fallback)");
    if (!d->fallback || !d->backend_id) {
        ca_rule_eot_detector_destroy(d->fallback);
        free(d->backend_id);
        free(d);
        return NULL;
    }
    return d;
}
void ca_smart_turn_detector_destroy(ca_smart_turn_detector_t *d) {
    if (!d) return;
    ca_rule_eot_detector_destroy(d->fallback);
    free(d->backend_id);
    free(d);
}
static const char *smart_backend_id(void *self) {
    ca_smart_turn_detector_t *d = (ca_smart_turn_detector_t *)self;
    return d ? d->backend_id : NULL;
}
static int smart_predict(void *self, const char *partial, int64_t silence,
                         ca_end_of_turn_result_t *out) {
    ca_smart_turn_detector_t *d = (ca_smart_turn_detector_t *)self;
    if (!d || !out) return -1;
    if (!d->has_runner || !d->runner.score_completion)
        return ruleeot_predict(d->fallback, partial, silence, out);

    float prob = d->runner.score_completion(d->runner.self, partial, silence);
    if (prob < 0.0f) prob = 0.0f;
    if (prob > 1.0f) prob = 1.0f;
    if (prob >= d->threshold) {
        out->is_complete = true; out->confidence = prob; out->wait_more_ms = 0;
        return 0;
    }
    out->is_complete = false; out->confidence = prob;
    out->wait_more_ms = (int)lround((1.0f - prob) * 1000.0f);
    return 0;
}
static void smart_reset(void *self) {
    ca_smart_turn_detector_t *d = (ca_smart_turn_detector_t *)self;
    if (d) ruleeot_reset(d->fallback);
}
ca_end_of_turn_detector_t ca_smart_turn_detector_as_detector(ca_smart_turn_detector_t *d) {
    ca_end_of_turn_detector_t v;
    v.self = d;
    v.backend_id = smart_backend_id;
    v.predict = smart_predict;
    v.reset = smart_reset;
    return v;
}

/* ===========================================================================
 * IVoiceActivityDetector (per-frame)
 * =========================================================================== */

struct ca_null_speech_vad { int _; };
ca_null_speech_vad_t *ca_null_speech_vad_create(void) {
    return (ca_null_speech_vad_t *)calloc(1, sizeof(ca_null_speech_vad_t));
}
void ca_null_speech_vad_destroy(ca_null_speech_vad_t *v) { free(v); }
static const char *nullvad_backend_id(void *self) { (void)self; return "null"; }
static float nullvad_threshold(void *self) { (void)self; return 0.5f; }
static int nullvad_classify(void *self, const uint8_t *audio, size_t len, int rate,
                            int64_t offset, ca_vad_frame_result_t *out) {
    (void)self; (void)audio; (void)len; (void)rate;
    if (!out) return -1;
    out->is_speech = true;
    out->speech_probability = 1.0f;
    out->offset_ms = offset;
    return 0;
}
static void nullvad_reset(void *self) { (void)self; }
ca_speech_vad_t ca_null_speech_vad_as_vad(ca_null_speech_vad_t *v) {
    ca_speech_vad_t r;
    r.self = v;
    r.backend_id = nullvad_backend_id;
    r.speech_threshold = nullvad_threshold;
    r.classify = nullvad_classify;
    r.reset = nullvad_reset;
    return r;
}

struct ca_energy_speech_vad {
    float speech_threshold;
    float energy_threshold;
    int   hangover_frames;
    int   hangover_remaining;
};
ca_energy_speech_vad_t *ca_energy_speech_vad_create(float speech_threshold,
                                                    float energy_threshold,
                                                    int hangover_frames) {
    ca_energy_speech_vad_t *v = (ca_energy_speech_vad_t *)calloc(1, sizeof(*v));
    if (!v) return NULL;
    v->speech_threshold = speech_threshold;
    v->energy_threshold = energy_threshold;
    v->hangover_frames = hangover_frames;
    return v;
}
void ca_energy_speech_vad_destroy(ca_energy_speech_vad_t *v) { free(v); }
static const char *energyvad_backend_id(void *self) { (void)self; return "energy"; }
static float energyvad_threshold(void *self) {
    ca_energy_speech_vad_t *v = (ca_energy_speech_vad_t *)self;
    return v ? v->speech_threshold : 0.0f;
}
/* Core scoring shared with Silero fallback: fills *out. */
static void energy_classify_core(ca_energy_speech_vad_t *v, const uint8_t *audio,
                                 size_t len, int64_t offset,
                                 ca_vad_frame_result_t *out) {
    if (len < 2) {
        out->is_speech = false; out->speech_probability = 0.0f; out->offset_ms = offset;
        return;
    }
    size_t n = len / 2;
    double sum_sq = 0.0;
    int zc = 0;
    int prev = 0;
    for (size_t i = 0; i < n; ++i) {
        int s = rd_i16le(audio + i * 2);
        sum_sq += (double)s * (double)s;
        if (i > 0) {
            int ss = (s > 0) - (s < 0);
            int ps = (prev > 0) - (prev < 0);
            if (ss != ps && s != 0 && prev != 0) zc++;
        }
        prev = s;
    }
    double rms = sqrt(sum_sq / (double)n) / 32767.0;
    float zcr = (float)zc / (float)n;

    bool energy_good = rms >= v->energy_threshold;
    bool zcr_good = zcr >= 0.02f && zcr <= 0.30f;
    float raw = energy_good ? (zcr_good ? 0.85f : 0.6f) : 0.1f;

    bool is_speech;
    if (raw >= v->speech_threshold) {
        is_speech = true;
        v->hangover_remaining = v->hangover_frames;
    } else if (v->hangover_remaining > 0) {
        is_speech = true;
        v->hangover_remaining--;
        if (raw < v->speech_threshold) raw = v->speech_threshold; /* Math.Max */
    } else {
        is_speech = false;
    }
    out->is_speech = is_speech;
    out->speech_probability = raw;
    out->offset_ms = offset;
}
static int energyvad_classify(void *self, const uint8_t *audio, size_t len, int rate,
                              int64_t offset, ca_vad_frame_result_t *out) {
    ca_energy_speech_vad_t *v = (ca_energy_speech_vad_t *)self;
    (void)rate;
    if (!v || !out) return -1;
    energy_classify_core(v, audio, len, offset, out);
    return 0;
}
static void energyvad_reset(void *self) {
    ca_energy_speech_vad_t *v = (ca_energy_speech_vad_t *)self;
    if (v) v->hangover_remaining = 0;
}
ca_speech_vad_t ca_energy_speech_vad_as_vad(ca_energy_speech_vad_t *v) {
    ca_speech_vad_t r;
    r.self = v;
    r.backend_id = energyvad_backend_id;
    r.speech_threshold = energyvad_threshold;
    r.classify = energyvad_classify;
    r.reset = energyvad_reset;
    return r;
}

struct ca_silero_speech_vad {
    bool                    has_runner;
    ca_vad_model_runner_t   runner;
    ca_energy_speech_vad_t *fallback;
    float                   speech_threshold;
    int                     hangover_frames;
    int                     hangover_remaining;
    char                   *backend_id; /* owned */
};
ca_silero_speech_vad_t *ca_silero_speech_vad_create(bool has_runner,
                                                    ca_vad_model_runner_t runner,
                                                    float speech_threshold,
                                                    int hangover_frames) {
    ca_silero_speech_vad_t *v = (ca_silero_speech_vad_t *)calloc(1, sizeof(*v));
    if (!v) return NULL;
    v->has_runner = has_runner;
    v->runner = runner;
    /* C# fallback ctor: new EnergyVoiceActivityDetector(speechThreshold). */
    v->fallback = ca_energy_speech_vad_create(speech_threshold, 0.012f, 8);
    v->speech_threshold = speech_threshold;
    v->hangover_frames = hangover_frames;
    v->backend_id = sp_strdup(has_runner ? "silero" : "silero (fallback)");
    if (!v->fallback || !v->backend_id) {
        ca_energy_speech_vad_destroy(v->fallback);
        free(v->backend_id);
        free(v);
        return NULL;
    }
    return v;
}
void ca_silero_speech_vad_destroy(ca_silero_speech_vad_t *v) {
    if (!v) return;
    ca_energy_speech_vad_destroy(v->fallback);
    free(v->backend_id);
    free(v);
}
static const char *silerovad_backend_id(void *self) {
    ca_silero_speech_vad_t *v = (ca_silero_speech_vad_t *)self;
    return v ? v->backend_id : NULL;
}
static float silerovad_threshold(void *self) {
    ca_silero_speech_vad_t *v = (ca_silero_speech_vad_t *)self;
    return v ? v->speech_threshold : 0.0f;
}
static int silerovad_classify(void *self, const uint8_t *audio, size_t len, int rate,
                              int64_t offset, ca_vad_frame_result_t *out) {
    ca_silero_speech_vad_t *v = (ca_silero_speech_vad_t *)self;
    if (!v || !out) return -1;
    if (!v->has_runner || !v->runner.score_frame)
        return energyvad_classify(v->fallback, audio, len, rate, offset, out);

    float prob = v->runner.score_frame(v->runner.self, audio, len, rate);
    bool is_speech;
    if (prob >= v->speech_threshold) {
        is_speech = true;
        v->hangover_remaining = v->hangover_frames;
    } else if (v->hangover_remaining > 0) {
        is_speech = true;
        v->hangover_remaining--;
    } else {
        is_speech = false;
    }
    out->is_speech = is_speech;
    out->speech_probability = prob;
    out->offset_ms = offset;
    return 0;
}
static void silerovad_reset(void *self) {
    ca_silero_speech_vad_t *v = (ca_silero_speech_vad_t *)self;
    if (!v) return;
    v->hangover_remaining = 0;
    energyvad_reset(v->fallback);
}
ca_speech_vad_t ca_silero_speech_vad_as_vad(ca_silero_speech_vad_t *v) {
    ca_speech_vad_t r;
    r.self = v;
    r.backend_id = silerovad_backend_id;
    r.speech_threshold = silerovad_threshold;
    r.classify = silerovad_classify;
    r.reset = silerovad_reset;
    return r;
}

/* ===========================================================================
 * AudioFormatConverter — G.711 mu-law / a-law + linear resample (byte-exact)
 * =========================================================================== */

static int16_t mulaw_to_linear(uint8_t mu) {
    mu = (uint8_t)~mu;
    int sign = mu & 0x80;
    int exponent = (mu >> 4) & 0x07;
    int mantissa = mu & 0x0F;
    int magnitude = ((mantissa << 3) + 0x84) << exponent;
    int sample = magnitude - 0x84;
    return (int16_t)(sign != 0 ? -sample : sample);
}
static uint8_t linear_to_mulaw(int16_t pcm) {
    const int Bias = 0x84;
    const int Clip = 32635;
    int sign = (pcm >> 8) & 0x80;
    int v = pcm;
    if (sign != 0) v = -v;
    if (v > Clip) v = Clip;
    v += Bias;
    int exponent;
    if      (v >= 0x4000) exponent = 7;
    else if (v >= 0x2000) exponent = 6;
    else if (v >= 0x1000) exponent = 5;
    else if (v >= 0x0800) exponent = 4;
    else if (v >= 0x0400) exponent = 3;
    else if (v >= 0x0200) exponent = 2;
    else if (v >= 0x0100) exponent = 1;
    else                  exponent = 0;
    int mantissa = (v >> (exponent + 3)) & 0x0F;
    return (uint8_t)(~(sign | (exponent << 4) | mantissa));
}
static int16_t alaw_to_linear(uint8_t a) {
    a ^= 0x55;
    int sign = a & 0x80;
    int exponent = (a >> 4) & 0x07;
    int mantissa = a & 0x0F;
    int magnitude;
    if (exponent != 0) magnitude = ((mantissa << 4) + 0x108) << (exponent - 1);
    else               magnitude = (mantissa << 4) + 0x08;
    return (int16_t)(sign != 0 ? -magnitude : magnitude);
}
static uint8_t linear_to_alaw(int16_t pcm) {
    int sign = (pcm >> 8) & 0x80;
    int v = pcm;
    if (sign != 0) v = -v;
    if (v > 0x7FFF) v = 0x7FFF;
    int exponent, mantissa;
    if (v < 256) {
        exponent = 0;
        mantissa = v >> 4;
    } else {
        if      (v >= 0x4000) exponent = 7;
        else if (v >= 0x2000) exponent = 6;
        else if (v >= 0x1000) exponent = 5;
        else if (v >= 0x0800) exponent = 4;
        else if (v >= 0x0400) exponent = 3;
        else if (v >= 0x0200) exponent = 2;
        else                  exponent = 1;
        mantissa = (v >> (exponent + 3)) & 0x0F;
    }
    return (uint8_t)((sign | (exponent << 4) | mantissa) ^ 0x55);
}

uint8_t *ca_audio_mulaw_to_pcm16(const uint8_t *mulaw, size_t len, size_t *out_len) {
    if (len == 0) { if (out_len) *out_len = 0; return NULL; }
    uint8_t *pcm = (uint8_t *)malloc(len * 2);
    if (!pcm) { if (out_len) *out_len = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < len; ++i) wr_i16le(pcm + i * 2, mulaw_to_linear(mulaw[i]));
    if (out_len) *out_len = len * 2;
    return pcm;
}
uint8_t *ca_audio_pcm16_to_mulaw(const uint8_t *pcm, size_t len, size_t *out_len) {
    size_t samples = len / 2;
    if (samples == 0) { if (out_len) *out_len = 0; return NULL; }
    uint8_t *mu = (uint8_t *)malloc(samples);
    if (!mu) { if (out_len) *out_len = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < samples; ++i) mu[i] = linear_to_mulaw(rd_i16le(pcm + i * 2));
    if (out_len) *out_len = samples;
    return mu;
}
uint8_t *ca_audio_alaw_to_pcm16(const uint8_t *alaw, size_t len, size_t *out_len) {
    if (len == 0) { if (out_len) *out_len = 0; return NULL; }
    uint8_t *pcm = (uint8_t *)malloc(len * 2);
    if (!pcm) { if (out_len) *out_len = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < len; ++i) wr_i16le(pcm + i * 2, alaw_to_linear(alaw[i]));
    if (out_len) *out_len = len * 2;
    return pcm;
}
uint8_t *ca_audio_pcm16_to_alaw(const uint8_t *pcm, size_t len, size_t *out_len) {
    size_t samples = len / 2;
    if (samples == 0) { if (out_len) *out_len = 0; return NULL; }
    uint8_t *al = (uint8_t *)malloc(samples);
    if (!al) { if (out_len) *out_len = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < samples; ++i) al[i] = linear_to_alaw(rd_i16le(pcm + i * 2));
    if (out_len) *out_len = samples;
    return al;
}
uint8_t *ca_audio_resample_pcm16_linear(const uint8_t *pcm, size_t len,
                                        int from_hz, int to_hz, size_t *out_len) {
    if (from_hz == to_hz) {
        /* return a copy (C# returns the same array; we return an owned copy) */
        if (len == 0) { if (out_len) *out_len = 0; return NULL; }
        uint8_t *cpy = (uint8_t *)malloc(len);
        if (!cpy) { if (out_len) *out_len = SIZE_MAX; return NULL; }
        memcpy(cpy, pcm, len);
        if (out_len) *out_len = len;
        return cpy;
    }
    size_t src_samples = len / 2;
    size_t dst_samples = (size_t)((long long)src_samples * to_hz / from_hz);
    if (dst_samples == 0) { if (out_len) *out_len = 0; return NULL; }
    uint8_t *dst = (uint8_t *)malloc(dst_samples * 2);
    if (!dst) { if (out_len) *out_len = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < dst_samples; ++i) {
        double src_idx = (double)i * from_hz / to_hz;
        long idx0 = (long)floor(src_idx);
        long idx1 = idx0 + 1;
        if (idx1 > (long)src_samples - 1) idx1 = (long)src_samples - 1;
        double frac = src_idx - (double)idx0;
        int16_t s0 = rd_i16le(pcm + idx0 * 2);
        int16_t s1 = rd_i16le(pcm + idx1 * 2);
        int16_t s = (int16_t)(s0 + (int)((s1 - s0) * frac));
        wr_i16le(dst + i * 2, s);
    }
    if (out_len) *out_len = dst_samples * 2;
    return dst;
}

uint8_t *ca_audio_convert(const uint8_t *input, size_t input_len,
                          ca_audio_codec_t input_codec, int input_sample_rate_hz,
                          ca_audio_codec_t output_codec, int output_sample_rate_hz,
                          size_t *out_len) {
    if (out_len) *out_len = 0;
    if (input_sample_rate_hz <= 0 || output_sample_rate_hz <= 0) {
        if (out_len) *out_len = SIZE_MAX;
        return NULL;
    }

    /* 1) decode source to PCM-16 (owned) */
    uint8_t *pcm_in = NULL;
    size_t pcm_in_len = 0;
    switch (input_codec) {
        case CA_AUDIO_CODEC_PCM16:
            if (input_len) {
                pcm_in = (uint8_t *)malloc(input_len);
                if (!pcm_in) { if (out_len) *out_len = SIZE_MAX; return NULL; }
                memcpy(pcm_in, input, input_len);
                pcm_in_len = input_len;
            }
            break;
        case CA_AUDIO_CODEC_MULAW:
            pcm_in = ca_audio_mulaw_to_pcm16(input, input_len, &pcm_in_len);
            if (pcm_in_len == SIZE_MAX) { if (out_len) *out_len = SIZE_MAX; return NULL; }
            break;
        case CA_AUDIO_CODEC_ALAW:
            pcm_in = ca_audio_alaw_to_pcm16(input, input_len, &pcm_in_len);
            if (pcm_in_len == SIZE_MAX) { if (out_len) *out_len = SIZE_MAX; return NULL; }
            break;
        default:
            if (out_len) *out_len = SIZE_MAX;
            return NULL;
    }

    /* 2) resample if needed (owned) */
    uint8_t *pcm_rs = pcm_in;
    size_t   pcm_rs_len = pcm_in_len;
    if (input_sample_rate_hz != output_sample_rate_hz) {
        size_t rl = 0;
        uint8_t *rs = ca_audio_resample_pcm16_linear(pcm_in, pcm_in_len,
                                                     input_sample_rate_hz,
                                                     output_sample_rate_hz, &rl);
        if (rl == SIZE_MAX) { free(pcm_in); if (out_len) *out_len = SIZE_MAX; return NULL; }
        free(pcm_in);
        pcm_rs = rs;
        pcm_rs_len = rl;
    }

    /* 3) encode to target */
    uint8_t *result = NULL;
    size_t   result_len = 0;
    switch (output_codec) {
        case CA_AUDIO_CODEC_PCM16:
            result = pcm_rs;   /* transfer ownership */
            result_len = pcm_rs_len;
            pcm_rs = NULL;
            break;
        case CA_AUDIO_CODEC_MULAW:
            result = ca_audio_pcm16_to_mulaw(pcm_rs, pcm_rs_len, &result_len);
            if (result_len == SIZE_MAX) { free(pcm_rs); if (out_len) *out_len = SIZE_MAX; return NULL; }
            free(pcm_rs); pcm_rs = NULL;
            break;
        case CA_AUDIO_CODEC_ALAW:
            result = ca_audio_pcm16_to_alaw(pcm_rs, pcm_rs_len, &result_len);
            if (result_len == SIZE_MAX) { free(pcm_rs); if (out_len) *out_len = SIZE_MAX; return NULL; }
            free(pcm_rs); pcm_rs = NULL;
            break;
        default:
            free(pcm_rs);
            if (out_len) *out_len = SIZE_MAX;
            return NULL;
    }
    if (out_len) *out_len = result_len;
    return result;
}
