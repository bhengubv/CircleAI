/*
 * net_sync_delta.c — CircleAI.Networking.SyncDelta + SchedulingHint (C11 port).
 */

#include "circle_ai/net_sync_delta.h"

#include <stdlib.h>
#include <string.h>

/* ---- small helpers (mirror networking.c) ---- */

static char *dup_or_null(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static char *dup_or_empty(const char *s) {
    return dup_or_null(s ? s : "");
}

static uint8_t *dup_bytes(const uint8_t *src, size_t len) {
    uint8_t *p = (uint8_t *)malloc(len ? len : 1);
    if (!p) return NULL;
    if (len && src) memcpy(p, src, len);
    return p;
}

/* Deep-copy a string array. count==0 -> NULL, *ok=true. */
static char **dup_str_array(char **src, size_t count, bool *ok) {
    *ok = true;
    if (count == 0) return NULL;
    char **out = (char **)calloc(count, sizeof(*out));
    if (!out) { *ok = false; return NULL; }
    for (size_t i = 0; i < count; ++i) {
        out[i] = dup_or_empty(src ? src[i] : NULL);
        if (!out[i]) {
            for (size_t j = 0; j < i; ++j) free(out[j]);
            free(out);
            *ok = false;
            return NULL;
        }
    }
    return out;
}

static ca_net_scheduling_hint_t *hint_copy(const ca_net_scheduling_hint_t *h) {
    if (!h) return NULL;
    ca_net_scheduling_hint_t *c =
        (ca_net_scheduling_hint_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    bool ok = true;
    c->preferred_peer_ids =
        dup_str_array(h->preferred_peer_ids, h->preferred_count, &ok);
    if (!ok) { free(c); return NULL; }
    c->preferred_count = h->preferred_count;
    c->has_window = h->has_window;
    c->suggested_window_unix_ms = h->suggested_window_unix_ms;
    c->confidence_score = h->confidence_score;
    return c;
}

static void hint_free(ca_net_scheduling_hint_t *h) {
    if (!h) return;
    for (size_t i = 0; i < h->preferred_count; ++i) free(h->preferred_peer_ids[i]);
    free(h->preferred_peer_ids);
    free(h);
}

ca_net_sync_delta_t *ca_net_sync_delta_new(
    const char *owner_id, const char *source_device_id,
    const char *target_device_id, const char *domain_key,
    const uint8_t *payload, size_t payload_len, int64_t sequence,
    ca_net_delivery_mode_t delivery_mode, bool has_ttl, int64_t ttl_ms,
    int64_t created_at_unix_ms,
    const ca_net_scheduling_hint_t *scheduling_hint) {
    ca_net_sync_delta_t *d = (ca_net_sync_delta_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;

    d->owner_id = dup_or_empty(owner_id);
    d->source_device_id = dup_or_empty(source_device_id);
    d->target_device_id = dup_or_empty(target_device_id);
    d->domain_key = dup_or_empty(domain_key);
    if (!d->owner_id || !d->source_device_id || !d->target_device_id ||
        !d->domain_key)
        goto fail;

    d->payload = dup_bytes(payload, payload_len);
    if (!d->payload) goto fail;
    d->payload_len = payload_len;

    d->sequence = sequence;
    d->delivery_mode = delivery_mode;
    d->has_ttl = has_ttl;
    d->ttl_ms = ttl_ms;
    d->created_at_unix_ms = created_at_unix_ms;

    if (scheduling_hint) {
        d->scheduling_hint = hint_copy(scheduling_hint);
        if (!d->scheduling_hint) goto fail;
    }
    return d;

fail:
    ca_net_sync_delta_destroy(d);
    return NULL;
}

void ca_net_sync_delta_destroy(ca_net_sync_delta_t *d) {
    if (!d) return;
    free(d->owner_id);
    free(d->source_device_id);
    free(d->target_device_id);
    free(d->domain_key);
    free(d->payload);
    hint_free(d->scheduling_hint);
    free(d);
}

ca_net_sync_delta_t *ca_net_sync_delta_copy(const ca_net_sync_delta_t *d) {
    if (!d) return NULL;
    return ca_net_sync_delta_new(
        d->owner_id, d->source_device_id, d->target_device_id, d->domain_key,
        d->payload, d->payload_len, d->sequence, d->delivery_mode, d->has_ttl,
        d->ttl_ms, d->created_at_unix_ms, d->scheduling_hint);
}
