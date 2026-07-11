/*
 * test_social.c — CircleAI.Social (C11 port) verification against
 * SocialPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_social_post_t mk_post(const char *id, const char *author, int64_t at) {
    ca_social_post_t p; memset(&p, 0, sizeof(p));
    p.post_id = (char *)id; p.author_id = (char *)author; p.body = (char *)"hi";
    p.at_utc_ms = at;
    return p;
}

static void test_posts_reactions(void) {
    ca_social_board_t *b = ca_social_board_create();
    assert(b);
    assert(ca_social_board_post(b, NULL) == -1);

    ca_social_post_t p1 = mk_post("p1", "alice", 100);
    assert(ca_social_board_post(b, &p1) == 0);
    ca_social_post_t got;
    assert(ca_social_board_get_post(b, "p1", &got) && strcmp(got.author_id, "alice") == 0);
    ca_social_post_free(&got);

    ca_social_reaction_t r1; memset(&r1, 0, sizeof(r1));
    r1.post_id = (char *)"p1"; r1.user_id = (char *)"u1"; r1.kind = (char *)"like"; r1.at_utc_ms = 1;
    ca_social_reaction_t r2; memset(&r2, 0, sizeof(r2));
    r2.post_id = (char *)"p1"; r2.user_id = (char *)"u2"; r2.kind = (char *)"LIKE"; r2.at_utc_ms = 2;
    ca_social_reaction_t r3; memset(&r3, 0, sizeof(r3));
    r3.post_id = (char *)"p1"; r3.user_id = (char *)"u3"; r3.kind = (char *)"love"; r3.at_utc_ms = 3;
    assert(ca_social_board_react(b, &r1) == 0);
    assert(ca_social_board_react(b, &r2) == 0);
    assert(ca_social_board_react(b, &r3) == 0);

    /* ReactionCount like (CI): r1 + r2 = 2. */
    assert(ca_social_board_reaction_count(b, "p1", "like") == 2);
    assert(ca_social_board_reaction_count(b, "p1", "love") == 1);

    ca_social_board_destroy(b);
    printf("  posts_reactions: ok\n");
}

static void test_follow_feed(void) {
    ca_social_board_t *b = ca_social_board_create();

    /* self-follow rejected. */
    ca_social_follow_t self; memset(&self, 0, sizeof(self));
    self.follower_id = (char *)"u1"; self.followee_id = (char *)"u1";
    assert(ca_social_board_follow(b, &self) == -2);

    /* u1 follows alice + bob. */
    ca_social_follow_t f1; memset(&f1, 0, sizeof(f1));
    f1.follower_id = (char *)"u1"; f1.followee_id = (char *)"alice";
    ca_social_follow_t f2; memset(&f2, 0, sizeof(f2));
    f2.follower_id = (char *)"u1"; f2.followee_id = (char *)"bob";
    ca_social_follow_t f3; memset(&f3, 0, sizeof(f3));
    f3.follower_id = (char *)"u2"; f3.followee_id = (char *)"alice";
    assert(ca_social_board_follow(b, &f1) == 0);
    assert(ca_social_board_follow(b, &f2) == 0);
    assert(ca_social_board_follow(b, &f3) == 0);

    /* Posts by alice(100), bob(300), carol(200 not followed). */
    ca_social_post_t pa = mk_post("pa", "alice", 100);
    ca_social_post_t pb = mk_post("pb", "bob", 300);
    ca_social_post_t pc = mk_post("pc", "carol", 200);
    assert(ca_social_board_post(b, &pa) == 0);
    assert(ca_social_board_post(b, &pb) == 0);
    assert(ca_social_board_post(b, &pc) == 0);

    /* Feed for u1 newest-first: pb(300), pa(100); pc excluded. */
    size_t n = 0;
    ca_social_post_t *feed = ca_social_board_feed_for(b, "u1", 20, &n);
    assert(n == 2 && strcmp(feed[0].post_id, "pb") == 0 && strcmp(feed[1].post_id, "pa") == 0);
    ca_social_post_free_array(feed, n);

    /* Followers of alice: u1, u2 (append order). */
    char **fol = ca_social_board_followers(b, "alice", &n);
    assert(n == 2 && strcmp(fol[0], "u1") == 0 && strcmp(fol[1], "u2") == 0);
    ca_social_followers_free(fol, n);

    /* Unfollow u1->alice; feed now pb only. */
    assert(ca_social_board_unfollow(b, "u1", "alice") == 1);
    feed = ca_social_board_feed_for(b, "u1", 20, &n);
    assert(n == 1 && strcmp(feed[0].post_id, "pb") == 0);
    ca_social_post_free_array(feed, n);

    ca_social_board_destroy(b);
    printf("  follow_feed: ok\n");
}

int main(void) {
    test_posts_reactions();
    test_follow_feed();
    printf("test_social: all assertions passed\n");
    return 0;
}
