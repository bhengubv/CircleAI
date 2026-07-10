/*
 * test_net_http.c — CircleAI.Networking.Http (net_http.h).
 *
 * Verifies:
 *   Helpers  : HttpStatusFamily Is2xx/3xx/4xx/5xx + ShouldRetry
 *   Escape   : Uri.EscapeDataString percent-encoding of reserved chars
 *   Records  : endpoint / cache key new+copy + cache-key value equality
 *   Metrics  : Register LWW, GetEndpoint, Log + RecentRequests desc by AtUtc,
 *              Avg2xxLatencyMs (2xx only)
 *   Transport: Kind==Http, IsAvailable==true, base-URL trailing-slash trim,
 *              URL building ({base}/messages/{escape(dest)} vs {base}/messages),
 *              headers passed (id + priority name), 2xx success first try,
 *              transient-then-success retry, terminal failure after 3 attempts
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <stdlib.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

#define T0 1700000000000LL

/* ---- scripted test poster ---- */
typedef struct {
    int         statuses[8]; /* status to return on each attempt (0=transient) */
    int         n;
    int         calls;
    char        last_url[256];
    char        last_content_type[64];
    char        last_id[64];
    char        last_priority[32];
} test_poster_t;

static int tp_post(void *self, const char *url, const uint8_t *data, size_t len,
                   const char *content_type, const char *payload_id,
                   const char *priority_name) {
    test_poster_t *p = (test_poster_t *)self;
    (void)data; (void)len;
    strncpy(p->last_url, url ? url : "", sizeof(p->last_url) - 1);
    p->last_url[sizeof(p->last_url) - 1] = '\0';
    strncpy(p->last_content_type, content_type ? content_type : "",
            sizeof(p->last_content_type) - 1);
    p->last_content_type[sizeof(p->last_content_type) - 1] = '\0';
    strncpy(p->last_id, payload_id ? payload_id : "", sizeof(p->last_id) - 1);
    p->last_id[sizeof(p->last_id) - 1] = '\0';
    strncpy(p->last_priority, priority_name ? priority_name : "",
            sizeof(p->last_priority) - 1);
    p->last_priority[sizeof(p->last_priority) - 1] = '\0';
    int idx = p->calls < p->n ? p->calls : p->n - 1;
    p->calls++;
    return p->statuses[idx];
}

static ca_http_poster_t make_poster(test_poster_t *p) {
    ca_http_poster_t v;
    v.self = p;
    v.post = tp_post;
    return v;
}

static void test_status_family(void) {
    assert(ca_http_status_is_2xx(200) && ca_http_status_is_2xx(299));
    assert(!ca_http_status_is_2xx(300));
    assert(ca_http_status_is_3xx(301) && ca_http_status_is_4xx(404) &&
           ca_http_status_is_5xx(503));
    assert(ca_http_status_should_retry(408));
    assert(ca_http_status_should_retry(425));
    assert(ca_http_status_should_retry(429));
    assert(ca_http_status_should_retry(500));
    assert(!ca_http_status_should_retry(404));
    assert(!ca_http_status_should_retry(200));
}

static void test_escape(void) {
    char *e = ca_http_escape_data_string("a b/c?d=1&x");
    assert(e);
    /* space->%20, '/'->%2F, '?'->%3F, '='->%3D, '&'->%26 */
    assert(strcmp(e, "a%20b%2Fc%3Fd%3D1%26x") == 0);
    free(e);
    /* unreserved passthrough */
    e = ca_http_escape_data_string("Aa0-_.~");
    assert(strcmp(e, "Aa0-_.~") == 0);
    free(e);
}

