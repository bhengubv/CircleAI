/*
 * accessibility.c — CircleAI.Accessibility (C11 port of AccessibilityPrimitives.cs).
 *
 * InMemoryAccessibilityBoard: profiles (UserId keyed). HintsFor derives adaptation
 * hints in the C#'s fixed order. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/accessibility.h"
#include "board_common.h"
#include <stdio.h>

/* C# AccessibilityNeed.ToString() names (stable enum identifiers). */
static const char *need_name(ca_accessibility_need_t n) {
    switch (n) {
        case CA_ACCESSIBILITY_NEED_VISUAL:    return "Visual";
        case CA_ACCESSIBILITY_NEED_HEARING:   return "Hearing";
        case CA_ACCESSIBILITY_NEED_MOTOR:     return "Motor";
        case CA_ACCESSIBILITY_NEED_COGNITIVE: return "Cognitive";
        case CA_ACCESSIBILITY_NEED_SPEECH:    return "Speech";
        default:                              return "Visual";
    }
}

/* ── records ────────────────────────────────────────────────────────────── */

void ca_accessibility_profile_free(ca_accessibility_profile_t *p) {
    if (!p) return;
    free(p->user_id);
    free(p->needs);
    p->user_id = NULL;
    p->needs = NULL;
    p->need_count = 0;
}

static bool profile_copy(ca_accessibility_profile_t *dst,
                         const ca_accessibility_profile_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->user_id        = cab_strdup_empty(src->user_id);
    dst->text_scale     = src->text_scale;
    dst->high_contrast  = src->high_contrast;
    dst->reduced_motion = src->reduced_motion;
    dst->screen_reader  = src->screen_reader;
    if (!dst->user_id) return false;
    if (src->need_count > 0) {
        dst->needs = (ca_accessibility_need_t *)malloc(
            src->need_count * sizeof(*dst->needs));
        if (!dst->needs) { ca_accessibility_profile_free(dst); return false; }
        memcpy(dst->needs, src->needs,
               src->need_count * sizeof(*dst->needs));
        dst->need_count = src->need_count;
    }
    return true;
}

void ca_accessibility_hint_free(ca_accessibility_hint_t *h) {
    if (!h) return;
    free(h->kind);
    free(h->value);
    h->kind = h->value = NULL;
}
void ca_accessibility_hint_free_array(ca_accessibility_hint_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_accessibility_hint_free(&arr[i]);
    free(arr);
}

/* Set a hint slot (owning). Returns false on OOM. */
static bool hint_set(ca_accessibility_hint_t *h, const char *kind,
                     const char *value) {
    h->kind = cab_strdup_empty(kind);
    h->value = cab_strdup_empty(value);
    if (!h->kind || !h->value) { ca_accessibility_hint_free(h); return false; }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_accessibility_board {
    ca_accessibility_profile_t *profiles;
    size_t                      count, cap;
};

ca_accessibility_board_t *ca_accessibility_board_create(void) {
    return (ca_accessibility_board_t *)calloc(1, sizeof(ca_accessibility_board_t));
}
void ca_accessibility_board_destroy(ca_accessibility_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->count; ++i)
        ca_accessibility_profile_free(&b->profiles[i]);
    free(b->profiles);
    free(b);
}

int ca_accessibility_board_set_profile(ca_accessibility_board_t *b,
                                       const ca_accessibility_profile_t *p) {
    if (!b || !p) return -1;
    for (size_t i = 0; i < b->count; ++i) {
        if (cab_ord_eq(b->profiles[i].user_id, p->user_id)) {
            ca_accessibility_profile_t copy;
            if (!profile_copy(&copy, p)) return -1;
            ca_accessibility_profile_free(&b->profiles[i]);
            b->profiles[i] = copy;
            return 0;
        }
    }
    ca_accessibility_profile_t copy;
    if (!profile_copy(&copy, p)) return -1;
    if (b->count == b->cap) {
        size_t nc = b->cap ? b->cap * 2 : 4;
        void *n = realloc(b->profiles, nc * sizeof(*b->profiles));
        if (!n) { ca_accessibility_profile_free(&copy); return -1; }
        b->profiles = (ca_accessibility_profile_t *)n;
        b->cap = nc;
    }
    b->profiles[b->count++] = copy;
    return 0;
}

bool ca_accessibility_board_get_profile(const ca_accessibility_board_t *b,
                                        const char *user_id,
                                        ca_accessibility_profile_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !user_id || !out) return false;
    for (size_t i = 0; i < b->count; ++i)
        if (cab_ord_eq(b->profiles[i].user_id, user_id))
            return profile_copy(out, &b->profiles[i]);
    return false;
}

