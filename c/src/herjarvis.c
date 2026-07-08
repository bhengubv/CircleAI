/*
 * herjarvis.c — CircleAI HER/Jarvis companion contracts (C11 port).
 *
 * In-memory, deterministic implementations ported 1:1 from the C# reference
 * (HerJarvisContracts.cs + HerJarvisRealImplementations.cs +
 * SelfBenchSelfImprovementLoop.cs + VoiceCompanionListener.cs). See herjarvis.h
 * for the contract-by-contract mapping and ownership rules.
 *
 * Pure C11 + libc. Links against -lm. No pthreads.
 */

#include "circle_ai/herjarvis.h"
#include "circle_ai/compression.h"   /* ca_base64_encode */

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <math.h>
#include <ctype.h>

/* Portable PI (mingw gates HJ_PI behind _USE_MATH_DEFINES). */
#ifndef HJ_PI
#define HJ_PI 3.14159265358979323846
#endif

/* =====================================================================
 * Small shared helpers
 * ===================================================================== */

static char *hj_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static bool hj_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p)
        if (!isspace(*p)) return false;
    return true;
}

/* Deterministic 32-hex id from a monotonically increasing counter (stands in for
 * Guid.NewGuid().ToString("n") — the C# id is opaque, so a reproducible one
 * keeps tests deterministic). */
static void hj_make_id(uint64_t counter, char out[33]) {
    /* Mix the counter so successive ids don't share long prefixes. */
    uint64_t x = counter * 0x9E3779B97F4A7C15ull + 0x1234567890ABCDEFull;
    uint64_t y = (counter ^ 0xD1B54A32D192ED03ull) * 0xBF58476D1CE4E5B9ull;
    snprintf(out, 33, "%08x%08x%08x%08x",
             (unsigned)(x >> 32), (unsigned)(x & 0xFFFFFFFFu),
             (unsigned)(y >> 32), (unsigned)(y & 0xFFFFFFFFu));
}

/* Clamp helper. */
static double hj_clamp(double v, double lo, double hi) {
    return v < lo ? lo : (v > hi ? hi : v);
}

/* Growable char buffer (append raw). */
typedef struct { char *buf; size_t len; size_t cap; } hj_sb;
static void hj_sb_ensure(hj_sb *b, size_t extra) {
    if (b->len + extra + 1 > b->cap) {
        size_t nc = b->cap ? b->cap : 64;
        while (b->len + extra + 1 > nc) nc *= 2;
        char *nb = (char *)realloc(b->buf, nc);
        if (!nb) return;
        b->buf = nb; b->cap = nc;
    }
}
static void hj_sb_append(hj_sb *b, const char *s) {
    if (!s) return;
    size_t n = strlen(s);
    hj_sb_ensure(b, n);
    if (!b->buf) return;
    memcpy(b->buf + b->len, s, n);
    b->len += n;
    b->buf[b->len] = '\0';
}
static void hj_sb_append_char(hj_sb *b, char c) {
    hj_sb_ensure(b, 1);
    if (!b->buf) return;
    b->buf[b->len++] = c;
    b->buf[b->len] = '\0';
}

/* JSON string escape matching System.Text.Json's JavaScriptEncoder.Default —
 * the same encoding companion_reason.c uses so belief/skill JSON is identical
 * across the ports. Hex digits UPPERCASE; the HTML/JS-sensitive ASCII set
 * < > & ' ` + escaped to \uXXXX. */
static void hj_json_emit_u(hj_sb *b, unsigned cp) {
    char tmp[8];
    snprintf(tmp, sizeof(tmp), "\\u%04X", cp & 0xFFFF);
    hj_sb_append(b, tmp);
}
static void hj_json_escape(hj_sb *b, const char *s) {
    const unsigned char *p = (const unsigned char *)s;
    while (p && *p) {
        unsigned char c = *p;
        if (c < 0x80) {
            switch (c) {
                case '\\': hj_sb_append(b, "\\\\"); ++p; continue;
                case '\b': hj_sb_append(b, "\\b");  ++p; continue;
                case '\t': hj_sb_append(b, "\\t");  ++p; continue;
                case '\n': hj_sb_append(b, "\\n");  ++p; continue;
                case '\f': hj_sb_append(b, "\\f");  ++p; continue;
                case '\r': hj_sb_append(b, "\\r");  ++p; continue;
                default: break;
            }
            if (c < 0x20 || c == '"' || c == '<' || c == '>' || c == '&' ||
                c == '\'' || c == '`' || c == '+') {
                hj_json_emit_u(b, c);
            } else {
                hj_sb_append_char(b, (char)c);
            }
            ++p;
            continue;
        }
        unsigned cp; int adv;
        if ((c & 0xE0) == 0xC0 && p[1]) { cp = ((c & 0x1Fu) << 6) | (p[1] & 0x3Fu); adv = 2; }
        else if ((c & 0xF0) == 0xE0 && p[1] && p[2]) {
            cp = ((c & 0x0Fu) << 12) | ((p[1] & 0x3Fu) << 6) | (p[2] & 0x3Fu); adv = 3;
        } else if ((c & 0xF8) == 0xF0 && p[1] && p[2] && p[3]) {
            cp = ((c & 0x07u) << 18) | ((p[1] & 0x3Fu) << 12) |
                 ((p[2] & 0x3Fu) << 6) | (p[3] & 0x3Fu); adv = 4;
        } else { cp = c; adv = 1; }
        if (cp <= 0xFFFF) hj_json_emit_u(b, cp);
        else {
            unsigned v = cp - 0x10000u;
            hj_json_emit_u(b, 0xD800u | (v >> 10));
            hj_json_emit_u(b, 0xDC00u | (v & 0x3FFu));
        }
        p += adv;
    }
}

/* ISO-8601 "O"-style UTC timestamp from Unix ms, matching DateTimeOffset "O"
 * (yyyy-MM-ddTHH:mm:ss.fffffff+00:00). We render 7 fractional digits from the
 * millisecond value (sub-ms is always zero here). */
static void hj_iso8601(int64_t unix_ms, char out[48]) {
    int64_t secs = unix_ms / 1000;
    int ms = (int)(unix_ms % 1000);
    if (ms < 0) { ms += 1000; secs -= 1; }
    /* civil-from-days (Howard Hinnant's algorithm; shift the epoch into the
     * 0000-03-01 era with +719468 days). */
    int64_t z = secs / 86400;
    int64_t rem = secs % 86400;
    if (rem < 0) { rem += 86400; z -= 1; }
    z += 719468;
    int64_t era = (z >= 0 ? z : z - 146096) / 146097;
    unsigned doe = (unsigned)(z - era * 146097);
    unsigned yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    int64_t y = (int64_t)yoe + era * 400;
    unsigned doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    unsigned mp = (5 * doy + 2) / 153;
    unsigned d = doy - (153 * mp + 2) / 5 + 1;
    unsigned m = mp < 10 ? mp + 3 : mp - 9;
    if (m <= 2) y += 1;
    unsigned hh = (unsigned)(rem / 3600) % 24u;
    unsigned mm = (unsigned)((rem % 3600) / 60) % 60u;
    unsigned ss = (unsigned)(rem % 60) % 60u;
    unsigned frac = ((unsigned)ms % 1000u) * 10000u;   /* 7-digit fractional seconds, < 10^7 */
    /* Buffer sized for the widest int64 year (out[48] from the prototype is
     * ample; write into a local then copy to keep -Wformat-truncation quiet). */
    char tmp[64];
    snprintf(tmp, sizeof(tmp), "%04lld-%02u-%02uT%02u:%02u:%02u.%07u+00:00",
             (long long)y, m, d, hh, mm, ss, frac);
    tmp[47] = '\0';
    memcpy(out, tmp, strlen(tmp) + 1);
}

/* =====================================================================
 * 1. HeartbeatAlwaysOnPresence
 * ===================================================================== */

struct ca_always_on_presence {
    bool     running;
    bool     ever_started;
    int64_t  ticks;
};

ca_always_on_presence_t *ca_always_on_presence_create(void) {
    return (ca_always_on_presence_t *)calloc(1, sizeof(ca_always_on_presence_t));
}
void ca_always_on_presence_destroy(ca_always_on_presence_t *p) { free(p); }

bool ca_always_on_presence_is_running(const ca_always_on_presence_t *p) {
    return p && p->running;
}
int64_t ca_always_on_presence_heartbeats(const ca_always_on_presence_t *p) {
    return p ? p->ticks : 0;
}
void ca_always_on_presence_start(ca_always_on_presence_t *p) {
    if (!p || p->running) return;      /* idempotent: no restart while running */
    p->running = true;
    /* Timer dueTime = Zero fires one immediate heartbeat on start. */
    p->ticks++;
}
void ca_always_on_presence_stop(ca_always_on_presence_t *p) {
    if (!p) return;
    p->running = false;
}
int64_t ca_always_on_presence_tick(ca_always_on_presence_t *p) {
    if (!p) return 0;
    if (p->running) p->ticks++;
    return p->ticks;
}

/* =====================================================================
 * 2. ChannelFusedPerception (publish/drain FIFO)
 * ===================================================================== */

void ca_fused_percept_free(ca_fused_percept_t *p) {
    if (!p) return;
    free(p->vision); free(p->audio); free(p->text);
    if (p->sensor_keys) for (size_t i = 0; i < p->sensor_count; ++i) free(p->sensor_keys[i]);
    free(p->sensor_keys);
    free(p->sensor_values);
    p->vision = p->audio = p->text = NULL;
    p->sensor_keys = NULL; p->sensor_values = NULL; p->sensor_count = 0;
}

static void hj_percept_copy(ca_fused_percept_t *dst, const ca_fused_percept_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->at_ms = src->at_ms;
    dst->vision = hj_strdup(src->vision);
    dst->audio  = hj_strdup(src->audio);
    dst->text   = hj_strdup(src->text);
    dst->sensor_count = src->sensor_count;
    if (src->sensor_count) {
        dst->sensor_keys = (char **)calloc(src->sensor_count, sizeof(char *));
        dst->sensor_values = (double *)calloc(src->sensor_count, sizeof(double));
        for (size_t i = 0; i < src->sensor_count; ++i) {
            dst->sensor_keys[i] = hj_strdup(src->sensor_keys ? src->sensor_keys[i] : NULL);
            dst->sensor_values[i] = src->sensor_values ? src->sensor_values[i] : 0.0;
        }
    }
}

