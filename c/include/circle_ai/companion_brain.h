#ifndef CIRCLE_AI_COMPANION_BRAIN_H
#define CIRCLE_AI_COMPANION_BRAIN_H

/*
 * companion_brain.h — CircleAI companion-brain (C11 port).
 *
 * Belief attribution + revision (memory integrity), the background memory
 * encoder (turn → graph + beliefs, drained on close), and the concrete
 * companion session (recall → prompt → generate → persist → encode). Ported from
 * the C# reference (CircleAI.Companion) and mirroring the Swift / Rust / Go / TS
 * ports 1:1.
 *
 * Memory ownership follows the same contract as memory_brain.h: owning structs
 * hold strdup'd copies with matching *_free; returned arrays are deep copies the
 * caller frees.
 *
 * Pure C11 + libc. Links against -lm.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#include "models.h"        /* ca_chat_message_t, ca_role_t */
#include "memory_brain.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Attribution + PersonalBelief
 * ===========================================================================
 *
 * Whose fact a belief is about. The highest-harm rule: a third party's fact
 * ("my mother is diabetic") must never be recorded as a fact about the user.
 */

typedef enum {
    CA_ATTRIBUTION_SELF  = 0,
    CA_ATTRIBUTION_OTHER = 1,
    CA_ATTRIBUTION_WORLD = 2
} ca_attribution_t;

typedef struct {
    ca_attribution_t attribution;
    char            *subject;    /* owned */
    char            *predicate;  /* owned */
    char            *object;     /* owned */
    double           confidence;
    char            *source;     /* owned, or NULL */
    int64_t          recorded_at_ms;
} ca_personal_belief_t;

void ca_personal_belief_free(ca_personal_belief_t *belief);
void ca_personal_belief_free_array(ca_personal_belief_t *beliefs, size_t count);

/* Extract attributed beliefs from a sentence (predicate "isAbout", confidence
 * 0.6). source may be NULL. Returns a fresh belief array (caller frees with
 * ca_personal_belief_free_array); *out_count set. An empty / object-less sentence
 * yields count 0. */
ca_personal_belief_t *ca_belief_extract(const char *text, const char *source,
                                        size_t *out_count);

/* The belief-extractor seam consumed by the encoder. */
typedef ca_personal_belief_t *(*ca_belief_extractor_fn)(
    void *user, const char *text, const char *source, size_t *out_count);

/* Adapter wrapping the heuristic extractor as a seam (user is ignored). */
ca_personal_belief_t *ca_belief_extractor_heuristic_adapter(
    void *user, const char *text, const char *source, size_t *out_count);

/* ===========================================================================
 * SelfBeliefStore
 * ===========================================================================
 *
 * Holds the user's OWN facts. Only self beliefs become user facts; other/world
 * beliefs are audited (remembered, never a user fact). Same (subject,predicate)
 * supersedes (case-insensitive). Retract by object substring. Provenance = the
 * distinct source turns behind the user's facts.
 */

typedef struct ca_self_belief_store ca_self_belief_store_t;

ca_self_belief_store_t *ca_self_belief_store_create(void);
void                    ca_self_belief_store_destroy(ca_self_belief_store_t *store);

/* Record a copy of belief. Self → user fact (superseding same subject+predicate);
 * other/world → audit list. */
void ca_self_belief_store_record(ca_self_belief_store_t *store,
                                 const ca_personal_belief_t *belief);

/* Deep copy of the user's own facts. */
ca_personal_belief_t *ca_self_belief_store_self_facts(const ca_self_belief_store_t *store,
                                                      size_t *out_count);

/* Deep copy of the audited (non-self) beliefs. */
ca_personal_belief_t *ca_self_belief_store_non_self(const ca_self_belief_store_t *store,
                                                    size_t *out_count);

/* Drop any user fact whose object contains object_substr (case-insensitive).
 * Returns the number removed. */
size_t ca_self_belief_store_retract(ca_self_belief_store_t *store,
                                    const char *object_substr);

/* The distinct source turns behind the user's facts (first-seen order). Returns a
 * fresh array of owned strings; caller frees with ca_string_array_free. */
char **ca_self_belief_store_provenance(const ca_self_belief_store_t *store,
                                       size_t *out_count);

/* Free an array of owned strings (each string + the array). NULL-safe. */
void ca_string_array_free(char **arr, size_t count);

