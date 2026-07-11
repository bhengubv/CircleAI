/*
 * integration_email.c — CircleAI.Integration.Email (C11 port).
 *
 * In-memory IEmailConnector backends for Gmail / IMAP / MsGraph. The mailbox is
 * a linear message array seeded via ca_int_email_seed (the injected network
 * data). Contract-identical: unread-newest-first ListUnread, subject|body
 * substring Search, MarkRead flips the flag. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/integration_email.h"
#include "board_common.h"

typedef enum { EPROV_GMAIL, EPROV_IMAP, EPROV_MSGRAPH } email_prov_t;

typedef struct {
    email_prov_t             provider;
    bool                     configured;
    ca_int_email_message_t  *items;
    size_t                   count, cap;
} email_impl_t;

static const char *email_provider_id(void *impl) {
    switch (((email_impl_t *)impl)->provider) {
        case EPROV_GMAIL:   return "gmail";
        case EPROV_MSGRAPH: return "ms-graph-mail";
        default:            return "imap";
    }
}

static bool email_is_configured(void *impl) {
    return ((email_impl_t *)impl)->configured;
}

/* Stable descending sort of collected indices by ReceivedUtc. */
static void email_sort_recv_desc(const email_impl_t *m, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = m->items[key].received_utc_ms;
        size_t j = i;
        while (j > 0 && m->items[idx[j - 1]].received_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

/* Shared collect+sort+take+copy over a predicate. mode 0 = unread, 1 = search. */
static ca_int_email_message_t *email_query(email_impl_t *m, int mode,
                                           const char *query, int max,
                                           size_t *out_count) {
    if (m->count == 0) { *out_count = 0; return NULL; }
    size_t *idx = (size_t *)malloc(m->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < m->count; ++i) {
        bool match;
        if (mode == 0) {
            match = m->items[i].unread;
        } else {
            match = cab_ci_contains(m->items[i].subject, query) ||
                    cab_ci_contains(m->items[i].body_text, query);
        }
        if (match) idx[n++] = i;
    }
    email_sort_recv_desc(m, idx, n);
    if ((size_t)max < n) n = (size_t)max;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_int_email_message_t *out =
        (ca_int_email_message_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!ca_int_email_message_copy(&out[i], &m->items[idx[i]])) {
            ca_int_email_message_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

static ca_int_email_message_t *email_list_unread(void *impl, int max,
                                                 size_t *out_count) {
    if (!out_count) return NULL;
    if (max <= 0) { *out_count = (size_t)-1; return NULL; }
    return email_query((email_impl_t *)impl, 0, NULL, max, out_count);
}

static ca_int_email_message_t *email_search(void *impl, const char *query,
                                            int max, size_t *out_count) {
    if (!out_count) return NULL;
    if (cab_is_ws(query) || max <= 0) { *out_count = (size_t)-1; return NULL; }
    return email_query((email_impl_t *)impl, 1, query, max, out_count);
}

static int email_mark_read(void *impl, const char *message_id) {
    email_impl_t *m = (email_impl_t *)impl;
    if (!m) return -1;
    if (cab_is_ws(message_id)) return -1; /* ArgumentException */
    for (size_t i = 0; i < m->count; ++i)
        if (cab_ord_eq(m->items[i].message_id, message_id))
            m->items[i].unread = false;
    return 0;
}

/* ── construction / seeding ─────────────────────────────────────────────── */

static ca_int_email_connector_t *email_new(email_prov_t provider, bool configured) {
    email_impl_t *m = (email_impl_t *)calloc(1, sizeof(email_impl_t));
    if (!m) return NULL;
    m->provider   = provider;
    m->configured = configured;

    ca_int_email_connector_t *c =
        (ca_int_email_connector_t *)calloc(1, sizeof(*c));
    if (!c) { free(m); return NULL; }
    c->impl          = m;
    c->provider_id   = email_provider_id;
    c->is_configured = email_is_configured;
    c->list_unread   = email_list_unread;
    c->search        = email_search;
    c->mark_read     = email_mark_read;
    return c;
}

ca_int_email_connector_t *ca_int_gmail_email_create(bool has_token_provider) {
    return email_new(EPROV_GMAIL, has_token_provider);
}

ca_int_email_connector_t *ca_int_imap_email_create(const char *host,
                                                   const char *username,
                                                   const char *password) {
    bool configured = !cab_is_ws(host) && !cab_is_ws(username) && !cab_is_ws(password);
    return email_new(EPROV_IMAP, configured);
}

ca_int_email_connector_t *ca_int_msgraph_email_create(bool has_token_provider) {
    return email_new(EPROV_MSGRAPH, has_token_provider);
}

int ca_int_email_seed(ca_int_email_connector_t *c,
                      const ca_int_email_message_t *msg) {
    if (!c || !msg) return -1;
    email_impl_t *m = (email_impl_t *)c->impl;
    ca_int_email_message_t copy;
    if (!ca_int_email_message_copy(&copy, msg)) return -1;
    if (m->count == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 4;
        void *nb = realloc(m->items, nc * sizeof(*m->items));
        if (!nb) { ca_int_email_message_free(&copy); return -1; }
        m->items = (ca_int_email_message_t *)nb;
        m->cap = nc;
    }
    m->items[m->count++] = copy;
    return 0;
}

void ca_int_email_connector_destroy(ca_int_email_connector_t *c) {
    if (!c) return;
    email_impl_t *m = (email_impl_t *)c->impl;
    if (m) {
        for (size_t i = 0; i < m->count; ++i) ca_int_email_message_free(&m->items[i]);
        free(m->items);
        free(m);
    }
    free(c);
}
