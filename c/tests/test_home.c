/*
 * test_home.c — CircleAI.Home (C11 port) verification against HomePrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_home_room_t mk_room(const char *id, const char *name) {
    ca_home_room_t r; memset(&r, 0, sizeof(r));
    r.room_id = (char *)id; r.name = (char *)name; r.area_m2 = 12.0;
    return r;
}
static ca_home_device_t mk_dev(const char *id, const char *room, bool on) {
    ca_home_device_t d; memset(&d, 0, sizeof(d));
    d.device_id = (char *)id; d.name = (char *)"D"; d.kind = (char *)"light";
    if (room) { d.has_room = true; d.room_id = (char *)room; }
    d.is_on = on;
    return d;
}
static ca_home_task_t mk_task(const char *id, int64_t due, bool done) {
    ca_home_task_t t; memset(&t, 0, sizeof(t));
    t.task_id = (char *)id; t.description = (char *)"fix"; t.due_on_ms = due;
    t.completed = done;
    return t;
}

static void test_rooms_devices(void) {
    ca_home_board_t *b = ca_home_board_create();
    assert(b);

    ca_home_room_t r1 = mk_room("r1", "Kitchen");
    ca_home_room_t r2 = mk_room("r2", "Bedroom");
    assert(ca_home_board_add_room(b, &r1) == 0);
    assert(ca_home_board_add_room(b, &r2) == 0);

    ca_home_room_t got;
    assert(ca_home_board_get_room(b, "r1", &got) && strcmp(got.name, "Kitchen") == 0);
    ca_home_room_free(&got);

    /* Rooms ordered by Name: Bedroom, Kitchen. */
    size_t n = 0;
    ca_home_room_t *rooms = ca_home_board_rooms(b, &n);
    assert(n == 2 && strcmp(rooms[0].name, "Bedroom") == 0);
    ca_home_room_free_array(rooms, n);

    ca_home_device_t d1 = mk_dev("d1", "r1", true);
    ca_home_device_t d2 = mk_dev("d2", "r1", false);
    ca_home_device_t d3 = mk_dev("d3", NULL, true); /* no room */
    assert(ca_home_board_add_device(b, &d1) == 0);
    assert(ca_home_board_add_device(b, &d2) == 0);
    assert(ca_home_board_add_device(b, &d3) == 0);

    /* DevicesIn(r1): d1, d2 (insertion order); d3 excluded (null room). */
    ca_home_device_t *devs = ca_home_board_devices_in(b, "r1", &n);
    assert(n == 2);
    assert(strcmp(devs[0].device_id, "d1") == 0);
    assert(strcmp(devs[1].device_id, "d2") == 0);
    ca_home_device_free_array(devs, n);

    /* ActiveDevices: d1, d3. */
    devs = ca_home_board_active_devices(b, &n);
    assert(n == 2);
    assert(strcmp(devs[0].device_id, "d1") == 0);
    assert(strcmp(devs[1].device_id, "d3") == 0);
    ca_home_device_free_array(devs, n);

    /* Toggle unknown => 1; toggle d2 on => now active. */
    assert(ca_home_board_toggle(b, "nope", true) == 1);
    assert(ca_home_board_toggle(b, "d2", true) == 0);
    devs = ca_home_board_active_devices(b, &n);
    assert(n == 3);
    ca_home_device_free_array(devs, n);

    ca_home_board_destroy(b);
    printf("  rooms_devices: ok\n");
}

static void test_tasks(void) {
    ca_home_board_t *b = ca_home_board_create();

    ca_home_task_t t1 = mk_task("t1", 300, false);
    ca_home_task_t t2 = mk_task("t2", 100, false);
    ca_home_task_t t3 = mk_task("t3", 200, true);  /* completed */
    ca_home_task_t t4 = mk_task("t4", 999, false);  /* due after 'by' */
    assert(ca_home_board_schedule_task(b, &t1) == 0);
    assert(ca_home_board_schedule_task(b, &t2) == 0);
    assert(ca_home_board_schedule_task(b, &t3) == 0);
    assert(ca_home_board_schedule_task(b, &t4) == 0);

    /* UpcomingTasks(by=500): !completed && due<=500 => t1(300),t2(100);
     * ordered by DueOn asc => t2, t1. */
    size_t n = 0;
    ca_home_task_t *arr = ca_home_board_upcoming_tasks(b, 500, &n);
    assert(n == 2);
    assert(strcmp(arr[0].task_id, "t2") == 0);
    assert(strcmp(arr[1].task_id, "t1") == 0);
    ca_home_task_free_array(arr, n);

    /* CompleteTask(t1) removes it from upcoming. */
    assert(ca_home_board_complete_task(b, "t1") == 0);
    arr = ca_home_board_upcoming_tasks(b, 500, &n);
    assert(n == 1 && strcmp(arr[0].task_id, "t2") == 0);
    ca_home_task_free_array(arr, n);

    assert(ca_home_board_complete_task(b, "nope") == 1);

    ca_home_board_destroy(b);
    printf("  tasks: ok\n");
}

int main(void) {
    test_rooms_devices();
    test_tasks();
    printf("test_home: all assertions passed\n");
    return 0;
}
