/*
 * test_iot.c — CircleAI.IoT (C11 port) verification against IoTPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_iot_device_t mk_dev(const char *id, const char *name) {
    ca_iot_device_t d; memset(&d, 0, sizeof(d));
    d.device_id = (char *)id; d.name = (char *)name; d.kind = (char *)"sensor";
    d.firmware_version = (char *)"1.0"; d.last_seen_utc_ms = 0;
    return d;
}
static ca_iot_telemetry_t mk_tel(const char *id, const char *m, double v, int64_t at) {
    ca_iot_telemetry_t t; memset(&t, 0, sizeof(t));
    t.device_id = (char *)id; t.metric = (char *)m; t.value = v; t.at_utc_ms = at;
    return t;
}
static ca_iot_command_t mk_cmd(const char *cid, const char *did, int64_t at) {
    ca_iot_command_t c; memset(&c, 0, sizeof(c));
    c.command_id = (char *)cid; c.device_id = (char *)did; c.action = (char *)"on";
    c.arguments_json = (char *)"{}"; c.sent_utc_ms = at;
    return c;
}

static void test_devices(void) {
    ca_iot_board_t *b = ca_iot_board_create();
    assert(b);
    assert(ca_iot_board_register(b, NULL) == -1);

    ca_iot_device_t d1 = mk_dev("d1", "Zeta");
    ca_iot_device_t d2 = mk_dev("d2", "Alpha");
    assert(ca_iot_board_register(b, &d1) == 0);
    assert(ca_iot_board_register(b, &d2) == 0);

    ca_iot_device_t got;
    assert(ca_iot_board_get_device(b, "d1", &got) && strcmp(got.firmware_version, "1.0") == 0);
    ca_iot_device_free(&got);

    /* Devices ordered by Name: Alpha, Zeta. */
    size_t n = 0;
    ca_iot_device_t *arr = ca_iot_board_devices(b, &n);
    assert(n == 2 && strcmp(arr[0].name, "Alpha") == 0);
    ca_iot_device_free_array(arr, n);

    ca_iot_board_destroy(b);
    printf("  devices: ok\n");
}

static void test_telemetry(void) {
    ca_iot_board_t *b = ca_iot_board_create();

    assert(isnan(ca_iot_board_latest_value(b, "d1", "temp")));

    ca_iot_telemetry_t t1 = mk_tel("d1", "temp", 20.0, 10);
    ca_iot_telemetry_t t2 = mk_tel("d1", "temp", 25.0, 30); /* newest */
    ca_iot_telemetry_t t3 = mk_tel("d1", "temp", 22.0, 20);
    ca_iot_telemetry_t t4 = mk_tel("d1", "humidity", 50.0, 40);
    assert(ca_iot_board_record_telemetry(b, &t1) == 0);
    assert(ca_iot_board_record_telemetry(b, &t2) == 0);
    assert(ca_iot_board_record_telemetry(b, &t3) == 0);
    assert(ca_iot_board_record_telemetry(b, &t4) == 0);

    assert(ca_iot_board_latest_value(b, "d1", "temp") == 25.0);
    assert(ca_iot_board_latest_value(b, "d1", "humidity") == 50.0);

    /* History newest-first: 25(30), 22(20), 20(10). */
    size_t n = 0;
    ca_iot_telemetry_t *arr = ca_iot_board_history(b, "d1", "temp", 100, &n);
    assert(n == 3);
    assert(arr[0].value == 25.0 && arr[1].value == 22.0 && arr[2].value == 20.0);
    ca_iot_telemetry_free_array(arr, n);

    /* limit caps after sort. */
    arr = ca_iot_board_history(b, "d1", "temp", 2, &n);
    assert(n == 2 && arr[0].value == 25.0 && arr[1].value == 22.0);
    ca_iot_telemetry_free_array(arr, n);

    assert(ca_iot_board_history(b, "d1", "temp", 0, &n) == NULL && n == (size_t)-1);

    ca_iot_board_destroy(b);
    printf("  telemetry: ok\n");
}

static void test_commands(void) {
    ca_iot_board_t *b = ca_iot_board_create();

    ca_iot_command_t c1 = mk_cmd("c1", "d1", 10);
    ca_iot_command_t c2 = mk_cmd("c2", "d1", 30); /* newest */
    ca_iot_command_t c3 = mk_cmd("c3", "d2", 20);
    assert(ca_iot_board_send_command(b, &c1) == 0);
    assert(ca_iot_board_send_command(b, &c2) == 0);
    assert(ca_iot_board_send_command(b, &c3) == 0);

    /* CommandsFor(d1) newest-first: c2(30), c1(10). */
    size_t n = 0;
    ca_iot_command_t *arr = ca_iot_board_commands_for(b, "d1", &n);
    assert(n == 2);
    assert(strcmp(arr[0].command_id, "c2") == 0);
    assert(strcmp(arr[1].command_id, "c1") == 0);
    ca_iot_command_free_array(arr, n);

    arr = ca_iot_board_commands_for(b, "zzz", &n);
    assert(n == 0 && arr == NULL);

    ca_iot_board_destroy(b);
    printf("  commands: ok\n");
}

int main(void) {
    test_devices();
    test_telemetry();
    test_commands();
    printf("test_iot: all assertions passed\n");
    return 0;
}
