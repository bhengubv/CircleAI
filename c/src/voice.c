/*
 * voice.c — CircleAI.Voice (C11 port).
 *
 * Ports AudioFormat, IAudioCapture (+Null/scripted), IVoiceActivityDetector
 * (+Null/EnergyVadDetector), IVoiceTranscriber (+Null/keyword), IWakeWordDetector
 * (+Null/EnergyWakeWordDetector), ITtsEngine (+Null/template),
 * ISpeechEmotionDetector (deterministic over an injected logits runner +
 * Russell-circumplex mapping), ISpeakerIdentity (cosine-centroid enroll/identify
 * over an injected embedder), and VoicePipeline.
 *
 * The EnergyVadDetector framing loop and the emotion softmax / speaker-centroid
 * arithmetic reproduce the reference C# exactly. PCM-16 little-endian throughout.
 *
 * Pure C11 + libc + libm.
 */

#include "circle_ai/voice.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <ctype.h>

/* ── shared helpers ─────────────────────────────────────────────────────── */

static char *vc_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *vc_strdup_empty(const char *s) { return vc_strdup(s ? s : ""); }

static int16_t rd_i16le(const uint8_t *p) {
    return (int16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}
static void wr_i16le(uint8_t *p, int16_t v) {
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)((v >> 8) & 0xFF);
}

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

/* ── AudioFormat ────────────────────────────────────────────────────────── */

ca_voice_audio_format_t ca_voice_audio_format_pcm16_mono16k(void) {
    ca_voice_audio_format_t f = { 16000, 1, 16 };
    return f;
}

/* ── record frees ───────────────────────────────────────────────────────── */

void ca_voice_transcription_result_free(ca_voice_transcription_result_t *r) {
    if (!r) return;
    free(r->text);
    free(r->language_code);
    r->text = r->language_code = NULL;
}
void ca_voice_partial_transcription_free(ca_voice_partial_transcription_t *p) {
    if (!p) return;
    free(p->text);
    p->text = NULL;
}
void ca_voice_partial_transcription_free_array(ca_voice_partial_transcription_t *arr,
                                               size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_voice_partial_transcription_free(&arr[i]);
    free(arr);
}
void ca_voice_vad_segment_free(ca_voice_vad_segment_t *s) {
    if (!s) return;
    free(s->audio);
    s->audio = NULL;
    s->audio_len = 0;
}
void ca_voice_vad_segment_free_array(ca_voice_vad_segment_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_voice_vad_segment_free(&arr[i]);
    free(arr);
}
void ca_voice_tts_result_free(ca_voice_tts_result_t *r) {
    if (!r) return;
    free(r->audio_data);
    r->audio_data = NULL;
    r->audio_len = 0;
}
void ca_voice_wake_event_free(ca_voice_wake_event_t *e) {
    if (!e) return;
    free(e->wake_word);
    e->wake_word = NULL;
}
void ca_speech_emotion_frame_free(ca_speech_emotion_frame_t *f) {
    if (!f) return;
    free(f->label);
    f->label = NULL;
}

/* ===========================================================================
 * IAudioCapture
 * =========================================================================== */

typedef struct {
    uint8_t *data;
    size_t   len;
} chunk_t;

struct ca_audio_capture {
    ca_voice_audio_format_t format;
    bool                    is_null;
    chunk_t                *chunks;
    size_t                  count, cap;
    size_t                  cursor;
};

