/*
 * test_net_aether.c — CircleAI.Networking.AetherNet (net_aether.h).
 *
 * Verifies:
 *   AetherPeer          : new/copy/deep-copy of capabilities + friendly name
 *   Registry            : Register LWW, GetPeer, Peers ordered by PeerId,
 *                         RecordHop + AvgRoundTripMs, RecordPacket +
 *                         RecentPackets (desc by AtUtc, Take(limit)) +
 *                         TotalBytesBetween
 *   IAetherContext      : in-memory install-level / availability / IsSufficient
 *                         / RequiresAuth semantics
 *   AetherNetworkTransport : Kind==Aether, IsAvailable mirrors context, inject +
 *                         drain unbounded inbound, StopAsync completes channel,
 *                         Send is a completed no-op
 *   AetherPeerDiscovery : empty Discover, Announce retains last
 *   AetherSyncChannel   : last-sequence tracking, empty receive, push no-op
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <stdlib.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

#define T0 1700000000000LL

static void test_peer(void) {
    const char *caps[] = { "chat", "voice" };
    ca_aether_peer_t *p = ca_aether_peer_new(
        "peer-1", CA_AETHER_PEER_LAPTOP, "Alice", caps, 2);
    assert(p);
    assert(strcmp(p->peer_id, "peer-1") == 0);
    assert(p->kind == CA_AETHER_PEER_LAPTOP);
    assert(strcmp(p->friendly_name, "Alice") == 0);
    assert(p->capability_count == 2);
    assert(strcmp(p->capabilities[1], "voice") == 0);

    ca_aether_peer_t *c = ca_aether_peer_copy(p);
    assert(c && c != p);
    assert(c->capabilities != p->capabilities);
    assert(strcmp(c->capabilities[0], "chat") == 0);
    ca_aether_peer_destroy(c);

    /* null friendly name allowed */
    ca_aether_peer_t *p2 = ca_aether_peer_new("p2", CA_AETHER_PEER_IOT, NULL,
                                              NULL, 0);
    assert(p2 && p2->friendly_name == NULL && p2->capability_count == 0);
    ca_aether_peer_destroy(p2);
    ca_aether_peer_destroy(p);
}

static void test_registry(void) {
    ca_aethernet_registry_t *r = ca_aethernet_registry_create();
    assert(r);

    const char *caps[] = { "x" };
    ca_aether_peer_t *pb = ca_aether_peer_new("bravo", CA_AETHER_PEER_PHONE,
                                              "B", caps, 1);
    ca_aether_peer_t *pa = ca_aether_peer_new("alpha", CA_AETHER_PEER_PHONE,
                                              "A", caps, 1);
    assert(ca_aethernet_registry_register(r, pb) == 0);
    assert(ca_aethernet_registry_register(r, pa) == 0);
    ca_aether_peer_destroy(pa);
    ca_aether_peer_destroy(pb);

    /* GetPeer */
    ca_aether_peer_t *got = ca_aethernet_registry_get_peer(r, "alpha");
    assert(got && strcmp(got->friendly_name, "A") == 0);
    ca_aether_peer_destroy(got);
    assert(ca_aethernet_registry_get_peer(r, "nope") == NULL);

    /* Peers ordered by PeerId ascending: alpha, bravo. */
    ca_aether_peer_t **peers = NULL;
    size_t n = 0;
    assert(ca_aethernet_registry_peers(r, &peers, &n) == 0);
    assert(n == 2);
    assert(strcmp(peers[0]->peer_id, "alpha") == 0);
    assert(strcmp(peers[1]->peer_id, "bravo") == 0);
    for (size_t i = 0; i < n; ++i) ca_aether_peer_destroy(peers[i]);
    free(peers);

    /* LWW re-register bravo with a new friendly name. */
    ca_aether_peer_t *pb2 = ca_aether_peer_new("bravo", CA_AETHER_PEER_EDGE,
                                               "B2", NULL, 0);
    assert(ca_aethernet_registry_register(r, pb2) == 0);
    ca_aether_peer_destroy(pb2);
    got = ca_aethernet_registry_get_peer(r, "bravo");
    assert(got && strcmp(got->friendly_name, "B2") == 0 &&
           got->kind == CA_AETHER_PEER_EDGE);
    ca_aether_peer_destroy(got);

    /* Hop telemetry: avg round trip. */
    assert(ca_aethernet_registry_avg_round_trip_ms(r, "alpha") == 0.0);
    ca_aethernet_registry_record_hop(r, "alpha", 2, 10.0, T0);
    ca_aethernet_registry_record_hop(r, "alpha", 3, 20.0, T0 + 1);
    ca_aethernet_registry_record_hop(r, "bravo", 1, 100.0, T0 + 2);
    assert(ca_aethernet_registry_avg_round_trip_ms(r, "alpha") == 15.0);
    assert(ca_aethernet_registry_avg_round_trip_ms(r, "bravo") == 100.0);

    /* Packets: recent (desc by AtUtc) + total bytes between. */
    ca_aethernet_registry_record_packet(r, "pk1", "alpha", "bravo", 100,
                                        "data", T0 + 10);
    ca_aethernet_registry_record_packet(r, "pk2", "alpha", "bravo", 50,
                                        "data", T0 + 30);
    ca_aethernet_registry_record_packet(r, "pk3", "bravo", "alpha", 7,
                                        "ack", T0 + 20);
    assert(ca_aethernet_registry_total_bytes_between(r, "alpha", "bravo") == 150);
    assert(ca_aethernet_registry_total_bytes_between(r, "bravo", "alpha") == 7);

    size_t pc = 0;
    ca_aether_packet_summary_t *pk =
        ca_aethernet_registry_recent_packets(r, 2, &pc);
    assert(pk && pc == 2);
    /* newest first: pk2 (T0+30), pk3 (T0+20). */
    assert(strcmp(pk[0].packet_id, "pk2") == 0);
    assert(strcmp(pk[1].packet_id, "pk3") == 0);
    ca_aether_packet_summary_free_array(pk, pc);

    /* limit larger than count returns all 3. */
    pk = ca_aethernet_registry_recent_packets(r, 100, &pc);
    assert(pk && pc == 3);
    assert(strcmp(pk[0].packet_id, "pk2") == 0); /* newest */
    ca_aether_packet_summary_free_array(pk, pc);

    ca_aethernet_registry_destroy(r);
}

