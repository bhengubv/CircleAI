/*
 * test_personal_mental.c — CircleAI.Personal.Mental (C11 port) verification
 * against PersonalMentalPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include <math.h>
#include "circle_ai/circle_ai.h"

/* A fixed "now" so the 7-day window is deterministic. */
#define NOW 1000000000000LL
#define DAY_MS (24LL * 60 * 60 * 1000)

static void test_moods(void) {
    ca_mental_board_t *b = ca_mental_board_create();
    assert(b);

    /* AvgMood7Day of empty -> NaN. */
    assert(isnan(ca_mental_board_avg_mood_7day(b, NOW)));
    size_t n = 0;
    ca_mental_mood_log_t *arr = ca_mental_board_last_7_days(b, NOW, &n);
    assert(n == 0 && arr == NULL);

    /* Three within window (ages 1d, 3d, 6d), one outside (age 8d). */
    ca_mental_mood_log_t m1; memset(&m1, 0, sizeof(m1));
    m1.mood = CA_MOOD_GOOD; m1.at_utc_ms = NOW - 1 * DAY_MS;
    ca_mental_mood_log_t m2; memset(&m2, 0, sizeof(m2));
    m2.mood = CA_MOOD_LOW; m2.at_utc_ms = NOW - 3 * DAY_MS; m2.has_note = true; m2.note = (char *)"meh";
    ca_mental_mood_log_t m3; memset(&m3, 0, sizeof(m3));
    m3.mood = CA_MOOD_GREAT; m3.at_utc_ms = NOW - 6 * DAY_MS;
    ca_mental_mood_log_t mOld; memset(&mOld, 0, sizeof(mOld));
    mOld.mood = CA_MOOD_VERY_LOW; mOld.at_utc_ms = NOW - 8 * DAY_MS;
    assert(ca_mental_board_log_mood(b, &m1) == 0);
    assert(ca_mental_board_log_mood(b, &m2) == 0);
    assert(ca_mental_board_log_mood(b, &m3) == 0);
    assert(ca_mental_board_log_mood(b, &mOld) == 0);

    /* Last7Days ascending by AtUtc: m3(6d), m2(3d), m1(1d). */
    arr = ca_mental_board_last_7_days(b, NOW, &n);
    assert(n == 3);
    assert(arr[0].mood == CA_MOOD_GREAT);   /* oldest-in-window first */
    assert(arr[1].mood == CA_MOOD_LOW && arr[1].has_note && strcmp(arr[1].note, "meh") == 0);
    assert(arr[2].mood == CA_MOOD_GOOD);
    ca_mental_mood_log_free_array(arr, n);

    /* AvgMood7Day = (Good(3) + Low(1) + Great(4)) / 3 = 8/3. */
    double avg = ca_mental_board_avg_mood_7day(b, NOW);
    assert(fabs(avg - (8.0 / 3.0)) < 1e-9);

    ca_mental_board_destroy(b);
    printf("  moods: ok\n");
}

static void test_entries(void) {
    ca_mental_board_t *b = ca_mental_board_create();

    /* Empty/whitespace EntryId rejected -> 2. */
    ca_mental_journal_entry_t bad; memset(&bad, 0, sizeof(bad));
    bad.entry_id = (char *)"   "; bad.title = (char *)"t"; bad.body = (char *)"b";
    assert(ca_mental_board_add_entry(b, &bad) == 2);

    ca_mental_journal_entry_t e1; memset(&e1, 0, sizeof(e1));
    e1.entry_id = (char *)"e1"; e1.title = (char *)"First"; e1.body = (char *)"..."; e1.at_utc_ms = 100;
    ca_mental_journal_entry_t e2; memset(&e2, 0, sizeof(e2));
    e2.entry_id = (char *)"e2"; e2.title = (char *)"Second"; e2.body = (char *)"..."; e2.at_utc_ms = 300;
    ca_mental_journal_entry_t e3; memset(&e3, 0, sizeof(e3));
    e3.entry_id = (char *)"e3"; e3.title = (char *)"Third"; e3.body = (char *)"..."; e3.at_utc_ms = 200;
    assert(ca_mental_board_add_entry(b, &e1) == 0);
    assert(ca_mental_board_add_entry(b, &e2) == 0);
    assert(ca_mental_board_add_entry(b, &e3) == 0);

    /* Entries ordered by AtUtc descending: e2(300), e3(200), e1(100). */
    size_t n = 0;
    ca_mental_journal_entry_t *arr = ca_mental_board_entries(b, &n);
    assert(n == 3);
    assert(strcmp(arr[0].entry_id, "e2") == 0);
    assert(strcmp(arr[1].entry_id, "e3") == 0);
    assert(strcmp(arr[2].entry_id, "e1") == 0);
    ca_mental_journal_entry_free_array(arr, n);

    ca_mental_board_destroy(b);
    printf("  entries: ok\n");
}

static void test_strategies(void) {
    ca_mental_board_t *b = ca_mental_board_create();

    const char *tg1[] = { "breathing", "calm" };
    const char *tg2[] = { "Grounding" };
    ca_mental_strategy_t s1; memset(&s1, 0, sizeof(s1));
    s1.strategy_id = (char *)"s1"; s1.title = (char *)"Box Breathing";
    s1.description = (char *)"..."; s1.tags = (char **)tg1; s1.tag_count = 2;
    ca_mental_strategy_t s2; memset(&s2, 0, sizeof(s2));
    s2.strategy_id = (char *)"s2"; s2.title = (char *)"5-4-3-2-1";
    s2.description = (char *)"..."; s2.tags = (char **)tg2; s2.tag_count = 1;
    assert(ca_mental_board_register_strategy(b, &s1) == 0);
    assert(ca_mental_board_register_strategy(b, &s2) == 0);

    /* StrategiesByTag("CALM") case-insensitive -> s1. */
    size_t n = 0;
    ca_mental_strategy_t *arr = ca_mental_board_strategies_by_tag(b, "CALM", &n);
    assert(n == 1 && strcmp(arr[0].strategy_id, "s1") == 0 && arr[0].tag_count == 2);
    ca_mental_strategy_free_array(arr, n);

    /* miss. */
    arr = ca_mental_board_strategies_by_tag(b, "zzz", &n);
    assert(n == 0 && arr == NULL);

    /* whitespace tag -> SIZE_MAX. */
    arr = ca_mental_board_strategies_by_tag(b, "  ", &n);
    assert(n == (size_t)-1);

    ca_mental_board_destroy(b);
    printf("  strategies: ok\n");
}

int main(void) {
    test_moods();
    test_entries();
    test_strategies();
    printf("test_personal_mental: all assertions passed\n");
    return 0;
}
