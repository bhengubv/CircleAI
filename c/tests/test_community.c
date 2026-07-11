/*
 * test_community.c — CircleAI.Community (C11 port) verification against
 * CommunityPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_groups(void) {
    ca_community_board_t *b = ca_community_board_create();
    assert(b);
    assert(ca_community_board_create_group(b, NULL) == -1);

    char *m1[] = { (char *)"u1", (char *)"u2" };
    ca_community_group_t g1; memset(&g1, 0, sizeof(g1));
    g1.group_id = (char *)"g1"; g1.name = (char *)"Runners"; g1.purpose = (char *)"5k";
    g1.member_ids = m1; g1.member_count = 2;
    char *m2[] = { (char *)"u2", (char *)"u3" };
    ca_community_group_t g2; memset(&g2, 0, sizeof(g2));
    g2.group_id = (char *)"g2"; g2.name = (char *)"Readers"; g2.purpose = (char *)"books";
    g2.member_ids = m2; g2.member_count = 2;
    assert(ca_community_board_create_group(b, &g1) == 0);
    assert(ca_community_board_create_group(b, &g2) == 0);

    ca_community_group_t got;
    assert(ca_community_board_get_group(b, "g1", &got) && got.member_count == 2);
    ca_community_group_free(&got);

    /* GroupsForMember u2: both g1, g2. */
    size_t n = 0;
    ca_community_group_t *gs = ca_community_board_groups_for_member(b, "u2", &n);
    assert(n == 2 && strcmp(gs[0].group_id, "g1") == 0 && strcmp(gs[1].group_id, "g2") == 0);
    ca_community_group_free_array(gs, n);
    /* u1 -> g1 only. */
    gs = ca_community_board_groups_for_member(b, "u1", &n);
    assert(n == 1 && strcmp(gs[0].group_id, "g1") == 0);
    ca_community_group_free_array(gs, n);

    ca_community_board_destroy(b);
    printf("  groups: ok\n");
}

static void test_annc_opps(void) {
    ca_community_board_t *b = ca_community_board_create();

    ca_community_announcement_t a1; memset(&a1, 0, sizeof(a1));
    a1.announcement_id = (char *)"a1"; a1.group_id = (char *)"g1";
    a1.title = (char *)"T1"; a1.body = (char *)"B1"; a1.at_utc_ms = 100;
    ca_community_announcement_t a2; memset(&a2, 0, sizeof(a2));
    a2.announcement_id = (char *)"a2"; a2.group_id = (char *)"g1";
    a2.title = (char *)"T2"; a2.body = (char *)"B2"; a2.at_utc_ms = 300;
    ca_community_announcement_t a3; memset(&a3, 0, sizeof(a3));
    a3.announcement_id = (char *)"a3"; a3.group_id = (char *)"g2";
    a3.title = (char *)"T3"; a3.body = (char *)"B3"; a3.at_utc_ms = 200;
    assert(ca_community_board_post(b, &a1) == 0);
    assert(ca_community_board_post(b, &a2) == 0);
    assert(ca_community_board_post(b, &a3) == 0);

    /* AnnouncementsFor g1 newest-first: a2(300), a1(100). */
    size_t n = 0;
    ca_community_announcement_t *an = ca_community_board_announcements_for(b, "g1", 20, &n);
    assert(n == 2 && strcmp(an[0].announcement_id, "a2") == 0 &&
           strcmp(an[1].announcement_id, "a1") == 0);
    ca_community_announcement_free_array(an, n);
    /* limit 1. */
    an = ca_community_board_announcements_for(b, "g1", 1, &n);
    assert(n == 1 && strcmp(an[0].announcement_id, "a2") == 0);
    ca_community_announcement_free_array(an, n);
    assert(ca_community_board_announcements_for(b, "g1", 0, &n) == NULL && n == (size_t)-1);

    /* Opportunities. */
    ca_community_opportunity_t o1; memset(&o1, 0, sizeof(o1));
    o1.opp_id = (char *)"o1"; o1.group_id = (char *)"g1"; o1.description = (char *)"help";
    o1.volunteers_needed = 3; o1.when_utc_ms = 500;
    ca_community_opportunity_t o2; memset(&o2, 0, sizeof(o2));
    o2.opp_id = (char *)"o2"; o2.group_id = (char *)"g1"; o2.description = (char *)"clean";
    o2.volunteers_needed = 5; o2.when_utc_ms = 300;
    ca_community_opportunity_t o3; memset(&o3, 0, sizeof(o3));
    o3.opp_id = (char *)"o3"; o3.group_id = (char *)"g1"; o3.description = (char *)"past";
    o3.volunteers_needed = 1; o3.when_utc_ms = 50;
    assert(ca_community_board_list_opportunity(b, &o1) == 0);
    assert(ca_community_board_list_opportunity(b, &o2) == 0);
    assert(ca_community_board_list_opportunity(b, &o3) == 0);

    /* Opportunities(now=100): o2(300), o1(500); o3 past. */
    ca_community_opportunity_t *op = ca_community_board_opportunities(b, 100, &n);
    assert(n == 2 && strcmp(op[0].opp_id, "o2") == 0 && strcmp(op[1].opp_id, "o1") == 0);
    ca_community_opportunity_free_array(op, n);

    ca_community_board_destroy(b);
    printf("  annc_opps: ok\n");
}

int main(void) {
    test_groups();
    test_annc_opps();
    printf("test_community: all assertions passed\n");
    return 0;
}
