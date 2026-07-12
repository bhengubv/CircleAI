/*
 * telephony_agent.c — CircleAI.Telephony voice-agent layer (C11 port).
 *
 * The pure-logic voice-agent classes on top of the carrier contract surface.
 * Deterministic in-memory logic; async / TTS / HTTP / tunnel boundaries are
 * ca_ fn-ptr seams. Pure C11 + libc + libm. No pthreads.
 *
 * See telephony_agent.h for the per-type contract.
 */

#include "circle_ai/telephony_agent.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <math.h>
#include <time.h>

/* ── shared helpers ─────────────────────────────────────────────────────── */

static char *ta_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *ta_strdup_empty(const char *s) { return ta_strdup(s ? s : ""); }

static bool ta_is_ws(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (!isspace((unsigned char)*p)) return false;
    return true;
}

/* case-insensitive equality (OrdinalIgnoreCase, ASCII). */
static bool ta_ieq(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return false;
        ++a; ++b;
    }
    return *a == *b;
}

/* case-insensitive prefix test: does `s` start with `prefix`? */
static bool ta_istartswith(const char *s, const char *prefix) {
    if (!s || !prefix) return false;
    while (*prefix) {
        if (tolower((unsigned char)*s) != tolower((unsigned char)*prefix)) return false;
        ++s; ++prefix;
    }
    return true;
}

/* case-insensitive substring (IndexOf(..,OrdinalIgnoreCase) >= 0). */
static bool ta_icontains(const char *hay, const char *needle) {
    if (!hay || !needle) return false;
    if (!*needle) return true;
    size_t nl = strlen(needle);
    for (const char *p = hay; *p; ++p) {
        size_t i = 0;
        while (i < nl && p[i] &&
               tolower((unsigned char)p[i]) == tolower((unsigned char)needle[i]))
            ++i;
        if (i == nl) return true;
    }
    return false;
}

static int64_t ta_now_ms(void) { return (int64_t)time(NULL) * 1000; }

/* strdup an owned array of strings. */
static char **ta_strarr_dup(const char *const *src, size_t n) {
    if (n == 0) return NULL;
    char **out = (char **)calloc(n, sizeof(char *));
    if (!out) return NULL;
    for (size_t i = 0; i < n; ++i) {
        out[i] = ta_strdup_empty(src[i]);
        if (!out[i]) {
            for (size_t j = 0; j < i; ++j) free(out[j]);
            free(out);
            return NULL;
        }
    }
    return out;
}
static void ta_strarr_free(char **arr, size_t n) {
    if (!arr) return;
    for (size_t i = 0; i < n; ++i) free(arr[i]);
    free(arr);
}

/* ===========================================================================
 * BargeInController
 * =========================================================================== */