/* Generic single-thread FIFO of fixed-size elements. */
typedef struct {
    void  *items;
    size_t elem_size;
    size_t head, tail, count, cap;
} hj_queue;

static void hj_queue_init(hj_queue *q, size_t elem_size) {
    q->items = NULL; q->elem_size = elem_size;
    q->head = q->tail = q->count = q->cap = 0;
}
static bool hj_queue_push(hj_queue *q, const void *elem) {
    if (q->count == q->cap) {
        size_t nc = q->cap ? q->cap * 2 : 8;
        void *ni = malloc(nc * q->elem_size);
        if (!ni) return false;
        /* re-linearise */
        for (size_t i = 0; i < q->count; ++i) {
            memcpy((char *)ni + i * q->elem_size,
                   (char *)q->items + ((q->head + i) % q->cap) * q->elem_size,
                   q->elem_size);
        }
        free(q->items);
        q->items = ni; q->cap = nc; q->head = 0; q->tail = q->count;
    }
    memcpy((char *)q->items + q->tail * q->elem_size, elem, q->elem_size);
    q->tail = (q->tail + 1) % q->cap;
    q->count++;
    return true;
}
static bool hj_queue_pop(hj_queue *q, void *out) {
    if (q->count == 0) return false;
    memcpy(out, (char *)q->items + q->head * q->elem_size, q->elem_size);
    q->head = (q->head + 1) % q->cap;
    q->count--;
    return true;
}

struct ca_fused_perception {
    hj_queue q;         /* of ca_fused_percept_t (owned copies) */
    bool     completed;
};

ca_fused_perception_t *ca_fused_perception_create(void) {
    ca_fused_perception_t *fp = (ca_fused_perception_t *)calloc(1, sizeof(*fp));
    if (fp) hj_queue_init(&fp->q, sizeof(ca_fused_percept_t));
    return fp;
}
void ca_fused_perception_destroy(ca_fused_perception_t *fp) {
    if (!fp) return;
    ca_fused_percept_t tmp;
    while (hj_queue_pop(&fp->q, &tmp)) ca_fused_percept_free(&tmp);
    free(fp->q.items);
    free(fp);
}
void ca_fused_perception_publish(ca_fused_perception_t *fp, const ca_fused_percept_t *p) {
    if (!fp || !p || fp->completed) return;   /* TryWrite after complete → dropped */
    ca_fused_percept_t copy;
    hj_percept_copy(&copy, p);
    if (!hj_queue_push(&fp->q, &copy)) ca_fused_percept_free(&copy);
}
void ca_fused_perception_complete(ca_fused_perception_t *fp) {
    if (fp) fp->completed = true;
}
bool ca_fused_perception_read(ca_fused_perception_t *fp, ca_fused_percept_t *out) {
    if (!fp || !out) return false;
    return hj_queue_pop(&fp->q, out);
}

/* =====================================================================
 * 4. EwaContinuousLearner
 * ===================================================================== */

typedef struct { char *id; double avg; double weight; } hj_ewa_entry;

struct ca_continuous_learner {
    double        alpha;
    hj_ewa_entry *entries;
    size_t        count, cap;
};

ca_continuous_learner_t *ca_continuous_learner_create(double alpha) {
    if (alpha <= 0.0 || alpha > 1.0) return NULL;
    ca_continuous_learner_t *l = (ca_continuous_learner_t *)calloc(1, sizeof(*l));
    if (l) l->alpha = alpha;
    return l;
}
void ca_continuous_learner_destroy(ca_continuous_learner_t *l) {
    if (!l) return;
    for (size_t i = 0; i < l->count; ++i) free(l->entries[i].id);
    free(l->entries);
    free(l);
}
static hj_ewa_entry *hj_ewa_find(ca_continuous_learner_t *l, const char *id) {
    for (size_t i = 0; i < l->count; ++i)
        if (strcmp(l->entries[i].id, id) == 0) return &l->entries[i];   /* Ordinal */
    return NULL;
}
void ca_continuous_learner_register(ca_continuous_learner_t *l,
                                    const char *interaction_id, double reward,
                                    const char *context_json) {
    (void)context_json;
    if (!l || hj_blank(interaction_id)) return;
    hj_ewa_entry *e = hj_ewa_find(l, interaction_id);
    if (e) {
        e->avg = e->avg * (1.0 - l->alpha) + reward * l->alpha;
        e->weight += 1.0;
        return;
    }
    if (l->count == l->cap) {
        size_t nc = l->cap ? l->cap * 2 : 8;
        hj_ewa_entry *ne = (hj_ewa_entry *)realloc(l->entries, nc * sizeof(hj_ewa_entry));
        if (!ne) return;
        l->entries = ne; l->cap = nc;
    }
    l->entries[l->count].id = hj_strdup(interaction_id);
    l->entries[l->count].avg = reward;
    l->entries[l->count].weight = 1.0;
    l->count++;
}
bool ca_continuous_learner_average(const ca_continuous_learner_t *l,
                                   const char *interaction_id, double *out) {
    if (!l || !interaction_id || !out) return false;
    hj_ewa_entry *e = hj_ewa_find((ca_continuous_learner_t *)l, interaction_id);
    if (!e) return false;
    *out = e->avg;
    return true;
}
int64_t ca_continuous_learner_observations(const ca_continuous_learner_t *l,
                                           const char *interaction_id) {
    if (!l || !interaction_id) return 0;
    hj_ewa_entry *e = hj_ewa_find((ca_continuous_learner_t *)l, interaction_id);
    return e ? (int64_t)e->weight : 0;
}

/* =====================================================================
 * 6. InMemoryGoalPursuer
 * ===================================================================== */

void ca_long_horizon_goal_free(ca_long_horizon_goal_t *g) {
    if (!g) return;
    free(g->id); free(g->description); free(g->plan_json);
    g->id = g->description = g->plan_json = NULL;
}

/* Build the milestone plan JSON exactly like C# BuildPlan. now/deadline are ms. */
static char *hj_build_plan(const char *description, int64_t now_ms, int64_t deadline_ms) {
    int64_t span_ms = deadline_ms - now_ms;
    int total_days = (int)(span_ms / 86400000LL);
    if (total_days < 1) total_days = 1;
    int milestones = total_days / 14;
    if (milestones < 2) milestones = 2;
    if (milestones > 8) milestones = 8;
    /* step = (deadline - now) / milestones (integer-ms division, as TimeSpan/int) */
    int64_t step_ms = span_ms / milestones;

    hj_sb sb = {0};
    hj_sb_append(&sb, "{\"description\":\"");
    hj_json_escape(&sb, description ? description : "");
    hj_sb_append(&sb, "\",\"milestones\":[");
    for (int i = 1; i <= milestones; ++i) {
        if (i > 1) hj_sb_append_char(&sb, ',');
        int64_t due_ms = now_ms + step_ms * i;
        char iso[48];
        hj_iso8601(due_ms, iso);
        hj_sb_append(&sb, "{\"index\":");
        char num[16]; snprintf(num, sizeof(num), "%d", i);
        hj_sb_append(&sb, num);
        hj_sb_append(&sb, ",\"due\":\"");
        hj_sb_append(&sb, iso);
        hj_sb_append(&sb, "\"}");
    }
    hj_sb_append(&sb, "]}");
    return sb.buf ? sb.buf : hj_strdup("{}");
}

struct ca_goal_pursuer {
    ca_long_horizon_goal_t *goals;
    size_t                  count, cap;
    uint64_t                counter;
};

ca_goal_pursuer_t *ca_goal_pursuer_create(void) {
    return (ca_goal_pursuer_t *)calloc(1, sizeof(ca_goal_pursuer_t));
}
void ca_goal_pursuer_destroy(ca_goal_pursuer_t *gp) {
    if (!gp) return;
    for (size_t i = 0; i < gp->count; ++i) ca_long_horizon_goal_free(&gp->goals[i]);
    free(gp->goals);
    free(gp);
}
static ca_long_horizon_goal_t *hj_goal_find(ca_goal_pursuer_t *gp, const char *id) {
    for (size_t i = 0; i < gp->count; ++i)
        if (strcmp(gp->goals[i].id, id) == 0) return &gp->goals[i];
    return NULL;
}
static void hj_goal_copy(ca_long_horizon_goal_t *dst, const ca_long_horizon_goal_t *src) {
    dst->id = hj_strdup(src->id);
    dst->description = hj_strdup(src->description);
    dst->deadline_ms = src->deadline_ms;
    dst->plan_json = hj_strdup(src->plan_json);
    dst->progress_fraction = src->progress_fraction;
}
bool ca_goal_pursuer_register(ca_goal_pursuer_t *gp, const char *description,
                              int64_t deadline_ms, int64_t now_ms,
                              ca_long_horizon_goal_t *out) {
    if (!gp || !out || hj_blank(description)) return false;
    if (deadline_ms <= now_ms) return false;   /* "deadline must be in the future" */
    if (gp->count == gp->cap) {
        size_t nc = gp->cap ? gp->cap * 2 : 8;
        ca_long_horizon_goal_t *ng =
            (ca_long_horizon_goal_t *)realloc(gp->goals, nc * sizeof(*ng));
        if (!ng) return false;
        gp->goals = ng; gp->cap = nc;
    }
    char id[33];
    hj_make_id(gp->counter++, id);
    ca_long_horizon_goal_t *g = &gp->goals[gp->count++];
    g->id = hj_strdup(id);
    g->description = hj_strdup(description);
    g->deadline_ms = deadline_ms;
    g->plan_json = hj_build_plan(description, now_ms, deadline_ms);
    g->progress_fraction = 0.0;
    hj_goal_copy(out, g);
    return true;
}
bool ca_goal_pursuer_current(const ca_goal_pursuer_t *gp, const char *id,
                             ca_long_horizon_goal_t *out) {
    if (!gp || !id || !out) return false;
    ca_long_horizon_goal_t *g = hj_goal_find((ca_goal_pursuer_t *)gp, id);
    if (!g) return false;
    hj_goal_copy(out, g);
    return true;
}
bool ca_goal_pursuer_replan(ca_goal_pursuer_t *gp, const char *id, int64_t now_ms) {
    if (!gp || !id) return false;
    ca_long_horizon_goal_t *g = hj_goal_find(gp, id);
    if (!g) return false;
    char *plan = hj_build_plan(g->description, now_ms, g->deadline_ms);
    free(g->plan_json);
    g->plan_json = plan;
    return true;
}
bool ca_goal_pursuer_progress(ca_goal_pursuer_t *gp, const char *id, double fraction) {
    if (!gp || !id) return false;
    if (fraction < 0.0 || fraction > 1.0) return false;   /* ArgumentOutOfRange */
    ca_long_horizon_goal_t *g = hj_goal_find(gp, id);
    if (!g) return false;
    g->progress_fraction = fraction;
    return true;
}

