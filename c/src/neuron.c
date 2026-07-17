/*
 * neuron.c — the Neuron (CircleAI.Hosting.Neuron), C11.
 *
 * The concierge decision table + gate, the RAM admission gate for the specialist
 * slot, and the host-neutral NeuronNode facade + NullChatRuntime. Self-contained
 * over the C substrate (ca_chat_capability_t flags + fn-pointer seams).
 */

#include "circle_ai/neuron.h"

#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---------------------------------------------------------------------------
 * Small helpers
 * --------------------------------------------------------------------------- */

/* Case-insensitive equality (ASCII), NULL-safe. */
static int ci_equal(const char *a, const char *b) {
    if (!a || !b) return a == b;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return 0;
        a++;
        b++;
    }
    return *a == *b;
}

/* Case-insensitive substring test (ASCII), NULL-safe. */
static bool contains_ci(const char *hay, const char *needle) {
    if (!hay || !needle) return false;
    size_t nl = strlen(needle);
    if (nl == 0) return true;
    for (const char *p = hay; *p; ++p) {
        size_t i = 0;
        while (i < nl && p[i] &&
               tolower((unsigned char)p[i]) == tolower((unsigned char)needle[i])) {
            i++;
        }
        if (i == nl) return true;
    }
    return false;
}

/* ---------------------------------------------------------------------------
 * Concierge router + gate
 * --------------------------------------------------------------------------- */

static const char *const REASONING_CUES[] = {
    "debug", "stack trace", "solve", "prove", "reason", "analy", "calculate",
    "equation", "step by step", "algorithm", "why does", "derive", "diagnose",
};
static const size_t REASONING_CUE_COUNT =
    sizeof REASONING_CUES / sizeof REASONING_CUES[0];

void ca_neuron_router_init(ca_neuron_router_t *router) {
    if (!router) return;
    router->long_context_chars = 4000;
    router->gate = NULL;
    router->gate_user = NULL;
}

static ca_route_decision_t classify(const ca_neuron_router_t *router,
                                    const ca_route_context_t *ctx) {
    ca_route_decision_t d;

    if (ctx->has_image) {
        d.organ = CA_ORGAN_SPECIALIST;
        d.capability = CA_CHAT_CAP_VISION;
        d.reason = "image present";
        return d;
    }

    int32_t chars = ctx->prompt_chars;
    if (chars < 0) chars = ctx->query ? (int32_t)strlen(ctx->query) : 0;
    int32_t lcc = router->long_context_chars > 0 ? router->long_context_chars : 4000;
    if (chars >= lcc) {
        d.organ = CA_ORGAN_SPECIALIST;
        d.capability = CA_CHAT_CAP_LONG_CTX;
        d.reason = "long prompt";
        return d;
    }

    for (size_t i = 0; i < REASONING_CUE_COUNT; ++i) {
        if (contains_ci(ctx->query, REASONING_CUES[i])) {
            d.organ = CA_ORGAN_SPECIALIST;
            d.capability = CA_CHAT_CAP_REASONING;
            d.reason = "reasoning cue";
            return d;
        }
    }

    d.organ = CA_ORGAN_GENERALIST;
    d.capability = CA_CHAT_CAP_DEFAULT;
    d.reason = "default";
    return d;
}

ca_route_decision_t ca_neuron_route(const ca_neuron_router_t *router,
                                    const ca_route_context_t *ctx) {
    ca_route_decision_t d = classify(router, ctx);
    if (d.organ == CA_ORGAN_SPECIALIST && router->gate) {
        if (!router->gate(&d, router->gate_user)) {
            d.organ = CA_ORGAN_GENERALIST;
            d.capability = CA_CHAT_CAP_DEFAULT;
            d.reason = "gate-vetoed";
        }
    }
    return d;
}

/* ---------------------------------------------------------------------------
 * ResidentSlotManager
 * --------------------------------------------------------------------------- */

void ca_slot_manager_init(ca_resident_slot_manager_t *m,
                          int64_t generalist_reserved_bytes,
                          ca_ram_available_fn ram_available, void *ram_user) {
    if (!m) return;
    memset(m, 0, sizeof *m);
    m->generalist_reserved_bytes = generalist_reserved_bytes;
    m->ram_available = ram_available;
    m->ram_user = ram_user;
}

void ca_slot_manager_set_free(ca_resident_slot_manager_t *m,
                              ca_specialist_free_fn free_fn, void *free_user) {
    if (!m) return;
    m->free_fn = free_fn;
    m->free_user = free_user;
}

static void slot_free_current(ca_resident_slot_manager_t *m) {
    if (m->has_specialist && m->free_fn) m->free_fn(m->specialist, m->free_user);
    m->specialist = NULL;
    m->has_specialist = false;
    m->specialist_model_id[0] = '\0';
}

void ca_slot_evict(ca_resident_slot_manager_t *m) {
    if (!m) return;
    slot_free_current(m);
}

