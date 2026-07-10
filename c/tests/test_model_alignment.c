/*
 * test_model_alignment.c — CircleAI.ModelAlignment (C11 port).
 *
 * Verifies InMemoryAlignmentToolkit (Apply reversible-only / Revert / ListApplied),
 * RefuseAlignedPublishAuditor, and the Null* implementations against
 * InMemoryModelAlignment.cs + NullImplementations.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_alignment_profile_t mk_profile(const char *id, bool reversible,
                                         const char *const *cats, size_t n_cats) {
    ca_alignment_profile_t p; memset(&p, 0, sizeof(p));
    p.profile_id = strdup(id);
    p.description = strdup("desc");
    p.created_at_utc_ms = 12345;
    p.is_reversible = reversible;
    if (n_cats) {
        p.refusal_categories_removed = (char **)calloc(n_cats, sizeof(char *));
        for (size_t i = 0; i < n_cats; ++i) p.refusal_categories_removed[i] = strdup(cats[i]);
        p.refusal_categories_count = n_cats;
    }
    return p;
}

static void test_toolkit_apply_revert(void) {
    ca_alignment_toolkit_t *t = ca_in_memory_alignment_toolkit_create();
    assert(strcmp(ca_alignment_toolkit_backend_id(t), "in-memory") == 0);

    const char *cats[] = { "violence", "self-harm" };
    ca_alignment_profile_t p = mk_profile("p1", true, cats, 2);
    ca_alignment_result_t r; memset(&r, 0, sizeof(r));

    /* apply reversible → success */
    assert(ca_alignment_toolkit_apply(t, "model-a", &p, &r));
    assert(r.success && strcmp(r.profile_id, "p1") == 0 && r.failure_reason == NULL);
    ca_alignment_result_free(&r);

    /* list applied → 1, deep copy carries the categories */
    size_t n = 0;
    ca_alignment_profile_t *applied = ca_alignment_toolkit_list_applied(t, "model-a", &n);
    assert(n == 1);
    assert(strcmp(applied[0].profile_id, "p1") == 0);
    assert(applied[0].refusal_categories_count == 2);
    assert(strcmp(applied[0].refusal_categories_removed[0], "violence") == 0);
    assert(applied[0].is_reversible == true);
    ca_alignment_profile_free_array(applied, n);

    /* apply non-reversible → failure result, not stored */
    ca_alignment_profile_t np = mk_profile("p2", false, NULL, 0);
    assert(ca_alignment_toolkit_apply(t, "model-a", &np, &r));
    assert(r.success == false);
    assert(strcmp(r.failure_reason, "Non-reversible alignment refused by InMemoryAlignmentToolkit") == 0);
    ca_alignment_result_free(&r);
    ca_alignment_profile_free(&np);
    /* still only 1 applied */
    applied = ca_alignment_toolkit_list_applied(t, "model-a", &n);
    assert(n == 1);
    ca_alignment_profile_free_array(applied, n);

    /* revert unknown model */
    assert(ca_alignment_toolkit_revert(t, "ghost", "p1", &r));
    assert(r.success == false && strcmp(r.failure_reason, "Unknown model") == 0);
    ca_alignment_result_free(&r);

    /* revert wrong profile on known model */
    assert(ca_alignment_toolkit_revert(t, "model-a", "pX", &r));
    assert(r.success == false && strcmp(r.failure_reason, "Profile not applied to this model") == 0);
    ca_alignment_result_free(&r);

    /* revert correct → success, list empties */
    assert(ca_alignment_toolkit_revert(t, "model-a", "p1", &r));
    assert(r.success == true && r.failure_reason == NULL);
    ca_alignment_result_free(&r);
    applied = ca_alignment_toolkit_list_applied(t, "model-a", &n);
    assert(n == 0 && applied == NULL);

    /* argument errors */
    assert(ca_alignment_toolkit_apply(t, "  ", &p, &r) == false);   /* blank modelId */
    assert(ca_alignment_toolkit_apply(t, "m", NULL, &r) == false);  /* null profile */
    assert(ca_alignment_toolkit_revert(t, "  ", "p", &r) == false); /* blank modelId */
    assert(ca_alignment_toolkit_revert(t, "m", "  ", &r) == false); /* blank profileId */
    assert(ca_alignment_toolkit_list_applied(t, "  ", &n) == NULL && n == SIZE_MAX);

    ca_alignment_profile_free(&p);
    ca_alignment_toolkit_destroy(t);
    printf("  toolkit_apply_revert: ok\n");
}

