#ifndef CIRCLE_AI_COMPANION_SYNC_H
#define CIRCLE_AI_COMPANION_SYNC_H

/*
 * companion_sync.h — CircleAI.Memory.Sync (C11 port).
 *
 * Faithful port of the companion-state sync layer:
 *   - HybridLogicalClock (HLC)        — HybridLogicalClock.cs
 *   - SyncableEntry                   — SyncableEntry.cs
 *   - SyncEnvelope / kinds / vectors  — SyncEnvelope.cs
 *   - ISyncableEntryStore + InMemory  — ISyncableEntryStore.cs / InMemorySyncableEntryStore.cs
 *   - ICompanionStateChannel + InProc — ICompanionStateChannel.cs / InProcessCompanionStateChannel.cs
 *   - CompanionStateSyncEngine        — CompanionStateSyncEngine.cs / ICompanionStateSyncEngine.cs
 *   - PersonaStateSyncBridge          — PersonaStateSyncBridge.cs
 *   - LoraAdapterSyncBridge/-Snapshot — LoraAdapterSyncBridge.cs
 *   - CompanionConversationSyncBridge — CompanionConversationSyncBridge.cs
 *
 * C# is the exact spec. Async is collapsed to synchronous calls (no pthreads),
 * generics/hashmaps become linear arrays, and the convergence protocol +
 * apply/tiebreak rules + HLC bit-layout + SHA-256 content hashing are matched
 * byte-for-byte.
 *
 * Conventions: ca_ prefix, _t on types, opaque handles with create/destroy,
 * strdup'd owning fields with matching *_free, returned arrays are deep copies
 * the caller frees. Errors: NULL / count == SIZE_MAX / bool false.
 *
 * Pure C11 + libc. Reuses ca_sha256_hex from multimodal.h for content hashing.
 */

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

/* Reused SDK primitives (shared, not redefined here):
 *   ca_base64_encode / ca_base64_decode  — compression.h
 *   ca_string_array_free                 — companion_brain.h
 * They have identical signatures + semantics to what this module needs. */
#include "compression.h"
#include "companion_brain.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * HybridLogicalClock — HybridLogicalClock.cs
 *
 * Version bit-layout (64-bit): high 48 bits physical ms, mid 10 bits logical,
 * low 6 bits nodeShortId (0..63). Compose/Decompose are static + pure.
 * =========================================================================== */

/* Source of physical time in milliseconds (Func<long> physicalNowMs). */
typedef int64_t (*ca_hlc_now_fn)(void *user);

typedef struct ca_hybrid_logical_clock ca_hybrid_logical_clock_t;

/* Create an HLC. node_short_id must be in 0..63 (returns NULL otherwise).
 * now may be NULL → default system-time source (DateTimeOffset.UtcNow ms). */
ca_hybrid_logical_clock_t *ca_hlc_create(int64_t node_short_id,
                                         ca_hlc_now_fn now, void *now_user);
void ca_hlc_destroy(ca_hybrid_logical_clock_t *clock);

/* Tick() — next outgoing version for a locally-originated write. */
int64_t ca_hlc_tick(ca_hybrid_logical_clock_t *clock);

/* Observe(incoming) — fold in a received version so subsequent local ticks stay
 * monotonic w.r.t. peers. Returns the recomposed local version. */
int64_t ca_hlc_observe(ca_hybrid_logical_clock_t *clock, int64_t incoming);

/* Static composition helpers (pure — no clock instance needed). */
int64_t ca_hlc_compose(int64_t physical_ms, int64_t logical, int64_t node_short_id);
void    ca_hlc_decompose(int64_t version,
                         int64_t *out_physical_ms, int64_t *out_logical,
                         int64_t *out_node_short_id);

/* ===========================================================================
 * SyncableEntry — SyncableEntry.cs (the wire unit)
 * =========================================================================== */

typedef struct {
    char   *entity_type;    /* owned */
    char   *entity_id;      /* owned */
    int64_t version;        /* HLC version stamp */
    bool    is_tombstone;   /* true → deletion; payload empty */
    char   *content_hash;   /* owned; lowercase SHA-256 hex of payload */
    char   *payload;        /* owned; opaque string */
    char   *source_node_id; /* owned */
    int64_t authored_at_ms; /* Unix ms UTC — display/provenance only */
} ca_syncable_entry_t;

/* Free the owned fields of an entry (does not free the struct itself). */
void ca_syncable_entry_free(ca_syncable_entry_t *e);
/* Free an array of entries and the array itself (returned deep copies). */
void ca_syncable_entry_free_array(ca_syncable_entry_t *arr, size_t count);
/* Deep-copy src into dst (dst owns fresh strdup'd fields). Returns dst. */
ca_syncable_entry_t *ca_syncable_entry_copy(ca_syncable_entry_t *dst,
                                            const ca_syncable_entry_t *src);

