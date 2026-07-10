/*
 * test_networking.c — CircleAI.Networking core abstraction (networking.h).
 *
 * Verifies:
 *   Records      : NetworkPayload create/new/copy/metadata + Guid "N";
 *                  NetworkContext new/copy/Offline/Supports; PeerInfo copy
 *   Policy       : DefaultNetworkPolicy permissive flags; NetworkPolicyBuilder
 *                  allow-set / no-cloud / force / disable-queue / mesh-first
 *   Transport    : loopback INetworkTransport — start-gated send/receive FIFO
 *   Mesh         : in-memory IMeshNetwork — peer set + default-Offline health
 *   Channel      : in-memory IMessageChannel — subscribe-before-publish,
 *                  publish-before-subscribe backlog retention (no lost msg),
 *                  multi-subscriber fan-out, unsubscribe
 *   Connectivity : in-memory IConnectivityMonitor — snapshot + WatchAsync
 *                  fan-out; only post-subscribe snapshots delivered
 *   Selector     : default cascade order, availability filter, ForceTransport
 *                  short-circuit, no-cloud filter, disable-queue floor,
 *                  MeshFirst hoist, SelectBest floor
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <stdlib.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

#define T0 1700000000000LL

/* ---------------------------------------------------------------------------
 * NetworkPayload / NetworkContext / PeerInfo records
 * --------------------------------------------------------------------------- */
