/*
 * test_companion_sync.c — CircleAI.Memory.Sync (C11 port).
 *
 * Verifies the companion-state sync layer against the C# spec:
 *   - HybridLogicalClock bit-layout, Tick monotonicity, Observe, overflow
 *   - InMemorySyncableEntryStore apply rules (version / tombstone / hash) + since + vector
 *   - InProcessSyncHub broadcast (skip self) + channel subscribe/unsubscribe
 *   - CompanionStateSyncEngine WriteLocal + two-peer convergence
 *   - PersonaState / LoraAdapter / Conversation bridges (publish + decode round-trip)
 *   - base64 round-trip
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* deterministic fake clock */
static int64_t g_fake_ms = 1000;
static int64_t fake_now(void *u) { (void)u; return g_fake_ms; }

/* ── HybridLogicalClock ──────────────────────────────────────────────── */
static void test_hlc(void) {
    /* Compose/Decompose round-trip matches the C# bit layout. */
    int64_t v = ca_hlc_compose(123456789LL, 7, 42);
    int64_t p, l, n;
    ca_hlc_decompose(v, &p, &l, &n);
    assert(p == 123456789LL);
    assert(l == 7);
    assert(n == 42);
    /* masks: logical & 0x3FF, node & 0x3F */
    int64_t v2 = ca_hlc_compose(1, 1024 /* overflows 10 bits */, 64 /* overflows 6 bits */);
    ca_hlc_decompose(v2, &p, &l, &n);
    assert(l == 0);   /* 1024 & 0x3FF == 0 */
    assert(n == 0);   /* 64 & 0x3F == 0 */

    /* node id must be 0..63 */
    assert(ca_hlc_create(-1, NULL, NULL) == NULL);
    assert(ca_hlc_create(64, NULL, NULL) == NULL);

    g_fake_ms = 5000;
    ca_hybrid_logical_clock_t *c = ca_hlc_create(9, fake_now, NULL);
    assert(c);
    /* same physical → logical increments, monotonic */
    int64_t a = ca_hlc_tick(c);
    int64_t b = ca_hlc_tick(c);
    assert(b > a);
    ca_hlc_decompose(a, &p, &l, &n);
    assert(p == 5000 && l == 1 && n == 9);   /* first tick: now==last → logical 0→1 */
    ca_hlc_decompose(b, &p, &l, &n);
    assert(l == 2);
    /* physical advances → logical resets */
    g_fake_ms = 6000;
    int64_t d = ca_hlc_tick(c);
    ca_hlc_decompose(d, &p, &l, &n);
    assert(p == 6000 && l == 0);

    /* Observe folds in a higher incoming physical */
    int64_t incoming = ca_hlc_compose(9000, 3, 1);
    int64_t obs = ca_hlc_observe(c, incoming);
    ca_hlc_decompose(obs, &p, &l, &n);
    assert(p == 9000);        /* max(last=6000, incoming=9000, now=6000) */
    assert(l == 4);           /* maxPhysical==incoming → logical = incLogical+1 = 4 */
    assert(n == 9);           /* node stays local */
    ca_hlc_destroy(c);
    printf("  hlc: ok\n");
}

/* ── InMemorySyncableEntryStore ──────────────────────────────────────── */
static ca_syncable_entry_t mk_entry(const char *type, const char *id, int64_t ver,
                                    bool tomb, const char *hash, const char *payload) {
    ca_syncable_entry_t e; memset(&e, 0, sizeof(e));
    e.entity_type = strdup(type);
    e.entity_id = strdup(id);
    e.version = ver;
    e.is_tombstone = tomb;
    e.content_hash = strdup(hash);
    e.payload = strdup(payload);
    e.source_node_id = strdup("node");
    e.authored_at_ms = 0;
    return e;
}

