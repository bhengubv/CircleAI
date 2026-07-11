#ifndef CIRCLE_AI_INTEGRATION_HOME_H
#define CIRCLE_AI_INTEGRATION_HOME_H

/*
 * integration_home.h — CircleAI.Integration.HomeAssistant (C11 port).
 *
 * Deterministic in-memory IHomeAutomationConnector standing in for
 * HomeAssistantConnector. The real connector hits the HA REST API
 * (GET api/states, POST api/services/{domain}/{service}) with a long-lived token;
 * here the entity registry is in memory (seeded via ca_int_home_seed_entity — the
 * injected network state) and the contract matches:
 *
 *   ProviderId "home-assistant".
 *   IsConfigured := base_url non-null && access_token non-blank.
 *   ListEntities()               : all registered HaEntity, insertion order.
 *   CallService(domain,service,data) : accepts the call. When domain=="homeassistant"
 *     and service=="turn_on"/"turn_off", the entity named by data["entity_id"]
 *     has its State set to "on"/"off" (mirrors the HA turn_on/turn_off services
 *     the C# TurnOnAsync/TurnOffAsync convenience helpers drive). domain/service
 *     NULL/whitespace -> ArgumentException (rc -1). data may be NULL/empty.
 *   TurnOn/TurnOff(entityId)     : the C# convenience wrappers over CallService.
 *
 * Conventions per integration.h. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>

#include "integration.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Create the in-memory HomeAssistant connector (ProviderId "home-assistant").
 * has_base_url mirrors "BaseUrl is not null"; IsConfigured := has_base_url &&
 * access_token non-blank. access_token may be NULL. NULL on OOM. */
ca_int_home_connector_t *ca_int_home_assistant_create(bool has_base_url,
                                                      const char *access_token);

/* Seed an entity into the registry (deep-copied). If an entity with the same
 * EntityId already exists it is replaced. 0 success; -1 bad args/OOM. */
int ca_int_home_seed_entity(ca_int_home_connector_t *c,
                            const ca_int_ha_entity_t *entity);

/* homeassistant.turn_on(entity_id) convenience (C# TurnOnAsync). 0/-1. */
int ca_int_home_turn_on(ca_int_home_connector_t *c, const char *entity_id);
/* homeassistant.turn_off(entity_id) convenience (C# TurnOffAsync). 0/-1. */
int ca_int_home_turn_off(ca_int_home_connector_t *c, const char *entity_id);

/* Destroy the connector (frees registry + vtable). */
void ca_int_home_connector_destroy(ca_int_home_connector_t *c);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_INTEGRATION_HOME_H */