static void test_context(void) {
    /* Reuses aether.h ca_aether_context_impl_*: IsAvailable = level!=None &&
     * enabled; IsSufficient = !has_min || (has_rt && rt>=min); RequiresAuth =
     * level==OS. Version is {major,minor,build,revision} with -1 for unset. */
    ca_aether_version_t rt = { 2, 5, 0, -1 };
    ca_aether_version_t min = { 2, 0, -1, -1 };
    ca_aether_context_impl_t *c = ca_aether_context_impl_create(
        CA_AETHER_INSTALL_OS, true, rt, true, min, /*enabled*/ true);
    assert(c);
    ca_aether_context_t v = ca_aether_context_impl_as_context(c);
    assert(v.install_level(v.self) == CA_AETHER_INSTALL_OS);
    assert(v.is_available(v.self) == true);    /* OS + enabled */
    assert(v.is_sufficient(v.self) == true);   /* 2.5 >= 2.0 */
    assert(v.requires_auth(v.self) == true);   /* OS-managed */
    assert(v.is_enabled(v.self) == true);

    /* runtime < minimum => not sufficient. */
    ca_aether_version_t rt2 = { 1, 9, 0, -1 };
    ca_aether_context_impl_t *c2 = ca_aether_context_impl_create(
        CA_AETHER_INSTALL_APP, true, rt2, true, min, /*enabled*/ false);
    ca_aether_context_t v2 = ca_aether_context_impl_as_context(c2);
    assert(v2.is_sufficient(v2.self) == false);
    assert(v2.requires_auth(v2.self) == false); /* App, not OS */

    /* null minimum => always sufficient. */
    ca_aether_version_t none = { -1, -1, -1, -1 };
    ca_aether_context_impl_t *c3 = ca_aether_context_impl_create(
        CA_AETHER_INSTALL_NONE, false, none, false, none, /*enabled*/ false);
    ca_aether_context_t v3 = ca_aether_context_impl_as_context(c3);
    assert(v3.is_sufficient(v3.self) == true);

    ca_aether_context_impl_destroy(c);
    ca_aether_context_impl_destroy(c2);
    ca_aether_context_impl_destroy(c3);
}