static void test_store(void) {
    ca_syncable_entry_store_t *s = ca_inmem_syncable_store_create();
    assert(s);

    ca_syncable_entry_t e1 = mk_entry("T", "1", 10, false, "aaa", "p1");
    assert(ca_inmem_syncable_store_apply(s, &e1) == true);       /* new → applied */
    /* lower version → not applied */
    ca_syncable_entry_t e2 = mk_entry("T", "1", 5, false, "zzz", "p0");
    assert(ca_inmem_syncable_store_apply(s, &e2) == false);
    /* higher version → applied */
    ca_syncable_entry_t e3 = mk_entry("T", "1", 20, false, "bbb", "p2");
    assert(ca_inmem_syncable_store_apply(s, &e3) == true);
    /* equal version, tombstone beats non-tombstone */
    ca_syncable_entry_t e4 = mk_entry("T", "1", 20, true, "aaa", "");
    assert(ca_inmem_syncable_store_apply(s, &e4) == true);
    /* equal version, non-tombstone does NOT beat tombstone */
    ca_syncable_entry_t e5 = mk_entry("T", "1", 20, false, "zzz", "p3");
    assert(ca_inmem_syncable_store_apply(s, &e5) == false);

    /* content-hash tiebreak at equal version + equal tombstone state */
    ca_syncable_entry_t f1 = mk_entry("H", "1", 1, false, "m", "x");
    ca_syncable_entry_t f2hi = mk_entry("H", "1", 1, false, "z", "x"); /* z > m → applies */
    ca_syncable_entry_t f2lo = mk_entry("H", "1", 1, false, "a", "x"); /* a < m → no */
    assert(ca_inmem_syncable_store_apply(s, &f1) == true);
    assert(ca_inmem_syncable_store_apply(s, &f2hi) == true);
    assert(ca_inmem_syncable_store_apply(s, &f2lo) == false);

    /* Get returns the current (tombstone) entry */
    ca_syncable_entry_t got; memset(&got, 0, sizeof(got));
    assert(ca_inmem_syncable_store_get(s, "T", "1", &got));
    assert(got.is_tombstone == true && got.version == 20);
    ca_syncable_entry_free(&got);
    assert(ca_inmem_syncable_store_get(s, "T", "missing", &got) == false);

    /* GetSince ascending, strictly-greater */
    ca_syncable_entry_t a1 = mk_entry("S", "a", 3, false, "h3", "3");
    ca_syncable_entry_t a2 = mk_entry("S", "b", 7, false, "h7", "7");
    ca_syncable_entry_t a3 = mk_entry("S", "c", 5, false, "h5", "5");
    ca_inmem_syncable_store_apply(s, &a1);
    ca_inmem_syncable_store_apply(s, &a2);
    ca_inmem_syncable_store_apply(s, &a3);
    size_t n = 0;
    ca_syncable_entry_t *since = ca_inmem_syncable_store_get_since(s, "S", 3, &n);
    assert(n == 2);                     /* 5 and 7, not 3 */
    assert(since[0].version == 5);      /* ascending */
    assert(since[1].version == 7);
    ca_syncable_entry_free_array(since, n);
    since = ca_inmem_syncable_store_get_since(s, "S", 100, &n);
    assert(n == 0 && since == NULL);

    /* state vector: max per type, ascending by type (ordinal) */
    ca_state_vector_entry_t *vec = ca_inmem_syncable_store_get_state_vector(s, &n);
    assert(n == 3);                     /* H, S, T */
    assert(strcmp(vec[0].entity_type, "H") == 0);
    assert(strcmp(vec[1].entity_type, "S") == 0 && vec[1].max_known_version == 7);
    assert(strcmp(vec[2].entity_type, "T") == 0 && vec[2].max_known_version == 20);
    ca_state_vector_free_array(vec, n);

    ca_syncable_entry_free(&e1); ca_syncable_entry_free(&e2); ca_syncable_entry_free(&e3);
    ca_syncable_entry_free(&e4); ca_syncable_entry_free(&e5);
    ca_syncable_entry_free(&f1); ca_syncable_entry_free(&f2hi); ca_syncable_entry_free(&f2lo);
    ca_syncable_entry_free(&a1); ca_syncable_entry_free(&a2); ca_syncable_entry_free(&a3);
    ca_inmem_syncable_store_destroy(s);
    printf("  store: ok\n");
}

/* ── channel / hub ───────────────────────────────────────────────────── */
static int g_delivered = 0;
static ca_sync_envelope_kind_t g_last_kind;
static void count_handler(void *u, const ca_sync_envelope_t *env) {
    (void)u; g_delivered++; g_last_kind = env->kind;
}

