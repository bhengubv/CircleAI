/*
 * test_energy.c — CircleAI.Energy (C11 port) verification against
 * EnergyPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define S CA_ENERGY_DECIMAL_SCALE

static void test_readings_cost(void) {
    ca_energy_board_t *b = ca_energy_board_create();
    assert(b);
    assert(ca_energy_board_record(b, NULL) == -1);

    /* Meter m1 cumulative readings 100,120,150 at t=100,200,300. */
    ca_energy_reading_t r1; memset(&r1, 0, sizeof(r1));
    r1.meter_id = (char *)"m1"; r1.kwh = 100.0; r1.at_utc_ms = 100;
    ca_energy_reading_t r2; memset(&r2, 0, sizeof(r2));
    r2.meter_id = (char *)"m1"; r2.kwh = 120.0; r2.at_utc_ms = 200;
    ca_energy_reading_t r3; memset(&r3, 0, sizeof(r3));
    r3.meter_id = (char *)"m1"; r3.kwh = 150.0; r3.at_utc_ms = 300;
    assert(ca_energy_board_record(b, &r3) == 0); /* out of order insert */
    assert(ca_energy_board_record(b, &r1) == 0);
    assert(ca_energy_board_record(b, &r2) == 0);

    /* ReadingsFor since 50 ascending: 100,120,150. */
    size_t n = 0;
    ca_energy_reading_t *rs = ca_energy_board_readings_for(b, "m1", 50, &n);
    assert(n == 3 && rs[0].kwh == 100.0 && rs[2].kwh == 150.0);
    ca_energy_reading_free_array(rs, n);

    /* TotalKwhSince 50 = 150 - 100 = 50. */
    assert(ca_energy_board_total_kwh_since(b, "m1", 50) == 50.0);
    /* since 250 -> only 150 -> < 2 readings -> 0. */
    assert(ca_energy_board_total_kwh_since(b, "m1", 250) == 0.0);

    /* Tariff. */
    ca_energy_tariff_t t1; memset(&t1, 0, sizeof(t1));
    t1.tariff_id = (char *)"t1"; t1.name = (char *)"Std"; t1.peak_kwh_rate = 0.5;
    t1.off_peak_kwh_rate = 0.2; t1.currency = (char *)"USD";
    assert(ca_energy_board_set_tariff(b, &t1) == 0);

    ca_energy_tariff_t gt;
    assert(ca_energy_board_get_tariff(b, "t1", &gt) && gt.peak_kwh_rate == 0.5);
    ca_energy_tariff_free(&gt);

    /* EstimateCost = 50 kwh * 0.5 = 25.0 -> 25 * scale. */
    ca_energy_decimal_t cost = 0;
    assert(ca_energy_board_estimate_cost(b, "m1", "t1", 50, &cost) == 0);
    assert(cost == 25 * S);
    assert(ca_energy_board_estimate_cost(b, "m1", "nope", 50, &cost) == -2);

    ca_energy_board_destroy(b);
    printf("  readings_cost: ok\n");
}

static void test_outages(void) {
    ca_energy_board_t *b = ca_energy_board_create();

    ca_energy_outage_t o1; memset(&o1, 0, sizeof(o1));
    o1.outage_id = (char *)"o1"; o1.area = (char *)"North"; o1.start_utc_ms = 100;
    o1.has_end_utc = false; o1.has_reason = true; o1.reason = (char *)"storm";
    ca_energy_outage_t o2; memset(&o2, 0, sizeof(o2));
    o2.outage_id = (char *)"o2"; o2.area = (char *)"South"; o2.start_utc_ms = 200;
    o2.has_end_utc = true; o2.end_utc_ms = 300; o2.has_reason = false;
    assert(ca_energy_board_log_outage(b, &o1) == 0);
    assert(ca_energy_board_log_outage(b, &o2) == 0);

    /* ActiveOutages: o1 only (o2 ended). */
    size_t n = 0;
    ca_energy_outage_t *ao = ca_energy_board_active_outages(b, &n);
    assert(n == 1 && strcmp(ao[0].outage_id, "o1") == 0 && ao[0].has_reason &&
           strcmp(ao[0].reason, "storm") == 0);
    ca_energy_outage_free_array(ao, n);

    /* Resolve o1 by logging with an end time -> no active outages. */
    o1.has_end_utc = true; o1.end_utc_ms = 400;
    assert(ca_energy_board_log_outage(b, &o1) == 0);
    ao = ca_energy_board_active_outages(b, &n);
    assert(ao == NULL && n == 0);

    ca_energy_board_destroy(b);
    printf("  outages: ok\n");
}

int main(void) {
    test_readings_cost();
    test_outages();
    printf("test_energy: all assertions passed\n");
    return 0;
}
