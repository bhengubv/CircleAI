/*
 * test_kg_extractor.c — HeuristicKnowledgeGraphExtractor: bidirectional
 * mentions/seenin triples, stop/short-word filtering, dedup, memory-id fallback.
 * Mirrors the Rust suite kg_extractor_test.rs (and TS/Go) 1:1.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static bool has_triple(const ca_knowledge_triple_t *t, size_t n,
                       const char *s, const char *p, const char *o) {
    for (size_t i = 0; i < n; ++i) {
        if (strcmp(t[i].subject, s) == 0 && strcmp(t[i].predicate, p) == 0 &&
            strcmp(t[i].object, o) == 0) return true;
    }
    return false;
}

/* Collect the "mentions" objects, sorted ascending. */
static char **mentions_objects(const ca_knowledge_triple_t *t, size_t n, size_t *out_count) {
    char **objs = (char **)calloc(n ? n : 1, sizeof(char *));
    size_t c = 0;
    for (size_t i = 0; i < n; ++i) {
        if (strcmp(t[i].predicate, "mentions") == 0) {
            size_t l = strlen(t[i].object) + 1;
            objs[c] = (char *)malloc(l); memcpy(objs[c], t[i].object, l); c++;
        }
    }
    /* sort ascending */
    for (size_t a = 0; a + 1 < c; ++a)
        for (size_t b = a + 1; b < c; ++b)
            if (strcmp(objs[b], objs[a]) < 0) { char *tmp = objs[a]; objs[a] = objs[b]; objs[b] = tmp; }
    *out_count = c;
    return objs;
}
static void free_objs(char **o, size_t c) { for (size_t i = 0; i < c; ++i) free(o[i]); free(o); }

int main(void) {
    /* ── two-way link per content word, keyed by episode id ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = ca_kg_extract_from_turn("Durban weather is sunny", "", "ep1", &n);
        /* content words: durban, weather, sunny ("is" is a short stop word) */
        assert(n == 6);
        assert(has_triple(t, n, "ep1", "mentions", "durban"));
        assert(has_triple(t, n, "durban", "seenin", "ep1"));
        assert(has_triple(t, n, "ep1", "mentions", "weather"));
        assert(has_triple(t, n, "ep1", "mentions", "sunny"));
        ca_knowledge_triple_free_array(t, n);
    }

    /* ── drops stop words and words shorter than 3 chars ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = ca_kg_extract_from_turn("I am at the shop", "", "ep2", &n);
        size_t oc = 0;
        char **objs = mentions_objects(t, n, &oc);
        assert(oc == 1);
        assert(strcmp(objs[0], "shop") == 0);
        free_objs(objs, oc);
        ca_knowledge_triple_free_array(t, n);
    }

    /* ── dedupes a repeated word ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = ca_kg_extract_from_turn("test test test", "", "ep3", &n);
        assert(n == 2); /* one mentions + one seenin for "test" */
        ca_knowledge_triple_free_array(t, n);
    }

    /* ── includes assistant-side content words ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = ca_kg_extract_from_turn("tell me about", "Johannesburg traffic", "ep4", &n);
        size_t oc = 0;
        char **objs = mentions_objects(t, n, &oc);
        assert(oc == 3);
        assert(strcmp(objs[0], "johannesburg") == 0);
        assert(strcmp(objs[1], "tell") == 0);
        assert(strcmp(objs[2], "traffic") == 0);
        free_objs(objs, oc);
        ca_knowledge_triple_free_array(t, n);
    }

    /* ── falls back to user text as the memory id when no episode id ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = ca_kg_extract_from_turn("hello world", "", NULL, &n);
        assert(has_triple(t, n, "hello world", "mentions", "hello") ||
               has_triple(t, n, "hello world", "mentions", "world"));
        ca_knowledge_triple_free_array(t, n);
    }

    /* ── returns nothing for an empty turn ── */
    {
        size_t n = 999;
        ca_knowledge_triple_t *t = ca_kg_extract_from_turn("", "", NULL, &n);
        assert(n == 0);
        assert(t == NULL);
    }

    /* ── tags every triple with the source episode id and default confidence ── */
    {
        size_t n = 0;
        ca_knowledge_triple_t *t = ca_kg_extract_from_turn("coffee", "", "ep5", &n);
        assert(n > 0);
        for (size_t i = 0; i < n; ++i) {
            assert(t[i].source && strcmp(t[i].source, "ep5") == 0);
            assert(t[i].confidence == 0.6);
        }
        ca_knowledge_triple_free_array(t, n);
    }

    printf("All kg extractor tests passed.\n");
    return 0;
}