static void test_channel(void) {
    ca_inproc_sync_hub_t *hub = ca_inproc_sync_hub_create();
    ca_companion_state_channel_t *a = ca_inproc_channel_create(hub, "A");
    ca_companion_state_channel_t *b = ca_inproc_channel_create(hub, "B");
    assert(a && b);
    assert(ca_inproc_channel_create(hub, "  ") == NULL);   /* blank rejected */
    assert(ca_inproc_channel_create(NULL, "X") == NULL);
    assert(strcmp(ca_inproc_channel_local_node_id(a), "A") == 0);

    size_t cc = 0;
    char **conn = ca_inproc_sync_hub_connected(hub, &cc);
    assert(cc == 2);
    ca_string_array_free(conn, cc);

    /* B subscribes; A sends → only B (not A) receives */
    g_delivered = 0;
    void *sub = ca_inproc_channel_subscribe(b, count_handler, NULL);
    ca_sync_envelope_t env; memset(&env, 0, sizeof(env));
    env.kind = CA_SYNC_ENVELOPE_ANNOUNCE;
    env.from_node_id = strdup("A");
    ca_inproc_channel_send(a, &env);
    assert(g_delivered == 1);
    assert(g_last_kind == CA_SYNC_ENVELOPE_ANNOUNCE);
    ca_sync_envelope_free(&env);

    /* A sending to itself is not self-delivered even if A subscribes */
    g_delivered = 0;
    void *subA = ca_inproc_channel_subscribe(a, count_handler, NULL);
    ca_sync_envelope_t e2; memset(&e2, 0, sizeof(e2));
    e2.kind = CA_SYNC_ENVELOPE_PUSH; e2.from_node_id = strdup("A");
    ca_inproc_channel_send(a, &e2);
    assert(g_delivered == 1);   /* only B, not A itself */
    ca_sync_envelope_free(&e2);

    /* unsubscribe stops delivery */
    g_delivered = 0;
    ca_inproc_channel_unsubscribe(b, sub);
    ca_sync_envelope_t e3; memset(&e3, 0, sizeof(e3));
    e3.kind = CA_SYNC_ENVELOPE_PUSH; e3.from_node_id = strdup("A");
    ca_inproc_channel_send(a, &e3);
    assert(g_delivered == 0);
    ca_sync_envelope_free(&e3);
    ca_inproc_channel_unsubscribe(a, subA);

    ca_inproc_channel_destroy(a);
    conn = ca_inproc_sync_hub_connected(hub, &cc);
    assert(cc == 1);   /* A left */
    ca_string_array_free(conn, cc);
    ca_inproc_channel_destroy(b);
    ca_inproc_sync_hub_destroy(hub);
    printf("  channel: ok\n");
}

