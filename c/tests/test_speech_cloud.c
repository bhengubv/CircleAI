/*
 * test_speech_cloud.c — CircleAI.Speech.Cloud IVoiceIntentRouter (C11 port).
 *
 * Verifies KeywordVoiceIntentRouter + NullVoiceIntentRouter against
 * KeywordVoiceIntentRouter.cs: ordered first-hit-wins, named captures (trimmed,
 * non-empty), empty transcript -> fallback, no-match -> fallback with the
 * trimmed transcript, and the injected-matcher seam.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* A custom matcher exercising the injected seam: matches when the transcript
 * equals "ping" exactly, adding capture "cmd"->"ping". */
static bool ping_matcher(void *self, const char *t, ca_intent_captures_t *caps) {
    (void)self;
    if (strcmp(t, "ping") == 0) {
        ca_intent_captures_add(caps, "cmd", "ping");
        return true;
    }
    return false;
}

static void test_keyword_router(void) {
    ca_voice_intent_router_t *r = ca_keyword_voice_intent_router_create(NULL); /* "ask-ai" */
    assert(strcmp(ca_voice_intent_router_backend_id(r), "keyword") == 0);

    /* Ordered intents: "open <arg>" (substring + capture), "close" (substring),
     * and a custom exact "ping" matcher. Needle has no trailing space because the
     * router trims the transcript (so a trailing-space needle would never match a
     * bare keyword); the matcher trims the captured tail itself. */
    assert(ca_keyword_voice_intent_router_add_substring(r, "open-note", "open", "target") == 0);
    assert(ca_keyword_voice_intent_router_add_substring(r, "close-note", "close", NULL) == 0);
    ca_intent_matcher_t pm = { NULL, ping_matcher };
    assert(ca_keyword_voice_intent_router_add(r, "ping", pm) == 0);

    ca_voice_intent_match_t m;

    /* "open shopping list" -> open-note, capture target="shopping list" (trimmed). */
    assert(ca_voice_intent_router_route(r, "  open shopping list  ", &m) == 0);
    assert(strcmp(m.intent_name, "open-note") == 0);
    assert(strcmp(m.transcript, "open shopping list") == 0);   /* trimmed */
    const char *tgt = ca_voice_intent_match_capture(&m, "target");
    assert(tgt && strcmp(tgt, "shopping list") == 0);
    ca_voice_intent_match_free(&m);

    /* First-hit-wins: "open and close" hits open-note (earlier), not close-note. */
    assert(ca_voice_intent_router_route(r, "open and close", &m) == 0);
    assert(strcmp(m.intent_name, "open-note") == 0);
    ca_voice_intent_match_free(&m);

    /* "close" (case-insensitive) -> close-note, no captures. */
    assert(ca_voice_intent_router_route(r, "CLOSE it", &m) == 0);
    assert(strcmp(m.intent_name, "close-note") == 0);
    assert(m.capture_count == 0);
    ca_voice_intent_match_free(&m);

    /* custom matcher exact "ping" -> ping, capture cmd=ping. */
    assert(ca_voice_intent_router_route(r, "ping", &m) == 0);
    assert(strcmp(m.intent_name, "ping") == 0);
    const char *cmd = ca_voice_intent_match_capture(&m, "cmd");
    assert(cmd && strcmp(cmd, "ping") == 0);
    ca_voice_intent_match_free(&m);

    /* no match -> fallback with trimmed transcript. */
    assert(ca_voice_intent_router_route(r, "  do something else ", &m) == 0);
    assert(strcmp(m.intent_name, "ask-ai") == 0);
    assert(strcmp(m.transcript, "do something else") == 0);
    assert(m.capture_count == 0);
    ca_voice_intent_match_free(&m);

    /* empty / whitespace transcript -> fallback, empty transcript, empty captures. */
    assert(ca_voice_intent_router_route(r, "   ", &m) == 0);
    assert(strcmp(m.intent_name, "ask-ai") == 0);
    assert(strcmp(m.transcript, "") == 0);
    assert(m.capture_count == 0);
    ca_voice_intent_match_free(&m);

    assert(ca_voice_intent_router_route(r, NULL, &m) == 0);
    assert(strcmp(m.intent_name, "ask-ai") == 0 && strcmp(m.transcript, "") == 0);
    ca_voice_intent_match_free(&m);

    /* capture with empty tail is skipped (matches !IsNullOrEmpty). "open " alone
     * hits but produces no target capture. */
    assert(ca_voice_intent_router_route(r, "open ", &m) == 0);
    assert(strcmp(m.intent_name, "open-note") == 0);
    assert(ca_voice_intent_match_capture(&m, "target") == NULL);
    ca_voice_intent_match_free(&m);

    ca_voice_intent_router_destroy(r);
    printf("  keyword_router: ok\n");
}

static void test_custom_fallback(void) {
    ca_voice_intent_router_t *r = ca_keyword_voice_intent_router_create("dictate");
    ca_voice_intent_match_t m;
    assert(ca_voice_intent_router_route(r, "random", &m) == 0);
    assert(strcmp(m.intent_name, "dictate") == 0);
    ca_voice_intent_match_free(&m);
    ca_voice_intent_router_destroy(r);
    printf("  custom_fallback: ok\n");
}

static void test_null_router(void) {
    ca_null_voice_intent_router_t *r = ca_null_voice_intent_router_create();
    assert(strcmp(ca_null_voice_intent_router_backend_id(r), "null") == 0);
    ca_voice_intent_match_t m;
    assert(ca_null_voice_intent_router_route(r, "whatever you say", &m) == 0);
    assert(strcmp(m.intent_name, "ask-ai") == 0);
    assert(strcmp(m.transcript, "whatever you say") == 0);   /* NOT trimmed by null */
    assert(m.capture_count == 0);
    ca_voice_intent_match_free(&m);

    assert(ca_null_voice_intent_router_route(r, NULL, &m) == 0);
    assert(strcmp(m.transcript, "") == 0);
    ca_voice_intent_match_free(&m);

    ca_null_voice_intent_router_destroy(r);
    printf("  null_router: ok\n");
}

int main(void) {
    test_keyword_router();
    test_custom_fallback();
    test_null_router();
    printf("test_speech_cloud: all assertions passed\n");
    return 0;
}