/* ===========================================================================
 * Companion memory encoder
 * ===========================================================================
 *
 * Background writer: turn → knowledge graph + attributed beliefs, off the hot
 * path. enqueue is synchronous and non-blocking; a full queue DROPS (never
 * blocks). close() drains the buffered turns synchronously (no threads — the
 * drain-on-close design shared by the Swift/Go/Rust ports keeps drop-on-full
 * deterministic). The graph, extractor seam, optional belief seam, and optional
 * belief store are all borrowed (the caller keeps them alive).
 */

typedef struct ca_memory_encoder ca_memory_encoder_t;

/* Create an encoder. belief_fn / beliefs may both be NULL to skip belief
 * formation. capacity 0 → default 256. Returns NULL only on a NULL graph or
 * extractor. */
ca_memory_encoder_t *ca_memory_encoder_create(
    ca_kg_extractor_fn extractor_fn, void *extractor_user,
    ca_knowledge_graph_t *graph /* borrowed */,
    ca_belief_extractor_fn belief_fn, void *belief_user,
    ca_self_belief_store_t *beliefs /* borrowed, or NULL */,
    size_t capacity);

/* Destroys the encoder. If it was never closed, any buffered turns are DROPPED
 * (not drained) — call ca_memory_encoder_close first to flush. */
void ca_memory_encoder_destroy(ca_memory_encoder_t *enc);

/* Hand a turn to the encoder. Non-blocking. A blank episode_id is ignored; an
 * overflow beyond capacity is dropped; an enqueue after close is ignored. */
void ca_memory_encoder_enqueue(ca_memory_encoder_t *enc,
                               const char *user_text, const char *assistant_text,
                               const char *episode_id);

/* Stop accepting work and drain the queue into the graph + beliefs. Idempotent. */
void ca_memory_encoder_close(ca_memory_encoder_t *enc);

/* The first error message captured while draining, or NULL. Borrowed. */
const char *ca_memory_encoder_last_error(const ca_memory_encoder_t *enc);

/* ===========================================================================
 * Companion session
 * ===========================================================================
 *
 * The conscious loop. On send(): recall fused memory + the user's own facts,
 * build the message list (system prompt = persona/affect + "[What you know about
 * the user]" facts + "[Relevant memories]" snippets, then history, then the user
 * turn), call the generator, persist the exchange to episodic, hand it to the
 * encoder, and append to history.
 *
 * Generator convention: the generator returns a heap-allocated (malloc'd) reply
 * string; the session takes ownership and frees it. The messages array is valid
 * only for the duration of the call.
 */

typedef char *(*ca_generate_fn)(void *user, const ca_chat_message_t *msgs, size_t n);

typedef struct {
    const char *session_id;
    const char *identity_id;
    const char *interface_kind;    /* free-form label, e.g. "mobile"; may be NULL */
    const char *persona_hints;     /* prepended to the system prompt; may be NULL */
    const char *affect_summary;    /* prepended to the system prompt; may be NULL */
    const char *app_context;       /* stamped on persisted episodes; may be NULL */
    int         recall_top_k;      /* 0 → default 5 */
} ca_companion_session_options_t;

typedef struct ca_companion_session ca_companion_session_t;

/* Create a session. generator, episodic and recall are required (borrowed).
 * encoder and beliefs are optional (borrowed, or NULL). opts is copied. */
ca_companion_session_t *ca_companion_session_create(
    ca_generate_fn generator, void *generator_user,
    ca_episodic_store_t *episodic /* borrowed */,
    ca_fused_recall_t *recall /* borrowed */,
    ca_memory_encoder_t *encoder /* borrowed, or NULL */,
    ca_self_belief_store_t *beliefs /* borrowed, or NULL */,
    const ca_companion_session_options_t *opts);
void ca_companion_session_destroy(ca_companion_session_t *session);

/* Run one turn. Returns a freshly malloc'd reply the CALLER frees, or NULL on a
 * generator failure. */
char *ca_companion_session_send(ca_companion_session_t *session, const char *message);

/* Number of history messages (2 per completed turn: user + assistant). */
size_t ca_companion_session_history_count(const ca_companion_session_t *session);

/* Borrowed pointer to the role of history message i ("user"/"assistant"), or NULL. */
const char *ca_companion_session_history_role(const ca_companion_session_t *session, size_t i);
/* Borrowed pointer to the content of history message i, or NULL. */
const char *ca_companion_session_history_content(const ca_companion_session_t *session, size_t i);

/* The memory snippets recalled on the last turn (borrowed pointers into the
 * session context). *out_count set. */
const char *const *ca_companion_session_context_snippets(
    const ca_companion_session_t *session, size_t *out_count);

/* Recompute context snippets from a memory recall with an empty query (no turn).*/
void ca_companion_session_refresh_context(ca_companion_session_t *session);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COMPANION_BRAIN_H */
