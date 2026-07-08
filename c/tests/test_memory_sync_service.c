/*
 * test_memory_sync_service.c — CircleAI.Sync (C11 port).
 *
 * Verifies MemorySyncService (push/receive orchestration), SyncDomainKeys, and
 * SyncPrimitives (VersionVector + SyncReconciliation) against the C# spec:
 *   - PushMemoryDeltaAsync wraps (owner,domain,delta) with source=local,
 *     target="" (broadcast), Guaranteed mode by default, and pushes it
 *   - ReceiveLoop skips own echoes and applies episodic-domain deltas
 *   - VersionVector Merge / ADominatesB / LastWriterWins
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* ── in-proc ISyncChannel mock ───────────────────────────────────────── */
typedef struct {
    ca_sync_delta_full_t   last_push;   /* deep copy of the last pushed delta */
    bool                   has_push;
    ca_sync_channel_delta_cb cb;        /* registered receive handler */
    void                  *cb_user;
    int64_t                last_seq;    /* returned by get_last_sequence */
} mock_channel_t;

static bool mock_push(void *self, const ca_sync_delta_full_t *d) {
    mock_channel_t *m = (mock_channel_t *)self;
    if (m->has_push) { ca_sync_delta_full_free(&m->last_push); }
    memset(&m->last_push, 0, sizeof(m->last_push));
    ca_sync_delta_full_copy(&m->last_push, d);
    m->has_push = true;
    return true;
}
static void *mock_receive_start(void *self, const char *owner, ca_sync_channel_delta_cb cb, void *user) {
    (void)owner;
    mock_channel_t *m = (mock_channel_t *)self;
    m->cb = cb; m->cb_user = user;
    return m; /* token */
}
static void mock_receive_stop(void *self, void *sub) {
    (void)sub;
    mock_channel_t *m = (mock_channel_t *)self;
    m->cb = NULL; m->cb_user = NULL;
}
static int64_t mock_get_last_seq(void *self, const char *owner, const char *domain) {
    (void)owner; (void)domain;
    return ((mock_channel_t *)self)->last_seq;
}
static ca_sync_channel_iface_t mock_iface(mock_channel_t *m) {
    ca_sync_channel_iface_t v;
    v.self = m;
    v.push_delta = mock_push;
    v.receive_start = mock_receive_start;
    v.receive_stop = mock_receive_stop;
    v.get_last_sequence = mock_get_last_seq;
    return v;
}

/* episodic-apply seam records what it received */
static int g_apply_count = 0;
static char g_apply_owner[64];
static char g_apply_payload[128];
static bool episodic_apply(void *user, const char *owner, const uint8_t *payload, size_t len) {
    (void)user;
    g_apply_count++;
    snprintf(g_apply_owner, sizeof(g_apply_owner), "%s", owner ? owner : "");
    size_t n = len < sizeof(g_apply_payload) - 1 ? len : sizeof(g_apply_payload) - 1;
    memcpy(g_apply_payload, payload, n); g_apply_payload[n] = '\0';
    return true;
}

/* Simulate an inbound delta arriving on the channel. */
static void deliver(mock_channel_t *m, const char *owner, const char *source_dev,
                    const char *domain, const char *payload) {
    if (!m->cb) return;
    ca_sync_delta_full_t d; memset(&d, 0, sizeof(d));
    d.owner_id = (char *)owner;
    d.source_device_id = (char *)source_dev;
    d.target_device_id = (char *)"";
    d.domain_key = (char *)domain;
    d.payload = (uint8_t *)payload;
    d.payload_len = strlen(payload);
    d.delivery_mode = CA_SYNC_DELIVERY_GUARANTEED;
    m->cb(m->cb_user, &d);
}

