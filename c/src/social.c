/*
 * social.c — CircleAI.Social (C11 port of SocialPrimitives.cs).
 *
 * InMemorySocialBoard: posts (PostId keyed), reactions (append list), follows
 * (append list, duplicates allowed). Feed collects posts by the user's followees.
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/social.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_social_post_free(ca_social_post_t *p) {
    if (!p) return;
    free(p->post_id);
    free(p->author_id);
    free(p->body);
    cab_strv_free(p->tags, p->tag_count);
    p->post_id = p->author_id = p->body = NULL;
    p->tags = NULL;
    p->tag_count = 0;
}
void ca_social_post_free_array(ca_social_post_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_social_post_free(&arr[i]);
    free(arr);
}

static bool post_copy(ca_social_post_t *dst, const ca_social_post_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->post_id   = cab_strdup_empty(src->post_id);
    dst->author_id = cab_strdup_empty(src->author_id);
    dst->body      = cab_strdup_empty(src->body);
    dst->at_utc_ms = src->at_utc_ms;
    bool ok = dst->post_id && dst->author_id && dst->body;
    if (ok) ok = cab_strv_copy(&dst->tags, src->tags, src->tag_count);
    if (ok) dst->tag_count = src->tag_count;
    if (!ok) { ca_social_post_free(dst); return false; }
    return true;
}

static bool reaction_copy(ca_social_reaction_t *dst,
                          const ca_social_reaction_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->post_id   = cab_strdup_empty(src->post_id);
    dst->user_id   = cab_strdup_empty(src->user_id);
    dst->kind      = cab_strdup_empty(src->kind);
    dst->at_utc_ms = src->at_utc_ms;
    if (!dst->post_id || !dst->user_id || !dst->kind) {
        free(dst->post_id); free(dst->user_id); free(dst->kind);
        memset(dst, 0, sizeof(*dst));
        return false;
    }
    return true;
}
static void reaction_free(ca_social_reaction_t *r) {
    if (!r) return;
    free(r->post_id);
    free(r->user_id);
    free(r->kind);
    r->post_id = r->user_id = r->kind = NULL;
}

static bool follow_copy(ca_social_follow_t *dst, const ca_social_follow_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->follower_id = cab_strdup_empty(src->follower_id);
    dst->followee_id = cab_strdup_empty(src->followee_id);
    dst->at_utc_ms   = src->at_utc_ms;
    if (!dst->follower_id || !dst->followee_id) {
        free(dst->follower_id); free(dst->followee_id);
        memset(dst, 0, sizeof(*dst));
        return false;
    }
    return true;
}
static void follow_free(ca_social_follow_t *f) {
    if (!f) return;
    free(f->follower_id);
    free(f->followee_id);
    f->follower_id = f->followee_id = NULL;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_social_board {
    ca_social_post_t     *posts;
    size_t                p_count, p_cap;
    ca_social_reaction_t *reacts;
    size_t                r_count, r_cap;
    ca_social_follow_t   *follows;
    size_t                f_count, f_cap;
};

ca_social_board_t *ca_social_board_create(void) {
    return (ca_social_board_t *)calloc(1, sizeof(ca_social_board_t));
}
void ca_social_board_destroy(ca_social_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->p_count; ++i) ca_social_post_free(&b->posts[i]);
    for (size_t i = 0; i < b->r_count; ++i) reaction_free(&b->reacts[i]);
    for (size_t i = 0; i < b->f_count; ++i) follow_free(&b->follows[i]);
    free(b->posts);
    free(b->reacts);
    free(b->follows);
    free(b);
}

int ca_social_board_post(ca_social_board_t *b, const ca_social_post_t *p) {
    if (!b || !p) return -1;
    for (size_t i = 0; i < b->p_count; ++i) {
        if (cab_ord_eq(b->posts[i].post_id, p->post_id)) {
            ca_social_post_t copy;
            if (!post_copy(&copy, p)) return -1;
            ca_social_post_free(&b->posts[i]);
            b->posts[i] = copy;
            return 0;
        }
    }
    ca_social_post_t copy;
    if (!post_copy(&copy, p)) return -1;
    if (b->p_count == b->p_cap) {
        size_t nc = b->p_cap ? b->p_cap * 2 : 4;
        void *n = realloc(b->posts, nc * sizeof(*b->posts));
        if (!n) { ca_social_post_free(&copy); return -1; }
        b->posts = (ca_social_post_t *)n;
        b->p_cap = nc;
    }
    b->posts[b->p_count++] = copy;
    return 0;
}

bool ca_social_board_get_post(const ca_social_board_t *b, const char *id,
                              ca_social_post_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->p_count; ++i)
        if (cab_ord_eq(b->posts[i].post_id, id))
            return post_copy(out, &b->posts[i]);
    return false;
}

int ca_social_board_react(ca_social_board_t *b, const ca_social_reaction_t *r) {
    if (!b || !r) return -1;
    ca_social_reaction_t copy;
    if (!reaction_copy(&copy, r)) return -1;
    if (b->r_count == b->r_cap) {
        size_t nc = b->r_cap ? b->r_cap * 2 : 4;
        void *n = realloc(b->reacts, nc * sizeof(*b->reacts));
        if (!n) { reaction_free(&copy); return -1; }
        b->reacts = (ca_social_reaction_t *)n;
        b->r_cap = nc;
    }
    b->reacts[b->r_count++] = copy;
    return 0;
}

int ca_social_board_reaction_count(const ca_social_board_t *b,
                                   const char *post_id, const char *kind) {
    if (!b || !post_id || !kind) return 0;
    int count = 0;
    for (size_t i = 0; i < b->r_count; ++i) {
        const ca_social_reaction_t *r = &b->reacts[i];
        if (cab_ord_eq(r->post_id, post_id) && cab_ci_eq(r->kind, kind)) count++;
    }
    return count;
}

int ca_social_board_follow(ca_social_board_t *b, const ca_social_follow_t *f) {
    if (!b || !f) return -1;
    if (cab_ord_eq(f->follower_id, f->followee_id)) return -2; /* self-follow */
    ca_social_follow_t copy;
    if (!follow_copy(&copy, f)) return -1;
    if (b->f_count == b->f_cap) {
        size_t nc = b->f_cap ? b->f_cap * 2 : 4;
        void *n = realloc(b->follows, nc * sizeof(*b->follows));
        if (!n) { follow_free(&copy); return -1; }
        b->follows = (ca_social_follow_t *)n;
        b->f_cap = nc;
    }
    b->follows[b->f_count++] = copy;
    return 0;
}

