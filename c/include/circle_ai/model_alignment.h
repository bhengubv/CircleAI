#ifndef CIRCLE_AI_MODEL_ALIGNMENT_H
#define CIRCLE_AI_MODEL_ALIGNMENT_H

/*
 * model_alignment.h — CircleAI.ModelAlignment (C11 port).
 *
 * Ports the CircleAI.ModelAlignment namespace:
 *   - AlignmentProfile record, AlignmentResult record
 *   - IAlignmentToolkit  (InMemory / Null)
 *   - IAlignmentAuditor  (RefuseAlignedPublish / Null)
 *
 * InMemoryAlignmentToolkit.ApplyAsync only accepts reversible profiles
 * ("no permanent abliteration"); RefuseAlignedPublishAuditor refuses to
 * publish any model that carries applied alignment profiles.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup'd owning fields
 * with matching *_free, deep-copy getters, arrays are fresh copies the caller
 * frees. Errors surface via NULL + count=SIZE_MAX.
 *
 * Pure C11 + libc.
 */

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── AlignmentProfile ───────────────────────────────────────────────────── */

typedef struct {
    char           *profile_id;                 /* owned */
    char           *description;                /* owned */
    char          **refusal_categories_removed; /* owned array of owned strings */
    size_t          refusal_categories_count;
    int64_t         created_at_utc_ms;          /* Unix ms UTC */
    bool            is_reversible;
} ca_alignment_profile_t;

void ca_alignment_profile_free(ca_alignment_profile_t *p);
void ca_alignment_profile_free_array(ca_alignment_profile_t *arr, size_t count);
ca_alignment_profile_t *ca_alignment_profile_copy(ca_alignment_profile_t *dst,
                                                  const ca_alignment_profile_t *src);

/* ── AlignmentResult ────────────────────────────────────────────────────── */

typedef struct {
    char *profile_id;       /* owned */
    bool  success;
    char *failure_reason;   /* owned, or NULL */
} ca_alignment_result_t;

void ca_alignment_result_free(ca_alignment_result_t *r);
ca_alignment_result_t *ca_alignment_result_copy(ca_alignment_result_t *dst,
                                                const ca_alignment_result_t *src);

/* ── IAlignmentToolkit (InMemory / Null) ────────────────────────────────── */

typedef struct ca_alignment_toolkit ca_alignment_toolkit_t;

ca_alignment_toolkit_t *ca_in_memory_alignment_toolkit_create(void);
ca_alignment_toolkit_t *ca_null_alignment_toolkit_create(void);

void        ca_alignment_toolkit_destroy(ca_alignment_toolkit_t *t);
const char *ca_alignment_toolkit_backend_id(const ca_alignment_toolkit_t *t);

/* ApplyAsync — writes result into *out (caller frees with
 * ca_alignment_result_free). Returns false only on NULL t / NULL out / NULL
 * profile / blank modelId (C# throws ArgumentException/ArgumentNullException in
 * those cases). A non-reversible profile is accepted by the call but yields a
 * failure result (Success=false), matching the C# in-memory toolkit. */
bool ca_alignment_toolkit_apply(ca_alignment_toolkit_t *t, const char *model_id,
                                const ca_alignment_profile_t *profile,
                                ca_alignment_result_t *out);

/* RevertAsync — writes result into *out. Returns false only on NULL t / NULL
 * out / blank modelId / blank profileId. */
bool ca_alignment_toolkit_revert(ca_alignment_toolkit_t *t, const char *model_id,
                                 const char *profile_id, ca_alignment_result_t *out);

/* ListAppliedAsync — fresh array of applied profiles for model_id (caller frees
 * with ca_alignment_profile_free_array). *out_count receives the count
 * (0 → NULL). Blank modelId or NULL t → *out_count SIZE_MAX + NULL. */
ca_alignment_profile_t *ca_alignment_toolkit_list_applied(ca_alignment_toolkit_t *t,
                                                          const char *model_id,
                                                          size_t *out_count);

/* ── IAlignmentAuditor (RefuseAlignedPublish / Null) ────────────────────── */

typedef struct ca_alignment_auditor ca_alignment_auditor_t;

/* RefuseAlignedPublishAuditor — wraps a toolkit (borrowed; must outlive the
 * auditor). Refuses to publish a model that has applied profiles. Returns NULL
 * if toolkit is NULL. */
ca_alignment_auditor_t *ca_refuse_aligned_publish_auditor_create(ca_alignment_toolkit_t *toolkit);
/* NullAlignmentAuditor — always ok to publish. */
ca_alignment_auditor_t *ca_null_alignment_auditor_create(void);

void        ca_alignment_auditor_destroy(ca_alignment_auditor_t *a);
const char *ca_alignment_auditor_backend_id(const ca_alignment_auditor_t *a);

/* AssertOkToPublishAsync — returns true when publishing is allowed. Returns
 * false when it is refused (C# throws InvalidOperationException); when
 * out_reason != NULL and the call refuses, *out_reason receives a freshly
 * allocated explanation the caller frees. On allow, *out_reason is set to NULL.
 * NULL a / blank modelId also returns false (C# ArgumentException) with a
 * message in *out_reason. */
bool ca_alignment_auditor_assert_ok_to_publish(ca_alignment_auditor_t *a,
                                               const char *model_id,
                                               char **out_reason);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_MODEL_ALIGNMENT_H */
