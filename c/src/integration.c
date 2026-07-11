/*
 * integration.c — CircleAI.Integration (C11 port of Contracts.cs).
 *
 * Record deep-copy / free primitives for the six integration records, shared by
 * the in-memory calendar / email / news / geo / home sub-modules. The interface
 * vtables themselves are plain function-pointer structs (declared in the header);
 * their instances are minted by the sub-modules. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/integration.h"
#include "board_common.h"

/* ── nullable-string helper ─────────────────────────────────────────────── */

/* Copy a C# nullable string into (has,*dst). false only on OOM. */
static bool opt_copy(bool *has_dst, char **dst, bool has_src, const char *src) {
    if (has_src) {
        *dst = cab_strdup_empty(src);
        if (!*dst) { *has_dst = false; return false; }
        *has_dst = true;
    } else {
        *has_dst = false;
        *dst = NULL;
    }
    return true;
}

/* ── CalendarEvent ──────────────────────────────────────────────────────── */

void ca_int_calendar_event_free(ca_int_calendar_event_t *e) {
    if (!e) return;
    free(e->event_id);
    free(e->calendar_id);
    free(e->title);
    free(e->description);
    free(e->location);
    cab_strv_free(e->attendees, e->attendees_count);
    e->event_id = e->calendar_id = e->title = e->description = e->location = NULL;
    e->attendees = NULL;
    e->attendees_count = 0;
    e->has_description = e->has_location = e->is_all_day = false;
}
void ca_int_calendar_event_free_array(ca_int_calendar_event_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_int_calendar_event_free(&arr[i]);
    free(arr);
}

bool ca_int_calendar_event_copy(ca_int_calendar_event_t *dst,
                                const ca_int_calendar_event_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->event_id     = cab_strdup_empty(src->event_id);
    dst->calendar_id  = cab_strdup_empty(src->calendar_id);
    dst->title        = cab_strdup_empty(src->title);
    dst->start_utc_ms = src->start_utc_ms;
    dst->end_utc_ms   = src->end_utc_ms;
    dst->is_all_day   = src->is_all_day;
    bool ok = dst->event_id && dst->calendar_id && dst->title;
    ok = ok && opt_copy(&dst->has_description, &dst->description, src->has_description, src->description);
    ok = ok && opt_copy(&dst->has_location,    &dst->location,    src->has_location,    src->location);
    ok = ok && cab_strv_copy(&dst->attendees, src->attendees, src->attendees_count);
    if (ok) dst->attendees_count = src->attendees_count;
    if (!ok) { ca_int_calendar_event_free(dst); return false; }
    return true;
}

/* ── EmailMessage ───────────────────────────────────────────────────────── */

void ca_int_email_message_free(ca_int_email_message_t *m) {
    if (!m) return;
    free(m->message_id);
    free(m->from);
    cab_strv_free(m->to, m->to_count);
    free(m->subject);
    free(m->body_text);
    cab_strv_free(m->labels, m->labels_count);
    m->message_id = m->from = m->subject = m->body_text = NULL;
    m->to = m->labels = NULL;
    m->to_count = m->labels_count = 0;
    m->unread = false;
}
void ca_int_email_message_free_array(ca_int_email_message_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_int_email_message_free(&arr[i]);
    free(arr);
}

bool ca_int_email_message_copy(ca_int_email_message_t *dst,
                               const ca_int_email_message_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->message_id      = cab_strdup_empty(src->message_id);
    dst->from            = cab_strdup_empty(src->from);
    dst->subject         = cab_strdup_empty(src->subject);
    dst->body_text       = cab_strdup_empty(src->body_text);
    dst->received_utc_ms = src->received_utc_ms;
    dst->unread          = src->unread;
    bool ok = dst->message_id && dst->from && dst->subject && dst->body_text;
    ok = ok && cab_strv_copy(&dst->to, src->to, src->to_count);
    if (ok) dst->to_count = src->to_count;
    ok = ok && cab_strv_copy(&dst->labels, src->labels, src->labels_count);
    if (ok) dst->labels_count = src->labels_count;
    if (!ok) { ca_int_email_message_free(dst); return false; }
    return true;
}

/* ── NewsItem ───────────────────────────────────────────────────────────── */

void ca_int_news_item_free(ca_int_news_item_t *n) {
    if (!n) return;
    free(n->item_id);
    free(n->source_id);
    free(n->title);
    free(n->summary);
    free(n->url);
    cab_strv_free(n->tags, n->tags_count);
    n->item_id = n->source_id = n->title = n->summary = n->url = NULL;
    n->tags = NULL;
    n->tags_count = 0;
}
void ca_int_news_item_free_array(ca_int_news_item_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_int_news_item_free(&arr[i]);
    free(arr);
}