int ca_social_board_unfollow(ca_social_board_t *b, const char *follower_id,
                             const char *followee_id) {
    if (!b || !follower_id || !followee_id) return -1;
    int removed = 0;
    size_t w = 0;
    for (size_t i = 0; i < b->f_count; ++i) {
        ca_social_follow_t *f = &b->follows[i];
        if (cab_ord_eq(f->follower_id, follower_id) &&
            cab_ord_eq(f->followee_id, followee_id)) {
            follow_free(f);
            removed++;
        } else {
            b->follows[w++] = *f;
        }
    }
    b->f_count = w;
    return removed;
}

/* Is authorId in the user's followee set? */
static bool user_follows(const ca_social_board_t *b, const char *user_id,
                         const char *author_id) {
    for (size_t i = 0; i < b->f_count; ++i)
        if (cab_ord_eq(b->follows[i].follower_id, user_id) &&
            cab_ord_eq(b->follows[i].followee_id, author_id))
            return true;
    return false;
}

/* Stable descending sort of collected post indices by AtUtc. */
static void post_sort_desc(const ca_social_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->posts[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->posts[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_social_post_t *ca_social_board_feed_for(const ca_social_board_t *b,
                                           const char *user_id, int limit,
                                           size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !user_id || limit <= 0) { *out_count = (size_t)-1; return NULL; }
    if (b->p_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->p_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->p_count; ++i)
        if (user_follows(b, user_id, b->posts[i].author_id)) idx[n++] = i;
    post_sort_desc(b, idx, n);
    if ((size_t)limit < n) n = (size_t)limit;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_social_post_t *out = (ca_social_post_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!post_copy(&out[i], &b->posts[idx[i]])) {
            ca_social_post_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

void ca_social_followers_free(char **v, size_t count) {
    cab_strv_free(v, count);
}

char **ca_social_board_followers(const ca_social_board_t *b, const char *user_id,
                                 size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !user_id) { *out_count = (size_t)-1; return NULL; }
    if (b->f_count == 0) { *out_count = 0; return NULL; }

    size_t n = 0;
    for (size_t i = 0; i < b->f_count; ++i)
        if (cab_ord_eq(b->follows[i].followee_id, user_id)) n++;
    if (n == 0) { *out_count = 0; return NULL; }

    char **out = (char **)calloc(n, sizeof(char *));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    size_t k = 0;
    for (size_t i = 0; i < b->f_count; ++i) {
        if (cab_ord_eq(b->follows[i].followee_id, user_id)) {
            out[k] = cab_strdup_empty(b->follows[i].follower_id);
            if (!out[k]) { cab_strv_free(out, k); *out_count = (size_t)-1; return NULL; }
            k++;
        }
    }
    *out_count = n;
    return out;
}