/* =====================================================================
 * 8. EnergyBandVoiceIdentity — MFCC + cosine
 * ===================================================================== */

#define HJ_MFCC_COEFFS 13
#define HJ_MEL_FILTERS 26
#define HJ_FRAME_SIZE  400
#define HJ_FRAME_STEP  160
#define HJ_PRE_EMPHASIS 0.97

typedef struct { char *user; double *fps; size_t fp_count; } hj_voice_entry;

struct ca_voice_identity {
    hj_voice_entry *entries;
    size_t          count, cap;
};

static float *hj_decode_pcm16(const uint8_t *pcm, size_t byte_len, size_t *out_n) {
    size_t n = byte_len / 2;
    *out_n = n;
    if (n == 0) return NULL;
    float *s = (float *)malloc(n * sizeof(float));
    if (!s) { *out_n = 0; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        short v = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
        s[i] = v / 32768.0f;
    }
    return s;
}

static double hz_to_mel(double hz) { return 2595.0 * log10(1.0 + hz / 700.0); }
static double mel_to_hz(double mel) { return 700.0 * (pow(10.0, mel / 2595.0) - 1.0); }

/* mean MFCC over frames into out[13]. Returns coefficient count (13) or 0. */
size_t ca_voice_identity_mfcc(const uint8_t *audio_pcm16, size_t byte_len,
                              int sample_rate_hz, double out_coeffs[13]) {
    if (!audio_pcm16 || !out_coeffs || sample_rate_hz <= 0) return 0;
    size_t n;
    float *samples = hj_decode_pcm16(audio_pcm16, byte_len, &n);
    for (int i = 0; i < HJ_MFCC_COEFFS; ++i) out_coeffs[i] = 0.0;
    if (!samples || n < HJ_FRAME_SIZE) { free(samples); return HJ_MFCC_COEFFS; }

    /* pre-emphasis */
    for (size_t i = n - 1; i > 0; --i)
        samples[i] -= (float)HJ_PRE_EMPHASIS * samples[i - 1];

    /* mel filterbank */
    int half = HJ_FRAME_SIZE / 2 + 1;
    double low_mel = hz_to_mel(0.0);
    double high_mel = hz_to_mel(sample_rate_hz / 2.0);
    double mel_points[HJ_MEL_FILTERS + 2];
    int bin_points[HJ_MEL_FILTERS + 2];
    for (int i = 0; i < HJ_MEL_FILTERS + 2; ++i) {
        mel_points[i] = low_mel + (high_mel - low_mel) * i / (HJ_MEL_FILTERS + 1);
        bin_points[i] = (int)floor((HJ_FRAME_SIZE + 1) * mel_to_hz(mel_points[i]) / sample_rate_hz);
    }
    double *filters = (double *)calloc((size_t)HJ_MEL_FILTERS * half, sizeof(double));
    if (!filters) { free(samples); return HJ_MFCC_COEFFS; }
    for (int m = 0; m < HJ_MEL_FILTERS; ++m) {
        int left = bin_points[m], centre = bin_points[m + 1], right = bin_points[m + 2];
        for (int k = left; k < centre && k < half; ++k)
            if (centre != left) filters[m * half + k] = (double)(k - left) / (centre - left);
        for (int k = centre; k < right && k < half; ++k)
            if (right != centre) filters[m * half + k] = (double)(right - k) / (right - centre);
    }

    /* Hamming window */
    float window[HJ_FRAME_SIZE];
    for (int i = 0; i < HJ_FRAME_SIZE; ++i)
        window[i] = 0.54f - 0.46f * (float)cos(2.0 * HJ_PI * i / (HJ_FRAME_SIZE - 1));

    double sum[HJ_MFCC_COEFFS] = {0};
    int frame_count = 0;
    double *power = (double *)malloc(half * sizeof(double));
    for (size_t start = 0; start + HJ_FRAME_SIZE <= n; start += HJ_FRAME_STEP) {
        /* windowed power spectrum via direct DFT */
        for (int k = 0; k < half; ++k) {
            double re = 0, im = 0, omega = -2.0 * HJ_PI * k / HJ_FRAME_SIZE;
            for (int t = 0; t < HJ_FRAME_SIZE; ++t) {
                double x = samples[start + t] * window[t];
                re += x * cos(omega * t);
                im += x * sin(omega * t);
            }
            power[k] = re * re + im * im;
        }
        /* mel energies → log */
        double log_e[HJ_MEL_FILTERS];
        for (int m = 0; m < HJ_MEL_FILTERS; ++m) {
            double e = 0;
            for (int k = 0; k < half; ++k) e += power[k] * filters[m * half + k];
            log_e[m] = log(e < 1e-10 ? 1e-10 : e);
        }
        /* DCT-II → first 13 */
        for (int kc = 0; kc < HJ_MFCC_COEFFS; ++kc) {
            double acc = 0;
            for (int i = 0; i < HJ_MEL_FILTERS; ++i)
                acc += log_e[i] * cos(HJ_PI * kc * (i + 0.5) / HJ_MEL_FILTERS);
            sum[kc] += acc;
        }
        frame_count++;
    }
    free(power);
    free(filters);
    free(samples);
    if (frame_count == 0) { for (int i = 0; i < HJ_MFCC_COEFFS; ++i) out_coeffs[i] = sum[i]; return HJ_MFCC_COEFFS; }
    for (int i = 0; i < HJ_MFCC_COEFFS; ++i) out_coeffs[i] = sum[i] / frame_count;
    return HJ_MFCC_COEFFS;
}

static double hj_cosine(const double *a, const double *b, size_t n) {
    double dot = 0, na = 0, nb = 0;
    for (size_t i = 0; i < n; ++i) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
    return (na == 0 || nb == 0) ? 0.0 : dot / (sqrt(na) * sqrt(nb));
}

ca_voice_identity_t *ca_voice_identity_create(void) {
    return (ca_voice_identity_t *)calloc(1, sizeof(ca_voice_identity_t));
}
void ca_voice_identity_destroy(ca_voice_identity_t *v) {
    if (!v) return;
    for (size_t i = 0; i < v->count; ++i) { free(v->entries[i].user); free(v->entries[i].fps); }
    free(v->entries);
    free(v);
}
static hj_voice_entry *hj_voice_find(ca_voice_identity_t *v, const char *user) {
    for (size_t i = 0; i < v->count; ++i)
        if (strcmp(v->entries[i].user, user) == 0) return &v->entries[i];
    return NULL;
}
void ca_voice_identity_enroll(ca_voice_identity_t *v, const char *user_id,
                              const uint8_t *audio_pcm16, size_t byte_len,
                              int sample_rate_hz) {
    if (!v || hj_blank(user_id) || !audio_pcm16) return;
    double fp[HJ_MFCC_COEFFS];
    ca_voice_identity_mfcc(audio_pcm16, byte_len, sample_rate_hz, fp);
    hj_voice_entry *e = hj_voice_find(v, user_id);
    if (!e) {
        if (v->count == v->cap) {
            size_t nc = v->cap ? v->cap * 2 : 8;
            hj_voice_entry *ne = (hj_voice_entry *)realloc(v->entries, nc * sizeof(*ne));
            if (!ne) return;
            v->entries = ne; v->cap = nc;
        }
        e = &v->entries[v->count++];
        e->user = hj_strdup(user_id);
        e->fps = NULL; e->fp_count = 0;
    }
    double *nf = (double *)realloc(e->fps, (e->fp_count + 1) * HJ_MFCC_COEFFS * sizeof(double));
    if (!nf) return;
    e->fps = nf;
    memcpy(e->fps + e->fp_count * HJ_MFCC_COEFFS, fp, HJ_MFCC_COEFFS * sizeof(double));
    e->fp_count++;
}
char *ca_voice_identity_identify(const ca_voice_identity_t *v,
                                 const uint8_t *audio_pcm16, size_t byte_len,
                                 int sample_rate_hz) {
    if (!v || !audio_pcm16) return NULL;
    double fp[HJ_MFCC_COEFFS];
    ca_voice_identity_mfcc(audio_pcm16, byte_len, sample_rate_hz, fp);
    const char *best = NULL;
    double best_sim = -1.0;
    for (size_t i = 0; i < v->count; ++i) {
        for (size_t j = 0; j < v->entries[i].fp_count; ++j) {
            double sim = hj_cosine(fp, v->entries[i].fps + j * HJ_MFCC_COEFFS, HJ_MFCC_COEFFS);
            if (sim > best_sim) { best_sim = sim; best = v->entries[i].user; }
        }
    }
    return (best_sim > 0.85 && best) ? hj_strdup(best) : NULL;
}

/* =====================================================================
 * 9. HistoricalCalibratedConfidence
 * ===================================================================== */

typedef struct { double raw; bool correct; } hj_calib_sample;

struct ca_calibrated_confidence {
    hj_calib_sample *hist;
    size_t           count, cap;
};

