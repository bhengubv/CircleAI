/*
 * model_alignment.c — CircleAI.ModelAlignment (C11 port).
 *
 * Ports Contracts.cs (AlignmentProfile / AlignmentResult), InMemoryModelAlignment.cs
 * (InMemoryAlignmentToolkit + RefuseAlignedPublishAuditor) and
 * NullImplementations.cs (NullAlignmentToolkit + NullAlignmentAuditor).
 *
 * InMemoryAlignmentToolkit keeps a per-model list of applied profiles; Apply
 * refuses non-reversible profiles (Success=false), Revert removes by profileId,
 * ListApplied snapshots the list. RefuseAlignedPublishAuditor asserts a model
 * has zero applied profiles before publish.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/model_alignment.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

static char *ma_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static bool ma_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p)
        if (*p != ' ' && *p != '\t' && *p != '\n' && *p != '\r' &&
            *p != '\f' && *p != '\v') return false;
    return true;
}

/* ── AlignmentProfile ───────────────────────────────────────────────────── */

void ca_alignment_profile_free(ca_alignment_profile_t *p) {
    if (!p) return;
    free(p->profile_id);
    free(p->description);
    if (p->refusal_categories_removed) {
        for (size_t i = 0; i < p->refusal_categories_count; ++i)
            free(p->refusal_categories_removed[i]);
        free(p->refusal_categories_removed);
    }
    p->profile_id = p->description = NULL;
    p->refusal_categories_removed = NULL;
    p->refusal_categories_count = 0;
}
void ca_alignment_profile_free_array(ca_alignment_profile_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_alignment_profile_free(&arr[i]);
    free(arr);
}
ca_alignment_profile_t *ca_alignment_profile_copy(ca_alignment_profile_t *dst,
                                                  const ca_alignment_profile_t *src) {
    if (!dst || !src) return dst;
    dst->profile_id        = ma_strdup(src->profile_id);
    dst->description       = ma_strdup(src->description);
    dst->created_at_utc_ms = src->created_at_utc_ms;
    dst->is_reversible     = src->is_reversible;
    dst->refusal_categories_count = src->refusal_categories_count;
    if (src->refusal_categories_count && src->refusal_categories_removed) {
        dst->refusal_categories_removed =
            (char **)calloc(src->refusal_categories_count, sizeof(char *));
        if (dst->refusal_categories_removed) {
            for (size_t i = 0; i < src->refusal_categories_count; ++i)
                dst->refusal_categories_removed[i] = ma_strdup(src->refusal_categories_removed[i]);
        } else {
            dst->refusal_categories_count = 0;
        }
    } else {
        dst->refusal_categories_removed = NULL;
        dst->refusal_categories_count = 0;
    }
    return dst;
}

/* ── AlignmentResult ────────────────────────────────────────────────────── */

void ca_alignment_result_free(ca_alignment_result_t *r) {
    if (!r) return;
    free(r->profile_id);
    free(r->failure_reason);
    r->profile_id = r->failure_reason = NULL;
}
ca_alignment_result_t *ca_alignment_result_copy(ca_alignment_result_t *dst,
                                                const ca_alignment_result_t *src) {
    if (!dst || !src) return dst;
    dst->profile_id     = ma_strdup(src->profile_id);
    dst->success        = src->success;
    dst->failure_reason = ma_strdup(src->failure_reason);
    return dst;
}

static void ma_set_result(ca_alignment_result_t *out, const char *profile_id,
                          bool success, const char *failure_reason) {
    out->profile_id     = ma_strdup(profile_id);
    out->success        = success;
    out->failure_reason = ma_strdup(failure_reason);
}

/* ── IAlignmentToolkit ──────────────────────────────────────────────────── */

typedef enum { MA_TK_IN_MEMORY, MA_TK_NULL } ma_tk_kind_t;

/* per-model applied-profile list */
typedef struct {
    char                   *model_id;   /* owned */
    ca_alignment_profile_t *profiles;   /* owned */
    size_t                  count, cap;
} ma_model_entry_t;