/* ===========================================================================
 * SyncEnvelope + payload records — SyncEnvelope.cs
 * =========================================================================== */

typedef enum {
    CA_SYNC_ENVELOPE_ANNOUNCE = 0, /* per-type high-watermark broadcast */
    CA_SYNC_ENVELOPE_REQUEST  = 1, /* ask for entries newer than a version */
    CA_SYNC_ENVELOPE_PUSH     = 2  /* delivery of entries */
} ca_sync_envelope_kind_t;

/* StateVectorEntry(EntityType, MaxKnownVersion). */
typedef struct {
    char   *entity_type;      /* owned */
    int64_t max_known_version;
} ca_state_vector_entry_t;

/* RequestItem(EntityType, SinceVersion). */
typedef struct {
    char   *entity_type;  /* owned */
    int64_t since_version;
} ca_request_item_t;

/* SyncEnvelope — nullable lists become pointer + count (NULL/0). */
typedef struct {
    ca_sync_envelope_kind_t  kind;
    char                    *from_node_id;   /* owned */
    ca_state_vector_entry_t *state_vector;   /* owned array or NULL */
    size_t                   state_vector_count;
    ca_request_item_t       *requests;       /* owned array or NULL */
    size_t                   requests_count;
    ca_syncable_entry_t     *entries;        /* owned array or NULL */
    size_t                   entries_count;
} ca_sync_envelope_t;

/* Free an envelope's owned members (not the struct itself). */
void ca_sync_envelope_free(ca_sync_envelope_t *env);

/* ===========================================================================
 * ISyncableEntryStore — ISyncableEntryStore.cs
 *
 * Apply rules (implementations MUST enforce for convergence):
 *   - higher Version wins
 *   - on tie, tombstone-of-non-tombstone wins
 *   - else higher ContentHash (ordinal string compare) wins
 * =========================================================================== */

typedef struct ca_syncable_entry_store ca_syncable_entry_store_t;

/* ApplyAsync — true when local state was actually updated. */
typedef bool (*ca_store_apply_fn)(void *self, const ca_syncable_entry_t *entry);

/* GetAsync — deep-copy the current entry for (type,id) into *out and return
 * true, or return false when unknown. Tombstones ARE returned. */
typedef bool (*ca_store_get_fn)(void *self, const char *entity_type,
                                const char *entity_id, ca_syncable_entry_t *out);

/* GetSinceAsync — fresh ascending-by-version array of entries of entity_type
 * whose Version > since_version. Sets *out_count and returns the array
 * (caller frees with ca_syncable_entry_free_array). NULL + *out_count == 0
 * when none. On failure sets *out_count = SIZE_MAX and returns NULL. */
typedef ca_syncable_entry_t *(*ca_store_get_since_fn)(
    void *self, const char *entity_type, int64_t since_version, size_t *out_count);

/* GetStateVectorAsync — highest known Version per type, ascending by type
 * (ordinal). Fresh array (caller frees with ca_state_vector_free_array). */
typedef ca_state_vector_entry_t *(*ca_store_get_state_vector_fn)(
    void *self, size_t *out_count);

/* Vtable seam so the engine (and MemorySyncService) can drive any store. */
typedef struct {
    void                        *self;
    ca_store_apply_fn            apply;
    ca_store_get_fn             get;
    ca_store_get_since_fn        get_since;
    ca_store_get_state_vector_fn get_state_vector;
} ca_syncable_store_iface_t;

void ca_state_vector_free_array(ca_state_vector_entry_t *arr, size_t count);

/* --- InMemorySyncableEntryStore — the concrete default ------------------- */

ca_syncable_entry_store_t *ca_inmem_syncable_store_create(void);
void ca_inmem_syncable_store_destroy(ca_syncable_entry_store_t *store);

/* Wrap a concrete in-memory store as the vtable seam. */
ca_syncable_store_iface_t ca_inmem_syncable_store_iface(ca_syncable_entry_store_t *store);

/* Direct (non-vtable) accessors mirroring the interface. */
bool ca_inmem_syncable_store_apply(ca_syncable_entry_store_t *store,
                                   const ca_syncable_entry_t *entry);
bool ca_inmem_syncable_store_get(ca_syncable_entry_store_t *store,
                                 const char *entity_type, const char *entity_id,
                                 ca_syncable_entry_t *out);
ca_syncable_entry_t *ca_inmem_syncable_store_get_since(
    ca_syncable_entry_store_t *store,
    const char *entity_type, int64_t since_version, size_t *out_count);
ca_state_vector_entry_t *ca_inmem_syncable_store_get_state_vector(
    ca_syncable_entry_store_t *store, size_t *out_count);