ca_calibrated_confidence_t *ca_calibrated_confidence_create(void) {
    return (ca_calibrated_confidence_t *)calloc(1, sizeof(ca_calibrated_confidence_t));
}
void ca_calibrated_confidence_destroy(ca_calibrated_confidence_t *c) {
    if (!c) return;
    free(c->hist);
    free(c);
}
void ca_calibrated_confidence_record(ca_calibrated_confidence_t *c,
                                     double raw_score, bool was_correct) {
    if (!c) return;
    if (c->count == c->cap) {
        size_t nc = c->cap ? c->cap * 2 : 8;
        hj_calib_sample *nh = (hj_calib_sample *)realloc(c->hist, nc * sizeof(*nh));
        if (!nh) return;
        c->hist = nh; c->cap = nc;
    }
    c->hist[c->count].raw = hj_clamp(raw_score, 0.0, 1.0);
    c->hist[c->count].correct = was_correct;
    c->count++;
}

/* Word-boundary count of any hedge token (maybe/perhaps/might/possibly/unclear or
 * the phrase "don't know"), case-insensitive, matching the C# regex. */
static int hj_count_hedges(const char *answer) {
    static const char *words[] = { "maybe", "perhaps", "might", "possibly", "unclear" };
    int count = 0;
    size_t alen = strlen(answer);
    /* single-word hedges */
    for (size_t w = 0; w < sizeof(words) / sizeof(words[0]); ++w) {
        size_t wl = strlen(words[w]);
        for (size_t i = 0; i + wl <= alen; ++i) {
            /* left boundary */
            if (i > 0 && (isalnum((unsigned char)answer[i - 1]) || answer[i - 1] == '_')) continue;
            /* case-insensitive compare */
            bool eq = true;
            for (size_t k = 0; k < wl; ++k)
                if (tolower((unsigned char)answer[i + k]) != words[w][k]) { eq = false; break; }
            if (!eq) continue;
            /* right boundary */
            size_t after = i + wl;
            if (after < alen && (isalnum((unsigned char)answer[after]) || answer[after] == '_')) continue;
            count++;
        }
    }
    /* phrase "don't know" — \b matches at the apostrophe boundary; count case-insensitive
     * occurrences of the literal substring with a left word boundary before "don". */
    const char *phrase = "don't know";
    size_t pl = strlen(phrase);
    for (size_t i = 0; i + pl <= alen; ++i) {
        if (i > 0 && (isalnum((unsigned char)answer[i - 1]) || answer[i - 1] == '_')) continue;
        bool eq = true;
        for (size_t k = 0; k < pl; ++k)
            if (tolower((unsigned char)answer[i + k]) != phrase[k]) { eq = false; break; }
        if (eq) count++;
    }
    return count;
}

static double hj_raw_score(const char *answer, const char *context_json) {
    /* trimmed length */
    const char *s = answer;
    while (*s && isspace((unsigned char)*s)) s++;
    const char *e = answer + strlen(answer);
    while (e > s && isspace((unsigned char)e[-1])) e--;
    size_t len = (size_t)(e - s);
    if (len < 1) len = 1;
    int hedges = hj_count_hedges(answer);
    double hedge_penalty = hedges * 0.1;
    if (hedge_penalty > 0.5) hedge_penalty = 0.5;
    bool has_context = context_json && !hj_blank(context_json) && strlen(context_json) > 2;
    double v = (log((double)len) / 10.0) + (has_context ? 0.1 : 0.0) - hedge_penalty;
    return hj_clamp(v, 0.0, 1.0);
}

bool ca_calibrated_confidence_evaluate(const ca_calibrated_confidence_t *c,
                                       const char *answer, const char *context_json,
                                       ca_confidence_band_t *out) {
    if (!c || !answer || !out) return false;
    double raw = hj_raw_score(answer, context_json);
    double calibrated;
    if (c->count < 5) {
        calibrated = raw;
    } else {
        /* 5 nearest by |raw - h.raw|; stable order matches OrderBy (ascending
         * distance, preserving insertion order for ties). */
        size_t idx[5];
        double dist[5];
        int have = 0;
        for (size_t i = 0; i < c->count; ++i) {
            double d = fabs(c->hist[i].raw - raw);
            if (have < 5) {
                /* insert keeping ascending order */
                int pos = have;
                while (pos > 0 && dist[pos - 1] > d) { dist[pos] = dist[pos - 1]; idx[pos] = idx[pos - 1]; pos--; }
                dist[pos] = d; idx[pos] = i; have++;
            } else if (d < dist[4]) {
                int pos = 4;
                while (pos > 0 && dist[pos - 1] > d) { dist[pos] = dist[pos - 1]; idx[pos] = idx[pos - 1]; pos--; }
                dist[pos] = d; idx[pos] = i;
            }
        }
        int correct = 0;
        for (int i = 0; i < have; ++i) if (c->hist[idx[i]].correct) correct++;
        calibrated = (double)correct / have;
    }
    double half = 0.25 - calibrated * 0.2;
    if (half < 0.05) half = 0.05;   /* halfBand = Math.Max(0.05, ...) */
    /* new ConfidenceBand(Math.Max(0, calibrated - half), Math.Min(1, calibrated + half)) */
    double lo = calibrated - half; if (lo < 0.0) lo = 0.0;
    double hi = calibrated + half; if (hi > 1.0) hi = 1.0;
    out->lower = lo;
    out->upper = hi;
    return true;
}

/* =====================================================================
 * 11. KeywordEmotionSensor (stateless)
 * ===================================================================== */

void ca_emotion_frame_free(ca_emotion_frame_t *f) {
    if (!f) return;
    free(f->label); f->label = NULL;
}

/* Count word-boundary occurrences of any pipe-listed alternative (all ASCII,
 * case-insensitive), matching the C# @"\b(a|b|...)\b" patterns. */
static int hj_count_word_alts(const char *text, const char *const *alts, size_t n_alts) {
    int total = 0;
    size_t tlen = strlen(text);
    for (size_t a = 0; a < n_alts; ++a) {
        size_t wl = strlen(alts[a]);
        for (size_t i = 0; i + wl <= tlen; ++i) {
            if (i > 0 && (isalnum((unsigned char)text[i - 1]) || text[i - 1] == '_')) continue;
            bool eq = true;
            for (size_t k = 0; k < wl; ++k)
                if (tolower((unsigned char)text[i + k]) != alts[a][k]) { eq = false; break; }
            if (!eq) continue;
            size_t after = i + wl;
            if (after < tlen && (isalnum((unsigned char)text[after]) || text[after] == '_')) continue;
            total++;
        }
    }
    return total;
}

bool ca_emotion_sensor_sense(const char *fused_json, ca_emotion_frame_t *out) {
    if (!fused_json || !out) return false;

    static const char *joy[]      = { "happy", "joy", "delight", "excited", "love", "wonderful" };
    static const char *anger[]    = { "angry", "furious", "rage", "hate", "annoyed" };
    static const char *sad[]      = { "sad", "lonely", "grief", "cry", "depressed", "down" };
    static const char *fear[]     = { "afraid", "scared", "terrified", "anxious", "worried" };
    static const char *surprise[] = { "surprised", "amazed", "astonished", "wow" };
    static const char *calm[]     = { "calm", "peaceful", "relaxed", "content", "fine" };

    struct { const char *label; double arousal; double valence; const char *const *alts; size_t n; } pats[] = {
        { "joy",      0.8,  0.9, joy,      6 },
        { "anger",    0.9, -0.8, anger,    5 },
        { "sad",      0.3, -0.7, sad,      6 },
        { "fear",     0.85,-0.6, fear,     5 },
        { "surprise", 0.7,  0.3, surprise, 4 },
        { "calm",     0.1,  0.5, calm,     5 },
    };

    int counts[6];
    int total_weight = 0;
    double aw = 0, vw = 0;
    int best_i = -1, best_count = 0;
    for (int i = 0; i < 6; ++i) {
        counts[i] = hj_count_word_alts(fused_json, pats[i].alts, pats[i].n);
        if (counts[i] > 0) {
            total_weight += counts[i];
            aw += pats[i].arousal * counts[i];
            vw += pats[i].valence * counts[i];
            if (counts[i] > best_count) { best_count = counts[i]; best_i = i; }
        }
    }
    if (total_weight == 0) {
        out->label = hj_strdup("neutral");
        out->arousal = 0.0; out->valence = 0.0;
        return true;
    }
    out->label = hj_strdup(pats[best_i].label);
    out->arousal = aw / total_weight;
    out->valence = vw / total_weight;
    return true;
}

/* =====================================================================
 * 12. DemoStoreSkillAcquisition
 * ===================================================================== */

void ca_acquired_skill_free(ca_acquired_skill_t *s) {
    if (!s) return;
    free(s->id); free(s->name); free(s->description_json);
    s->id = s->name = s->description_json = NULL;
}
void ca_acquired_skill_free_array(ca_acquired_skill_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_acquired_skill_free(&arr[i]);
    free(arr);
}

struct ca_skill_acquisition {
    ca_acquired_skill_t *skills;
    size_t               count, cap;
    uint64_t             counter;
};

ca_skill_acquisition_t *ca_skill_acquisition_create(void) {
    return (ca_skill_acquisition_t *)calloc(1, sizeof(ca_skill_acquisition_t));
}
void ca_skill_acquisition_destroy(ca_skill_acquisition_t *sa) {
    if (!sa) return;
    for (size_t i = 0; i < sa->count; ++i) ca_acquired_skill_free(&sa->skills[i]);
    free(sa->skills);
    free(sa);
}

/* Extract a top-level JSON string field "name" (very small parser: find
 * "name" key, expect ':' then a quoted string). Returns malloc'd value or NULL.
 * Matches the C#'s JsonDocument string-field lookup for the common shapes used;
 * unescapes \" and \\ minimally. */
