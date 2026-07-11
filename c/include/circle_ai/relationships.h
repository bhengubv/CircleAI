#ifndef CIRCLE_AI_RELATIONSHIPS_H
#define CIRCLE_AI_RELATIONSHIPS_H

/*
 * relationships.h — CircleAI.Relationships (C11 port of RelationshipsPrimitives.cs).
 *
 *   Records : PersonContact(ContactId, Name, Relationship, string? Notes);
 *             ImportantDate(DateId, ContactId, Kind, DateTime Date);
 *             ContactEvent(ContactId, Kind, DateTimeOffset AtUtc, string? Note).
 *   Board   : IRelationshipsBoard -> InMemoryRelationshipsBoard
 *               AddContact (ContactId keyed), GetContact(id), Contacts ordered by
 *               Name asc, AddImportantDate (DateId keyed), UpcomingThisMonth(now)
 *               — dates whose Date.Month == now.Month, ordered by Date.Day,
 *               RecordTouchpoint (appends), LastContact(contactId) — newest AtUtc
 *               of that contact's events (nullable), NotContactedSince(cutoff) —
 *               contacts whose LastContact is null or < cutoff (insertion order).
 *
 * The C# UpcomingThisMonth reads DateTime.UtcNow; the port takes an explicit
 * now_ms. DateTimeOffset/DateTime as Unix ms UTC. Notes/Note optional via has_*.
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

/* PersonContact(ContactId, Name, Relationship, string? Notes). */
typedef struct {
    char   *contact_id;   /* owned, non-null */
    char   *name;         /* owned, non-null */
    char   *relationship; /* owned, non-null */
    bool    has_notes;    /* false == C# null Notes */
    char   *notes;        /* owned, valid only when has_notes */
} ca_relationships_contact_t;

void ca_relationships_contact_free(ca_relationships_contact_t *c);
void ca_relationships_contact_free_array(ca_relationships_contact_t *arr,
                                         size_t count);

/* ImportantDate(DateId, ContactId, Kind, DateTime Date). */
typedef struct {
    char   *date_id;    /* owned, non-null */
    char   *contact_id; /* owned, non-null */
    char   *kind;       /* owned, non-null */
    int64_t date_ms;
} ca_relationships_important_date_t;

void ca_relationships_important_date_free(ca_relationships_important_date_t *d);
void ca_relationships_important_date_free_array(
    ca_relationships_important_date_t *arr, size_t count);

/* ContactEvent(ContactId, Kind, DateTimeOffset AtUtc, string? Note). */
typedef struct {
    char   *contact_id; /* owned, non-null */
    char   *kind;       /* owned, non-null */
    int64_t at_utc_ms;
    bool    has_note;   /* false == C# null Note */
    char   *note;       /* owned, valid only when has_note */
} ca_relationships_event_t;

typedef struct ca_relationships_board ca_relationships_board_t;

ca_relationships_board_t *ca_relationships_board_create(void); /* NULL on OOM */
void ca_relationships_board_destroy(ca_relationships_board_t *b);

/* AddContact(c) — ContactId keyed set. 0 / -1. */
int ca_relationships_board_add_contact(ca_relationships_board_t *b,
                                       const ca_relationships_contact_t *c);

/* GetContact(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_relationships_board_get_contact(const ca_relationships_board_t *b,
                                        const char *id,
                                        ca_relationships_contact_t *out);

/* Contacts -> fresh owned array ordered by Name asc. NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_relationships_contact_t *ca_relationships_board_contacts(
    const ca_relationships_board_t *b, size_t *out_count);

/* AddImportantDate(d) — DateId keyed set. 0 / -1. */
int ca_relationships_board_add_important_date(
    ca_relationships_board_t *b, const ca_relationships_important_date_t *d);

/* UpcomingThisMonth(now_ms) -> fresh owned array of dates whose Date.Month ==
 * now's month, ordered by Date.Day asc. NULL + 0 empty; NULL + SIZE_MAX error. */
ca_relationships_important_date_t *ca_relationships_board_upcoming_this_month(
    const ca_relationships_board_t *b, int64_t now_ms, size_t *out_count);

/* RecordTouchpoint(e) — appends. 0 / -1. */
int ca_relationships_board_record_touchpoint(
    ca_relationships_board_t *b, const ca_relationships_event_t *e);

/* LastContact(contactId) -> writes the newest AtUtc into *out_ms and returns true;
 * false (the C# null) when the contact has no events / bad args. */
bool ca_relationships_board_last_contact(const ca_relationships_board_t *b,
                                         const char *contact_id, int64_t *out_ms);

/* NotContactedSince(cutoff_ms) -> fresh owned array (insertion order) of contacts
 * whose LastContact is null or < cutoff. NULL + 0 empty; NULL + SIZE_MAX error. */
ca_relationships_contact_t *ca_relationships_board_not_contacted_since(
    const ca_relationships_board_t *b, int64_t cutoff_ms, size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_RELATIONSHIPS_H */
