/*
 * test_llm_extractor.c — LlmKnowledgeGraphExtractor (C11 port).
 *
 * Mirrors the verified TypeScript suite tests/llm_extractor.test.ts 1:1: parses
 * a clean JSON array, tolerates prose/markdown-fence-wrapped JSON, defaults
 * confidence when "c" is missing/non-numeric, clamps out-of-range confidence,
 * skips blank-s/p/o and non-object entries, and returns empty on garbage / an
 * empty turn / a failing generator. Also checks the verbatim system prompt and
 * the USER/ASSISTANT-framed user message.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* ── strdup helper (generator must return a malloc'd string the extractor frees) ── */
static char *dupstr(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* ── Fake generator: returns a canned reply, records the last messages seen. ── */
static const char *g_reply = NULL;
static int g_call_count = 0;
static ca_role_t g_last_roles[8];
static char     *g_last_contents[8];
static size_t    g_last_n = 0;

static void clear_capture(void) {
    for (size_t i = 0; i < g_last_n; ++i) { free(g_last_contents[i]); g_last_contents[i] = NULL; }
    g_last_n = 0;
}

static char *fake_generator(void *user, const ca_chat_message_t *msgs, size_t n) {
    (void)user;
    g_call_count++;
    clear_capture();
    g_last_n = n < 8 ? n : 8;
    for (size_t i = 0; i < g_last_n; ++i) {
        g_last_roles[i] = msgs[i].role;
        g_last_contents[i] = dupstr(msgs[i].content);
    }
    return dupstr(g_reply);
}

/* A generator that always fails (returns NULL) — the graceful-degradation path. */
static char *throwing_generator(void *user, const ca_chat_message_t *msgs, size_t n) {
    (void)user; (void)msgs; (void)n;
    g_call_count++;
    return NULL;
}

static ca_knowledge_triple_t *extract(const char *reply, const char *u, const char *a,
                                      const char *ep, size_t *n) {
    g_reply = reply;
    return ca_llm_extract_from_turn(fake_generator, NULL, u, a, ep, n);
}

int main(void) {
    /* ── clean JSON: plain array of triples ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = extract(
            "[{\"s\":\"Tony\",\"p\":\"has_daughter\",\"o\":\"Alex\",\"c\":0.9},"
            "{\"s\":\"Alex\",\"p\":\"lives_in\",\"o\":\"Durban\",\"c\":0.5}]",
            "hi", "ok", "ep1", &n);
        assert(n == 2);
        assert(strcmp(t[0].subject, "Tony") == 0);
        assert(strcmp(t[0].predicate, "has_daughter") == 0);
        assert(strcmp(t[0].object, "Alex") == 0);
        assert(t[0].confidence == 0.9);
        assert(t[0].source && strcmp(t[0].source, "ep1") == 0);
        assert(t[0].recorded_at_ms != 0);
        assert(strcmp(t[1].object, "Durban") == 0);
        assert(t[1].confidence == 0.5);
        ca_knowledge_triple_free_array(t, n);
    }

    /* ── verbatim system prompt + USER/ASSISTANT-framed user message ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = extract("[]", "the weather", "is sunny", "ep1", &n);
        assert(n == 0 && t == NULL);
        assert(g_last_n == 2);
        assert(g_last_roles[0] == CA_ROLE_SYSTEM);
        assert(strncmp(g_last_contents[0], "You are a knowledge-graph extractor.",
                       strlen("You are a knowledge-graph extractor.")) == 0);
        /* exact system prompt matches the accessor */
        assert(strcmp(g_last_contents[0], ca_llm_extractor_system_prompt()) == 0);
        assert(g_last_roles[1] == CA_ROLE_USER);
        assert(strcmp(g_last_contents[1], "USER:\nthe weather\nASSISTANT:\nis sunny\n") == 0);
    }

    /* ── defensive parsing: JSON embedded in prose / markdown fences ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = extract(
            "Sure! Here are the triples:\n```json\n"
            "[{\"s\":\"Paris\",\"p\":\"capital_of\",\"o\":\"France\",\"c\":0.95}]\n"
            "```\nHope that helps.",
            "u", "a", "ep2", &n);
        assert(n == 1);
        assert(strcmp(t[0].subject, "Paris") == 0);
        assert(strcmp(t[0].predicate, "capital_of") == 0);
        assert(strcmp(t[0].object, "France") == 0);
        assert(t[0].confidence == 0.95);
        ca_knowledge_triple_free_array(t, n);
    }

    /* ── defaults confidence to 0.75 when "c" is missing ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = extract("[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}]", "u", "a", "ep3", &n);
        assert(n == 1);
        assert(t[0].confidence == 0.75);
        ca_knowledge_triple_free_array(t, n);
    }

    /* ── defaults confidence to 0.75 when "c" is non-numeric ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = extract("[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\",\"c\":\"high\"}]",
                                           "u", "a", "ep3", &n);
        assert(n == 1);
        assert(t[0].confidence == 0.75);
        ca_knowledge_triple_free_array(t, n);
    }

    /* ── clamps confidence into [0,1] ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = extract(
            "[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\",\"c\":5},{\"s\":\"d\",\"p\":\"e\",\"o\":\"f\",\"c\":-2}]",
            "u", "a", "ep3", &n);
        assert(n == 2);
        assert(t[0].confidence == 1);
        assert(t[1].confidence == 0);
        ca_knowledge_triple_free_array(t, n);
    }

    /* ── skips objects whose s/p/o are blank or missing ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = extract(
            "[{\"s\":\"\",\"p\":\"b\",\"o\":\"c\"},{\"s\":\"a\",\"p\":\"  \",\"o\":\"c\"},"
            "{\"s\":\"a\",\"p\":\"b\"},{\"s\":\"keep\",\"p\":\"p\",\"o\":\"o\"}]",
            "u", "a", "ep3", &n);
        assert(n == 1);
        assert(strcmp(t[0].subject, "keep") == 0);
        ca_knowledge_triple_free_array(t, n);
    }

    /* ── skips non-object array entries ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = extract("[1, \"two\", null, {\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}]",
                                           "u", "a", "ep3", &n);
        assert(n == 1);
        assert(strcmp(t[0].subject, "a") == 0);
        ca_knowledge_triple_free_array(t, n);
    }

    /* ── empty on pure garbage (no brackets) ── */
    {
        size_t n = 999;
        ca_knowledge_triple_t *t = extract("I could not find any facts, sorry.", "u", "a", "ep4", &n);
        assert(n == 0 && t == NULL);
    }

    /* ── empty on malformed JSON inside brackets ── */
    {
        size_t n = 999;
        ca_knowledge_triple_t *t = extract("[{\"s\":\"a\", \"p\": }]", "u", "a", "ep4", &n);
        assert(n == 0 && t == NULL);
    }

    /* ── empty when the JSON is an object, not an array (no '[' before ']') ── */
    {
        size_t n = 999;
        ca_knowledge_triple_t *t = extract("{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}", "u", "a", "ep4", &n);
        assert(n == 0 && t == NULL);
    }

    /* ── empty when both user and assistant text are blank → NO generator call ── */
    {
        g_call_count = 0;
        size_t n = 999;
        ca_knowledge_triple_t *t = ca_llm_extract_from_turn(
            fake_generator, NULL, "   ", "", NULL, &n);
        assert(n == 0 && t == NULL);
        assert(g_call_count == 0); /* generator never invoked */
    }

    /* ── empty when the generator fails (returns NULL) ── */
    {
        size_t n = 999;
        ca_knowledge_triple_t *t = ca_llm_extract_from_turn(
            throwing_generator, NULL, "u", "a", "ep5", &n);
        assert(n == 0 && t == NULL);
    }

    /* ── direct parser: exercises ca_llm_extractor_parse_triples with NULL source ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = ca_llm_extractor_parse_triples(
            "[{\"s\":\"x\",\"p\":\"y\",\"o\":\"z\"}]", NULL, &n);
        assert(n == 1);
        assert(t[0].source == NULL);
        assert(t[0].confidence == 0.75);
        ca_knowledge_triple_free_array(t, n);
    }

    clear_capture();
    printf("All llm extractor tests passed.\n");
    return 0;
}