static char *hj_json_find_string(const char *json, const char *key) {
    if (!json) return NULL;
    size_t klen = strlen(key);
    const char *p = json;
    while ((p = strchr(p, '"')) != NULL) {
        const char *ks = p + 1;
        if (strncmp(ks, key, klen) == 0 && ks[klen] == '"') {
            const char *q = ks + klen + 1;
            while (*q && isspace((unsigned char)*q)) q++;
            if (*q != ':') { p = ks; continue; }
            q++;
            while (*q && isspace((unsigned char)*q)) q++;
            if (*q != '"') return NULL;   /* value not a string */
            q++;
            hj_sb sb = {0};
            while (*q && *q != '"') {
                if (*q == '\\' && q[1]) {
                    q++;
                    switch (*q) {
                        case 'n': hj_sb_append_char(&sb, '\n'); break;
                        case 't': hj_sb_append_char(&sb, '\t'); break;
                        case 'r': hj_sb_append_char(&sb, '\r'); break;
                        case '"': hj_sb_append_char(&sb, '"'); break;
                        case '\\': hj_sb_append_char(&sb, '\\'); break;
                        case '/': hj_sb_append_char(&sb, '/'); break;
                        default: hj_sb_append_char(&sb, *q); break;
                    }
                    q++;
                } else {
                    hj_sb_append_char(&sb, *q++);
                }
            }
            return sb.buf ? sb.buf : hj_strdup("");
        }
        p = p + 1;
    }
    return NULL;
}

bool ca_skill_acquisition_acquire(ca_skill_acquisition_t *sa,
                                  const char *demonstration_json,
                                  ca_acquired_skill_t *out) {
    if (!sa || !demonstration_json || !out) return false;
    char id[33];
    hj_make_id(sa->counter++, id);
    char *name = hj_json_find_string(demonstration_json, "name");
    if (!name) {
        char buf[16];
        snprintf(buf, sizeof(buf), "skill-%.6s", id);
        name = hj_strdup(buf);
    }
    if (sa->count == sa->cap) {
        size_t nc = sa->cap ? sa->cap * 2 : 8;
        ca_acquired_skill_t *ns = (ca_acquired_skill_t *)realloc(sa->skills, nc * sizeof(*ns));
        if (!ns) { free(name); return false; }
        sa->skills = ns; sa->cap = nc;
    }
    ca_acquired_skill_t *sk = &sa->skills[sa->count++];
    sk->id = hj_strdup(id);
    sk->name = name;
    sk->description_json = hj_strdup(demonstration_json);
    out->id = hj_strdup(sk->id);
    out->name = hj_strdup(sk->name);
    out->description_json = hj_strdup(sk->description_json);
    return true;
}

static int hj_skill_cmp(const void *a, const void *b) {
    const ca_acquired_skill_t *x = (const ca_acquired_skill_t *)a;
    const ca_acquired_skill_t *y = (const ca_acquired_skill_t *)b;
    return strcmp(x->name, y->name);   /* OrderBy(s => s.Name), Ordinal */
}

ca_acquired_skill_t *ca_skill_acquisition_list(const ca_skill_acquisition_t *sa,
                                               size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!sa || sa->count == 0) return NULL;
    ca_acquired_skill_t *arr = (ca_acquired_skill_t *)calloc(sa->count, sizeof(*arr));
    if (!arr) { if (out_count) *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < sa->count; ++i) {
        arr[i].id = hj_strdup(sa->skills[i].id);
        arr[i].name = hj_strdup(sa->skills[i].name);
        arr[i].description_json = hj_strdup(sa->skills[i].description_json);
    }
    qsort(arr, sa->count, sizeof(*arr), hj_skill_cmp);
    if (out_count) *out_count = sa->count;
    return arr;
}

/* =====================================================================
 * 17. ChannelBioSignalStream
 * ===================================================================== */

void ca_bio_signal_free(ca_bio_signal_t *s) {
    if (!s) return;
    free(s->kind); s->kind = NULL;
}

struct ca_bio_signal_stream {
    hj_queue q;         /* of ca_bio_signal_t (owned copies) */
    bool     completed;
};

ca_bio_signal_stream_t *ca_bio_signal_stream_create(void) {
    ca_bio_signal_stream_t *bs = (ca_bio_signal_stream_t *)calloc(1, sizeof(*bs));
    if (bs) hj_queue_init(&bs->q, sizeof(ca_bio_signal_t));
    return bs;
}
void ca_bio_signal_stream_destroy(ca_bio_signal_stream_t *bs) {
    if (!bs) return;
    ca_bio_signal_t tmp;
    while (hj_queue_pop(&bs->q, &tmp)) ca_bio_signal_free(&tmp);
    free(bs->q.items);
    free(bs);
}
void ca_bio_signal_stream_publish(ca_bio_signal_stream_t *bs, const ca_bio_signal_t *s) {
    if (!bs || !s || bs->completed) return;
    ca_bio_signal_t copy;
    copy.kind = hj_strdup(s->kind);
    copy.value = s->value;
    copy.at_ms = s->at_ms;
    if (!hj_queue_push(&bs->q, &copy)) ca_bio_signal_free(&copy);
}
void ca_bio_signal_stream_complete(ca_bio_signal_stream_t *bs) {
    if (bs) bs->completed = true;
}
bool ca_bio_signal_stream_read(ca_bio_signal_stream_t *bs, ca_bio_signal_t *out) {
    if (!bs || !out) return false;
    return hj_queue_pop(&bs->q, out);
}

/* =====================================================================
 * 18. RegistryPhysicalActuator
 * ===================================================================== */

void ca_physical_command_result_free(ca_physical_command_result_t *r) {
    if (!r) return;
    free(r->error); r->error = NULL;
}

typedef struct { char *device_id; ca_physical_device_handler_fn handler; void *user; } hj_device;

struct ca_physical_actuator {
    hj_device *devices;
    size_t     count, cap;
};

ca_physical_actuator_t *ca_physical_actuator_create(void) {
    return (ca_physical_actuator_t *)calloc(1, sizeof(ca_physical_actuator_t));
}
void ca_physical_actuator_destroy(ca_physical_actuator_t *a) {
    if (!a) return;
    for (size_t i = 0; i < a->count; ++i) free(a->devices[i].device_id);
    free(a->devices);
    free(a);
}
void ca_physical_actuator_register(ca_physical_actuator_t *a, const char *device_id,
                                   ca_physical_device_handler_fn handler, void *user) {
    if (!a || hj_blank(device_id) || !handler) return;
    for (size_t i = 0; i < a->count; ++i)
        if (strcmp(a->devices[i].device_id, device_id) == 0) {
            a->devices[i].handler = handler; a->devices[i].user = user; return;
        }
    if (a->count == a->cap) {
        size_t nc = a->cap ? a->cap * 2 : 8;
        hj_device *nd = (hj_device *)realloc(a->devices, nc * sizeof(*nd));
        if (!nd) return;
        a->devices = nd; a->cap = nc;
    }
    a->devices[a->count].device_id = hj_strdup(device_id);
    a->devices[a->count].handler = handler;
    a->devices[a->count].user = user;
    a->count++;
}
bool ca_physical_actuator_invoke(const ca_physical_actuator_t *a,
                                 const ca_physical_command_t *cmd,
                                 ca_physical_command_result_t *out) {
    if (!a || !cmd || !out) return false;
    out->succeeded = false; out->error = NULL;
    for (size_t i = 0; i < a->count; ++i) {
        if (cmd->device_id && strcmp(a->devices[i].device_id, cmd->device_id) == 0) {
            a->devices[i].handler(a->devices[i].user, cmd, out);
            return true;
        }
    }
    char buf[128];
    snprintf(buf, sizeof(buf), "Unknown device '%s'", cmd->device_id ? cmd->device_id : "");
    out->succeeded = false;
    out->error = hj_strdup(buf);
    return true;
}

/* =====================================================================
 * 19. MailboxAgentPeerNetwork
 * ===================================================================== */

void ca_agent_peer_message_free(ca_agent_peer_message_t *m) {
    if (!m) return;
    free(m->from_agent_id); free(m->to_agent_id); free(m->payload);
    m->from_agent_id = m->to_agent_id = m->payload = NULL;
}

typedef struct { char *agent_id; hj_queue box; } hj_mailbox;

struct ca_agent_peer_network {
    hj_mailbox *boxes;
    size_t      count, cap;
};

ca_agent_peer_network_t *ca_agent_peer_network_create(void) {
    return (ca_agent_peer_network_t *)calloc(1, sizeof(ca_agent_peer_network_t));
}
void ca_agent_peer_network_destroy(ca_agent_peer_network_t *n) {
    if (!n) return;
    for (size_t i = 0; i < n->count; ++i) {
        ca_agent_peer_message_t tmp;
        while (hj_queue_pop(&n->boxes[i].box, &tmp)) ca_agent_peer_message_free(&tmp);
        free(n->boxes[i].box.items);
        free(n->boxes[i].agent_id);
    }
    free(n->boxes);
    free(n);
}
static hj_mailbox *hj_mailbox_get(ca_agent_peer_network_t *n, const char *agent_id) {
    for (size_t i = 0; i < n->count; ++i)
        if (strcmp(n->boxes[i].agent_id, agent_id) == 0) return &n->boxes[i];
    if (n->count == n->cap) {
        size_t nc = n->cap ? n->cap * 2 : 8;
        hj_mailbox *nb = (hj_mailbox *)realloc(n->boxes, nc * sizeof(*nb));
        if (!nb) return NULL;
        n->boxes = nb; n->cap = nc;
    }
    n->boxes[n->count].agent_id = hj_strdup(agent_id);
    hj_queue_init(&n->boxes[n->count].box, sizeof(ca_agent_peer_message_t));
    return &n->boxes[n->count++];
}
void ca_agent_peer_network_send(ca_agent_peer_network_t *n, const ca_agent_peer_message_t *m) {
    if (!n || !m || !m->to_agent_id) return;
    hj_mailbox *box = hj_mailbox_get(n, m->to_agent_id);
    if (!box) return;
    ca_agent_peer_message_t copy;
    copy.from_agent_id = hj_strdup(m->from_agent_id);
    copy.to_agent_id = hj_strdup(m->to_agent_id);
    copy.payload = hj_strdup(m->payload);
    copy.at_ms = m->at_ms;
    if (!hj_queue_push(&box->box, &copy)) ca_agent_peer_message_free(&copy);
}
bool ca_agent_peer_network_receive(ca_agent_peer_network_t *n, const char *for_agent_id,
                                   ca_agent_peer_message_t *out) {
    if (!n || hj_blank(for_agent_id) || !out) return false;
    hj_mailbox *box = hj_mailbox_get(n, for_agent_id);
    if (!box) return false;
    return hj_queue_pop(&box->box, out);
}

