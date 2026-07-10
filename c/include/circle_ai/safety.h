#ifndef CIRCLE_AI_SAFETY_H
#define CIRCLE_AI_SAFETY_H

/*
 * safety.h — CircleAI.Safety domain primitives (C11 port).
 *
 * Ports CircleAI.Safety SafetyPrimitives.cs:
 *   - IncidentSeverity enum
 *   - Incident / Hazard / EmergencyContact records
 *   - ISafetyBoard + InMemorySafetyBoard
 *
 * The board logs incidents (most-recent-first views), notes hazards (keyed by
 * HazardId, last-write-wins), and holds an ordered emergency-contact list.
 * Severity routing exposes AtOrAboveSeverity(minimum).
 *
 * Conventions: ca_ prefix, _t types, opaque board handle, strdup'd owning
 * fields with matching *_free, deep-copy getters, arrays are fresh copies the
 * caller frees.
 *
 * Pure C11 + libc.
 */

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── IncidentSeverity ───────────────────────────────────────────────────── */

typedef enum {
    CA_INCIDENT_SEVERITY_INFO      = 0,
    CA_INCIDENT_SEVERITY_WARNING   = 1,
    CA_INCIDENT_SEVERITY_CRITICAL  = 2,
    CA_INCIDENT_SEVERITY_EMERGENCY = 3
} ca_incident_severity_t;

/* ── Records ────────────────────────────────────────────────────────────── */

typedef struct {
    char                  *incident_id;  /* owned */
    ca_incident_severity_t severity;
    char                  *description;  /* owned */
    bool                   has_latitude;
    double                 latitude;     /* valid iff has_latitude */
    bool                   has_longitude;
    double                 longitude;    /* valid iff has_longitude */
    int64_t                at_utc_ms;    /* Unix ms UTC */
} ca_incident_t;

typedef struct {
    char   *hazard_id;    /* owned */
    char   *description;  /* owned */
    char   *category;     /* owned */
    int64_t noted_utc_ms; /* Unix ms UTC */
} ca_hazard_t;

typedef struct {
    char *contact_id;    /* owned */
    char *name;          /* owned */
    char *phone;         /* owned */
    char *relationship;  /* owned */
} ca_emergency_contact_t;

void ca_incident_free(ca_incident_t *i);
void ca_incident_free_array(ca_incident_t *arr, size_t count);
ca_incident_t *ca_incident_copy(ca_incident_t *dst, const ca_incident_t *src);

void ca_hazard_free(ca_hazard_t *h);
void ca_hazard_free_array(ca_hazard_t *arr, size_t count);
ca_hazard_t *ca_hazard_copy(ca_hazard_t *dst, const ca_hazard_t *src);

void ca_emergency_contact_free(ca_emergency_contact_t *c);
void ca_emergency_contact_free_array(ca_emergency_contact_t *arr, size_t count);
ca_emergency_contact_t *ca_emergency_contact_copy(ca_emergency_contact_t *dst,
                                                  const ca_emergency_contact_t *src);

/* ── ISafetyBoard + InMemorySafetyBoard ─────────────────────────────────── */

typedef struct ca_safety_board ca_safety_board_t;

ca_safety_board_t *ca_safety_board_create(void);
void               ca_safety_board_destroy(ca_safety_board_t *board);

/* Log — appends a deep copy of the incident. Returns false on NULL board/incident. */
bool ca_safety_board_log(ca_safety_board_t *board, const ca_incident_t *i);

/* Active — all incidents ordered by AtUtc descending. Fresh array the caller
 * frees with ca_incident_free_array. *out_count receives the count (0 → NULL).
 * NULL board → *out_count SIZE_MAX + NULL. */
ca_incident_t *ca_safety_board_active(ca_safety_board_t *board, size_t *out_count);

/* AtOrAboveSeverity — incidents with Severity >= minimum, AtUtc descending.
 * Fresh array (caller frees). NULL board → *out_count SIZE_MAX + NULL. */
ca_incident_t *ca_safety_board_at_or_above_severity(ca_safety_board_t *board,
                                                    ca_incident_severity_t minimum,
                                                    size_t *out_count);

/* NoteHazard — inserts/replaces by HazardId (last-write-wins). Returns false on
 * NULL board/hazard. */
bool ca_safety_board_note_hazard(ca_safety_board_t *board, const ca_hazard_t *h);

/* Hazards — all hazards ordered by NotedUtc descending. Fresh array (caller
 * frees with ca_hazard_free_array). NULL board → *out_count SIZE_MAX + NULL. */
ca_hazard_t *ca_safety_board_hazards(ca_safety_board_t *board, size_t *out_count);

/* AddContact — appends a deep copy. Returns false on NULL board/contact. */
bool ca_safety_board_add_contact(ca_safety_board_t *board,
                                 const ca_emergency_contact_t *c);

/* FirstContact — deep copy of the first-added contact into *out (true), or
 * false when there are none / board is NULL. Caller frees *out with
 * ca_emergency_contact_free. */
bool ca_safety_board_first_contact(ca_safety_board_t *board,
                                   ca_emergency_contact_t *out);

/* Contacts — all contacts in insertion order. Fresh array (caller frees).
 * NULL board → *out_count SIZE_MAX + NULL. */
ca_emergency_contact_t *ca_safety_board_contacts(ca_safety_board_t *board,
                                                 size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SAFETY_H */