/* ── engine: write-local + two-peer convergence ──────────────────────── */
static void test_engine(void) {
    ca_inproc_sync_hub_t *hub = ca_inproc_sync_hub_create();
    ca_companion_state_channel_t *chA = ca_inproc_channel_create(hub, "A");
    ca_companion_state_channel_t *chB = ca_inproc_channel_create(hub, "B");
    ca_syncable_entry_store_t *stA = ca_inmem_syncable_store_create();
    ca_syncable_entry_store_t *stB = ca_inmem_syncable_store_create();
    g_fake_ms = 10000;
    ca_hybrid_logical_clock_t *clkA = ca_hlc_create(1, fake_now, NULL);
    ca_hybrid_logical_clock_t *clkB = ca_hlc_create(2, fake_now, NULL);

    ca_companion_state_sync_engine_t *engA = ca_sync_engine_create(
        ca_inproc_channel_iface(chA), ca_inmem_syncable_store_iface(stA), clkA, fake_now, NULL);
    ca_companion_state_sync_engine_t *engB = ca_sync_engine_create(
        ca_inproc_channel_iface(chB), ca_inmem_syncable_store_iface(stB), clkB, fake_now, NULL);
    assert(engA && engB);
    assert(ca_sync_engine_create(ca_inproc_channel_iface(chA),
                                 ca_inmem_syncable_store_iface(stA), NULL, NULL, NULL) == NULL);

    assert(ca_sync_engine_start(engA));
    assert(ca_sync_engine_start(engB));

    /* A writes locally → Push to B → B applies (event-driven). Because B then
     * re-announces and A has it too, everything converges. */
    ca_syncable_entry_t written; memset(&written, 0, sizeof(written));
    assert(ca_sync_engine_write_local(engA, "PersonaState", "user-1", "{\"v\":1}", false, &written));
    assert(strcmp(written.entity_type, "PersonaState") == 0);
    assert(written.version > 0);
    /* content hash present (64 lowercase hex) */
    assert(strlen(written.content_hash) == 64);
    ca_syncable_entry_free(&written);

    /* B should now have the entry via the Push handler */
    ca_syncable_entry_t onB; memset(&onB, 0, sizeof(onB));
    assert(ca_inmem_syncable_store_get(stB, "PersonaState", "user-1", &onB));
    assert(strcmp(onB.payload, "{\"v\":1}") == 0);
    assert(onB.is_tombstone == false);
    ca_syncable_entry_free(&onB);

    /* Now simulate a peer that was offline: fresh store + engine C joins, and a
     * SyncNow Announce drives the Request→Push catch-up. */
    ca_companion_state_channel_t *chC = ca_inproc_channel_create(hub, "C");
    ca_syncable_entry_store_t *stC = ca_inmem_syncable_store_create();
    ca_hybrid_logical_clock_t *clkC = ca_hlc_create(3, fake_now, NULL);
    ca_companion_state_sync_engine_t *engC = ca_sync_engine_create(
        ca_inproc_channel_iface(chC), ca_inmem_syncable_store_iface(stC), clkC, fake_now, NULL);
    ca_sync_engine_start(engC);

    /* C announces empty vector; A/B announce theirs; C requests + receives. But
     * the driver here: C is behind, so when A re-announces (triggered by its own
     * SyncNow) C will Request. Trigger A's announce: */
    assert(ca_sync_engine_sync_now(engA));
    /* After Announce from A → C sees higher version → Request → A Push → C applies. */
    ca_syncable_entry_t onC; memset(&onC, 0, sizeof(onC));
    assert(ca_inmem_syncable_store_get(stC, "PersonaState", "user-1", &onC));
    assert(strcmp(onC.payload, "{\"v\":1}") == 0);
    ca_syncable_entry_free(&onC);

    ca_sync_engine_destroy(engA);
    ca_sync_engine_destroy(engB);
    ca_sync_engine_destroy(engC);
    ca_hlc_destroy(clkA); ca_hlc_destroy(clkB); ca_hlc_destroy(clkC);
    ca_inmem_syncable_store_destroy(stA);
    ca_inmem_syncable_store_destroy(stB);
    ca_inmem_syncable_store_destroy(stC);
    ca_inproc_channel_destroy(chA); ca_inproc_channel_destroy(chB); ca_inproc_channel_destroy(chC);
    ca_inproc_sync_hub_destroy(hub);
    printf("  engine: ok\n");
}

