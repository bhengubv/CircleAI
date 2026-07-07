/*
 * test_companion_session.c — the concrete CompanionSession end-to-end: recalls
 * fused memory + user facts into the system prompt, calls the generator, persists
 * the exchange, hands it to the encoder, recalls a prior turn later, reflects the
 * recalled memories in the context. Mirrors the Rust suite
 * companion_session_test.rs (and TS/Go).
 *
 * The capturing generator copies the messages it was handed into a struct the
 * test inspects, and returns a freshly malloc'd canned reply (the session frees
 * it after persisting; the test also gets its own copy back from send()).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static char *tdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1; char *p = (char *)malloc(n); if (p) memcpy(p, s, n); return p;
}

/* Capturing generator state. */
typedef struct {
    const char *reply;
    /* captured on each call */
    char       *last_system;   /* content of msgs[0] */
    char       *last_user;     /* content of msgs[n-1] */
    ca_role_t   first_role;
    size_t      msg_count;
} capturing_gen_t;

static char *capturing_generate(void *user, const ca_chat_message_t *msgs, size_t n) {
    capturing_gen_t *g = (capturing_gen_t *)user;
    free(g->last_system);
    free(g->last_user);
    g->last_system = tdup(n > 0 ? msgs[0].content : "");
    g->last_user = tdup(n > 0 ? msgs[n - 1].content : "");
    g->first_role = n > 0 ? msgs[0].role : CA_ROLE_USER;
    g->msg_count = n;
    return tdup(g->reply); /* session takes ownership */
}

static void gen_reset(capturing_gen_t *g, const char *reply) {
    memset(g, 0, sizeof(*g));
    g->reply = reply;
}
static void gen_free(capturing_gen_t *g) { free(g->last_system); free(g->last_user); }

static ca_episodic_entry_t seed_entry(const char *user_text, const char *assistant_text) {
    ca_episodic_entry_t e;
    memset(&e, 0, sizeof(e));
    e.id = (char *)"seed";
    e.user_text = (char *)user_text;
    e.assistant_text = (char *)assistant_text;
    e.recorded_at_ms = 1735689600000LL;
    return e;
}

static void record_self_fact(ca_self_belief_store_t *beliefs, const char *text) {
    size_t n = 0;
    ca_personal_belief_t *bs = ca_belief_extract(text, "t0", &n);
    for (size_t i = 0; i < n; ++i) ca_self_belief_store_record(beliefs, &bs[i]);
    ca_personal_belief_free_array(bs, n);
}

