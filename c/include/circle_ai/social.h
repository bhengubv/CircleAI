#ifndef CIRCLE_AI_SOCIAL_H
#define CIRCLE_AI_SOCIAL_H

/*
 * social.h — CircleAI.Social (C11 port of SocialPrimitives.cs).
 *
 *   Records : SocialPost(PostId, AuthorId, Body, DateTimeOffset AtUtc,
 *                        IReadOnlyList<string> Tags);
 *             Reaction(PostId, UserId, Kind, DateTimeOffset AtUtc);
 *             Follow(FollowerId, FolloweeId, DateTimeOffset AtUtc).
 *   Board   : ISocialBoard -> InMemorySocialBoard
 *               Post (PostId keyed), GetPost(id), React (appends),
 *               ReactionCount(postId, kind) (Kind OrdinalIgnoreCase),
 *               Follow(f) — appends (self-follow throws), Unfollow(follower,
 *               followee) — removes all matching (ordinal), FeedFor(userId,
 *               limit=20) — posts by anyone the user follows, newest-first by
 *               AtUtc, top-limit, Followers(userId) — follower ids (append order).
 *
 * DateTimeOffset as Unix ms UTC. Follow appends duplicates (mirrors the C# list).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields, deep
 * copy getters, errors via NULL / count SIZE_MAX. Linear arrays, no pthreads.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* SocialPost(PostId, AuthorId, Body, DateTimeOffset AtUtc, Tags[]). */
typedef struct {
    char   *post_id;   /* owned, non-null */
    char   *author_id; /* owned, non-null */
    char   *body;      /* owned, non-null */
    int64_t at_utc_ms;
    char  **tags;      /* owned array of owned strings (may be NULL if 0) */
    size_t  tag_count;
} ca_social_post_t;

void ca_social_post_free(ca_social_post_t *p);
void ca_social_post_free_array(ca_social_post_t *arr, size_t count);

/* Reaction(PostId, UserId, Kind, DateTimeOffset AtUtc). */
typedef struct {
    char   *post_id;   /* owned, non-null */
    char   *user_id;   /* owned, non-null */
    char   *kind;      /* owned, non-null */
    int64_t at_utc_ms;
} ca_social_reaction_t;

/* Follow(FollowerId, FolloweeId, DateTimeOffset AtUtc). */
typedef struct {
    char   *follower_id; /* owned, non-null */
    char   *followee_id; /* owned, non-null */
    int64_t at_utc_ms;
} ca_social_follow_t;

typedef struct ca_social_board ca_social_board_t;

ca_social_board_t *ca_social_board_create(void); /* NULL on OOM */
void ca_social_board_destroy(ca_social_board_t *b);

/* Post(p) — PostId keyed set. 0 / -1. */
int ca_social_board_post(ca_social_board_t *b, const ca_social_post_t *p);

/* GetPost(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_social_board_get_post(const ca_social_board_t *b, const char *id,
                              ca_social_post_t *out);

/* React(r) — appends. 0 / -1. */
int ca_social_board_react(ca_social_board_t *b, const ca_social_reaction_t *r);

/* ReactionCount(postId, kind) — count of reactions on postId with Kind matching
 * (OrdinalIgnoreCase). 0 on bad args. */
int ca_social_board_reaction_count(const ca_social_board_t *b,
                                   const char *post_id, const char *kind);

/* Follow(f) — appends. 0 on success, -1 on bad args, -2 when FollowerId ==
 * FolloweeId (C# "Cannot follow yourself"). */
int ca_social_board_follow(ca_social_board_t *b, const ca_social_follow_t *f);

/* Unfollow(followerId, followeeId) — removes all matching follows (ordinal).
 * Returns the number removed, or -1 on bad args. */
int ca_social_board_unfollow(ca_social_board_t *b, const char *follower_id,
                             const char *followee_id);

/* FeedFor(userId, limit) -> fresh owned array of posts by anyone userId follows,
 * newest-first by AtUtc, first `limit`. limit must be > 0 (SIZE_MAX on limit<=0 /
 * bad args). Use 20 for the C# default. NULL + 0 empty. */
ca_social_post_t *ca_social_board_feed_for(const ca_social_board_t *b,
                                           const char *user_id, int limit,
                                           size_t *out_count);

/* Followers(userId) -> fresh owned array of follower ids (append order).
 * NULL + 0 empty; NULL + SIZE_MAX on error. Free with cab-style strv or the
 * dedicated free below. */
char **ca_social_board_followers(const ca_social_board_t *b, const char *user_id,
                                 size_t *out_count);
/* Free a follower-id array returned above. */
void ca_social_followers_free(char **v, size_t count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SOCIAL_H */
