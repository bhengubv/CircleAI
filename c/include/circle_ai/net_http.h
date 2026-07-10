#ifndef CIRCLE_AI_NET_HTTP_H
#define CIRCLE_AI_NET_HTTP_H

/*
 * net_http.h — CircleAI.Networking.Http (C11 port).
 *
 * The HTTP network transport. Ports CircleAI.Networking.Http 1:1:
 *
 *   Records   : HttpEndpointDescriptor, HttpRequestSummary, HttpCacheKey
 *   Helpers   : HttpStatusFamily (Is2xx/Is3xx/Is4xx/Is5xx/ShouldRetry)
 *   Metrics   : InMemoryHttpRequestMetrics (endpoints + request log + avg 2xx
 *               latency)
 *   Poster    : IHttpPoster — the injected HttpClient seam. post() returns an
 *               HTTP status code or a transient-failure signal.
 *   Transport : HttpNetworkTransport — INetworkTransport over HttpClient.
 *               Kind==Http, IsAvailable==true. StartAsync/StopAsync flip an
 *               internal running flag. SendAsync POSTs the payload to
 *               {baseUrl}/messages/{EscapeDataString(destinationId)} (or
 *               {baseUrl}/messages when no destination), retrying up to 3 times
 *               with exponential backoff on transient failure — the backoff is
 *               COUNTED, not slept, for determinism. ReceiveAsync yields nothing
 *               (HTTP is request-response).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no hashtable, no pthreads. Durations are milliseconds; timestamps Unix
 * ms UTC, passed in.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "networking.h"   /* ca_network_transport_t, ca_network_payload_t,
                             ca_net_metadata_pair_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * HttpStatusFamily
 * =========================================================================== */

bool ca_http_status_is_2xx(int s);
bool ca_http_status_is_3xx(int s);
bool ca_http_status_is_4xx(int s);
bool ca_http_status_is_5xx(int s);
/* ShouldRetry: 408 || 425 || 429 || 5xx. */
bool ca_http_status_should_retry(int s);

/* ===========================================================================
 * HttpEndpointDescriptor — DefaultHeaders optional (owned pair array, may be
 * absent: has_headers=false models a null IReadOnlyDictionary).
 * =========================================================================== */

typedef struct {
    char                   *method;    /* owned, non-null */
    char                   *base_uri;  /* owned, non-null */
    char                   *path;      /* owned, non-null */
    bool                    has_headers;
    ca_net_metadata_pair_t *default_headers; /* owned array (valid iff has_headers) */
    size_t                  header_count;
} ca_http_endpoint_descriptor_t;

ca_http_endpoint_descriptor_t *ca_http_endpoint_descriptor_new(
    const char *method, const char *base_uri, const char *path,
    bool has_headers, const ca_net_metadata_pair_t *default_headers,
    size_t header_count);
void ca_http_endpoint_descriptor_destroy(ca_http_endpoint_descriptor_t *e);
ca_http_endpoint_descriptor_t *ca_http_endpoint_descriptor_copy(
    const ca_http_endpoint_descriptor_t *e);

/* ===========================================================================
 * HttpRequestSummary
 * =========================================================================== */

typedef struct {
    char   *endpoint_id;   /* owned */
    int     status_code;
    int64_t latency_ms;    /* TimeSpan Latency */
    int     response_bytes;
    int64_t at_unix_ms;
} ca_http_request_summary_t;

void ca_http_request_summary_free(ca_http_request_summary_t *s);
void ca_http_request_summary_free_array(ca_http_request_summary_t *arr,
                                        size_t count);

/* ===========================================================================
 * HttpCacheKey
 * =========================================================================== */

typedef struct {
    char *method;        /* owned */
    char *full_uri;      /* owned */
    char *accept_header; /* owned */
} ca_http_cache_key_t;

ca_http_cache_key_t *ca_http_cache_key_new(const char *method,
                                           const char *full_uri,
                                           const char *accept_header);
void ca_http_cache_key_destroy(ca_http_cache_key_t *k);
ca_http_cache_key_t *ca_http_cache_key_copy(const ca_http_cache_key_t *k);
/* Value equality (record semantics): method, full_uri, accept_header all equal. */
bool ca_http_cache_key_equals(const ca_http_cache_key_t *a,
                              const ca_http_cache_key_t *b);