bool ca_int_news_item_copy(ca_int_news_item_t *dst, const ca_int_news_item_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->item_id          = cab_strdup_empty(src->item_id);
    dst->source_id        = cab_strdup_empty(src->source_id);
    dst->title            = cab_strdup_empty(src->title);
    dst->summary          = cab_strdup_empty(src->summary);
    dst->url              = cab_strdup_empty(src->url);
    dst->published_utc_ms = src->published_utc_ms;
    bool ok = dst->item_id && dst->source_id && dst->title && dst->summary && dst->url;
    ok = ok && cab_strv_copy(&dst->tags, src->tags, src->tags_count);
    if (ok) dst->tags_count = src->tags_count;
    if (!ok) { ca_int_news_item_free(dst); return false; }
    return true;
}

/* ── WeatherSample ──────────────────────────────────────────────────────── */

void ca_int_weather_sample_free(ca_int_weather_sample_t *w) {
    if (!w) return;
    free(w->condition);
    w->condition = NULL;
}
void ca_int_weather_sample_free_array(ca_int_weather_sample_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_int_weather_sample_free(&arr[i]);
    free(arr);
}

bool ca_int_weather_sample_copy(ca_int_weather_sample_t *dst,
                                const ca_int_weather_sample_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->at_utc_ms    = src->at_utc_ms;
    dst->temp_c       = src->temp_c;
    dst->feels_like_c = src->feels_like_c;
    dst->precip_mm    = src->precip_mm;
    dst->wind_kph     = src->wind_kph;
    dst->cloud_pct    = src->cloud_pct;
    dst->condition    = cab_strdup_empty(src->condition);
    if (!dst->condition) { ca_int_weather_sample_free(dst); return false; }
    return true;
}

/* ── RouteEstimate ──────────────────────────────────────────────────────── */

void ca_int_route_estimate_free(ca_int_route_estimate_t *r) {
    if (!r) return;
    free(r->polyline);
    r->polyline = NULL;
    r->polyline_count = 0;
}

bool ca_int_route_estimate_copy(ca_int_route_estimate_t *dst,
                                const ca_int_route_estimate_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->distance_km = src->distance_km;
    dst->duration_ms = src->duration_ms;
    if (src->polyline_count > 0) {
        dst->polyline = (ca_int_route_point_t *)malloc(
            src->polyline_count * sizeof(ca_int_route_point_t));
        if (!dst->polyline) return false;
        memcpy(dst->polyline, src->polyline,
               src->polyline_count * sizeof(ca_int_route_point_t));
        dst->polyline_count = src->polyline_count;
    }
    return true;
}

/* ── HaEntity ───────────────────────────────────────────────────────────── */

static void attr_pairs_free(ca_int_attr_pair_t *p, size_t n) {
    if (!p) return;
    for (size_t i = 0; i < n; ++i) {
        free(p[i].key);
        free(p[i].value);
    }
    free(p);
}

/* Deep-copy a pair array (each key/value empty-coalesced). false on OOM. */
static bool attr_pairs_copy(ca_int_attr_pair_t **out, const ca_int_attr_pair_t *src,
                            size_t n) {
    *out = NULL;
    if (n == 0) return true;
    ca_int_attr_pair_t *v = (ca_int_attr_pair_t *)calloc(n, sizeof(*v));
    if (!v) return false;
    for (size_t i = 0; i < n; ++i) {
        v[i].key   = cab_strdup_empty(src ? src[i].key : NULL);
        v[i].value = cab_strdup_empty(src ? src[i].value : NULL);
        if (!v[i].key || !v[i].value) { attr_pairs_free(v, i + 1); return false; }
    }
    *out = v;
    return true;
}

void ca_int_ha_entity_free(ca_int_ha_entity_t *e) {
    if (!e) return;
    free(e->entity_id);
    free(e->friendly_name);
    free(e->domain);
    free(e->state);
    attr_pairs_free(e->attributes, e->attributes_count);
    e->entity_id = e->friendly_name = e->domain = e->state = NULL;
    e->attributes = NULL;
    e->attributes_count = 0;
}
void ca_int_ha_entity_free_array(ca_int_ha_entity_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_int_ha_entity_free(&arr[i]);
    free(arr);
}

bool ca_int_ha_entity_copy(ca_int_ha_entity_t *dst, const ca_int_ha_entity_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->entity_id     = cab_strdup_empty(src->entity_id);
    dst->friendly_name = cab_strdup_empty(src->friendly_name);
    dst->domain        = cab_strdup_empty(src->domain);
    dst->state         = cab_strdup_empty(src->state);
    bool ok = dst->entity_id && dst->friendly_name && dst->domain && dst->state;
    ok = ok && attr_pairs_copy(&dst->attributes, src->attributes, src->attributes_count);
    if (ok) dst->attributes_count = src->attributes_count;
    if (!ok) { ca_int_ha_entity_free(dst); return false; }
    return true;
}

const char *ca_int_ha_entity_attr(const ca_int_ha_entity_t *e, const char *key) {
    if (!e || !key) return NULL;
    for (size_t i = 0; i < e->attributes_count; ++i)
        if (cab_ord_eq(e->attributes[i].key, key)) return e->attributes[i].value;
    return NULL;
}