ca_audio_capture_t *ca_null_audio_capture_create(void) {
    ca_audio_capture_t *c = (ca_audio_capture_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->format = ca_voice_audio_format_pcm16_mono16k();
    c->is_null = true;
    return c;
}
ca_audio_capture_t *ca_scripted_audio_capture_create(ca_voice_audio_format_t fmt) {
    ca_audio_capture_t *c = (ca_audio_capture_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->format = fmt;
    c->is_null = false;
    return c;
}
void ca_audio_capture_destroy(ca_audio_capture_t *c) {
    if (!c) return;
    for (size_t i = 0; i < c->count; ++i) free(c->chunks[i].data);
    free(c->chunks);
    free(c);
}
ca_voice_audio_format_t ca_audio_capture_format(const ca_audio_capture_t *c) {
    if (c) return c->format;
    return ca_voice_audio_format_pcm16_mono16k();
}
int ca_scripted_audio_capture_push(ca_audio_capture_t *c, const uint8_t *data, size_t len) {
    if (!c || c->is_null) return -1;
    if (c->count == c->cap) {
        size_t nc = c->cap ? c->cap * 2 : 4;
        void *n = realloc(c->chunks, nc * sizeof(*c->chunks));
        if (!n) return -1;
        c->chunks = (chunk_t *)n;
        c->cap = nc;
    }
    uint8_t *cpy = NULL;
    if (len) {
        cpy = (uint8_t *)malloc(len);
        if (!cpy) return -1;
        memcpy(cpy, data, len);
    }
    c->chunks[c->count].data = cpy;
    c->chunks[c->count].len = len;
    c->count++;
    return 0;
}
bool ca_audio_capture_next(ca_audio_capture_t *c, uint8_t **out_data, size_t *out_len) {
    if (!c || !out_data || !out_len) return false;
    if (c->is_null || c->cursor >= c->count) { *out_data = NULL; *out_len = 0; return false; }
    chunk_t *ch = &c->chunks[c->cursor++];
    if (ch->len) {
        uint8_t *cpy = (uint8_t *)malloc(ch->len);
        if (!cpy) { *out_data = NULL; *out_len = 0; return false; }
        memcpy(cpy, ch->data, ch->len);
        *out_data = cpy;
    } else {
        *out_data = NULL;
    }
    *out_len = ch->len;
    return true;
}
void ca_audio_capture_reset(ca_audio_capture_t *c) {
    if (c) c->cursor = 0;
}

/* ── generic growable VadSegment list ───────────────────────────────────── */

typedef struct {
    ca_voice_vad_segment_t *items;
    size_t                  count, cap;
} seg_list_t;

static bool seg_list_push(seg_list_t *l, const uint8_t *data, size_t len, bool is_speech) {
    if (l->count == l->cap) {
        size_t nc = l->cap ? l->cap * 2 : 4;
        void *n = realloc(l->items, nc * sizeof(*l->items));
        if (!n) return false;
        l->items = (ca_voice_vad_segment_t *)n;
        l->cap = nc;
    }
    uint8_t *cpy = NULL;
    if (len) {
        cpy = (uint8_t *)malloc(len);
        if (!cpy) return false;
        memcpy(cpy, data, len);
    }
    l->items[l->count].audio = cpy;
    l->items[l->count].audio_len = len;
    l->items[l->count].is_speech = is_speech;
    l->count++;
    return true;
}

/* ===========================================================================
 * IVoiceActivityDetector (stream)
 * =========================================================================== */

struct ca_null_voice_vad_stream { int _; };
ca_null_voice_vad_stream_t *ca_null_voice_vad_stream_create(void) {
    return (ca_null_voice_vad_stream_t *)calloc(1, sizeof(ca_null_voice_vad_stream_t));
}
void ca_null_voice_vad_stream_destroy(ca_null_voice_vad_stream_t *v) { free(v); }

static ca_voice_vad_segment_t *nullvad_detect(void *self, ca_audio_capture_t *cap,
                                              size_t *out_count) {
    (void)self;
    if (out_count) *out_count = 0;
    if (!cap) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    seg_list_t l; memset(&l, 0, sizeof(l));
    uint8_t *data; size_t len;
    while (ca_audio_capture_next(cap, &data, &len)) {
        bool ok = seg_list_push(&l, data, len, true);
        free(data);
        if (!ok) { ca_voice_vad_segment_free_array(l.items, l.count); if (out_count) *out_count = SIZE_MAX; return NULL; }
    }
    if (l.count == 0) return NULL;
    if (out_count) *out_count = l.count;
    return l.items;
}
ca_voice_vad_stream_t ca_null_voice_vad_stream_as_stream(ca_null_voice_vad_stream_t *v) {
    ca_voice_vad_stream_t s;
    s.self = v;
    s.detect = nullvad_detect;
    return s;
}

struct ca_energy_vad_stream {
    float energy_threshold;
    int   silence_frames;
    int   frame_size_bytes;
};
ca_energy_vad_stream_t *ca_energy_vad_stream_create(float energy_threshold,
                                                    int silence_frames,
                                                    int frame_size_bytes) {
    if (silence_frames <= 0 || frame_size_bytes <= 0 || energy_threshold < 0.0f)
        return NULL;
    ca_energy_vad_stream_t *v = (ca_energy_vad_stream_t *)calloc(1, sizeof(*v));
    if (!v) return NULL;
    v->energy_threshold = energy_threshold;
    v->silence_frames = silence_frames;
    v->frame_size_bytes = frame_size_bytes;
    return v;
}
void ca_energy_vad_stream_destroy(ca_energy_vad_stream_t *v) { free(v); }

/* RMS energy of a PCM-16 frame normalised to [0,1] (matches C# ComputeRmsEnergy:
 * divide by 32768.0). */
static float rms_energy(const uint8_t *frame, size_t len) {
    size_t n = len / 2;
    if (n == 0) return 0.0f;
    double sum = 0.0;
    for (size_t i = 0; i < n; ++i) {
        double norm = rd_i16le(frame + i * 2) / 32768.0;
        sum += norm * norm;
    }
    return (float)sqrt(sum / (double)n);
}

/* A growable byte buffer for residual + speech accumulation (MemoryStream). */
typedef struct { uint8_t *b; size_t len, cap; } bytebuf_t;
static bool bb_append(bytebuf_t *q, const uint8_t *data, size_t len) {
    if (len == 0) return true;
    if (q->len + len > q->cap) {
        size_t nc = q->cap ? q->cap : 64;
        while (nc < q->len + len) nc *= 2;
        void *n = realloc(q->b, nc);
        if (!n) return false;
        q->b = (uint8_t *)n;
        q->cap = nc;
    }
    memcpy(q->b + q->len, data, len);
    q->len += len;
    return true;
}
static void bb_free(bytebuf_t *q) { free(q->b); q->b = NULL; q->len = q->cap = 0; }

static ca_voice_vad_segment_t *energyvad_detect(void *self, ca_audio_capture_t *cap,
                                                size_t *out_count) {
    ca_energy_vad_stream_t *v = (ca_energy_vad_stream_t *)self;
    if (out_count) *out_count = 0;
    if (!v || !cap) { if (out_count) *out_count = SIZE_MAX; return NULL; }

    seg_list_t out; memset(&out, 0, sizeof(out));
    bytebuf_t residual; memset(&residual, 0, sizeof(residual));
    bytebuf_t speech;   memset(&speech, 0, sizeof(speech));
    bool in_speech = false;
    int consec_silence = 0;
    int frame = v->frame_size_bytes;
    bool oom = false;

    uint8_t *data; size_t len;
    while (ca_audio_capture_next(cap, &data, &len)) {
        if (len == 0) { free(data); continue; }
        if (!bb_append(&residual, data, len)) { free(data); oom = true; break; }
        free(data);

        size_t offset = 0;
        while (residual.len - offset >= (size_t)frame) {
            const uint8_t *fr = residual.b + offset;
            float rms = rms_energy(fr, (size_t)frame);
            bool is_speech_frame = rms >= v->energy_threshold;

            if (is_speech_frame) {
                if (!in_speech) {
                    in_speech = true;
                    consec_silence = 0;
                    speech.len = 0;
                } else {
                    consec_silence = 0;
                }
                if (!bb_append(&speech, fr, (size_t)frame)) { oom = true; break; }
            } else if (in_speech) {
                if (!bb_append(&speech, fr, (size_t)frame)) { oom = true; break; }
                consec_silence++;
                if (consec_silence >= v->silence_frames) {
                    in_speech = false;
                    consec_silence = 0;
                    if (!seg_list_push(&out, speech.b, speech.len, true)) { oom = true; break; }
                    speech.len = 0;
                }
            }
            offset += (size_t)frame;
        }
        if (oom) break;

        /* Move unconsumed residual to the front. */
        size_t remaining = residual.len - offset;
        if (remaining > 0 && offset > 0)
            memmove(residual.b, residual.b + offset, remaining);
        residual.len = remaining;
    }

    if (!oom && in_speech && speech.len > 0) {
        if (!seg_list_push(&out, speech.b, speech.len, true)) oom = true;
    }

    bb_free(&residual);
    bb_free(&speech);

    if (oom) {
        ca_voice_vad_segment_free_array(out.items, out.count);
        if (out_count) *out_count = SIZE_MAX;
        return NULL;
    }
    if (out.count == 0) return NULL;
    if (out_count) *out_count = out.count;
    return out.items;
}
ca_voice_vad_stream_t ca_energy_vad_stream_as_stream(ca_energy_vad_stream_t *v) {
    ca_voice_vad_stream_t s;
    s.self = v;
    s.detect = energyvad_detect;
    return s;
}

/* ===========================================================================
 * IVoiceTranscriber
 * =========================================================================== */

struct ca_null_voice_transcriber { int _; };
ca_null_voice_transcriber_t *ca_null_voice_transcriber_create(void) {
    return (ca_null_voice_transcriber_t *)calloc(1, sizeof(ca_null_voice_transcriber_t));
}
void ca_null_voice_transcriber_destroy(ca_null_voice_transcriber_t *t) { free(t); }

static int nulltr_transcribe(void *self, const uint8_t *pcm, size_t len,
                             ca_voice_transcription_result_t *out) {
    (void)self; (void)pcm; (void)len;
    if (!out) return -1;
    out->text = vc_strdup_empty(NULL);
    out->confidence = 0.0f;
    out->language_code = vc_strdup("und");
    return 0;
}
static ca_voice_partial_transcription_t *nulltr_stream(void *self,
                                                       ca_audio_capture_t *chunks,
                                                       size_t *out_count) {
    (void)self;
    if (out_count) *out_count = 0;
    if (!chunks) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    /* Drain but emit nothing. */
    uint8_t *data; size_t len;
    while (ca_audio_capture_next(chunks, &data, &len)) free(data);
    return NULL;
}
ca_voice_transcriber_t ca_null_voice_transcriber_as_transcriber(
    ca_null_voice_transcriber_t *t) {
    ca_voice_transcriber_t v;
    v.self = t;
    v.transcribe = nulltr_transcribe;
    v.stream_transcribe = nulltr_stream;
    return v;
}

struct ca_keyword_voice_transcriber {
    size_t min_samples;
    char  *phrase;    /* owned */
    float  confidence;
    char  *language;  /* owned */
};
ca_keyword_voice_transcriber_t *ca_keyword_voice_transcriber_create(
    size_t min_samples, const char *phrase, float confidence, const char *language) {
    ca_keyword_voice_transcriber_t *t = (ca_keyword_voice_transcriber_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->min_samples = min_samples;
    t->phrase = vc_strdup(phrase ? phrase : "");
    t->confidence = confidence;
    t->language = vc_strdup(language ? language : "und");
    if (!t->phrase || !t->language) {
        free(t->phrase); free(t->language); free(t);
        return NULL;
    }
    return t;
}
void ca_keyword_voice_transcriber_destroy(ca_keyword_voice_transcriber_t *t) {
    if (!t) return;
    free(t->phrase);
    free(t->language);
    free(t);
}
static int kwtr_transcribe(void *self, const uint8_t *pcm, size_t len,
                           ca_voice_transcription_result_t *out) {
    ca_keyword_voice_transcriber_t *t = (ca_keyword_voice_transcriber_t *)self;
    (void)pcm;
    if (!t || !out) return -1;
    size_t samples = len / 2;
    if (samples >= t->min_samples) {
        out->text = vc_strdup(t->phrase);
        out->confidence = t->confidence;
    } else {
        out->text = vc_strdup_empty(NULL);
        out->confidence = 0.0f;
    }
    out->language_code = vc_strdup(t->language);
    if (!out->text || !out->language_code) {
        ca_voice_transcription_result_free(out);
        return -1;
    }
    return 0;
}
static ca_voice_partial_transcription_t *kwtr_stream(void *self,
                                                     ca_audio_capture_t *chunks,
                                                     size_t *out_count) {
    ca_keyword_voice_transcriber_t *t = (ca_keyword_voice_transcriber_t *)self;
    if (out_count) *out_count = 0;
    if (!t || !chunks) { if (out_count) *out_count = SIZE_MAX; return NULL; }

    size_t total_samples = 0;
    bool crossed = false;
    /* accumulate; when threshold crossed, remember and emit a mid partial. */
    typedef struct { char *text; bool is_final; float conf; } part_t;
    part_t *arr = NULL;
    size_t n = 0, cap = 0;
    #define PUSH_PART(TX, FIN, CF) do { \
        if (n == cap) { size_t nc = cap ? cap*2 : 4; void *_p = realloc(arr, nc*sizeof(part_t)); \
            if (!_p) { goto oom; } arr = (part_t*)_p; cap = nc; } \
        arr[n].text = vc_strdup(TX); if (!arr[n].text) goto oom; \
        arr[n].is_final = (FIN); arr[n].conf = (CF); n++; } while (0)

    uint8_t *data; size_t len;
    while (ca_audio_capture_next(chunks, &data, &len)) {
        total_samples += len / 2;
        free(data);
        if (!crossed && total_samples >= t->min_samples) {
            crossed = true;
            PUSH_PART(t->phrase, false, t->confidence);   /* interim hypothesis */
        }
    }
    if (crossed) {
        PUSH_PART(t->phrase, true, t->confidence);        /* final */
    } else {
        PUSH_PART("", true, 0.0f);                        /* final empty */
    }
    #undef PUSH_PART

    /* Convert to public array. */
    ca_voice_partial_transcription_t *res =
        (ca_voice_partial_transcription_t *)calloc(n, sizeof(*res));
    if (!res) goto oom;
    for (size_t i = 0; i < n; ++i) {
        res[i].text = arr[i].text;   /* transfer */
        res[i].is_final = arr[i].is_final;
        res[i].confidence = arr[i].conf;
    }
    free(arr);
    if (out_count) *out_count = n;
    return res;
oom:
    for (size_t i = 0; i < n; ++i) free(arr[i].text);
    free(arr);
    if (out_count) *out_count = SIZE_MAX;
    return NULL;
}
ca_voice_transcriber_t ca_keyword_voice_transcriber_as_transcriber(
    ca_keyword_voice_transcriber_t *t) {
    ca_voice_transcriber_t v;
    v.self = t;
    v.transcribe = kwtr_transcribe;
    v.stream_transcribe = kwtr_stream;
    return v;
}

/* ===========================================================================
 * IWakeWordDetector — event/Start/Stop + pump
 * =========================================================================== */

typedef struct {
    ca_voice_wake_event_t *items;
    size_t head, count, cap;
} vwe_fifo_t;

static void vwe_move_destroy(ca_voice_wake_event_t *e) {
    if (!e) return;
    free(e->wake_word);
    e->wake_word = NULL;
}
static bool vwe_fifo_push(vwe_fifo_t *q, ca_voice_wake_event_t item) {
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
            q->items = (ca_voice_wake_event_t *)ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = item;
    return true;
}
static bool vwe_fifo_pop(vwe_fifo_t *q, ca_voice_wake_event_t *out) {
    if (q->head >= q->count) return false;
    *out = q->items[q->head];
    memset(&q->items[q->head], 0, sizeof(q->items[q->head]));
    q->head++;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return true;
}
static void vwe_fifo_free(vwe_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i) vwe_move_destroy(&q->items[i]);
    free(q->items);
    q->items = NULL;
    q->head = q->count = q->cap = 0;
}

struct ca_voice_wake_sub {
    ca_voice_wake_handler_fn handler;
    void                    *ctx;
    vwe_fifo_t               queue;
};

struct ca_voice_wake_detector {
    char                     *wake_word;   /* owned */
    bool                      listening;
    bool                      energy_mode;
    /* Energy mode collaborators (borrowed). */
    ca_audio_capture_t       *capture;
    ca_voice_transcriber_t    transcriber;
    ca_energy_vad_stream_t   *vad;         /* owned */
    ca_voice_wake_sub_t     **subs;
    size_t                    count, cap;
};

ca_voice_wake_detector_t *ca_null_voice_wake_detector_create(const char *wake_word) {
    ca_voice_wake_detector_t *d = (ca_voice_wake_detector_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    const char *ww = (wake_word && wake_word[0]) ? wake_word : "Hey B";
    d->wake_word = vc_strdup(ww);
    if (!d->wake_word) { free(d); return NULL; }
    d->energy_mode = false;
    return d;
}
ca_voice_wake_detector_t *ca_energy_voice_wake_detector_create(
    ca_audio_capture_t *capture, ca_voice_transcriber_t transcriber,
    const char *wake_word, float energy_threshold) {
    if (!capture || !transcriber.transcribe) return NULL;
    const char *ww = (wake_word && wake_word[0]) ? wake_word : "hey b";
    /* C# trims the wake word. */
    ca_voice_wake_detector_t *d = (ca_voice_wake_detector_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    /* trim */
    const char *s = ww; size_t b = strlen(s); size_t a = 0;
    while (a < b && isspace((unsigned char)s[a])) a++;
    while (b > a && isspace((unsigned char)s[b - 1])) b--;
    d->wake_word = (char *)malloc(b - a + 1);
    if (!d->wake_word) { free(d); return NULL; }
    memcpy(d->wake_word, s + a, b - a);
    d->wake_word[b - a] = '\0';
    d->energy_mode = true;
    d->capture = capture;
    d->transcriber = transcriber;
    /* EnergyVadDetector(energyThreshold, silenceFrames:10, frameSizeBytes:640). */
    d->vad = ca_energy_vad_stream_create(energy_threshold, 10, 640);
    if (!d->vad) { free(d->wake_word); free(d); return NULL; }
    return d;
}
void ca_voice_wake_detector_destroy(ca_voice_wake_detector_t *d) {
    if (!d) return;
    for (size_t i = 0; i < d->count; ++i) {
        vwe_fifo_free(&d->subs[i]->queue);
        free(d->subs[i]);
    }
    free(d->subs);
    ca_energy_vad_stream_destroy(d->vad);
    free(d->wake_word);
    free(d);
}
const char *ca_voice_wake_detector_wake_word(const ca_voice_wake_detector_t *d) {
    return d ? d->wake_word : NULL;
}
bool ca_voice_wake_detector_is_listening(const ca_voice_wake_detector_t *d) {
    return d ? d->listening : false;
}
void ca_voice_wake_detector_start(ca_voice_wake_detector_t *d) {
    if (d) d->listening = true;
}
void ca_voice_wake_detector_stop(ca_voice_wake_detector_t *d) {
    if (d) d->listening = false;
}
ca_voice_wake_sub_t *ca_voice_wake_detector_subscribe(
    ca_voice_wake_detector_t *d, ca_voice_wake_handler_fn handler, void *ctx) {
    if (!d) return NULL;
    if (d->count == d->cap) {
        size_t nc = d->cap ? d->cap * 2 : 4;
        void *ns = realloc(d->subs, nc * sizeof(*d->subs));
        if (!ns) return NULL;
        d->subs = (ca_voice_wake_sub_t **)ns;
        d->cap = nc;
    }
    ca_voice_wake_sub_t *s = (ca_voice_wake_sub_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->handler = handler;
    s->ctx = ctx;
    d->subs[d->count++] = s;
    return s;
}
void ca_voice_wake_detector_unsubscribe(ca_voice_wake_detector_t *d,
                                        ca_voice_wake_sub_t *sub) {
    if (!d || !sub) return;
    for (size_t i = 0; i < d->count; ++i) {
        if (d->subs[i] == sub) {
            vwe_fifo_free(&sub->queue);
            free(sub);
            d->subs[i] = d->subs[--d->count];
            return;
        }
    }
}
bool ca_voice_wake_sub_next(ca_voice_wake_sub_t *sub, ca_voice_wake_event_t *out) {
    if (!sub || !out) return false;
    return vwe_fifo_pop(&sub->queue, out);
}
size_t ca_voice_wake_sub_pending(const ca_voice_wake_sub_t *sub) {
    return sub ? (sub->queue.count - sub->queue.head) : 0;
}

/* Fire an event to every live subscriber (buffered copy + synchronous handler). */
static size_t wake_fire(ca_voice_wake_detector_t *d, float confidence,
                        int64_t at_utc_ms) {
    size_t delivered = 0;
    size_t nsub = d->count;
    for (size_t i = 0; i < nsub; ++i) {
        ca_voice_wake_sub_t *s = d->subs[i];
        ca_voice_wake_event_t item;
        memset(&item, 0, sizeof(item));
        item.wake_word = vc_strdup(d->wake_word);
        item.detected_at_utc_ms = at_utc_ms;
        item.confidence = confidence;
        if (item.wake_word && vwe_fifo_push(&s->queue, item)) delivered++;
        else vwe_move_destroy(&item);
        if (s->handler) {
            ca_voice_wake_event_t borrowed;
            borrowed.wake_word = d->wake_word;
            borrowed.detected_at_utc_ms = at_utc_ms;
            borrowed.confidence = confidence;
            s->handler(s->ctx, &borrowed);
        }
    }
    return delivered;
}

size_t ca_voice_wake_detector_pump(ca_voice_wake_detector_t *d) {
    if (!d || !d->energy_mode || !d->listening) return 0;

    ca_audio_capture_reset(d->capture);
    ca_voice_vad_stream_t vad = ca_energy_vad_stream_as_stream(d->vad);
    size_t seg_count = 0;
    ca_voice_vad_segment_t *segs = vad.detect(vad.self, d->capture, &seg_count);
    if (seg_count == SIZE_MAX) return 0;

    size_t fires = 0;
    for (size_t i = 0; i < seg_count; ++i) {
        if (!segs[i].is_speech || segs[i].audio_len == 0) continue;
        ca_voice_transcription_result_t res;
        memset(&res, 0, sizeof(res));
        if (d->transcriber.transcribe(d->transcriber.self, segs[i].audio,
                                      segs[i].audio_len, &res) != 0) {
            ca_voice_transcription_result_free(&res);
            continue;   /* transcription failed for this segment; keep going */
        }
        if (res.text && res.text[0] != '\0' &&
            ci_contains(res.text, d->wake_word)) {
            /* Confidence from the transcription (matches C# result.Confidence). */
            wake_fire(d, res.confidence, 0);
            fires++;
        }
        ca_voice_transcription_result_free(&res);
    }
    ca_voice_vad_segment_free_array(segs, seg_count);
    return fires;
}

/* ===========================================================================
 * ITtsEngine
 * =========================================================================== */

struct ca_null_voice_tts { int _; };
ca_null_voice_tts_t *ca_null_voice_tts_create(void) {
    return (ca_null_voice_tts_t *)calloc(1, sizeof(ca_null_voice_tts_t));
}
void ca_null_voice_tts_destroy(ca_null_voice_tts_t *e) { free(e); }
static int nulltts_synth(void *self, const char *text, ca_voice_tts_result_t *out) {
    (void)self; (void)text;
    if (!out) return -1;
    out->audio_data = NULL;
    out->audio_len = 0;
    out->sample_rate = 24000;
    out->channels = 1;
    out->bits_per_sample = 16;
    return 0;
}
ca_voice_tts_engine_t ca_null_voice_tts_as_engine(ca_null_voice_tts_t *e) {
    ca_voice_tts_engine_t v;
    v.self = e;
    v.synthesise = nulltts_synth;
    return v;
}

struct ca_template_voice_tts {
    int sample_rate;
    int samples_per_char;
};
ca_template_voice_tts_t *ca_template_voice_tts_create(int sample_rate, int samples_per_char) {
    ca_template_voice_tts_t *e = (ca_template_voice_tts_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->sample_rate = sample_rate > 0 ? sample_rate : 24000;
    e->samples_per_char = samples_per_char > 0 ? samples_per_char : 240;
    return e;
}
void ca_template_voice_tts_destroy(ca_template_voice_tts_t *e) { free(e); }
static int tpltts_synth(void *self, const char *text, ca_voice_tts_result_t *out) {
    ca_template_voice_tts_t *e = (ca_template_voice_tts_t *)self;
    if (!e || !out) return -1;
    memset(out, 0, sizeof(*out));
    out->sample_rate = e->sample_rate;
    out->channels = 1;
    out->bits_per_sample = 16;
    const char *t = text ? text : "";
    size_t nchars = strlen(t);
    size_t nsamples = nchars * (size_t)e->samples_per_char;
    if (nsamples == 0) return 0;
    uint8_t *buf = (uint8_t *)malloc(nsamples * 2);
    if (!buf) return -1;
    size_t idx = 0;
    for (size_t c = 0; c < nchars; ++c) {
        unsigned code = (unsigned char)t[c];
        int half_period = 4 + (int)(code % 60);
        for (int k = 0; k < e->samples_per_char; ++k) {
            int phase = (k / half_period) & 1;
            int16_t v = phase ? (int16_t)8192 : (int16_t)-8192;
            wr_i16le(buf + idx * 2, v);
            idx++;
        }
    }
    out->audio_data = buf;
    out->audio_len = nsamples * 2;
    return 0;
}
ca_voice_tts_engine_t ca_template_voice_tts_as_engine(ca_template_voice_tts_t *e) {
    ca_voice_tts_engine_t v;
    v.self = e;
    v.synthesise = tpltts_synth;
    return v;
}

/* ===========================================================================
 * ISpeechEmotionDetector
 * =========================================================================== */

/* Russell circumplex table (from OnnxSpeechEmotionDetector.Circumplex). */
typedef struct { const char *label; double arousal; double valence; } circ_t;
static const circ_t CIRCUMPLEX[] = {
    { "neutral",     0.00,  0.00 },
    { "happy",       0.55,  0.81 },
    { "happiness",   0.55,  0.81 },
    { "joy",         0.60,  0.82 },
    { "angry",       0.74, -0.62 },
    { "anger",       0.74, -0.62 },
    { "sad",        -0.43, -0.65 },
    { "sadness",    -0.43, -0.65 },
    { "fear",        0.78, -0.64 },
    { "fearful",     0.78, -0.64 },
    { "surprise",    0.85,  0.40 },
    { "surprised",   0.85,  0.40 },
    { "disgust",     0.45, -0.60 },
    { "disgusted",   0.45, -0.60 },
    { "calm",       -0.40,  0.45 },
    { "excited",     0.82,  0.70 },
    { "bored",      -0.65, -0.20 },
    { "frustrated",  0.55, -0.55 },
    { "contempt",    0.20, -0.55 },
};
static const size_t CIRCUMPLEX_COUNT = sizeof(CIRCUMPLEX) / sizeof(CIRCUMPLEX[0]);

static bool circ_lookup(const char *label, double *ar, double *va) {
    for (size_t i = 0; i < CIRCUMPLEX_COUNT; ++i) {
        /* OrdinalIgnoreCase */
        const char *a = CIRCUMPLEX[i].label, *b = label;
        size_t k = 0; bool eq = true;
        while (a[k] || b[k]) {
            if (tolower((unsigned char)a[k]) != tolower((unsigned char)b[k])) { eq = false; break; }
            k++;
        }
        if (eq) { *ar = CIRCUMPLEX[i].arousal; *va = CIRCUMPLEX[i].valence; return true; }
    }
    return false;
}

/* Default labels: neutral / happy / angry / sad. */
static const char *DEFAULT_EMOTION_LABELS[] = { "neutral", "happy", "angry", "sad" };

struct ca_speech_emotion_detector {
    ca_emotion_logits_runner_t runner;
    char                     **labels;   /* owned array of owned strings */
    size_t                     label_count;
    int                        sample_rate_hz;
    int                        max_clip_ms;
};

ca_speech_emotion_detector_t *ca_speech_emotion_detector_create(
    ca_emotion_logits_runner_t runner,
    const char *const *labels, size_t label_count,
    int sample_rate_hz, int max_clip_ms) {
    if (!runner.infer) return NULL;
    ca_speech_emotion_detector_t *d = (ca_speech_emotion_detector_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->runner = runner;
    d->sample_rate_hz = sample_rate_hz > 0 ? sample_rate_hz : 16000;
    d->max_clip_ms = max_clip_ms > 0 ? max_clip_ms : 8000;

    const char *const *src = labels;
    size_t n = label_count;
    if (!src || n == 0) {
        src = DEFAULT_EMOTION_LABELS;
        n = sizeof(DEFAULT_EMOTION_LABELS) / sizeof(DEFAULT_EMOTION_LABELS[0]);
    }
    d->labels = (char **)calloc(n, sizeof(char *));
    if (!d->labels) { free(d); return NULL; }
    for (size_t i = 0; i < n; ++i) {
        d->labels[i] = vc_strdup(src[i]);
        if (!d->labels[i]) {
            for (size_t k = 0; k < i; ++k) free(d->labels[k]);
            free(d->labels); free(d);
            return NULL;
        }
    }
    d->label_count = n;
    return d;
}
void ca_speech_emotion_detector_destroy(ca_speech_emotion_detector_t *d) {
    if (!d) return;
    for (size_t i = 0; i < d->label_count; ++i) free(d->labels[i]);
    free(d->labels);
    free(d);
}

/* Softmax argmax (matches OnnxSpeechEmotionDetector.Softmax). */
static void softmax_argmax(const float *logits, size_t n, int *best_idx, double *best_prob) {
    if (n == 0) { *best_idx = -1; *best_prob = 0.0; return; }
    float mx = logits[0];
    for (size_t i = 1; i < n; ++i) if (logits[i] > mx) mx = logits[i];
    double denom = 0.0;
    for (size_t i = 0; i < n; ++i) denom += exp((double)logits[i] - mx);
    int bi = 0; double bp = 0.0;
    for (size_t i = 0; i < n; ++i) {
        double p = exp((double)logits[i] - mx) / denom;
        if (p > bp) { bp = p; bi = (int)i; }
    }
    *best_idx = bi; *best_prob = bp;
}

bool ca_speech_emotion_detector_sense(ca_speech_emotion_detector_t *d,
                                      const uint8_t *audio_pcm16, size_t len,
                                      int sample_rate_hz,
                                      ca_speech_emotion_frame_t *out) {
    if (!d || !out) return false;
    if (len == 0) return false;                          /* audioPcm16.IsEmpty */
    if (sample_rate_hz != d->sample_rate_hz) return false; /* mismatch -> null */

    size_t max_samples = (size_t)sample_rate_hz * (size_t)d->max_clip_ms / 1000;
    size_t n_samples = len / 2;
    if (n_samples > max_samples) n_samples = max_samples;
    if (n_samples == 0) return false;

    float *window = (float *)malloc(n_samples * sizeof(float));
    if (!window) return false;
    for (size_t i = 0; i < n_samples; ++i)
        window[i] = rd_i16le(audio_pcm16 + i * 2) / 32768.0f;

    size_t cap = d->label_count > 0 ? d->label_count : 1;
    float *logits = (float *)malloc(cap * sizeof(float));
    if (!logits) { free(window); return false; }

    int nlog = d->runner.infer(d->runner.self, window, n_samples, logits, cap);
    free(window);
    if (nlog <= 0) { free(logits); return false; }   /* inference failed -> null */

    int best_idx; double best_prob;
    softmax_argmax(logits, (size_t)nlog, &best_idx, &best_prob);
    free(logits);

    const char *raw = (best_idx >= 0 && (size_t)best_idx < d->label_count)
                          ? d->labels[best_idx] : "unknown";
    /* ToLowerInvariant */
    size_t rl = strlen(raw);
    char *label = (char *)malloc(rl + 1);
    if (!label) return false;
    for (size_t i = 0; i < rl; ++i) label[i] = (char)tolower((unsigned char)raw[i]);
    label[rl] = '\0';

    double ar = 0.0, va = 0.0;
    circ_lookup(label, &ar, &va);   /* miss -> (0,0) */

    out->label = label;
    out->arousal = ar;
    out->valence = va;
    out->probability = best_prob;
    return true;
}

/* ===========================================================================
 * ISpeakerIdentity — cosine-centroid enroll/identify
 * =========================================================================== */

typedef struct {
    char  *user_id;   /* owned */
    float *centroid;  /* owned, embed_dim */
    int    sample_count;
} enrolled_t;

struct ca_speaker_identity {
    ca_speaker_embedder_runner_t runner;
    size_t                       embed_dim;
    int                          sample_rate_hz;
    int                          min_utterance_ms;
    int                          max_utterance_ms;
    double                       match_threshold;
    enrolled_t                  *enrolled;
    size_t                       count, cap;
};

ca_speaker_identity_t *ca_speaker_identity_create(
    ca_speaker_embedder_runner_t runner, size_t embed_dim,
    int sample_rate_hz, int min_utterance_ms, int max_utterance_ms,
    double match_threshold) {
    if (!runner.embed || embed_dim == 0) return NULL;
    ca_speaker_identity_t *s = (ca_speaker_identity_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->runner = runner;
    s->embed_dim = embed_dim;
    s->sample_rate_hz = sample_rate_hz > 0 ? sample_rate_hz : 16000;
    s->min_utterance_ms = min_utterance_ms > 0 ? min_utterance_ms : 1000;
    s->max_utterance_ms = max_utterance_ms > 0 ? max_utterance_ms : 8000;
    s->match_threshold = match_threshold;
    return s;
}
void ca_speaker_identity_destroy(ca_speaker_identity_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) {
        free(s->enrolled[i].user_id);
        free(s->enrolled[i].centroid);
    }
    free(s->enrolled);
    free(s);
}

static void l2_normalise(float *v, size_t n) {
    double sq = 0.0;
    for (size_t i = 0; i < n; ++i) sq += (double)v[i] * v[i];
    double norm = sqrt(sq);
    if (norm < 1e-9) return;
    for (size_t i = 0; i < n; ++i) v[i] = (float)(v[i] / norm);
}
static double cosine(const float *a, const float *b, size_t n) {
    double dot = 0.0;
    for (size_t i = 0; i < n; ++i) dot += (double)a[i] * b[i];
    return dot;   /* both L2-normalised => cosine == dot (matches C#) */
}

/* Compute an L2-normalised embedding for the clip, or NULL when C# returns null
 * (rate mismatch / too short). Caller frees. */
static float *compute_embedding(ca_speaker_identity_t *s, const uint8_t *pcm16,
                                size_t len, int sample_rate_hz) {
    if (sample_rate_hz != s->sample_rate_hz) return NULL;
    size_t min_samples = (size_t)sample_rate_hz * (size_t)s->min_utterance_ms / 1000;
    size_t max_samples = (size_t)sample_rate_hz * (size_t)s->max_utterance_ms / 1000;
    size_t n_samples = len / 2;
    if (n_samples < min_samples) return NULL;
    if (n_samples > max_samples) n_samples = max_samples;

    float *window = (float *)malloc(n_samples * sizeof(float));
    if (!window) return NULL;
    for (size_t i = 0; i < n_samples; ++i)
        window[i] = rd_i16le(pcm16 + i * 2) / 32768.0f;

    float *emb = (float *)malloc(s->embed_dim * sizeof(float));
    if (!emb) { free(window); return NULL; }
    int dim = s->runner.embed(s->runner.self, window, n_samples, emb, s->embed_dim);
    free(window);
    if (dim <= 0) { free(emb); return NULL; }
    /* Runner may report a dim <= embed_dim; treat that many as valid. */
    size_t d = (size_t)dim < s->embed_dim ? (size_t)dim : s->embed_dim;
    l2_normalise(emb, d);
    /* zero the tail if the runner under-filled */
    for (size_t i = d; i < s->embed_dim; ++i) emb[i] = 0.0f;
    return emb;
}

bool ca_speaker_identity_identify(ca_speaker_identity_t *s,
                                  const uint8_t *audio_pcm16, size_t len,
                                  int sample_rate_hz, char **out_user) {
    if (out_user) *out_user = NULL;
    if (!s) return false;
    if (len == 0) return false;         /* audioPcm16.IsEmpty */
    if (s->count == 0) return false;    /* _enrolled.IsEmpty */

    float *emb = compute_embedding(s, audio_pcm16, len, sample_rate_hz);
    if (!emb) return false;

    const char *best = NULL;
    double best_sim = -1e308;   /* double.MinValue */
    for (size_t i = 0; i < s->count; ++i) {
        double sim = cosine(emb, s->enrolled[i].centroid, s->embed_dim);
        if (sim > best_sim) { best_sim = sim; best = s->enrolled[i].user_id; }
    }
    free(emb);
    if (best_sim >= s->match_threshold && best) {
        if (out_user) {
            *out_user = vc_strdup(best);
            if (!*out_user) return false;
        }
        return true;
    }
    return false;
}

int ca_speaker_identity_enroll(ca_speaker_identity_t *s, const char *user_id,
                               const uint8_t *audio_pcm16, size_t len,
                               int sample_rate_hz) {
    if (!s || !user_id || user_id[0] == '\0') return -1;   /* ArgumentException */
    if (len == 0) return -1;                                /* audio required */

    float *emb = compute_embedding(s, audio_pcm16, len, sample_rate_hz);
    if (!emb) return -1;    /* InvalidOperationException: extraction failed */

    /* AddOrUpdate: running mean then L2-normalise on update; raw on first add. */
    for (size_t i = 0; i < s->count; ++i) {
        if (strcmp(s->enrolled[i].user_id, user_id) == 0) {
            int n = s->enrolled[i].sample_count;
            float *nc = (float *)malloc(s->embed_dim * sizeof(float));
            if (!nc) { free(emb); return -1; }
            for (size_t k = 0; k < s->embed_dim; ++k)
                nc[k] = (s->enrolled[i].centroid[k] * n + emb[k]) / (n + 1);
            l2_normalise(nc, s->embed_dim);
            free(s->enrolled[i].centroid);
            s->enrolled[i].centroid = nc;
            s->enrolled[i].sample_count = n + 1;
            free(emb);
            return 0;
        }
    }
    /* first enrollment: store the raw (already-normalised) embedding */
    if (s->count == s->cap) {
        size_t ncap = s->cap ? s->cap * 2 : 4;
        void *ne = realloc(s->enrolled, ncap * sizeof(*s->enrolled));
        if (!ne) { free(emb); return -1; }
        s->enrolled = (enrolled_t *)ne;
        s->cap = ncap;
    }
    s->enrolled[s->count].user_id = vc_strdup(user_id);
    if (!s->enrolled[s->count].user_id) { free(emb); return -1; }
    s->enrolled[s->count].centroid = emb;   /* transfer */
    s->enrolled[s->count].sample_count = 1;
    s->count++;
    return 0;
}
size_t ca_speaker_identity_enrolled_count(const ca_speaker_identity_t *s) {
    return s ? s->count : 0;
}
int ca_speaker_identity_sample_count(const ca_speaker_identity_t *s,
                                     const char *user_id) {
    if (!s || !user_id) return 0;
    for (size_t i = 0; i < s->count; ++i)
        if (strcmp(s->enrolled[i].user_id, user_id) == 0)
            return s->enrolled[i].sample_count;
    return 0;
}

/* ===========================================================================
 * VoicePipeline
 * =========================================================================== */

struct ca_voice_pipeline {
    ca_voice_wake_detector_t *wake;        /* borrowed */
    ca_voice_transcriber_t    transcriber; /* borrowed vtable */
    ca_audio_capture_t       *capture;     /* borrowed; may be an owned Null */
    bool                      owns_capture;
    bool                      has_vad;
    ca_voice_vad_stream_t     vad;         /* borrowed vtable */
    ca_voice_transcribed_fn   on_transcribed;
    void                     *ctx;
};

ca_voice_pipeline_t *ca_voice_pipeline_create(
    ca_voice_wake_detector_t *wake, ca_voice_transcriber_t transcriber,
    ca_audio_capture_t *capture, bool has_vad, ca_voice_vad_stream_t vad) {
    if (!wake || !transcriber.transcribe) return NULL;
    ca_voice_pipeline_t *p = (ca_voice_pipeline_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->wake = wake;
    p->transcriber = transcriber;
    if (capture) {
        p->capture = capture;
        p->owns_capture = false;
    } else {
        p->capture = ca_null_audio_capture_create();
        if (!p->capture) { free(p); return NULL; }
        p->owns_capture = true;
    }
    p->has_vad = has_vad;
    p->vad = vad;
    return p;
}
void ca_voice_pipeline_destroy(ca_voice_pipeline_t *p) {
    if (!p) return;
    if (p->owns_capture) ca_audio_capture_destroy(p->capture);
    free(p);
}
void ca_voice_pipeline_on_transcribed(ca_voice_pipeline_t *p,
                                      ca_voice_transcribed_fn handler, void *ctx) {
    if (!p) return;
    p->on_transcribed = handler;
    p->ctx = ctx;
}

/* Drain the final PartialTranscription of a stream into a TranscriptionResult
 * (mirrors ToFinalAsync: last item, language "und"). Returns true if a final
 * result exists. */
static bool stream_to_final(ca_voice_partial_transcription_t *parts, size_t n,
                            ca_voice_transcription_result_t *out) {
    if (n == 0 || !parts) return false;
    /* last emitted OR first is_final, whichever comes first (ToFinalAsync breaks
     * on the first is_final; otherwise takes the last). */
    size_t last = 0;
    bool found = false;
    for (size_t i = 0; i < n; ++i) {
        last = i;
        if (parts[i].is_final) { found = true; break; }
    }
    (void)found;
    out->text = vc_strdup(parts[last].text ? parts[last].text : "");
    out->confidence = parts[last].confidence;
    out->language_code = vc_strdup("und");
    return out->text && out->language_code;
}

bool ca_voice_pipeline_run_activation(ca_voice_pipeline_t *p, int64_t completed_at_utc_ms) {
    if (!p) return false;
    ca_audio_capture_reset(p->capture);

    ca_audio_capture_t *feed = p->capture;
    ca_audio_capture_t *speech_only = NULL;

    if (p->has_vad && p->vad.detect) {
        /* Extract speech segments, then feed only their bytes to the transcriber
         * (ExtractSpeechSegmentsAsync). */
        size_t seg_count = 0;
        ca_voice_vad_segment_t *segs = p->vad.detect(p->vad.self, p->capture, &seg_count);
        if (seg_count == SIZE_MAX) return false;
        speech_only = ca_scripted_audio_capture_create(ca_audio_capture_format(p->capture));
        if (!speech_only) { ca_voice_vad_segment_free_array(segs, seg_count); return false; }
        for (size_t i = 0; i < seg_count; ++i)
            if (segs[i].is_speech && segs[i].audio_len > 0)
                ca_scripted_audio_capture_push(speech_only, segs[i].audio, segs[i].audio_len);
        ca_voice_vad_segment_free_array(segs, seg_count);
        feed = speech_only;
    }

    size_t np = 0;
    ca_voice_partial_transcription_t *parts =
        p->transcriber.stream_transcribe(p->transcriber.self, feed, &np);
    if (speech_only) ca_audio_capture_destroy(speech_only);

    if (np == SIZE_MAX) return false;

    ca_voice_transcription_result_t res;
    memset(&res, 0, sizeof(res));
    bool have_final = stream_to_final(parts, np, &res);
    ca_voice_partial_transcription_free_array(parts, np);

    if (!have_final) {
        ca_voice_transcription_result_free(&res);
        return false;
    }
    if (p->on_transcribed) p->on_transcribed(p->ctx, &res, completed_at_utc_ms);
    ca_voice_transcription_result_free(&res);
    return true;
}
