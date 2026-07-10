#ifndef CIRCLE_AI_SPEECH_CLOUD_H
#define CIRCLE_AI_SPEECH_CLOUD_H

/*
 * speech_cloud.h — CircleAI.Speech.Cloud IVoiceIntentRouter (C11 port).
 *
 * Ports KeywordVoiceIntentRouter.cs 1:1 (the cloud STT/TTS HTTP recognizers +
 * synthesizers are external HttpClient dependencies, out of scope for the
 * hermetic C port):
 *
 *   Records   : VoiceIntent(Name, Pattern), VoiceIntentMatch(IntentName,
 *               Transcript, Captures).
 *   Router    : IVoiceIntentRouter — matches a transcript against an ordered
 *               intent list; first hit wins; falls through to a caller-defined
 *               fallback intent (default "ask-ai") with empty Captures.
 *               Ships KeywordVoiceIntentRouter + NullVoiceIntentRouter.
 *
 * The C# pattern is a compiled Regex. C has no std regex, so the pattern is a
 * host-supplied matcher (a vtable): given the trimmed transcript it reports a
 * hit + fills the named-capture dictionary. A built-in substring matcher (with
 * optional named-capture-of-the-tail) ships for deterministic hermetic use and
 * reproduces the C# semantics: trimmed transcript, ordered first-hit-wins,
 * captured-group values trimmed and non-empty, empty transcript -> fallback.
 *
 * Conventions: ca_ prefix, _t types, opaque handle, strdup-owning fields with
 * matching *_free, deep-copy getters, ordered linear list.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * VoiceIntentMatch(IntentName, Transcript, Captures)
 *
 * Captures is a dictionary erased to a parallel (key,value) array (both owned,
 * insertion order). Ordinal string comparer in C# -> we compare bytes.
 * =========================================================================== */

typedef struct {
    char *key;   /* owned */
    char *value; /* owned */
} ca_intent_capture_t;

typedef struct {
    char                *intent_name;   /* owned, non-null */
    char                *transcript;    /* owned, non-null */
    ca_intent_capture_t *captures;      /* owned array (may be NULL/empty) */
    size_t               capture_count;
} ca_voice_intent_match_t;

void ca_voice_intent_match_free(ca_voice_intent_match_t *m);
/* Lookup a capture by key (Ordinal). Returns borrowed value or NULL. */
const char *ca_voice_intent_match_capture(const ca_voice_intent_match_t *m,
                                          const char *key);

/* ===========================================================================
 * VoiceIntent matcher (the injected Regex seam)
 *
 * match(self, transcript, &out_match) is invoked with the ALREADY-TRIMMED,
 * non-empty transcript. It returns true on a hit. When it hits it may push
 * named captures via the provided sink; the router owns building the final
 * dictionary. To keep the ABI simple, the matcher instead fills a caller-owned
 * growable capture list through ca_intent_captures_add.
 * =========================================================================== */

/* Opaque capture accumulator handed to a matcher on a hit. */
typedef struct ca_intent_captures ca_intent_captures_t;
/* Add a named capture (key/value copied; value is NOT trimmed here — the
 * built-in matcher trims before calling, matching Regex group.Value.Trim()).
 * Empty/NULL value is skipped (matches the C# !IsNullOrEmpty guard). 0 / -1. */
int ca_intent_captures_add(ca_intent_captures_t *acc, const char *key,
                           const char *value);

/* Matcher vtable. Return true on match (and optionally add captures). */
typedef struct {
    void *self;
    bool (*match)(void *self, const char *trimmed_transcript,
                  ca_intent_captures_t *captures);
} ca_intent_matcher_t;

/* ===========================================================================
 * IVoiceIntentRouter
 * =========================================================================== */

typedef struct ca_voice_intent_router ca_voice_intent_router_t;

/* KeywordVoiceIntentRouter(fallback_intent_name). fallback must be non-empty
 * (default "ask-ai" when NULL/empty). NULL on OOM. */
ca_voice_intent_router_t *ca_keyword_voice_intent_router_create(
    const char *fallback_intent_name);
void ca_voice_intent_router_destroy(ca_voice_intent_router_t *r);

/* Append an intent (name + injected matcher). Order is match order. 0 / -1. */
int ca_keyword_voice_intent_router_add(ca_voice_intent_router_t *r,
                                       const char *name,
                                       ca_intent_matcher_t matcher);

/* Append an intent backed by the BUILT-IN case-insensitive substring matcher:
 * hits when `needle` occurs in the trimmed transcript. If capture_name is
 * non-NULL, the substring FOLLOWING the needle (trimmed) is captured under that
 * key when non-empty. Reproduces the common "prefix keyword + argument" regex.
 * 0 / -1. */
int ca_keyword_voice_intent_router_add_substring(ca_voice_intent_router_t *r,
                                                 const char *name,
                                                 const char *needle,
                                                 const char *capture_name);

/* BackendId — "keyword". */
const char *ca_voice_intent_router_backend_id(const ca_voice_intent_router_t *r);

/* RouteAsync — synchronous ValueTask completion. Fills *out (owned; caller frees
 * with ca_voice_intent_match_free). Empty/whitespace transcript -> fallback with
 * empty Captures and empty Transcript. 0 / -1. */
int ca_voice_intent_router_route(ca_voice_intent_router_t *r,
                                 const char *transcript,
                                 ca_voice_intent_match_t *out);

/* ===========================================================================
 * NullVoiceIntentRouter — BackendId "null"; always returns intent "ask-ai" with
 * Transcript = input (or "") and empty Captures.
 * =========================================================================== */

typedef struct ca_null_voice_intent_router ca_null_voice_intent_router_t;
ca_null_voice_intent_router_t *ca_null_voice_intent_router_create(void);
void ca_null_voice_intent_router_destroy(ca_null_voice_intent_router_t *r);
const char *ca_null_voice_intent_router_backend_id(const ca_null_voice_intent_router_t *r);
int ca_null_voice_intent_router_route(ca_null_voice_intent_router_t *r,
                                      const char *transcript,
                                      ca_voice_intent_match_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SPEECH_CLOUD_H */