/* ── bridges ─────────────────────────────────────────────────────────── */
static void test_bridges(void) {
    ca_inproc_sync_hub_t *hub = ca_inproc_sync_hub_create();
    ca_companion_state_channel_t *ch = ca_inproc_channel_create(hub, "A");
    ca_syncable_entry_store_t *st = ca_inmem_syncable_store_create();
    g_fake_ms = 20000;
    ca_hybrid_logical_clock_t *clk = ca_hlc_create(1, fake_now, NULL);
    ca_companion_state_sync_engine_t *eng = ca_sync_engine_create(
        ca_inproc_channel_iface(ch), ca_inmem_syncable_store_iface(st), clk, fake_now, NULL);
    ca_sync_engine_start(eng);

    /* Persona bridge: save persists an entry under (PersonaState, user) */
    assert(ca_persona_sync_bridge_save(eng, "u1", "{\"UserId\":\"u1\"}"));
    ca_syncable_entry_t pe; memset(&pe, 0, sizeof(pe));
    assert(ca_inmem_syncable_store_get(st, CA_PERSONA_SYNC_ENTITY_TYPE, "u1", &pe));
    char *dec = ca_persona_sync_bridge_try_decode(&pe);
    assert(dec && strcmp(dec, "{\"UserId\":\"u1\"}") == 0);
    free(dec);
    /* wrong type → NULL */
    ca_syncable_entry_t wrong = mk_entry("Other", "x", 1, false, "h", "p");
    assert(ca_persona_sync_bridge_try_decode(&wrong) == NULL);
    ca_syncable_entry_free(&wrong);
    ca_syncable_entry_free(&pe);

    /* Lora bridge: publish adapter bytes → round-trip decode */
    const uint8_t adapter[] = { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x42, 0x99 };
    assert(ca_lora_sync_bridge_publish(eng, "personal-u1", adapter, sizeof(adapter),
                                       123456789LL, 7));
    ca_syncable_entry_t le; memset(&le, 0, sizeof(le));
    assert(ca_inmem_syncable_store_get(st, CA_LORA_SYNC_ENTITY_TYPE, "personal-u1", &le));
    ca_lora_adapter_snapshot_t snap; memset(&snap, 0, sizeof(snap));
    uint8_t *back = NULL; size_t back_len = 0;
    assert(ca_lora_sync_bridge_try_write(&le, &snap, &back, &back_len));
    assert(strcmp(snap.adapter_id, "personal-u1") == 0);
    assert(snap.step_count == 7);
    assert(snap.trained_at_ms == 123456789LL);
    assert(back_len == sizeof(adapter));
    assert(memcmp(back, adapter, sizeof(adapter)) == 0);
    free(back);
    ca_lora_adapter_snapshot_free(&snap);
    ca_syncable_entry_free(&le);

    /* Conversation bridge: publish delta → decode; then terminate → tombstone */
    ca_conversation_state_delta_t delta; memset(&delta, 0, sizeof(delta));
    delta.session_id = strdup("s1");
    delta.user_text = strdup("hi \"there\"\n");   /* forces JSON escaping */
    delta.assistant_text = strdup("hello");
    delta.is_turn_complete = false;
    delta.started_at_ms = 1700000000000LL;
    delta.updated_at_ms = 1700000005000LL;
    assert(ca_conversation_sync_bridge_publish(eng, &delta));
    ca_conversation_state_delta_free(&delta);

    ca_syncable_entry_t ce; memset(&ce, 0, sizeof(ce));
    assert(ca_inmem_syncable_store_get(st, CA_CONVERSATION_SYNC_ENTITY_TYPE, "s1", &ce));
    ca_conversation_state_delta_t decoded; memset(&decoded, 0, sizeof(decoded));
    assert(ca_conversation_sync_bridge_try_decode(&ce, &decoded));
    assert(strcmp(decoded.session_id, "s1") == 0);
    assert(strcmp(decoded.user_text, "hi \"there\"\n") == 0);   /* escaping round-trips */
    assert(strcmp(decoded.assistant_text, "hello") == 0);
    assert(decoded.is_turn_complete == false);
    assert(decoded.started_at_ms == 1700000000000LL);
    assert(decoded.updated_at_ms == 1700000005000LL);
    ca_conversation_state_delta_free(&decoded);
    ca_syncable_entry_free(&ce);

    /* terminate → tombstone entry, TryDecode returns false */
    assert(ca_conversation_sync_bridge_terminate(eng, "s1"));
    ca_syncable_entry_t te; memset(&te, 0, sizeof(te));
    assert(ca_inmem_syncable_store_get(st, CA_CONVERSATION_SYNC_ENTITY_TYPE, "s1", &te));
    assert(te.is_tombstone == true);
    ca_conversation_state_delta_t td; memset(&td, 0, sizeof(td));
    assert(ca_conversation_sync_bridge_try_decode(&te, &td) == false);
    ca_syncable_entry_free(&te);

    ca_sync_engine_destroy(eng);
    ca_hlc_destroy(clk);
    ca_inmem_syncable_store_destroy(st);
    ca_inproc_channel_destroy(ch);
    ca_inproc_sync_hub_destroy(hub);
    printf("  bridges: ok\n");
}

/* ── base64 ──────────────────────────────────────────────────────────── */
static void test_base64(void) {
    /* known vectors */
    char *e = ca_base64_encode((const uint8_t *)"Man", 3);
    assert(strcmp(e, "TWFu") == 0); free(e);
    e = ca_base64_encode((const uint8_t *)"Ma", 2);
    assert(strcmp(e, "TWE=") == 0); free(e);
    e = ca_base64_encode((const uint8_t *)"M", 1);
    assert(strcmp(e, "TQ==") == 0); free(e);
    /* round-trip arbitrary bytes */
    const uint8_t raw[] = { 0, 1, 2, 253, 254, 255, 128, 64 };
    e = ca_base64_encode(raw, sizeof(raw));
    size_t dl = 0;
    uint8_t *d = ca_base64_decode(e, &dl);
    assert(dl == sizeof(raw) && memcmp(d, raw, dl) == 0);
    free(e); free(d);
    /* decode with embedded whitespace */
    d = ca_base64_decode("TW\nFu", &dl);
    assert(dl == 3 && memcmp(d, "Man", 3) == 0);
    free(d);
    /* bad input → NULL */
    assert(ca_base64_decode("!!!", &dl) == NULL);
    printf("  base64: ok\n");
}

int main(void) {
    test_hlc();
    test_store();
    test_channel();
    test_engine();
    test_bridges();
    test_base64();
    printf("test_companion_sync: all assertions passed\n");
    return 0;
}
