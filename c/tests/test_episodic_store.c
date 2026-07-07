/*
 * test_episodic_store.c — InMemoryEpisodicStore: cosine search, recency fallback,
 * FIFO capacity eviction, prune, count. Mirrors the Rust suite
 * episodic_store_test.rs (and TS/Go) 1:1. Returns 0 on all-pass, assert() on fail.
 */

#include <stdio.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* Build an entry (embedding copied by the store). */
static ca_episodic_entry_t mk_entry(const char *id, const char *user_text,
                                    const float *emb, size_t emb_len, int64_t recorded) {
    ca_episodic_entry_t e;
    memset(&e, 0, sizeof(e));
    e.id = (char *)id;
    e.user_text = (char *)(user_text && user_text[0] ? user_text : "u");
    e.assistant_text = (char *)"a";
    e.app_context = NULL;
    e.embedding = (float *)emb;
    e.embedding_len = emb_len;
    e.recorded_at_ms = recorded;
    return e;
}

int main(void) {
    const int64_t T_JAN = 1735689600000LL; /* 2026-01-01 */
    const int64_t T_JUN = 1748736000000LL; /* 2026-06-01 */

    /* ── Cosine search ranks the nearest embedding first ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1000);
        float ex[] = {1.0f, 0.0f}, ey[] = {0.0f, 1.0f};
        ca_episodic_entry_t x = mk_entry("x", "x-axis", ex, 2, T_JAN);
        ca_episodic_entry_t y = mk_entry("y", "y-axis", ey, 2, T_JAN);
        ca_episodic_store_add(store, &x);
        ca_episodic_store_add(store, &y);

        float q[] = {1.0f, 0.0f};
        size_t n = 0;
        ca_episodic_entry_t *hits = ca_episodic_store_search(store, q, 2, 2, &n);
        assert(n == 2);
        assert(strcmp(hits[0].id, "x") == 0);
        assert(strcmp(hits[1].id, "y") == 0);
        ca_episodic_entry_free_array(hits, n);
        ca_episodic_store_destroy(store);
    }

    /* ── Cosine search respects top_k ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1000);
        float a[] = {1.0f, 0.0f}, b[] = {0.9f, 0.1f}, c[] = {0.0f, 1.0f};
        ca_episodic_entry_t ea = mk_entry("a", "a", a, 2, T_JAN);
        ca_episodic_entry_t eb = mk_entry("b", "b", b, 2, T_JAN);
        ca_episodic_entry_t ec = mk_entry("c", "c", c, 2, T_JAN);
        ca_episodic_store_add(store, &ea);
        ca_episodic_store_add(store, &eb);
        ca_episodic_store_add(store, &ec);

        float q[] = {1.0f, 0.0f};
        size_t n = 0;
        ca_episodic_entry_t *hits = ca_episodic_store_search(store, q, 2, 1, &n);
        assert(n == 1);
        assert(strcmp(hits[0].id, "a") == 0);
        ca_episodic_entry_free_array(hits, n);
        ca_episodic_store_destroy(store);
    }

    /* ── Cosine search ignores dimension mismatch ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1000);
        float ok[] = {1.0f, 0.0f}, wrong[] = {1.0f, 0.0f, 0.0f};
        ca_episodic_entry_t eok = mk_entry("ok", "ok", ok, 2, T_JAN);
        ca_episodic_entry_t ew = mk_entry("wrongdim", "wd", wrong, 3, T_JAN);
        ca_episodic_store_add(store, &eok);
        ca_episodic_store_add(store, &ew);

        float q[] = {1.0f, 0.0f};
        size_t n = 0;
        ca_episodic_entry_t *hits = ca_episodic_store_search(store, q, 2, 5, &n);
        assert(n == 1);
        assert(strcmp(hits[0].id, "ok") == 0);
        ca_episodic_entry_free_array(hits, n);
        ca_episodic_store_destroy(store);
    }

    /* ── Recency: newest-first when embedding is NULL ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1000);
        ca_episodic_entry_t eo = mk_entry("old", "o", NULL, 0, T_JAN);
        ca_episodic_entry_t en = mk_entry("new", "n", NULL, 0, T_JUN);
        ca_episodic_store_add(store, &eo);
        ca_episodic_store_add(store, &en);

        size_t n = 0;
        ca_episodic_entry_t *hits = ca_episodic_store_search(store, NULL, 0, 5, &n);
        assert(n == 2);
        assert(strcmp(hits[0].id, "new") == 0);
        assert(strcmp(hits[1].id, "old") == 0);
        ca_episodic_entry_free_array(hits, n);
        ca_episodic_store_destroy(store);
    }

    /* ── Recency: empty embedding treated as NULL ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1000);
        ca_episodic_entry_t eo = mk_entry("old", "o", NULL, 0, T_JAN);
        ca_episodic_entry_t en = mk_entry("new", "n", NULL, 0, T_JUN);
        ca_episodic_store_add(store, &eo);
        ca_episodic_store_add(store, &en);

        float empty[1] = {0};
        size_t n = 0;
        ca_episodic_entry_t *hits = ca_episodic_store_search(store, empty, 0, 1, &n);
        assert(n == 1);
        assert(strcmp(hits[0].id, "new") == 0);
        ca_episodic_entry_free_array(hits, n);
        ca_episodic_store_destroy(store);
    }

    /* ── Capacity evicts oldest beyond max_entries (FIFO) ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(2);
        ca_episodic_entry_t ea = mk_entry("a", "a", NULL, 0, T_JAN);
        ca_episodic_entry_t eb = mk_entry("b", "b", NULL, 0, T_JAN);
        ca_episodic_entry_t ec = mk_entry("c", "c", NULL, 0, T_JAN);
        ca_episodic_store_add(store, &ea);
        ca_episodic_store_add(store, &eb);
        ca_episodic_store_add(store, &ec);

        assert(ca_episodic_store_count(store) == 2);
        size_t n = 0;
        ca_episodic_entry_t *recent = ca_episodic_store_get_recent(store, 10, &n);
        assert(n == 2);
        /* a should be evicted; b and c remain. */
        bool has_a = false, has_b = false, has_c = false;
        for (size_t i = 0; i < n; ++i) {
            if (strcmp(recent[i].id, "a") == 0) has_a = true;
            if (strcmp(recent[i].id, "b") == 0) has_b = true;
            if (strcmp(recent[i].id, "c") == 0) has_c = true;
        }
        assert(!has_a && has_b && has_c);
        ca_episodic_entry_free_array(recent, n);
        ca_episodic_store_destroy(store);
    }

    /* ── Prune removes entries older than cutoff and returns the count ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1000);
        ca_episodic_entry_t eo = mk_entry("old", "o", NULL, 0, T_JAN);
        ca_episodic_entry_t en = mk_entry("new", "n", NULL, 0, T_JUN);
        ca_episodic_store_add(store, &eo);
        ca_episodic_store_add(store, &en);

        const int64_t T_MAR = 1740787200000LL; /* 2026-03-01 */
        size_t removed = ca_episodic_store_prune_older_than(store, T_MAR);
        assert(removed == 1);
        assert(ca_episodic_store_count(store) == 1);
        size_t n = 0;
        ca_episodic_entry_t *rem = ca_episodic_store_get_recent(store, 10, &n);
        assert(n == 1);
        assert(strcmp(rem[0].id, "new") == 0);
        ca_episodic_entry_free_array(rem, n);
        ca_episodic_store_destroy(store);
    }

    /* ── Rejects non-positive max_entries ── */
    {
        ca_episodic_store_t *bad = ca_episodic_store_create(0);
        assert(bad == NULL);
    }

    printf("All episodic store tests passed.\n");
    return 0;
}
