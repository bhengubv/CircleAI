/*
 * test_beauty.c — CircleAI.Beauty (C11 port) verification against
 * BeautyPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_treatments_appts(void) {
    ca_beauty_board_t *b = ca_beauty_board_create();
    assert(b);
    assert(ca_beauty_board_add_treatment(b, NULL) == -1);

    ca_beauty_treatment_t t1; memset(&t1, 0, sizeof(t1));
    t1.treatment_id = (char *)"t1"; t1.name = (char *)"Acne Facial";
    t1.duration_minutes = 60; t1.price = 50 * CA_BEAUTY_DECIMAL_SCALE;
    t1.currency = (char *)"USD";
    assert(ca_beauty_board_add_treatment(b, &t1) == 0);

    ca_beauty_treatment_t got;
    assert(ca_beauty_board_get_treatment(b, "t1", &got) &&
           got.price == 50 * CA_BEAUTY_DECIMAL_SCALE);
    ca_beauty_treatment_free(&got);
    assert(!ca_beauty_board_get_treatment(b, "nope", &got));

    /* Appointments. */
    ca_beauty_appointment_t a1; memset(&a1, 0, sizeof(a1));
    a1.appt_id = (char *)"a1"; a1.client_name = (char *)"Ann"; a1.treatment_id = (char *)"t1";
    a1.at_utc_ms = 300; a1.has_notes = true; a1.notes = (char *)"first visit";
    ca_beauty_appointment_t a2; memset(&a2, 0, sizeof(a2));
    a2.appt_id = (char *)"a2"; a2.client_name = (char *)"Bea"; a2.treatment_id = (char *)"t1";
    a2.at_utc_ms = 100; a2.has_notes = false;
    ca_beauty_appointment_t a3; memset(&a3, 0, sizeof(a3));
    a3.appt_id = (char *)"a3"; a3.client_name = (char *)"Cid"; a3.treatment_id = (char *)"t1";
    a3.at_utc_ms = 900;
    assert(ca_beauty_board_book(b, &a1) == 0);
    assert(ca_beauty_board_book(b, &a2) == 0);
    assert(ca_beauty_board_book(b, &a3) == 0);

    /* Between [100,300] inclusive ordered asc: a2(100), a1(300). */
    size_t n = 0;
    ca_beauty_appointment_t *ap = ca_beauty_board_appointments_between(b, 100, 300, &n);
    assert(n == 2 && strcmp(ap[0].appt_id, "a2") == 0 && strcmp(ap[1].appt_id, "a1") == 0);
    assert(ap[1].has_notes && strcmp(ap[1].notes, "first visit") == 0);
    assert(!ap[0].has_notes);
    ca_beauty_appointment_free_array(ap, n);

    ca_beauty_board_destroy(b);
    printf("  treatments_appts: ok\n");
}

static void test_profile_recommend(void) {
    ca_beauty_board_t *b = ca_beauty_board_create();

    ca_beauty_treatment_t t1; memset(&t1, 0, sizeof(t1));
    t1.treatment_id = (char *)"t1"; t1.name = (char *)"Acne Treatment";
    t1.duration_minutes = 60; t1.price = 0; t1.currency = (char *)"USD";
    ca_beauty_treatment_t t2; memset(&t2, 0, sizeof(t2));
    t2.treatment_id = (char *)"t2"; t2.name = (char *)"Anti-Wrinkle Serum";
    t2.duration_minutes = 30; t2.price = 0; t2.currency = (char *)"USD";
    ca_beauty_treatment_t t3; memset(&t3, 0, sizeof(t3));
    t3.treatment_id = (char *)"t3"; t3.name = (char *)"Relaxing Massage";
    t3.duration_minutes = 45; t3.price = 0; t3.currency = (char *)"USD";
    assert(ca_beauty_board_add_treatment(b, &t1) == 0);
    assert(ca_beauty_board_add_treatment(b, &t2) == 0);
    assert(ca_beauty_board_add_treatment(b, &t3) == 0);

    /* No profile -> empty. */
    size_t n = 0;
    ca_beauty_treatment_t *rec = ca_beauty_board_recommend_for(b, "Ann", &n);
    assert(rec == NULL && n == 0);

    char *concerns[] = { (char *)"acne", (char *)"wrinkle" };
    ca_beauty_skin_profile_t p; memset(&p, 0, sizeof(p));
    p.client_name = (char *)"Ann"; p.skin_type = (char *)"oily";
    p.concerns = concerns; p.concern_count = 2;
    assert(ca_beauty_board_save_profile(b, &p) == 0);

    ca_beauty_skin_profile_t gp;
    assert(ca_beauty_board_get_profile(b, "Ann", &gp) && gp.concern_count == 2);
    ca_beauty_skin_profile_free(&gp);

    /* RecommendFor Ann: t1 (Acne) + t2 (Wrinkle); not t3. */
    rec = ca_beauty_board_recommend_for(b, "Ann", &n);
    assert(n == 2 && strcmp(rec[0].treatment_id, "t1") == 0 &&
           strcmp(rec[1].treatment_id, "t2") == 0);
    ca_beauty_treatment_free_array(rec, n);

    ca_beauty_board_destroy(b);
    printf("  profile_recommend: ok\n");
}

int main(void) {
    test_treatments_appts();
    test_profile_recommend();
    printf("test_beauty: all assertions passed\n");
    return 0;
}