static void test_multiple_profiles_and_models(void) {
    ca_alignment_toolkit_t *t = ca_in_memory_alignment_toolkit_create();
    ca_alignment_result_t r; memset(&r, 0, sizeof(r));

    ca_alignment_profile_t a = mk_profile("a", true, NULL, 0);
    ca_alignment_profile_t b = mk_profile("b", true, NULL, 0);
    assert(ca_alignment_toolkit_apply(t, "m1", &a, &r)); ca_alignment_result_free(&r);
    assert(ca_alignment_toolkit_apply(t, "m1", &b, &r)); ca_alignment_result_free(&r);
    assert(ca_alignment_toolkit_apply(t, "m2", &a, &r)); ca_alignment_result_free(&r);

    size_t n = 0;
    ca_alignment_profile_t *m1 = ca_alignment_toolkit_list_applied(t, "m1", &n);
    assert(n == 2 && strcmp(m1[0].profile_id, "a") == 0 && strcmp(m1[1].profile_id, "b") == 0);
    ca_alignment_profile_free_array(m1, n);
    ca_alignment_profile_t *m2 = ca_alignment_toolkit_list_applied(t, "m2", &n);
    assert(n == 1 && strcmp(m2[0].profile_id, "a") == 0);
    ca_alignment_profile_free_array(m2, n);

    /* remove only "a" from m1, "b" remains */
    assert(ca_alignment_toolkit_revert(t, "m1", "a", &r) && r.success); ca_alignment_result_free(&r);
    m1 = ca_alignment_toolkit_list_applied(t, "m1", &n);
    assert(n == 1 && strcmp(m1[0].profile_id, "b") == 0);
    ca_alignment_profile_free_array(m1, n);

    ca_alignment_profile_free(&a);
    ca_alignment_profile_free(&b);
    ca_alignment_toolkit_destroy(t);
    printf("  multiple_profiles_and_models: ok\n");
}

static void test_null_toolkit(void) {
    ca_alignment_toolkit_t *t = ca_null_alignment_toolkit_create();
    assert(strcmp(ca_alignment_toolkit_backend_id(t), "null") == 0);
    ca_alignment_result_t r; memset(&r, 0, sizeof(r));
    ca_alignment_profile_t p = mk_profile("p1", true, NULL, 0);

    assert(ca_alignment_toolkit_apply(t, "m", &p, &r));
    assert(r.success == false && strcmp(r.failure_reason, "NullAlignmentToolkit: no real backend wired.") == 0);
    assert(strcmp(r.profile_id, "p1") == 0);
    ca_alignment_result_free(&r);

    assert(ca_alignment_toolkit_revert(t, "m", "p1", &r));
    assert(r.success == false && strcmp(r.failure_reason, "NullAlignmentToolkit: nothing to revert.") == 0);
    ca_alignment_result_free(&r);

    size_t n = 0;
    assert(ca_alignment_toolkit_list_applied(t, "m", &n) == NULL && n == 0);

    ca_alignment_profile_free(&p);
    ca_alignment_toolkit_destroy(t);
    printf("  null_toolkit: ok\n");
}

static void test_auditor(void) {
    ca_alignment_toolkit_t *t = ca_in_memory_alignment_toolkit_create();
    ca_alignment_auditor_t *a = ca_refuse_aligned_publish_auditor_create(t);
    assert(a);
    assert(strcmp(ca_alignment_auditor_backend_id(a), "refuse-aligned") == 0);

    char *reason = NULL;
    /* clean model → ok to publish */
    assert(ca_alignment_auditor_assert_ok_to_publish(a, "clean", &reason) == true);
    assert(reason == NULL);

    /* apply a profile then publish → refused with a descriptive reason */
    ca_alignment_result_t r; memset(&r, 0, sizeof(r));
    ca_alignment_profile_t p = mk_profile("p1", true, NULL, 0);
    assert(ca_alignment_toolkit_apply(t, "dirty", &p, &r)); ca_alignment_result_free(&r);
    assert(ca_alignment_auditor_assert_ok_to_publish(a, "dirty", &reason) == false);
    assert(reason && strstr(reason, "dirty") && strstr(reason, "1 alignment profile"));
    free(reason); reason = NULL;

    /* blank modelId → false + message */
    assert(ca_alignment_auditor_assert_ok_to_publish(a, "  ", &reason) == false);
    assert(reason && strcmp(reason, "modelId required") == 0);
    free(reason); reason = NULL;

    /* auditor with NULL toolkit is refused at construction */
    assert(ca_refuse_aligned_publish_auditor_create(NULL) == NULL);

    ca_alignment_profile_free(&p);
    ca_alignment_auditor_destroy(a);
    ca_alignment_toolkit_destroy(t);

    /* Null auditor: always ok */
    ca_alignment_auditor_t *na = ca_null_alignment_auditor_create();
    assert(strcmp(ca_alignment_auditor_backend_id(na), "null") == 0);
    assert(ca_alignment_auditor_assert_ok_to_publish(na, "anything", &reason) == true);
    assert(reason == NULL);
    ca_alignment_auditor_destroy(na);
    printf("  auditor: ok\n");
}

int main(void) {
    test_toolkit_apply_revert();
    test_multiple_profiles_and_models();
    test_null_toolkit();
    test_auditor();
    printf("test_model_alignment: all assertions passed\n");
    return 0;
}