struct ca_alignment_toolkit {
    ma_tk_kind_t      kind;
    ma_model_entry_t *models;
    size_t            count, cap;
};

ca_alignment_toolkit_t *ca_in_memory_alignment_toolkit_create(void) {
    ca_alignment_toolkit_t *t = (ca_alignment_toolkit_t *)calloc(1, sizeof(*t));
    if (t) t->kind = MA_TK_IN_MEMORY;
    return t;
}
ca_alignment_toolkit_t *ca_null_alignment_toolkit_create(void) {
    ca_alignment_toolkit_t *t = (ca_alignment_toolkit_t *)calloc(1, sizeof(*t));
    if (t) t->kind = MA_TK_NULL;
    return t;
}
void ca_alignment_toolkit_destroy(ca_alignment_toolkit_t *t) {
    if (!t) return;
    for (size_t i = 0; i < t->count; ++i) {
        free(t->models[i].model_id);
        for (size_t j = 0; j < t->models[i].count; ++j)
            ca_alignment_profile_free(&t->models[i].profiles[j]);
        free(t->models[i].profiles);
    }
    free(t->models);
    free(t);
}
const char *ca_alignment_toolkit_backend_id(const ca_alignment_toolkit_t *t) {
    if (!t) return NULL;
    return t->kind == MA_TK_NULL ? "null" : "in-memory";
}

static ma_model_entry_t *ma_find_model(ca_alignment_toolkit_t *t, const char *model_id) {
    for (size_t i = 0; i < t->count; ++i)
        if (t->models[i].model_id && strcmp(t->models[i].model_id, model_id) == 0)
            return &t->models[i];
    return NULL;
}
static ma_model_entry_t *ma_get_or_add_model(ca_alignment_toolkit_t *t, const char *model_id) {
    ma_model_entry_t *e = ma_find_model(t, model_id);
    if (e) return e;
    if (t->count == t->cap) {
        size_t nc = t->cap ? t->cap * 2 : 4;
        void *n = realloc(t->models, nc * sizeof(*t->models));
        if (!n) return NULL;
        t->models = n; t->cap = nc;
    }
    e = &t->models[t->count];
    memset(e, 0, sizeof(*e));
    e->model_id = ma_strdup(model_id);
    t->count++;
    return e;
}

bool ca_alignment_toolkit_apply(ca_alignment_toolkit_t *t, const char *model_id,
                                const ca_alignment_profile_t *profile,
                                ca_alignment_result_t *out) {
    if (!t || !out || !profile) return false;      /* ArgumentNullException(profile) */
    if (ma_blank(model_id)) return false;          /* ArgumentException(modelId) */

    if (t->kind == MA_TK_NULL) {
        ma_set_result(out, profile->profile_id, false,
                      "NullAlignmentToolkit: no real backend wired.");
        return true;
    }

    if (!profile->is_reversible) {
        ma_set_result(out, profile->profile_id, false,
                      "Non-reversible alignment refused by InMemoryAlignmentToolkit");
        return true;
    }

    ma_model_entry_t *e = ma_get_or_add_model(t, model_id);
    if (!e) return false;
    if (e->count == e->cap) {
        size_t nc = e->cap ? e->cap * 2 : 4;
        void *n = realloc(e->profiles, nc * sizeof(*e->profiles));
        if (!n) return false;
        e->profiles = n; e->cap = nc;
    }
    ca_alignment_profile_copy(&e->profiles[e->count], profile);
    e->count++;
    ma_set_result(out, profile->profile_id, true, NULL);
    return true;
}

