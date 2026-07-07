/*
 * test_knowledge_graph.c — KnowledgeGraph (triples + nodes) and HippoRagStore
 * (Personalised PageRank multi-hop recall), including the three precision
 * guarantees. Mirrors the Rust suite knowledge_graph_test.rs (and TS/Go) 1:1.
 */

#include <stdio.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static bool has_hit(const ca_memory_hit_t *hits, size_t n, const char *id) {
    for (size_t i = 0; i < n; ++i) if (strcmp(hits[i].item.id, id) == 0) return true;
    return false;
}

static const ca_memory_hit_t *find_hit(const ca_memory_hit_t *hits, size_t n, const char *id) {
    for (size_t i = 0; i < n; ++i) if (strcmp(hits[i].item.id, id) == 0) return &hits[i];
    return NULL;
}

int main(void) {
    /* ── stores and returns triples ── */
    {
        ca_knowledge_graph_t *kg = ca_knowledge_graph_create();
        assert(ca_knowledge_graph_add_triple(kg, "a", "rel", "b", "ep1", 1.0));
        size_t n = 0;
        ca_knowledge_triple_t *all = ca_knowledge_graph_all_triples(kg, &n);
        assert(n == 1);
        assert(strcmp(all[0].subject, "a") == 0);
        assert(strcmp(all[0].object, "b") == 0);
        assert(all[0].confidence == 1.0);
        ca_knowledge_triple_free_array(all, n);
        ca_knowledge_graph_destroy(kg);
    }

    /* ── replaces a triple with the same (s,p,o) ── */
    {
        ca_knowledge_graph_t *kg = ca_knowledge_graph_create();
        ca_knowledge_graph_add_triple(kg, "a", "rel", "b", "ep1", 0.5);
        ca_knowledge_graph_add_triple(kg, "a", "rel", "b", "ep2", 0.9);
        size_t n = 0;
        ca_knowledge_triple_t *all = ca_knowledge_graph_all_triples(kg, &n);
        assert(n == 1);
        assert(all[0].confidence == 0.9);
        assert(strcmp(all[0].source, "ep2") == 0);
        ca_knowledge_triple_free_array(all, n);
        ca_knowledge_graph_destroy(kg);
    }

    /* ── upserts and fetches nodes ── */
    {
        ca_knowledge_graph_t *kg = ca_knowledge_graph_create();
        assert(ca_knowledge_graph_upsert_node(kg, "heart", "organ", "the heart", NULL, NULL, 0));
        ca_knowledge_node_t node;
        assert(ca_knowledge_graph_get_node(kg, "heart", &node));
        assert(strcmp(node.name, "the heart") == 0);
        ca_knowledge_node_free(&node);
        ca_knowledge_node_t missing;
        assert(!ca_knowledge_graph_get_node(kg, "missing", &missing));
        ca_knowledge_graph_destroy(kg);
    }

    /* ── rejects out-of-range confidence ── */
    {
        ca_knowledge_graph_t *kg = ca_knowledge_graph_create();
        assert(!ca_knowledge_graph_add_triple(kg, "a", "r", "b", NULL, 1.5));
        ca_knowledge_graph_destroy(kg);
    }

    /* ── reaches associated nodes across hops and excludes the seed ── */
    {
        ca_knowledge_graph_t *kg = ca_knowledge_graph_create();
        ca_knowledge_graph_add_triple(kg, "chest", "relates", "heart", "ep1", 1.0);
        ca_knowledge_graph_add_triple(kg, "heart", "relates", "father_cardiac_event", "ep2", 1.0);
        ca_hippo_store_t *hippo = ca_hippo_store_create(kg);

        size_t n = 0;
        ca_memory_hit_t *hits = ca_hippo_store_multi_hop_recall(hippo, "chest tightness", 5, &n);
        assert(!has_hit(hits, n, "chest"));
        assert(has_hit(hits, n, "heart"));
        assert(has_hit(hits, n, "father_cardiac_event"));
        const ca_memory_hit_t *heart = find_hit(hits, n, "heart");
        const ca_memory_hit_t *father = find_hit(hits, n, "father_cardiac_event");
        assert(heart && father);
        assert(heart->score >= father->score);
        ca_memory_hit_free_array(hits, n);
        ca_hippo_store_destroy(hippo);
        ca_knowledge_graph_destroy(kg);
    }

    /* ── returns empty when no query term touches the graph ── */
    {
        ca_knowledge_graph_t *kg = ca_knowledge_graph_create();
        ca_knowledge_graph_add_triple(kg, "chest", "relates", "heart", "ep1", 1.0);
        ca_hippo_store_t *hippo = ca_hippo_store_create(kg);
        size_t n = 0;
        ca_memory_hit_t *hits = ca_hippo_store_multi_hop_recall(hippo, "banana apple", 5, &n);
        assert(n == 0);
        ca_memory_hit_free_array(hits, n);
        ca_hippo_store_destroy(hippo);
        ca_knowledge_graph_destroy(kg);
    }

    /* ── returns empty on an empty graph ── */
    {
        ca_knowledge_graph_t *kg = ca_knowledge_graph_create();
        ca_hippo_store_t *hippo = ca_hippo_store_create(kg);
        size_t n = 0;
        ca_memory_hit_t *hits = ca_hippo_store_multi_hop_recall(hippo, "anything", 5, &n);
        assert(n == 0);
        ca_memory_hit_free_array(hits, n);
        ca_hippo_store_destroy(hippo);
        ca_knowledge_graph_destroy(kg);
    }

    /* ── confidence weights edge spread: stated fact outranks a guess ── */
    {
        ca_knowledge_graph_t *kg = ca_knowledge_graph_create();
        ca_knowledge_graph_add_triple(kg, "root", "r", "alpha", "ep1", 1.0);
        ca_knowledge_graph_add_triple(kg, "root", "r", "beta", "ep2", 0.1);
        ca_hippo_store_t *hippo = ca_hippo_store_create(kg);
        size_t n = 0;
        ca_memory_hit_t *hits = ca_hippo_store_multi_hop_recall(hippo, "root", 5, &n);
        assert(!has_hit(hits, n, "root"));
        assert(n >= 2);
        assert(strcmp(hits[0].item.id, "alpha") == 0);
        assert(strcmp(hits[1].item.id, "beta") == 0);
        assert(hits[0].score > hits[1].score);
        ca_memory_hit_free_array(hits, n);
        ca_hippo_store_destroy(hippo);
        ca_knowledge_graph_destroy(kg);
    }

    /* ── uses the node name as recall text when a node is present ── */
    {
        ca_knowledge_graph_t *kg = ca_knowledge_graph_create();
        ca_knowledge_graph_add_triple(kg, "chest", "relates", "heart", "ep1", 1.0);
        ca_knowledge_graph_upsert_node(kg, "heart", "organ", "the heart", NULL, NULL, 0);
        ca_hippo_store_t *hippo = ca_hippo_store_create(kg);
        size_t n = 0;
        ca_memory_hit_t *hits = ca_hippo_store_multi_hop_recall(hippo, "chest", 5, &n);
        const ca_memory_hit_t *heart = find_hit(hits, n, "heart");
        assert(heart);
        assert(strcmp(heart->item.text, "the heart") == 0);
        ca_memory_hit_free_array(hits, n);
        ca_hippo_store_destroy(hippo);
        ca_knowledge_graph_destroy(kg);
    }

    /* ── index registers the item and its metadata as graph triples ── */
    {
        ca_knowledge_graph_t *kg = ca_knowledge_graph_create();
        ca_hippo_store_t *hippo = ca_hippo_store_create(kg);
        ca_memory_item_t item;
        memset(&item, 0, sizeof(item));
        item.id = (char *)"note1";
        item.text = (char *)"durban weather";
        const char *mk[] = {"topic"}; const char *mv[] = {"durban"};
        item.meta_keys = (char **)mk; item.meta_values = (char **)mv; item.meta_count = 1;
        assert(ca_hippo_store_index(hippo, &item));

        size_t n = 0;
        ca_knowledge_triple_t *triples = ca_knowledge_graph_read_triples(kg, "note1", &n);
        assert(n == 2);
        bool has_text = false, has_topic = false;
        for (size_t i = 0; i < n; ++i) {
            if (strcmp(triples[i].predicate, "memory_text") == 0) has_text = true;
            if (strcmp(triples[i].predicate, "topic") == 0) has_topic = true;
        }
        assert(has_text && has_topic);
        ca_knowledge_triple_free_array(triples, n);
        ca_hippo_store_destroy(hippo);
        ca_knowledge_graph_destroy(kg);
    }

    /* ── recalls a memory node reached from a query-term seed (reverse edge) ── */
    {
        ca_knowledge_graph_t *kg = ca_knowledge_graph_create();
        ca_knowledge_graph_add_triple(kg, "durban", "seenin", "note1", "ep1", 1.0);
        ca_knowledge_graph_upsert_node(kg, "note1", "memory", "durban weather", NULL, NULL, 0);
        ca_hippo_store_t *hippo = ca_hippo_store_create(kg);
        size_t n = 0;
        ca_memory_hit_t *hits = ca_hippo_store_multi_hop_recall(hippo, "durban", 5, &n);
        assert(!has_hit(hits, n, "durban"));
        assert(has_hit(hits, n, "note1"));
        const ca_memory_hit_t *note = find_hit(hits, n, "note1");
        assert(note);
        assert(strcmp(note->item.text, "durban weather") == 0);
        ca_memory_hit_free_array(hits, n);
        ca_hippo_store_destroy(hippo);
        ca_knowledge_graph_destroy(kg);
    }

    printf("All knowledge graph tests passed.\n");
    return 0;
}
