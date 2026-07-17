#ifndef CIRCLE_AI_NEURON_H
#define CIRCLE_AI_NEURON_H

/*
 * neuron.h — the Neuron: the concierge (per-turn router + gate) that steers an
 * always-warm generalist and one hot-swappable specialist, the RAM admission
 * gate for the specialist slot, and the host-neutral chat-runtime seam +
 * NeuronNode facade.
 *
 * Port of CircleAI.Hosting.Neuron. The C hosting substrate is the thinnest of
 * the ports (a callback generator + capability flags + device probe, with no
 * AIService chat loop), so the Neuron ships here as a self-contained C11 module
 * over that substrate: the concierge decision table, the admission gate, and the
 * node facade. Capabilities reuse the ca_chat_capability_t flags from selector.h.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>
#include "selector.h" /* ca_chat_capability_t (CA_CHAT_CAP_*) */

#ifdef __cplusplus
extern "C" {
#endif

/* ---------------------------------------------------------------------------
 * Concierge router + gate
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_ORGAN_GENERALIST = 0,
    CA_ORGAN_SPECIALIST = 1
} ca_neuron_organ_t;

typedef struct {
    const char *query;        /* user text for the turn (may be NULL)          */
    bool        has_image;    /* turn carries image bytes → vision             */
    int32_t     prompt_chars; /* total prompt length; < 0 → strlen(query)      */
} ca_route_context_t;

typedef struct {
    ca_neuron_organ_t organ;
    uint32_t          capability; /* a ca_chat_capability_t flag               */
    const char       *reason;     /* static string, for observability          */
} ca_route_decision_t;

/* Gate predicate: return false to veto a specialist pick (→ generalist). */
typedef bool (*ca_neuron_gate_fn)(const ca_route_decision_t *decision, void *user);

typedef struct {
    int32_t           long_context_chars; /* <= 0 → default 4000               */
    ca_neuron_gate_fn gate;               /* NULL → allow all                  */
    void             *gate_user;
} ca_neuron_router_t;

/* Initialise a router with defaults (long_context_chars = 4000, no gate). */
void ca_neuron_router_init(ca_neuron_router_t *router);

/*
 * Route one turn. Cheap heuristics, in priority order:
 *   image        → SPECIALIST(VISION)
 *   long prompt  → SPECIALIST(LONG_CTX)     (>= long_context_chars)
 *   reasoning    → SPECIALIST(REASONING)
 *   otherwise    → GENERALIST(DEFAULT)
 * A gate veto demotes any specialist pick back to the generalist.
 */
ca_route_decision_t ca_neuron_route(const ca_neuron_router_t *router,
                                    const ca_route_context_t *ctx);

/* ---------------------------------------------------------------------------
 * ResidentSlotManager — RAM admission gate for the specialist slot
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_SLOT_ADMITTED         = 0,
    CA_SLOT_ALREADY_RESIDENT = 1,
    CA_SLOT_INSUFFICIENT_RAM = 2,
    CA_SLOT_BUILD_FAILED     = 3
} ca_slot_outcome_t;

/* Build a specialist for model_id. Returns an opaque handle, or NULL on failure. */
typedef void *(*ca_specialist_build_fn)(const char *model_id, void *user);
/* Optional destructor for a built specialist handle. */
typedef void (*ca_specialist_free_fn)(void *specialist, void *user);
/* Live RAM ceiling in bytes. */
typedef int64_t (*ca_ram_available_fn)(void *user);

typedef struct {
    int64_t               generalist_reserved_bytes;
    ca_ram_available_fn   ram_available;
    void                 *ram_user;
    ca_specialist_free_fn free_fn;   /* may be NULL                            */
    void                 *free_user;

    /* internal state */
    void *specialist;                /* resident handle, or NULL               */
    bool  has_specialist;
    char  specialist_model_id[128];
} ca_resident_slot_manager_t;