/* =====================================================================
 * 20. InMemoryFederatedFineTuner
 * ===================================================================== */

void ca_finetune_status_free(ca_finetune_status_t *s) {
    if (!s) return;
    free(s->job_id); free(s->error);
    s->job_id = s->error = NULL;
}

typedef struct { char *job_id; double progress; char *error; } hj_finetune_job;

struct ca_federated_finetuner {
    ca_finetune_trainer_fn trainer;
    void                  *trainer_user;
    hj_finetune_job       *jobs;
    size_t                 count, cap;
    uint64_t               counter;
};

/* progress sink: bound to a specific job slot */
static void hj_finetune_report(void *sink, double progress) {
    hj_finetune_job *job = (hj_finetune_job *)sink;
    job->progress = hj_clamp(progress, 0.0, 1.0);
}

/* Default trainer: read line count if the file exists (else 100 steps), report
 * steady progress, finish at 1.0. Never fails. */
static char *hj_default_trainer(void *user, const char *base_model,
                                const char *training_data_path,
                                ca_finetune_progress_fn report, void *sink) {
    (void)user; (void)base_model;
    long lines = 100;
    FILE *f = fopen(training_data_path, "rb");
    if (f) {
        lines = 0;
        int c, prev = '\n';
        while ((c = fgetc(f)) != EOF) { if (c == '\n') lines++; prev = c; }
        if (prev != '\n' && lines >= 0) lines++;   /* count a trailing partial line */
        if (lines == 0) lines = 1;
        fclose(f);
    }
    double step = 1.0 / (lines < 1 ? 1 : lines);
    for (long i = 0; i < lines; ++i) report(sink, i * step);
    report(sink, 1.0);
    return NULL;
}

ca_federated_finetuner_t *ca_federated_finetuner_create(ca_finetune_trainer_fn trainer,
                                                        void *trainer_user) {
    ca_federated_finetuner_t *ft = (ca_federated_finetuner_t *)calloc(1, sizeof(*ft));
    if (!ft) return NULL;
    ft->trainer = trainer ? trainer : hj_default_trainer;
    ft->trainer_user = trainer ? trainer_user : NULL;
    return ft;
}
void ca_federated_finetuner_destroy(ca_federated_finetuner_t *ft) {
    if (!ft) return;
    for (size_t i = 0; i < ft->count; ++i) { free(ft->jobs[i].job_id); free(ft->jobs[i].error); }
    free(ft->jobs);
    free(ft);
}
char *ca_federated_finetuner_start(ca_federated_finetuner_t *ft,
                                   const char *base_model, const char *training_data_path) {
    if (!ft || hj_blank(base_model) || hj_blank(training_data_path)) return NULL;
    if (ft->count == ft->cap) {
        size_t nc = ft->cap ? ft->cap * 2 : 8;
        hj_finetune_job *nj = (hj_finetune_job *)realloc(ft->jobs, nc * sizeof(*nj));
        if (!nj) return NULL;
        ft->jobs = nj; ft->cap = nc;
    }
    char id[33];
    hj_make_id(ft->counter++, id);
    hj_finetune_job *job = &ft->jobs[ft->count++];
    job->job_id = hj_strdup(id);
    job->progress = 0.0;
    job->error = NULL;
    /* Run the trainer to completion synchronously (no threads). */
    char *err = ft->trainer(ft->trainer_user, base_model, training_data_path,
                            hj_finetune_report, job);
    if (err) {
        job->error = err;   /* takes ownership; progress stays where the trainer left it */
    } else {
        job->progress = 1.0;
        job->error = NULL;
    }
    return hj_strdup(id);
}
bool ca_federated_finetuner_status(const ca_federated_finetuner_t *ft,
                                   const char *job_id, ca_finetune_status_t *out) {
    if (!ft || !job_id || !out) return false;
    for (size_t i = 0; i < ft->count; ++i) {
        if (strcmp(ft->jobs[i].job_id, job_id) == 0) {
            out->job_id = hj_strdup(job_id);
            out->progress = ft->jobs[i].progress;
            out->error = ft->jobs[i].error ? hj_strdup(ft->jobs[i].error) : NULL;
            return true;
        }
    }
    out->job_id = hj_strdup(job_id);
    out->progress = 0.0;
    out->error = hj_strdup("unknown job");
    return true;
}

/* =====================================================================
 * 21. SlidingP50FirstTokenOptimizer
 * ===================================================================== */

struct ca_first_token_optimizer {
    int   target_ms;
    int   window_size;
    int  *samples;      /* ring buffer */
    size_t head, count, cap;
};

ca_first_token_optimizer_t *ca_first_token_optimizer_create(int target_ms, int window_size) {
    if (target_ms <= 0 || window_size <= 0) return NULL;
    ca_first_token_optimizer_t *o = (ca_first_token_optimizer_t *)calloc(1, sizeof(*o));
    if (!o) return NULL;
    o->target_ms = target_ms;
    o->window_size = window_size;
    o->samples = (int *)malloc((size_t)window_size * sizeof(int));
    if (!o->samples) { free(o); return NULL; }
    o->cap = (size_t)window_size;
    return o;
}
void ca_first_token_optimizer_destroy(ca_first_token_optimizer_t *o) {
    if (!o) return;
    free(o->samples);
    free(o);
}
void ca_first_token_optimizer_record(ca_first_token_optimizer_t *o, int ms) {
    if (!o || ms < 0) return;
    if (o->count < o->cap) {
        o->samples[(o->head + o->count) % o->cap] = ms;
        o->count++;
    } else {
        o->samples[o->head] = ms;
        o->head = (o->head + 1) % o->cap;
    }
}
static int hj_int_cmp(const void *a, const void *b) {
    int x = *(const int *)a, y = *(const int *)b;
    return (x > y) - (x < y);
}
bool ca_first_token_optimizer_current(const ca_first_token_optimizer_t *o,
                                      ca_first_token_budget_t *out) {
    if (!o || !out) return false;
    int p50 = 0;
    if (o->count > 0) {
        int *sorted = (int *)malloc(o->count * sizeof(int));
        if (!sorted) return false;
        for (size_t i = 0; i < o->count; ++i) sorted[i] = o->samples[(o->head + i) % o->cap];
        qsort(sorted, o->count, sizeof(int), hj_int_cmp);
        p50 = sorted[o->count / 2];
        free(sorted);
    }
    out->target_ms = o->target_ms;
    out->current_p50_ms = p50;
    return true;
}

/* =====================================================================
 * 22. Crypto delegation (HMAC-SHA256 sign + verify)
 * =====================================================================
 *
 * Self-contained FIPS 180-4 SHA-256 + HMAC-SHA256 (RFC 2104). Signs the same
 * canonical "issuer|subject|scope|expiresISO" as the C# ECDSA path.
 */

typedef struct { uint32_t h[8]; uint64_t len; uint8_t buf[64]; size_t buf_len; } hj_sha_ctx;
static uint32_t hj_rotr(uint32_t x, unsigned n) { return (x >> n) | (x << (32 - n)); }
static const uint32_t HJ_SHA_K[64] = {
    0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
    0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
    0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
    0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
    0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
    0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
    0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
    0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2 };
static void hj_sha_init(hj_sha_ctx *c) {
    c->h[0]=0x6a09e667;c->h[1]=0xbb67ae85;c->h[2]=0x3c6ef372;c->h[3]=0xa54ff53a;
    c->h[4]=0x510e527f;c->h[5]=0x9b05688c;c->h[6]=0x1f83d9ab;c->h[7]=0x5be0cd19;
    c->len=0;c->buf_len=0;
}
static void hj_sha_block(hj_sha_ctx *c, const uint8_t *p) {
    uint32_t w[64];
    for (int i=0;i<16;++i)
        w[i]=((uint32_t)p[i*4]<<24)|((uint32_t)p[i*4+1]<<16)|((uint32_t)p[i*4+2]<<8)|(uint32_t)p[i*4+3];
    for (int i=16;i<64;++i){
        uint32_t s0=hj_rotr(w[i-15],7)^hj_rotr(w[i-15],18)^(w[i-15]>>3);
        uint32_t s1=hj_rotr(w[i-2],17)^hj_rotr(w[i-2],19)^(w[i-2]>>10);
        w[i]=w[i-16]+s0+w[i-7]+s1;
    }
    uint32_t a=c->h[0],b=c->h[1],cc=c->h[2],d=c->h[3],e=c->h[4],f=c->h[5],g=c->h[6],hh=c->h[7];
    for (int i=0;i<64;++i){
        uint32_t S1=hj_rotr(e,6)^hj_rotr(e,11)^hj_rotr(e,25);
        uint32_t ch=(e&f)^(~e&g);
        uint32_t t1=hh+S1+ch+HJ_SHA_K[i]+w[i];
        uint32_t S0=hj_rotr(a,2)^hj_rotr(a,13)^hj_rotr(a,22);
        uint32_t maj=(a&b)^(a&cc)^(b&cc);
        uint32_t t2=S0+maj;
        hh=g;g=f;f=e;e=d+t1;d=cc;cc=b;b=a;a=t1+t2;
    }
    c->h[0]+=a;c->h[1]+=b;c->h[2]+=cc;c->h[3]+=d;c->h[4]+=e;c->h[5]+=f;c->h[6]+=g;c->h[7]+=hh;
}
static void hj_sha_update(hj_sha_ctx *c, const uint8_t *data, size_t len) {
    c->len += len;
    while (len) {
        size_t take = 64 - c->buf_len;
        if (take > len) take = len;
        memcpy(c->buf + c->buf_len, data, take);
        c->buf_len += take; data += take; len -= take;
        if (c->buf_len == 64) { hj_sha_block(c, c->buf); c->buf_len = 0; }
    }
}
static void hj_sha_final(hj_sha_ctx *c, uint8_t out[32]) {
    uint64_t bits = c->len * 8;
    uint8_t pad = 0x80; hj_sha_update(c, &pad, 1);
    uint8_t zero = 0;
    while (c->buf_len != 56) hj_sha_update(c, &zero, 1);
    uint8_t lb[8];
    for (int i=0;i<8;++i) lb[i]=(uint8_t)(bits >> (56 - i*8));
    hj_sha_update(c, lb, 8);
    for (int i=0;i<8;++i){ out[i*4]=(uint8_t)(c->h[i]>>24);out[i*4+1]=(uint8_t)(c->h[i]>>16);
        out[i*4+2]=(uint8_t)(c->h[i]>>8);out[i*4+3]=(uint8_t)c->h[i]; }
}
static void hj_hmac_sha256(const uint8_t *key, size_t key_len,
                           const uint8_t *msg, size_t msg_len, uint8_t out[32]) {
    uint8_t k[64]; memset(k, 0, 64);
    if (key_len > 64) { hj_sha_ctx c; hj_sha_init(&c); hj_sha_update(&c, key, key_len); hj_sha_final(&c, k); }
    else memcpy(k, key, key_len);
    uint8_t ipad[64], opad[64];
    for (int i=0;i<64;++i){ ipad[i]=k[i]^0x36; opad[i]=k[i]^0x5c; }
    uint8_t inner[32];
    hj_sha_ctx c;
    hj_sha_init(&c); hj_sha_update(&c, ipad, 64); hj_sha_update(&c, msg, msg_len); hj_sha_final(&c, inner);
    hj_sha_init(&c); hj_sha_update(&c, opad, 64); hj_sha_update(&c, inner, 32); hj_sha_final(&c, out);
}

