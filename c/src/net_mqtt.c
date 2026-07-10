/*
 * net_mqtt.c — CircleAI.Networking.Mqtt (C11 port).
 *
 * MqttQos, the topic/retained/client-descriptor records, InMemoryMqttBroker
 * (subscription tracking + MQTT wildcard match + retained store), the injected
 * IMqttClientAdapter seam + a deterministic in-memory adapter, and
 * MqttNetworkTransport (INetworkTransport).
 *
 * Pure C11 + libc.
 */

#include "circle_ai/net_mqtt.h"

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

static uint8_t *dup_bytes(const uint8_t *src, size_t len) {
    if (len == 0) {
        uint8_t *p = (uint8_t *)malloc(1); /* non-null owned zero-length buffer */
        return p;
    }
    uint8_t *p = (uint8_t *)malloc(len);
    if (p) memcpy(p, src, len);
    return p;
}

/* whitespace / null check mirroring string.IsNullOrWhiteSpace */
static bool is_null_or_ws(const char *s) {
    if (!s) return true;
    for (; *s; ++s) {
        unsigned char c = (unsigned char)*s;
        if (c != ' ' && c != '\t' && c != '\n' && c != '\r' && c != '\f' &&
            c != '\v')
            return false;
    }
    return true;
}

/* ===========================================================================
 * MqttTopicDescriptor
 * =========================================================================== */

