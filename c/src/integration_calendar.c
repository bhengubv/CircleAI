/*
 * integration_calendar.c — CircleAI.Integration.Calendar (C11 port).
 *
 * In-memory ICalendarConnector backends for the CalDAV / Google / MsGraph
 * connectors. The real connectors issue HTTP; here the store is a linear event
 * array and the network is the injected dependency. Contract-identical:
 * time-range ListEvents (overlap, StartUtc asc), UID-assigning CreateEvent,
 * idempotent DeleteEvent. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/integration_calendar.h"
#include "board_common.h"

typedef enum { PROV_CALDAV, PROV_GOOGLE, PROV_MSGRAPH } cal_prov_t;

typedef struct {
    cal_prov_t                provider;
    bool                      configured;
    uint64_t                  uid_seq;   /* deterministic Guid("N") surrogate */
    ca_int_calendar_event_t  *items;
    size_t                    count, cap;
} cal_impl_t;

/* ── Guid("N") surrogate: 32 lowercase hex chars from a monotonic counter ── */
static char *next_uid(cal_impl_t *m) {
    char *s = (char *)malloc(33);
    if (!s) return NULL;
    uint64_t v = ++m->uid_seq;
    /* Low 64 bits carry the counter; high 64 bits are zero -> stable, unique. */
    static const char hex[] = "0123456789abcdef";
    for (int i = 0; i < 16; ++i) s[i] = '0';
    for (int i = 31; i >= 16; --i) { s[i] = hex[v & 0xF]; v >>= 4; }
    s[32] = '\0';
    return s;
}

/* ── vtable ops ─────────────────────────────────────────────────────────── */

static const char *cal_provider_id(void *impl) {
    switch (((cal_impl_t *)impl)->provider) {
        case PROV_GOOGLE:  return "google-calendar";
        case PROV_MSGRAPH: return "ms-graph-calendar";
        default:           return "caldav";
    }
}

static bool cal_is_configured(void *impl) {
    return ((cal_impl_t *)impl)->configured;
}

/* Stable ascending sort of collected indices by StartUtc. */
static void cal_sort_start_asc(const cal_impl_t *m, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = m->items[key].start_utc_ms;
        size_t j = i;
        while (j > 0 && m->items[idx[j - 1]].start_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

static ca_int_calendar_event_t *cal_list_events(void *impl, int64_t from_utc_ms,
                                                int64_t to_utc_ms,
                                                size_t *out_count) {
    if (!out_count) return NULL;
    cal_impl_t *m = (cal_impl_t *)impl;
    if (m->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(m->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < m->count; ++i) {
        /* Half-open overlap with [fromUtc, toUtc): Start < to && End > from. */
        if (m->items[i].start_utc_ms < to_utc_ms &&
            m->items[i].end_utc_ms > from_utc_ms)
            idx[n++] = i;
    }
    cal_sort_start_asc(m, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_int_calendar_event_t *out =
        (ca_int_calendar_event_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!ca_int_calendar_event_copy(&out[i], &m->items[idx[i]])) {
            ca_int_calendar_event_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

static int cal_create_event(void *impl, const ca_int_calendar_event_t *ev,
                            ca_int_calendar_event_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    cal_impl_t *m = (cal_impl_t *)impl;
    if (!m || !ev) return -1; /* ArgumentNullException */

    /* EventId blank -> assign a fresh UID; store a copy, then hand back a copy. */
    ca_int_calendar_event_t stored;
    if (!ca_int_calendar_event_copy(&stored, ev)) return -1;
    if (cab_is_ws(stored.event_id)) {
        char *uid = next_uid(m);
        if (!uid) { ca_int_calendar_event_free(&stored); return -1; }
        free(stored.event_id);
        stored.event_id = uid;
    }

    if (m->count == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 4;
        void *nb = realloc(m->items, nc * sizeof(*m->items));
        if (!nb) { ca_int_calendar_event_free(&stored); return -1; }
        m->items = (ca_int_calendar_event_t *)nb;
        m->cap = nc;
    }
    if (out && !ca_int_calendar_event_copy(out, &stored)) {
        ca_int_calendar_event_free(&stored);
        return -1;
    }
    m->items[m->count++] = stored;
    return 0;
}

static int cal_delete_event(void *impl, const char *calendar_id,
                            const char *event_id) {
    cal_impl_t *m = (cal_impl_t *)impl;
    if (!m) return -1;
    if (cab_is_ws(event_id)) return -1; /* ArgumentException */
    for (size_t i = 0; i < m->count; ++i) {
        bool cal_ok = cab_is_ws(calendar_id) ||
                      cab_ord_eq(m->items[i].calendar_id, calendar_id);
        if (cal_ok && cab_ord_eq(m->items[i].event_id, event_id)) {
            ca_int_calendar_event_free(&m->items[i]);
            m->items[i] = m->items[m->count - 1];
            m->count--;
            return 0;
        }
    }
    return 0; /* not-found swallowed, mirroring C# */
}

/* ── construction ───────────────────────────────────────────────────────── */

static ca_int_calendar_connector_t *cal_new(cal_prov_t provider, bool configured) {
    cal_impl_t *m = (cal_impl_t *)calloc(1, sizeof(cal_impl_t));
    if (!m) return NULL;
    m->provider   = provider;
    m->configured = configured;

    ca_int_calendar_connector_t *c =
        (ca_int_calendar_connector_t *)calloc(1, sizeof(*c));
    if (!c) { free(m); return NULL; }
    c->impl          = m;
    c->provider_id   = cal_provider_id;
    c->is_configured = cal_is_configured;
    c->list_events   = cal_list_events;
    c->create_event  = cal_create_event;
    c->delete_event  = cal_delete_event;
    return c;
}

ca_int_calendar_connector_t *ca_int_caldav_calendar_create(const char *username,
                                                           const char *password) {
    bool configured = !cab_is_ws(username) && !cab_is_ws(password);
    return cal_new(PROV_CALDAV, configured);
}

ca_int_calendar_connector_t *ca_int_google_calendar_create(bool has_token_provider,
                                                           const char *calendar_id) {
    (void)calendar_id; /* stored events carry their own CalendarId */
    return cal_new(PROV_GOOGLE, has_token_provider);
}

ca_int_calendar_connector_t *ca_int_msgraph_calendar_create(bool has_token_provider,
                                                            const char *calendar_id) {
    (void)calendar_id;
    return cal_new(PROV_MSGRAPH, has_token_provider);
}

void ca_int_calendar_connector_destroy(ca_int_calendar_connector_t *c) {
    if (!c) return;
    cal_impl_t *m = (cal_impl_t *)c->impl;
    if (m) {
        for (size_t i = 0; i < m->count; ++i) ca_int_calendar_event_free(&m->items[i]);
        free(m->items);
        free(m);
    }
    free(c);
}