void ca_delegation_credential_free(ca_delegation_credential_t *c) {
    if (!c) return;
    free(c->issuer); free(c->subject_id); free(c->scope); free(c->signature_b64);
    c->issuer = c->subject_id = c->scope = c->signature_b64 = NULL;
}

struct ca_crypto_delegation {
    char    *issuer;
    uint8_t *key;
    size_t   key_len;
};

/* Fixed internal key when the host injects none (deterministic). */
static const uint8_t HJ_DEFAULT_KEY[32] = {
    0x43,0x69,0x72,0x63,0x6c,0x65,0x41,0x49,0x2d,0x64,0x65,0x6c,0x65,0x67,0x61,0x74,
    0x69,0x6f,0x6e,0x2d,0x68,0x6d,0x61,0x63,0x2d,0x6b,0x65,0x79,0x2d,0x76,0x31,0x00 };

ca_crypto_delegation_t *ca_crypto_delegation_create(const char *issuer,
                                                    const uint8_t *key, size_t key_len) {
    ca_crypto_delegation_t *d = (ca_crypto_delegation_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->issuer = hj_strdup(hj_blank(issuer) ? "circleai-companion" : issuer);
    if (key && key_len) {
        d->key = (uint8_t *)malloc(key_len);
        if (!d->key) { free(d->issuer); free(d); return NULL; }
        memcpy(d->key, key, key_len);
        d->key_len = key_len;
    } else {
        d->key = (uint8_t *)malloc(sizeof(HJ_DEFAULT_KEY));
        if (!d->key) { free(d->issuer); free(d); return NULL; }
        memcpy(d->key, HJ_DEFAULT_KEY, sizeof(HJ_DEFAULT_KEY));
        d->key_len = sizeof(HJ_DEFAULT_KEY);
    }
    return d;
}
void ca_crypto_delegation_destroy(ca_crypto_delegation_t *d) {
    if (!d) return;
    free(d->issuer); free(d->key);
    free(d);
}

/* canonical "issuer|subject|scope|expiresISO" */
static char *hj_delegation_canonical(const char *issuer, const char *subject,
                                     const char *scope, int64_t expires_ms) {
    char iso[48]; hj_iso8601(expires_ms, iso);
    hj_sb sb = {0};
    hj_sb_append(&sb, issuer);   hj_sb_append_char(&sb, '|');
    hj_sb_append(&sb, subject);  hj_sb_append_char(&sb, '|');
    hj_sb_append(&sb, scope);    hj_sb_append_char(&sb, '|');
    hj_sb_append(&sb, iso);
    return sb.buf ? sb.buf : hj_strdup("");
}

bool ca_crypto_delegation_issue(const ca_crypto_delegation_t *d,
                                const char *subject_id, const char *scope,
                                int64_t lifetime_ms, int64_t now_ms,
                                ca_delegation_credential_t *out) {
    if (!d || !out || hj_blank(subject_id) || hj_blank(scope)) return false;
    if (lifetime_ms <= 0) return false;
    int64_t expires = now_ms + lifetime_ms;
    char *canon = hj_delegation_canonical(d->issuer, subject_id, scope, expires);
    if (!canon) return false;
    uint8_t mac[32];
    hj_hmac_sha256(d->key, d->key_len, (const uint8_t *)canon, strlen(canon), mac);
    free(canon);
    char *b64 = ca_base64_encode(mac, 32);
    out->issuer = hj_strdup(d->issuer);
    out->subject_id = hj_strdup(subject_id);
    out->scope = hj_strdup(scope);
    out->expires_at_ms = expires;
    out->signature_b64 = b64 ? b64 : hj_strdup("");
    return true;
}
bool ca_crypto_delegation_verify(const ca_crypto_delegation_t *d,
                                 const ca_delegation_credential_t *cred, int64_t now_ms) {
    if (!d || !cred) return false;
    if (!cred->issuer || strcmp(cred->issuer, d->issuer) != 0) return false;
    if (cred->expires_at_ms <= now_ms) return false;
    if (hj_blank(cred->signature_b64)) return false;
    size_t sig_len = 0;
    uint8_t *sig = ca_base64_decode(cred->signature_b64, &sig_len);
    if (!sig) return false;
    char *canon = hj_delegation_canonical(d->issuer, cred->subject_id ? cred->subject_id : "",
                                          cred->scope ? cred->scope : "", cred->expires_at_ms);
    uint8_t mac[32];
    hj_hmac_sha256(d->key, d->key_len, (const uint8_t *)canon, strlen(canon), mac);
    free(canon);
    bool ok = (sig_len == 32) && (memcmp(sig, mac, 32) == 0);
    free(sig);
    return ok;
}

/* =====================================================================
 * 23. SyntaxCheckingCodeGenerationLoop
 * ===================================================================== */

void ca_codegen_job_free(ca_codegen_job_t *j) {
    if (!j) return;
    free(j->id); free(j->prompt); free(j->output_snippet); free(j->deploy_hint);
    j->id = j->prompt = j->output_snippet = j->deploy_hint = NULL;
}

bool ca_code_is_syntactically_balanced(const char *snippet) {
    if (!snippet || !*snippet) return false;
    int curly = 0, paren = 0, square = 0;
    for (const char *p = snippet; *p; ++p) {
        switch (*p) {
            case '{': curly++; break;  case '}': curly--; break;
            case '(': paren++; break;  case ')': paren--; break;
            case '[': square++; break; case ']': square--; break;
            default: break;
        }
        if (curly < 0 || paren < 0 || square < 0) return false;
    }
    return curly == 0 && paren == 0 && square == 0;
}

static char *hj_default_generator(void *user, const char *prompt) {
    (void)user;
    /* "(3.3.0) generated from: <prompt with newlines→spaces>\nreturn 0;" */
    hj_sb sb = {0};
    hj_sb_append(&sb, "// (3.3.0) generated from: ");
    for (const char *p = prompt; p && *p; ++p) hj_sb_append_char(&sb, *p == '\n' ? ' ' : *p);
    hj_sb_append(&sb, "\nreturn 0;");
    return sb.buf ? sb.buf : hj_strdup("");
}
static bool hj_default_test_runner(void *user, const char *snippet) {
    (void)user;
    return ca_code_is_syntactically_balanced(snippet);
}
static char *hj_default_deploy_hint(void *user, const char *snippet) {
    (void)user;
    return hj_strdup(strstr(snippet, "public class") ? "stage as nuget" : "run inline");
}

struct ca_code_generation_loop {
    ca_codegen_generator_fn   generator; void *generator_user;
    ca_codegen_test_runner_fn test_runner; void *test_runner_user;
    ca_codegen_deploy_hint_fn deploy_hint; void *deploy_hint_user;
    uint64_t counter;
};

ca_code_generation_loop_t *ca_code_generation_loop_create(
    ca_codegen_generator_fn generator, void *generator_user,
    ca_codegen_test_runner_fn test_runner, void *test_runner_user,
    ca_codegen_deploy_hint_fn deploy_hint, void *deploy_hint_user) {
    ca_code_generation_loop_t *l = (ca_code_generation_loop_t *)calloc(1, sizeof(*l));
    if (!l) return NULL;
    l->generator = generator ? generator : hj_default_generator;
    l->generator_user = generator ? generator_user : NULL;
    l->test_runner = test_runner ? test_runner : hj_default_test_runner;
    l->test_runner_user = test_runner ? test_runner_user : NULL;
    l->deploy_hint = deploy_hint ? deploy_hint : hj_default_deploy_hint;
    l->deploy_hint_user = deploy_hint ? deploy_hint_user : NULL;
    return l;
}
void ca_code_generation_loop_destroy(ca_code_generation_loop_t *l) { free(l); }

bool ca_code_generation_loop_run(ca_code_generation_loop_t *l, const char *prompt,
                                 ca_codegen_job_t *out) {
    if (!l || !out || hj_blank(prompt)) return false;
    char id[33];
    hj_make_id(l->counter++, id);
    char *snippet = l->generator(l->generator_user, prompt);
    if (!snippet) snippet = hj_strdup("");
    bool parses = ca_code_is_syntactically_balanced(snippet);
    bool tests_ok = parses && l->test_runner(l->test_runner_user, snippet);
    out->id = hj_strdup(id);
    out->prompt = hj_strdup(prompt);
    out->output_snippet = snippet;   /* takes ownership */
    out->tests_pass = tests_ok;
    out->deploy_hint = tests_ok ? l->deploy_hint(l->deploy_hint_user, snippet) : NULL;
    return true;
}

/* =====================================================================
 * 24a. TrackingSelfImprovementLoop
 * ===================================================================== */

void ca_self_improvement_verdict_free(ca_self_improvement_verdict_t *v) {
    if (!v) return;
    free(v->improvements_applied); v->improvements_applied = NULL;
}

typedef struct { char *suite; double score; } hj_best_score;

struct ca_self_improvement_loop {
    ca_selfimprove_bench_fn   bench; void *bench_user;
    ca_selfimprove_propose_fn propose; void *propose_user;
    hj_best_score *best; size_t count, cap;
};

/* default bench: 0.5 + (hash(id) & 0xFFFF)/65535*0.5 — deterministic per id. */
static double hj_default_bench(void *user, const char *bench_suite_id) {
    (void)user;
    /* FNV-1a 32-bit hash, low 16 bits — deterministic (C# used GetHashCode which
     * is randomised; we use a fixed hash so the C default is reproducible). */
    uint32_t h = 2166136261u;
    for (const unsigned char *p = (const unsigned char *)bench_suite_id; p && *p; ++p) {
        h ^= *p; h *= 16777619u;
    }
    return 0.5 + (double)(h & 0xFFFF) / 65535.0 * 0.5;
}
static char *hj_default_propose(void *user, const char *bench_suite_id, double current) {
    (void)user; (void)bench_suite_id;
    char buf[80];
    /* mirror "retry-with-temperature-0 (score was {current:F3})" */
    snprintf(buf, sizeof(buf), "retry-with-temperature-0 (score was %.3f)", current);
    return hj_strdup(buf);
}

ca_self_improvement_loop_t *ca_self_improvement_loop_create(
    ca_selfimprove_bench_fn bench, void *bench_user,
    ca_selfimprove_propose_fn propose, void *propose_user) {
    ca_self_improvement_loop_t *l = (ca_self_improvement_loop_t *)calloc(1, sizeof(*l));
    if (!l) return NULL;
    l->bench = bench ? bench : hj_default_bench;
    l->bench_user = bench ? bench_user : NULL;
    l->propose = propose ? propose : hj_default_propose;
    l->propose_user = propose ? propose_user : NULL;
    return l;
}
void ca_self_improvement_loop_destroy(ca_self_improvement_loop_t *l) {
    if (!l) return;
    for (size_t i = 0; i < l->count; ++i) free(l->best[i].suite);
    free(l->best);
    free(l);
}
static hj_best_score *hj_best_find(ca_self_improvement_loop_t *l, const char *suite) {
    for (size_t i = 0; i < l->count; ++i)
        if (strcmp(l->best[i].suite, suite) == 0) return &l->best[i];
    return NULL;
}
static void hj_best_set(ca_self_improvement_loop_t *l, const char *suite, double score) {
    hj_best_score *b = hj_best_find(l, suite);
    if (b) { b->score = score; return; }
    if (l->count == l->cap) {
        size_t nc = l->cap ? l->cap * 2 : 8;
        hj_best_score *nb = (hj_best_score *)realloc(l->best, nc * sizeof(*nb));
        if (!nb) return;
        l->best = nb; l->cap = nc;
    }
    l->best[l->count].suite = hj_strdup(suite);
    l->best[l->count].score = score;
    l->count++;
}
bool ca_self_improvement_loop_cycle(ca_self_improvement_loop_t *l,
                                    const char *bench_suite_id,
                                    ca_self_improvement_verdict_t *out) {
    if (!l || !out || hj_blank(bench_suite_id)) return false;
    hj_best_score *b = hj_best_find(l, bench_suite_id);
    double baseline = b ? b->score : 0.0;
    double current = l->bench(l->bench_user, bench_suite_id);
    char *applied;
    if (current >= baseline) {
        hj_best_set(l, bench_suite_id, current);
        applied = hj_strdup(current > baseline ? "new best" : "no regression");
    } else {
        applied = l->propose(l->propose_user, bench_suite_id, current);
        if (!applied) applied = hj_strdup("none");
    }
    out->improvements_applied = applied;
    out->new_bench_score = current;
    return true;
}
double ca_self_improvement_loop_best_score(const ca_self_improvement_loop_t *l,
                                           const char *bench_suite_id) {
    if (!l || !bench_suite_id) return 0.0;
    hj_best_score *b = hj_best_find((ca_self_improvement_loop_t *)l, bench_suite_id);
    return b ? b->score : 0.0;
}

/* =====================================================================
 * 24b. SelfBenchSelfImprovementLoop
 * ===================================================================== */

void ca_ab_verdict_free(ca_ab_verdict_t *v) {
    if (!v) return;
    free(v->reason); v->reason = NULL;
}

struct ca_selfbench_improvement_loop {
    ca_selfbench_suite_count_fn suite_count; void *suite_count_user;
    ca_selfbench_ab_run_fn      ab_run; void *ab_run_user;
    ca_selfbench_promote_fn     promote; void *promote_user;
    hj_best_score *best; size_t count, cap;
};

ca_selfbench_improvement_loop_t *ca_selfbench_improvement_loop_create(
    ca_selfbench_suite_count_fn suite_count, void *suite_count_user,
    ca_selfbench_ab_run_fn ab_run, void *ab_run_user,
    ca_selfbench_promote_fn promote, void *promote_user) {
    if (!suite_count || !ab_run) return NULL;
    ca_selfbench_improvement_loop_t *l =
        (ca_selfbench_improvement_loop_t *)calloc(1, sizeof(*l));
    if (!l) return NULL;
    l->suite_count = suite_count; l->suite_count_user = suite_count_user;
    l->ab_run = ab_run; l->ab_run_user = ab_run_user;
    l->promote = promote; l->promote_user = promote_user;
    return l;
}
void ca_selfbench_improvement_loop_destroy(ca_selfbench_improvement_loop_t *l) {
    if (!l) return;
    for (size_t i = 0; i < l->count; ++i) free(l->best[i].suite);
    free(l->best);
    free(l);
}
static hj_best_score *hj_sb_best_find(ca_selfbench_improvement_loop_t *l, const char *suite) {
    for (size_t i = 0; i < l->count; ++i)
        if (strcmp(l->best[i].suite, suite) == 0) return &l->best[i];
    return NULL;
}
bool ca_selfbench_improvement_loop_cycle(ca_selfbench_improvement_loop_t *l,
                                         const char *bench_suite_id,
                                         ca_self_improvement_verdict_t *out) {
    if (!l || !out) return false;
    const char *suite = hj_blank(bench_suite_id) ? "default" : bench_suite_id;
    size_t tasks = l->suite_count(l->suite_count_user, suite);
    if (tasks == 0) {
        out->improvements_applied = hj_strdup("skipped: no tasks in suite");
        out->new_bench_score = 0.0;
        return true;
    }
    ca_ab_verdict_t verdict; memset(&verdict, 0, sizeof(verdict));
    if (!l->ab_run(l->ab_run_user, suite, tasks, &verdict)) {
        ca_ab_verdict_free(&verdict);
        return false;
    }
    double new_score = verdict.candidate_mean_score;
    char buf[256];
    if (verdict.should_promote) {
        if (l->promote) l->promote(l->promote_user, &verdict);
        hj_best_score *b = hj_sb_best_find(l, suite);
        if (b) { if (new_score > b->score) b->score = new_score; }
        else {
            if (l->count == l->cap) {
                size_t nc = l->cap ? l->cap * 2 : 8;
                hj_best_score *nb = (hj_best_score *)realloc(l->best, nc * sizeof(*nb));
                if (nb) { l->best = nb; l->cap = nc; }
            }
            if (l->count < l->cap) {
                l->best[l->count].suite = hj_strdup(suite);
                l->best[l->count].score = new_score;
                l->count++;
            }
        }
        snprintf(buf, sizeof(buf), "promoted candidate (%s)", verdict.reason ? verdict.reason : "");
    } else {
        snprintf(buf, sizeof(buf), "rejected (%s)", verdict.reason ? verdict.reason : "");
    }
    out->improvements_applied = hj_strdup(buf);
    out->new_bench_score = new_score;
    ca_ab_verdict_free(&verdict);
    return true;
}
double ca_selfbench_improvement_loop_best_score(const ca_selfbench_improvement_loop_t *l,
                                                const char *bench_suite_id) {
    if (!l || !bench_suite_id) return 0.0;
    hj_best_score *b = hj_sb_best_find((ca_selfbench_improvement_loop_t *)l, bench_suite_id);
    return b ? b->score : 0.0;
}

/* =====================================================================
 * VoiceCompanionListener
 * ===================================================================== */

struct ca_voice_listener {
    ca_voice_session_send_fn session_send; void *session_user;
    ca_utterance_detected_fn on_utterance; void *on_utterance_user;
    ca_response_ready_fn     on_response;  void *on_response_user;
    bool disposed;
};

ca_voice_listener_t *ca_voice_listener_create(
    ca_voice_session_send_fn session_send, void *session_user,
    ca_utterance_detected_fn on_utterance, void *on_utterance_user,
    ca_response_ready_fn on_response, void *on_response_user) {
    if (!session_send) return NULL;
    ca_voice_listener_t *l = (ca_voice_listener_t *)calloc(1, sizeof(*l));
    if (!l) return NULL;
    l->session_send = session_send; l->session_user = session_user;
    l->on_utterance = on_utterance; l->on_utterance_user = on_utterance_user;
    l->on_response = on_response; l->on_response_user = on_response_user;
    return l;
}
void ca_voice_listener_destroy(ca_voice_listener_t *l) {
    if (!l) return;
    l->disposed = true;
    free(l);
}
bool ca_voice_listener_on_transcribed(ca_voice_listener_t *l, const char *text,
                                      float confidence, int64_t detected_at_ms,
                                      int64_t now_ms) {
    if (!l || l->disposed) return false;
    /* raise UtteranceDetected */
    if (l->on_utterance) {
        ca_utterance_detected_event_t ev;
        ev.text = text; ev.confidence = confidence; ev.detected_at_ms = detected_at_ms;
        l->on_utterance(l->on_utterance_user, &ev);
    }
    /* forward to the session; NULL reply → swallow (C# try/catch traces) */
    char *reply = l->session_send(l->session_user, text);
    if (!reply) return false;
    bool fired = false;
    if (!l->disposed && l->on_response) {
        ca_response_ready_event_t ev;
        ev.text = reply; ev.original_utterance = text; ev.completed_at_ms = now_ms;
        l->on_response(l->on_response_user, &ev);
        fired = true;
    }
    free(reply);
    return fired;
}
