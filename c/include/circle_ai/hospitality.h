#ifndef CIRCLE_AI_HOSPITALITY_H
#define CIRCLE_AI_HOSPITALITY_H

/*
 * hospitality.h — CircleAI.Hospitality (C11 port of HospitalityPrimitives.cs).
 *
 *   Records : HotelRoom(RoomId, Type, decimal NightlyRate, Currency, bool IsClean);
 *             GuestReservation(ReservationId, GuestName, RoomId, DateTime CheckIn,
 *                       DateTime CheckOut);
 *             FrontDeskNote(NoteId, ReservationId, Body, DateTimeOffset AtUtc).
 *   Board   : IHospitalityBoard -> InMemoryHospitalityBoard
 *               AddRoom (RoomId keyed), GetRoom(id), AvailableOn(date) — clean
 *               rooms not covered by a reservation whose [CheckIn, CheckOut) spans
 *               date (insertion order), Reserve (ReservationId keyed),
 *               CheckOut(reservationId, roomNeedsCleaning) — when cleaning is
 *               needed flips the room's IsClean=false (unknown reservation
 *               throws), GetReservation(id), AddNote (appends),
 *               NotesFor(reservationId) newest-first by AtUtc.
 *
 * decimal NightlyRate via ca_decimal_t. DateTime/DateTimeOffset as Unix ms UTC.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields, deep
 * copy getters, errors via NULL / count SIZE_MAX. Linear arrays, no pthreads.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef int64_t ca_hospitality_decimal_t; /* micro-units (1e-6) */
#define CA_HOSPITALITY_DECIMAL_SCALE 1000000LL

/* HotelRoom(RoomId, Type, decimal NightlyRate, Currency, bool IsClean). */
typedef struct {
    char   *room_id;      /* owned, non-null */
    char   *type;         /* owned, non-null */
    ca_hospitality_decimal_t nightly_rate; /* micro-units */
    char   *currency;     /* owned, non-null */
    bool    is_clean;
} ca_hospitality_room_t;

void ca_hospitality_room_free(ca_hospitality_room_t *r);
void ca_hospitality_room_free_array(ca_hospitality_room_t *arr, size_t count);

/* GuestReservation(ReservationId, GuestName, RoomId, DateTime CheckIn,
 * DateTime CheckOut). */
typedef struct {
    char   *reservation_id; /* owned, non-null */
    char   *guest_name;     /* owned, non-null */
    char   *room_id;        /* owned, non-null */
    int64_t check_in_ms;
    int64_t check_out_ms;
} ca_hospitality_reservation_t;

void ca_hospitality_reservation_free(ca_hospitality_reservation_t *r);

/* FrontDeskNote(NoteId, ReservationId, Body, DateTimeOffset AtUtc). */
typedef struct {
    char   *note_id;        /* owned, non-null */
    char   *reservation_id; /* owned, non-null */
    char   *body;           /* owned, non-null */
    int64_t at_utc_ms;
} ca_hospitality_note_t;

void ca_hospitality_note_free(ca_hospitality_note_t *n);
void ca_hospitality_note_free_array(ca_hospitality_note_t *arr, size_t count);

typedef struct ca_hospitality_board ca_hospitality_board_t;

ca_hospitality_board_t *ca_hospitality_board_create(void); /* NULL on OOM */
void ca_hospitality_board_destroy(ca_hospitality_board_t *b);

/* AddRoom(r) — RoomId keyed set. 0 / -1. */
int ca_hospitality_board_add_room(ca_hospitality_board_t *b,
                                  const ca_hospitality_room_t *r);

/* GetRoom(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_hospitality_board_get_room(const ca_hospitality_board_t *b,
                                   const char *id, ca_hospitality_room_t *out);

/* AvailableOn(date_ms) -> fresh owned array (insertion order) of clean rooms with
 * no reservation covering date (CheckIn <= date < CheckOut). NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_hospitality_room_t *ca_hospitality_board_available_on(
    const ca_hospitality_board_t *b, int64_t date_ms, size_t *out_count);

/* Reserve(r) — ReservationId keyed set. 0 / -1. */
int ca_hospitality_board_reserve(ca_hospitality_board_t *b,
                                 const ca_hospitality_reservation_t *r);

/* CheckOut(reservationId, roomNeedsCleaning) — when cleaning is needed flips the
 * reservation's room IsClean=false. 0 on success, -1 on bad args, -2 when the
 * reservation is unknown (C# InvalidOperationException). */
int ca_hospitality_board_check_out(ca_hospitality_board_t *b,
                                   const char *reservation_id,
                                   bool room_needs_cleaning);

/* GetReservation(id) -> fresh owned copy into *out, true; false on miss. */
bool ca_hospitality_board_get_reservation(const ca_hospitality_board_t *b,
                                          const char *id,
                                          ca_hospitality_reservation_t *out);

/* AddNote(n) — appends. 0 / -1. */
int ca_hospitality_board_add_note(ca_hospitality_board_t *b,
                                  const ca_hospitality_note_t *n);

/* NotesFor(reservationId) -> fresh owned array newest-first by AtUtc.
 * NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_hospitality_note_t *ca_hospitality_board_notes_for(
    const ca_hospitality_board_t *b, const char *reservation_id,
    size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_HOSPITALITY_H */