static void test_records(void) {
    /* Guid "N": 32 lowercase hex, no dashes. */
    char g[33];
    ca_net_new_guid_n(g);
    assert(strlen(g) == 32);
    for (size_t i = 0; i < 32; ++i) {
        char ch = g[i];
        assert((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'));
    }

    /* Create: default content-type, null source, empty metadata. */
    const uint8_t body[] = { 1, 2, 3, 4 };
    ca_network_payload_t *p = ca_network_payload_create(
        body, sizeof(body), "dest-1", CA_MSG_PRIORITY_HIGH, NULL,
        true, 5000, T0, NULL);
    assert(p);
    assert(p->source_id == NULL);
    assert(strcmp(p->destination_id, "dest-1") == 0);
    assert(strcmp(p->content_type, "application/octet-stream") == 0);
    assert(p->priority == CA_MSG_PRIORITY_HIGH);
    assert(p->has_ttl && p->ttl_ms == 5000);
    assert(p->data_len == 4 && p->data[2] == 3);
    assert(strlen(p->id) == 32);
    assert(p->metadata_count == 0);

    /* Copy is deep. */
    ca_network_payload_t *p2 = ca_network_payload_copy(p);
    assert(p2 && p2->id != p->id && strcmp(p2->id, p->id) == 0);
    assert(p2->data != p->data && p2->data_len == 4 && p2->data[0] == 1);
    ca_network_payload_destroy(p);
    ca_network_payload_destroy(p2);

    /* Full ctor with metadata + explicit id + source. */
    ca_net_metadata_pair_t md[2] = {
        { (char *)"trace", (char *)"abc" },
        { (char *)"lang",  (char *)"zu"  },
    };
    ca_network_payload_t *p3 = ca_network_payload_new(
        "id-xyz", "src-A", "dst-B", body, sizeof(body), CA_MSG_PRIORITY_URGENT,
        false, 0, "text/plain", md, 2, T0);
    assert(p3 && strcmp(p3->id, "id-xyz") == 0);
    assert(strcmp(p3->source_id, "src-A") == 0);
    assert(!p3->has_ttl);
    assert(strcmp(ca_network_payload_metadata(p3, "trace"), "abc") == 0);
    assert(strcmp(ca_network_payload_metadata(p3, "lang"), "zu") == 0);
    assert(ca_network_payload_metadata(p3, "missing") == NULL);
    ca_network_payload_t *p4 = ca_network_payload_copy(p3);
    assert(p4 && p4->metadata_count == 2);
    assert(strcmp(ca_network_payload_metadata(p4, "lang"), "zu") == 0);
    ca_network_payload_destroy(p3);
    ca_network_payload_destroy(p4);

    /* Empty payload round-trips with a distinct owned buffer. */
    ca_network_payload_t *pe = ca_network_payload_create(
        NULL, 0, NULL, CA_MSG_PRIORITY_LOW, "application/json",
        false, 0, T0, "e1");
    assert(pe && pe->data_len == 0 && pe->data != NULL);
    assert(pe->destination_id == NULL);
    ca_network_payload_destroy(pe);

    /* NetworkContext new + supports + copy. */
    ca_transport_kind_t avail[] = { CA_TRANSPORT_WIFI, CA_TRANSPORT_AETHER };
    ca_network_context_t *c = ca_network_context_new(
        CA_CONNECTIVITY_MESH_ONLY, CA_TRANSPORT_AETHER, avail, 2,
        true, -55, true, 2000000, true, 42, 3, T0);
    assert(c);
    assert(c->state == CA_CONNECTIVITY_MESH_ONLY);
    assert(c->has_signal_strength && c->signal_strength_dbm == -55);
    assert(c->has_bandwidth && c->estimated_bandwidth_bps == 2000000);
    assert(c->has_latency && c->latency_ms == 42);
    assert(c->nearby_peer_count == 3);
    assert(ca_network_context_supports(c, CA_TRANSPORT_WIFI));
    assert(ca_network_context_supports(c, CA_TRANSPORT_AETHER));
    assert(!ca_network_context_supports(c, CA_TRANSPORT_HTTP));
    ca_network_context_t *c2 = ca_network_context_copy(c);
    assert(c2 && c2->available_count == 2 &&
           c2->available_transports != c->available_transports);
    assert(ca_network_context_supports(c2, CA_TRANSPORT_AETHER));
    ca_network_context_destroy(c);
    ca_network_context_destroy(c2);

    /* NetworkContext.Offline. */
    ca_network_context_t *off = ca_network_context_offline(T0);
    assert(off && off->state == CA_CONNECTIVITY_OFFLINE);
    assert(off->preferred_transport == CA_TRANSPORT_LOCAL_STORE);
    assert(off->available_count == 0);
    assert(!off->has_signal_strength && !off->has_bandwidth && !off->has_latency);
    assert(off->nearby_peer_count == 0);
    ca_network_context_destroy(off);

    /* PeerInfo copy + optional display name. */
    ca_transport_kind_t sup[] = { CA_TRANSPORT_BLUETOOTH };
    ca_peer_info_t *pi = ca_peer_info_new(
        "peer-1", "Alice", sup, 1, CA_PEER_ROLE_RELAY, true, -70, T0);
    assert(pi && strcmp(pi->node_id, "peer-1") == 0);
    assert(strcmp(pi->display_name, "Alice") == 0);
    assert(pi->role == CA_PEER_ROLE_RELAY);
    assert(pi->has_signal_strength && pi->signal_strength_dbm == -70);
    ca_peer_info_t *pi2 = ca_peer_info_copy(pi);
    assert(pi2 && pi2->supported_count == 1 &&
           pi2->supported_transports[0] == CA_TRANSPORT_BLUETOOTH);
    ca_peer_info_destroy(pi);
    ca_peer_info_destroy(pi2);

    ca_peer_info_t *pi3 = ca_peer_info_new(
        "peer-2", NULL, NULL, 0, CA_PEER_ROLE_PEER, false, 0, T0);
    assert(pi3 && pi3->display_name == NULL && pi3->supported_count == 0);
    assert(!pi3->has_signal_strength);
    ca_peer_info_destroy(pi3);
}

/* ---------------------------------------------------------------------------
 * DefaultNetworkPolicy + NetworkPolicyBuilder
 * --------------------------------------------------------------------------- */
static void test_policy(void) {
    ca_network_policy_t d = ca_default_network_policy();
    ca_transport_kind_t forced;
    assert(d.permits(d.self, CA_TRANSPORT_HTTP, NULL));
    assert(d.permits(d.self, CA_TRANSPORT_AETHER, NULL));
    assert(!d.force_transport(d.self, &forced)); /* null */
    assert(!d.mesh_first(d.self));
    assert(d.offline_queue_enabled(d.self));
    assert(d.allow_cloud_transports(d.self));

    /* Builder: allow-set restricts to listed kinds. */
    ca_network_policy_builder_t *b = ca_network_policy_builder_create();
    ca_transport_kind_t allow[] = { CA_TRANSPORT_WIFI, CA_TRANSPORT_AETHER };
    ca_network_policy_builder_allow(b, allow, 2);
    ca_network_policy_builder_mesh_first(b);
    ca_network_policy_impl_t *impl = ca_network_policy_builder_build(b);
    ca_network_policy_builder_destroy(b); /* impl is independent */
    assert(impl);
    ca_network_policy_t p = ca_network_policy_impl_as_policy(impl);
    assert(p.permits(p.self, CA_TRANSPORT_WIFI, NULL));
    assert(p.permits(p.self, CA_TRANSPORT_AETHER, NULL));
    assert(!p.permits(p.self, CA_TRANSPORT_HTTP, NULL)); /* not in allow-set */
    assert(p.mesh_first(p.self));
    assert(p.offline_queue_enabled(p.self)); /* default true */
    assert(p.allow_cloud_transports(p.self)); /* no NoCloud */
    assert(!p.force_transport(p.self, &forced));
    ca_network_policy_impl_destroy(impl);

    /* Builder: no-cloud blocks the 4 cloud kinds; empty allow-set => permit rest. */
    ca_network_policy_builder_t *b2 = ca_network_policy_builder_create();
    ca_network_policy_builder_no_cloud(b2);
    ca_network_policy_builder_disable_queue(b2);
    ca_network_policy_impl_t *impl2 = ca_network_policy_builder_build(b2);
    ca_network_policy_builder_destroy(b2);
    ca_network_policy_t p2 = ca_network_policy_impl_as_policy(impl2);
    assert(!p2.permits(p2.self, CA_TRANSPORT_HTTP, NULL));
    assert(!p2.permits(p2.self, CA_TRANSPORT_WEBSOCKET, NULL));
    assert(!p2.permits(p2.self, CA_TRANSPORT_GRPC, NULL));
    assert(!p2.permits(p2.self, CA_TRANSPORT_MQTT, NULL));
    assert(p2.permits(p2.self, CA_TRANSPORT_TCP, NULL));   /* non-cloud allowed */
    assert(p2.permits(p2.self, CA_TRANSPORT_AETHER, NULL));
    assert(!p2.allow_cloud_transports(p2.self));
    assert(!p2.offline_queue_enabled(p2.self)); /* DisableQueue */
    ca_network_policy_impl_destroy(impl2);

    /* Builder: force. */
    ca_network_policy_builder_t *b3 = ca_network_policy_builder_create();
    ca_network_policy_builder_force(b3, CA_TRANSPORT_TCP);
    ca_network_policy_impl_t *impl3 = ca_network_policy_builder_build(b3);
    ca_network_policy_builder_destroy(b3);
    ca_network_policy_t p3 = ca_network_policy_impl_as_policy(impl3);
    assert(p3.force_transport(p3.self, &forced) && forced == CA_TRANSPORT_TCP);
    ca_network_policy_impl_destroy(impl3);
}

/* ---------------------------------------------------------------------------
 * Loopback INetworkTransport
 * --------------------------------------------------------------------------- */
static void test_transport(void) {
    ca_loopback_transport_t *lt = ca_loopback_transport_create(CA_TRANSPORT_TCP);
    assert(lt);
    ca_network_transport_t t = ca_loopback_transport_as_transport(lt);
    assert(t.kind(t.self) == CA_TRANSPORT_TCP);

    /* Not available / send fails before StartAsync. */
    assert(!t.is_available(t.self));
    ca_network_payload_t *p1 = ca_network_payload_create(
        (const uint8_t *)"a", 1, "d", CA_MSG_PRIORITY_NORMAL, NULL,
        false, 0, T0, "m1");
    assert(t.send(t.self, p1) == -1);

    /* Start, then send/receive FIFO order. */
    assert(t.start(t.self) == 0);
    assert(t.is_available(t.self));
    assert(t.send(t.self, p1) == 0);
    ca_network_payload_t *p2 = ca_network_payload_create(
        (const uint8_t *)"bb", 2, "d", CA_MSG_PRIORITY_NORMAL, NULL,
        false, 0, T0, "m2");
    assert(t.send(t.self, p2) == 0);
    assert(ca_loopback_transport_pending(lt) == 2);

    ca_network_payload_t *out = NULL;
    assert(t.receive_next(t.self, &out) && out);
    assert(strcmp(out->id, "m1") == 0 && out->data_len == 1);
    ca_network_payload_destroy(out);
    out = NULL;
    assert(t.receive_next(t.self, &out) && out);
    assert(strcmp(out->id, "m2") == 0 && out->data_len == 2);
    ca_network_payload_destroy(out);
    /* Drained. */
    assert(!t.receive_next(t.self, &out));
    assert(ca_loopback_transport_pending(lt) == 0);

    /* Stop => unavailable; receive drains nothing further. */
    assert(t.stop(t.self) == 0);
    assert(!t.is_available(t.self));
    assert(!t.receive_next(t.self, &out));

    ca_network_payload_destroy(p1);
    ca_network_payload_destroy(p2);
    ca_loopback_transport_destroy(lt);
}

/* ---------------------------------------------------------------------------
 * In-memory IMeshNetwork
 * --------------------------------------------------------------------------- */
static void test_mesh(void) {
    ca_mem_mesh_t *m = ca_mem_mesh_create("local-node");
    assert(m);
    ca_mesh_network_t mesh = ca_mem_mesh_as_mesh(m);
    assert(strcmp(mesh.local_node_id(mesh.self), "local-node") == 0);

    /* Empty peers. */
    char **ids = (char **)0x1;
    size_t n = 99;
    assert(mesh.peer_ids(mesh.self, &ids, &n) == 0);
    assert(ids == NULL && n == 0);

    /* Add peers (set semantics — duplicate ignored). */
    assert(ca_mem_mesh_add_peer(m, "p1") == 0);
    assert(ca_mem_mesh_add_peer(m, "p2") == 0);
    assert(ca_mem_mesh_add_peer(m, "p1") == 0); /* dup */
    assert(mesh.peer_ids(mesh.self, &ids, &n) == 0);
    assert(n == 2 && ids);
    /* Owned deep copies. */
    bool saw1 = false, saw2 = false;
    for (size_t i = 0; i < n; ++i) {
        if (strcmp(ids[i], "p1") == 0) saw1 = true;
        if (strcmp(ids[i], "p2") == 0) saw2 = true;
        free(ids[i]);
    }
    free(ids);
    assert(saw1 && saw2);

    /* Remove. */
    ca_mem_mesh_remove_peer(m, "p1");
    assert(mesh.peer_ids(mesh.self, &ids, &n) == 0);
    assert(n == 1 && strcmp(ids[0], "p2") == 0);
    free(ids[0]);
    free(ids);

    /* Health defaults to Offline until set. */
    ca_network_context_t *h = NULL;
    assert(mesh.mesh_health(mesh.self, &h) == 0 && h);
    assert(h->state == CA_CONNECTIVITY_OFFLINE);
    ca_network_context_destroy(h);

    /* Set health => returned copy reflects it. */
    ca_transport_kind_t av[] = { CA_TRANSPORT_AETHER };
    ca_network_context_t *set = ca_network_context_new(
        CA_CONNECTIVITY_MESH_ONLY, CA_TRANSPORT_AETHER, av, 1,
        false, 0, false, 0, false, 0, 5, T0);
    assert(ca_mem_mesh_set_health(m, set) == 0);
    ca_network_context_destroy(set); /* mesh kept its own copy */
    assert(mesh.mesh_health(mesh.self, &h) == 0 && h);
    assert(h->state == CA_CONNECTIVITY_MESH_ONLY && h->nearby_peer_count == 5);
    ca_network_context_destroy(h);

    ca_mem_mesh_destroy(m);
}

/* ---------------------------------------------------------------------------
 * In-memory IMessageChannel — pub/sub with backlog retention
 * --------------------------------------------------------------------------- */
static void test_channel(void) {
    ca_mem_channel_t *c = ca_mem_channel_create();
    assert(c);
    ca_message_channel_t ch = ca_mem_channel_as_channel(c);

    /* Subscribe SYNCHRONOUSLY before publishing (no lost-message race). */
    ca_mem_channel_sub_t *s1 = ca_mem_channel_subscribe(c);
    assert(s1);
    assert(ch.send(ch.self, "dst", (const uint8_t *)"hello", 5,
                   "text/plain") == 0);
    assert(ca_mem_channel_sub_pending(s1) == 1);

    ca_channel_message_t m;
    assert(ca_mem_channel_receive_next(s1, &m));
    assert(m.destination_id && strcmp(m.destination_id, "dst") == 0);
    assert(m.len == 5 && memcmp(m.data, "hello", 5) == 0);
    assert(strcmp(m.content_type, "text/plain") == 0);
    ca_channel_message_destroy(&m);
    assert(!ca_mem_channel_receive_next(s1, &m)); /* drained */

    /* Publish-BEFORE-subscribe: an unbounded channel retains the write, so a
     * LATE subscriber still receives it. */
    assert(ch.send(ch.self, "d2", (const uint8_t *)"early", 5, "app/x") == 0);
    /* s1 (already subscribed) also gets it. */
    assert(ca_mem_channel_sub_pending(s1) == 1);
    ca_mem_channel_sub_t *s2 = ca_mem_channel_subscribe(c); /* attaches AFTER */
    assert(s2);
    assert(ca_mem_channel_sub_pending(s2) == 2); /* backlog: "hello" + "early" */

    /* s2 drains the full backlog in order. */
    assert(ca_mem_channel_receive_next(s2, &m));
    assert(strcmp((char *)m.data, "hello") == 0 || m.len == 5);
    assert(m.len == 5 && memcmp(m.data, "hello", 5) == 0);
    ca_channel_message_destroy(&m);
    assert(ca_mem_channel_receive_next(s2, &m));
    assert(m.len == 5 && memcmp(m.data, "early", 5) == 0);
    ca_channel_message_destroy(&m);
    assert(!ca_mem_channel_receive_next(s2, &m));

    /* Fan-out to BOTH live subscriptions. */
    assert(ch.send(ch.self, NULL, (const uint8_t *)"bcast", 5, "app/y") == 0);
    assert(ca_mem_channel_receive_next(s1, &m)); /* s1 had "early" pending too */
    /* s1's next-in-line is "early" (published before s2 attached). */
    assert(m.len == 5 && memcmp(m.data, "early", 5) == 0);
    ca_channel_message_destroy(&m);
    assert(ca_mem_channel_receive_next(s1, &m));
    assert(m.len == 5 && memcmp(m.data, "bcast", 5) == 0);
    assert(m.destination_id == NULL); /* broadcast: null destination preserved */
    ca_channel_message_destroy(&m);

    assert(ca_mem_channel_receive_next(s2, &m));
    assert(m.len == 5 && memcmp(m.data, "bcast", 5) == 0);
    ca_channel_message_destroy(&m);

    /* Unsubscribe s1; further sends only reach s2. */
    ca_mem_channel_unsubscribe(c, s1);
    assert(ch.send(ch.self, "d3", (const uint8_t *)"after", 5, "app/z") == 0);
    assert(ca_mem_channel_receive_next(s2, &m));
    assert(m.len == 5 && memcmp(m.data, "after", 5) == 0);
    ca_channel_message_destroy(&m);

    ca_mem_channel_unsubscribe(c, s2);
    ca_mem_channel_destroy(c);
}

/* ---------------------------------------------------------------------------
 * In-memory IConnectivityMonitor — WatchAsync fan-out
 * --------------------------------------------------------------------------- */
static void test_connectivity(void) {
    ca_network_context_t *init = ca_network_context_offline(T0);
    ca_mem_connectivity_t *mon = ca_mem_connectivity_create(init);
    ca_network_context_destroy(init); /* monitor kept its own copy */
    assert(mon);

    ca_connectivity_monitor_t cm = ca_mem_connectivity_as_monitor(mon);
    assert(cm.current_state(cm.self) == CA_CONNECTIVITY_OFFLINE);
    ca_network_context_t *snap = NULL;
    assert(cm.snapshot(cm.self, &snap) == 0 && snap);
    assert(snap->state == CA_CONNECTIVITY_OFFLINE);
    ca_network_context_destroy(snap);

    /* Start watching. Only snapshots pushed AFTER this are delivered. */
    ca_mem_connectivity_sub_t *w = ca_mem_connectivity_watch(mon);
    assert(w);
    ca_network_context_t *out = NULL;
    assert(!ca_mem_connectivity_watch_next(w, &out)); /* nothing yet */

    /* Push an Online snapshot. */
    ca_transport_kind_t av[] = { CA_TRANSPORT_GRPC, CA_TRANSPORT_HTTP };
    ca_network_context_t *online = ca_network_context_new(
        CA_CONNECTIVITY_ONLINE, CA_TRANSPORT_GRPC, av, 2,
        true, -40, true, 10000000, true, 12, 0, T0);
    assert(ca_mem_connectivity_push(mon, online) == 0);
    ca_network_context_destroy(online);

    /* Current state updated + watcher received the emission. */
    assert(cm.current_state(cm.self) == CA_CONNECTIVITY_ONLINE);
    assert(ca_mem_connectivity_watch_next(w, &out) && out);
    assert(out->state == CA_CONNECTIVITY_ONLINE && out->available_count == 2);
    assert(ca_network_context_supports(out, CA_TRANSPORT_GRPC));
    ca_network_context_destroy(out);
    assert(!ca_mem_connectivity_watch_next(w, &out)); /* drained */

    /* A watcher that starts AFTER a push misses the earlier one. */
    ca_mem_connectivity_sub_t *w2 = ca_mem_connectivity_watch(mon);
    ca_network_context_t *local = ca_network_context_offline(T0);
    local->state = CA_CONNECTIVITY_LOCAL_ONLY;
    assert(ca_mem_connectivity_push(mon, local) == 0);
    ca_network_context_destroy(local);
    /* w gets exactly the LocalOnly one (Online already drained). */
    assert(ca_mem_connectivity_watch_next(w, &out) && out);
    assert(out->state == CA_CONNECTIVITY_LOCAL_ONLY);
    ca_network_context_destroy(out);
    /* w2 gets only the LocalOnly one (missed Online). */
    assert(ca_mem_connectivity_watch_next(w2, &out) && out);
    assert(out->state == CA_CONNECTIVITY_LOCAL_ONLY);
    ca_network_context_destroy(out);
    assert(!ca_mem_connectivity_watch_next(w2, &out));

    ca_mem_connectivity_unwatch(mon, w);
    ca_mem_connectivity_unwatch(mon, w2);
    ca_mem_connectivity_destroy(mon);
}

/* ---------------------------------------------------------------------------
 * Default ITransportSelector — cascade
 * --------------------------------------------------------------------------- */
static void test_selector_default(void) {
    ca_default_selector_t *sel =
        ca_default_selector_create(ca_default_network_policy());
    assert(sel);
    ca_transport_selector_t ts = ca_default_selector_as_selector(sel);

    ca_network_payload_t *p = ca_network_payload_create(
        (const uint8_t *)"x", 1, "d", CA_MSG_PRIORITY_NORMAL, NULL,
        false, 0, T0, "sid");

    /* No availability list => full base cascade, permissive policy. */
    ca_network_context_t *ctx = ca_network_context_offline(T0);
    /* Offline has empty available list -> treat all as available. */
    size_t n = 0;
    ca_transport_kind_t *casc = ts.get_cascade(ts.self, p, ctx, &n);
    assert(casc && n == 12);
    assert(casc[0] == CA_TRANSPORT_GRPC);
    assert(casc[1] == CA_TRANSPORT_WEBSOCKET);
    assert(casc[2] == CA_TRANSPORT_HTTP);
    assert(casc[3] == CA_TRANSPORT_MQTT);
    assert(casc[4] == CA_TRANSPORT_TCP);
    assert(casc[5] == CA_TRANSPORT_UDP);
    assert(casc[6] == CA_TRANSPORT_WIFI);
    assert(casc[7] == CA_TRANSPORT_BLUETOOTH);
    assert(casc[8] == CA_TRANSPORT_NEARLINK);
    assert(casc[9] == CA_TRANSPORT_AETHER);
    assert(casc[10] == CA_TRANSPORT_DTN);
    assert(casc[11] == CA_TRANSPORT_LOCAL_STORE);
    free(casc);
    /* SelectBest => head of cascade. */
    assert(ts.select_best(ts.self, p, ctx) == CA_TRANSPORT_GRPC);
    ca_network_context_destroy(ctx);

    /* Availability list filters live kinds; DTN/LocalStore always eligible. */
    ca_transport_kind_t av[] = { CA_TRANSPORT_HTTP, CA_TRANSPORT_WIFI };
    ca_network_context_t *ctx2 = ca_network_context_new(
        CA_CONNECTIVITY_ONLINE, CA_TRANSPORT_HTTP, av, 2,
        false, 0, false, 0, false, 0, 0, T0);
    casc = ts.get_cascade(ts.self, p, ctx2, &n);
    assert(casc && n == 4); /* HTTP, WiFi, DTN, LocalStore */
    assert(casc[0] == CA_TRANSPORT_HTTP);
    assert(casc[1] == CA_TRANSPORT_WIFI);
    assert(casc[2] == CA_TRANSPORT_DTN);
    assert(casc[3] == CA_TRANSPORT_LOCAL_STORE);
    free(casc);
    assert(ts.select_best(ts.self, p, ctx2) == CA_TRANSPORT_HTTP);
    ca_network_context_destroy(ctx2);

    ca_network_payload_destroy(p);
    ca_default_selector_destroy(sel);
}

/* Selector honouring a built policy: force / no-cloud / disable-queue / mesh-first. */
static void test_selector_policy(void) {
    ca_network_payload_t *p = ca_network_payload_create(
        (const uint8_t *)"x", 1, NULL, CA_MSG_PRIORITY_NORMAL, NULL,
        false, 0, T0, "sid");
    ca_network_context_t *ctx = ca_network_context_offline(T0); /* no avail list */

    /* Force TCP => cascade [TCP, LocalStore]; SelectBest => TCP. */
    ca_network_policy_builder_t *bf = ca_network_policy_builder_create();
    ca_network_policy_builder_force(bf, CA_TRANSPORT_TCP);
    ca_network_policy_impl_t *implf = ca_network_policy_builder_build(bf);
    ca_network_policy_builder_destroy(bf);
    ca_default_selector_t *sf =
        ca_default_selector_create(ca_network_policy_impl_as_policy(implf));
    ca_transport_selector_t tf = ca_default_selector_as_selector(sf);
    size_t n = 0;
    ca_transport_kind_t *casc = tf.get_cascade(tf.self, p, ctx, &n);
    assert(casc && n == 2);
    assert(casc[0] == CA_TRANSPORT_TCP);
    assert(casc[1] == CA_TRANSPORT_LOCAL_STORE);
    free(casc);
    assert(tf.select_best(tf.self, p, ctx) == CA_TRANSPORT_TCP);
    ca_default_selector_destroy(sf);
    ca_network_policy_impl_destroy(implf);

    /* No-cloud + disable-queue => cloud kinds dropped, LocalStore floor dropped.
     * Remaining floor is DTN (store-and-forward, permitted, needs no live path). */
    ca_network_policy_builder_t *bn = ca_network_policy_builder_create();
    ca_network_policy_builder_no_cloud(bn);
    ca_network_policy_builder_disable_queue(bn);
    ca_network_policy_impl_t *impln = ca_network_policy_builder_build(bn);
    ca_network_policy_builder_destroy(bn);
    ca_default_selector_t *sn =
        ca_default_selector_create(ca_network_policy_impl_as_policy(impln));
    ca_transport_selector_t tn = ca_default_selector_as_selector(sn);
    casc = tn.get_cascade(tn.self, p, ctx, &n);
    assert(casc && n > 0);
    /* First entry must be non-cloud (TCP is first surviving base kind). */
    assert(casc[0] == CA_TRANSPORT_TCP);
    /* LocalStore must NOT appear (queue disabled); DTN must be the last entry. */
    for (size_t i = 0; i < n; ++i)
        assert(casc[i] != CA_TRANSPORT_LOCAL_STORE);
    assert(casc[n - 1] == CA_TRANSPORT_DTN);
    free(casc);
    ca_default_selector_destroy(sn);
    ca_network_policy_impl_destroy(impln);

    /* Mesh-first hoists WiFi/BT/NearLink/Aether ahead of cloud/TCP/UDP. */
    ca_network_policy_builder_t *bm = ca_network_policy_builder_create();
    ca_network_policy_builder_mesh_first(bm);
    ca_network_policy_impl_t *implm = ca_network_policy_builder_build(bm);
    ca_network_policy_builder_destroy(bm);
    ca_default_selector_t *sm =
        ca_default_selector_create(ca_network_policy_impl_as_policy(implm));
    ca_transport_selector_t tm = ca_default_selector_as_selector(sm);
    casc = tm.get_cascade(tm.self, p, ctx, &n);
    assert(casc && n == 12);
    /* First four are the mesh kinds in relative order. */
    assert(casc[0] == CA_TRANSPORT_WIFI);
    assert(casc[1] == CA_TRANSPORT_BLUETOOTH);
    assert(casc[2] == CA_TRANSPORT_NEARLINK);
    assert(casc[3] == CA_TRANSPORT_AETHER);
    /* Then the non-mesh kinds in their original relative order. */
    assert(casc[4] == CA_TRANSPORT_GRPC);
    assert(casc[5] == CA_TRANSPORT_WEBSOCKET);
    assert(casc[6] == CA_TRANSPORT_HTTP);
    assert(casc[7] == CA_TRANSPORT_MQTT);
    assert(casc[8] == CA_TRANSPORT_TCP);
    assert(casc[9] == CA_TRANSPORT_UDP);
    assert(casc[10] == CA_TRANSPORT_DTN);
    assert(casc[11] == CA_TRANSPORT_LOCAL_STORE);
    free(casc);
    /* SelectBest under mesh-first => WiFi. */
    assert(tm.select_best(tm.self, p, ctx) == CA_TRANSPORT_WIFI);
    ca_default_selector_destroy(sm);
    ca_network_policy_impl_destroy(implm);

    ca_network_context_destroy(ctx);
    ca_network_payload_destroy(p);
}

int main(void) {
    test_records();
    test_policy();
    test_transport();
    test_mesh();
    test_channel();
    test_connectivity();
    test_selector_default();
    test_selector_policy();
    return 0;
}
