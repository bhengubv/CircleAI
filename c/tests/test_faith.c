/*
 * test_faith.c — CircleAI.Faith (C11 port) verification against FaithPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_services_prayers(void) {
    ca_faith_board_t *b = ca_faith_board_create();
    assert(b);
    assert(ca_faith_board_schedule(b, NULL) == -1);

    ca_faith_service_t s1; memset(&s1, 0, sizeof(s1));
    s1.service_id = (char *)"s1"; s1.community_name = (char *)"St Mary";
    s1.title = (char *)"Sunday Mass"; s1.start_utc_ms = 500; s1.location = (char *)"Nave";
    ca_faith_service_t s2; memset(&s2, 0, sizeof(s2));
    s2.service_id = (char *)"s2"; s2.community_name = (char *)"St Mary";
    s2.title = (char *)"Vespers"; s2.start_utc_ms = 300; s2.location = (char *)"Chapel";
    ca_faith_service_t s3; memset(&s3, 0, sizeof(s3));
    s3.service_id = (char *)"s3"; s3.community_name = (char *)"St Mary";
    s3.title = (char *)"Late"; s3.start_utc_ms = 900; s3.location = (char *)"Hall";
    assert(ca_faith_board_schedule(b, &s1) == 0);
    assert(ca_faith_board_schedule(b, &s2) == 0);
    assert(ca_faith_board_schedule(b, &s3) == 0);

    /* Between [300,500] asc: s2(300), s1(500). */
    size_t n = 0;
    ca_faith_service_t *sv = ca_faith_board_services_between(b, 300, 500, &n);
    assert(n == 2 && strcmp(sv[0].service_id, "s2") == 0 && strcmp(sv[1].service_id, "s1") == 0);
    ca_faith_service_free_array(sv, n);

    /* Prayers. */
    ca_faith_prayer_t p1; memset(&p1, 0, sizeof(p1));
    p1.request_id = (char *)"p1"; p1.author = (char *)"Ann"; p1.body = (char *)"health";
    p1.submitted_utc_ms = 100; p1.is_anonymous = false;
    ca_faith_prayer_t p2; memset(&p2, 0, sizeof(p2));
    p2.request_id = (char *)"p2"; p2.author = (char *)"Anon"; p2.body = (char *)"peace";
    p2.submitted_utc_ms = 300; p2.is_anonymous = true;
    assert(ca_faith_board_submit_prayer(b, &p1) == 0);
    assert(ca_faith_board_submit_prayer(b, &p2) == 0);

    /* RecentPrayers newest-first: p2(300), p1(100). */
    ca_faith_prayer_t *pr = ca_faith_board_recent_prayers(b, 20, &n);
    assert(n == 2 && strcmp(pr[0].request_id, "p2") == 0 && pr[0].is_anonymous &&
           strcmp(pr[1].request_id, "p1") == 0);
    ca_faith_prayer_free_array(pr, n);
    /* limit 1. */
    pr = ca_faith_board_recent_prayers(b, 1, &n);
    assert(n == 1 && strcmp(pr[0].request_id, "p2") == 0);
    ca_faith_prayer_free_array(pr, n);

    ca_faith_board_destroy(b);
    printf("  services_prayers: ok\n");
}

static void test_scripture(void) {
    ca_faith_board_t *b = ca_faith_board_create();

    ca_faith_scripture_t r1; memset(&r1, 0, sizeof(r1));
    r1.reference_id = (char *)"r1"; r1.tradition = (char *)"Christian";
    r1.book = (char *)"John"; r1.chapter = 3; r1.verse = 16; r1.text = (char *)"For God...";
    ca_faith_scripture_t r2; memset(&r2, 0, sizeof(r2));
    r2.reference_id = (char *)"r2"; r2.tradition = (char *)"Christian";
    r2.book = (char *)"Genesis"; r2.chapter = 1; r2.verse = 1; r2.text = (char *)"In the beginning...";
    assert(ca_faith_board_add_scripture(b, &r1) == 0);
    assert(ca_faith_board_add_scripture(b, &r2) == 0);

    /* Lookup exact. */
    ca_faith_scripture_t got;
    assert(ca_faith_board_lookup(b, "Christian", "John", 3, 16, &got) &&
           strcmp(got.reference_id, "r1") == 0);
    ca_faith_scripture_free(&got);
    /* wrong verse -> miss. */
    assert(!ca_faith_board_lookup(b, "Christian", "John", 3, 17, &got));

    /* ByTradition "christian" (CI): r1, r2. */
    size_t n = 0;
    ca_faith_scripture_t *bt = ca_faith_board_by_tradition(b, "christian", &n);
    assert(n == 2 && strcmp(bt[0].reference_id, "r1") == 0 && strcmp(bt[1].reference_id, "r2") == 0);
    ca_faith_scripture_free_array(bt, n);

    ca_faith_board_destroy(b);
    printf("  scripture: ok\n");
}

int main(void) {
    test_services_prayers();
    test_scripture();
    printf("test_faith: all assertions passed\n");
    return 0;
}
