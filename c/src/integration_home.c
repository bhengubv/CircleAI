/*
 * integration_home.c — CircleAI.Integration.HomeAssistant (C11 port).
 *
 * In-memory IHomeAutomationConnector backend for HomeAssistant. The real
 * connector hits the HA REST API; here the entity registry is a linear array
 * seeded via ca_int_home_seed_entity (the injected network state). CallService
 * accepts the call and, for the homeassistant.turn_on/turn_off services, mutates
 * the target entity's State. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/integration_home.h"
#include "board_common.h"

typedef struct {
    bool                configured;
    ca_int_ha_entity_t *items;
    size_t              count, cap;
} home_impl_t;

static const char *home_provider_id(void *impl) {
    (void)impl;
    return "home-assistant";
}

static bool home_is_configured(void *impl) {
    return ((home_impl_t *)impl)->configured;
}

static ca_int_ha_entity_t *home_list_entities(void *impl, size_t *out_count) {
    if (!out_count) return NULL;
    home_impl_t *m = (home_impl_t *)impl;
    if (m->count == 0) { *out_count = 0; return NULL; }
    ca_int_ha_entity_t *out = (ca_int_ha_entity_t *)calloc(m->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < m->count; ++i) {
        if (!ca_int_ha_entity_copy(&out[i], &m->items[i])) {
            ca_int_ha_entity_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = m->count;
    return out;
}

/* Set the State of the entity with EntityId==id to `state` (in place). */
static void set_state(home_impl_t *m, const char *id, const char *state) {
    if (cab_is_ws(id)) return;
    for (size_t i = 0; i < m->count; ++i) {
        if (cab_ord_eq(m->items[i].entity_id, id)) {
            char *ns = cab_strdup_empty(state);
            if (!ns) return; /* leave prior state on OOM */
            free(m->items[i].state);
            m->items[i].state = ns;
            return;
        }
    }
}

static int home_call_service(void *impl, const char *domain, const char *service,
                             const ca_int_service_data_pair_t *data,
                             size_t data_count) {
    home_impl_t *m = (home_impl_t *)impl;
    if (!m) return -1;
    if (cab_is_ws(domain) || cab_is_ws(service)) return -1; /* ArgumentException */

    if (cab_ord_eq(domain, "homeassistant") &&
        (cab_ord_eq(service, "turn_on") || cab_ord_eq(service, "turn_off"))) {
        const char *entity_id = NULL;
        for (size_t i = 0; i < data_count; ++i)
            if (data && cab_ord_eq(data[i].key, "entity_id")) {
                entity_id = data[i].value;
                break;
            }
        if (entity_id)
            set_state(m, entity_id, cab_ord_eq(service, "turn_on") ? "on" : "off");
    }
    return 0; /* HA POST returns 2xx regardless */
}

/* ── convenience wrappers (C# TurnOnAsync/TurnOffAsync) ──────────────────── */

static int turn(ca_int_home_connector_t *c, const char *entity_id, bool on) {
    if (!c) return -1;
    ca_int_service_data_pair_t d;
    d.key   = (char *)"entity_id";
    d.value = (char *)entity_id;
    return home_call_service(c->impl, "homeassistant",
                             on ? "turn_on" : "turn_off", &d, 1);
}

int ca_int_home_turn_on(ca_int_home_connector_t *c, const char *entity_id) {
    return turn(c, entity_id, true);
}
int ca_int_home_turn_off(ca_int_home_connector_t *c, const char *entity_id) {
    return turn(c, entity_id, false);
}

/* ── construction / seeding ─────────────────────────────────────────────── */

int ca_int_home_seed_entity(ca_int_home_connector_t *c,
                            const ca_int_ha_entity_t *entity) {
    if (!c || !entity) return -1;
    home_impl_t *m = (home_impl_t *)c->impl;
    if (cab_is_ws(entity->entity_id)) return -1;

    ca_int_ha_entity_t copy;
    if (!ca_int_ha_entity_copy(&copy, entity)) return -1;

    for (size_t i = 0; i < m->count; ++i) {
        if (cab_ord_eq(m->items[i].entity_id, entity->entity_id)) {
            ca_int_ha_entity_free(&m->items[i]);
            m->items[i] = copy;
            return 0;
        }
    }
    if (m->count == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 4;
        void *nb = realloc(m->items, nc * sizeof(*m->items));
        if (!nb) { ca_int_ha_entity_free(&copy); return -1; }
        m->items = (ca_int_ha_entity_t *)nb;
        m->cap = nc;
    }
    m->items[m->count++] = copy;
    return 0;
}

ca_int_home_connector_t *ca_int_home_assistant_create(bool has_base_url,
                                                      const char *access_token) {
    home_impl_t *m = (home_impl_t *)calloc(1, sizeof(home_impl_t));
    if (!m) return NULL;
    m->configured = has_base_url && !cab_is_ws(access_token);

    ca_int_home_connector_t *c =
        (ca_int_home_connector_t *)calloc(1, sizeof(*c));
    if (!c) { free(m); return NULL; }
    c->impl          = m;
    c->provider_id   = home_provider_id;
    c->is_configured = home_is_configured;
    c->list_entities = home_list_entities;
    c->call_service  = home_call_service;
    return c;
}

void ca_int_home_connector_destroy(ca_int_home_connector_t *c) {
    if (!c) return;
    home_impl_t *m = (home_impl_t *)c->impl;
    if (m) {
        for (size_t i = 0; i < m->count; ++i) ca_int_ha_entity_free(&m->items[i]);
        free(m->items);
        free(m);
    }
    free(c);
}
