#ifndef CIRCLE_AI_CIVIC_H
#define CIRCLE_AI_CIVIC_H

/*
 * civic.h — CircleAI.Civic (C11 port of CivicPrimitives.cs).
 *
 *   Records : CivicIssue(IssueId, Category, Description, double Lat, double Lon,
 *                        DateTimeOffset ReportedUtc, Status);
 *             Representative(RepId, Name, Office, ContactEmail, string? District);
 *             CivicEvent(EventId, Title, DateTimeOffset AtUtc, Location, Audience).
 *   Board   : ICivicBoard -> InMemoryCivicBoard
 *               Report (IssueId keyed), Resolve(issueId, status) — sets Status
 *               (unknown issue throws), OpenIssues() — Status != "Resolved"
 *               (OrdinalIgnoreCase; insertion order), AddRep (RepId keyed),
 *               RepsForDistrict(district) (OrdinalIgnoreCase; insertion order),
 *               Schedule (EventId keyed), UpcomingEvents(now) — AtUtc >= now,
 *               ordered by AtUtc asc.
 *
 * The C# UpcomingEvents reads DateTimeOffset.UtcNow; the port takes an explicit
 * now_ms. DateTimeOffset as Unix ms UTC. District optional via has_district.
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

/* CivicIssue(IssueId, Category, Description, double Lat, double Lon,
 * DateTimeOffset ReportedUtc, Status). */
typedef struct {
    char   *issue_id;    /* owned, non-null */
    char   *category;    /* owned, non-null */
    char   *description; /* owned, non-null */
    double  lat, lon;
    int64_t reported_utc_ms;
    char   *status;      /* owned, non-null */
} ca_civic_issue_t;

void ca_civic_issue_free(ca_civic_issue_t *i);
void ca_civic_issue_free_array(ca_civic_issue_t *arr, size_t count);

/* Representative(RepId, Name, Office, ContactEmail, string? District). */
typedef struct {
    char   *rep_id;        /* owned, non-null */
    char   *name;          /* owned, non-null */
    char   *office;        /* owned, non-null */
    char   *contact_email; /* owned, non-null */
    bool    has_district;  /* false == C# null District */
    char   *district;      /* owned, valid only when has_district */
} ca_civic_rep_t;

void ca_civic_rep_free(ca_civic_rep_t *r);
void ca_civic_rep_free_array(ca_civic_rep_t *arr, size_t count);

/* CivicEvent(EventId, Title, DateTimeOffset AtUtc, Location, Audience). */
typedef struct {
    char   *event_id;    /* owned, non-null */
    char   *title;       /* owned, non-null */
    int64_t at_utc_ms;
    char   *location;    /* owned, non-null */
    char   *audience;    /* owned, non-null */
} ca_civic_event_t;

void ca_civic_event_free(ca_civic_event_t *e);
void ca_civic_event_free_array(ca_civic_event_t *arr, size_t count);

typedef struct ca_civic_board ca_civic_board_t;

ca_civic_board_t *ca_civic_board_create(void); /* NULL on OOM */
void ca_civic_board_destroy(ca_civic_board_t *b);

/* Report(i) — IssueId keyed set. 0 / -1. */
int ca_civic_board_report(ca_civic_board_t *b, const ca_civic_issue_t *i);

/* Resolve(issueId, status) — sets Status. 0 on success, -1 on bad args, -2 when
 * the issue is unknown (C# InvalidOperationException). */
int ca_civic_board_resolve(ca_civic_board_t *b, const char *issue_id,
                           const char *status);

/* OpenIssues() -> fresh owned array (insertion order) with Status != "Resolved"
 * (OrdinalIgnoreCase). NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_civic_issue_t *ca_civic_board_open_issues(const ca_civic_board_t *b,
                                             size_t *out_count);

/* AddRep(r) — RepId keyed set. 0 / -1. */
int ca_civic_board_add_rep(ca_civic_board_t *b, const ca_civic_rep_t *r);

/* RepsForDistrict(district) -> fresh owned array (insertion order) with District
 * matching (OrdinalIgnoreCase). NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_civic_rep_t *ca_civic_board_reps_for_district(const ca_civic_board_t *b,
                                                 const char *district,
                                                 size_t *out_count);

/* Schedule(e) — EventId keyed set. 0 / -1. */
int ca_civic_board_schedule(ca_civic_board_t *b, const ca_civic_event_t *e);

/* UpcomingEvents(now_ms) -> fresh owned array (AtUtc >= now) ordered by AtUtc asc.
 * NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_civic_event_t *ca_civic_board_upcoming_events(const ca_civic_board_t *b,
                                                 int64_t now_ms,
                                                 size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CIVIC_H */
