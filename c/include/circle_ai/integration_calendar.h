#ifndef CIRCLE_AI_INTEGRATION_CALENDAR_H
#define CIRCLE_AI_INTEGRATION_CALENDAR_H

/*
 * integration_calendar.h — CircleAI.Integration.Calendar (C11 port).
 *
 * Deterministic in-memory ICalendarConnector implementations standing in for the
 * three HTTP connectors (CalDavCalendarConnector, GoogleCalendarConnector,
 * MsGraphCalendarConnector). The real connectors talk to CalDAV / Google
 * Calendar v3 / Microsoft Graph over an injected HttpClient; here the network is
 * the injected dependency and the store lives in memory, but the observable
 * contract — ProviderId, IsConfigured, time-range ListEvents, UID-assigning
 * CreateEvent, idempotent DeleteEvent — matches the C# spec byte-for-byte.
 *
 *   Provider ids : "caldav", "google-calendar", "ms-graph-calendar".
 *   IsConfigured : CalDav  := username && password both non-blank
 *                             (CalDavCalendarConnector.IsConfigured);
 *                  Google  := AccessTokenProvider is not null;
 *                  MsGraph := AccessTokenProvider is not null.
 *   ListEvents(fromUtc, toUtc) : events overlapping [fromUtc, toUtc)
 *                  (StartUtc < toUtc && EndUtc > fromUtc), ordered by StartUtc
 *                  ascending (singleEvents=true&orderBy=startTime).
 *   CreateEvent(ev) : EventId blank -> a fresh 32-hex UID (Guid("N")); the event
 *                  is stored under its CalendarId and the stored copy returned.
 *   DeleteEvent(calendarId, eventId) : removes the matching event; unknown ids
 *                  are swallowed (C# tolerates 404/410/NoContent). eventId
 *                  NULL/whitespace -> ArgumentException (rc -1).
 *
 * Conventions per integration.h. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>

#include "integration.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Create an in-memory CalDAV connector (ProviderId "caldav").
 * IsConfigured := username && password both non-blank. username/password may be
 * NULL (fail-soft: IsConfigured=false). Returns a heap-owned vtable (destroy with
 * ca_int_calendar_connector_destroy) or NULL on OOM. */
ca_int_calendar_connector_t *ca_int_caldav_calendar_create(const char *username,
                                                           const char *password);

/* Create an in-memory Google Calendar connector (ProviderId "google-calendar").
 * has_token_provider mirrors "AccessTokenProvider is not null". calendar_id
 * defaults to "primary" when NULL. NULL on OOM. */
ca_int_calendar_connector_t *ca_int_google_calendar_create(bool has_token_provider,
                                                           const char *calendar_id);

/* Create an in-memory Microsoft Graph calendar connector
 * (ProviderId "ms-graph-calendar"). Same shape as Google. NULL on OOM. */
ca_int_calendar_connector_t *ca_int_msgraph_calendar_create(bool has_token_provider,
                                                            const char *calendar_id);

/* Destroy any calendar connector returned above (frees impl + vtable). */
void ca_int_calendar_connector_destroy(ca_int_calendar_connector_t *c);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_INTEGRATION_CALENDAR_H */