/* ===========================================================================
 * InMemoryHttpRequestMetrics
 *
 * Register: LWW by id. GetEndpoint: fresh copy or NULL. Log: append.
 * RecentRequests(limit): newest ordered by AtUtc descending. Avg2xxLatencyMs:
 * mean latency of 2xx rows for an endpoint (0.0 when none).
 * =========================================================================== */

typedef struct ca_http_metrics ca_http_metrics_t;

ca_http_metrics_t *ca_http_metrics_create(void);
void ca_http_metrics_destroy(ca_http_metrics_t *m);

int ca_http_metrics_register(ca_http_metrics_t *m, const char *id,
                             const ca_http_endpoint_descriptor_t *d);
ca_http_endpoint_descriptor_t *ca_http_metrics_get_endpoint(
    const ca_http_metrics_t *m, const char *id);
int ca_http_metrics_log(ca_http_metrics_t *m, const char *endpoint_id,
                        int status_code, int64_t latency_ms, int response_bytes,
                        int64_t at_unix_ms);
ca_http_request_summary_t *ca_http_metrics_recent_requests(
    const ca_http_metrics_t *m, int limit, size_t *count);
double ca_http_metrics_avg_2xx_latency_ms(const ca_http_metrics_t *m,
                                          const char *endpoint_id);

/* ===========================================================================
 * IHttpPoster — injected HttpClient seam (vtable).
 *
 * post(): POST `data` (len bytes, content_type) to `url` with the X-Payload-Id
 * and X-Payload-Priority headers. Returns an HTTP status code (>=100), or
 * CA_HTTP_TRANSIENT (0) to model an HttpRequestException thrown before any
 * response (connection failure). In the C# both a connection failure AND a
 * non-2xx response raise HttpRequestException (PostAsync / EnsureSuccessStatusCode
 * respectively), so BOTH are retried while attempt < 2; only a 2xx status is
 * success. This port applies the same rule: post() returning CA_HTTP_TRANSIENT
 * or any non-2xx status is a retryable failure; a 2xx status ends the loop.
 * =========================================================================== */

#define CA_HTTP_TRANSIENT 0  /* HttpRequestException thrown before a response */

typedef struct {
    void *self;
    int (*post)(void *self, const char *url, const uint8_t *data, size_t len,
                const char *content_type, const char *payload_id,
                const char *priority_name);
} ca_http_poster_t;

/* ===========================================================================
 * HttpNetworkTransport
 *
 * SendAsync outcome codes (return of ca_http_net_transport send()):
 *   0   : a 2xx response was received on some attempt (success)
 *  -1   : all attempts failed (transient and/or non-2xx). In the C# the final
 *         (attempt==2) HttpRequestException propagates to the caller; this port
 *         surfaces that terminal failure as -1 so a test can observe it.
 * Use ca_http_net_transport_last_attempts() to read how many POSTs were issued and
 * ca_http_net_transport_last_url() for the URL that was targeted.
 * =========================================================================== */

typedef struct ca_http_net_transport ca_http_net_transport_t;

/* Create over an injected poster + base URL (trailing '/' trimmed, like the C#).
 * base_url must be non-empty. NULL on OOM / empty base_url. */
ca_http_net_transport_t *ca_http_net_transport_create(ca_http_poster_t poster,
                                              const char *base_url);
void ca_http_net_transport_destroy(ca_http_net_transport_t *t);
ca_network_transport_t ca_http_net_transport_as_transport(ca_http_net_transport_t *t);
/* The normalised base URL (borrowed). */
const char *ca_http_net_transport_base_url(const ca_http_net_transport_t *t);
/* Number of POST attempts issued by the last SendAsync (0..3). */
int ca_http_net_transport_last_attempts(const ca_http_net_transport_t *t);
/* The URL targeted by the last SendAsync (borrowed, or NULL if none sent). */
const char *ca_http_net_transport_last_url(const ca_http_net_transport_t *t);

/* Uri.EscapeDataString — percent-encode all but the RFC 3986 unreserved set
 * (A-Z a-z 0-9 - _ . ~). Returns a freshly-allocated string (caller frees) or
 * NULL on OOM. Exposed for testing the URL-building contract. */
char *ca_http_escape_data_string(const char *s);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_NET_HTTP_H */
