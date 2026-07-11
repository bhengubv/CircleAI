/*
 * test_integration_email.c — CircleAI.Integration.Email (C11 port) verification
 * of the in-memory Gmail / IMAP / MsGraph connectors.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_int_email_message_t mk_msg(const char *id, const char *subject,
                                     const char *body, int64_t recv, bool unread) {
    ca_int_email_message_t m; memset(&m, 0, sizeof(m));
    m.message_id = (char *)id; m.from = (char *)"a@x.io";
    m.subject = (char *)subject; m.body_text = (char *)body;
    m.received_utc_ms = recv; m.unread = unread;
    return m;
}

static void test_config_and_ids(void) {
    ca_int_email_connector_t *g = ca_int_gmail_email_create(true);
    ca_int_email_connector_t *g0 = ca_int_gmail_email_create(false);
    ca_int_email_connector_t *im = ca_int_imap_email_create("imap.x.io", "u", "p");
    ca_int_email_connector_t *im0 = ca_int_imap_email_create("imap.x.io", "u", "");
    ca_int_email_connector_t *ms = ca_int_msgraph_email_create(true);
    assert(g && g0 && im && im0 && ms);

    assert(strcmp(g->provider_id(g->impl), "gmail") == 0);
    assert(strcmp(im->provider_id(im->impl), "imap") == 0);
    assert(strcmp(ms->provider_id(ms->impl), "ms-graph-mail") == 0);

    assert(g->is_configured(g->impl) && !g0->is_configured(g0->impl));
    assert(im->is_configured(im->impl) && !im0->is_configured(im0->impl));
    assert(ms->is_configured(ms->impl));

    ca_int_email_connector_destroy(g);
    ca_int_email_connector_destroy(g0);
    ca_int_email_connector_destroy(im);
    ca_int_email_connector_destroy(im0);
    ca_int_email_connector_destroy(ms);
    printf("  config_and_ids: ok\n");
}

static void test_unread_search_markread(void) {
    ca_int_email_connector_t *c = ca_int_gmail_email_create(true);
    assert(c);

    ca_int_email_message_t m1 = mk_msg("m1", "Invoice", "pay now", 100, true);
    ca_int_email_message_t m2 = mk_msg("m2", "Lunch", "invoice attached", 300, true);
    ca_int_email_message_t m3 = mk_msg("m3", "Old", "read me", 200, false);
    assert(ca_int_email_seed(c, &m1) == 0);
    assert(ca_int_email_seed(c, &m2) == 0);
    assert(ca_int_email_seed(c, &m3) == 0);

    /* ListUnread: m1(100) + m2(300) unread; newest-first => m2, m1. */
    size_t n = 0;
    ca_int_email_message_t *arr = c->list_unread(c->impl, 10, &n);
    assert(n == 2);
    assert(strcmp(arr[0].message_id, "m2") == 0);
    assert(strcmp(arr[1].message_id, "m1") == 0);
    ca_int_email_message_free_array(arr, n);

    /* max caps after sort. */
    arr = c->list_unread(c->impl, 1, &n);
    assert(n == 1 && strcmp(arr[0].message_id, "m2") == 0);
    ca_int_email_message_free_array(arr, n);

    /* Search "invoice" (CI): subject of m1 ("Invoice") + body of m2
     * ("invoice attached"); newest-first => m2, m1. */
    arr = c->search(c->impl, "invoice", 10, &n);
    assert(n == 2);
    assert(strcmp(arr[0].message_id, "m2") == 0);
    assert(strcmp(arr[1].message_id, "m1") == 0);
    ca_int_email_message_free_array(arr, n);

    /* MarkRead(m2) -> no longer unread. */
    assert(c->mark_read(c->impl, "m2") == 0);
    arr = c->list_unread(c->impl, 10, &n);
    assert(n == 1 && strcmp(arr[0].message_id, "m1") == 0);
    ca_int_email_message_free_array(arr, n);

    /* MarkRead unknown id swallowed. */
    assert(c->mark_read(c->impl, "zzz") == 0);

    /* error paths. */
    assert(c->list_unread(c->impl, 0, &n) == NULL && n == (size_t)-1);
    assert(c->search(c->impl, NULL, 10, &n) == NULL && n == (size_t)-1);
    assert(c->search(c->impl, "  ", 10, &n) == NULL && n == (size_t)-1);
    assert(c->search(c->impl, "x", 0, &n) == NULL && n == (size_t)-1);
    assert(c->mark_read(c->impl, "  ") == -1);

    ca_int_email_connector_destroy(c);
    printf("  unread_search_markread: ok\n");
}

int main(void) {
    test_config_and_ids();
    test_unread_search_markread();
    printf("test_integration_email: all assertions passed\n");
    return 0;
}
