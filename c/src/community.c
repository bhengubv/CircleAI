/*
 * community.c — CircleAI.Community (C11 port of CommunityPrimitives.cs).
 *
 * InMemoryCommunityBoard: groups (GroupId keyed), announcements (append list),
 * opportunities (OppId keyed). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/community.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_community_group_free(ca_community_group_t *g) {
    if (!g) return;
    free(g->group_id);
    free(g->name);
    free(g->purpose);
    cab_strv_free(g->member_ids, g->member_count);
    g->group_id = g->name = g->purpose = NULL;
    g->member_ids = NULL;
    g->member_count = 0;
}
void ca_community_group_free_array(ca_community_group_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_community_group_free(&arr[i]);
    free(arr);
}

static bool group_copy(ca_community_group_t *dst,
                       const ca_community_group_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->group_id = cab_strdup_empty(src->group_id);
    dst->name     = cab_strdup_empty(src->name);
    dst->purpose  = cab_strdup_empty(src->purpose);
    bool ok = dst->group_id && dst->name && dst->purpose;
    if (ok) ok = cab_strv_copy(&dst->member_ids, src->member_ids,
                               src->member_count);
    if (ok) dst->member_count = src->member_count;
    if (!ok) { ca_community_group_free(dst); return false; }
    return true;
}

void ca_community_announcement_free(ca_community_announcement_t *a) {
    if (!a) return;
    free(a->announcement_id);
    free(a->group_id);
    free(a->title);
    free(a->body);
    a->announcement_id = a->group_id = a->title = a->body = NULL;
}
void ca_community_announcement_free_array(ca_community_announcement_t *arr,
                                          size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_community_announcement_free(&arr[i]);
    free(arr);
}

static bool announcement_copy(ca_community_announcement_t *dst,
                              const ca_community_announcement_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->announcement_id = cab_strdup_empty(src->announcement_id);
    dst->group_id        = cab_strdup_empty(src->group_id);
    dst->title           = cab_strdup_empty(src->title);
    dst->body            = cab_strdup_empty(src->body);
    dst->at_utc_ms       = src->at_utc_ms;
    if (!dst->announcement_id || !dst->group_id || !dst->title || !dst->body) {
        ca_community_announcement_free(dst);
        return false;
    }
    return true;
}

void ca_community_opportunity_free(ca_community_opportunity_t *o) {
    if (!o) return;
    free(o->opp_id);
    free(o->group_id);
    free(o->description);
    o->opp_id = o->group_id = o->description = NULL;
}
void ca_community_opportunity_free_array(ca_community_opportunity_t *arr,
                                         size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_community_opportunity_free(&arr[i]);
    free(arr);
}

static bool opportunity_copy(ca_community_opportunity_t *dst,
                             const ca_community_opportunity_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->opp_id            = cab_strdup_empty(src->opp_id);
    dst->group_id          = cab_strdup_empty(src->group_id);
    dst->description       = cab_strdup_empty(src->description);
    dst->volunteers_needed = src->volunteers_needed;
    dst->when_utc_ms       = src->when_utc_ms;
    if (!dst->opp_id || !dst->group_id || !dst->description) {
        ca_community_opportunity_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_community_board {
    ca_community_group_t       *groups;
    size_t                      g_count, g_cap;
    ca_community_announcement_t *annc;
    size_t                      a_count, a_cap;
    ca_community_opportunity_t *opps;
    size_t                      o_count, o_cap;
};

ca_community_board_t *ca_community_board_create(void) {
    return (ca_community_board_t *)calloc(1, sizeof(ca_community_board_t));
}
void ca_community_board_destroy(ca_community_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->g_count; ++i) ca_community_group_free(&b->groups[i]);
    for (size_t i = 0; i < b->a_count; ++i) ca_community_announcement_free(&b->annc[i]);
    for (size_t i = 0; i < b->o_count; ++i) ca_community_opportunity_free(&b->opps[i]);
    free(b->groups);
    free(b->annc);
    free(b->opps);
    free(b);
}

int ca_community_board_create_group(ca_community_board_t *b,
                                    const ca_community_group_t *g) {
    if (!b || !g) return -1;
    for (size_t i = 0; i < b->g_count; ++i) {
        if (cab_ord_eq(b->groups[i].group_id, g->group_id)) {
            ca_community_group_t copy;
            if (!group_copy(&copy, g)) return -1;
            ca_community_group_free(&b->groups[i]);
            b->groups[i] = copy;
            return 0;
        }
    }
    ca_community_group_t copy;
    if (!group_copy(&copy, g)) return -1;
    if (b->g_count == b->g_cap) {
        size_t nc = b->g_cap ? b->g_cap * 2 : 4;
        void *n = realloc(b->groups, nc * sizeof(*b->groups));
        if (!n) { ca_community_group_free(&copy); return -1; }
        b->groups = (ca_community_group_t *)n;
        b->g_cap = nc;
    }
    b->groups[b->g_count++] = copy;
    return 0;
}

bool ca_community_board_get_group(const ca_community_board_t *b, const char *id,
                                  ca_community_group_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->g_count; ++i)
        if (cab_ord_eq(b->groups[i].group_id, id))
            return group_copy(out, &b->groups[i]);
    return false;
}

/* Does group's MemberIds contain memberId (ordinal, C# List.Contains)? */
static bool group_has_member(const ca_community_group_t *g,
                             const char *member_id) {
    for (size_t i = 0; i < g->member_count; ++i)
        if (cab_ord_eq(g->member_ids[i], member_id)) return true;
    return false;
}