int main(void) {
    ca_companion_session_options_t opts;
    memset(&opts, 0, sizeof(opts));
    opts.session_id = "s1";
    opts.identity_id = "u1";
    opts.interface_kind = "mobile";
    opts.recall_top_k = 5;

    /* ── injects recalled memories and user facts into the system prompt ── */
    {
        ca_episodic_store_t *ep = ca_episodic_store_create(1000);
        ca_episodic_entry_t e = seed_entry("I have a peanut allergy", "Noted");
        ca_episodic_store_add(ep, &e);
        ca_self_belief_store_t *beliefs = ca_self_belief_store_create();
        record_self_fact(beliefs, "i am vegetarian");

        ca_fused_recall_t *recall = ca_fused_recall_create(
            ca_episodic_store_search_adapter, ep, NULL, NULL, NULL);
        capturing_gen_t g; gen_reset(&g, "Here are some options");
        ca_companion_session_t *s = ca_companion_session_create(
            capturing_generate, &g, ep, recall, NULL, beliefs, &opts);

        char *reply = ca_companion_session_send(s, "what can I eat?");
        assert(reply && strcmp(reply, "Here are some options") == 0);
        free(reply);

        assert(g.first_role == CA_ROLE_SYSTEM);
        assert(strstr(g.last_system, "peanut allergy") != NULL);
        assert(strstr(g.last_system, "vegetarian") != NULL);
        assert(strcmp(g.last_user, "what can I eat?") == 0);

        gen_free(&g);
        ca_companion_session_destroy(s);
        ca_fused_recall_destroy(recall);
        ca_self_belief_store_destroy(beliefs);
        ca_episodic_store_destroy(ep);
    }

    /* ── persists the turn and grows the history ── */
    {
        ca_episodic_store_t *ep = ca_episodic_store_create(1000);
        ca_fused_recall_t *recall = ca_fused_recall_create(
            ca_episodic_store_search_adapter, ep, NULL, NULL, NULL);
        capturing_gen_t g; gen_reset(&g, "ok");
        ca_companion_session_t *s = ca_companion_session_create(
            capturing_generate, &g, ep, recall, NULL, NULL, &opts);

        char *reply = ca_companion_session_send(s, "hello");
        free(reply);
        assert(ca_episodic_store_count(ep) == 1);
        assert(ca_companion_session_history_count(s) == 2);
        assert(strcmp(ca_companion_session_history_role(s, 0), "user") == 0);
        assert(strcmp(ca_companion_session_history_role(s, 1), "assistant") == 0);

        gen_free(&g);
        ca_companion_session_destroy(s);
        ca_fused_recall_destroy(recall);
        ca_episodic_store_destroy(ep);
    }

    /* ── recalls a prior turn on a later turn ── */
    {
        ca_episodic_store_t *ep = ca_episodic_store_create(1000);
        ca_fused_recall_t *recall = ca_fused_recall_create(
            ca_episodic_store_search_adapter, ep, NULL, NULL, NULL);
        capturing_gen_t g; gen_reset(&g, "noted");
        ca_companion_session_t *s = ca_companion_session_create(
            capturing_generate, &g, ep, recall, NULL, NULL, &opts);

        char *r1 = ca_companion_session_send(s, "my favourite colour is blue");
        free(r1);
        char *r2 = ca_companion_session_send(s, "what's my favourite colour?");
        free(r2);

        assert(strstr(g.last_system, "favourite colour is blue") != NULL);

        gen_free(&g);
        ca_companion_session_destroy(s);
        ca_fused_recall_destroy(recall);
        ca_episodic_store_destroy(ep);
    }

    /* ── hands the turn to the background encoder, filling the graph ── */
    {
        ca_episodic_store_t *ep = ca_episodic_store_create(1000);
        ca_knowledge_graph_t *graph = ca_knowledge_graph_create();
        ca_memory_encoder_t *enc = ca_memory_encoder_create(
            ca_kg_extractor_heuristic_adapter, NULL, graph, NULL, NULL, NULL, 0);
        ca_fused_recall_t *recall = ca_fused_recall_create(
            ca_episodic_store_search_adapter, ep, NULL, NULL, NULL);
        capturing_gen_t g; gen_reset(&g, "ok");
        ca_companion_session_t *s = ca_companion_session_create(
            capturing_generate, &g, ep, recall, enc, NULL, &opts);

        char *reply = ca_companion_session_send(s, "remember my dentist appointment");
        free(reply);
        ca_memory_encoder_close(enc);

        size_t n = 0;
        ca_knowledge_triple_t *all = ca_knowledge_graph_all_triples(graph, &n);
        bool found = false;
        for (size_t i = 0; i < n; ++i) if (strcmp(all[i].object, "dentist") == 0) found = true;
        assert(found);
        ca_knowledge_triple_free_array(all, n);

        gen_free(&g);
        ca_companion_session_destroy(s);
        ca_fused_recall_destroy(recall);
        ca_memory_encoder_destroy(enc);
        ca_knowledge_graph_destroy(graph);
        ca_episodic_store_destroy(ep);
    }

    /* ── get_context reflects the memories recalled on the last turn ── */
    {
        ca_episodic_store_t *ep = ca_episodic_store_create(1000);
        ca_episodic_entry_t e = seed_entry("I live in Durban", "Nice");
        ca_episodic_store_add(ep, &e);
        ca_fused_recall_t *recall = ca_fused_recall_create(
            ca_episodic_store_search_adapter, ep, NULL, NULL, NULL);
        capturing_gen_t g; gen_reset(&g, "ok");
        ca_companion_session_t *s = ca_companion_session_create(
            capturing_generate, &g, ep, recall, NULL, NULL, &opts);

        char *reply = ca_companion_session_send(s, "where do I live?");
        free(reply);

        size_t sc = 0;
        const char *const *snips = ca_companion_session_context_snippets(s, &sc);
        bool found = false;
        for (size_t i = 0; i < sc; ++i) if (strcmp(snips[i], "I live in Durban") == 0) found = true;
        assert(found);

        gen_free(&g);
        ca_companion_session_destroy(s);
        ca_fused_recall_destroy(recall);
        ca_episodic_store_destroy(ep);
    }

    printf("All companion session tests passed.\n");
    return 0;
}
