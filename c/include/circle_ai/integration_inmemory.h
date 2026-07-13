#ifndef CIRCLE_AI_INTEGRATION_INMEMORY_H
#define CIRCLE_AI_INTEGRATION_INMEMORY_H

/*
 * integration_inmemory.h — CircleAI.Integration InMemoryIntegrationConnectors.cs
 * (C11 port).
 *
 * The canonical dependency-free in-memory reference implementations of the six
 * integration connector contracts (distinct from the provider-specific in-memory
 * doubles in integration_calendar/_email/_geo/_home/_news, which carry real
 * provider ids). Every one of these carries ProviderId/SourceId "in-memory" and
 * deterministic behaviour with no network:
 *
 *   InMemoryCalendarConnector       — events held in a map; ListEvents returns
 *     those overlapping [from,to) (Start < to && End > from), ordered by StartUtc;
 *     CreateEvent stores by EventId; DeleteEvent removes by eventId.
 *   InMemoryEmailConnector          — seeded messages; ListUnread + Search are
 *     newest-first (Take(Max(0,max))); Search matches Subject/BodyText
 *     (OrdinalIgnoreCase, null query => ""); MarkRead flips Unread.
 *   InMemoryNewsSource              — seeded items, newest-first (Take(Max(0,max))).
 *   InMemoryWeatherProvider         — deterministic pseudo-weather from lat/lon/
 *     hour: TempC = Round(15 + 10*cos((lat+hour)*PI/12), 2) (banker's rounding),
 *     FeelsLike = Round(TempC-1.5, 2), Precip 0, Wind 12, Cloud 40, "Clear";
 *     AtUtc = UnixEpoch + hour hours. Hourly(hours) yields Max(0,hours) samples.
 *   InMemoryRoutingProvider         — Haversine (km, r=6371) + a mode speed
 *     (walk 5, bike 18, transit 30, else 60 kph); DistanceKm = Round(km,3),
 *     Duration = FromHours(km/kph), 2-point polyline.
 *   InMemoryHomeAutomationConnector — seeded entities ordered by EntityId;
 *     CallService turns matching-domain entities on/off/toggle.
 *
 * The vtable + record types are shared from integration.h. These creators return
 * one of those vtable structs; free each with its matching *_destroy below (do
 * NOT use the provider-specific ca_int_*_connector_destroy — the impl layout
 * differs). Seed helpers append records to the stateful connectors.
 *
 * Numeric formulas mirror the C# byte-for-byte (banker's rounding via round-half-
 * to-even; the build uses -ffp-contract=off). Pure C11 + libc + libm. No pthreads.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "integration.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ── InMemoryCalendarConnector (ProviderId "in-memory") ─────────────────────
 * Events are created via the vtable's create_event; ListEvents/DeleteEvent as in
 * integration.h. NULL on OOM. */
ca_int_calendar_connector_t *ca_int_inmemory_calendar_create(void);
void ca_int_inmemory_calendar_destroy(ca_int_calendar_connector_t *c);

/* ── InMemoryEmailConnector (ProviderId "in-memory") ────────────────────────
 * NULL on OOM. Seed messages with ca_int_inmemory_email_seed (keyed by MessageId,
 * last-write-wins). */
ca_int_email_connector_t *ca_int_inmemory_email_create(void);
void ca_int_inmemory_email_destroy(ca_int_email_connector_t *c);
/* Seed/replace one message (deep-copied) by MessageId. 0 / -1 on bad args/OOM. */
int ca_int_inmemory_email_seed(ca_int_email_connector_t *c,
                               const ca_int_email_message_t *m);

/* ── InMemoryNewsSource (SourceId "in-memory") ──────────────────────────────
 * NULL on OOM. Seed items with ca_int_inmemory_news_seed (keyed by ItemId, LWW). */
ca_int_news_source_t *ca_int_inmemory_news_create(void);
void ca_int_inmemory_news_destroy(ca_int_news_source_t *s);
/* Seed/replace one item (deep-copied) by ItemId. 0 / -1 on bad args/OOM. */
int ca_int_inmemory_news_seed(ca_int_news_source_t *s,
                              const ca_int_news_item_t *item);

/* ── InMemoryWeatherProvider (ProviderId "in-memory") ───────────────────────
 * Stateless. NULL on OOM. Note Hourly does NOT throw on hours<=0 — it yields an
 * empty array (Max(0,hours)); the current()/hourly() vtable ops reflect that. */
ca_int_weather_provider_t *ca_int_inmemory_weather_create(void);
void ca_int_inmemory_weather_destroy(ca_int_weather_provider_t *p);

/* ── InMemoryRoutingProvider (ProviderId "in-memory") ───────────────────────
 * Stateless. NULL on OOM. mode NULL treated as "car" (default 60 kph). */
ca_int_routing_provider_t *ca_int_inmemory_routing_create(void);
void ca_int_inmemory_routing_destroy(ca_int_routing_provider_t *p);

/* ── InMemoryHomeAutomationConnector (ProviderId "in-memory") ───────────────
 * NULL on OOM. Seed entities with ca_int_inmemory_home_seed (keyed by EntityId,
 * LWW). ListEntities returns them ordered by EntityId; CallService turns
 * matching-domain (OrdinalIgnoreCase) entities on/off/toggle. */
ca_int_home_connector_t *ca_int_inmemory_home_create(void);
void ca_int_inmemory_home_destroy(ca_int_home_connector_t *c);
/* Seed/replace one entity (deep-copied) by EntityId. 0 / -1 on bad args/OOM. */
int ca_int_inmemory_home_seed(ca_int_home_connector_t *c,
                              const ca_int_ha_entity_t *entity);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_INTEGRATION_INMEMORY_H */