void ca_tela_barge_transition_free(ca_tela_barge_transition_t *t) {
    if (!t) return;
    free(t->reason);
    t->reason = NULL;
}
ca_tela_barge_transition_t *ca_tela_barge_transition_copy(
    const ca_tela_barge_transition_t *t) {
    if (!t) return NULL;
    ca_tela_barge_transition_t *c =
        (ca_tela_barge_transition_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->from = t->from; c->to = t->to; c->at_utc_ms = t->at_utc_ms;
    c->reason = ta_strdup_empty(t->reason);
    if (!c->reason) { free(c); return NULL; }
    return c;
}

static ca_tela_barge_transition_t *barge_transition_new(
    ca_tela_barge_state_t from, ca_tela_barge_state_t to, int64_t at,
    const char *reason) {
    ca_tela_barge_transition_t *t =
        (ca_tela_barge_transition_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->from = from; t->to = to; t->at_utc_ms = at;
    t->reason = ta_strdup_empty(reason);
    if (!t->reason) { free(t); return NULL; }
    return t;
}

struct ca_tela_barge_controller {
    int64_t pause_after_ticks;
    int64_t cancel_after_ticks;
    ca_tela_barge_state_t state;
    bool    has_speech_start;
    int64_t speech_started_at_ms;
};

ca_tela_barge_controller_t *ca_tela_barge_controller_create(
    int64_t pause_after_ticks, int64_t cancel_after_ticks) {
    ca_tela_barge_controller_t *c =
        (ca_tela_barge_controller_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->pause_after_ticks =
        pause_after_ticks > 0 ? pause_after_ticks : 100 * CA_TELA_TICKS_PER_MS;
    c->cancel_after_ticks =
        cancel_after_ticks > 0 ? cancel_after_ticks : 600 * CA_TELA_TICKS_PER_MS;
    c->state = CA_TELA_BARGE_SPEAKING;
    return c;
}
void ca_tela_barge_controller_destroy(ca_tela_barge_controller_t *c) { free(c); }

ca_tela_barge_state_t ca_tela_barge_controller_state(
    const ca_tela_barge_controller_t *c) {
    return c ? c->state : CA_TELA_BARGE_SPEAKING;
}

void ca_tela_barge_controller_on_playback_start(ca_tela_barge_controller_t *c) {
    if (!c) return;
    c->state = CA_TELA_BARGE_SPEAKING;
    c->has_speech_start = false;
}

ca_tela_barge_transition_t *ca_tela_barge_controller_on_caller_speech(
    ca_tela_barge_controller_t *c, int64_t now_utc_ms) {
    if (!c) return NULL;
    if (c->state == CA_TELA_BARGE_CANCELLED) return NULL;
    if (!c->has_speech_start) {
        c->has_speech_start = true;
        c->speech_started_at_ms = now_utc_ms;
        return NULL;
    }
    int64_t elapsed_ms = now_utc_ms - c->speech_started_at_ms;
    int64_t elapsed_ticks = elapsed_ms * CA_TELA_TICKS_PER_MS;
    char reason[96];
    if (c->state == CA_TELA_BARGE_SPEAKING && elapsed_ticks >= c->pause_after_ticks) {
        snprintf(reason, sizeof(reason), "Caller speech %lld ms",
                 (long long)elapsed_ms);
        ca_tela_barge_transition_t *t = barge_transition_new(
            c->state, CA_TELA_BARGE_PAUSED, now_utc_ms, reason);
        if (t) c->state = CA_TELA_BARGE_PAUSED;
        return t;
    }
    if (c->state == CA_TELA_BARGE_PAUSED && elapsed_ticks >= c->cancel_after_ticks) {
        snprintf(reason, sizeof(reason), "Confirmed barge-in after %lld ms",
                 (long long)elapsed_ms);
        ca_tela_barge_transition_t *t = barge_transition_new(
            c->state, CA_TELA_BARGE_CANCELLED, now_utc_ms, reason);
        if (t) c->state = CA_TELA_BARGE_CANCELLED;
        return t;
    }
    return NULL;
}

ca_tela_barge_transition_t *ca_tela_barge_controller_on_caller_silence(
    ca_tela_barge_controller_t *c, int64_t now_utc_ms) {
    if (!c) return NULL;
    c->has_speech_start = false;
    if (c->state == CA_TELA_BARGE_PAUSED) {
        ca_tela_barge_transition_t *t = barge_transition_new(
            c->state, CA_TELA_BARGE_RESUMED, now_utc_ms,
            "Caller fell silent after pause");
        if (t) c->state = CA_TELA_BARGE_SPEAKING;   /* resume */
        return t;
    }
    return NULL;
}

bool ca_tela_barge_controller_should_emit_audio(
    const ca_tela_barge_controller_t *c) {
    return c && c->state == CA_TELA_BARGE_SPEAKING;
}
bool ca_tela_barge_controller_was_barged_in(const ca_tela_barge_controller_t *c) {
    return c && c->state == CA_TELA_BARGE_CANCELLED;
}

/* ===========================================================================
 * IvrLoopDetector
 * =========================================================================== */

void ca_tela_ivr_verdict_free(ca_tela_ivr_verdict_t *v) {
    if (!v) return;
    free(v->reason);
    v->reason = NULL;
}

static ca_tela_ivr_verdict_t *ivr_verdict_new(bool looping, int len,
                                              const char *reason) {
    ca_tela_ivr_verdict_t *v = (ca_tela_ivr_verdict_t *)calloc(1, sizeof(*v));
    if (!v) return NULL;
    v->is_looping = looping; v->loop_length = len;
    v->reason = ta_strdup_empty(reason);
    if (!v->reason) { free(v); return NULL; }
    return v;
}

struct ca_tela_ivr_detector {
    ca_tela_ivr_round_t *rounds;
    size_t               count, cap;
    int                  max_rounds;
    int                  min_rounds;
    double               similarity;
};

ca_tela_ivr_detector_t *ca_tela_ivr_detector_create(
    int max_rounds_to_track, int min_rounds_for_loop, double similarity_threshold) {
    ca_tela_ivr_detector_t *d =
        (ca_tela_ivr_detector_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->max_rounds = max_rounds_to_track > 0 ? max_rounds_to_track : 32;
    d->min_rounds = min_rounds_for_loop > 0 ? min_rounds_for_loop : 2;
    d->similarity = similarity_threshold > 0.0 ? similarity_threshold : 0.85;
    return d;
}

static void ivr_round_clear(ca_tela_ivr_round_t *r) {
    free(r->speech); free(r->dtmf_pressed);
    r->speech = r->dtmf_pressed = NULL;
}

void ca_tela_ivr_detector_destroy(ca_tela_ivr_detector_t *d) {
    if (!d) return;
    for (size_t i = 0; i < d->count; ++i) ivr_round_clear(&d->rounds[i]);
    free(d->rounds);
    free(d);
}

/* Jaccard word-set similarity, case-insensitive, threshold-gated. */
static bool ivr_similar(const char *a, const char *b, double threshold) {
    if (ta_ieq(a, b)) return true;
    if (!a || !b) return false;
    /* tokenise on ' ' (RemoveEmptyEntries) into two owned word lists. */
    char **wa = NULL, **wb = NULL; size_t na = 0, nb = 0, capa = 0, capb = 0;
    bool ok = true;
    const char *srcs[2]; char ***outs[2]; size_t *ns[2]; size_t *caps[2];
    srcs[0] = a; outs[0] = &wa; ns[0] = &na; caps[0] = &capa;
    srcs[1] = b; outs[1] = &wb; ns[1] = &nb; caps[1] = &capb;
    for (int k = 0; k < 2 && ok; ++k) {
        const char *p = srcs[k];
        while (*p) {
            while (*p == ' ') ++p;
            if (!*p) break;
            const char *start = p;
            while (*p && *p != ' ') ++p;
            size_t wlen = (size_t)(p - start);
            char *w = (char *)malloc(wlen + 1);
            if (!w) { ok = false; break; }
            memcpy(w, start, wlen); w[wlen] = '\0';
            if (*ns[k] == *caps[k]) {
                size_t nc = *caps[k] ? *caps[k] * 2 : 4;
                void *ni = realloc(*outs[k], nc * sizeof(char *));
                if (!ni) { free(w); ok = false; break; }
                *outs[k] = (char **)ni; *caps[k] = nc;
            }
            (*outs[k])[(*ns[k])++] = w;
        }
    }
    bool result = false;
    if (ok && na > 0 && nb > 0) {
        /* dedupe not required for Jaccard-on-sets; emulate HashSet by ignoring
         * repeats when counting union/intersection. */
        size_t inter = 0;
        /* intersection: distinct words of A present in B */
        for (size_t i = 0; i < na; ++i) {
            bool dup = false;
            for (size_t j = 0; j < i; ++j) if (ta_ieq(wa[i], wa[j])) { dup = true; break; }
            if (dup) continue;
            for (size_t j = 0; j < nb; ++j) if (ta_ieq(wa[i], wb[j])) { inter++; break; }
        }
        /* union: distinct(A) + distinct(B not in A) */
        size_t distinctA = 0;
        for (size_t i = 0; i < na; ++i) {
            bool dup = false;
            for (size_t j = 0; j < i; ++j) if (ta_ieq(wa[i], wa[j])) { dup = true; break; }
            if (!dup) distinctA++;
        }
        size_t extraB = 0;
        for (size_t i = 0; i < nb; ++i) {
            bool dup = false;
            for (size_t j = 0; j < i; ++j) if (ta_ieq(wb[i], wb[j])) { dup = true; break; }
            if (dup) continue;
            bool inA = false;
            for (size_t j = 0; j < na; ++j) if (ta_ieq(wb[i], wa[j])) { inA = true; break; }
            if (!inA) extraB++;
        }
        size_t uni = distinctA + extraB;
        if (uni > 0) result = ((double)inter / (double)uni) >= threshold;
    }
    ta_strarr_free(wa, na);
    ta_strarr_free(wb, nb);
    return result;
}

/* NULL-safe dtmf equality (Ordinal, matching C# string == with null). */
static bool ivr_dtmf_eq(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    return strcmp(a, b) == 0;
}

static ca_tela_ivr_verdict_t *ivr_evaluate(ca_tela_ivr_detector_t *d) {
    size_t n = d->count;
    /* Strong signal: same DTMF + similar prompt three in a row. */
    if (n >= 3) {
        ca_tela_ivr_round_t *r0 = &d->rounds[n - 3];
        ca_tela_ivr_round_t *r1 = &d->rounds[n - 2];
        ca_tela_ivr_round_t *r2 = &d->rounds[n - 1];
        bool same_dtmf = ivr_dtmf_eq(r1->dtmf_pressed, r0->dtmf_pressed) &&
                         ivr_dtmf_eq(r2->dtmf_pressed, r0->dtmf_pressed);
        bool sim = ivr_similar(r0->speech, r0->speech, d->similarity) &&
                   ivr_similar(r1->speech, r0->speech, d->similarity) &&
                   ivr_similar(r2->speech, r0->speech, d->similarity);
        if (same_dtmf && sim)
            return ivr_verdict_new(true, 1, "Same prompt-and-press triple in a row.");
    }

    if (n < (size_t)(d->min_rounds * 2))
        return ivr_verdict_new(false, 0, "Not enough rounds to evaluate.");

    for (int L = d->min_rounds; L <= (int)(n / 2); ++L) {
        /* tail = last 2L rounds */
        size_t base = n - (size_t)(2 * L);
        bool looped = true;
        for (int i = 0; i < L; ++i) {
            ca_tela_ivr_round_t *a = &d->rounds[base + (size_t)i];
            ca_tela_ivr_round_t *b = &d->rounds[base + (size_t)(L + i)];
            if (!ivr_similar(a->speech, b->speech, d->similarity) ||
                !ivr_dtmf_eq(a->dtmf_pressed, b->dtmf_pressed)) {
                looped = false;
                break;
            }
        }
        if (looped) {
            char reason[64];
            snprintf(reason, sizeof(reason),
                     "Detected repeating cycle of length %d.", L);
            return ivr_verdict_new(true, L, reason);
        }
    }
    return ivr_verdict_new(false, 0, "No loop detected.");
}

ca_tela_ivr_verdict_t *ca_tela_ivr_detector_observe(
    ca_tela_ivr_detector_t *d, const char *speech, const char *dtmf_pressed,
    int64_t at_utc_ms) {
    if (!d || !speech) return NULL;
    if (d->count == d->cap) {
        size_t nc = d->cap ? d->cap * 2 : 8;
        void *ni = realloc(d->rounds, nc * sizeof(*d->rounds));
        if (!ni) return NULL;
        d->rounds = (ca_tela_ivr_round_t *)ni; d->cap = nc;
    }
    ca_tela_ivr_round_t *r = &d->rounds[d->count];
    memset(r, 0, sizeof(*r));
    r->speech = ta_strdup(speech);
    r->dtmf_pressed = dtmf_pressed ? ta_strdup(dtmf_pressed) : NULL;
    r->at_utc_ms = at_utc_ms;
    if (!r->speech || (dtmf_pressed && !r->dtmf_pressed)) {
        ivr_round_clear(r);
        return NULL;
    }
    d->count++;
    /* trim to max_rounds (remove from front) */
    while (d->count > (size_t)d->max_rounds) {
        ivr_round_clear(&d->rounds[0]);
        memmove(&d->rounds[0], &d->rounds[1], (d->count - 1) * sizeof(*d->rounds));
        d->count--;
    }
    return ivr_evaluate(d);
}

ca_tela_ivr_verdict_t *ca_tela_ivr_detector_current(ca_tela_ivr_detector_t *d) {
    if (!d) return NULL;
    return ivr_evaluate(d);
}
void ca_tela_ivr_detector_reset(ca_tela_ivr_detector_t *d) {
    if (!d) return;
    for (size_t i = 0; i < d->count; ++i) ivr_round_clear(&d->rounds[i]);
    d->count = 0;
}

/* ===========================================================================
 * CircuitBreakerToolRegistry
 * =========================================================================== */

ca_tela_tool_policy_t ca_tela_tool_policy_default(void) {
    ca_tela_tool_policy_t p;
    p.timeout_ticks = 5 * CA_TELA_TICKS_PER_SEC;
    p.failure_threshold = 3;
    p.open_duration_ticks = 30 * CA_TELA_TICKS_PER_SEC;
    return p;
}

static ca_tela_tool_policy_t policy_or_defaults(const ca_tela_tool_policy_t *p) {
    ca_tela_tool_policy_t d = ca_tela_tool_policy_default();
    if (!p) return d;
    ca_tela_tool_policy_t out;
    out.timeout_ticks = p->timeout_ticks > 0 ? p->timeout_ticks : d.timeout_ticks;
    out.failure_threshold =
        p->failure_threshold > 0 ? p->failure_threshold : d.failure_threshold;
    out.open_duration_ticks =
        p->open_duration_ticks > 0 ? p->open_duration_ticks : d.open_duration_ticks;
    return out;
}

typedef struct {
    char                 *tool_name; /* owned */
    ca_tela_tool_policy_t policy;
} cb_policy_entry_t;

typedef struct {
    char   *tool_name; /* owned */
    int     consecutive_failures;
    int64_t opened_at_ms;
    bool    is_open;
} cb_breaker_entry_t;

struct ca_tela_cb_registry {
    ca_tel_tool_registry_t *inner;   /* borrowed */
    ca_tela_tool_policy_t   default_policy;
    cb_policy_entry_t      *policies;
    size_t                  policy_count, policy_cap;
    cb_breaker_entry_t     *breakers;
    size_t                  breaker_count, breaker_cap;
};

ca_tela_cb_registry_t *ca_tela_cb_registry_create(
    ca_tel_tool_registry_t *inner, const ca_tela_tool_policy_t *default_policy) {
    if (!inner) return NULL;
    ca_tela_cb_registry_t *r = (ca_tela_cb_registry_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->inner = inner;
    r->default_policy = policy_or_defaults(default_policy);
    return r;
}
void ca_tela_cb_registry_destroy(ca_tela_cb_registry_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->policy_count; ++i) free(r->policies[i].tool_name);
    free(r->policies);
    for (size_t i = 0; i < r->breaker_count; ++i) free(r->breakers[i].tool_name);
    free(r->breakers);
    free(r);
}

static ca_tela_tool_policy_t cb_get_policy(const ca_tela_cb_registry_t *r,
                                           const char *tool_name) {
    for (size_t i = 0; i < r->policy_count; ++i)
        if (ta_ieq(r->policies[i].tool_name, tool_name))
            return r->policies[i].policy;
    return r->default_policy;
}

int ca_tela_cb_registry_set_policy(ca_tela_cb_registry_t *r, const char *tool_name,
                                   const ca_tela_tool_policy_t *policy) {
    if (!r || !tool_name || !policy) return -1;
    ca_tela_tool_policy_t eff = policy_or_defaults(policy);
    for (size_t i = 0; i < r->policy_count; ++i) {
        if (ta_ieq(r->policies[i].tool_name, tool_name)) {
            r->policies[i].policy = eff;
            return 0;
        }
    }
    if (r->policy_count == r->policy_cap) {
        size_t nc = r->policy_cap ? r->policy_cap * 2 : 4;
        void *ni = realloc(r->policies, nc * sizeof(*r->policies));
        if (!ni) return -1;
        r->policies = (cb_policy_entry_t *)ni; r->policy_cap = nc;
    }
    r->policies[r->policy_count].tool_name = ta_strdup(tool_name);
    if (!r->policies[r->policy_count].tool_name) return -1;
    r->policies[r->policy_count].policy = eff;
    r->policy_count++;
    return 0;
}

static cb_breaker_entry_t *cb_get_or_add_breaker(ca_tela_cb_registry_t *r,
                                                 const char *tool_name) {
    for (size_t i = 0; i < r->breaker_count; ++i)
        if (ta_ieq(r->breakers[i].tool_name, tool_name)) return &r->breakers[i];
    if (r->breaker_count == r->breaker_cap) {
        size_t nc = r->breaker_cap ? r->breaker_cap * 2 : 4;
        void *ni = realloc(r->breakers, nc * sizeof(*r->breakers));
        if (!ni) return NULL;
        r->breakers = (cb_breaker_entry_t *)ni; r->breaker_cap = nc;
    }
    cb_breaker_entry_t *e = &r->breakers[r->breaker_count];
    memset(e, 0, sizeof(*e));
    e->tool_name = ta_strdup(tool_name);
    if (!e->tool_name) return NULL;
    r->breaker_count++;
    return e;
}

static ca_tela_breaker_state_t cb_state_of(const cb_breaker_entry_t *e, int64_t now_ms,
                                           int64_t open_duration_ticks) {
    if (!e || !e->is_open) return CA_TELA_BREAKER_CLOSED;
    int64_t open_ms = open_duration_ticks / CA_TELA_TICKS_PER_MS;
    if (now_ms - e->opened_at_ms >= open_ms) return CA_TELA_BREAKER_HALF_OPEN;
    return CA_TELA_BREAKER_OPEN;
}

ca_tela_breaker_state_t ca_tela_cb_registry_get_state(
    const ca_tela_cb_registry_t *r, const char *tool_name, int64_t now_utc_ms) {
    if (!r) return CA_TELA_BREAKER_CLOSED;
    ca_tela_tool_policy_t p = cb_get_policy(r, tool_name);
    for (size_t i = 0; i < r->breaker_count; ++i)
        if (ta_ieq(r->breakers[i].tool_name, tool_name))
            return cb_state_of(&r->breakers[i], now_utc_ms, p.open_duration_ticks);
    return CA_TELA_BREAKER_CLOSED;
}

int ca_tela_cb_registry_register_local(ca_tela_cb_registry_t *r,
                                       const ca_tel_tool_definition_t *def,
                                       ca_tel_local_tool_handler_fn handler,
                                       void *handler_ctx) {
    if (!r) return -1;
    return ca_tel_tool_registry_register_local(r->inner, def, handler, handler_ctx);
}
int ca_tela_cb_registry_register_webhook(ca_tela_cb_registry_t *r,
                                         const ca_tel_tool_definition_t *def,
                                         const char *webhook_url) {
    if (!r) return -1;
    return ca_tel_tool_registry_register_webhook(r->inner, def, webhook_url);
}

static void cb_record_success(cb_breaker_entry_t *e) {
    e->consecutive_failures = 0;
    e->is_open = false;
}
static void cb_record_failure(cb_breaker_entry_t *e, int threshold, int64_t now_ms) {
    if (++e->consecutive_failures >= threshold) {
        e->is_open = true;
        e->opened_at_ms = now_ms;
    }
}

/* Build a ToolResult with Succeeded=false + a message. */
static ca_tel_tool_result_t *cb_fail_result(const char *call_id, const char *msg) {
    ca_tel_tool_result_t *res = (ca_tel_tool_result_t *)calloc(1, sizeof(*res));
    if (!res) return NULL;
    res->call_id = ta_strdup_empty(call_id);
    res->succeeded = false;
    res->result_json = ta_strdup("{}");
    res->error = ta_strdup_empty(msg);
    if (!res->call_id || !res->result_json || !res->error) {
        ca_tel_tool_result_free(res); free(res); return NULL;
    }
    return res;
}

ca_tel_tool_result_t *ca_tela_cb_registry_invoke(
    ca_tela_cb_registry_t *r, const ca_tel_tool_invocation_t *invocation,
    int64_t now_utc_ms, bool simulate_timeout) {
    if (!r || !invocation) return NULL;
    ca_tela_tool_policy_t policy = cb_get_policy(r, invocation->tool_name);
    cb_breaker_entry_t *entry = cb_get_or_add_breaker(r, invocation->tool_name);
    if (!entry) return NULL;

    ca_tela_breaker_state_t state =
        cb_state_of(entry, now_utc_ms, policy.open_duration_ticks);
    if (state == CA_TELA_BREAKER_OPEN) {
        char msg[192];
        snprintf(msg, sizeof(msg),
                 "Tool '%s' is circuit-broken; retry after the breaker resets.",
                 invocation->tool_name ? invocation->tool_name : "");
        return cb_fail_result(invocation->call_id, msg);
    }

    if (simulate_timeout) {
        cb_record_failure(entry, policy.failure_threshold, now_utc_ms);
        char msg[160];
        double ms = (double)policy.timeout_ticks / (double)CA_TELA_TICKS_PER_MS;
        snprintf(msg, sizeof(msg), "Tool '%s' timed out after %g ms.",
                 invocation->tool_name ? invocation->tool_name : "", ms);
        return cb_fail_result(invocation->call_id, msg);
    }

    ca_tel_tool_result_t *result = ca_tel_tool_registry_invoke(r->inner, invocation);
    if (!result) return NULL;
    if (result->succeeded) cb_record_success(entry);
    else cb_record_failure(entry, policy.failure_threshold, now_utc_ms);
    return result;
}

/* ===========================================================================
 * Guardrails
 * =========================================================================== */

void ca_tela_guard_result_free(ca_tela_guard_result_t *r) {
    if (!r) return;
    free(r->final_text);
    ta_strarr_free(r->triggered_rules, r->triggered_count);
    r->final_text = NULL;
    r->triggered_rules = NULL;
    r->triggered_count = 0;
}

typedef struct {
    char                   *name;      /* owned */
    ca_tela_guard_action_t  action;
    char                   *fallback;  /* owned or NULL */
    ca_tela_guard_match_fn  match;
    ca_tela_guard_redact_fn redactor;
    void                   *ctx;       /* borrowed */
} guard_rule_t;

struct ca_tela_guardrails {
    guard_rule_t *rules;
    size_t        count, cap;
    char         *default_fallback;    /* owned */
};

ca_tela_guardrails_t *ca_tela_guardrails_create(const char *default_fallback) {
    ca_tela_guardrails_t *g = (ca_tela_guardrails_t *)calloc(1, sizeof(*g));
    if (!g) return NULL;
    g->default_fallback = ta_strdup(
        default_fallback ? default_fallback
                         : "I'm sorry, I can't help with that right now.");
    if (!g->default_fallback) { free(g); return NULL; }
    return g;
}
void ca_tela_guardrails_destroy(ca_tela_guardrails_t *g) {
    if (!g) return;
    for (size_t i = 0; i < g->count; ++i) {
        free(g->rules[i].name);
        free(g->rules[i].fallback);
    }
    free(g->rules);
    free(g->default_fallback);
    free(g);
}

int ca_tela_guardrails_add_rule(ca_tela_guardrails_t *g, const char *name,
                                ca_tela_guard_action_t action,
                                const char *fallback_message,
                                ca_tela_guard_match_fn match,
                                ca_tela_guard_redact_fn redactor, void *ctx) {
    if (!g || !name) return -1;
    if (g->count == g->cap) {
        size_t nc = g->cap ? g->cap * 2 : 4;
        void *ni = realloc(g->rules, nc * sizeof(*g->rules));
        if (!ni) return -1;
        g->rules = (guard_rule_t *)ni; g->cap = nc;
    }
    guard_rule_t *r = &g->rules[g->count];
    memset(r, 0, sizeof(*r));
    r->name = ta_strdup(name);
    if (!r->name) return -1;
    if (fallback_message) {
        r->fallback = ta_strdup(fallback_message);
        if (!r->fallback) { free(r->name); return -1; }
    }
    r->action = action;
    r->match = match;
    r->redactor = redactor;
    r->ctx = ctx;
    g->count++;
    return 0;
}

/* append a name to the triggered list (grows). */
static bool guard_add_triggered(char ***arr, size_t *count, size_t *cap,
                                const char *name) {
    if (*count == *cap) {
        size_t nc = *cap ? *cap * 2 : 4;
        void *ni = realloc(*arr, nc * sizeof(char *));
        if (!ni) return false;
        *arr = (char **)ni; *cap = nc;
    }
    char *dup = ta_strdup_empty(name);
    if (!dup) return false;
    (*arr)[(*count)++] = dup;
    return true;
}

ca_tela_guard_result_t *ca_tela_guardrails_apply(ca_tela_guardrails_t *g,
                                                 const char *draft) {
    if (!g) return NULL;
    ca_tela_guard_result_t *res =
        (ca_tela_guard_result_t *)calloc(1, sizeof(*res));
    if (!res) return NULL;

    if (!draft || draft[0] == '\0') {
        res->final_text = ta_strdup_empty(draft);
        if (!res->final_text) { free(res); return NULL; }
        return res;
    }

    char **triggered = NULL; size_t tcount = 0, tcap = 0;
    char *text = ta_strdup(draft);
    if (!text) { free(res); return NULL; }
    bool blocked = false;

    for (size_t i = 0; i < g->count; ++i) {
        guard_rule_t *rule = &g->rules[i];
        if (!rule->match || !rule->match(rule->ctx, text)) continue;
        if (!guard_add_triggered(&triggered, &tcount, &tcap, rule->name)) {
            free(text); ta_strarr_free(triggered, tcount); free(res); return NULL;
        }
        if (rule->action == CA_TELA_GUARD_REPLACE) {
            blocked = true;
            const char *fb = rule->fallback ? rule->fallback : g->default_fallback;
            char *nf = ta_strdup(fb);
            if (!nf) { free(text); ta_strarr_free(triggered, tcount); free(res); return NULL; }
            free(text);
            res->final_text = nf;
            res->was_modified = true;
            res->was_blocked = true;
            res->triggered_rules = triggered;
            res->triggered_count = tcount;
            return res;
        } else if (rule->action == CA_TELA_GUARD_REDACT) {
            if (rule->redactor) {
                char *redacted = rule->redactor(rule->ctx, text);
                if (!redacted) { free(text); ta_strarr_free(triggered, tcount); free(res); return NULL; }
                free(text);
                text = redacted;
            }
            /* NULL redactor: flag only, no mutation. */
        }
        /* WARN: flag only. */
    }

    bool modified = strcmp(text, draft) != 0;
    res->final_text = text;
    res->was_modified = modified;
    res->was_blocked = blocked;
    res->triggered_rules = triggered;
    res->triggered_count = tcount;
    return res;
}

/* ── CommonGuardrails ──────────────────────────────────────────────────────── */

/* count consecutive digits allowing interior spaces/hyphens; used by the CC rule.
 * Returns the max run of digits (ignoring separators) anywhere in the text. This
 * mirrors the regex \b(?:\d[ -]*?){13,19}\b intent for detection. */
static int cc_max_digit_run(const char *text) {
    int best = 0, run = 0;
    bool in_run = false;
    for (const char *p = text; *p; ++p) {
        if (isdigit((unsigned char)*p)) {
            run++;
            in_run = true;
            if (run > best) best = run;
        } else if (in_run && (*p == ' ' || *p == '-')) {
            /* separator continues the run (does not reset) */
        } else {
            run = 0;
            in_run = false;
        }
    }
    return best;
}

bool ca_tela_common_credit_card_match(void *ctx, const char *text) {
    (void)ctx;
    if (!text) return false;
    int run = cc_max_digit_run(text);
    return run >= 13 && run <= 19;
}

/* Replace each maximal digit(+separator) run of length 13..19 with the redaction. */
char *ca_tela_common_credit_card_redact(void *ctx, const char *text) {
    (void)ctx;
    if (!text) return NULL;
    static const char *REDACT = "[redacted card number]";
    size_t redact_len = strlen(REDACT);
    size_t n = strlen(text);
    /* worst case: text unchanged; else runs replaced. Build into a growable buf. */
    size_t cap = n + 1;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    size_t o = 0;
    size_t i = 0;
    while (i < n) {
        if (isdigit((unsigned char)text[i])) {
            /* scan a run of digits + interior separators */
            size_t j = i;
            int digits = 0;
            size_t last_digit = i;
            while (j < n) {
                if (isdigit((unsigned char)text[j])) { digits++; last_digit = j; j++; }
                else if (text[j] == ' ' || text[j] == '-') { j++; }
                else break;
            }
            /* run spans [i, last_digit] inclusive */
            size_t run_end = last_digit + 1;
            if (digits >= 13 && digits <= 19) {
                if (o + redact_len + 1 > cap) {
                    cap = o + redact_len + (n - run_end) + 1;
                    char *ni = (char *)realloc(out, cap);
                    if (!ni) { free(out); return NULL; }
                    out = ni;
                }
                memcpy(out + o, REDACT, redact_len);
                o += redact_len;
                i = run_end;
            } else {
                /* copy the run verbatim */
                if (o + (run_end - i) + 1 > cap) {
                    cap = o + (run_end - i) + (n - run_end) + 1;
                    char *ni = (char *)realloc(out, cap);
                    if (!ni) { free(out); return NULL; }
                    out = ni;
                }
                memcpy(out + o, text + i, run_end - i);
                o += (run_end - i);
                i = run_end;
            }
        } else {
            if (o + 2 > cap) {
                cap = o + 2 + (n - i);
                char *ni = (char *)realloc(out, cap);
                if (!ni) { free(out); return NULL; }
                out = ni;
            }
            out[o++] = text[i++];
        }
    }
    out[o] = '\0';
    return out;
}

bool ca_tela_common_ssn_match(void *ctx, const char *text) {
    (void)ctx;
    if (!text) return false;
    /* \b\d{3}-\d{2}-\d{4}\b */
    size_t n = strlen(text);
    for (size_t i = 0; i + 11 <= n; ++i) {
        /* boundary before */
        if (i > 0 && (isalnum((unsigned char)text[i - 1]) || text[i - 1] == '_'))
            continue;
        if (isdigit((unsigned char)text[i]) && isdigit((unsigned char)text[i+1]) &&
            isdigit((unsigned char)text[i+2]) && text[i+3] == '-' &&
            isdigit((unsigned char)text[i+4]) && isdigit((unsigned char)text[i+5]) &&
            text[i+6] == '-' &&
            isdigit((unsigned char)text[i+7]) && isdigit((unsigned char)text[i+8]) &&
            isdigit((unsigned char)text[i+9]) && isdigit((unsigned char)text[i+10])) {
            /* boundary after */
            size_t after = i + 11;
            if (after < n && (isalnum((unsigned char)text[after]) || text[after] == '_'))
                continue;
            return true;
        }
    }
    return false;
}

struct ca_tela_competitor_ctx {
    char **names;
    size_t count;
};

ca_tela_competitor_ctx_t *ca_tela_common_competitor_create(
    const char *const *competitors, size_t count) {
    ca_tela_competitor_ctx_t *c =
        (ca_tela_competitor_ctx_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    if (count > 0) {
        c->names = ta_strarr_dup(competitors, count);
        if (!c->names) { free(c); return NULL; }
        c->count = count;
    }
    return c;
}
void ca_tela_common_competitor_free(ca_tela_competitor_ctx_t *c) {
    if (!c) return;
    ta_strarr_free(c->names, c->count);
    free(c);
}

/* whole-word (ASCII) case-insensitive presence of any competitor name. */
bool ca_tela_common_competitor_match(void *ctx, const char *text) {
    ca_tela_competitor_ctx_t *c = (ca_tela_competitor_ctx_t *)ctx;
    if (!c || !text) return false;
    for (size_t k = 0; k < c->count; ++k) {
        const char *needle = c->names[k];
        size_t nl = strlen(needle);
        if (nl == 0) continue;
        for (const char *p = text; *p; ++p) {
            /* boundary before */
            if (p != text && (isalnum((unsigned char)p[-1]) || p[-1] == '_')) continue;
            size_t i = 0;
            while (i < nl && p[i] &&
                   tolower((unsigned char)p[i]) == tolower((unsigned char)needle[i]))
                ++i;
            if (i == nl) {
                char after = p[nl];
                if (after == '\0' || !(isalnum((unsigned char)after) || after == '_'))
                    return true;
            }
        }
    }
    return false;
}

/* ===========================================================================
 * ReassuranceFiller
 * =========================================================================== */

struct ca_tela_reassurance {
    char  **shorts; size_t short_count;
    char  **longs;  size_t long_count;
    int64_t short_after_ticks;
    int64_t long_every_ticks;
    int     short_rotation;
    int     long_rotation;
};

static const char *const REASSURE_SHORT[] = {
    "One moment.", "Let me check.", "Give me a sec.", "Just a moment."
};
static const char *const REASSURE_LONG[] = {
    "Still looking that up for you.",
    "This is taking a bit longer than usual — bear with me.",
    "Almost there — still pulling that information.",
    "Thanks for your patience, I'm checking that now."
};

ca_tela_reassurance_t *ca_tela_reassurance_create(
    const char *const *short_fillers, size_t short_count,
    const char *const *long_fillers, size_t long_count) {
    ca_tela_reassurance_t *r = (ca_tela_reassurance_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    if (short_count > 0) {
        r->shorts = ta_strarr_dup(short_fillers, short_count);
        if (!r->shorts) { free(r); return NULL; }
        r->short_count = short_count;
    }
    if (long_count > 0) {
        r->longs = ta_strarr_dup(long_fillers, long_count);
        if (!r->longs) { ta_strarr_free(r->shorts, r->short_count); free(r); return NULL; }
        r->long_count = long_count;
    }
    r->short_after_ticks = 600 * CA_TELA_TICKS_PER_MS;
    r->long_every_ticks  = 3 * CA_TELA_TICKS_PER_SEC;
    return r;
}
ca_tela_reassurance_t *ca_tela_reassurance_create_default(void) {
    return ca_tela_reassurance_create(
        REASSURE_SHORT, sizeof(REASSURE_SHORT) / sizeof(REASSURE_SHORT[0]),
        REASSURE_LONG, sizeof(REASSURE_LONG) / sizeof(REASSURE_LONG[0]));
}
void ca_tela_reassurance_destroy(ca_tela_reassurance_t *r) {
    if (!r) return;
    ta_strarr_free(r->shorts, r->short_count);
    ta_strarr_free(r->longs, r->long_count);
    free(r);
}

int64_t ca_tela_reassurance_short_after_ticks(const ca_tela_reassurance_t *r) {
    return r ? r->short_after_ticks : 0;
}
int64_t ca_tela_reassurance_long_every_ticks(const ca_tela_reassurance_t *r) {
    return r ? r->long_every_ticks : 0;
}
void ca_tela_reassurance_set_short_after_ticks(ca_tela_reassurance_t *r, int64_t t) {
    if (r) r->short_after_ticks = t;
}
void ca_tela_reassurance_set_long_every_ticks(ca_tela_reassurance_t *r, int64_t t) {
    if (r) r->long_every_ticks = t;
}

const char *ca_tela_reassurance_next_short(ca_tela_reassurance_t *r) {
    if (!r) return "One moment.";
    if (r->short_count == 0) return "One moment.";
    int idx = r->short_rotation++;   /* Interlocked.Increment-1 => start at 0 */
    if (idx < 0) idx = -idx;
    return r->shorts[(size_t)idx % r->short_count];
}
const char *ca_tela_reassurance_next_long(ca_tela_reassurance_t *r) {
    if (!r) return "Almost there.";
    if (r->long_count == 0) return "Almost there.";
    int idx = r->long_rotation++;
    if (idx < 0) idx = -idx;
    return r->longs[(size_t)idx % r->long_count];
}

/* ===========================================================================
 * SpeculativeGenerator
 * =========================================================================== */

struct ca_tela_speculator {
    char *active_partial;  /* owned or NULL */
    char *active_draft;    /* owned or NULL (captured generator output) */
    int   min_partial_length;
};

ca_tela_speculator_t *ca_tela_speculator_create(int min_partial_length) {
    ca_tela_speculator_t *s = (ca_tela_speculator_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->min_partial_length = min_partial_length > 0 ? min_partial_length : 8;
    return s;
}
void ca_tela_speculator_destroy(ca_tela_speculator_t *s) {
    if (!s) return;
    free(s->active_partial);
    free(s->active_draft);
    free(s);
}

const char *ca_tela_speculator_active_partial(const ca_tela_speculator_t *s) {
    return s ? s->active_partial : NULL;
}

static void speculator_clear(ca_tela_speculator_t *s) {
    free(s->active_partial); s->active_partial = NULL;
    free(s->active_draft);   s->active_draft = NULL;
}

int ca_tela_speculator_speculate(ca_tela_speculator_t *s, const char *partial,
                                 ca_tela_generator_fn generator, void *gen_ctx) {
    if (!s || !generator) return -1;
    if (ta_is_ws(partial)) return 0;
    if ((int)strlen(partial) < s->min_partial_length) return 0;

    /* If the new partial merely extends the active one, keep it. */
    if (s->active_partial && ta_istartswith(partial, s->active_partial))
        return 0;

    char *dup = ta_strdup(partial);
    if (!dup) return -1;
    char *draft = generator(gen_ctx, partial);   /* may be NULL (error) */
    speculator_clear(s);
    s->active_partial = dup;
    s->active_draft = draft;   /* NULL is fine — commit will regenerate */
    return 0;
}

char *ca_tela_speculator_commit(ca_tela_speculator_t *s, const char *final_transcript,
                                ca_tela_generator_fn generator, void *gen_ctx) {
    if (!s || !generator) return NULL;
    if (ta_is_ws(final_transcript)) return ta_strdup("");

    if (s->active_partial &&
        ta_istartswith(final_transcript, s->active_partial)) {
        if (s->active_draft &&
            ta_ieq(final_transcript, s->active_partial)) {
            char *draft = ta_strdup(s->active_draft);
            speculator_clear(s);
            return draft;   /* reuse the captured draft */
        }
        /* final extended the partial (or draft was NULL) — regenerate below. */
    }

    speculator_clear(s);
    return generator(gen_ctx, final_transcript);
}

void ca_tela_speculator_abort(ca_tela_speculator_t *s) {
    if (!s) return;
    speculator_clear(s);
}

/* ===========================================================================
 * FalseInterruptionTracker
 * =========================================================================== */

struct ca_tela_false_interruption_tracker {
    int64_t total_pauses;
    int64_t confirmed;
    int64_t false_alarms;
};

ca_tela_false_interruption_tracker_t *ca_tela_false_interruption_tracker_create(void) {
    return (ca_tela_false_interruption_tracker_t *)calloc(
        1, sizeof(ca_tela_false_interruption_tracker_t));
}
void ca_tela_false_interruption_tracker_destroy(
    ca_tela_false_interruption_tracker_t *t) { free(t); }

void ca_tela_false_interruption_tracker_record_state(
    ca_tela_false_interruption_tracker_t *t, ca_tela_barge_state_t to) {
    if (!t) return;
    switch (to) {
        case CA_TELA_BARGE_PAUSED:    t->total_pauses++; break;
        case CA_TELA_BARGE_CANCELLED: t->confirmed++;    break;
        case CA_TELA_BARGE_RESUMED:   t->false_alarms++; break;
        default: break;
    }
}
void ca_tela_false_interruption_tracker_record(
    ca_tela_false_interruption_tracker_t *t, const ca_tela_barge_transition_t *tr) {
    if (!t || !tr) return;
    ca_tela_false_interruption_tracker_record_state(t, tr->to);
}
ca_tela_interruption_stats_t ca_tela_false_interruption_tracker_stats(
    const ca_tela_false_interruption_tracker_t *t) {
    ca_tela_interruption_stats_t s;
    memset(&s, 0, sizeof(s));
    if (!t) return s;
    s.total_pause_events = t->total_pauses;
    s.confirmed_barge_ins = t->confirmed;
    s.false_alarms = t->false_alarms;
    s.false_alarm_rate = t->total_pauses > 0
        ? (float)t->false_alarms / (float)t->total_pauses : 0.0f;
    return s;
}
void ca_tela_false_interruption_tracker_reset(
    ca_tela_false_interruption_tracker_t *t) {
    if (!t) return;
    t->total_pauses = t->confirmed = t->false_alarms = 0;
}

/* ===========================================================================
 * HoldMusicMixer
 * =========================================================================== */

static int16_t rd16le(const uint8_t *p) {
    return (int16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}
static void wr16le(uint8_t *p, int16_t v) {
    p[0] = (uint8_t)((uint16_t)v & 0xFF);
    p[1] = (uint8_t)(((uint16_t)v >> 8) & 0xFF);
}
static int16_t clamp16(int v) {
    if (v < -32768) return -32768;
    if (v > 32767) return 32767;
    return (int16_t)v;
}

struct ca_tela_hold_mixer {
    uint8_t *bg;
    size_t   bg_len;
    float    bg_gain;
    float    ducked_gain;
    size_t   cursor;
};

ca_tela_hold_mixer_t *ca_tela_hold_mixer_create(const uint8_t *background_loop,
                                                size_t loop_len,
                                                float background_gain,
                                                float ducked_gain) {
    if (!background_loop || loop_len < 2) return NULL;
    float bg = background_gain < 0 ? 0.6f : background_gain;
    float dk = ducked_gain < 0 ? 0.15f : ducked_gain;
    if (bg > 1.0f || dk > 1.0f) return NULL;
    ca_tela_hold_mixer_t *m = (ca_tela_hold_mixer_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    m->bg = (uint8_t *)malloc(loop_len);
    if (!m->bg) { free(m); return NULL; }
    memcpy(m->bg, background_loop, loop_len);
    m->bg_len = loop_len;
    m->bg_gain = bg;
    m->ducked_gain = dk;
    return m;
}
void ca_tela_hold_mixer_destroy(ca_tela_hold_mixer_t *m) {
    if (!m) return;
    free(m->bg);
    free(m);
}
void ca_tela_hold_mixer_reset(ca_tela_hold_mixer_t *m) { if (m) m->cursor = 0; }

size_t ca_tela_hold_mixer_mix_frame(ca_tela_hold_mixer_t *m,
                                    const uint8_t *speech, size_t speech_len,
                                    uint8_t *dest, size_t dest_len) {
    if (!m || !dest) return SIZE_MAX;
    if (dest_len < 2) return 0;
    bool has_speech = speech && speech_len >= 2;
    size_t frame_len = has_speech ? speech_len : dest_len;
    if (dest_len < frame_len) return SIZE_MAX;
    float gain = has_speech ? m->ducked_gain : m->bg_gain;

    for (size_t i = 0; i + 1 < frame_len; i += 2) {
        int16_t sp = has_speech ? rd16le(speech + i) : 0;
        int16_t bgs = rd16le(m->bg + m->cursor);
        m->cursor = (m->cursor + 2) % m->bg_len;
        if (m->cursor % 2 != 0 && m->cursor > 0) m->cursor--; /* 16-bit align */
        int mixed = (int)sp + (int)((float)bgs * gain);
        wr16le(dest + i, clamp16(mixed));
    }
    return frame_len;
}

/* ===========================================================================
 * WarmTransferOrchestrator
 * =========================================================================== */

void ca_tela_warm_transfer_result_free(ca_tela_warm_transfer_result_t *r) {
    if (!r) return;
    free(r->failure_reason);
    if (r->bridge_session) ca_tel_call_session_destroy(r->bridge_session);
    r->failure_reason = NULL;
    r->bridge_session = NULL;
}

static ca_tela_warm_transfer_result_t *warm_result(bool ok, const char *reason,
                                                   ca_tel_call_session_t *bridge) {
    ca_tela_warm_transfer_result_t *r =
        (ca_tela_warm_transfer_result_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->succeeded = ok;
    r->bridge_session = bridge;
    if (reason) {
        r->failure_reason = ta_strdup(reason);
        if (!r->failure_reason) { free(r); return NULL; }
    }
    return r;
}

ca_tela_warm_transfer_result_t *ca_tela_warm_transfer_execute(
    ca_tel_call_session_t *source, const char *target_number,
    const char *briefing_text, const char *bridge_stream_url,
    ca_tel_carrier_t *carrier, ca_tela_tts_fn tts, void *tts_ctx) {
    if (!carrier || !tts) return NULL;
    if (!source) return warm_result(false, "SourceSession is required", NULL);
    if (ta_is_ws(target_number))
        return warm_result(false, "TargetNumber is required", NULL);

    /* 1) Dial target on a fresh leg. */
    const ca_tel_call_info_t *src_info = ca_tel_call_session_info(source);
    const char *from = src_info ? src_info->to : "";
    ca_tel_call_session_t *bridge =
        ca_tel_carrier_dial(carrier, from, target_number, bridge_stream_url, NULL);
    if (!bridge)
        return warm_result(false, "Failed to dial target: dial returned no session",
                           NULL);

    /* 2) Speak briefing to target. */
    uint8_t *pcm = NULL; size_t pcm_len = 0;
    if (tts(tts_ctx, briefing_text ? briefing_text : "", &pcm, &pcm_len) != 0) {
        ca_tel_call_session_hangup(bridge);
        ca_tel_call_session_destroy(bridge);
        free(pcm);
        return warm_result(false, "Failed to brief target: synthesiser error", NULL);
    }
    if (pcm && pcm_len > 0) {
        ca_tel_audio_frame_t f;
        memset(&f, 0, sizeof(f));
        f.pcm = pcm; f.pcm_len = pcm_len; f.format = CA_TEL_FMT_PCM24000;
        f.offset_ticks = 0;
        int src = ca_tel_call_session_send_audio(bridge, &f);
        free(pcm); pcm = NULL;
        if (src != 0) {
            ca_tel_call_session_hangup(bridge);
            ca_tel_call_session_destroy(bridge);
            return warm_result(false,
                               "Failed to brief target: send failed", NULL);
        }
    } else {
        free(pcm); pcm = NULL;
    }

    /* 3) Hand caller off to target (cold transfer) — the bridge moment. */
    if (ca_tel_call_session_transfer(source, target_number, CA_TEL_TRANSFER_COLD,
                                     NULL) != 0) {
        ca_tel_call_session_hangup(bridge);
        ca_tel_call_session_destroy(bridge);
        return warm_result(false, "Failed to bridge caller: transfer failed", NULL);
    }

    /* 4) AI leg ends; caller + target stay connected. */
    ca_tel_call_session_hangup(bridge);
    return warm_result(true, NULL, bridge);   /* caller owns the hung-up bridge */
}

/* ===========================================================================
 * ConsultEscalation
 * =========================================================================== */

void ca_tela_consult_answer_free(ca_tela_consult_answer_t *a) {
    if (!a) return;
    free(a->answer); free(a->notes);
    a->answer = a->notes = NULL;
}

struct ca_tela_consult_escalator {
    ca_tela_consult_channel_t *channels;
    size_t                     count;
};

ca_tela_consult_escalator_t *ca_tela_consult_escalator_create(
    const ca_tela_consult_channel_t *channels, size_t count) {
    if (count > 0 && !channels) return NULL;
    ca_tela_consult_escalator_t *e =
        (ca_tela_consult_escalator_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    if (count > 0) {
        e->channels = (ca_tela_consult_channel_t *)calloc(
            count, sizeof(ca_tela_consult_channel_t));
        if (!e->channels) { free(e); return NULL; }
        memcpy(e->channels, channels, count * sizeof(ca_tela_consult_channel_t));
        e->count = count;
    }
    return e;
}
void ca_tela_consult_escalator_destroy(ca_tela_consult_escalator_t *e) {
    if (!e) return;
    free(e->channels);
    free(e);
}

int ca_tela_consult_escalator_escalate(ca_tela_consult_escalator_t *e,
                                       const char *call_id, const char *question,
                                       const char *context_json, const char *urgency,
                                       int64_t timeout_per_channel_ticks,
                                       ca_tela_consult_answer_t **out) {
    if (!e || !out) return -1;
    *out = NULL;
    ca_tela_consult_request_t req;
    req.call_id = (char *)(call_id ? call_id : "");
    req.question = (char *)(question ? question : "");
    req.context_json = (char *)(context_json ? context_json : "");
    req.urgency = (char *)(urgency ? urgency : "normal");

    for (size_t i = 0; i < e->count; ++i) {
        ca_tela_consult_channel_t *ch = &e->channels[i];
        if (!ch->ask) continue;
        ca_tela_consult_answer_t *answer = NULL;
        int rc = ch->ask(ch->ctx, &req, timeout_per_channel_ticks, &answer);
        if (rc != 0) continue;          /* channel threw — skip */
        if (answer) { *out = answer; return 0; }
    }
    return 0;
}

/* ===========================================================================
 * AgentHandoff
 * =========================================================================== */

void ca_tela_call_agent_free(ca_tela_call_agent_t *a) {
    if (!a) return;
    free(a->agent_id); free(a->display_name);
    free(a->system_prompt); free(a->greeting_text);
    a->agent_id = a->display_name = a->system_prompt = a->greeting_text = NULL;
}
ca_tela_call_agent_t *ca_tela_call_agent_copy(const ca_tela_call_agent_t *a) {
    if (!a) return NULL;
    ca_tela_call_agent_t *c = (ca_tela_call_agent_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->agent_id = ta_strdup_empty(a->agent_id);
    c->display_name = ta_strdup_empty(a->display_name);
    c->system_prompt = ta_strdup_empty(a->system_prompt);
    c->greeting_text = a->greeting_text ? ta_strdup(a->greeting_text) : NULL;
    if (!c->agent_id || !c->display_name || !c->system_prompt ||
        (a->greeting_text && !c->greeting_text)) {
        ca_tela_call_agent_free(c); free(c); return NULL;
    }
    return c;
}

void ca_tela_handoff_result_free(ca_tela_handoff_result_t *r) {
    if (!r) return;
    free(r->failure_reason);
    if (r->active_agent) { ca_tela_call_agent_free(r->active_agent); free(r->active_agent); }
    r->failure_reason = NULL;
    r->active_agent = NULL;
}

struct ca_tela_handoff {
    ca_tela_call_agent_t *agents; /* owned array */
    size_t                count, cap;
    size_t                current; /* index+1, 0 == none */
};

ca_tela_handoff_t *ca_tela_handoff_create(void) {
    return (ca_tela_handoff_t *)calloc(1, sizeof(ca_tela_handoff_t));
}
void ca_tela_handoff_destroy(ca_tela_handoff_t *h) {
    if (!h) return;
    for (size_t i = 0; i < h->count; ++i) ca_tela_call_agent_free(&h->agents[i]);
    free(h->agents);
    free(h);
}

static size_t handoff_find(const ca_tela_handoff_t *h, const char *agent_id) {
    for (size_t i = 0; i < h->count; ++i)
        if (ta_ieq(h->agents[i].agent_id, agent_id)) return i;
    return SIZE_MAX;
}

ca_tela_call_agent_t *ca_tela_handoff_current_agent(const ca_tela_handoff_t *h) {
    if (!h || h->current == 0) return NULL;
    return ca_tela_call_agent_copy(&h->agents[h->current - 1]);
}

int ca_tela_handoff_register_agent(ca_tela_handoff_t *h, const char *agent_id,
                                   const char *display_name,
                                   const char *system_prompt,
                                   const char *greeting_text) {
    if (!h || ta_is_ws(agent_id)) return -1;
    size_t idx = handoff_find(h, agent_id);
    ca_tela_call_agent_t tmp;
    memset(&tmp, 0, sizeof(tmp));
    tmp.agent_id = ta_strdup(agent_id);
    tmp.display_name = ta_strdup_empty(display_name);
    tmp.system_prompt = ta_strdup_empty(system_prompt);
    tmp.greeting_text = greeting_text ? ta_strdup(greeting_text) : NULL;
    if (!tmp.agent_id || !tmp.display_name || !tmp.system_prompt ||
        (greeting_text && !tmp.greeting_text)) {
        ca_tela_call_agent_free(&tmp);
        return -1;
    }
    if (idx != SIZE_MAX) {
        ca_tela_call_agent_free(&h->agents[idx]);
        h->agents[idx] = tmp;
        return 0;
    }
    if (h->count == h->cap) {
        size_t nc = h->cap ? h->cap * 2 : 4;
        void *ni = realloc(h->agents, nc * sizeof(*h->agents));
        if (!ni) { ca_tela_call_agent_free(&tmp); return -1; }
        h->agents = (ca_tela_call_agent_t *)ni; h->cap = nc;
    }
    h->agents[h->count++] = tmp;
    return 0;
}

size_t ca_tela_handoff_agent_count(const ca_tela_handoff_t *h) {
    return h ? h->count : 0;
}
const ca_tela_call_agent_t *ca_tela_handoff_find_agent(const ca_tela_handoff_t *h,
                                                       const char *agent_id) {
    if (!h) return NULL;
    size_t idx = handoff_find(h, agent_id);
    return idx == SIZE_MAX ? NULL : &h->agents[idx];
}

int ca_tela_handoff_set_initial_agent(ca_tela_handoff_t *h, const char *agent_id) {
    if (!h) return -1;
    size_t idx = handoff_find(h, agent_id);
    if (idx == SIZE_MAX) return -1;
    h->current = idx + 1;
    return 0;
}

static ca_tela_handoff_result_t *handoff_result_new(bool ok, const char *reason,
                                                    const ca_tela_call_agent_t *active) {
    ca_tela_handoff_result_t *r =
        (ca_tela_handoff_result_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->succeeded = ok;
    if (reason) {
        r->failure_reason = ta_strdup(reason);
        if (!r->failure_reason) { free(r); return NULL; }
    }
    if (active) {
        r->active_agent = ca_tela_call_agent_copy(active);
        if (!r->active_agent) { free(r->failure_reason); free(r); return NULL; }
    }
    return r;
}

ca_tela_handoff_result_t *ca_tela_handoff_handoff(
    ca_tela_handoff_t *h, ca_tel_call_session_t *session, const char *target_agent_id,
    ca_tela_tts_fn tts, void *tts_ctx) {
    if (!h || !session || !tts) return NULL;
    const ca_tela_call_agent_t *current =
        h->current ? &h->agents[h->current - 1] : NULL;
    if (ta_is_ws(target_agent_id))
        return handoff_result_new(false, "targetAgentId is required", current);

    size_t idx = handoff_find(h, target_agent_id);
    if (idx == SIZE_MAX) {
        char msg[160];
        snprintf(msg, sizeof(msg), "Agent '%s' is not registered.", target_agent_id);
        return handoff_result_new(false, msg, current);
    }
    ca_tela_call_agent_t *target = &h->agents[idx];
    /* same-agent handoff is a success no-op */
    if (current && ta_ieq(current->agent_id, target->agent_id))
        return handoff_result_new(true, NULL, current);
    h->current = idx + 1;

    /* speak the greeting (failures swallowed) */
    if (!ta_is_ws(target->greeting_text)) {
        uint8_t *pcm = NULL; size_t pcm_len = 0;
        if (tts(tts_ctx, target->greeting_text, &pcm, &pcm_len) == 0 &&
            pcm && pcm_len > 0) {
            ca_tel_audio_frame_t f;
            memset(&f, 0, sizeof(f));
            f.pcm = pcm; f.pcm_len = pcm_len; f.format = CA_TEL_FMT_PCM24000;
            (void)ca_tel_call_session_send_audio(session, &f);
        }
        free(pcm);
    }
    return handoff_result_new(true, NULL, target);
}

/* ===========================================================================
 * LlmJudge
 * =========================================================================== */

void ca_tela_judge_verdict_free(ca_tela_judge_verdict_t *v) {
    if (!v) return;
    for (size_t i = 0; i < v->score_count; ++i) free(v->scores[i].name);
    free(v->scores);
    free(v->overall);
    free(v->reasoning);
    v->scores = NULL; v->score_count = 0;
    v->overall = v->reasoning = NULL;
}

bool ca_tela_judge_verdict_score(const ca_tela_judge_verdict_t *v, const char *name,
                                 int *out_score) {
    if (!v || !name) return false;
    for (size_t i = 0; i < v->score_count; ++i) {
        if (ta_ieq(v->scores[i].name, name)) {
            if (out_score) *out_score = v->scores[i].score;
            return true;
        }
    }
    return false;
}

char *ca_tela_judge_build_prompt(const char *user_utterance,
                                 const char *assistant_response,
                                 const ca_tela_judge_dimension_t *dims,
                                 size_t dim_count) {
    if (!user_utterance || !assistant_response) return NULL;
    /* accumulate into a growable buffer */
    size_t cap = 512, len = 0;
    char *buf = (char *)malloc(cap);
    if (!buf) return NULL;
    buf[0] = '\0';
    #define APPEND(str) do { \
        const char *_s = (str); size_t _l = strlen(_s); \
        if (len + _l + 1 > cap) { \
            while (len + _l + 1 > cap) cap *= 2; \
            char *_ni = (char *)realloc(buf, cap); \
            if (!_ni) { free(buf); return NULL; } \
            buf = _ni; \
        } \
        memcpy(buf + len, _s, _l); len += _l; buf[len] = '\0'; \
    } while (0)

    APPEND("You are an evaluation judge. Score the assistant's reply across the rubric below.\n");
    APPEND("Reply ONLY in this JSON shape:\n");
    APPEND("{ \"scores\": { \"<dim_name>\": <0-10>, ... }, \"overall\": \"pass|borderline|fail\", \"reasoning\": \"<one paragraph>\" }\n");
    APPEND("\nRubric:\n");
    for (size_t i = 0; i < dim_count; ++i) {
        APPEND("- ");
        APPEND(dims[i].name ? dims[i].name : "");
        APPEND(": ");
        APPEND(dims[i].description ? dims[i].description : "");
        APPEND("\n");
    }
    APPEND("\nUser utterance:\n");
    APPEND(user_utterance);
    APPEND("\n\nAssistant reply:\n");
    APPEND(assistant_response);
    #undef APPEND
    return buf;
}

/* Extract the outermost {..} span (tolerate prose/fences). Returns a freshly-owned
 * substring, or a copy of raw if no braces. */
static char *judge_extract_json(const char *raw) {
    const char *start = strchr(raw, '{');
    const char *end = strrchr(raw, '}');
    if (!start || !end || end <= start) return ta_strdup(raw);
    size_t n = (size_t)(end - start) + 1;
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    memcpy(out, start, n); out[n] = '\0';
    return out;
}

/* Minimal scanner: find `"key"` then, after a ':', read either a JSON string
 * value or a bare number/token. Writes an owned copy of the value into *out_val
 * for strings/tokens; for numbers returns the integer via *out_num. Only used on
 * the flat top level + the scores object — sufficient for the judge's shape.
 * Returns a pointer just past the value, or NULL if not found. */
static const char *judge_find_key(const char *json, const char *key) {
    size_t klen = strlen(key);
    for (const char *p = json; *p; ++p) {
        if (*p != '"') continue;
        if (strncmp(p + 1, key, klen) == 0 && p[1 + klen] == '"') {
            const char *q = p + 1 + klen + 1;
            while (*q && *q != ':') ++q;
            if (*q == ':') return q + 1;
        }
    }
    return NULL;
}

/* read a JSON string value starting at `p` (which points at optional ws then '"').
 * owned copy or NULL. */
static char *judge_read_string(const char *p) {
    while (*p && isspace((unsigned char)*p)) ++p;
    if (*p != '"') return NULL;
    ++p;
    size_t cap = 32, len = 0;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    while (*p && *p != '"') {
        char c = *p++;
        if (c == '\\' && *p) {
            char e = *p++;
            switch (e) {
                case 'n': c = '\n'; break;
                case 't': c = '\t'; break;
                case 'r': c = '\r'; break;
                case '"': c = '"'; break;
                case '\\': c = '\\'; break;
                case '/': c = '/'; break;
                default: c = e; break;
            }
        }
        if (len + 1 >= cap) {
            cap *= 2;
            char *ni = (char *)realloc(out, cap);
            if (!ni) { free(out); return NULL; }
            out = ni;
        }
        out[len++] = c;
    }
    out[len] = '\0';
    return out;
}

/* read an int at `p` (skips ws); returns true + value, or false. Handles a quoted
 * number too. */
static bool judge_read_int(const char *p, int *out) {
    while (*p && isspace((unsigned char)*p)) ++p;
    bool quoted = false;
    if (*p == '"') { quoted = true; ++p; }
    char *endp = NULL;
    long v = strtol(p, &endp, 10);
    if (endp == p) return false;
    if (quoted) { while (*endp && isspace((unsigned char)*endp)) ++endp; if (*endp != '"') { /* tolerate */ } }
    *out = (int)v;
    return true;
}

static ca_tela_judge_verdict_t *judge_fallback(const ca_tela_judge_dimension_t *dims,
                                               size_t dim_count) {
    ca_tela_judge_verdict_t *v =
        (ca_tela_judge_verdict_t *)calloc(1, sizeof(*v));
    if (!v) return NULL;
    if (dim_count > 0) {
        v->scores = (ca_tela_judge_score_t *)calloc(dim_count, sizeof(*v->scores));
        if (!v->scores) { free(v); return NULL; }
        for (size_t i = 0; i < dim_count; ++i) {
            v->scores[i].name = ta_strdup_empty(dims[i].name);
            v->scores[i].score = 0;
            if (!v->scores[i].name) {
                for (size_t j = 0; j < i; ++j) free(v->scores[j].name);
                free(v->scores); free(v); return NULL;
            }
        }
        v->score_count = dim_count;
    }
    v->overall = ta_strdup("borderline");
    v->reasoning = ta_strdup("Judge response could not be parsed.");
    if (!v->overall || !v->reasoning) { ca_tela_judge_verdict_free(v); free(v); return NULL; }
    return v;
}

static ca_tela_judge_verdict_t *judge_parse(const char *raw,
                                            const ca_tela_judge_dimension_t *dims,
                                            size_t dim_count) {
    if (!raw) return judge_fallback(dims, dim_count);
    char *json = judge_extract_json(raw);
    if (!json) return NULL;

    const char *scores_at = judge_find_key(json, "scores");
    if (!scores_at) { free(json); return judge_fallback(dims, dim_count); }
    /* find the scores object bounds */
    const char *obj = scores_at;
    while (*obj && *obj != '{') ++obj;
    if (*obj != '{') { free(json); return judge_fallback(dims, dim_count); }

    ca_tela_judge_verdict_t *v =
        (ca_tela_judge_verdict_t *)calloc(1, sizeof(*v));
    if (!v) { free(json); return NULL; }
    if (dim_count > 0) {
        v->scores = (ca_tela_judge_score_t *)calloc(dim_count, sizeof(*v->scores));
        if (!v->scores) { free(v); free(json); return NULL; }
        v->score_count = dim_count;
        for (size_t i = 0; i < dim_count; ++i) {
            v->scores[i].name = ta_strdup_empty(dims[i].name);
            if (!v->scores[i].name) {
                for (size_t j = 0; j < i; ++j) free(v->scores[j].name);
                free(v->scores); free(v); free(json); return NULL;
            }
            v->scores[i].score = 0;
            /* look up this dim's key WITHIN the scores object */
            const char *val = judge_find_key(obj, dims[i].name ? dims[i].name : "");
            if (val) {
                int sc = 0;
                if (judge_read_int(val, &sc)) v->scores[i].score = sc;
            }
        }
    }
    /* overall + reasoning from the top-level json */
    const char *ov = judge_find_key(json, "overall");
    char *overall = ov ? judge_read_string(ov) : NULL;
    v->overall = overall ? overall : ta_strdup("borderline");
    const char *rr = judge_find_key(json, "reasoning");
    char *reason = rr ? judge_read_string(rr) : NULL;
    v->reasoning = reason ? reason : ta_strdup("");
    free(json);
    if (!v->overall || !v->reasoning) { ca_tela_judge_verdict_free(v); free(v); return NULL; }
    return v;
}

ca_tela_judge_verdict_t *ca_tela_judge_run(const char *user_utterance,
                                           const char *assistant_response,
                                           const ca_tela_judge_dimension_t *dims,
                                           size_t dim_count,
                                           ca_tela_judge_completion_fn completion,
                                           void *completion_ctx) {
    if (!user_utterance || !assistant_response || !completion) return NULL;
    char *prompt = ca_tela_judge_build_prompt(user_utterance, assistant_response,
                                              dims, dim_count);
    if (!prompt) return NULL;
    char *raw = completion(completion_ctx, prompt);
    free(prompt);
    ca_tela_judge_verdict_t *v = judge_parse(raw, dims, dim_count);
    free(raw);
    return v;
}

/* ===========================================================================
 * EvalSession
 * =========================================================================== */

void ca_tela_eval_run_result_free(ca_tela_eval_run_result_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->turn_count; ++i) {
        free(r->turns[i].assistant_response);
        ta_strarr_free(r->turns[i].missing_keywords, r->turns[i].missing_count);
    }
    free(r->turns);
    r->turns = NULL; r->turn_count = 0;
}

ca_tela_eval_run_result_t *ca_tela_eval_run(const ca_tela_eval_turn_t *script,
                                            size_t turn_count,
                                            ca_tela_eval_turn_fn handler, void *ctx) {
    if ((turn_count > 0 && !script) || !handler) return NULL;
    ca_tela_eval_run_result_t *res =
        (ca_tela_eval_run_result_t *)calloc(1, sizeof(*res));
    if (!res) return NULL;
    if (turn_count > 0) {
        res->turns = (ca_tela_eval_turn_result_t *)calloc(
            turn_count, sizeof(ca_tela_eval_turn_result_t));
        if (!res->turns) { free(res); return NULL; }
    }
    res->all_keywords_hit = true;

    for (size_t i = 0; i < turn_count; ++i) {
        char *response = NULL;
        int64_t elapsed = 0;
        if (handler(ctx, script[i].user_transcript, &response, &elapsed) != 0) {
            free(response);
            ca_tela_eval_run_result_free(res); free(res); return NULL;
        }
        if (!response) response = ta_strdup("");
        if (!response) { ca_tela_eval_run_result_free(res); free(res); return NULL; }

        ca_tela_eval_turn_result_t *tr = &res->turns[i];
        tr->assistant_response = response;
        tr->latency_ticks = elapsed;
        res->total_latency_ticks += elapsed;

        /* missing keyword scan (case-insensitive substring) */
        char **missing = NULL; size_t mcount = 0, mcap = 0;
        for (size_t k = 0; k < script[i].expected_count; ++k) {
            const char *kw = script[i].expected_keywords[k];
            if (!ta_icontains(response, kw)) {
                if (mcount == mcap) {
                    size_t nc = mcap ? mcap * 2 : 4;
                    void *ni = realloc(missing, nc * sizeof(char *));
                    if (!ni) { ta_strarr_free(missing, mcount); ca_tela_eval_run_result_free(res); free(res); return NULL; }
                    missing = (char **)ni; mcap = nc;
                }
                missing[mcount] = ta_strdup_empty(kw);
                if (!missing[mcount]) { ta_strarr_free(missing, mcount); ca_tela_eval_run_result_free(res); free(res); return NULL; }
                mcount++;
            }
        }
        tr->missing_keywords = missing;
        tr->missing_count = mcount;
        if (mcount > 0) res->all_keywords_hit = false;
        res->turn_count++;
    }
    return res;
}

/* ===========================================================================
 * SentenceChunker
 * =========================================================================== */

struct ca_tela_chunker {
    char  *buffer;   /* owned, NUL-terminated */
    size_t len, cap;
    int    min_sentence_length;
};

ca_tela_chunker_t *ca_tela_chunker_create(int min_sentence_length) {
    ca_tela_chunker_t *c = (ca_tela_chunker_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->min_sentence_length = min_sentence_length > 0 ? min_sentence_length : 4;
    c->cap = 32;
    c->buffer = (char *)malloc(c->cap);
    if (!c->buffer) { free(c); return NULL; }
    c->buffer[0] = '\0';
    return c;
}
void ca_tela_chunker_destroy(ca_tela_chunker_t *c) {
    if (!c) return;
    free(c->buffer);
    free(c);
}

static bool chunker_append(ca_tela_chunker_t *c, const char *s) {
    size_t l = strlen(s);
    if (c->len + l + 1 > c->cap) {
        size_t nc = c->cap;
        while (c->len + l + 1 > nc) nc *= 2;
        char *ni = (char *)realloc(c->buffer, nc);
        if (!ni) return false;
        c->buffer = ni; c->cap = nc;
    }
    memcpy(c->buffer + c->len, s, l + 1);
    c->len += l;
    return true;
}

/* is `ch` a terminal punctuation? (matches the C#'s TerminalPunctuation incl.
 * fullwidth). Operates on a byte; the fullwidth CJK marks are multi-byte in UTF-8
 * so we detect their leading/complete sequences below. Here we only need ASCII. */
static bool chunker_is_terminal_ascii(char ch) {
    return ch == '.' || ch == '!' || ch == '?';
}

/* Detect a terminal punctuation at byte index i (ASCII or one of the 3 fullwidth
 * UTF-8 marks 。！？ = E3 80 82 / EF BC 81 / EF BC 9F). Returns the byte length of
 * the terminal (1 or 3) or 0. */
static int chunker_terminal_at(const char *s, size_t i, size_t n) {
    unsigned char c = (unsigned char)s[i];
    if (c < 0x80) return chunker_is_terminal_ascii((char)c) ? 1 : 0;
    if (i + 3 <= n) {
        unsigned char c0 = c, c1 = (unsigned char)s[i+1], c2 = (unsigned char)s[i+2];
        if (c0 == 0xE3 && c1 == 0x80 && c2 == 0x82) return 3; /* 。 */
        if (c0 == 0xEF && c1 == 0xBC && c2 == 0x81) return 3; /* ！ */
        if (c0 == 0xEF && c1 == 0xBC && c2 == 0x9F) return 3; /* ？ */
    }
    return 0;
}

/* trim leading+trailing ASCII whitespace; returns an owned trimmed copy. */
static char *chunker_trim(const char *s, size_t len) {
    size_t a = 0, b = len;
    while (a < b && isspace((unsigned char)s[a])) ++a;
    while (b > a && isspace((unsigned char)s[b - 1])) --b;
    size_t nl = b - a;
    char *out = (char *)malloc(nl + 1);
    if (!out) return NULL;
    memcpy(out, s + a, nl); out[nl] = '\0';
    return out;
}

/* count characters (bytes here; length gate uses byte length which matches the
 * C# for ASCII and is a safe over/under for multibyte — the min is small). */
static size_t chunker_charlen(const char *s) { return strlen(s); }

size_t ca_tela_chunker_push_token(ca_tela_chunker_t *c, const char *token,
                                  char ***out) {
    if (out) *out = NULL;
    if (!c || !token || token[0] == '\0') return 0;
    if (!chunker_append(c, token)) return SIZE_MAX;

    char **ready = NULL; size_t rcount = 0, rcap = 0;

    for (;;) {
        /* ExtractNext over the current buffer */
        const char *buf = c->buffer;
        size_t n = c->len;
        size_t search_from = 0;
        char  *chunk = NULL;
        size_t kept_from = 0;
        bool   found = false;
        while (search_from < n) {
            size_t idx = search_from;
            int termlen = 0;
            for (; idx < n; ++idx) {
                termlen = chunker_terminal_at(buf, idx, n);
                if (termlen) break;
            }
            if (idx >= n || termlen == 0) break; /* no terminal -> keep whole buffer */

            size_t end = idx + (size_t)termlen;
            while (end < n) {
                unsigned char ch = (unsigned char)buf[end];
                if (ch < 0x80 && (isspace(ch) || ch == '"' || ch == '\'' || ch == ')'))
                    end++;
                else break;
            }
            char *candidate = chunker_trim(buf, end);
            if (!candidate) { ta_strarr_free(ready, rcount); return SIZE_MAX; }
            if (chunker_charlen(candidate) >= (size_t)c->min_sentence_length) {
                chunk = candidate;
                kept_from = end;
                found = true;
                break;
            }
            free(candidate);
            search_from = end;   /* too short — extend past this punctuation */
        }
        if (!found) break;

        /* buffer := kept (buf[kept_from..]) */
        size_t kept_len = c->len - kept_from;
        memmove(c->buffer, c->buffer + kept_from, kept_len);
        c->len = kept_len;
        c->buffer[c->len] = '\0';

        if (rcount == rcap) {
            size_t nc = rcap ? rcap * 2 : 4;
            void *ni = realloc(ready, nc * sizeof(char *));
            if (!ni) { free(chunk); ta_strarr_free(ready, rcount); return SIZE_MAX; }
            ready = (char **)ni; rcap = nc;
        }
        ready[rcount++] = chunk;
    }

    if (out) *out = ready;
    else ta_strarr_free(ready, rcount);
    return rcount;
}

char *ca_tela_chunker_flush(ca_tela_chunker_t *c) {
    if (!c) return NULL;
    char *out = ta_strdup(c->buffer);
    if (!out) return NULL;
    c->len = 0;
    c->buffer[0] = '\0';
    return out;
}

/* ===========================================================================
 * LatencyTracker
 * =========================================================================== */

const char *const CA_TELA_STAGE_ASR_FIRST_WORD    = "asr.first_word";
const char *const CA_TELA_STAGE_ASR_FINAL         = "asr.final";
const char *const CA_TELA_STAGE_LLM_FIRST_TOKEN   = "llm.first_token";
const char *const CA_TELA_STAGE_LLM_FULL_RESPONSE = "llm.full_response";
const char *const CA_TELA_STAGE_TTS_FIRST_AUDIO   = "tts.first_audio";
const char *const CA_TELA_STAGE_TTS_FULL_AUDIO    = "tts.full_audio";
const char *const CA_TELA_STAGE_END_TO_END        = "voice_loop.end_to_end";

void ca_tela_latency_snapshot_free(ca_tela_latency_snapshot_t *s) {
    if (!s) return;
    free(s->stage);
    s->stage = NULL;
}
void ca_tela_latency_snapshot_free_array(ca_tela_latency_snapshot_t *arr, size_t n) {
    if (!arr) return;
    for (size_t i = 0; i < n; ++i) ca_tela_latency_snapshot_free(&arr[i]);
    free(arr);
}

typedef struct {
    char   *stage;      /* owned */
    int64_t*ms;         /* ring of observations in ms */
    size_t  head, count, cap; /* cap == window size */
} latency_queue_t;

struct ca_tela_latency_tracker {
    int              window_size;
    latency_queue_t *queues;
    size_t           queue_count, queue_cap;
};

ca_tela_latency_tracker_t *ca_tela_latency_tracker_create(int window_size) {
    ca_tela_latency_tracker_t *t =
        (ca_tela_latency_tracker_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->window_size = window_size > 0 ? window_size : 256;
    return t;
}
void ca_tela_latency_tracker_destroy(ca_tela_latency_tracker_t *t) {
    if (!t) return;
    for (size_t i = 0; i < t->queue_count; ++i) {
        free(t->queues[i].stage);
        free(t->queues[i].ms);
    }
    free(t->queues);
    free(t);
}

static latency_queue_t *latency_find(const ca_tela_latency_tracker_t *t,
                                     const char *stage) {
    for (size_t i = 0; i < t->queue_count; ++i)
        if (strcmp(t->queues[i].stage, stage) == 0)  /* StringComparer.Ordinal */
            return (latency_queue_t *)&t->queues[i];
    return NULL;
}

void ca_tela_latency_tracker_record(ca_tela_latency_tracker_t *t, const char *stage,
                                    int64_t latency_ticks) {
    if (!t || ta_is_ws(stage)) return;
    if (latency_ticks < 0) return;
    int64_t ms = latency_ticks / CA_TELA_TICKS_PER_MS;
    latency_queue_t *q = latency_find(t, stage);
    if (!q) {
        if (t->queue_count == t->queue_cap) {
            size_t nc = t->queue_cap ? t->queue_cap * 2 : 4;
            void *ni = realloc(t->queues, nc * sizeof(*t->queues));
            if (!ni) return;
            t->queues = (latency_queue_t *)ni; t->queue_cap = nc;
        }
        q = &t->queues[t->queue_count];
        memset(q, 0, sizeof(*q));
        q->stage = ta_strdup(stage);
        if (!q->stage) return;
        q->cap = (size_t)t->window_size;
        q->ms = (int64_t *)malloc(q->cap * sizeof(int64_t));
        if (!q->ms) { free(q->stage); return; }
        t->queue_count++;
    }
    /* enqueue with sliding window (drop oldest) */
    if (q->count < q->cap) {
        size_t tail = (q->head + q->count) % q->cap;
        q->ms[tail] = ms;
        q->count++;
    } else {
        q->ms[q->head] = ms;         /* overwrite oldest */
        q->head = (q->head + 1) % q->cap;
    }
}

static int cmp_i64(const void *a, const void *b) {
    int64_t x = *(const int64_t *)a, y = *(const int64_t *)b;
    return (x > y) - (x < y);
}

static ca_tela_latency_snapshot_t *latency_snapshot_of(const latency_queue_t *q) {
    if (q->count == 0) return NULL;
    int64_t *sorted = (int64_t *)malloc(q->count * sizeof(int64_t));
    if (!sorted) return NULL;
    for (size_t i = 0; i < q->count; ++i)
        sorted[i] = q->ms[(q->head + i) % q->cap];
    qsort(sorted, q->count, sizeof(int64_t), cmp_i64);

    ca_tela_latency_snapshot_t *s =
        (ca_tela_latency_snapshot_t *)calloc(1, sizeof(*s));
    if (!s) { free(sorted); return NULL; }
    s->stage = ta_strdup(q->stage);
    if (!s->stage) { free(s); free(sorted); return NULL; }
    s->samples = (int)q->count;
    #define PCT(p) do { \
        long idx = (long)ceil((p) * (double)q->count) - 1; \
        if (idx < 0) idx = 0; \
        if (idx >= (long)q->count) idx = (long)q->count - 1; \
        _pct = sorted[idx]; \
    } while (0)
    int64_t _pct;
    s->min_ticks = sorted[0] * CA_TELA_TICKS_PER_MS;
    PCT(0.50); s->p50_ticks = _pct * CA_TELA_TICKS_PER_MS;
    PCT(0.95); s->p95_ticks = _pct * CA_TELA_TICKS_PER_MS;
    PCT(0.99); s->p99_ticks = _pct * CA_TELA_TICKS_PER_MS;
    s->max_ticks = sorted[q->count - 1] * CA_TELA_TICKS_PER_MS;
    #undef PCT
    free(sorted);
    return s;
}

ca_tela_latency_snapshot_t *ca_tela_latency_tracker_snapshot(
    const ca_tela_latency_tracker_t *t, const char *stage) {
    if (!t || !stage) return NULL;
    latency_queue_t *q = latency_find(t, stage);
    if (!q) return NULL;
    return latency_snapshot_of(q);
}

ca_tela_latency_snapshot_t *ca_tela_latency_tracker_snapshot_all(
    const ca_tela_latency_tracker_t *t, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!t || t->queue_count == 0) return NULL;
    ca_tela_latency_snapshot_t *arr = (ca_tela_latency_snapshot_t *)calloc(
        t->queue_count, sizeof(*arr));
    if (!arr) return NULL;
    size_t n = 0;
    for (size_t i = 0; i < t->queue_count; ++i) {
        ca_tela_latency_snapshot_t *s = latency_snapshot_of(&t->queues[i]);
        if (s) { arr[n++] = *s; free(s); }
    }
    if (n == 0) { free(arr); return NULL; }
    if (out_count) *out_count = n;
    return arr;
}

void ca_tela_latency_tracker_reset(ca_tela_latency_tracker_t *t, const char *stage) {
    if (!t || !stage) return;
    latency_queue_t *q = latency_find(t, stage);
    if (q) { q->head = q->count = 0; }
}
void ca_tela_latency_tracker_reset_all(ca_tela_latency_tracker_t *t) {
    if (!t) return;
    for (size_t i = 0; i < t->queue_count; ++i) {
        free(t->queues[i].stage);
        free(t->queues[i].ms);
    }
    free(t->queues);
    t->queues = NULL; t->queue_count = t->queue_cap = 0;
}

/* ===========================================================================
 * DashboardData
 * =========================================================================== */

static void dash_live_free(ca_tela_live_call_row_t *r) {
    free(r->call_id); free(r->carrier); free(r->from); free(r->to);
}
static void dash_recent_free(ca_tela_recent_call_row_t *r) {
    free(r->call_id); free(r->carrier); free(r->from); free(r->to);
}
static void dash_health_free(ca_tela_agent_health_row_t *r) {
    free(r->agent_label); free(r->health);
}

void ca_tela_dashboard_snapshot_free(ca_tela_dashboard_snapshot_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->live_count; ++i) dash_live_free(&s->live_calls[i]);
    free(s->live_calls);
    for (size_t i = 0; i < s->recent_count; ++i) dash_recent_free(&s->recent_calls[i]);
    free(s->recent_calls);
    for (size_t i = 0; i < s->agent_count; ++i) dash_health_free(&s->agent_health[i]);
    free(s->agent_health);
    ca_tela_latency_snapshot_free_array(s->latency_by_stage, s->latency_count);
    memset(s, 0, sizeof(*s));
}

static bool dash_copy_live(ca_tela_live_call_row_t *d, const ca_tela_live_call_row_t *s) {
    memset(d, 0, sizeof(*d));
    d->status = s->status; d->started_at_utc_ms = s->started_at_utc_ms;
    d->duration_ticks = s->duration_ticks; d->cost_so_far = s->cost_so_far;
    d->call_id = ta_strdup_empty(s->call_id);
    d->carrier = ta_strdup_empty(s->carrier);
    d->from = ta_strdup_empty(s->from);
    d->to = ta_strdup_empty(s->to);
    return d->call_id && d->carrier && d->from && d->to;
}
static bool dash_copy_recent(ca_tela_recent_call_row_t *d,
                             const ca_tela_recent_call_row_t *s) {
    memset(d, 0, sizeof(*d));
    d->final_status = s->final_status; d->ended_at_utc_ms = s->ended_at_utc_ms;
    d->duration_ticks = s->duration_ticks; d->total_cost = s->total_cost;
    d->call_id = ta_strdup_empty(s->call_id);
    d->carrier = ta_strdup_empty(s->carrier);
    d->from = ta_strdup_empty(s->from);
    d->to = ta_strdup_empty(s->to);
    return d->call_id && d->carrier && d->from && d->to;
}
static bool dash_copy_health(ca_tela_agent_health_row_t *d,
                             const ca_tela_agent_health_row_t *s) {
    memset(d, 0, sizeof(*d));
    d->consecutive_failures = s->consecutive_failures;
    d->agent_label = ta_strdup_empty(s->agent_label);
    d->health = ta_strdup_empty(s->health);
    return d->agent_label && d->health;
}

ca_tela_dashboard_snapshot_t *ca_tela_dashboard_snapshot_build(
    ca_tela_dashboard_summary_t summary,
    const ca_tela_live_call_row_t *live, size_t live_count,
    const ca_tela_recent_call_row_t *recent, size_t recent_count,
    const ca_tela_agent_health_row_t *health, size_t health_count,
    const ca_tela_latency_snapshot_t *latency, size_t latency_count) {
    ca_tela_dashboard_snapshot_t *s =
        (ca_tela_dashboard_snapshot_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->summary = summary;

    if (live_count > 0) {
        s->live_calls = (ca_tela_live_call_row_t *)calloc(
            live_count, sizeof(ca_tela_live_call_row_t));
        if (!s->live_calls) goto fail;
        for (size_t i = 0; i < live_count; ++i) {
            if (!dash_copy_live(&s->live_calls[i], &live[i])) { s->live_count = i + 1; goto fail; }
        }
        s->live_count = live_count;
    }
    if (recent_count > 0) {
        s->recent_calls = (ca_tela_recent_call_row_t *)calloc(
            recent_count, sizeof(ca_tela_recent_call_row_t));
        if (!s->recent_calls) goto fail;
        for (size_t i = 0; i < recent_count; ++i) {
            if (!dash_copy_recent(&s->recent_calls[i], &recent[i])) { s->recent_count = i + 1; goto fail; }
        }
        s->recent_count = recent_count;
    }
    if (health_count > 0) {
        s->agent_health = (ca_tela_agent_health_row_t *)calloc(
            health_count, sizeof(ca_tela_agent_health_row_t));
        if (!s->agent_health) goto fail;
        for (size_t i = 0; i < health_count; ++i) {
            if (!dash_copy_health(&s->agent_health[i], &health[i])) { s->agent_count = i + 1; goto fail; }
        }
        s->agent_count = health_count;
    }
    if (latency_count > 0) {
        s->latency_by_stage = (ca_tela_latency_snapshot_t *)calloc(
            latency_count, sizeof(ca_tela_latency_snapshot_t));
        if (!s->latency_by_stage) goto fail;
        for (size_t i = 0; i < latency_count; ++i) {
            s->latency_by_stage[i] = latency[i];
            s->latency_by_stage[i].stage = ta_strdup_empty(latency[i].stage);
            if (!s->latency_by_stage[i].stage) { s->latency_count = i + 1; goto fail; }
        }
        s->latency_count = latency_count;
    }
    return s;
fail:
    ca_tela_dashboard_snapshot_free(s);
    free(s);
    return NULL;
}

/* ===========================================================================
 * FirstMessagePreamble
 * =========================================================================== */

struct ca_tela_preamble {
    char   *template_text;  /* owned */
    int64_t max_latency_ticks;
};

ca_tela_preamble_t *ca_tela_preamble_create(const char *template_text,
                                            int64_t max_latency_ticks) {
    if (!template_text) return NULL;
    ca_tela_preamble_t *p = (ca_tela_preamble_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->template_text = ta_strdup(template_text);
    if (!p->template_text) { free(p); return NULL; }
    p->max_latency_ticks =
        max_latency_ticks > 0 ? max_latency_ticks : 250 * CA_TELA_TICKS_PER_MS;
    return p;
}
void ca_tela_preamble_destroy(ca_tela_preamble_t *p) {
    if (!p) return;
    free(p->template_text);
    free(p);
}
int64_t ca_tela_preamble_max_latency_ticks(const ca_tela_preamble_t *p) {
    return p ? p->max_latency_ticks : 0;
}

int ca_tela_preamble_speak(ca_tela_preamble_t *p, ca_tel_call_session_t *session,
                           ca_tela_prompt_resolver_t *resolver,
                           bool model_ready_within_window,
                           ca_tela_tts_fn tts, void *tts_ctx) {
    if (!p || !session || !tts) return -1;
    if (model_ready_within_window) return 0;   /* model won the race — skip */

    char *rendered;
    if (resolver) {
        rendered = ca_tela_prompt_resolver_render(resolver, p->template_text);
    } else {
        rendered = ta_strdup(p->template_text);
    }
    if (!rendered) return -1;
    if (ta_is_ws(rendered)) { free(rendered); return 0; }

    uint8_t *pcm = NULL; size_t pcm_len = 0;
    int rc = tts(tts_ctx, rendered, &pcm, &pcm_len);
    free(rendered);
    if (rc != 0) { free(pcm); return -1; }
    if (!pcm || pcm_len == 0) { free(pcm); return 0; }

    ca_tel_audio_frame_t f;
    memset(&f, 0, sizeof(f));
    f.pcm = pcm; f.pcm_len = pcm_len; f.format = CA_TEL_FMT_PCM24000;
    int src = ca_tel_call_session_send_audio(session, &f);
    free(pcm);
    return src == 0 ? 1 : -1;
}

/* ===========================================================================
 * StereoCallRecorder
 * =========================================================================== */

struct ca_tela_stereo_recorder {
    uint8_t *buf;
    size_t   len, cap;
    int      sample_rate_hz;
    int64_t  samples_written;  /* interleaved sample pairs */
    bool     header_written;
    bool     finalized;
};

ca_tela_stereo_recorder_t *ca_tela_stereo_recorder_create(int sample_rate_hz) {
    if (sample_rate_hz <= 0) return NULL;
    ca_tela_stereo_recorder_t *r =
        (ca_tela_stereo_recorder_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->sample_rate_hz = sample_rate_hz;
    return r;
}
void ca_tela_stereo_recorder_destroy(ca_tela_stereo_recorder_t *r) {
    if (!r) return;
    free(r->buf);
    free(r);
}

static bool stereo_reserve(ca_tela_stereo_recorder_t *r, size_t extra) {
    if (r->len + extra <= r->cap) return true;
    size_t nc = r->cap ? r->cap : 64;
    while (r->len + extra > nc) nc *= 2;
    uint8_t *ni = (uint8_t *)realloc(r->buf, nc);
    if (!ni) return false;
    r->buf = ni; r->cap = nc;
    return true;
}

static bool stereo_ensure_header(ca_tela_stereo_recorder_t *r) {
    if (r->header_written) return true;
    if (!stereo_reserve(r, 44)) return false;
    memset(r->buf + r->len, 0, 44);   /* reserve 44-byte header */
    r->len += 44;
    r->header_written = true;
    return true;
}

static int stereo_write_side(ca_tela_stereo_recorder_t *r, const uint8_t *pcm,
                             size_t len, bool is_caller) {
    if (!r || r->finalized) return -1;
    if (!pcm || len < 2) return 0;
    if (!stereo_ensure_header(r)) return -1;
    size_t samples = len / 2;
    if (!stereo_reserve(r, samples * 4)) return -1;
    for (size_t i = 0; i < samples; ++i) {
        int16_t mono = rd16le(pcm + i * 2);
        uint8_t *dst = r->buf + r->len;
        if (is_caller) { wr16le(dst, mono); wr16le(dst + 2, 0); }
        else           { wr16le(dst, 0);    wr16le(dst + 2, mono); }
        r->len += 4;
        r->samples_written++;
    }
    return 0;
}

int ca_tela_stereo_recorder_write_caller(ca_tela_stereo_recorder_t *r,
                                         const uint8_t *pcm, size_t len) {
    return stereo_write_side(r, pcm, len, true);
}
int ca_tela_stereo_recorder_write_agent(ca_tela_stereo_recorder_t *r,
                                        const uint8_t *pcm, size_t len) {
    return stereo_write_side(r, pcm, len, false);
}

static void wr32le(uint8_t *p, int32_t v) {
    p[0] = (uint8_t)((uint32_t)v & 0xFF);
    p[1] = (uint8_t)(((uint32_t)v >> 8) & 0xFF);
    p[2] = (uint8_t)(((uint32_t)v >> 16) & 0xFF);
    p[3] = (uint8_t)(((uint32_t)v >> 24) & 0xFF);
}

void ca_tela_stereo_recorder_finalize(ca_tela_stereo_recorder_t *r) {
    if (!r || r->finalized) return;
    if (!r->header_written) { r->finalized = true; return; }
    int32_t data_size = (int32_t)(r->samples_written * 4);
    int32_t chunk_size = 36 + data_size;
    uint8_t *h = r->buf;
    h[0]='R';h[1]='I';h[2]='F';h[3]='F';
    wr32le(h + 4, chunk_size);
    h[8]='W';h[9]='A';h[10]='V';h[11]='E';
    h[12]='f';h[13]='m';h[14]='t';h[15]=' ';
    wr32le(h + 16, 16);          /* Subchunk1Size */
    wr16le(h + 20, 1);           /* PCM */
    wr16le(h + 22, 2);           /* channels */
    wr32le(h + 24, r->sample_rate_hz);
    wr32le(h + 28, r->sample_rate_hz * 4); /* byte rate */
    wr16le(h + 32, 4);           /* block align */
    wr16le(h + 34, 16);          /* bits per sample */
    h[36]='d';h[37]='a';h[38]='t';h[39]='a';
    wr32le(h + 40, data_size);
    r->finalized = true;
}

const uint8_t *ca_tela_stereo_recorder_data(const ca_tela_stereo_recorder_t *r,
                                            size_t *out_len) {
    if (out_len) *out_len = r ? r->len : 0;
    return r ? r->buf : NULL;
}

/* ===========================================================================
 * AnsweringMachineDetector
 * =========================================================================== */

struct ca_tela_amd {
    int human_max_first_ms;
    int human_min_first_ms;
    int max_observation_ms;
    int silence_threshold_ms;
    double first_utterance_ms;
    double accumulated_ms;
    bool   utterance_in_progress;
    double trailing_silence_ms;
    ca_tela_amd_verdict_t verdict;
};

ca_tela_amd_t *ca_tela_amd_create(int human_max_first_ms, int human_min_first_ms,
                                  int max_observation_ms, int silence_threshold_ms) {
    ca_tela_amd_t *a = (ca_tela_amd_t *)calloc(1, sizeof(*a));
    if (!a) return NULL;
    a->human_max_first_ms = human_max_first_ms > 0 ? human_max_first_ms : 1800;
    a->human_min_first_ms = human_min_first_ms > 0 ? human_min_first_ms : 300;
    a->max_observation_ms = max_observation_ms > 0 ? max_observation_ms : 3500;
    a->silence_threshold_ms = silence_threshold_ms > 0 ? silence_threshold_ms : 250;
    a->verdict = CA_TELA_AMD_UNKNOWN;
    return a;
}
void ca_tela_amd_destroy(ca_tela_amd_t *a) { free(a); }

ca_tela_amd_verdict_t ca_tela_amd_current(const ca_tela_amd_t *a) {
    return a ? a->verdict : CA_TELA_AMD_UNKNOWN;
}

static bool amd_frame_has_speech(const uint8_t *pcm, size_t len) {
    const float energy_threshold = 0.012f;
    size_t samples = len / 2;
    if (samples == 0) return false;
    double sum_squares = 0.0;
    for (size_t i = 0; i < samples; ++i) {
        int s = rd16le(pcm + i * 2);
        sum_squares += (double)s * (double)s;
    }
    double rms = sqrt(sum_squares / (double)samples) / 32767.0;
    return rms >= energy_threshold;
}

ca_tela_amd_verdict_t ca_tela_amd_observe(ca_tela_amd_t *a, const uint8_t *pcm,
                                          size_t len, int sample_rate_hz) {
    if (!a) return CA_TELA_AMD_UNKNOWN;
    if (sample_rate_hz <= 0) return a->verdict;
    if (!pcm || len < 2) return a->verdict;
    if (a->verdict != CA_TELA_AMD_UNKNOWN) return a->verdict;

    double frame_ms = 1000.0 * (double)(len / 2) / (double)sample_rate_hz;
    bool is_speech = amd_frame_has_speech(pcm, len);

    a->accumulated_ms += frame_ms;
    if (is_speech) {
        if (!a->utterance_in_progress) a->utterance_in_progress = true;
        a->first_utterance_ms += frame_ms;
        a->trailing_silence_ms = 0.0;
    } else if (a->utterance_in_progress) {
        a->trailing_silence_ms += frame_ms;
        if (a->trailing_silence_ms >= (double)a->silence_threshold_ms)
            a->utterance_in_progress = false;
    }

    double first_ms = a->first_utterance_ms;
    if (first_ms >= (double)a->human_max_first_ms) {
        a->verdict = CA_TELA_AMD_ANSWERING_MACHINE;
    } else if (!a->utterance_in_progress &&
               first_ms >= (double)a->human_min_first_ms &&
               first_ms <  (double)a->human_max_first_ms) {
        a->verdict = CA_TELA_AMD_HUMAN;
    } else if (a->accumulated_ms >= (double)a->max_observation_ms) {
        a->verdict = first_ms < (double)a->human_min_first_ms
            ? CA_TELA_AMD_UNKNOWN : CA_TELA_AMD_ANSWERING_MACHINE;
    }
    return a->verdict;
}

void ca_tela_amd_reset(ca_tela_amd_t *a) {
    if (!a) return;
    a->first_utterance_ms = 0.0;
    a->accumulated_ms = 0.0;
    a->utterance_in_progress = false;
    a->trailing_silence_ms = 0.0;
    a->verdict = CA_TELA_AMD_UNKNOWN;
}

/* ===========================================================================
 * VoiceLoopTelemetry
 * =========================================================================== */

const char *const CA_TELA_TELEMETRY_SOURCE_NAME = "CircleAI.Telephony.VoiceLoop";

struct ca_tela_span {
    char                 *name;   /* owned */
    ca_tela_span_status_t status;
    ca_tela_span_tag_t   *tags;   /* owned */
    size_t                tag_count, tag_cap;
};

static bool span_set_tag(ca_tela_span_t *s, const char *key, const char *value) {
    /* replace if key exists */
    for (size_t i = 0; i < s->tag_count; ++i) {
        if (strcmp(s->tags[i].key, key) == 0) {
            char *nv = value ? ta_strdup(value) : NULL;
            if (value && !nv) return false;
            free(s->tags[i].value);
            s->tags[i].value = nv;
            return true;
        }
    }
    if (s->tag_count == s->tag_cap) {
        size_t nc = s->tag_cap ? s->tag_cap * 2 : 4;
        void *ni = realloc(s->tags, nc * sizeof(*s->tags));
        if (!ni) return false;
        s->tags = (ca_tela_span_tag_t *)ni; s->tag_cap = nc;
    }
    s->tags[s->tag_count].key = ta_strdup(key);
    if (!s->tags[s->tag_count].key) return false;
    s->tags[s->tag_count].value = value ? ta_strdup(value) : NULL;
    if (value && !s->tags[s->tag_count].value) {
        free(s->tags[s->tag_count].key);
        return false;
    }
    s->tag_count++;
    return true;
}

static ca_tela_span_t *span_new(const char *name) {
    ca_tela_span_t *s = (ca_tela_span_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->name = ta_strdup_empty(name);
    if (!s->name) { free(s); return NULL; }
    s->status = CA_TELA_SPAN_STATUS_UNSET;
    return s;
}

ca_tela_span_t *ca_tela_telemetry_start_turn(const char *call_id) {
    ca_tela_span_t *s = span_new("voice_loop.turn");
    if (!s) return NULL;
    if (!span_set_tag(s, "call.id", call_id)) { ca_tela_span_destroy(s); return NULL; }
    return s;
}
ca_tela_span_t *ca_tela_telemetry_start_asr(const char *backend) {
    ca_tela_span_t *s = span_new("voice_loop.asr");
    if (!s) return NULL;
    if (!span_set_tag(s, "backend", backend)) { ca_tela_span_destroy(s); return NULL; }
    return s;
}
ca_tela_span_t *ca_tela_telemetry_start_llm(const char *provider, const char *model) {
    ca_tela_span_t *s = span_new("voice_loop.llm");
    if (!s) return NULL;
    if (!span_set_tag(s, "provider", provider) || !span_set_tag(s, "model", model)) {
        ca_tela_span_destroy(s); return NULL;
    }
    return s;
}
ca_tela_span_t *ca_tela_telemetry_start_tts(const char *backend, const char *voice_id) {
    ca_tela_span_t *s = span_new("voice_loop.tts");
    if (!s) return NULL;
    if (!span_set_tag(s, "backend", backend) || !span_set_tag(s, "voice", voice_id)) {
        ca_tela_span_destroy(s); return NULL;
    }
    return s;
}
void ca_tela_span_destroy(ca_tela_span_t *s) {
    if (!s) return;
    free(s->name);
    for (size_t i = 0; i < s->tag_count; ++i) {
        free(s->tags[i].key);
        free(s->tags[i].value);
    }
    free(s->tags);
    free(s);
}

const char *ca_tela_span_name(const ca_tela_span_t *s) { return s ? s->name : NULL; }
ca_tela_span_status_t ca_tela_span_status(const ca_tela_span_t *s) {
    return s ? s->status : CA_TELA_SPAN_STATUS_UNSET;
}
size_t ca_tela_span_tag_count(const ca_tela_span_t *s) { return s ? s->tag_count : 0; }
const char *ca_tela_span_tag(const ca_tela_span_t *s, const char *key) {
    if (!s || !key) return NULL;
    for (size_t i = 0; i < s->tag_count; ++i)
        if (strcmp(s->tags[i].key, key) == 0) return s->tags[i].value;
    return NULL;
}

void ca_tela_telemetry_record_outcome(ca_tela_span_t *s, bool success,
                                      const char *error_reason) {
    if (!s) return;
    span_set_tag(s, "outcome", success ? "success" : "failure");
    if (!success && error_reason) {
        span_set_tag(s, "error.message", error_reason);
        s->status = CA_TELA_SPAN_STATUS_ERROR;
    } else if (success) {
        s->status = CA_TELA_SPAN_STATUS_OK;
    }
}

/* ===========================================================================
 * StreamingToolProgress
 * =========================================================================== */

void ca_tela_tool_progress_free(ca_tela_tool_progress_t *u) {
    if (!u) return;
    free(u->call_id); free(u->status_text);
    u->call_id = u->status_text = NULL;
}

static bool progress_copy(ca_tela_tool_progress_t *d,
                          const ca_tela_tool_progress_t *s) {
    memset(d, 0, sizeof(*d));
    d->percent_complete = s->percent_complete;
    d->emitted_at_utc_ms = s->emitted_at_utc_ms;
    d->call_id = ta_strdup_empty(s->call_id);
    d->status_text = s->status_text ? ta_strdup(s->status_text) : NULL;
    return d->call_id && (!s->status_text || d->status_text);
}

struct ca_tela_recording_sink {
    ca_tela_tool_progress_t *updates;
    size_t                   count, cap;
};

ca_tela_recording_sink_t *ca_tela_recording_sink_create(void) {
    return (ca_tela_recording_sink_t *)calloc(1, sizeof(ca_tela_recording_sink_t));
}
void ca_tela_recording_sink_destroy(ca_tela_recording_sink_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_tela_tool_progress_free(&s->updates[i]);
    free(s->updates);
    free(s);
}
int ca_tela_recording_sink_emit(ca_tela_recording_sink_t *s,
                                const ca_tela_tool_progress_t *update) {
    if (!s || !update) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *ni = realloc(s->updates, nc * sizeof(*s->updates));
        if (!ni) return -1;
        s->updates = (ca_tela_tool_progress_t *)ni; s->cap = nc;
    }
    if (!progress_copy(&s->updates[s->count], update)) {
        ca_tela_tool_progress_free(&s->updates[s->count]);
        return -1;
    }
    s->count++;
    return 0;
}
size_t ca_tela_recording_sink_count(const ca_tela_recording_sink_t *s) {
    return s ? s->count : 0;
}
ca_tela_tool_progress_t *ca_tela_recording_sink_updates(
    const ca_tela_recording_sink_t *s, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!s || s->count == 0) return NULL;
    ca_tela_tool_progress_t *arr = (ca_tela_tool_progress_t *)calloc(
        s->count, sizeof(*arr));
    if (!arr) return NULL;
    for (size_t i = 0; i < s->count; ++i) {
        if (!progress_copy(&arr[i], &s->updates[i])) {
            for (size_t j = 0; j <= i; ++j) ca_tela_tool_progress_free(&arr[j]);
            free(arr);
            return NULL;
        }
    }
    if (out_count) *out_count = s->count;
    return arr;
}

struct ca_tela_spoken_sink {
    ca_tel_call_session_t *session; /* borrowed */
    ca_tela_tts_fn         tts;
    void                  *tts_ctx;
    int64_t                min_interval_ticks;
    bool                   has_last;
    int64_t                last_spoken_ms;
};

ca_tela_spoken_sink_t *ca_tela_spoken_sink_create(ca_tel_call_session_t *session,
                                                  ca_tela_tts_fn tts, void *tts_ctx,
                                                  int64_t min_interval_ticks) {
    if (!session || !tts) return NULL;
    ca_tela_spoken_sink_t *s = (ca_tela_spoken_sink_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->session = session;
    s->tts = tts;
    s->tts_ctx = tts_ctx;
    s->min_interval_ticks =
        min_interval_ticks > 0 ? min_interval_ticks : 2 * CA_TELA_TICKS_PER_SEC;
    return s;
}
void ca_tela_spoken_sink_destroy(ca_tela_spoken_sink_t *s) { free(s); }

int ca_tela_spoken_sink_emit(ca_tela_spoken_sink_t *s,
                             const ca_tela_tool_progress_t *update, int64_t now_utc_ms) {
    if (!s || !update) return -1;
    if (ta_is_ws(update->status_text)) return 0;
    int64_t min_ms = s->min_interval_ticks / CA_TELA_TICKS_PER_MS;
    bool should_speak = !s->has_last || (now_utc_ms - s->last_spoken_ms) >= min_ms;
    if (!should_speak) return 0;
    s->has_last = true;
    s->last_spoken_ms = now_utc_ms;

    uint8_t *pcm = NULL; size_t pcm_len = 0;
    if (s->tts(s->tts_ctx, update->status_text, &pcm, &pcm_len) != 0) {
        free(pcm); return -1;
    }
    int rc = 0;
    if (pcm && pcm_len > 0) {
        ca_tel_audio_frame_t f;
        memset(&f, 0, sizeof(f));
        f.pcm = pcm; f.pcm_len = pcm_len; f.format = CA_TEL_FMT_PCM24000;
        rc = ca_tel_call_session_send_audio(s->session, &f);
    }
    free(pcm);
    return rc == 0 ? 1 : -1;
}

ca_tel_tool_result_t *ca_tela_streaming_tool_run(
    const ca_tel_tool_invocation_t *invocation, ca_tela_streaming_tool_fn handler,
    void *ctx, ca_tela_recording_sink_t *sink) {
    if (!invocation || !handler) return NULL;
    ca_tel_tool_result_t *res = (ca_tel_tool_result_t *)calloc(1, sizeof(*res));
    if (!res) return NULL;
    res->call_id = ta_strdup_empty(invocation->call_id);
    if (!res->call_id) { free(res); return NULL; }

    char *out = NULL;
    int rc = handler(ctx, invocation->arguments_json, sink, &out);
    if (rc == 0) {
        res->succeeded = true;
        res->result_json = out ? out : ta_strdup("{}");
        res->error = NULL;
        if (!res->result_json) { ca_tel_tool_result_free(res); free(res); return NULL; }
    } else {
        free(out);
        res->succeeded = false;
        res->result_json = ta_strdup("{}");
        res->error = ta_strdup("The tool handler threw an exception.");
        if (!res->result_json || !res->error) {
            ca_tel_tool_result_free(res); free(res); return NULL;
        }
    }
    return res;
}

/* ===========================================================================
 * VoiceLoopAsTool
 * =========================================================================== */

void ca_tela_voiceloop_result_free(ca_tela_voiceloop_result_t *r) {
    if (!r) return;
    free(r->summary); free(r->call_id); free(r->transcript);
    free(r->structured_output_json);
    r->summary = r->call_id = r->transcript = r->structured_output_json = NULL;
}

ca_tel_tool_definition_t ca_tela_voiceloop_descriptor(void) {
    ca_tel_tool_definition_t d;
    memset(&d, 0, sizeof(d));
    d.name = ta_strdup("make_voice_call");
    d.description = ta_strdup(
        "Place an outbound phone call and follow the supplied goal/script. "
        "Returns whether the goal was achieved.");
    d.arguments_json_schema = ta_strdup(
        "{\n"
        "  \"type\": \"object\",\n"
        "  \"properties\": {\n"
        "    \"to_number\":     { \"type\": \"string\", \"description\": \"E.164 destination.\" },\n"
        "    \"goal\":          { \"type\": \"string\" },\n"
        "    \"context_json\":  { \"type\": \"string\", \"nullable\": true },\n"
        "    \"system_prompt\": { \"type\": \"string\", \"nullable\": true },\n"
        "    \"max_duration_seconds\": { \"type\": \"integer\", \"nullable\": true }\n"
        "  },\n"
        "  \"required\": [\"to_number\", \"goal\"]\n"
        "}");
    /* on OOM leave fields NULL — caller frees safely */
    return d;
}

ca_tela_voiceloop_result_t *ca_tela_voiceloop_invoke(
    const ca_tela_voiceloop_request_t *request, ca_tela_voiceloop_runner_fn runner,
    void *runner_ctx, int64_t default_max_duration_ticks) {
    if (!request || !runner) return NULL;
    if (ta_is_ws(request->to_number)) return NULL;   /* ToNumber required */
    if (ta_is_ws(request->goal)) return NULL;         /* Goal required */

    int64_t def = default_max_duration_ticks > 0
        ? default_max_duration_ticks : 5 * 60 * CA_TELA_TICKS_PER_SEC;
    int64_t max_duration = request->max_duration_ticks > 0
        ? request->max_duration_ticks : def;

    /* Build the effective request passed to the runner (with the resolved max). */
    ca_tela_voiceloop_request_t eff = *request;
    eff.max_duration_ticks = max_duration;

    ca_tela_voiceloop_result_t *out = NULL;
    int rc = runner(runner_ctx, &eff, &out);
    if (rc == 0 && out) return out;

    if (out) { ca_tela_voiceloop_result_free(out); free(out); }
    /* timeout path */
    ca_tela_voiceloop_result_t *r =
        (ca_tela_voiceloop_result_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    double minutes = (double)max_duration / (double)(60 * CA_TELA_TICKS_PER_SEC);
    char summary[96];
    snprintf(summary, sizeof(summary), "Call timed out after %.1f minutes.", minutes);
    r->goal_achieved = false;
    r->summary = ta_strdup(summary);
    r->call_id = ta_strdup("");
    r->duration_ticks = max_duration;
    r->transcript = ta_strdup("");
    r->structured_output_json = NULL;
    if (!r->summary || !r->call_id || !r->transcript) {
        ca_tela_voiceloop_result_free(r); free(r); return NULL;
    }
    return r;
}

/* ===========================================================================
 * PromptVariableResolver
 * =========================================================================== */

typedef struct {
    char *name;   /* owned */
    char *value;  /* owned */
} prompt_static_t;

typedef struct {
    char                      *name;    /* owned */
    ca_tela_prompt_provider_fn provider;
    void                      *ctx;
} prompt_provider_t;

struct ca_tela_prompt_resolver {
    prompt_static_t   *statics;
    size_t             static_count, static_cap;
    prompt_provider_t *providers;
    size_t             provider_count, provider_cap;
    char              *default_missing; /* owned */
};

ca_tela_prompt_resolver_t *ca_tela_prompt_resolver_create(const char *default_missing) {
    ca_tela_prompt_resolver_t *r =
        (ca_tela_prompt_resolver_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->default_missing = ta_strdup(default_missing ? default_missing : "");
    if (!r->default_missing) { free(r); return NULL; }
    return r;
}
void ca_tela_prompt_resolver_destroy(ca_tela_prompt_resolver_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->static_count; ++i) {
        free(r->statics[i].name); free(r->statics[i].value);
    }
    free(r->statics);
    for (size_t i = 0; i < r->provider_count; ++i) free(r->providers[i].name);
    free(r->providers);
    free(r->default_missing);
    free(r);
}

int ca_tela_prompt_resolver_set(ca_tela_prompt_resolver_t *r, const char *name,
                                const char *value) {
    if (!r || ta_is_ws(name)) return -1;
    for (size_t i = 0; i < r->static_count; ++i) {
        if (ta_ieq(r->statics[i].name, name)) {
            char *nv = ta_strdup_empty(value);
            if (!nv) return -1;
            free(r->statics[i].value);
            r->statics[i].value = nv;
            return 0;
        }
    }
    if (r->static_count == r->static_cap) {
        size_t nc = r->static_cap ? r->static_cap * 2 : 4;
        void *ni = realloc(r->statics, nc * sizeof(*r->statics));
        if (!ni) return -1;
        r->statics = (prompt_static_t *)ni; r->static_cap = nc;
    }
    r->statics[r->static_count].name = ta_strdup(name);
    r->statics[r->static_count].value = ta_strdup_empty(value);
    if (!r->statics[r->static_count].name || !r->statics[r->static_count].value) {
        free(r->statics[r->static_count].name);
        free(r->statics[r->static_count].value);
        return -1;
    }
    r->static_count++;
    return 0;
}

int ca_tela_prompt_resolver_set_provider(ca_tela_prompt_resolver_t *r,
                                         const char *name,
                                         ca_tela_prompt_provider_fn provider,
                                         void *ctx) {
    if (!r || ta_is_ws(name) || !provider) return -1;
    for (size_t i = 0; i < r->provider_count; ++i) {
        if (ta_ieq(r->providers[i].name, name)) {
            r->providers[i].provider = provider;
            r->providers[i].ctx = ctx;
            return 0;
        }
    }
    if (r->provider_count == r->provider_cap) {
        size_t nc = r->provider_cap ? r->provider_cap * 2 : 4;
        void *ni = realloc(r->providers, nc * sizeof(*r->providers));
        if (!ni) return -1;
        r->providers = (prompt_provider_t *)ni; r->provider_cap = nc;
    }
    r->providers[r->provider_count].name = ta_strdup(name);
    if (!r->providers[r->provider_count].name) return -1;
    r->providers[r->provider_count].provider = provider;
    r->providers[r->provider_count].ctx = ctx;
    r->provider_count++;
    return 0;
}

/* Is `c` a valid variable-name start char ([A-Za-z_])? */
static bool prompt_name_start(char c) {
    return isalpha((unsigned char)c) || c == '_';
}
/* continuation [A-Za-z0-9_.] */
static bool prompt_name_cont(char c) {
    return isalnum((unsigned char)c) || c == '_' || c == '.';
}

/* Resolve one variable name to an owned string (statics, then providers, else
 * default-missing). */
static char *prompt_resolve_one(ca_tela_prompt_resolver_t *r, const char *name) {
    for (size_t i = 0; i < r->static_count; ++i)
        if (ta_ieq(r->statics[i].name, name))
            return ta_strdup(r->statics[i].value);
    for (size_t i = 0; i < r->provider_count; ++i) {
        if (ta_ieq(r->providers[i].name, name)) {
            char *val = NULL;
            int rc = r->providers[i].provider(r->providers[i].ctx, name, &val);
            if (rc == 0 && val) return val;
            free(val);
            return ta_strdup(r->default_missing);
        }
    }
    return ta_strdup(r->default_missing);
}

char *ca_tela_prompt_resolver_render(ca_tela_prompt_resolver_t *r,
                                     const char *template_text) {
    if (!r) return NULL;
    if (!template_text || template_text[0] == '\0') return ta_strdup("");

    size_t n = strlen(template_text);
    size_t cap = n + 16, len = 0;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;

    size_t i = 0;
    while (i < n) {
        /* match {{ \s* name \s* }} */
        if (template_text[i] == '{' && i + 1 < n && template_text[i + 1] == '{') {
            size_t j = i + 2;
            while (j < n && isspace((unsigned char)template_text[j])) ++j;
            size_t name_start = j;
            if (j < n && prompt_name_start(template_text[j])) {
                ++j;
                while (j < n && prompt_name_cont(template_text[j])) ++j;
                size_t name_end = j;
                while (j < n && isspace((unsigned char)template_text[j])) ++j;
                if (j + 1 < n && template_text[j] == '}' && template_text[j + 1] == '}') {
                    /* extract name */
                    size_t nl = name_end - name_start;
                    char *name = (char *)malloc(nl + 1);
                    if (!name) { free(out); return NULL; }
                    memcpy(name, template_text + name_start, nl); name[nl] = '\0';
                    char *val = prompt_resolve_one(r, name);
                    free(name);
                    if (!val) { free(out); return NULL; }
                    size_t vl = strlen(val);
                    if (len + vl + 1 > cap) {
                        while (len + vl + 1 > cap) cap *= 2;
                        char *ni = (char *)realloc(out, cap);
                        if (!ni) { free(val); free(out); return NULL; }
                        out = ni;
                    }
                    memcpy(out + len, val, vl); len += vl;
                    free(val);
                    i = j + 2;   /* past }} */
                    continue;
                }
            }
        }
        /* literal char */
        if (len + 2 > cap) {
            cap *= 2;
            char *ni = (char *)realloc(out, cap);
            if (!ni) { free(out); return NULL; }
            out = ni;
        }
        out[len++] = template_text[i++];
    }
    out[len] = '\0';
    return out;
}

/* ===========================================================================
 * LocalDevTunnel
 * =========================================================================== */

typedef enum { TUNNEL_NULL, TUNNEL_STATIC, TUNNEL_CLOUDFLARE, TUNNEL_NGROK } tunnel_kind_t;

struct ca_tela_tunnel {
    tunnel_kind_t             kind;
    char                     *provider_id;  /* owned */
    char                     *static_url;   /* owned (static) or NULL */
    ca_tela_tunnel_resolve_fn resolver;
    void                     *ctx;
};

static ca_tela_tunnel_t *tunnel_alloc(tunnel_kind_t kind, const char *provider_id) {
    ca_tela_tunnel_t *t = (ca_tela_tunnel_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->kind = kind;
    t->provider_id = ta_strdup(provider_id);
    if (!t->provider_id) { free(t); return NULL; }
    return t;
}

ca_tela_tunnel_t *ca_tela_tunnel_create_null(void) {
    return tunnel_alloc(TUNNEL_NULL, "null");
}
ca_tela_tunnel_t *ca_tela_tunnel_create_static(const char *public_url) {
    if (!public_url || !strstr(public_url, "://")) return NULL; /* must be absolute */
    ca_tela_tunnel_t *t = tunnel_alloc(TUNNEL_STATIC, "static");
    if (!t) return NULL;
    t->static_url = ta_strdup(public_url);
    if (!t->static_url) { ca_tela_tunnel_destroy(t); return NULL; }
    return t;
}
ca_tela_tunnel_t *ca_tela_tunnel_create_cloudflare(ca_tela_tunnel_resolve_fn resolver,
                                                   void *ctx) {
    if (!resolver) return NULL;
    ca_tela_tunnel_t *t = tunnel_alloc(TUNNEL_CLOUDFLARE, "cloudflare");
    if (!t) return NULL;
    t->resolver = resolver; t->ctx = ctx;
    return t;
}
ca_tela_tunnel_t *ca_tela_tunnel_create_ngrok(ca_tela_tunnel_resolve_fn resolver,
                                              void *ctx) {
    if (!resolver) return NULL;
    ca_tela_tunnel_t *t = tunnel_alloc(TUNNEL_NGROK, "ngrok");
    if (!t) return NULL;
    t->resolver = resolver; t->ctx = ctx;
    return t;
}
void ca_tela_tunnel_destroy(ca_tela_tunnel_t *t) {
    if (!t) return;
    free(t->provider_id);
    free(t->static_url);
    free(t);
}

const char *ca_tela_tunnel_provider_id(const ca_tela_tunnel_t *t) {
    return t ? t->provider_id : NULL;
}
bool ca_tela_tunnel_is_available(const ca_tela_tunnel_t *t) {
    if (!t) return false;
    return t->kind != TUNNEL_NULL;
}
int ca_tela_tunnel_get_public_url(ca_tela_tunnel_t *t, int local_port, char **out) {
    if (out) *out = NULL;
    if (!t || !out) return -1;
    switch (t->kind) {
        case TUNNEL_NULL:
            return -1;   /* InvalidOperationException — no tunnel configured */
        case TUNNEL_STATIC: {
            char *u = ta_strdup(t->static_url);
            if (!u) return -1;
            *out = u;
            return 0;
        }
        case TUNNEL_CLOUDFLARE:
        case TUNNEL_NGROK:
            if (!t->resolver) return -1;
            return t->resolver(t->ctx, local_port, out);
    }
    return -1;
}

/* ===========================================================================
 * McpToolImporter
 * =========================================================================== */

/* Append "?key=value" (URL-encoded value) to a base endpoint. Owned or NULL. */
static char *mcp_append_query(const char *base, const char *key, const char *value) {
    /* URL-encode value (RFC 3986 unreserved kept; others %XX). */
    size_t vlen = value ? strlen(value) : 0;
    /* worst-case 3x */
    char *enc = (char *)malloc(vlen * 3 + 1);
    if (!enc) return NULL;
    size_t e = 0;
    for (size_t i = 0; i < vlen; ++i) {
        unsigned char c = (unsigned char)value[i];
        if (isalnum(c) || c == '-' || c == '_' || c == '.' || c == '~') {
            enc[e++] = (char)c;
        } else {
            static const char *hex = "0123456789ABCDEF";
            enc[e++] = '%';
            enc[e++] = hex[(c >> 4) & 0xF];
            enc[e++] = hex[c & 0xF];
        }
    }
    enc[e] = '\0';

    bool has_query = strchr(base, '?') != NULL;
    const char *sep = has_query ? "&" : "?";
    size_t need = strlen(base) + strlen(sep) + strlen(key) + 1 + e + 1;
    char *out = (char *)malloc(need);
    if (!out) { free(enc); return NULL; }
    snprintf(out, need, "%s%s%s=%s", base, sep, key, enc);
    free(enc);
    return out;
}

/* Extract the value of a top-level-ish JSON string field named `key` occurring
 * after position `from`. Returns owned string + advances *from past it, or NULL. */
static char *mcp_json_string_field(const char *json, size_t start, const char *key,
                                   size_t *found_end) {
    size_t klen = strlen(key);
    for (const char *p = json + start; *p; ++p) {
        if (*p == '"' && strncmp(p + 1, key, klen) == 0 && p[1 + klen] == '"') {
            const char *q = p + 1 + klen + 1;
            while (*q && *q != ':') ++q;
            if (*q != ':') continue;
            ++q;
            char *val = judge_read_string(q);
            if (val && found_end) *found_end = (size_t)(q - json);
            return val;
        }
    }
    return NULL;
}

/* Extract the raw text of an object/array field named `key` (from `inputSchema`)
 * as a JSON substring. Returns owned or NULL (defaults to "{}" handled by caller).
 * Scans braces/brackets with string awareness. */
static char *mcp_json_raw_field(const char *json, size_t start, const char *key) {
    size_t klen = strlen(key);
    for (const char *p = json + start; *p; ++p) {
        if (*p == '"' && strncmp(p + 1, key, klen) == 0 && p[1 + klen] == '"') {
            const char *q = p + 1 + klen + 1;
            while (*q && *q != ':') ++q;
            if (*q != ':') continue;
            ++q;
            while (*q && isspace((unsigned char)*q)) ++q;
            if (*q != '{' && *q != '[') return NULL;
            char open = *q, close = (open == '{') ? '}' : ']';
            const char *s = q;
            int depth = 0;
            bool in_str = false;
            for (; *q; ++q) {
                char c = *q;
                if (in_str) {
                    if (c == '\\' && q[1]) { ++q; continue; }
                    if (c == '"') in_str = false;
                } else {
                    if (c == '"') in_str = true;
                    else if (c == open) depth++;
                    else if (c == close) { depth--; if (depth == 0) { ++q; break; } }
                }
            }
            size_t rawlen = (size_t)(q - s);
            char *out = (char *)malloc(rawlen + 1);
            if (!out) return NULL;
            memcpy(out, s, rawlen); out[rawlen] = '\0';
            return out;
        }
    }
    return NULL;
}

/* Locate the "tools" array and iterate its object elements, invoking `cb` with the
 * substring bounds of each element. Returns the element count processed. */
size_t ca_tela_mcp_import(ca_tel_tool_registry_t *registry,
                          const char *server_endpoint, const char *tool_name_prefix,
                          const char *tools_list_json,
                          ca_tel_tool_definition_t **out) {
    if (out) *out = NULL;
    if (!registry || !server_endpoint || !tools_list_json) return SIZE_MAX;

    /* find "result" then "tools": [ ... ] */
    const char *result_at = judge_find_key(tools_list_json, "result");
    if (!result_at) { return 0; }   /* no result -> nothing imported */
    /* find "tools" after result */
    size_t result_off = (size_t)(result_at - tools_list_json);
    const char *tools_key = NULL;
    for (const char *p = tools_list_json + result_off; *p; ++p) {
        if (*p == '"' && strncmp(p + 1, "tools\"", 6) == 0) { tools_key = p; break; }
    }
    if (!tools_key) return 0;
    const char *q = tools_key + 1 + 5 + 1; /* past "tools" */
    while (*q && *q != ':') ++q;
    if (*q != ':') return 0;
    ++q;
    while (*q && isspace((unsigned char)*q)) ++q;
    if (*q != '[') return 0;   /* tools not an array */

    ca_tel_tool_definition_t *defs = NULL;
    size_t dcount = 0, dcap = 0;

    /* iterate array elements: each is an object {...} */
    const char *p = q + 1;
    bool ok = true;
    while (*p && ok) {
        while (*p && (isspace((unsigned char)*p) || *p == ',')) ++p;
        if (*p == ']' || *p == '\0') break;
        if (*p != '{') { ++p; continue; }
        /* find matching close brace with string awareness */
        const char *s = p;
        int depth = 0; bool in_str = false;
        for (; *p; ++p) {
            char c = *p;
            if (in_str) {
                if (c == '\\' && p[1]) { ++p; continue; }
                if (c == '"') in_str = false;
            } else {
                if (c == '"') in_str = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) { ++p; break; } }
            }
        }
        size_t objlen = (size_t)(p - s);
        char *obj = (char *)malloc(objlen + 1);
        if (!obj) { ok = false; break; }
        memcpy(obj, s, objlen); obj[objlen] = '\0';

        /* name (required, non-blank), description, inputSchema */
        char *name = mcp_json_string_field(obj, 0, "name", NULL);
        if (ta_is_ws(name)) { free(name); free(obj); continue; }
        char *desc = mcp_json_string_field(obj, 0, "description", NULL);
        char *schema = mcp_json_raw_field(obj, 0, "inputSchema");
        free(obj);

        char *local_name;
        if (ta_is_ws(tool_name_prefix)) {
            local_name = ta_strdup(name);
        } else {
            size_t need = strlen(tool_name_prefix) + strlen(name) + 1;
            local_name = (char *)malloc(need);
            if (local_name) snprintf(local_name, need, "%s%s", tool_name_prefix, name);
        }

        ca_tel_tool_definition_t def;
        memset(&def, 0, sizeof(def));
        def.name = local_name;
        def.description = desc ? desc : ta_strdup("");
        def.arguments_json_schema = schema ? schema : ta_strdup("{}");

        char *invoke_url = mcp_append_query(server_endpoint, "remote_tool", name);
        free(name);

        if (!def.name || !def.description || !def.arguments_json_schema || !invoke_url) {
            ca_tel_tool_definition_free(&def);
            free(invoke_url);
            ok = false;
            break;
        }
        (void)ca_tel_tool_registry_register_webhook(registry, &def, invoke_url);
        free(invoke_url);

        /* keep a copy of the def to return */
        if (dcount == dcap) {
            size_t nc = dcap ? dcap * 2 : 4;
            void *ni = realloc(defs, nc * sizeof(*defs));
            if (!ni) { ca_tel_tool_definition_free(&def); ok = false; break; }
            defs = (ca_tel_tool_definition_t *)ni; dcap = nc;
        }
        defs[dcount++] = def;   /* transfer ownership of the copied strings */
    }

    if (!ok) {
        for (size_t i = 0; i < dcount; ++i) ca_tel_tool_definition_free(&defs[i]);
        free(defs);
        return SIZE_MAX;
    }
    if (dcount == 0) { free(defs); return 0; }
    if (out) *out = defs;
    else { for (size_t i = 0; i < dcount; ++i) ca_tel_tool_definition_free(&defs[i]); free(defs); }
    return dcount;
}
