/*
 * test_memory_encoder.c — CompanionMemoryEncoder end-to-end: a turn fills the
 * graph so associative recall can reach the episode; attributed beliefs formed
 * off the hot path (mother's fact never becomes the user's); overflow dropped;
 * close drains; extractor failure captured, not fatal. Mirrors the Rust suite
 * memory_encoder_test.rs (and TS/Go) 1:1.
 */

#include <stdio.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* Throwing extractor: signals failure via *out_count = SIZE_MAX. */
static ca_knowledge_triple_t *throwing_extractor(void *user, const char *u, const char *a,
                                                 const char *src, size_t *out_count) {
    (void)user; (void)u; (void)a; (void)src;
    *out_count = SIZE_MAX;
    return NULL;
}

static bool triples_have_object(ca_knowledge_graph_t *kg, const char *obj) {
    size_t n = 0;
    ca_knowledge_triple_t *all = ca_knowledge_graph_all_triples(kg, &n);
    bool found = false;
    for (size_t i = 0; i < n; ++i) if (strcmp(all[i].object, obj) == 0) { found = true; break; }
    ca_knowledge_triple_free_array(all, n);
    return found;
}

static bool has_node(ca_knowledge_graph_t *kg, const char *id) {
    ca_knowledge_node_t node;
    if (ca_knowledge_graph_get_node(kg, id, &node)) { ca_knowledge_node_free(&node); return true; }
    return false;
}

int main(void) {
    /* ── encodes a turn so associative recall can reach the episode ── */
    {
        ca_knowledge_graph_t *graph = ca_knowledge_graph_create();
        ca_memory_encoder_t *enc = ca_memory_encoder_create(
            ca_kg_extractor_heuristic_adapter, NULL, graph, NULL, NULL, NULL, 0);
        ca_memory_encoder_enqueue(enc, "I love hiking in Drakensberg", "Sounds wonderful", "ep-hike");
        ca_memory_encoder_close(enc);

        size_t n = 0;
        ca_knowledge_triple_t *all = ca_knowledge_graph_all_triples(graph, &n);
        assert(n > 0);
        ca_knowledge_triple_free_array(all, n);

        ca_hippo_store_t *hippo = ca_hippo_store_create(graph);
        ca_memory_hit_t *hits = ca_hippo_store_multi_hop_recall(hippo, "drakensberg", 5, &n);
        bool found = false;
        for (size_t i = 0; i < n; ++i) {
            if (strcmp(hits[i].item.id, "ep-hike") == 0) {
                found = true;
                assert(strcmp(hits[i].item.text, "I love hiking in Drakensberg") == 0);
            }
        }
        assert(found);
        ca_memory_hit_free_array(hits, n);
        ca_hippo_store_destroy(hippo);
        ca_memory_encoder_destroy(enc);
        ca_knowledge_graph_destroy(graph);
    }

    /* ── forms attributed beliefs off the hot path; mother's fact never the user's ── */
    {
        ca_knowledge_graph_t *graph = ca_knowledge_graph_create();
        ca_self_belief_store_t *beliefs = ca_self_belief_store_create();
        ca_memory_encoder_t *enc = ca_memory_encoder_create(
            ca_kg_extractor_heuristic_adapter, NULL, graph,
            ca_belief_extractor_heuristic_adapter, NULL, beliefs, 0);
        ca_memory_encoder_enqueue(enc, "my mother is diabetic", "Noted", "ep1");
        ca_memory_encoder_enqueue(enc, "i am vegetarian", "Got it", "ep2");
        ca_memory_encoder_close(enc);

        size_t fc = 0;
        ca_personal_belief_t *facts = ca_self_belief_store_self_facts(beliefs, &fc);
        bool has_veg = false;
        for (size_t i = 0; i < fc; ++i) {
            assert(strstr(facts[i].object, "diabetic") == NULL);
            if (strcmp(facts[i].object, "vegetarian") == 0) has_veg = true;
        }
        assert(has_veg);
        ca_personal_belief_free_array(facts, fc);

        size_t ac = 0;
        ca_personal_belief_t *audit = ca_self_belief_store_non_self(beliefs, &ac);
        bool has_diab = false;
        for (size_t i = 0; i < ac; ++i) if (strcmp(audit[i].object, "diabetic") == 0) has_diab = true;
        assert(has_diab);
        ca_personal_belief_free_array(audit, ac);

        ca_memory_encoder_destroy(enc);
        ca_self_belief_store_destroy(beliefs);
        ca_knowledge_graph_destroy(graph);
    }

    /* ── drops writes beyond capacity rather than blocking ── */
    {
        ca_knowledge_graph_t *graph = ca_knowledge_graph_create();
        ca_memory_encoder_t *enc = ca_memory_encoder_create(
            ca_kg_extractor_heuristic_adapter, NULL, graph, NULL, NULL, NULL, 2);
        ca_memory_encoder_enqueue(enc, "alpha", "", "e1");
        ca_memory_encoder_enqueue(enc, "bravo", "", "e2");
        ca_memory_encoder_enqueue(enc, "charlie", "", "e3"); /* overflow of capacity-2 */
        ca_memory_encoder_close(enc);

        assert(has_node(graph, "e1"));
        assert(has_node(graph, "e2"));
        assert(!has_node(graph, "e3"));
        ca_memory_encoder_destroy(enc);
        ca_knowledge_graph_destroy(graph);
    }

    /* ── ignores an enqueue with a blank episode id ── */
    {
        ca_knowledge_graph_t *graph = ca_knowledge_graph_create();
        ca_memory_encoder_t *enc = ca_memory_encoder_create(
            ca_kg_extractor_heuristic_adapter, NULL, graph, NULL, NULL, NULL, 0);
        ca_memory_encoder_enqueue(enc, "hello", "", "");
        ca_memory_encoder_enqueue(enc, "hello", "", "   ");
        ca_memory_encoder_close(enc);
        size_t n = 0;
        ca_knowledge_triple_t *all = ca_knowledge_graph_all_triples(graph, &n);
        assert(n == 0);
        ca_knowledge_triple_free_array(all, n);
        ca_memory_encoder_destroy(enc);
        ca_knowledge_graph_destroy(graph);
    }

    /* ── captures an extractor failure without crashing the drain ── */
    {
        ca_knowledge_graph_t *graph = ca_knowledge_graph_create();
        ca_memory_encoder_t *enc = ca_memory_encoder_create(
            throwing_extractor, NULL, graph, NULL, NULL, NULL, 0);
        ca_memory_encoder_enqueue(enc, "x", "", "e1");
        ca_memory_encoder_close(enc);

        const char *last = ca_memory_encoder_last_error(enc);
        assert(last != NULL);
        assert(strcmp(last, "boom") == 0);
        /* Node upserted before the extractor ran, so it survives. */
        assert(has_node(graph, "e1"));
        (void)triples_have_object; /* silence unused in this branch */
        ca_memory_encoder_destroy(enc);
        ca_knowledge_graph_destroy(graph);
    }

    printf("All memory encoder tests passed.\n");
    return 0;
}