/* ===========================================================================
 * ICompanionStateChannel + InProcess loopback — ICompanionStateChannel.cs /
 * InProcessCompanionStateChannel.cs
 * =========================================================================== */

/* Inbound handler (Func<SyncEnvelope, CancellationToken, Task>). */
typedef void (*ca_channel_handler_fn)(void *user, const ca_sync_envelope_t *env);

typedef struct ca_companion_state_channel ca_companion_state_channel_t;

/* Channel vtable seam. */
typedef struct {
    void       *self;
    const char *(*local_node_id)(void *self);
    void        (*send)(void *self, const ca_sync_envelope_t *env);
    /* Subscribe returns an opaque subscription token; unsubscribe with it. */
    void       *(*subscribe)(void *self, ca_channel_handler_fn handler, void *user);
    void        (*unsubscribe)(void *self, void *subscription);
} ca_companion_state_channel_iface_t;

/* --- InProcessSyncHub — routes envelopes between joined channels --------- */

typedef struct ca_inproc_sync_hub ca_inproc_sync_hub_t;

ca_inproc_sync_hub_t *ca_inproc_sync_hub_create(void);
void                  ca_inproc_sync_hub_destroy(ca_inproc_sync_hub_t *hub);

/* Node ids currently on the hub (fresh array of strdup'd ids the caller frees
 * with ca_string_array_free — declared in companion_brain.h). */
char **ca_inproc_sync_hub_connected(const ca_inproc_sync_hub_t *hub, size_t *out_count);

/* --- InProcessCompanionStateChannel ------------------------------------- */

/* Create a channel joined to the hub. Rejects NULL hub / blank id (NULL). */
ca_companion_state_channel_t *ca_inproc_channel_create(
    ca_inproc_sync_hub_t *hub, const char *local_node_id);
/* Dispose — leaves the hub and clears handlers (matches C# Dispose). */
void ca_inproc_channel_destroy(ca_companion_state_channel_t *channel);

const char *ca_inproc_channel_local_node_id(const ca_companion_state_channel_t *channel);
void        ca_inproc_channel_send(ca_companion_state_channel_t *channel,
                                   const ca_sync_envelope_t *env);
void       *ca_inproc_channel_subscribe(ca_companion_state_channel_t *channel,
                                        ca_channel_handler_fn handler, void *user);
void        ca_inproc_channel_unsubscribe(ca_companion_state_channel_t *channel,
                                          void *subscription);

/* Wrap the channel as the vtable seam the engine consumes. */
ca_companion_state_channel_iface_t ca_inproc_channel_iface(
    ca_companion_state_channel_t *channel);

/* ===========================================================================
 * CompanionStateSyncEngine — CompanionStateSyncEngine.cs /
 * ICompanionStateSyncEngine.cs
 * =========================================================================== */

typedef int64_t (*ca_engine_wallclock_fn)(void *user); /* Func<DateTimeOffset> ms */

typedef struct ca_companion_state_sync_engine ca_companion_state_sync_engine_t;

/* Create over an injected channel + store + clock. wall_clock may be NULL →
 * default UtcNow ms. Returns NULL when any required arg is NULL. Borrows all
 * three (the caller keeps them alive; the engine does not free them). */
ca_companion_state_sync_engine_t *ca_sync_engine_create(
    ca_companion_state_channel_iface_t channel,
    ca_syncable_store_iface_t store,
    ca_hybrid_logical_clock_t *clock,
    ca_engine_wallclock_fn wall_clock, void *wall_clock_user);

/* DisposeAsync — unsubscribes; safe to call twice. Frees the engine. */
void ca_sync_engine_destroy(ca_companion_state_sync_engine_t *engine);

/* StartAsync — subscribe to channel envelopes (idempotent). Returns false when
 * disposed. */
bool ca_sync_engine_start(ca_companion_state_sync_engine_t *engine);

/* SyncNowAsync — broadcast the local state vector as an Announce. */
bool ca_sync_engine_sync_now(ca_companion_state_sync_engine_t *engine);

/* WriteLocalAsync — stamp payload with a fresh HLC version, persist, and (if
 * started) Push it. Deep-copies the resulting entry into *out_entry (caller
 * frees with ca_syncable_entry_free) when out_entry != NULL. Returns false on a
 * blank entity_type/entity_id or when disposed. payload NULL → "". */
bool ca_sync_engine_write_local(
    ca_companion_state_sync_engine_t *engine,
    const char *entity_type, const char *entity_id, const char *payload,
    bool is_tombstone, ca_syncable_entry_t *out_entry);

/* ===========================================================================
 * PersonaStateSyncBridge — PersonaStateSyncBridge.cs
 *
 * The C engine writes an opaque payload; the C# bridge JSON-serialises the
 * PersonaState. In C the caller supplies the already-serialised payload
 * (persona serialisation lives with the persona store) so the bridge stays a
 * thin type-tag + write wrapper, exactly like the C# EntityType constant.
 * =========================================================================== */

