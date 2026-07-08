#ifndef CIRCLE_AI_HOST_MULTIPLAYER_H
#define CIRCLE_AI_HOST_MULTIPLAYER_H

/*
 * host_multiplayer.h — CircleAI.Hosting.Multiplayer (C11 port).
 *
 * Ports (from src/CircleAI.Hosting.Multiplayer):
 *   IMultiplayerPeerIdentity  — PeerId + DisplayName (identity seam)
 *   GuestPeerIdentity         — anonymous guest (deterministic id here)
 *   PeerState                 — ConnectionId / DisplayName / Color / DocId
 *   MultiplayerHub            — per-document groups, LWW-by-rev edits, live
 *                               cursors, presence. The SignalR transport is an
 *                               injected event-emitter seam; the hub logic
 *                               (join/leave/cursor/edit + rev arbitration +
 *                               ColourFor) is ported 1:1.
 *
 * The C# hub keeps static process-wide state (RevByDoc / PeerByConn). Here the
 * hub instance owns that state (one in-process hub == one SignalR server). Each
 * mutating call takes the caller's connection id explicitly (in SignalR it is
 * Context.ConnectionId).
 *
 * Conventions: ca_ prefix, _t types, opaque handle, strdup owning fields,
 * returned arrays are deep copies the caller frees.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * PeerState
 * =========================================================================== */

typedef struct {
    char *connection_id;   /* owned */
    char *display_name;    /* owned */
    char *color;           /* owned */
    char *doc_id;          /* owned, or NULL */
} ca_peer_state_t;

void ca_peer_state_free(ca_peer_state_t *p);
void ca_peer_state_free_array(ca_peer_state_t *arr, size_t count);

/* ===========================================================================
 * IMultiplayerPeerIdentity + GuestPeerIdentity
 * =========================================================================== */

typedef struct {
    const char *peer_id;
    const char *display_name;
} ca_peer_identity_t;

/* GuestPeerIdentity: peer_id NULL => a deterministic 32-hex id; display_name
 * NULL => "Guest". Fills *out (peer_id/display_name malloc'd). Free with
 * ca_guest_peer_identity_free. */
typedef struct {
    char *peer_id;        /* owned */
    char *display_name;   /* owned */
} ca_guest_peer_identity_t;

void ca_guest_peer_identity_init(ca_guest_peer_identity_t *out,
                                 const char *peer_id, const char *display_name);
void ca_guest_peer_identity_free(ca_guest_peer_identity_t *g);

/* ColourFor(peerId) — stable hash → "hsl(hue, 70%, 55%)" (empty => "#5a4fcf").
 * Returns a freshly-allocated string (caller frees). */
char *ca_multiplayer_colour_for(const char *peer_id);

/* ===========================================================================
 * MultiplayerHub
 * ===========================================================================
 *
 * Outgoing SignalR events are delivered through the emit seam. `target`
 * indicates the audience the C# used (OthersInGroup(docGroup)); the C port
 * passes the doc id so the host can fan-out to its own transport. args_json is
 * a JSON array of the SendAsync arguments.
 */

/* emit(user, event_name, doc_id, args_json). doc_id may be NULL. */
typedef void (*ca_multiplayer_emit_fn)(void *user, const char *event_name,
                                       const char *doc_id, const char *args_json);

typedef struct ca_multiplayer_hub ca_multiplayer_hub_t;

ca_multiplayer_hub_t *ca_multiplayer_hub_create(ca_multiplayer_emit_fn emit, void *emit_user);
void ca_multiplayer_hub_destroy(ca_multiplayer_hub_t *h);

/* OnConnectedAsync — register a connection with the peer identity. */
void ca_multiplayer_hub_on_connected(ca_multiplayer_hub_t *h, const char *connection_id,
                                     const ca_peer_identity_t *identity);
/* OnDisconnectedAsync — drop the connection; emit PeerLeft if it was in a doc. */
void ca_multiplayer_hub_on_disconnected(ca_multiplayer_hub_t *h, const char *connection_id);

/* JoinDocument — subscribe the connection to a doc group; emit PeerJoined. */
void ca_multiplayer_hub_join_document(ca_multiplayer_hub_t *h, const char *connection_id,
                                      const char *doc_id);
/* LeaveDocument — unsubscribe; emit PeerLeft. */
void ca_multiplayer_hub_leave_document(ca_multiplayer_hub_t *h, const char *connection_id,
                                       const char *doc_id);
/* SendCursor — broadcast cursor pos. */
void ca_multiplayer_hub_send_cursor(ca_multiplayer_hub_t *h, const char *connection_id,
                                    const char *doc_id, int line, int ch);
/* SendEdit — LWW-by-rev. Applies + broadcasts EditApplied when rev is greater
 * than the server rev; returns the new (accepted) rev, or the server's current
 * rev when the edit was stale. */
int64_t ca_multiplayer_hub_send_edit(ca_multiplayer_hub_t *h, const char *connection_id,
                                     const char *doc_id, const char *content, int64_t rev);

/* Peers(docId) — snapshot of who is in a document. Fresh array (caller frees).
 */
ca_peer_state_t *ca_multiplayer_hub_peers(ca_multiplayer_hub_t *h, const char *doc_id,
                                          size_t *out_count);
/* CurrentRev(docId) — server-known rev (0 if never touched). */
int64_t ca_multiplayer_hub_current_rev(ca_multiplayer_hub_t *h, const char *doc_id);
/* ResetStateForTesting — wipe rev + peer state. */
void ca_multiplayer_hub_reset(ca_multiplayer_hub_t *h);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_HOST_MULTIPLAYER_H */
