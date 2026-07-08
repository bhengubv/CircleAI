/*
 * test_host_multiplayer.c — CircleAI.Hosting.Multiplayer (C11 port).
 *
 * Verifies ColourFor (stable HSL hash), GuestPeerIdentity, and MultiplayerHub
 * (connect/join/cursor/edit LWW-by-rev/leave/disconnect + presence + rev
 * arbitration), including the emitted-event fan-out.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_colour_for(void) {
    /* empty -> fixed fallback */
    char *c = ca_multiplayer_colour_for("");
    assert(strcmp(c, "#5a4fcf") == 0);
    free(c);

    /* deterministic + stable for the same id */
    char *a = ca_multiplayer_colour_for("alice");
    char *a2 = ca_multiplayer_colour_for("alice");
    assert(strcmp(a, a2) == 0);
    assert(strncmp(a, "hsl(", 4) == 0);
    assert(strstr(a, "70%") && strstr(a, "55%"));
    free(a); free(a2);

    /* different ids usually differ */
    char *b = ca_multiplayer_colour_for("bob");
    char *aa = ca_multiplayer_colour_for("alice");
    /* not guaranteed different, but for these two it is */
    assert(strcmp(aa, b) != 0);
    free(b); free(aa);

    /* verify hue formula for a known short id: "A" -> h = 65 -> hue 65 */
    char *ca = ca_multiplayer_colour_for("A");
    assert(strcmp(ca, "hsl(65, 70%, 55%)") == 0);
    free(ca);
    printf("  colour for: ok\n");
}

static void test_guest_identity(void) {
    ca_guest_peer_identity_t g;
    ca_guest_peer_identity_init(&g, NULL, NULL);
    assert(strlen(g.peer_id) == 32); /* deterministic hex */
    assert(strcmp(g.display_name, "Guest") == 0);
    ca_guest_peer_identity_free(&g);

    ca_guest_peer_identity_init(&g, "peer-7", "Alice");
    assert(strcmp(g.peer_id, "peer-7") == 0 && strcmp(g.display_name, "Alice") == 0);
    ca_guest_peer_identity_free(&g);
    printf("  guest identity: ok\n");
}

/* capture emitted events */
static int g_events = 0;
static char g_last_event[32];
static char g_last_args[256];
static void emit(void *u, const char *event, const char *doc, const char *args) {
    (void)u; (void)doc;
    g_events++;
    snprintf(g_last_event, sizeof(g_last_event), "%s", event);
    snprintf(g_last_args, sizeof(g_last_args), "%s", args ? args : "");
}

static void test_hub(void) {
    ca_multiplayer_hub_t *h = ca_multiplayer_hub_create(emit, NULL);

    ca_peer_identity_t alice = { "alice", "Alice" };
    ca_peer_identity_t bob   = { "bob", "Bob" };
    ca_multiplayer_hub_on_connected(h, "conn-A", &alice);
    ca_multiplayer_hub_on_connected(h, "conn-B", &bob);

    /* join document */
    g_events = 0;
    ca_multiplayer_hub_join_document(h, "conn-A", "doc1");
    assert(g_events == 1 && strcmp(g_last_event, "PeerJoined") == 0);
    assert(strstr(g_last_args, "doc1") && strstr(g_last_args, "Alice"));
    ca_multiplayer_hub_join_document(h, "conn-B", "doc1");

    /* presence */
    size_t n = 0;
    ca_peer_state_t *peers = ca_multiplayer_hub_peers(h, "doc1", &n);
    assert(n == 2);
    ca_peer_state_free_array(peers, n);

    /* cursor */
    g_events = 0;
    ca_multiplayer_hub_send_cursor(h, "conn-A", "doc1", 3, 7);
    assert(g_events == 1 && strcmp(g_last_event, "CursorChanged") == 0);
    assert(strstr(g_last_args, "3,7"));

    /* edit: rev 1 accepted (new doc -> max(rev,1)=1) */
    assert(ca_multiplayer_hub_current_rev(h, "doc1") == 0);
    g_events = 0;
    int64_t r = ca_multiplayer_hub_send_edit(h, "conn-A", "doc1", "hello", 1);
    assert(r == 1);
    assert(ca_multiplayer_hub_current_rev(h, "doc1") == 1);
    assert(g_events == 1 && strcmp(g_last_event, "EditApplied") == 0);
    assert(strstr(g_last_args, "hello"));

    /* edit rev 5 accepted (> current) */
    r = ca_multiplayer_hub_send_edit(h, "conn-B", "doc1", "world", 5);
    assert(r == 5 && ca_multiplayer_hub_current_rev(h, "doc1") == 5);

    /* stale edit rev 3 (< current 5) rejected -> returns current rev, no emit */
    g_events = 0;
    r = ca_multiplayer_hub_send_edit(h, "conn-A", "doc1", "stale", 3);
    assert(r == 5); /* server rev */
    assert(g_events == 0); /* no EditApplied broadcast */
    assert(ca_multiplayer_hub_current_rev(h, "doc1") == 5);

    /* leave document */
    g_events = 0;
    ca_multiplayer_hub_leave_document(h, "conn-A", "doc1");
    assert(g_events == 1 && strcmp(g_last_event, "PeerLeft") == 0);
    peers = ca_multiplayer_hub_peers(h, "doc1", &n);
    assert(n == 1); /* only bob remains */
    ca_peer_state_free_array(peers, n);

    /* disconnect bob (in doc) -> emits PeerLeft */
    g_events = 0;
    ca_multiplayer_hub_on_disconnected(h, "conn-B");
    assert(g_events == 1 && strcmp(g_last_event, "PeerLeft") == 0);
    peers = ca_multiplayer_hub_peers(h, "doc1", &n);
    assert(n == 0 && peers == NULL);

    /* reset wipes rev state */
    ca_multiplayer_hub_reset(h);
    assert(ca_multiplayer_hub_current_rev(h, "doc1") == 0);

    ca_multiplayer_hub_destroy(h);
    printf("  hub: ok\n");
}

int main(void) {
    test_colour_for();
    test_guest_identity();
    test_hub();
    printf("test_host_multiplayer: all assertions passed\n");
    return 0;
}