ca_accessibility_hint_t *ca_accessibility_board_hints_for(
    const ca_accessibility_board_t *b, const char *user_id, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !user_id) { *out_count = (size_t)-1; return NULL; }

    const ca_accessibility_profile_t *p = NULL;
    for (size_t i = 0; i < b->count; ++i)
        if (cab_ord_eq(b->profiles[i].user_id, user_id)) { p = &b->profiles[i]; break; }
    if (!p) { *out_count = 0; return NULL; } /* Array.Empty */

    /* Max hints: 3 flags + text-scale + one per need. */
    size_t cap = 4 + p->need_count;
    ca_accessibility_hint_t *out =
        (ca_accessibility_hint_t *)calloc(cap, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    bool ok = true;

    if (ok && p->high_contrast)  ok = hint_set(&out[n++], "contrast", "high");
    if (ok && p->reduced_motion) ok = hint_set(&out[n++], "motion", "reduced");
    if (ok && p->screen_reader)  ok = hint_set(&out[n++], "aria", "verbose");
    if (ok && p->text_scale > 1) {
        char buf[64];
        snprintf(buf, sizeof(buf), "%.2f", p->text_scale); /* C# ToString("F2") */
        ok = hint_set(&out[n++], "text-scale", buf);
    }
    for (size_t i = 0; ok && i < p->need_count; ++i)
        ok = hint_set(&out[n++], "need", need_name(p->needs[i]));

    if (!ok) {
        ca_accessibility_hint_free_array(out, n);
        *out_count = (size_t)-1;
        return NULL;
    }
    if (n == 0) { free(out); *out_count = 0; return NULL; }
    *out_count = n;
    return out;
}

size_t ca_accessibility_board_count(const ca_accessibility_board_t *b) {
    return b ? b->count : 0;
}

bool ca_accessibility_board_remove(ca_accessibility_board_t *b,
                                   const char *user_id) {
    /* _profiles.TryRemove(userId, out _). */
    if (!b || !user_id) return false;
    for (size_t i = 0; i < b->count; ++i) {
        if (cab_ord_eq(b->profiles[i].user_id, user_id)) {
            ca_accessibility_profile_free(&b->profiles[i]);
            for (size_t j = i; j + 1 < b->count; ++j)
                b->profiles[j] = b->profiles[j + 1];
            b->count--;
            return true;
        }
    }
    return false;
}

/* Does a profile list `need` among its Needs? */
static bool profile_has_need(const ca_accessibility_profile_t *p,
                             ca_accessibility_need_t need) {
    for (size_t i = 0; i < p->need_count; ++i)
        if (p->needs[i] == need) return true;
    return false;
}

/* Collect+order profiles matching a predicate, ordered by UserId
 * (OrdinalIgnoreCase, stable on ties). want_need gates the need filter;
 * want_screen_reader gates the ScreenReader filter (both false => all). */
static ca_accessibility_profile_t *collect_ordered_by_user(
    const ca_accessibility_board_t *b, bool want_need,
    ca_accessibility_need_t need, bool want_screen_reader, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->count; ++i) {
        if (want_need && !profile_has_need(&b->profiles[i], need)) continue;
        if (want_screen_reader && !b->profiles[i].screen_reader) continue;
        idx[n++] = i;
    }
    if (n == 0) { free(idx); *out_count = 0; return NULL; }

    /* OrderBy(UserId, OrdinalIgnoreCase), stable insertion sort. */
    for (size_t i = 1; i < n; ++i) {
        size_t cur = idx[i];
        const char *ku = b->profiles[cur].user_id;
        size_t j = i;
        while (j > 0 && cab_ci_cmp(b->profiles[idx[j - 1]].user_id, ku) > 0) {
            idx[j] = idx[j - 1]; --j;
        }
        idx[j] = cur;
    }

    ca_accessibility_profile_t *out =
        (ca_accessibility_profile_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!profile_copy(&out[i], &b->profiles[idx[i]])) {
            for (size_t j = 0; j < i; ++j)
                ca_accessibility_profile_free(&out[j]);
            free(out); free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

ca_accessibility_profile_t *ca_accessibility_board_with_need(
    const ca_accessibility_board_t *b, ca_accessibility_need_t need,
    size_t *out_count) {
    return collect_ordered_by_user(b, true, need, false, out_count);
}

ca_accessibility_profile_t *ca_accessibility_board_screen_reader_users(
    const ca_accessibility_board_t *b, size_t *out_count) {
    return collect_ordered_by_user(b, false, CA_ACCESSIBILITY_NEED_VISUAL, true,
                                   out_count);
}

double ca_accessibility_board_average_text_scale(
    const ca_accessibility_board_t *b) {
    /* .Select(TextScale).DefaultIfEmpty(1.0).Average() */
    if (!b || b->count == 0) return 1.0;
    double sum = 0.0;
    for (size_t i = 0; i < b->count; ++i) sum += b->profiles[i].text_scale;
    return sum / (double)b->count;
}

bool ca_accessibility_board_needs_large_text(const ca_accessibility_board_t *b,
                                             const char *user_id,
                                             double threshold) {
    if (!b || !user_id) return false;
    for (size_t i = 0; i < b->count; ++i)
        if (cab_ord_eq(b->profiles[i].user_id, user_id))
            return b->profiles[i].text_scale >= threshold;
    return false;
}