#define CA_PERSONA_SYNC_ENTITY_TYPE "PersonaState"

/* SaveAsync-equivalent: write persona_json under (PersonaState, user_id). The
 * caller is expected to have persisted the persona to its own store first
 * (mirroring the C# store.SaveAsync then engine.WriteLocalAsync). */
bool ca_persona_sync_bridge_save(ca_companion_state_sync_engine_t *engine,
                                 const char *user_id, const char *persona_json);

/* TryDecode: returns a strdup'd copy of the payload JSON when the entry is a
 * live PersonaState entry, else NULL. (The C# returns a deserialised object;
 * here the payload IS the serialised persona — caller deserialises.) */
char *ca_persona_sync_bridge_try_decode(const ca_syncable_entry_t *entry);

/* ===========================================================================
 * LoraAdapterSyncBridge + LoraAdapterSnapshot — LoraAdapterSyncBridge.cs
 *
 * Adapter bytes are base64-encoded into a JSON payload:
 *   {"AdapterId":"..","Base64Bytes":"..","TrainedAtUtc":"..","StepCount":N}
 * matching System.Text.Json's default (PascalCase, ISO-8601 UTC "O" string).
 * =========================================================================== */

#define CA_LORA_SYNC_ENTITY_TYPE "LoraAdapter"

typedef struct {
    char   *adapter_id;      /* owned */
    char   *base64_bytes;    /* owned */
    int64_t trained_at_ms;   /* Unix ms UTC */
    int64_t step_count;
} ca_lora_adapter_snapshot_t;

void ca_lora_adapter_snapshot_free(ca_lora_adapter_snapshot_t *s);

/* Base64 (RFC 4648, '=' padding) is reused from compression.h:
 *   ca_base64_encode(const uint8_t *data, size_t len)  → NUL-terminated string
 *   ca_base64_decode(const char *b64, size_t *out_len) → bytes (NULL on error) */

/* PublishAsync — read adapter bytes, base64+JSON encode a snapshot, write it
 * under (LoraAdapter, adapter_id). Rejects blank ids/bytes. */
bool ca_lora_sync_bridge_publish(ca_companion_state_sync_engine_t *engine,
                                 const char *adapter_id,
                                 const uint8_t *adapter_bytes, size_t adapter_len,
                                 int64_t trained_at_ms, int64_t step_count);

/* TryWrite — decode an inbound entry's JSON payload into *out_snapshot and, when
 * base64 bytes are present, hand back the decoded adapter bytes via the
 * out_bytes and out_len pointers (caller frees out_bytes). Returns true on a
 * decoded snapshot (even with empty bytes: out_bytes NULL, out_len 0); false for a
 * tombstone / wrong type / undecodable payload. */
bool ca_lora_sync_bridge_try_write(const ca_syncable_entry_t *entry,
                                   ca_lora_adapter_snapshot_t *out_snapshot,
                                   uint8_t **out_bytes, size_t *out_len);

/* ===========================================================================
 * CompanionConversationSyncBridge + ConversationStateDelta —
 * CompanionConversationSyncBridge.cs
 *
 * JSON payload (PascalCase, System.Text.Json defaults):
 *   {"SessionId":"..","UserText":"..","AssistantText":"..",
 *    "IsTurnComplete":bool,"StartedAtUtc":"O","UpdatedAtUtc":"O"}
 * =========================================================================== */

#define CA_CONVERSATION_SYNC_ENTITY_TYPE "ConversationState"

typedef struct {
    char   *session_id;      /* owned */
    char   *user_text;       /* owned */
    char   *assistant_text;  /* owned */
    bool    is_turn_complete;
    int64_t started_at_ms;   /* Unix ms UTC */
    int64_t updated_at_ms;   /* Unix ms UTC */
} ca_conversation_state_delta_t;

void ca_conversation_state_delta_free(ca_conversation_state_delta_t *d);

/* PublishAsync — JSON-serialise the delta and write under
 * (ConversationState, SessionId). Rejects a blank session id. */
bool ca_conversation_sync_bridge_publish(
    ca_companion_state_sync_engine_t *engine,
    const ca_conversation_state_delta_t *delta);

/* TerminateAsync — tombstone the session (empty payload). */
bool ca_conversation_sync_bridge_terminate(
    ca_companion_state_sync_engine_t *engine, const char *session_id);

/* TryDecode — decode a live entry back into *out_delta (caller frees with
 * ca_conversation_state_delta_free). Returns false for tombstone / wrong
 * type / undecodable payload. */
bool ca_conversation_sync_bridge_try_decode(
    const ca_syncable_entry_t *entry, ca_conversation_state_delta_t *out_delta);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COMPANION_SYNC_H */