ca_slot_outcome_t ca_slot_ensure_specialist(ca_resident_slot_manager_t *m,
                                             const char *model_id,
                                             int64_t estimated_bytes,
                                             ca_specialist_build_fn build,
                                             void *build_user,
                                             void **out_specialist) {
    if (out_specialist) *out_specialist = NULL;
    if (!m) return CA_SLOT_BUILD_FAILED;

    if (m->has_specialist && ci_equal(m->specialist_model_id, model_id)) {
        if (out_specialist) *out_specialist = m->specialist;
        return CA_SLOT_ALREADY_RESIDENT;
    }

    /* RAM admission gate: reserve the floor, then check the specialist fits. */
    int64_t est = estimated_bytes > 0 ? estimated_bytes : 0;
    int64_t ceiling = m->ram_available ? m->ram_available(m->ram_user) : 0;
    if (m->generalist_reserved_bytes + est > ceiling) {
        return CA_SLOT_INSUFFICIENT_RAM;
    }

    /* Evict the incumbent (one specialist at a time) before building the new. */
    slot_free_current(m);

    void *built = build ? build(model_id, build_user) : NULL;
    if (!built) return CA_SLOT_BUILD_FAILED;

    m->specialist = built;
    m->has_specialist = true;
    snprintf(m->specialist_model_id, sizeof m->specialist_model_id, "%s",
             model_id ? model_id : "");
    if (out_specialist) *out_specialist = built;
    return CA_SLOT_ADMITTED;
}

const char *ca_slot_resident_model_id(const ca_resident_slot_manager_t *m) {
    if (!m || !m->has_specialist) return NULL;
    return m->specialist_model_id;
}

void *ca_slot_resident_specialist(const ca_resident_slot_manager_t *m) {
    if (!m || !m->has_specialist) return NULL;
    return m->specialist;
}

/* ---------------------------------------------------------------------------
 * NeuronNode facade
 * --------------------------------------------------------------------------- */

void ca_neuron_node_init(ca_neuron_node_t *node, ca_neuron_brain_t brain,
                         const char *snapshot_path) {
    if (!node) return;
    node->brain = brain;
    node->engine_label[0] = '\0';

    if (snapshot_path && snapshot_path[0]) {
        snprintf(node->snapshot_path, sizeof node->snapshot_path, "%s", snapshot_path);
    } else {
        const char *tmp = getenv("TMPDIR");
        if (!tmp || !tmp[0]) tmp = getenv("TEMP");
        if (!tmp || !tmp[0]) tmp = "/tmp";
        snprintf(node->snapshot_path, sizeof node->snapshot_path,
                 "%s/circleai-neuron-session.bin", tmp);
    }
}

const char *ca_neuron_node_id(const ca_neuron_node_t *node) {
    (void)node;
    return "circleai-neuron";
}

const char *ca_neuron_node_engine_label(ca_neuron_node_t *node) {
    if (!node) return "circleai-neuron";
    const char *mid = NULL;
    if (node->brain.resolved_model_id) {
        mid = node->brain.resolved_model_id(node->brain.impl);
    }
    if (mid && mid[0]) {
        snprintf(node->engine_label, sizeof node->engine_label,
                 "circleai-neuron:%s", mid);
    } else {
        snprintf(node->engine_label, sizeof node->engine_label, "circleai-neuron");
    }
    return node->engine_label;
}

bool ca_neuron_node_is_ready(const ca_neuron_node_t *node) {
    if (!node || !node->brain.is_ready) return false;
    return node->brain.is_ready(node->brain.impl);
}

const char *ca_neuron_node_status(const ca_neuron_node_t *node) {
    return ca_neuron_node_is_ready(node) ? "ready" : "loading model\xe2\x80\xa6";
}

const char *ca_neuron_node_snapshot_path(const ca_neuron_node_t *node) {
    if (!node || !node->snapshot_path[0]) return NULL;
    return node->snapshot_path;
}

void ca_neuron_node_stream(ca_neuron_node_t *node, const ca_chat_turn_t *turns,
                           size_t n, ca_chunk_callback on_chunk, void *userdata) {
    if (!node || !node->brain.stream) return;
    node->brain.stream(node->brain.impl, turns, n, on_chunk, userdata);
}

bool ca_neuron_node_save_session(ca_neuron_node_t *node, const char *path) {
    if (!node || !node->brain.save_session) return false;
    return node->brain.save_session(node->brain.impl, path);
}

bool ca_neuron_node_load_session(ca_neuron_node_t *node, const char *path) {
    if (!node || !node->brain.load_session) return false;
    return node->brain.load_session(node->brain.impl, path);
}

/* ---------------------------------------------------------------------------
 * NullChatRuntime
 * --------------------------------------------------------------------------- */

bool ca_null_chat_runtime_is_ready(void) {
    return false;
}

const char *ca_null_chat_runtime_status(void) {
    return "No chat engine configured.";
}

void ca_null_chat_runtime_stream(const ca_chat_turn_t *turns, size_t n,
                                 ca_chunk_callback on_chunk, void *userdata) {
    (void)turns;
    (void)n;
    if (on_chunk) on_chunk("No chat engine is configured.", userdata);
}