static void test_records(void) {
    ca_net_metadata_pair_t hdr[] = { { "Authorization", "Bearer x" } };
    ca_http_endpoint_descriptor_t *e = ca_http_endpoint_descriptor_new(
        "POST", "https://api", "/v1/send", true, hdr, 1);
    assert(e && strcmp(e->method, "POST") == 0 && e->has_headers &&
           e->header_count == 1 &&
           strcmp(e->default_headers[0].key, "Authorization") == 0);
    ca_http_endpoint_descriptor_t *ec = ca_http_endpoint_descriptor_copy(e);
    assert(ec && ec->default_headers != e->default_headers &&
           strcmp(ec->default_headers[0].value, "Bearer x") == 0);
    ca_http_endpoint_descriptor_destroy(ec);
    ca_http_endpoint_descriptor_destroy(e);

    /* null headers */
    ca_http_endpoint_descriptor_t *e2 = ca_http_endpoint_descriptor_new(
        "GET", "https://api", "/v1", false, NULL, 0);
    assert(e2 && !e2->has_headers && e2->default_headers == NULL);
    ca_http_endpoint_descriptor_destroy(e2);

    /* cache key value equality */
    ca_http_cache_key_t *k1 = ca_http_cache_key_new("GET", "https://a/x", "application/json");
    ca_http_cache_key_t *k2 = ca_http_cache_key_new("GET", "https://a/x", "application/json");
    ca_http_cache_key_t *k3 = ca_http_cache_key_new("POST", "https://a/x", "application/json");
    assert(ca_http_cache_key_equals(k1, k2));
    assert(!ca_http_cache_key_equals(k1, k3));
    ca_http_cache_key_t *kc = ca_http_cache_key_copy(k1);
    assert(ca_http_cache_key_equals(kc, k1));
    ca_http_cache_key_destroy(kc);
    ca_http_cache_key_destroy(k1);
    ca_http_cache_key_destroy(k2);
    ca_http_cache_key_destroy(k3);
}

static void test_metrics(void) {
    ca_http_metrics_t *m = ca_http_metrics_create();
    assert(m);
    ca_http_endpoint_descriptor_t *d = ca_http_endpoint_descriptor_new(
        "POST", "https://api", "/send", false, NULL, 0);
    assert(ca_http_metrics_register(m, "ep", d) == 0);
    ca_http_endpoint_descriptor_destroy(d);
    ca_http_endpoint_descriptor_t *g = ca_http_metrics_get_endpoint(m, "ep");
    assert(g && strcmp(g->path, "/send") == 0);
    ca_http_endpoint_descriptor_destroy(g);

    /* Log rows: two 2xx + one 5xx; Avg2xxLatencyMs averages the 2xx only. */
    assert(ca_http_metrics_log(m, "ep", 200, 100, 512, T0 + 5) == 0);
    assert(ca_http_metrics_log(m, "ep", 500, 999, 0, T0 + 20) == 0);
    assert(ca_http_metrics_log(m, "ep", 204, 200, 0, T0 + 10) == 0);
    assert(ca_http_metrics_avg_2xx_latency_ms(m, "ep") == 150.0); /* (100+200)/2 */
    assert(ca_http_metrics_avg_2xx_latency_ms(m, "none") == 0.0);

    /* RecentRequests desc by AtUtc: 500(T0+20), 204(T0+10), 200(T0+5). */
    size_t n = 0;
    ca_http_request_summary_t *rq = ca_http_metrics_recent_requests(m, 2, &n);
    assert(rq && n == 2);
    assert(rq[0].status_code == 500);
    assert(rq[1].status_code == 204);
    ca_http_request_summary_free_array(rq, n);

    ca_http_metrics_destroy(m);
}