static void test_transport(void) {
    ca_aether_version_t v0 = { -1, -1, -1, -1 };
    /* App + disabled => not available; toggle enabled => available. */
    ca_aether_context_impl_t *ctx = ca_aether_context_impl_create(
        CA_AETHER_INSTALL_APP, false, v0, false, v0, /*enabled*/ false);
    ca_aether_transport_t *t =
        ca_aether_transport_create(ca_aether_context_impl_as_context(ctx));
    assert(t);
    ca_network_transport_t nt = ca_aether_transport_as_transport(t);

    assert(nt.kind(nt.self) == CA_TRANSPORT_AETHER);
    assert(nt.is_available(nt.self) == false);
    ca_aether_context_impl_set_enabled(ctx, true);
    assert(nt.is_available(nt.self) == true);

    assert(nt.start(nt.self) == 0);

    /* Send is a completed no-op (does not enqueue inbound). */
    const uint8_t body[] = { 9, 8, 7 };
    ca_network_payload_t *p = ca_network_payload_create(
        body, sizeof(body), "dst", CA_MSG_PRIORITY_EMERGENCY, NULL, false, 0,
        T0, NULL);
    assert(p);
    assert(nt.send(nt.self, p) == 0);
    assert(ca_aether_transport_pending(t) == 0);

    /* Inject two inbound payloads (the mesh-received seam) and drain in FIFO. */
    assert(ca_aether_transport_inject(t, p) == 0);
    ca_network_payload_t *p2 = ca_network_payload_create(
        body, 2, "dst2", CA_MSG_PRIORITY_LOW, NULL, false, 0, T0, NULL);
    assert(ca_aether_transport_inject(t, p2) == 0);
    assert(ca_aether_transport_pending(t) == 2);

    ca_network_payload_t *out = NULL;
    assert(nt.receive_next(nt.self, &out) && out);
    assert(strcmp(out->destination_id, "dst") == 0);
    ca_network_payload_destroy(out);
    assert(nt.receive_next(nt.self, &out) && out);
    assert(strcmp(out->destination_id, "dst2") == 0);
    ca_network_payload_destroy(out);
    assert(nt.receive_next(nt.self, &out) == false);

    /* StopAsync completes the channel: further inject fails. */
    assert(nt.stop(nt.self) == 0);
    assert(ca_aether_transport_inject(t, p) == -1);

    ca_network_payload_destroy(p);
    ca_network_payload_destroy(p2);
    ca_aether_transport_destroy(t);
    ca_aether_context_impl_destroy(ctx);
}

static void test_discovery_and_sync(void) {
    ca_aether_version_t v0 = { -1, -1, -1, -1 };
    ca_aether_context_impl_t *ctx = ca_aether_context_impl_create(
        CA_AETHER_INSTALL_APP, false, v0, false, v0, /*enabled*/ true);
    ca_aether_context_t cv = ca_aether_context_impl_as_context(ctx);

    /* Discovery: empty stream, announce retains last. */
    ca_aether_discovery_t *d = ca_aether_discovery_create(cv);
    assert(d);
    ca_peer_info_t *pi = NULL;
    assert(ca_aether_discovery_discover_next(d, &pi) == false);
    assert(ca_aether_discovery_last_announced(d) == NULL);

    ca_transport_kind_t supp[] = { CA_TRANSPORT_AETHER };
    ca_peer_info_t *local = ca_peer_info_new("me", "Me", supp, 1,
                                             CA_PEER_ROLE_PEER, true, -50, T0);
    assert(local);
    assert(ca_aether_discovery_announce(d, local) == 0);
    const ca_peer_info_t *last = ca_aether_discovery_last_announced(d);
    assert(last && strcmp(last->node_id, "me") == 0);
    ca_peer_info_destroy(local);
    ca_aether_discovery_destroy(d);

    /* Sync channel: sequences default 0, set + read; empty receive; push ok. */
    ca_aether_sync_channel_t *s = ca_aether_sync_channel_create(cv);
    assert(s);
    assert(ca_aether_sync_channel_last_sequence(s, "owner", "memory") == 0);
    assert(ca_aether_sync_channel_set_sequence(s, "owner", "memory", 42) == 0);
    assert(ca_aether_sync_channel_last_sequence(s, "owner", "memory") == 42);
    /* different domain still 0 */
    assert(ca_aether_sync_channel_last_sequence(s, "owner", "affect") == 0);
    /* update existing */
    assert(ca_aether_sync_channel_set_sequence(s, "owner", "memory", 99) == 0);
    assert(ca_aether_sync_channel_last_sequence(s, "owner", "memory") == 99);

    const uint8_t pb[] = { 1 };
    ca_net_sync_delta_t *delta = ca_net_sync_delta_new(
        "owner", "devA", "", "memory", pb, 1, 7, CA_NET_DELIVERY_GUARANTEED,
        false, 0, T0, NULL);
    assert(delta);
    assert(ca_aether_sync_channel_push_delta(s, delta) == 0);
    ca_net_sync_delta_t *rx = NULL;
    assert(ca_aether_sync_channel_receive_next(s, "owner", &rx) == false);
    ca_net_sync_delta_destroy(delta);
    ca_aether_sync_channel_destroy(s);

    ca_aether_context_impl_destroy(ctx);
}

int main(void) {
    test_peer();
    test_registry();
    test_context();
    test_transport();
    test_discovery_and_sync();
    return 0;
}