bool ca_alignment_toolkit_revert(ca_alignment_toolkit_t *t, const char *model_id,
                                 const char *profile_id, ca_alignment_result_t *out) {
    if (!t || !out) return false;
    if (ma_blank(model_id) || ma_blank(profile_id)) return false;  /* ArgumentException */

    if (t->kind == MA_TK_NULL) {
        ma_set_result(out, profile_id, false, "NullAlignmentToolkit: nothing to revert.");
        return true;
    }

    ma_model_entry_t *e = ma_find_model(t, model_id);
    if (!e) {
        ma_set_result(out, profile_id, false, "Unknown model");
        return true;
    }
    size_t removed = 0;
    size_t w = 0;
    for (size_t r = 0; r < e->count; ++r) {
        if (e->profiles[r].profile_id && strcmp(e->profiles[r].profile_id, profile_id) == 0) {
            ca_alignment_profile_free(&e->profiles[r]);
            removed++;
        } else {
            if (w != r) e->profiles[w] = e->profiles[r];
            w++;
        }
    }
    e->count = w;
    if (removed > 0) ma_set_result(out, profile_id, true, NULL);
    else             ma_set_result(out, profile_id, false, "Profile not applied to this model");
    return true;
}

ca_alignment_profile_t *ca_alignment_toolkit_list_applied(ca_alignment_toolkit_t *t,
                                                          const char *model_id,
                                                          size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!t || ma_blank(model_id)) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    if (t->kind == MA_TK_NULL) return NULL;   /* always empty */

    ma_model_entry_t *e = ma_find_model(t, model_id);
    if (!e || e->count == 0) return NULL;
    ca_alignment_profile_t *res = (ca_alignment_profile_t *)calloc(e->count, sizeof(*res));
    if (!res) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < e->count; ++i) ca_alignment_profile_copy(&res[i], &e->profiles[i]);
    if (out_count) *out_count = e->count;
    return res;
}

/* ── IAlignmentAuditor ──────────────────────────────────────────────────── */

typedef enum { MA_AUD_REFUSE_ALIGNED, MA_AUD_NULL } ma_aud_kind_t;

struct ca_alignment_auditor {
    ma_aud_kind_t           kind;
    ca_alignment_toolkit_t *toolkit;   /* borrowed (refuse-aligned only) */
};

ca_alignment_auditor_t *ca_refuse_aligned_publish_auditor_create(ca_alignment_toolkit_t *toolkit) {
    if (!toolkit) return NULL;   /* C# ctor throws ArgumentNullException */
    ca_alignment_auditor_t *a = (ca_alignment_auditor_t *)calloc(1, sizeof(*a));
    if (!a) return NULL;
    a->kind = MA_AUD_REFUSE_ALIGNED;
    a->toolkit = toolkit;
    return a;
}
ca_alignment_auditor_t *ca_null_alignment_auditor_create(void) {
    ca_alignment_auditor_t *a = (ca_alignment_auditor_t *)calloc(1, sizeof(*a));
    if (a) a->kind = MA_AUD_NULL;
    return a;
}
void ca_alignment_auditor_destroy(ca_alignment_auditor_t *a) { free(a); }
const char *ca_alignment_auditor_backend_id(const ca_alignment_auditor_t *a) {
    if (!a) return NULL;
    return a->kind == MA_AUD_NULL ? "null" : "refuse-aligned";
}

bool ca_alignment_auditor_assert_ok_to_publish(ca_alignment_auditor_t *a,
                                               const char *model_id,
                                               char **out_reason) {
    if (out_reason) *out_reason = NULL;
    if (!a) return false;

    if (a->kind == MA_AUD_NULL) return true;   /* always ok */

    /* refuse-aligned */
    if (ma_blank(model_id)) {
        if (out_reason) *out_reason = ma_strdup("modelId required");
        return false;
    }
    size_t n = 0;
    ca_alignment_profile_t *applied = ca_alignment_toolkit_list_applied(a->toolkit, model_id, &n);
    /* list_applied returns SIZE_MAX only on blank modelId (already excluded) or
     * NULL toolkit (excluded by ctor) — treat any non-zero as "has profiles". */
    if (n != 0 && n != SIZE_MAX) {
        if (out_reason) {
            char buf[256];
            snprintf(buf, sizeof(buf),
                     "Cannot publish '%s': %zu alignment profile(s) applied — "
                     "this would distribute weights with safety modifications.",
                     model_id, n);
            *out_reason = ma_strdup(buf);
        }
        ca_alignment_profile_free_array(applied, n);
        return false;
    }
    if (applied && n != SIZE_MAX) ca_alignment_profile_free_array(applied, n);
    return true;
}