void ca_slot_manager_init(ca_resident_slot_manager_t *m,
                          int64_t generalist_reserved_bytes,
                          ca_ram_available_fn ram_available, void *ram_user);

/* Optional: register a destructor invoked when a specialist is evicted/swapped. */
void ca_slot_manager_set_free(ca_resident_slot_manager_t *m,
                              ca_specialist_free_fn free_fn, void *free_user);

/*
 * Ensure model_id is the resident specialist, building it via `build` if needed.
 * Admission-gated on RAM; a different pick evicts the incumbent. Never fails
 * hard — returns the outcome so the caller can fall back to the generalist.
 * out_specialist (may be NULL) receives the resident handle on
 * ADMITTED / ALREADY_RESIDENT.
 */
ca_slot_outcome_t ca_slot_ensure_specialist(ca_resident_slot_manager_t *m,
                                             const char *model_id,
                                             int64_t estimated_bytes,
                                             ca_specialist_build_fn build,
                                             void *build_user,
                                             void **out_specialist);

/* Resident specialist model id, or NULL when the slot is empty. */
const char *ca_slot_resident_model_id(const ca_resident_slot_manager_t *m);
/* Resident specialist handle, or NULL when the slot is empty. */
void *ca_slot_resident_specialist(const ca_resident_slot_manager_t *m);

/* Drop the resident specialist (the generalist floor is untouched). */
void ca_slot_evict(ca_resident_slot_manager_t *m);

/* ---------------------------------------------------------------------------
 * Host-neutral chat runtime seam + NeuronNode facade
 * --------------------------------------------------------------------------- */

typedef struct {
    const char *role;
    const char *content;
} ca_chat_turn_t;

/* Streaming sink: invoked once per emitted chunk. */
typedef void (*ca_chunk_callback)(const char *chunk, void *userdata);

/*
 * The "brain" a NeuronNode composes over (the IAIService analog). Any field may
 * be NULL: save/load default to false, resolved_model_id to none, is_ready to
 * false, stream to a no-op.
 */
typedef struct {
    bool        (*is_ready)(void *impl);
    const char *(*resolved_model_id)(void *impl);
    void        (*stream)(void *impl, const ca_chat_turn_t *turns, size_t n,
                          ca_chunk_callback on_chunk, void *userdata);
    bool        (*save_session)(void *impl, const char *path);
    bool        (*load_session)(void *impl, const char *path);
    void         *impl;
} ca_neuron_brain_t;

typedef struct {
    ca_neuron_brain_t brain;
    char              snapshot_path[512];
    char              engine_label[160];
} ca_neuron_node_t;

/* Initialise a node over brain. snapshot_path NULL/empty → a default temp path. */
void ca_neuron_node_init(ca_neuron_node_t *node, ca_neuron_brain_t brain,
                         const char *snapshot_path);

const char *ca_neuron_node_id(const ca_neuron_node_t *node);     /* "circleai-neuron"          */
const char *ca_neuron_node_engine_label(ca_neuron_node_t *node); /* "circleai-neuron[:id]"     */
bool        ca_neuron_node_is_ready(const ca_neuron_node_t *node);
const char *ca_neuron_node_status(const ca_neuron_node_t *node); /* "ready"/"loading model…"   */
const char *ca_neuron_node_snapshot_path(const ca_neuron_node_t *node);
void        ca_neuron_node_stream(ca_neuron_node_t *node, const ca_chat_turn_t *turns, size_t n,
                                  ca_chunk_callback on_chunk, void *userdata);
bool        ca_neuron_node_save_session(ca_neuron_node_t *node, const char *path);
bool        ca_neuron_node_load_session(ca_neuron_node_t *node, const char *path);

/* NullChatRuntime — never ready; streams a single "no engine" notice. */
bool        ca_null_chat_runtime_is_ready(void);
const char *ca_null_chat_runtime_status(void);
void        ca_null_chat_runtime_stream(const ca_chat_turn_t *turns, size_t n,
                                        ca_chunk_callback on_chunk, void *userdata);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_NEURON_H */
