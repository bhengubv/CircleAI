/*
 * test_gaming.c — CircleAI.Gaming (C11 port) verification against
 * GamingPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define TICKS_PER_MIN (60LL * 10000000LL)

static ca_gaming_title_t mk_title(const char *id, const char *name,
                                  const char *genre) {
    ca_gaming_title_t t; memset(&t, 0, sizeof(t));
    t.title_id = (char *)id; t.name = (char *)name; t.genre = (char *)genre;
    t.platform = (char *)"PC";
    return t;
}
static ca_gaming_session_t mk_sess(const char *id, const char *uid,
                                   const char *tid, int64_t mins, int64_t at) {
    ca_gaming_session_t s; memset(&s, 0, sizeof(s));
    s.session_id = (char *)id; s.user_id = (char *)uid; s.title_id = (char *)tid;
    s.duration_ticks = mins * TICKS_PER_MIN; s.at_utc_ms = at;
    return s;
}

static void test_titles_play(void) {
    ca_gaming_board_t *b = ca_gaming_board_create();
    assert(b);
    assert(ca_gaming_board_add_title(b, NULL) == -1);

    ca_gaming_title_t t1 = mk_title("t1", "Zelda", "Adventure");
    ca_gaming_title_t t2 = mk_title("t2", "Doom", "Shooter");
    ca_gaming_title_t t3 = mk_title("t3", "Portal", "adventure"); /* CI genre */
    assert(ca_gaming_board_add_title(b, &t1) == 0);
    assert(ca_gaming_board_add_title(b, &t2) == 0);
    assert(ca_gaming_board_add_title(b, &t3) == 0);

    ca_gaming_title_t got;
    assert(ca_gaming_board_get_title(b, "t2", &got) && strcmp(got.name, "Doom") == 0);
    ca_gaming_title_free(&got);

    /* TitlesByGenre "adventure" (CI): t1, t3 (insertion order). */
    size_t n = 0;
    ca_gaming_title_t *ts = ca_gaming_board_titles_by_genre(b, "adventure", &n);
    assert(n == 2 && strcmp(ts[0].title_id, "t1") == 0 && strcmp(ts[1].title_id, "t3") == 0);
    ca_gaming_title_free_array(ts, n);

    /* Sessions: u1 plays t1 30+45=75min, t2 60min, t3 10min. */
    ca_gaming_session_t s1 = mk_sess("s1", "u1", "t1", 30, 100);
    ca_gaming_session_t s2 = mk_sess("s2", "u1", "t1", 45, 200);
    ca_gaming_session_t s3 = mk_sess("s3", "u1", "t2", 60, 300);
    ca_gaming_session_t s4 = mk_sess("s4", "u1", "t3", 10, 400);
    assert(ca_gaming_board_record_session(b, &s1) == 0);
    assert(ca_gaming_board_record_session(b, &s2) == 0);
    assert(ca_gaming_board_record_session(b, &s3) == 0);
    assert(ca_gaming_board_record_session(b, &s4) == 0);

    /* TotalPlayTime u1/t1 = 75 min. */
    assert(ca_gaming_board_total_play_time(b, "u1", "t1") == 75 * TICKS_PER_MIN);
    assert(ca_gaming_board_total_play_time(b, "u1", "none") == 0);

    /* MostPlayed(u1): t1(75) > t2(60) > t3(10). topK=2 -> t1,t2. */
    ca_gaming_title_t *mp = ca_gaming_board_most_played(b, "u1", 2, &n);
    assert(n == 2 && strcmp(mp[0].title_id, "t1") == 0 && strcmp(mp[1].title_id, "t2") == 0);
    ca_gaming_title_free_array(mp, n);
    /* topK=5 -> all three ordered. */
    mp = ca_gaming_board_most_played(b, "u1", 5, &n);
    assert(n == 3 && strcmp(mp[0].title_id, "t1") == 0 && strcmp(mp[2].title_id, "t3") == 0);
    ca_gaming_title_free_array(mp, n);
    assert(ca_gaming_board_most_played(b, "u1", 0, &n) == NULL && n == (size_t)-1);

    ca_gaming_board_destroy(b);
    printf("  titles_play: ok\n");
}

static void test_achievements(void) {
    ca_gaming_board_t *b = ca_gaming_board_create();

    ca_gaming_unlock_t u1; memset(&u1, 0, sizeof(u1));
    u1.unlock_id = (char *)"u1"; u1.user_id = (char *)"p1"; u1.title_id = (char *)"t1";
    u1.achievement = (char *)"First Blood"; u1.at_utc_ms = 100;
    ca_gaming_unlock_t u2; memset(&u2, 0, sizeof(u2));
    u2.unlock_id = (char *)"u2"; u2.user_id = (char *)"p1"; u2.title_id = (char *)"t1";
    u2.achievement = (char *)"Speedrun"; u2.at_utc_ms = 300;
    assert(ca_gaming_board_unlock(b, &u1) == 0);
    assert(ca_gaming_board_unlock(b, &u2) == 0);

    /* Newest-first: u2(300), u1(100). */
    size_t n = 0;
    ca_gaming_unlock_t *ac = ca_gaming_board_achievements_for(b, "p1", &n);
    assert(n == 2 && strcmp(ac[0].unlock_id, "u2") == 0 && strcmp(ac[1].unlock_id, "u1") == 0);
    ca_gaming_unlock_free_array(ac, n);

    ca_gaming_board_destroy(b);
    printf("  achievements: ok\n");
}

int main(void) {
    test_titles_play();
    test_achievements();
    printf("test_gaming: all assertions passed\n");
    return 0;
}
