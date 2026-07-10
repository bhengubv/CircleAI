/*
 * net_http.c — CircleAI.Networking.Http (C11 port).
 *
 * HttpEndpointDescriptor / HttpRequestSummary / HttpCacheKey records, the
 * HttpStatusFamily helpers, InMemoryHttpRequestMetrics, the injected IHttpPoster
 * seam, and HttpNetworkTransport (INetworkTransport with 3-attempt retry).
 *
 * Pure C11 + libc.
 */

#include "circle_ai/net_http.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- helpers ---- */

static char *dup_or_null(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *dup_or_empty(const char *s) { return dup_or_null(s ? s : ""); }

static ca_net_metadata_pair_t *dup_pairs(const ca_net_metadata_pair_t *src,
                                         size_t count, bool *ok) {
    *ok = true;
    if (count == 0) return NULL;
    ca_net_metadata_pair_t *out =
        (ca_net_metadata_pair_t *)calloc(count, sizeof(*out));
    if (!out) { *ok = false; return NULL; }
    for (size_t i = 0; i < count; ++i) {
        out[i].key = dup_or_empty(src ? src[i].key : NULL);
        out[i].value = dup_or_empty(src ? src[i].value : NULL);
        if (!out[i].key || !out[i].value) {
            for (size_t j = 0; j <= i; ++j) { free(out[j].key); free(out[j].value); }
            free(out);
            *ok = false;
            return NULL;
        }
    }
    return out;
}
static void free_pairs(ca_net_metadata_pair_t *p, size_t n) {
    if (!p) return;
    for (size_t i = 0; i < n; ++i) { free(p[i].key); free(p[i].value); }
    free(p);
}

/* ===========================================================================
 * HttpStatusFamily
 * =========================================================================== */

bool ca_http_status_is_2xx(int s) { return s >= 200 && s < 300; }
bool ca_http_status_is_3xx(int s) { return s >= 300 && s < 400; }
bool ca_http_status_is_4xx(int s) { return s >= 400 && s < 500; }
bool ca_http_status_is_5xx(int s) { return s >= 500 && s < 600; }
bool ca_http_status_should_retry(int s) {
    return s == 408 || s == 425 || s == 429 || ca_http_status_is_5xx(s);
}

/* ===========================================================================
 * HttpEndpointDescriptor
 * =========================================================================== */

ca_http_endpoint_descriptor_t *ca_http_endpoint_descriptor_new(
    const char *method, const char *base_uri, const char *path,
    bool has_headers, const ca_net_metadata_pair_t *default_headers,
    size_t header_count) {
    ca_http_endpoint_descriptor_t *e =
        (ca_http_endpoint_descriptor_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->method = dup_or_empty(method);
    e->base_uri = dup_or_empty(base_uri);
    e->path = dup_or_empty(path);
    if (!e->method || !e->base_uri || !e->path) {
        ca_http_endpoint_descriptor_destroy(e);
        return NULL;
    }
    e->has_headers = has_headers;
    if (has_headers) {
        bool ok = true;
        e->default_headers = dup_pairs(default_headers, header_count, &ok);
        if (!ok) { ca_http_endpoint_descriptor_destroy(e); return NULL; }
        e->header_count = header_count;
    }
    return e;
}

void ca_http_endpoint_descriptor_destroy(ca_http_endpoint_descriptor_t *e) {
    if (!e) return;
    free(e->method);
    free(e->base_uri);
    free(e->path);
    free_pairs(e->default_headers, e->header_count);
    free(e);
}

ca_http_endpoint_descriptor_t *ca_http_endpoint_descriptor_copy(
    const ca_http_endpoint_descriptor_t *e) {
    if (!e) return NULL;
    return ca_http_endpoint_descriptor_new(e->method, e->base_uri, e->path,
                                           e->has_headers, e->default_headers,
                                           e->header_count);
}

/* ===========================================================================
 * HttpRequestSummary
 * =========================================================================== */

void ca_http_request_summary_free(ca_http_request_summary_t *s) {
    if (!s) return;
    free(s->endpoint_id);
    s->endpoint_id = NULL;
}

void ca_http_request_summary_free_array(ca_http_request_summary_t *arr,
                                        size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_http_request_summary_free(&arr[i]);
    free(arr);
}

/* ===========================================================================
 * HttpCacheKey
 * =========================================================================== */

ca_http_cache_key_t *ca_http_cache_key_new(const char *method,
                                           const char *full_uri,
                                           const char *accept_header) {
    ca_http_cache_key_t *k = (ca_http_cache_key_t *)calloc(1, sizeof(*k));
    if (!k) return NULL;
    k->method = dup_or_empty(method);
    k->full_uri = dup_or_empty(full_uri);
    k->accept_header = dup_or_empty(accept_header);
    if (!k->method || !k->full_uri || !k->accept_header) {
        ca_http_cache_key_destroy(k);
        return NULL;
    }
    return k;
}

void ca_http_cache_key_destroy(ca_http_cache_key_t *k) {
    if (!k) return;
    free(k->method);
    free(k->full_uri);
    free(k->accept_header);
    free(k);
}

ca_http_cache_key_t *ca_http_cache_key_copy(const ca_http_cache_key_t *k) {
    if (!k) return NULL;
    return ca_http_cache_key_new(k->method, k->full_uri, k->accept_header);
}

bool ca_http_cache_key_equals(const ca_http_cache_key_t *a,
                              const ca_http_cache_key_t *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    return strcmp(a->method, b->method) == 0 &&
           strcmp(a->full_uri, b->full_uri) == 0 &&
           strcmp(a->accept_header, b->accept_header) == 0;
}

/* ===========================================================================
 * InMemoryHttpRequestMetrics
 * =========================================================================== */

typedef struct {
    char                          *id;   /* owned */
    ca_http_endpoint_descriptor_t *desc; /* owned */
} http_ep_entry_t;

struct ca_http_metrics {
    http_ep_entry_t           *endpoints;
    size_t                     ep_count, ep_cap;
    ca_http_request_summary_t *requests;
    size_t                     req_count, req_cap;
};

ca_http_metrics_t *ca_http_metrics_create(void) {
    return (ca_http_metrics_t *)calloc(1, sizeof(ca_http_metrics_t));
}

void ca_http_metrics_destroy(ca_http_metrics_t *m) {
    if (!m) return;
    for (size_t i = 0; i < m->ep_count; ++i) {
        free(m->endpoints[i].id);
        ca_http_endpoint_descriptor_destroy(m->endpoints[i].desc);
    }
    free(m->endpoints);
    for (size_t i = 0; i < m->req_count; ++i)
        ca_http_request_summary_free(&m->requests[i]);
    free(m->requests);
    free(m);
}

static ptrdiff_t ep_index(const ca_http_metrics_t *m, const char *id) {
    for (size_t i = 0; i < m->ep_count; ++i)
        if (strcmp(m->endpoints[i].id, id) == 0) return (ptrdiff_t)i;
    return -1;
}

int ca_http_metrics_register(ca_http_metrics_t *m, const char *id,
                             const ca_http_endpoint_descriptor_t *d) {
    if (!m || !id || !d) return -1;
    ca_http_endpoint_descriptor_t *copy = ca_http_endpoint_descriptor_copy(d);
    if (!copy) return -1;
    ptrdiff_t idx = ep_index(m, id);
    if (idx >= 0) {
        ca_http_endpoint_descriptor_destroy(m->endpoints[idx].desc);
        m->endpoints[idx].desc = copy;
        return 0;
    }
    if (m->ep_count == m->ep_cap) {
        size_t nc = m->ep_cap ? m->ep_cap * 2 : 4;
        http_ep_entry_t *ne =
            (http_ep_entry_t *)realloc(m->endpoints, nc * sizeof(*ne));
        if (!ne) { ca_http_endpoint_descriptor_destroy(copy); return -1; }
        m->endpoints = ne;
        m->ep_cap = nc;
    }
    char *kid = dup_or_empty(id);
    if (!kid) { ca_http_endpoint_descriptor_destroy(copy); return -1; }
    m->endpoints[m->ep_count].id = kid;
    m->endpoints[m->ep_count].desc = copy;
    m->ep_count++;
    return 0;
}

ca_http_endpoint_descriptor_t *ca_http_metrics_get_endpoint(
    const ca_http_metrics_t *m, const char *id) {
    if (!m || !id) return NULL;
    ptrdiff_t idx = ep_index(m, id);
    return idx < 0 ? NULL
                   : ca_http_endpoint_descriptor_copy(m->endpoints[idx].desc);
}

int ca_http_metrics_log(ca_http_metrics_t *m, const char *endpoint_id,
                        int status_code, int64_t latency_ms, int response_bytes,
                        int64_t at_unix_ms) {
    if (!m) return -1;
    if (m->req_count == m->req_cap) {
        size_t nc = m->req_cap ? m->req_cap * 2 : 4;
        ca_http_request_summary_t *nr =
            (ca_http_request_summary_t *)realloc(m->requests, nc * sizeof(*nr));
        if (!nr) return -1;
        m->requests = nr;
        m->req_cap = nc;
    }
    ca_http_request_summary_t *s = &m->requests[m->req_count];
    s->endpoint_id = dup_or_empty(endpoint_id);
    if (!s->endpoint_id) return -1;
    s->status_code = status_code;
    s->latency_ms = latency_ms;
    s->response_bytes = response_bytes;
    s->at_unix_ms = at_unix_ms;
    m->req_count++;
    return 0;
}

typedef struct { const ca_http_request_summary_t *r; size_t ord; } req_ref_t;
static int cmp_req_desc(const void *a, const void *b) {
    const req_ref_t *ra = (const req_ref_t *)a;
    const req_ref_t *rb = (const req_ref_t *)b;
    if (ra->r->at_unix_ms > rb->r->at_unix_ms) return -1;
    if (ra->r->at_unix_ms < rb->r->at_unix_ms) return 1;
    if (ra->ord < rb->ord) return -1;
    if (ra->ord > rb->ord) return 1;
    return 0;
}

ca_http_request_summary_t *ca_http_metrics_recent_requests(
    const ca_http_metrics_t *m, int limit, size_t *count) {
    if (!m || !count) { if (count) *count = SIZE_MAX; return NULL; }
    if (limit < 0) limit = 0;
    size_t take = (size_t)limit < m->req_count ? (size_t)limit : m->req_count;
    if (take == 0) { *count = 0; return NULL; }
    req_ref_t *refs = (req_ref_t *)calloc(m->req_count, sizeof(*refs));
    if (!refs) { *count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < m->req_count; ++i) {
        refs[i].r = &m->requests[i];
        refs[i].ord = i;
    }
    qsort(refs, m->req_count, sizeof(*refs), cmp_req_desc);
    ca_http_request_summary_t *out =
        (ca_http_request_summary_t *)calloc(take, sizeof(*out));
    if (!out) { free(refs); *count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < take; ++i) {
        const ca_http_request_summary_t *s = refs[i].r;
        out[i].endpoint_id = dup_or_empty(s->endpoint_id);
        out[i].status_code = s->status_code;
        out[i].latency_ms = s->latency_ms;
        out[i].response_bytes = s->response_bytes;
        out[i].at_unix_ms = s->at_unix_ms;
        if (!out[i].endpoint_id) {
            ca_http_request_summary_free_array(out, i + 1);
            free(refs);
            *count = SIZE_MAX;
            return NULL;
        }
    }
    free(refs);
    *count = take;
    return out;
}

double ca_http_metrics_avg_2xx_latency_ms(const ca_http_metrics_t *m,
                                          const char *endpoint_id) {
    if (!m || !endpoint_id) return 0.0;
    double sum = 0.0;
    size_t n = 0;
    for (size_t i = 0; i < m->req_count; ++i) {
        if (strcmp(m->requests[i].endpoint_id, endpoint_id) == 0 &&
            ca_http_status_is_2xx(m->requests[i].status_code)) {
            sum += (double)m->requests[i].latency_ms;
            n++;
        }
    }
    return n == 0 ? 0.0 : sum / (double)n;
}

/* ===========================================================================
 * Uri.EscapeDataString
 * =========================================================================== */

static bool is_unreserved(unsigned char c) {
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
           (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' ||
           c == '~';
}

char *ca_http_escape_data_string(const char *s) {
    if (!s) return NULL;
    size_t len = strlen(s);
    /* worst case: every byte -> %XX (3 chars). */
    char *out = (char *)malloc(len * 3 + 1);
    if (!out) return NULL;
    static const char hex[] = "0123456789ABCDEF";
    size_t j = 0;
    for (size_t i = 0; i < len; ++i) {
        unsigned char c = (unsigned char)s[i];
        if (is_unreserved(c)) {
            out[j++] = (char)c;
        } else {
            out[j++] = '%';
            out[j++] = hex[c >> 4];
            out[j++] = hex[c & 0x0F];
        }
    }
    out[j] = '\0';
    return out;
}

/* Priority.ToString() — the C# enum member names. */
static const char *priority_name(ca_message_priority_t p) {
    switch (p) {
        case CA_MSG_PRIORITY_LOW:       return "Low";
        case CA_MSG_PRIORITY_NORMAL:    return "Normal";
        case CA_MSG_PRIORITY_HIGH:      return "High";
        case CA_MSG_PRIORITY_URGENT:    return "Urgent";
        case CA_MSG_PRIORITY_EMERGENCY: return "Emergency";
        default:                        return "Normal";
    }
}

/* ===========================================================================
 * HttpNetworkTransport
 * =========================================================================== */

struct ca_http_net_transport {
    ca_http_poster_t poster;    /* borrowed vtable */
    char            *base_url;  /* owned, trailing '/' trimmed */
    bool             running;
    int              last_attempts;
    char            *last_url;  /* owned, or NULL */
};

ca_http_net_transport_t *ca_http_net_transport_create(ca_http_poster_t poster,
                                              const char *base_url) {
    if (!base_url || base_url[0] == '\0') return NULL;
    ca_http_net_transport_t *t = (ca_http_net_transport_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->poster = poster;
    /* baseUrl.TrimEnd('/') */
    size_t n = strlen(base_url);
    while (n > 0 && base_url[n - 1] == '/') n--;
    t->base_url = (char *)malloc(n + 1);
    if (!t->base_url) { free(t); return NULL; }
    memcpy(t->base_url, base_url, n);
    t->base_url[n] = '\0';
    return t;
}

void ca_http_net_transport_destroy(ca_http_net_transport_t *t) {
    if (!t) return;
    free(t->base_url);
    free(t->last_url);
    free(t);
}

static ca_transport_kind_t ht_kind(void *self) {
    (void)self;
    return CA_TRANSPORT_HTTP;
}
static bool ht_available(void *self) {
    (void)self;
    return true; /* IsAvailable => true */
}
static int ht_start(void *self) {
    ((ca_http_net_transport_t *)self)->running = true;
    return 0;
}
static int ht_stop(void *self) {
    ((ca_http_net_transport_t *)self)->running = false;
    return 0;
}

/* Build "{base}/messages/{escape(dest)}" or "{base}/messages". NULL on OOM. */
static char *build_url(const char *base, const char *dest) {
    if (dest && dest[0] != '\0') {
        char *esc = ca_http_escape_data_string(dest);
        if (!esc) return NULL;
        size_t need = strlen(base) + strlen("/messages/") + strlen(esc) + 1;
        char *url = (char *)malloc(need);
        if (!url) { free(esc); return NULL; }
        snprintf(url, need, "%s/messages/%s", base, esc);
        free(esc);
        return url;
    }
    size_t need = strlen(base) + strlen("/messages") + 1;
    char *url = (char *)malloc(need);
    if (!url) return NULL;
    snprintf(url, need, "%s/messages", base);
    return url;
}

static int ht_send(void *self, const ca_network_payload_t *payload) {
    ca_http_net_transport_t *t = (ca_http_net_transport_t *)self;
    if (!payload) return -1;

    const char *dest = payload->destination_id; /* is { Length: > 0 } */
    char *url = build_url(t->base_url, dest);
    if (!url) return -1;
    free(t->last_url);
    t->last_url = url;
    t->last_attempts = 0;

    if (!t->poster.post) return -1;

    /* for (attempt = 0; attempt < 3; attempt++) */
    for (int attempt = 0; attempt < 3; ++attempt) {
        t->last_attempts = attempt + 1;
        int status = t->poster.post(
            t->poster.self, url, payload->data, payload->data_len,
            payload->content_type, payload->id, priority_name(payload->priority));
        if (status != CA_HTTP_TRANSIENT && ca_http_status_is_2xx(status)) {
            return 0; /* EnsureSuccessStatusCode passed; return. */
        }
        /* Failure (transient or non-2xx => HttpRequestException in C#).
         * catch (HttpRequestException) when (attempt < 2): delay + retry.
         * On attempt == 2 the exception propagates (terminal failure). */
        if (attempt < 2) {
            /* Task.Delay(2^attempt s) — counted, not slept (deterministic). */
            continue;
        }
        return -1;
    }
    return -1;
}

static bool ht_receive_next(void *self, ca_network_payload_t **out) {
    (void)self;
    if (out) *out = NULL;
    return false; /* yield break; */
}

ca_network_transport_t ca_http_net_transport_as_transport(ca_http_net_transport_t *t) {
    ca_network_transport_t v;
    v.self = t;
    v.kind = ht_kind;
    v.is_available = ht_available;
    v.start = ht_start;
    v.stop = ht_stop;
    v.send = ht_send;
    v.receive_next = ht_receive_next;
    return v;
}

const char *ca_http_net_transport_base_url(const ca_http_net_transport_t *t) {
    return t ? t->base_url : NULL;
}
int ca_http_net_transport_last_attempts(const ca_http_net_transport_t *t) {
    return t ? t->last_attempts : 0;
}
const char *ca_http_net_transport_last_url(const ca_http_net_transport_t *t) {
    return t ? t->last_url : NULL;
}
