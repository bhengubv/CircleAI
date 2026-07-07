/*
 * test_belief_integrity.c — attribution discipline (self/other/world) and
 * SelfBeliefStore filtering, revision (supersede), correction (retract),
 * provenance. Headline: "my mother is diabetic" never becomes a user fact.
 * Mirrors the Rust suite belief_integrity_test.rs (and TS/Go) 1:1.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* Extract exactly one belief; the caller frees it. */
static ca_personal_belief_t one_belief(const char *text) {
    size_t n = 0;
    ca_personal_belief_t *bs = ca_belief_extract(text, "turn", &n);
    assert(n == 1);
    ca_personal_belief_t b = bs[0];
    /* Shallow-move ownership out; free the array shell only. */
    memset(&bs[0], 0, sizeof(bs[0]));
    free(bs);
    return b;
}

static void record_all(ca_self_belief_store_t *store, const char *text, const char *src) {
    size_t n = 0;
    ca_personal_belief_t *bs = ca_belief_extract(text, src, &n);
    for (size_t i = 0; i < n; ++i) ca_self_belief_store_record(store, &bs[i]);
    ca_personal_belief_free_array(bs, n);
}

static bool non_self_has(ca_self_belief_store_t *store, const char *obj) {
    size_t n = 0;
    ca_personal_belief_t *ns = ca_self_belief_store_non_self(store, &n);
    bool found = false;
    for (size_t i = 0; i < n; ++i) if (strcmp(ns[i].object, obj) == 0) { found = true; break; }
    ca_personal_belief_free_array(ns, n);
    return found;
}

static ca_personal_belief_t mk_self(const char *obj, const char *predicate, const char *source) {
    ca_personal_belief_t b;
    memset(&b, 0, sizeof(b));
    b.attribution = CA_ATTRIBUTION_SELF;
    b.subject = (char *)"user";
    b.predicate = (char *)predicate;
    b.object = (char *)obj;
    b.confidence = 0.6;
    b.source = (char *)source;
    return b;
}

int main(void) {
    /* ── "my mother is diabetic" → other, about the mother ── */
    {
        ca_personal_belief_t b = one_belief("my mother is diabetic");
        assert(b.attribution == CA_ATTRIBUTION_OTHER);
        assert(strcmp(b.subject, "mother") == 0);
        assert(strcmp(b.object, "diabetic") == 0);
        ca_personal_belief_free(&b);
    }

    /* ── "i am vegetarian" → self, about the user ── */
    {
        ca_personal_belief_t b = one_belief("i am vegetarian");
        assert(b.attribution == CA_ATTRIBUTION_SELF);
        assert(strcmp(b.subject, "user") == 0);
        assert(strcmp(b.object, "vegetarian") == 0);
        ca_personal_belief_free(&b);
    }

    /* ── "my car is fast" → self (my + non-relation) ── */
    {
        ca_personal_belief_t b = one_belief("my car is fast");
        assert(b.attribution == CA_ATTRIBUTION_SELF);
        assert(strcmp(b.subject, "user") == 0);
        ca_personal_belief_free(&b);
    }

    /* ── a bare relation as subject → other ── */
    {
        ca_personal_belief_t b = one_belief("brother lives in Cape Town");
        assert(b.attribution == CA_ATTRIBUTION_OTHER);
        assert(strcmp(b.subject, "brother") == 0);
        ca_personal_belief_free(&b);
    }

    /* ── a general statement → world ── */
    {
        ca_personal_belief_t b = one_belief("paris is beautiful");
        assert(b.attribution == CA_ATTRIBUTION_WORLD);
        assert(strcmp(b.subject, "paris") == 0);
        ca_personal_belief_free(&b);
    }

    /* ── only self beliefs become user facts; other/world are audited ── */
    {
        ca_self_belief_store_t *store = ca_self_belief_store_create();
        record_all(store, "my mother is diabetic", "t1");
        record_all(store, "i am vegetarian", "t2");

        size_t fc = 0;
        ca_personal_belief_t *facts = ca_self_belief_store_self_facts(store, &fc);
        assert(fc == 1);
        assert(strcmp(facts[0].object, "vegetarian") == 0);
        for (size_t i = 0; i < fc; ++i) assert(strstr(facts[i].object, "diabetic") == NULL);
        ca_personal_belief_free_array(facts, fc);

        assert(non_self_has(store, "diabetic"));
        ca_self_belief_store_destroy(store);
    }

    /* ── a newer self belief supersedes the older one on the same predicate ── */
    {
        ca_self_belief_store_t *store = ca_self_belief_store_create();
        ca_personal_belief_t b1 = mk_self("vegetarian", "isAbout", "t");
        ca_personal_belief_t b2 = mk_self("vegan", "isAbout", "t");
        ca_self_belief_store_record(store, &b1);
        ca_self_belief_store_record(store, &b2);

        size_t fc = 0;
        ca_personal_belief_t *facts = ca_self_belief_store_self_facts(store, &fc);
        assert(fc == 1);
        assert(strcmp(facts[0].object, "vegan") == 0);
        ca_personal_belief_free_array(facts, fc);
        ca_self_belief_store_destroy(store);
    }

    /* ── retract removes user facts mentioning the text ── */
    {
        ca_self_belief_store_t *store = ca_self_belief_store_create();
        record_all(store, "i am vegetarian", "t1");
        size_t removed = ca_self_belief_store_retract(store, "vegetarian");
        assert(removed == 1);
        size_t fc = 0;
        ca_personal_belief_t *facts = ca_self_belief_store_self_facts(store, &fc);
        assert(fc == 0);
        ca_personal_belief_free_array(facts, fc);
        ca_self_belief_store_destroy(store);
    }

    /* ── provenance returns the distinct source turns behind user facts ── */
    {
        ca_self_belief_store_t *store = ca_self_belief_store_create();
        ca_personal_belief_t b1 = mk_self("vegetarian", "diet", "t1");
        ca_personal_belief_t b2 = mk_self("hiking", "hobby", "t2");
        ca_self_belief_store_record(store, &b1);
        ca_self_belief_store_record(store, &b2);

        size_t pc = 0;
        char **prov = ca_self_belief_store_provenance(store, &pc);
        assert(pc == 2);
        /* sort for a stable comparison */
        if (strcmp(prov[0], prov[1]) > 0) { char *t = prov[0]; prov[0] = prov[1]; prov[1] = t; }
        assert(strcmp(prov[0], "t1") == 0);
        assert(strcmp(prov[1], "t2") == 0);
        ca_string_array_free(prov, pc);
        ca_self_belief_store_destroy(store);
    }

    printf("All belief integrity tests passed.\n");
    return 0;
}
