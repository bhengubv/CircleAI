/*
 * test_wearable.c — CircleAI.Wearable (C11 port) verification against
 * WearablePrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_devices(void) {
    ca_wearable_board_t *b = ca_wearable_board_create();
    assert(b);
    assert(ca_wearable_board_add(b, NULL) == -1);

    ca_wearable_device_t d1; memset(&d1, 0, sizeof(d1));
    d1.device_id = (char *)"d1"; d1.kind = CA_WEARABLE_KIND_SMARTWATCH;
    d1.vendor = (char *)"Zenith"; d1.firmware_version = (char *)"1.0"; d1.battery_pct = 90;
    ca_wearable_device_t d2; memset(&d2, 0, sizeof(d2));
    d2.device_id = (char *)"d2"; d2.kind = CA_WEARABLE_KIND_FITNESS_BAND;
    d2.vendor = (char *)"Acme"; d2.firmware_version = (char *)"2.0"; d2.battery_pct = 50;
    assert(ca_wearable_board_add(b, &d1) == 0);
    assert(ca_wearable_board_add(b, &d2) == 0);

    ca_wearable_device_t got;
    assert(ca_wearable_board_get_device(b, "d1", &got) && got.battery_pct == 90);
    ca_wearable_device_free(&got);

    /* Devices ordered by Vendor: Acme(d2), Zenith(d1). */
    size_t n = 0;
    ca_wearable_device_t *ds = ca_wearable_board_devices(b, &n);
    assert(n == 2 && strcmp(ds[0].device_id, "d2") == 0 && strcmp(ds[1].device_id, "d1") == 0);
    ca_wearable_device_free_array(ds, n);

    ca_wearable_board_destroy(b);
    printf("  devices: ok\n");
}

static void test_samples(void) {
    ca_wearable_board_t *b = ca_wearable_board_create();
    ca_wearable_device_t d1; memset(&d1, 0, sizeof(d1));
    d1.device_id = (char *)"d1"; d1.kind = CA_WEARABLE_KIND_SMARTWATCH;
    d1.vendor = (char *)"Z"; d1.firmware_version = (char *)"1.0"; d1.battery_pct = 90;
    assert(ca_wearable_board_add(b, &d1) == 0);

    /* Record on unknown device rejected. */
    ca_wearable_sample_t bad; memset(&bad, 0, sizeof(bad));
    bad.device_id = (char *)"nope"; bad.kind = CA_WEARABLE_TELEMETRY_HEART_RATE;
    bad.value = 60; bad.at_utc_ms = 1;
    assert(ca_wearable_board_record(b, &bad) == -2);

    /* HR samples at t=100(70), 300(80), 200(75). */
    ca_wearable_sample_t s1; memset(&s1, 0, sizeof(s1));
    s1.device_id = (char *)"d1"; s1.kind = CA_WEARABLE_TELEMETRY_HEART_RATE; s1.value = 70; s1.at_utc_ms = 100;
    ca_wearable_sample_t s2; memset(&s2, 0, sizeof(s2));
    s2.device_id = (char *)"d1"; s2.kind = CA_WEARABLE_TELEMETRY_HEART_RATE; s2.value = 80; s2.at_utc_ms = 300;
    ca_wearable_sample_t s3; memset(&s3, 0, sizeof(s3));
    s3.device_id = (char *)"d1"; s3.kind = CA_WEARABLE_TELEMETRY_HEART_RATE; s3.value = 75; s3.at_utc_ms = 200;
    assert(ca_wearable_board_record(b, &s1) == 0);
    assert(ca_wearable_board_record(b, &s2) == 0);
    assert(ca_wearable_board_record(b, &s3) == 0);

    /* ReadSince(50) ascending: 70,75,80. */
    size_t n = 0;
    ca_wearable_sample_t *rs = ca_wearable_board_read_since(b, "d1", CA_WEARABLE_TELEMETRY_HEART_RATE, 50, &n);
    assert(n == 3 && rs[0].value == 70 && rs[1].value == 75 && rs[2].value == 80);
    ca_wearable_sample_free_array(rs, n);

    /* LatestValue = 80 (t=300). */
    double v = 0;
    assert(ca_wearable_board_latest_value(b, "d1", CA_WEARABLE_TELEMETRY_HEART_RATE, &v) && v == 80);
    /* No steps samples -> null. */
    assert(!ca_wearable_board_latest_value(b, "d1", CA_WEARABLE_TELEMETRY_STEPS, &v));

    /* AverageValue since 50 = (70+75+80)/3 = 75. */
    assert(fabs(ca_wearable_board_average_value(b, "d1", CA_WEARABLE_TELEMETRY_HEART_RATE, 50) - 75.0) < 1e-9);
    /* No matching -> NaN. */
    assert(isnan(ca_wearable_board_average_value(b, "d1", CA_WEARABLE_TELEMETRY_STEPS, 50)));

    ca_wearable_board_destroy(b);
    printf("  samples: ok\n");
}

int main(void) {
    test_devices();
    test_samples();
    printf("test_wearable: all assertions passed\n");
    return 0;
}