static void test_transport_success(void) {
    test_poster_t poster = {0};
    poster.statuses[0] = 200; poster.n = 1;

    /* base URL with trailing slash gets trimmed. */
    ca_http_net_transport_t *t =
        ca_http_net_transport_create(make_poster(&poster), "https://host/api/");
    assert(t);
    assert(strcmp(ca_http_net_transport_base_url(t), "https://host/api") == 0);

    ca_network_transport_t nt = ca_http_net_transport_as_transport(t);
    assert(nt.kind(nt.self) == CA_TRANSPORT_HTTP);
    assert(nt.is_available(nt.self) == true);
    assert(nt.start(nt.self) == 0);

    const uint8_t body[] = { 1, 2 };
    ca_network_payload_t *p = ca_network_payload_create(
        body, 2, "user id/42", CA_MSG_PRIORITY_URGENT, NULL, false, 0, T0,
        "fixed-id-1234");
    assert(p);
    assert(nt.send(nt.self, p) == 0);          /* 2xx first try */
    assert(ca_http_net_transport_last_attempts(t) == 1);
    /* URL: base + /messages/ + escaped destination. */
    assert(strcmp(poster.last_url,
                  "https://host/api/messages/user%20id%2F42") == 0);
    assert(strcmp(poster.last_content_type, "application/octet-stream") == 0);
    assert(strcmp(poster.last_id, "fixed-id-1234") == 0);
    assert(strcmp(poster.last_priority, "Urgent") == 0);
    ca_network_payload_destroy(p);

    /* No destination => {base}/messages. */
    ca_network_payload_t *p2 = ca_network_payload_create(
        body, 2, NULL, CA_MSG_PRIORITY_NORMAL, NULL, false, 0, T0, "id2");
    poster.calls = 0;
    assert(nt.send(nt.self, p2) == 0);
    assert(strcmp(poster.last_url, "https://host/api/messages") == 0);
    assert(strcmp(poster.last_priority, "Normal") == 0);
    ca_network_payload_destroy(p2);

    ca_http_net_transport_destroy(t);
}

static void test_transport_retry(void) {
    /* transient, transient, 201 => success on the 3rd attempt. */
    test_poster_t poster = {0};
    poster.statuses[0] = CA_HTTP_TRANSIENT;
    poster.statuses[1] = CA_HTTP_TRANSIENT;
    poster.statuses[2] = 201;
    poster.n = 3;

    ca_http_net_transport_t *t =
        ca_http_net_transport_create(make_poster(&poster), "https://h");
    ca_network_transport_t nt = ca_http_net_transport_as_transport(t);
    const uint8_t body[] = { 7 };
    ca_network_payload_t *p = ca_network_payload_create(
        body, 1, "d", CA_MSG_PRIORITY_HIGH, NULL, false, 0, T0, "idx");
    assert(nt.send(nt.self, p) == 0);
    assert(ca_http_net_transport_last_attempts(t) == 3);
    assert(poster.calls == 3);
    ca_network_payload_destroy(p);
    ca_http_net_transport_destroy(t);
}

static void test_transport_terminal_failure(void) {
    /* three transient failures => terminal failure (-1), 3 attempts. */
    test_poster_t poster = {0};
    poster.statuses[0] = CA_HTTP_TRANSIENT;
    poster.n = 1; /* always transient */

    ca_http_net_transport_t *t =
        ca_http_net_transport_create(make_poster(&poster), "https://h");
    ca_network_transport_t nt = ca_http_net_transport_as_transport(t);
    const uint8_t body[] = { 7 };
    ca_network_payload_t *p = ca_network_payload_create(
        body, 1, "d", CA_MSG_PRIORITY_LOW, NULL, false, 0, T0, "idx");
    assert(nt.send(nt.self, p) == -1);
    assert(ca_http_net_transport_last_attempts(t) == 3);
    assert(poster.calls == 3);
    ca_network_payload_destroy(p);
    ca_http_net_transport_destroy(t);

    /* A non-2xx status (e.g. 404) is likewise retried and ends terminal. */
    test_poster_t p404 = {0};
    p404.statuses[0] = 404; p404.n = 1;
    ca_http_net_transport_t *t2 =
        ca_http_net_transport_create(make_poster(&p404), "https://h");
    ca_network_transport_t nt2 = ca_http_net_transport_as_transport(t2);
    ca_network_payload_t *pp = ca_network_payload_create(
        body, 1, "d", CA_MSG_PRIORITY_LOW, NULL, false, 0, T0, "idx");
    assert(nt2.send(nt2.self, pp) == -1);
    assert(p404.calls == 3);
    ca_network_payload_destroy(pp);
    ca_http_net_transport_destroy(t2);

    /* base_url empty => create fails. */
    test_poster_t pe = {0};
    assert(ca_http_net_transport_create(make_poster(&pe), "") == NULL);
}

int main(void) {
    test_status_family();
    test_escape();
    test_records();
    test_metrics();
    test_transport_success();
    test_transport_retry();
    test_transport_terminal_failure();
    return 0;
}
