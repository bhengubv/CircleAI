#ifndef CIRCLE_AI_HOME_H
#define CIRCLE_AI_HOME_H

/*
 * home.h — CircleAI.Home (C11 port of HomePrimitives.cs).
 *
 *   Records : Room(RoomId, Name, double AreaM2);
 *             HomeDevice(DeviceId, Name, Kind, string? RoomId, bool IsOn);
 *             MaintenanceTask(TaskId, Description, DateTime DueOn,
 *                             bool Completed).
 *   Board   : IHomeBoard -> InMemoryHomeBoard
 *               AddRoom (RoomId keyed), GetRoom(id) -> room?,
 *               Rooms ordered by Name asc, AddDevice (DeviceId keyed),
 *               Toggle(deviceId, on) — throws on unknown (rc 1),
 *               DevicesIn(roomId) where RoomId == roomId (ordinal) in insertion
 *               order, ActiveDevices where IsOn in insertion order,
 *               ScheduleTask (TaskId keyed), CompleteTask(taskId) — throws on
 *               unknown (rc 1), UpcomingTasks(by) where !Completed && DueOn <= by
 *               ordered by DueOn asc.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Nullable
 * RoomId via has_room. DueOn (DateTime) as int64 Unix ms UTC. Linear arrays, no
 * pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Room(RoomId, Name, double AreaM2). */
typedef struct {
    char  *room_id; /* owned, non-null */
    char  *name;    /* owned, non-null */
    double area_m2;
} ca_home_room_t;

void ca_home_room_free(ca_home_room_t *r);
void ca_home_room_free_array(ca_home_room_t *arr, size_t count);

/* HomeDevice(DeviceId, Name, Kind, string? RoomId, bool IsOn). */
typedef struct {
    char *device_id; /* owned, non-null */
    char *name;      /* owned, non-null */
    char *kind;      /* owned, non-null */
    bool  has_room;  /* false == C# null RoomId */
    char *room_id;   /* owned, valid only when has_room */
    bool  is_on;
} ca_home_device_t;

void ca_home_device_free(ca_home_device_t *d);
void ca_home_device_free_array(ca_home_device_t *arr, size_t count);

/* MaintenanceTask(TaskId, Description, DateTime DueOn, bool Completed). */
typedef struct {
    char   *task_id;     /* owned, non-null */
    char   *description; /* owned, non-null */
    int64_t due_on_ms;   /* DateTime as Unix ms UTC */
    bool    completed;
} ca_home_task_t;

void ca_home_task_free(ca_home_task_t *t);
void ca_home_task_free_array(ca_home_task_t *arr, size_t count);

typedef struct ca_home_board ca_home_board_t;

ca_home_board_t *ca_home_board_create(void); /* NULL on OOM */
void ca_home_board_destroy(ca_home_board_t *b);

/* AddRoom(r) — RoomId keyed set. 0 / -1 on bad args/OOM. */
int ca_home_board_add_room(ca_home_board_t *b, const ca_home_room_t *r);

/* GetRoom(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_home_board_get_room(const ca_home_board_t *b, const char *id,
                            ca_home_room_t *out);

/* Rooms -> fresh owned array (*out_count) ordered by Name asc. NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_home_room_t *ca_home_board_rooms(const ca_home_board_t *b, size_t *out_count);

/* AddDevice(d) — DeviceId keyed set. 0 / -1. */
int ca_home_board_add_device(ca_home_board_t *b, const ca_home_device_t *d);

/* Toggle(deviceId, on) — sets IsOn. 0 on success, -1 on bad args, 1 when unknown
 * (InvalidOperationException). */
int ca_home_board_toggle(ca_home_board_t *b, const char *device_id, bool on);

/* DevicesIn(roomId) -> fresh owned array (*out_count): RoomId == roomId (ordinal)
 * in insertion order. NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_home_device_t *ca_home_board_devices_in(const ca_home_board_t *b,
                                           const char *room_id,
                                           size_t *out_count);

/* ActiveDevices -> fresh owned array (*out_count): IsOn in insertion order.
 * NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_home_device_t *ca_home_board_active_devices(const ca_home_board_t *b,
                                               size_t *out_count);

/* ScheduleTask(t) — TaskId keyed set. 0 / -1. */
int ca_home_board_schedule_task(ca_home_board_t *b, const ca_home_task_t *t);

/* CompleteTask(taskId) — sets Completed=true. 0 / -1 / 1 (unknown). */
int ca_home_board_complete_task(ca_home_board_t *b, const char *task_id);

/* UpcomingTasks(by_ms) -> fresh owned array (*out_count): !Completed && DueOn <=
 * by ordered by DueOn asc. NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_home_task_t *ca_home_board_upcoming_tasks(const ca_home_board_t *b,
                                             int64_t by_ms, size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_HOME_H */