ca_community_group_t *ca_community_board_groups_for_member(
    const ca_community_board_t *b, const char *member_id, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !member_id) { *out_count = (size_t)-1; return NULL; }
    if (b->g_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->g_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->g_count; ++i)
        if (group_has_member(&b->groups[i], member_id)) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_community_group_t *out = (ca_community_group_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!group_copy(&out[i], &b->groups[idx[i]])) {
            ca_community_group_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_community_board_post(ca_community_board_t *b,
                            const ca_community_announcement_t *a) {
    if (!b || !a) return -1;
    ca_community_announcement_t copy;
    if (!announcement_copy(&copy, a)) return -1;
    if (b->a_count == b->a_cap) {
        size_t nc = b->a_cap ? b->a_cap * 2 : 4;
        void *n = realloc(b->annc, nc * sizeof(*b->annc));
        if (!n) { ca_community_announcement_free(&copy); return -1; }
        b->annc = (ca_community_announcement_t *)n;
        b->a_cap = nc;
    }
    b->annc[b->a_count++] = copy;
    return 0;
}

/* Stable descending sort of collected indices by AtUtc. */
static void annc_sort_desc(const ca_community_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->annc[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->annc[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_community_announcement_t *ca_community_board_announcements_for(
    const ca_community_board_t *b, const char *group_id, int limit,
    size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !group_id || limit <= 0) { *out_count = (size_t)-1; return NULL; }
    if (b->a_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->a_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->a_count; ++i)
        if (cab_ord_eq(b->annc[i].group_id, group_id)) idx[n++] = i;
    annc_sort_desc(b, idx, n);
    if ((size_t)limit < n) n = (size_t)limit;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_community_announcement_t *out =
        (ca_community_announcement_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!announcement_copy(&out[i], &b->annc[idx[i]])) {
            ca_community_announcement_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_community_board_list_opportunity(ca_community_board_t *b,
                                        const ca_community_opportunity_t *o) {
    if (!b || !o) return -1;
    for (size_t i = 0; i < b->o_count; ++i) {
        if (cab_ord_eq(b->opps[i].opp_id, o->opp_id)) {
            ca_community_opportunity_t copy;
            if (!opportunity_copy(&copy, o)) return -1;
            ca_community_opportunity_free(&b->opps[i]);
            b->opps[i] = copy;
            return 0;
        }
    }
    ca_community_opportunity_t copy;
    if (!opportunity_copy(&copy, o)) return -1;
    if (b->o_count == b->o_cap) {
        size_t nc = b->o_cap ? b->o_cap * 2 : 4;
        void *n = realloc(b->opps, nc * sizeof(*b->opps));
        if (!n) { ca_community_opportunity_free(&copy); return -1; }
        b->opps = (ca_community_opportunity_t *)n;
        b->o_cap = nc;
    }
    b->opps[b->o_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected indices by WhenUtc. */
static void opp_sort_asc(const ca_community_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->opps[key].when_utc_ms;
        size_t j = i;
        while (j > 0 && b->opps[idx[j - 1]].when_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_community_opportunity_t *ca_community_board_opportunities(
    const ca_community_board_t *b, int64_t now_ms, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->o_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->o_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->o_count; ++i)
        if (b->opps[i].when_utc_ms >= now_ms) idx[n++] = i;
    opp_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_community_opportunity_t *out =
        (ca_community_opportunity_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!opportunity_copy(&out[i], &b->opps[idx[i]])) {
            ca_community_opportunity_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