static void test_service(void) {
    mock_channel_t mock; memset(&mock, 0, sizeof(mock));

    /* blank device id / null channel rejected */
    ca_sync_channel_iface_t bad = mock_iface(&mock); bad.self = NULL;
    assert(ca_memory_sync_service_create(bad, episodic_apply, NULL, "dev-1") == NULL);
    assert(ca_memory_sync_service_create(mock_iface(&mock), episodic_apply, NULL, "  ") == NULL);

    ca_memory_sync_service_t *svc = ca_memory_sync_service_create(
        mock_iface(&mock), episodic_apply, NULL, "dev-local");
    assert(svc);

    /* Push: wraps with source=local, target="", broadcast, Guaranteed. */
    const uint8_t delta[] = "episodic-blob";
    assert(ca_memory_sync_service_push_delta(svc, "owner-42",
                                             CA_SYNC_DOMAIN_EPISODIC_MEMORY,
                                             delta, sizeof(delta) - 1,
                                             CA_SYNC_DELIVERY_GUARANTEED));
    assert(mock.has_push);
    assert(strcmp(mock.last_push.owner_id, "owner-42") == 0);
    assert(strcmp(mock.last_push.source_device_id, "dev-local") == 0);
    assert(strcmp(mock.last_push.target_device_id, "") == 0);   /* broadcast */
    assert(strcmp(mock.last_push.domain_key, CA_SYNC_DOMAIN_EPISODIC_MEMORY) == 0);
    assert(mock.last_push.payload_len == sizeof(delta) - 1);
    assert(memcmp(mock.last_push.payload, delta, sizeof(delta) - 1) == 0);
    assert(mock.last_push.delivery_mode == CA_SYNC_DELIVERY_GUARANTEED);
    assert(mock.last_push.has_ttl == false);
    assert(mock.last_push.sequence > 0);

    /* Start receiving; an inbound episodic delta from ANOTHER device applies. */
    g_apply_count = 0;
    assert(ca_memory_sync_service_start_receiving(svc, "owner-42"));
    deliver(&mock, "owner-42", "dev-remote", CA_SYNC_DOMAIN_EPISODIC_MEMORY, "hello");
    assert(g_apply_count == 1);
    assert(strcmp(g_apply_owner, "owner-42") == 0);
    assert(strcmp(g_apply_payload, "hello") == 0);

    /* own echo is skipped */
    deliver(&mock, "owner-42", "dev-local", CA_SYNC_DOMAIN_EPISODIC_MEMORY, "echo");
    assert(g_apply_count == 1);   /* unchanged */

    /* non-episodic domain is not applied (no episodic handler match) */
    deliver(&mock, "owner-42", "dev-remote", CA_SYNC_DOMAIN_PERSONA, "persona-blob");
    assert(g_apply_count == 1);   /* unchanged */

    /* Stop receiving: further inbound deltas do nothing. */
    ca_memory_sync_service_stop_receiving(svc);
    deliver(&mock, "owner-42", "dev-remote", CA_SYNC_DOMAIN_EPISODIC_MEMORY, "after-stop");
    assert(g_apply_count == 1);

    ca_memory_sync_service_destroy(svc);
    ca_sync_delta_full_free(&mock.last_push);
    printf("  service: ok\n");
}

static void test_primitives(void) {
    const char *ka[] = { "a", "b" };      int64_t va[] = { 3, 5 };
    const char *kb[] = { "b", "c" };      int64_t vb[] = { 2, 9 };
    ca_version_vector_t *a = ca_version_vector_create(ka, va, 2);
    ca_version_vector_t *b = ca_version_vector_create(kb, vb, 2);
    assert(a && b);
    assert(ca_version_vector_get(a, "a") == 3);
    assert(ca_version_vector_get(a, "z") == 0);   /* absent → 0 */

    /* Merge → per-key max over union {a:3, b:max(5,2)=5, c:9} */
    ca_version_vector_t *m = ca_sync_reconciliation_merge(a, b);
    assert(m);
    assert(ca_version_vector_get(m, "a") == 3);
    assert(ca_version_vector_get(m, "b") == 5);
    assert(ca_version_vector_get(m, "c") == 9);
    ca_version_vector_destroy(m);

    /* ADominatesB: neither dominates (a has b=5>2 but c=0<9). */
    assert(ca_sync_reconciliation_a_dominates_b(a, b) == false);
    assert(ca_sync_reconciliation_a_dominates_b(b, a) == false);

    /* strict domination */
    const char *kc[] = { "a", "b" };   int64_t vc[] = { 3, 5 };
    const char *kd[] = { "a", "b" };   int64_t vd[] = { 3, 4 };
    ca_version_vector_t *c = ca_version_vector_create(kc, vc, 2);
    ca_version_vector_t *d = ca_version_vector_create(kd, vd, 2);
    assert(ca_sync_reconciliation_a_dominates_b(c, d) == true);   /* c >= d, strictly at b */
    assert(ca_sync_reconciliation_a_dominates_b(d, c) == false);
    /* equal vectors: no strict-greater → not dominating */
    assert(ca_sync_reconciliation_a_dominates_b(c, c) == false);
    ca_version_vector_destroy(c);
    ca_version_vector_destroy(d);

    /* LastWriterWins: later timestamp wins; tie → a */
    int64_t at;
    assert(ca_sync_reconciliation_last_writer_wins_i64(100, 11, 200, 22, &at) == 22 && at == 200);
    assert(ca_sync_reconciliation_last_writer_wins_i64(300, 33, 200, 22, &at) == 33 && at == 300);
    assert(ca_sync_reconciliation_last_writer_wins_i64(150, 44, 150, 55, &at) == 44 && at == 150); /* tie → a */

    ca_version_vector_destroy(a);
    ca_version_vector_destroy(b);
    printf("  primitives: ok\n");
}

int main(void) {
    test_service();
    test_primitives();
    printf("test_memory_sync_service: all assertions passed\n");
    return 0;
}
