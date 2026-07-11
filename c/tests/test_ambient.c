/*
 * test_ambient.c — CircleAI.Ambient (C11 port) verification against
 * AmbientPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_ambient_reading_t mk_reading(const char *dev, double t, double h,
                                       double noise, int64_t at) {
    ca_ambient_reading_t r; memset(&r, 0, sizeof(r));
    r.device_id = (char *)dev; r.temperature_c = t; r.humidity = h;
    r.lux_light = 300; r.db_noise = noise; r.at_utc_ms = at;
    return r;
}

static void test_readings(void) {
    ca_ambient_board_t *b = ca_ambient_board_create();
    assert(b);
    assert(ca_ambient_board_record(b, NULL) == -1);

    ca_ambient_reading_t r1 = mk_reading("d1", 20, 40, 35, 100);
    ca_ambient_reading_t r2 = mk_reading("d1", 22, 45, 30, 300);
    ca_ambient_reading_t r3 = mk_reading("d1", 21, 42, 32, 200);
    assert(ca_ambient_board_record(b, &r1) == 0);
    assert(ca_ambient_board_record(b, &r2) == 0);
    assert(ca_ambient_board_record(b, &r3) == 0);

    /* Latest = r2 (t=300). */
    ca_ambient_reading_t got;
    assert(ca_ambient_board_latest(b, "d1", &got) && got.temperature_c == 22 &&
           got.at_utc_ms == 300);
    ca_ambient_reading_free(&got);
    assert(!ca_ambient_board_latest(b, "nope", &got));

    /* History newest-first: 300,200,100. */
    size_t n = 0;
    ca_ambient_reading_t *h = ca_ambient_board_history(b, "d1", 50, &n);
    assert(n == 3 && h[0].at_utc_ms == 300 && h[1].at_utc_ms == 200 && h[2].at_utc_ms == 100);
    ca_ambient_reading_free_array(h, n);
    /* limit 1. */
    h = ca_ambient_board_history(b, "d1", 1, &n);
    assert(n == 1 && h[0].at_utc_ms == 300);
    ca_ambient_reading_free_array(h, n);

    ca_ambient_board_destroy(b);
    printf("  readings: ok\n");
}

static void test_comfort(void) {
    ca_ambient_board_t *b = ca_ambient_board_create();

    /* Target: 21C, 45% humidity, noise <= 40. */
    ca_ambient_preference_t p; memset(&p, 0, sizeof(p));
    p.location = (char *)"office"; p.target_temp_c = 21; p.target_humidity = 45;
    p.max_noise_db = 40;
    assert(ca_ambient_board_set_preference(b, &p) == 0);

    ca_ambient_preference_t gp;
    assert(ca_ambient_board_get_preference(b, "office", &gp) && gp.target_temp_c == 21);
    ca_ambient_preference_free(&gp);

    /* No reading yet -> not comfortable. */
    assert(!ca_ambient_board_is_comfortable(b, "d1", "office"));

    /* Comfortable reading: 22C (|1|<=2), 48% (|3|<=10), 35db (<=40). */
    ca_ambient_reading_t ok = mk_reading("d1", 22, 48, 35, 100);
    assert(ca_ambient_board_record(b, &ok) == 0);
    assert(ca_ambient_board_is_comfortable(b, "d1", "office"));

    /* Later loud reading: noise 50 > 40 -> not comfortable (uses latest). */
    ca_ambient_reading_t loud = mk_reading("d1", 21, 45, 50, 200);
    assert(ca_ambient_board_record(b, &loud) == 0);
    assert(!ca_ambient_board_is_comfortable(b, "d1", "office"));

    /* Unknown location -> false. */
    assert(!ca_ambient_board_is_comfortable(b, "d1", "garage"));

    ca_ambient_board_destroy(b);
    printf("  comfort: ok\n");
}

int main(void) {
    test_readings();
    test_comfort();
    printf("test_ambient: all assertions passed\n");
    return 0;
}