ca_mqtt_topic_descriptor_t *ca_mqtt_topic_descriptor_new(const char *topic,
                                                         ca_mqtt_qos_t qos) {
    ca_mqtt_topic_descriptor_t *d =
        (ca_mqtt_topic_descriptor_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->topic = dup_or_empty(topic);
    if (!d->topic) { free(d); return NULL; }
    d->qos = qos;
    return d;
}
void ca_mqtt_topic_descriptor_destroy(ca_mqtt_topic_descriptor_t *d) {
    if (!d) return;
    free(d->topic);
    free(d);
}
ca_mqtt_topic_descriptor_t *ca_mqtt_topic_descriptor_copy(
    const ca_mqtt_topic_descriptor_t *d) {
    if (!d) return NULL;
    return ca_mqtt_topic_descriptor_new(d->topic, d->qos);
}

/* ===========================================================================
 * MqttRetainedMessage
 * =========================================================================== */

ca_mqtt_retained_message_t *ca_mqtt_retained_message_new(
    const char *topic, const uint8_t *payload, size_t payload_len,
    int64_t retained_at_unix_ms) {
    ca_mqtt_retained_message_t *m =
        (ca_mqtt_retained_message_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    m->topic = dup_or_empty(topic);
    m->payload = dup_bytes(payload, payload_len);
    if (!m->topic || !m->payload) {
        ca_mqtt_retained_message_destroy(m);
        return NULL;
    }
    m->payload_len = payload_len;
    m->retained_at_unix_ms = retained_at_unix_ms;
    return m;
}
void ca_mqtt_retained_message_destroy(ca_mqtt_retained_message_t *m) {
    if (!m) return;
    free(m->topic);
    free(m->payload);
    free(m);
}
ca_mqtt_retained_message_t *ca_mqtt_retained_message_copy(
    const ca_mqtt_retained_message_t *m) {
    if (!m) return NULL;
    return ca_mqtt_retained_message_new(m->topic, m->payload, m->payload_len,
                                        m->retained_at_unix_ms);
}

/* ===========================================================================
 * MqttClientDescriptor
 * =========================================================================== */

ca_mqtt_client_descriptor_t *ca_mqtt_client_descriptor_new(
    const char *client_id, const char *host, int port, bool use_tls,
    int64_t keep_alive_ms) {
    ca_mqtt_client_descriptor_t *d =
        (ca_mqtt_client_descriptor_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->client_id = dup_or_empty(client_id);
    d->host = dup_or_empty(host);
    if (!d->client_id || !d->host) {
        ca_mqtt_client_descriptor_destroy(d);
        return NULL;
    }
    d->port = port;
    d->use_tls = use_tls;
    d->keep_alive_ms = keep_alive_ms;
    return d;
}
void ca_mqtt_client_descriptor_destroy(ca_mqtt_client_descriptor_t *d) {
    if (!d) return;
    free(d->client_id);
    free(d->host);
    free(d);
}
ca_mqtt_client_descriptor_t *ca_mqtt_client_descriptor_copy(
    const ca_mqtt_client_descriptor_t *d) {
    if (!d) return NULL;
    return ca_mqtt_client_descriptor_new(d->client_id, d->host, d->port,
                                         d->use_tls, d->keep_alive_ms);
}

/* ===========================================================================
 * InMemoryMqttBroker
 * =========================================================================== */

typedef struct {
    char  *client_id;   /* owned */
    char **filters;     /* owned array of owned strings (deduped, ordinal) */
    size_t filter_count;
    size_t filter_cap;
} mqtt_sub_entry_t;

struct ca_mqtt_broker {
    ca_mqtt_client_descriptor_t **clients; /* owned array (LWW by ClientId) */
    size_t                        client_count;
    size_t                        client_cap;

    mqtt_sub_entry_t             *subs;     /* owned array (per client id) */
    size_t                        sub_count;
    size_t                        sub_cap;

    ca_mqtt_retained_message_t  **retained; /* owned array (LWW by Topic) */
    size_t                        ret_count;
    size_t                        ret_cap;
};

ca_mqtt_broker_t *ca_mqtt_broker_create(void) {
    return (ca_mqtt_broker_t *)calloc(1, sizeof(ca_mqtt_broker_t));
}

void ca_mqtt_broker_destroy(ca_mqtt_broker_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->client_count; ++i)
        ca_mqtt_client_descriptor_destroy(b->clients[i]);
    free(b->clients);
    for (size_t i = 0; i < b->sub_count; ++i) {
        free(b->subs[i].client_id);
        for (size_t j = 0; j < b->subs[i].filter_count; ++j)
            free(b->subs[i].filters[j]);
        free(b->subs[i].filters);
    }
    free(b->subs);
    for (size_t i = 0; i < b->ret_count; ++i)
        ca_mqtt_retained_message_destroy(b->retained[i]);
    free(b->retained);
    free(b);
}

static ptrdiff_t broker_client_index(const ca_mqtt_broker_t *b,
                                     const char *id) {
    for (size_t i = 0; i < b->client_count; ++i)
        if (strcmp(b->clients[i]->client_id, id) == 0) return (ptrdiff_t)i;
    return -1;
}

int ca_mqtt_broker_connect(ca_mqtt_broker_t *b,
                           const ca_mqtt_client_descriptor_t *c) {
    if (!b || !c) return -1;
    ca_mqtt_client_descriptor_t *copy = ca_mqtt_client_descriptor_copy(c);
    if (!copy) return -1;
    ptrdiff_t idx = broker_client_index(b, c->client_id);
    if (idx >= 0) {
        ca_mqtt_client_descriptor_destroy(b->clients[idx]);
        b->clients[idx] = copy;
        return 0;
    }
    if (b->client_count == b->client_cap) {
        size_t nc = b->client_cap ? b->client_cap * 2 : 4;
        ca_mqtt_client_descriptor_t **np =
            (ca_mqtt_client_descriptor_t **)realloc(b->clients,
                                                    nc * sizeof(*np));
        if (!np) { ca_mqtt_client_descriptor_destroy(copy); return -1; }
        b->clients = np;
        b->client_cap = nc;
    }
    b->clients[b->client_count++] = copy;
    return 0;
}

void ca_mqtt_broker_disconnect(ca_mqtt_broker_t *b, const char *client_id) {
    if (!b || !client_id) return;
    ptrdiff_t idx = broker_client_index(b, client_id);
    if (idx < 0) return;
    ca_mqtt_client_descriptor_destroy(b->clients[idx]);
    for (size_t i = (size_t)idx; i + 1 < b->client_count; ++i)
        b->clients[i] = b->clients[i + 1];
    b->client_count--;
}

int ca_mqtt_broker_connected_clients(const ca_mqtt_broker_t *b,
                                     ca_mqtt_client_descriptor_t ***out,
                                     size_t *count) {
    if (!b || !out || !count) {
        if (out) *out = NULL;
        if (count) *count = SIZE_MAX;
        return -1;
    }
    if (b->client_count == 0) { *out = NULL; *count = 0; return 0; }
    ca_mqtt_client_descriptor_t **arr =
        (ca_mqtt_client_descriptor_t **)calloc(b->client_count, sizeof(*arr));
    if (!arr) { *out = NULL; *count = SIZE_MAX; return -1; }
    for (size_t i = 0; i < b->client_count; ++i) {
        arr[i] = ca_mqtt_client_descriptor_copy(b->clients[i]);
        if (!arr[i]) {
            for (size_t j = 0; j < i; ++j)
                ca_mqtt_client_descriptor_destroy(arr[j]);
            free(arr);
            *out = NULL; *count = SIZE_MAX;
            return -1;
        }
    }
    *out = arr;
    *count = b->client_count;
    return 0;
}

static ptrdiff_t broker_sub_index(const ca_mqtt_broker_t *b, const char *id) {
    for (size_t i = 0; i < b->sub_count; ++i)
        if (strcmp(b->subs[i].client_id, id) == 0) return (ptrdiff_t)i;
    return -1;
}

int ca_mqtt_broker_subscribe(ca_mqtt_broker_t *b, const char *client_id,
                             const char *topic_filter) {
    if (!b) return -1;
    if (is_null_or_ws(client_id) || is_null_or_ws(topic_filter)) return -1;

    ptrdiff_t idx = broker_sub_index(b, client_id);
    mqtt_sub_entry_t *e;
    if (idx >= 0) {
        e = &b->subs[idx];
    } else {
        if (b->sub_count == b->sub_cap) {
            size_t nc = b->sub_cap ? b->sub_cap * 2 : 4;
            mqtt_sub_entry_t *ns =
                (mqtt_sub_entry_t *)realloc(b->subs, nc * sizeof(*ns));
            if (!ns) return -1;
            b->subs = ns;
            b->sub_cap = nc;
        }
        e = &b->subs[b->sub_count];
        memset(e, 0, sizeof(*e));
        e->client_id = dup_or_empty(client_id);
        if (!e->client_id) return -1;
        b->sub_count++;
    }
    /* HashSet.Add — dedupe ordinal. */
    for (size_t i = 0; i < e->filter_count; ++i)
        if (strcmp(e->filters[i], topic_filter) == 0) return 0;
    if (e->filter_count == e->filter_cap) {
        size_t nc = e->filter_cap ? e->filter_cap * 2 : 4;
        char **nf = (char **)realloc(e->filters, nc * sizeof(*nf));
        if (!nf) return -1;
        e->filters = nf;
        e->filter_cap = nc;
    }
    char *f = dup_or_empty(topic_filter);
    if (!f) return -1;
    e->filters[e->filter_count++] = f;
    return 0;
}

/* Split `s` on '/' into a freshly-allocated array of freshly-allocated tokens.
 * *n receives the count. Returns NULL only on OOM (a non-empty string always
 * yields >=1 token; matches string.Split('/')). */
static char **split_slash(const char *s, size_t *n) {
    size_t count = 1;
    for (const char *p = s; *p; ++p)
        if (*p == '/') count++;
    char **out = (char **)calloc(count, sizeof(*out));
    if (!out) { *n = 0; return NULL; }
    size_t idx = 0;
    const char *start = s;
    for (const char *p = s;; ++p) {
        if (*p == '/' || *p == '\0') {
            size_t seg = (size_t)(p - start);
            char *tok = (char *)malloc(seg + 1);
            if (!tok) {
                for (size_t j = 0; j < idx; ++j) free(out[j]);
                free(out);
                *n = 0;
                return NULL;
            }
            memcpy(tok, start, seg);
            tok[seg] = '\0';
            out[idx++] = tok;
            if (*p == '\0') break;
            start = p + 1;
        }
    }
    *n = count;
    return out;
}
static void free_tokens(char **a, size_t n) {
    if (!a) return;
    for (size_t i = 0; i < n; ++i) free(a[i]);
    free(a);
}

bool ca_mqtt_broker_matches(const char *topic, const char *topic_filter) {
    /* string.IsNullOrEmpty(topic) || string.IsNullOrEmpty(topicFilter) => false */
    if (!topic || !*topic || !topic_filter || !*topic_filter) return false;
    size_t tn = 0, fn = 0;
    char **t = split_slash(topic, &tn);
    char **f = split_slash(topic_filter, &fn);
    bool result = false;
    if (!t || !f) goto done;
    for (size_t i = 0; i < fn; ++i) {
        if (strcmp(f[i], "#") == 0) { result = true; goto done; }
        if (i >= tn) { result = false; goto done; }
        if (strcmp(f[i], "+") == 0) continue;
        if (strcmp(f[i], t[i]) != 0) { result = false; goto done; }
    }
    result = (tn == fn);
done:
    free_tokens(t, tn);
    free_tokens(f, fn);
    return result;
}

static ptrdiff_t broker_ret_index(const ca_mqtt_broker_t *b, const char *topic) {
    for (size_t i = 0; i < b->ret_count; ++i)
        if (strcmp(b->retained[i]->topic, topic) == 0) return (ptrdiff_t)i;
    return -1;
}

int ca_mqtt_broker_publish_retained(ca_mqtt_broker_t *b,
                                    const ca_mqtt_retained_message_t *m) {
    if (!b || !m) return -1;
    ca_mqtt_retained_message_t *copy = ca_mqtt_retained_message_copy(m);
    if (!copy) return -1;
    ptrdiff_t idx = broker_ret_index(b, m->topic);
    if (idx >= 0) {
        ca_mqtt_retained_message_destroy(b->retained[idx]);
        b->retained[idx] = copy;
        return 0;
    }
    if (b->ret_count == b->ret_cap) {
        size_t nc = b->ret_cap ? b->ret_cap * 2 : 4;
        ca_mqtt_retained_message_t **nr =
            (ca_mqtt_retained_message_t **)realloc(b->retained,
                                                   nc * sizeof(*nr));
        if (!nr) { ca_mqtt_retained_message_destroy(copy); return -1; }
        b->retained = nr;
        b->ret_cap = nc;
    }
    b->retained[b->ret_count++] = copy;
    return 0;
}

ca_mqtt_retained_message_t *ca_mqtt_broker_get_retained(
    const ca_mqtt_broker_t *b, const char *topic) {
    if (!b || !topic) return NULL;
    ptrdiff_t idx = broker_ret_index(b, topic);
    if (idx < 0) return NULL;
    return ca_mqtt_retained_message_copy(b->retained[idx]);
}

int ca_mqtt_broker_matching_subscribers(const ca_mqtt_broker_t *b,
                                        const char *topic, char ***out,
                                        size_t *count) {
    if (!b || !topic || !out || !count) {
        if (out) *out = NULL;
        if (count) *count = SIZE_MAX;
        return -1;
    }
    char **arr = NULL;
    size_t n = 0, cap = 0;
    for (size_t i = 0; i < b->sub_count; ++i) {
        bool any = false;
        for (size_t j = 0; j < b->subs[i].filter_count && !any; ++j)
            if (ca_mqtt_broker_matches(topic, b->subs[i].filters[j]))
                any = true;
        if (!any) continue;
        if (n == cap) {
            size_t nc = cap ? cap * 2 : 4;
            char **na = (char **)realloc(arr, nc * sizeof(*na));
            if (!na) {
                for (size_t k = 0; k < n; ++k) free(arr[k]);
                free(arr);
                *out = NULL; *count = SIZE_MAX;
                return -1;
            }
            arr = na;
            cap = nc;
        }
        char *id = dup_or_empty(b->subs[i].client_id);
        if (!id) {
            for (size_t k = 0; k < n; ++k) free(arr[k]);
            free(arr);
            *out = NULL; *count = SIZE_MAX;
            return -1;
        }
        arr[n++] = id;
    }
    *out = arr; /* may be NULL when n==0 */
    *count = n;
    return 0;
}

/* ===========================================================================
 * Unbounded FIFO of NetworkPayload* (transport inbound channel)
 * =========================================================================== */

typedef struct {
    ca_network_payload_t **items;
    size_t head, count, cap;
} payload_fifo_t;

static bool pf_push(payload_fifo_t *q, ca_network_payload_t *owned) {
    if (q->count == q->cap) {
        if (q->head > 0) {
            size_t live = q->count - q->head;
            memmove(q->items, q->items + q->head, live * sizeof(*q->items));
            q->count = live;
            q->head = 0;
        }
        if (q->count == q->cap) {
            size_t nc = q->cap ? q->cap * 2 : 4;
            ca_network_payload_t **ni =
                (ca_network_payload_t **)realloc(q->items, nc * sizeof(*ni));
            if (!ni) return false;
            q->items = ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = owned;
    return true;
}
static ca_network_payload_t *pf_pop(payload_fifo_t *q) {
    if (q->head >= q->count) return NULL;
    ca_network_payload_t *p = q->items[q->head];
    q->items[q->head++] = NULL;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return p;
}
static void pf_free(payload_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i)
        ca_network_payload_destroy(q->items[i]);
    free(q->items);
    q->items = NULL;
    q->head = q->count = q->cap = 0;
}

/* ===========================================================================
 * Inbound writer — the ChannelWriter<NetworkPayload> seam
 * =========================================================================== */

struct ca_mqtt_inbound_writer {
    payload_fifo_t *queue;   /* borrowed — owned by the transport */
    bool           *open;    /* borrowed open flag */
};

int ca_mqtt_inbound_write(ca_mqtt_inbound_writer_t *writer, const uint8_t *data,
                          size_t len) {
    if (!writer) return -1;
    if (writer->open && !*writer->open) return -1;
    /* NetworkPayload.Create(bytes): id from guid-N, no destination, now=0 here
     * (host injects the deterministic timestamp). We build the envelope with a
     * fresh guid id and created_at 0 to mirror Create's defaults except time,
     * which is not observable through this seam. */
    char id[33];
    ca_net_new_guid_n(id);
    ca_network_payload_t *p = ca_network_payload_create(
        data, len, NULL, CA_MSG_PRIORITY_NORMAL, NULL, false, 0, 0, id);
    if (!p) return -1;
    if (!pf_push(writer->queue, p)) {
        ca_network_payload_destroy(p);
        return -1;
    }
    return 0;
}

/* ===========================================================================
 * In-memory IMqttClientAdapter
 * =========================================================================== */

struct ca_mem_mqtt_adapter {
    bool                      connected;
    ca_mqtt_inbound_writer_t *writer;      /* borrowed, set on connect */
    size_t                    publish_count;
    char                     *last_topic;  /* owned, may be NULL */
    ca_mqtt_qos_t             last_qos;
    char                     *last_sub;     /* owned, may be NULL */
};

ca_mem_mqtt_adapter_t *ca_mem_mqtt_adapter_create(bool start_connected) {
    ca_mem_mqtt_adapter_t *a =
        (ca_mem_mqtt_adapter_t *)calloc(1, sizeof(*a));
    if (!a) return NULL;
    a->connected = start_connected;
    return a;
}
void ca_mem_mqtt_adapter_destroy(ca_mem_mqtt_adapter_t *a) {
    if (!a) return;
    free(a->last_topic);
    free(a->last_sub);
    free(a);
}
void ca_mem_mqtt_adapter_set_connected(ca_mem_mqtt_adapter_t *a, bool v) {
    if (a) a->connected = v;
}

static bool mma_is_connected(void *self) {
    return ((ca_mem_mqtt_adapter_t *)self)->connected;
}
static int mma_connect(void *self, ca_mqtt_inbound_writer_t *writer) {
    ca_mem_mqtt_adapter_t *a = (ca_mem_mqtt_adapter_t *)self;
    a->writer = writer;
    a->connected = true;
    return 0;
}
static int mma_subscribe(void *self, const char *topic_filter) {
    ca_mem_mqtt_adapter_t *a = (ca_mem_mqtt_adapter_t *)self;
    char *copy = dup_or_null(topic_filter);
    if (topic_filter && !copy) return -1;
    free(a->last_sub);
    a->last_sub = copy;
    return 0;
}
static int mma_publish(void *self, const char *topic, const uint8_t *data,
                       size_t len, ca_mqtt_qos_t qos) {
    ca_mem_mqtt_adapter_t *a = (ca_mem_mqtt_adapter_t *)self;
    (void)data;
    (void)len;
    char *copy = dup_or_null(topic);
    if (topic && !copy) return -1;
    free(a->last_topic);
    a->last_topic = copy;
    a->last_qos = qos;
    a->publish_count++;
    return 0;
}
static int mma_disconnect(void *self) {
    ca_mem_mqtt_adapter_t *a = (ca_mem_mqtt_adapter_t *)self;
    a->connected = false;
    a->writer = NULL;
    return 0;
}

ca_mqtt_client_adapter_t ca_mem_mqtt_adapter_as_adapter(
    ca_mem_mqtt_adapter_t *a) {
    ca_mqtt_client_adapter_t v;
    v.self = a;
    v.is_connected = mma_is_connected;
    v.connect = mma_connect;
    v.subscribe = mma_subscribe;
    v.publish = mma_publish;
    v.disconnect = mma_disconnect;
    return v;
}

int ca_mem_mqtt_adapter_deliver(ca_mem_mqtt_adapter_t *a, const uint8_t *data,
                                size_t len) {
    if (!a) return -1;
    if (!a->connected || !a->writer) return -1;
    return ca_mqtt_inbound_write(a->writer, data, len);
}
size_t ca_mem_mqtt_adapter_publish_count(const ca_mem_mqtt_adapter_t *a) {
    return a ? a->publish_count : 0;
}
const char *ca_mem_mqtt_adapter_last_topic(const ca_mem_mqtt_adapter_t *a) {
    return a ? a->last_topic : NULL;
}
ca_mqtt_qos_t ca_mem_mqtt_adapter_last_qos(const ca_mem_mqtt_adapter_t *a) {
    return a ? a->last_qos : CA_MQTT_QOS_AT_MOST_ONCE;
}
const char *ca_mem_mqtt_adapter_last_subscription(
    const ca_mem_mqtt_adapter_t *a) {
    return a ? a->last_sub : NULL;
}

/* ===========================================================================
 * MqttNetworkTransport
 * =========================================================================== */

struct ca_mqtt_transport {
    ca_mqtt_client_adapter_t adapter;       /* borrowed vtable */
    char                    *local_client_id; /* owned */
    payload_fifo_t           inbound;
    bool                     inbound_open;
    ca_mqtt_inbound_writer_t writer;        /* points at inbound + inbound_open */
};

ca_mqtt_transport_t *ca_mqtt_transport_create(ca_mqtt_client_adapter_t adapter,
                                              const char *client_id) {
    if (!client_id || !*client_id) return NULL;
    ca_mqtt_transport_t *t = (ca_mqtt_transport_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->adapter = adapter;
    t->local_client_id = dup_or_empty(client_id);
    if (!t->local_client_id) { free(t); return NULL; }
    t->inbound_open = true;
    t->writer.queue = &t->inbound;
    t->writer.open = &t->inbound_open;
    return t;
}

void ca_mqtt_transport_destroy(ca_mqtt_transport_t *t) {
    if (!t) return;
    pf_free(&t->inbound);
    free(t->local_client_id);
    free(t);
}

static ca_transport_kind_t mqtt_kind(void *self) {
    (void)self;
    return CA_TRANSPORT_MQTT;
}
static bool mqtt_available(void *self) {
    ca_mqtt_transport_t *t = (ca_mqtt_transport_t *)self;
    return t->adapter.is_connected
               ? t->adapter.is_connected(t->adapter.self)
               : false;
}
static int mqtt_start(void *self) {
    /* ConnectAsync(options); SubscribeAsync("circle/payloads/{id}/#") */
    ca_mqtt_transport_t *t = (ca_mqtt_transport_t *)self;
    if (!t->adapter.connect) return -1;
    int rc = t->adapter.connect(t->adapter.self, &t->writer);
    if (rc != 0) return rc;
    if (!t->adapter.subscribe) return -1;
    size_t need = strlen("circle/payloads/") + strlen(t->local_client_id) +
                  strlen("/#") + 1;
    char *topic = (char *)malloc(need);
    if (!topic) return -1;
    snprintf(topic, need, "circle/payloads/%s/#", t->local_client_id);
    rc = t->adapter.subscribe(t->adapter.self, topic);
    free(topic);
    return rc;
}
static int mqtt_stop(void *self) {
    /* DisconnectAsync(); _inbound.Writer.TryComplete(); */
    ca_mqtt_transport_t *t = (ca_mqtt_transport_t *)self;
    int rc = t->adapter.disconnect ? t->adapter.disconnect(t->adapter.self) : 0;
    t->inbound_open = false;
    return rc;
}
static int mqtt_send(void *self, const ca_network_payload_t *payload) {
    ca_mqtt_transport_t *t = (ca_mqtt_transport_t *)self;
    if (!payload) return -1;
    if (!t->adapter.publish) return -1;

    /* topic = DestinationId is {Length>0} ? circle/payloads/{d}
     *                                     : circle/payloads/broadcast */
    char *topic;
    if (payload->destination_id && payload->destination_id[0] != '\0') {
        size_t need = strlen("circle/payloads/") +
                      strlen(payload->destination_id) + 1;
        topic = (char *)malloc(need);
        if (!topic) return -1;
        snprintf(topic, need, "circle/payloads/%s", payload->destination_id);
    } else {
        topic = dup_or_empty("circle/payloads/broadcast");
        if (!topic) return -1;
    }

    /* QoS ExactlyOnce iff Priority >= High else AtLeastOnce */
    ca_mqtt_qos_t qos = (payload->priority >= CA_MSG_PRIORITY_HIGH)
                            ? CA_MQTT_QOS_EXACTLY_ONCE
                            : CA_MQTT_QOS_AT_LEAST_ONCE;

    int rc = t->adapter.publish(t->adapter.self, topic, payload->data,
                                payload->data_len, qos);
    free(topic);
    return rc;
}
static bool mqtt_receive_next(void *self, ca_network_payload_t **out) {
    ca_mqtt_transport_t *t = (ca_mqtt_transport_t *)self;
    if (!out) return false;
    ca_network_payload_t *p = pf_pop(&t->inbound);
    if (!p) return false;
    *out = p;
    return true;
}

ca_network_transport_t ca_mqtt_transport_as_transport(ca_mqtt_transport_t *t) {
    ca_network_transport_t v;
    v.self = t;
    v.kind = mqtt_kind;
    v.is_available = mqtt_available;
    v.start = mqtt_start;
    v.stop = mqtt_stop;
    v.send = mqtt_send;
    v.receive_next = mqtt_receive_next;
    return v;
}

size_t ca_mqtt_transport_pending(const ca_mqtt_transport_t *t) {
    return t ? (t->inbound.count - t->inbound.head) : 0;
}
