/*
 * test_integration_home.c — CircleAI.Integration.HomeAssistant (C11 port)
 * verification of the in-memory HomeAssistant connector.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_int_ha_entity_t mk_entity(const char *id, const char *name,
                                    const char *domain, const char *state) {
    ca_int_ha_entity_t e; memset(&e, 0, sizeof(e));
    e.entity_id = (char *)id; e.friendly_name = (char *)name;
    e.domain = (char *)domain; e.state = (char *)state;
    return e;
}

static const char *state_of(ca_int_home_connector_t *c, const char *id) {
    /* helper: returns a freshly-listed entity's state via the vtable. */
    static char buf[64];
    size_t n = 0;
    ca_int_ha_entity_t *arr = c->list_entities(c->impl, &n);
    buf[0] = '\0';
    for (size_t i = 0; i < n; ++i)
        if (strcmp(arr[i].entity_id, id) == 0) {
            strncpy(buf, arr[i].state, sizeof(buf) - 1);
            buf[sizeof(buf) - 1] = '\0';
        }
    ca_int_ha_entity_free_array(arr, n);
    return buf;
}

static void test_config_and_id(void) {
    ca_int_home_connector_t *c = ca_int_home_assistant_create(true, "tok");
    ca_int_home_connector_t *c0 = ca_int_home_assistant_create(true, "  ");
    ca_int_home_connector_t *c1 = ca_int_home_assistant_create(false, "tok");
    assert(c && c0 && c1);
    assert(strcmp(c->provider_id(c->impl), "home-assistant") == 0);
    assert(c->is_configured(c->impl));
    assert(!c0->is_configured(c0->impl)); /* blank token */
    assert(!c1->is_configured(c1->impl)); /* no base url */
    ca_int_home_connector_destroy(c);
    ca_int_home_connector_destroy(c0);
    ca_int_home_connector_destroy(c1);
    printf("  config_and_id: ok\n");
}

static void test_entities_and_services(void) {
    ca_int_home_connector_t *c = ca_int_home_assistant_create(true, "tok");
    assert(c);

    ca_int_attr_pair_t attrs[1] = {{(char *)"friendly_name", (char *)"Kitchen"}};
    ca_int_ha_entity_t e1 = mk_entity("light.kitchen", "Kitchen", "light", "off");
    e1.attributes = attrs; e1.attributes_count = 1;
    ca_int_ha_entity_t e2 = mk_entity("switch.fan", "Fan", "switch", "off");
    assert(ca_int_home_seed_entity(c, &e1) == 0);
    assert(ca_int_home_seed_entity(c, &e2) == 0);

    size_t n = 0;
    ca_int_ha_entity_t *arr = c->list_entities(c->impl, &n);
    assert(n == 2);
    /* attribute survived the round-trip. */
    const char *fn = ca_int_ha_entity_attr(&arr[0], "friendly_name");
    assert(fn && strcmp(fn, "Kitchen") == 0);
    ca_int_ha_entity_free_array(arr, n);

    /* CallService homeassistant.turn_on(entity_id=light.kitchen) -> state "on". */
    ca_int_service_data_pair_t d = {(char *)"entity_id", (char *)"light.kitchen"};
    assert(c->call_service(c->impl, "homeassistant", "turn_on", &d, 1) == 0);
    assert(strcmp(state_of(c, "light.kitchen"), "on") == 0);
    assert(strcmp(state_of(c, "switch.fan"), "off") == 0); /* untouched */

    /* turn_off via convenience wrapper. */
    assert(ca_int_home_turn_off(c, "light.kitchen") == 0);
    assert(strcmp(state_of(c, "light.kitchen"), "off") == 0);
    /* turn_on via convenience wrapper. */
    assert(ca_int_home_turn_on(c, "switch.fan") == 0);
    assert(strcmp(state_of(c, "switch.fan"), "on") == 0);

    /* Non-turn service accepted (rc 0) but no state change. */
    assert(c->call_service(c->impl, "light", "toggle", &d, 1) == 0);
    assert(strcmp(state_of(c, "light.kitchen"), "off") == 0);

    /* domain/service required. */
    assert(c->call_service(c->impl, "  ", "turn_on", &d, 1) == -1);
    assert(c->call_service(c->impl, "homeassistant", "", &d, 1) == -1);

    /* NULL data accepted. */
    assert(c->call_service(c->impl, "homeassistant", "turn_on", NULL, 0) == 0);

    /* seed replace: same EntityId overwrites (state -> "unavailable"). */
    ca_int_ha_entity_t e1b = mk_entity("light.kitchen", "Kitchen", "light", "unavailable");
    assert(ca_int_home_seed_entity(c, &e1b) == 0);
    assert(strcmp(state_of(c, "light.kitchen"), "unavailable") == 0);
    arr = c->list_entities(c->impl, &n);
    assert(n == 2); /* replaced, not appended */
    ca_int_ha_entity_free_array(arr, n);

    ca_int_home_connector_destroy(c);
    printf("  entities_and_services: ok\n");
}

int main(void) {
    test_config_and_id();
    test_entities_and_services();
    printf("test_integration_home: all assertions passed\n");
    return 0;
}
