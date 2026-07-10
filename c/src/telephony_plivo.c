/*
 * telephony_plivo.c — CircleAI.Telephony.Plivo (C11 port).
 *
 * PlivoCarrier over an injected ca_tel_http_t transport (no real network). Basic
 * auth, /v1/Account/{AuthId}/ namespace, form-encoded bodies, AnswerUrl-driven
 * streaming. Builds the exact path + form body + Basic header the C# adapter
 * would; parses the JSON response.
 *
 * Pure C11 + libc. Reuses ca_base64_encode (compression.h) and
 * ca_http_escape_data_string (net_http.h).
 */

#include "circle_ai/telephony_plivo.h"
#include "circle_ai/compression.h"   /* ca_base64_encode */
#include "circle_ai/net_http.h"      /* ca_http_escape_data_string */
#include "telephony_carrier_internal.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <time.h>

static char *plv_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static bool plv_blank(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p) if (*p != ' ' && *p != '\t' && *p != '\n' && *p != '\r') return false;
    return true;
}
static bool is_2xx(int s) { return s >= 200 && s < 300; }

/* application/x-www-form-urlencoded value encoding. */
static char *form_encode(const char *s) {
    if (!s) return plv_strdup("");
    static const char hex[] = "0123456789ABCDEF";
    size_t cap = strlen(s) * 3 + 1;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    size_t j = 0;
    for (const char *p = s; *p; ++p) {
        unsigned char c = (unsigned char)*p;
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
            (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
            out[j++] = (char)c;
        else if (c == ' ') out[j++] = '+';
        else { out[j++] = '%'; out[j++] = hex[c >> 4]; out[j++] = hex[c & 0xF]; }
    }
    out[j] = '\0';
    return out;
}
static bool form_append(char **buf, size_t *len, size_t *cap,
                        const char *key, const char *value) {
    char *ev = form_encode(value);
    if (!ev) return false;
    size_t need = strlen(key) + 1 + strlen(ev) + 1 + 1;
    if (*len + need > *cap) {
        size_t nc = (*cap ? *cap : 64);
        while (*len + need > nc) nc *= 2;
        char *nb = realloc(*buf, nc);
        if (!nb) { free(ev); return false; }
        *buf = nb; *cap = nc;
    }
    if (*len > 0) (*buf)[(*len)++] = '&';
    int w = snprintf(*buf + *len, *cap - *len, "%s=%s", key, ev);
    *len += (size_t)w;
    free(ev);
    return true;
}

typedef struct {
    ca_tel_http_t http;
    char         *base_address;    /* owned */
    char         *auth_id;         /* owned or NULL */
    char         *auth_token;      /* owned or NULL */
    char         *answer_url_base; /* owned or NULL */
    char         *auth_header;     /* owned "Basic <b64>" or NULL */
} plivo_t;

static bool plivo_is_configured_impl(const plivo_t *p) {
    return !plv_blank(p->auth_id) && !plv_blank(p->auth_token);
}

static int plivo_request(plivo_t *p, const char *method, const char *path,
                         const char *content_type, const char *body,
                         int *status, char **out_body) {
    *status = 0; *out_body = NULL;
    return p->http.request(p->http.self, method, path, p->auth_header,
                           content_type, body, status, out_body);
}

static const char *plivo_carrier_id(void *self) { (void)self; return "plivo"; }
static bool plivo_is_configured(void *self) { return plivo_is_configured_impl((plivo_t *)self); }

static int plivo_provision(void *self, const char *cc, const char *ac,
                           ca_tel_provisioned_number_t *out) {
    plivo_t *p = (plivo_t *)self;
    if (!plivo_is_configured_impl(p)) return -1;
    if (!cc) return -1;

    /* GET /v1/Account/{id}/PhoneNumber/?country_iso={cc}&limit=1[&pattern=area] */
    char path[512];
    if (!plv_blank(ac)) {
        char *eac = ca_http_escape_data_string(ac);
        if (!eac) return -1;
        snprintf(path, sizeof(path),
                 "/v1/Account/%s/PhoneNumber/?country_iso=%s&limit=1&pattern=%s",
                 p->auth_id, cc, eac);
        free(eac);
    } else {
        snprintf(path, sizeof(path),
                 "/v1/Account/%s/PhoneNumber/?country_iso=%s&limit=1", p->auth_id, cc);
    }

    int status; char *body = NULL;
    if (plivo_request(p, "GET", path, NULL, NULL, &status, &body) != 0) return -1;
    if (!is_2xx(status)) { free(body); return -1; }
    catj_doc_t *doc = catj_parse(body ? body : "");
    free(body);
    if (!doc) return -1;
    const catj_node_t *objects = catj_get(catj_root(doc), "objects");
    const catj_node_t *first = catj_at(objects, 0);
    if (!first) { catj_free(doc); return -1; }
    const char *pn = catj_string(catj_get(first, "number"));
    if (!pn) { catj_free(doc); return -1; }
    char *pn_owned = plv_strdup(pn);
    ca_tel_decimal_t cost = 0;
    ca_tel_carrier_parse_decimal(catj_get(first, "monthly_rental_rate"), &cost);
    catj_free(doc);
    if (!pn_owned) return -1;

    /* POST /v1/Account/{id}/PhoneNumber/{number}/ (app_id="") */
    char buy_path[320];
    snprintf(buy_path, sizeof(buy_path),
             "/v1/Account/%s/PhoneNumber/%s/", p->auth_id, pn_owned);
    char *form = NULL; size_t flen = 0, fcap = 0;
    if (!form_append(&form, &flen, &fcap, "app_id", "")) { free(form); free(pn_owned); return -1; }
    int bstatus; char *bbody = NULL;
    int rc = plivo_request(p, "POST", buy_path, "application/x-www-form-urlencoded",
                           form, &bstatus, &bbody);
    free(form); free(bbody);
    if (rc != 0 || !is_2xx(bstatus)) { free(pn_owned); return -1; }

    memset(out, 0, sizeof(*out));
    out->phone_number = pn_owned;
    out->carrier_id = plv_strdup("plivo");
    out->provisioned_at_utc_ms = (int64_t)time(NULL) * 1000;
    out->monthly_recurring_cost = cost;
    if (!out->carrier_id) { ca_tel_provisioned_number_free(out); return -1; }
    return 0;
}

static int plivo_configure(void *self, const char *phone_number, const char *webhook) {
    plivo_t *p = (plivo_t *)self;
    if (!plivo_is_configured_impl(p)) return -1;
    if (!phone_number || !webhook) return -1;
    char path[320];
    snprintf(path, sizeof(path), "/v1/Account/%s/Number/%s/", p->auth_id, phone_number);
    char *form = NULL; size_t flen = 0, fcap = 0;
    bool ok = form_append(&form, &flen, &fcap, "answer_url", webhook) &&
              form_append(&form, &flen, &fcap, "answer_method", "POST");
    if (!ok) { free(form); return -1; }
    int status; char *body = NULL;
    int rc = plivo_request(p, "POST", path, "application/x-www-form-urlencoded",
                           form, &status, &body);
    free(form); free(body);
    if (rc != 0 || !is_2xx(status)) return -1;
    return 0;
}

/* Compose answer_url = AnswerUrlBase + [?|&]stream=<escaped streamUrl>.
 * Mirrors the UriBuilder logic: existing query (sans leading '?') gets the
 * separator + "stream=..." appended. Returns a fresh owned string. */
static char *compose_answer_url(const char *base, const char *stream_url) {
    char *esc = ca_http_escape_data_string(stream_url);
    if (!esc) return NULL;
    /* find existing query start */
    const char *q = strchr(base, '?');
    bool has_query = (q != NULL) && (*(q + 1) != '\0');
    const char *sep = has_query ? "&" : (q ? "" : "?");
    /* if base already ends with '?' (empty query), C# yields existingQuery="" ->
     * separator "" and Query set to "stream=..." (UriBuilder re-adds the '?'). We
     * approximate: base without trailing '?' then "?stream=...". */
    size_t base_len = strlen(base);
    bool trailing_q = base_len > 0 && base[base_len - 1] == '?';
    char *out;
    if (trailing_q) {
        size_t need = base_len + strlen("stream=") + strlen(esc) + 1;
        out = (char *)malloc(need);
        if (out) snprintf(out, need, "%sstream=%s", base, esc);
    } else {
        size_t need = base_len + strlen(sep) + strlen("stream=") + strlen(esc) + 1;
        out = (char *)malloc(need);
        if (out) snprintf(out, need, "%s%sstream=%s", base, sep, esc);
    }
    free(esc);
    return out;
}

static ca_tel_call_session_t *plivo_dial(void *self, ca_tel_carrier_t *carrier,
                                         const char *from, const char *to,
                                         const char *stream_url,
                                         const ca_tel_dial_options_t *options) {
    plivo_t *p = (plivo_t *)self;
    if (!plivo_is_configured_impl(p)) return NULL;
    if (plv_blank(p->answer_url_base)) return NULL;   /* requires AnswerUrlBase */
    if (!from || !to || !stream_url) return NULL;

    int ring = options ? options->ring_timeout_seconds : 30;
    const char *cid = (options && options->caller_id_override) ? options->caller_id_override : from;
    bool amd = options ? options->detect_answering_machine : false;

    char *answer_url = compose_answer_url(p->answer_url_base, stream_url);
    if (!answer_url) return NULL;

    char ringbuf[16];
    snprintf(ringbuf, sizeof(ringbuf), "%d", ring);
    char *form = NULL; size_t flen = 0, fcap = 0;
    bool ok = form_append(&form, &flen, &fcap, "from", cid) &&
              form_append(&form, &flen, &fcap, "to", to) &&
              form_append(&form, &flen, &fcap, "answer_url", answer_url) &&
              form_append(&form, &flen, &fcap, "answer_method", "POST") &&
              form_append(&form, &flen, &fcap, "ring_timeout", ringbuf);
    if (ok && amd) ok = form_append(&form, &flen, &fcap, "machine_detection", "true");
    free(answer_url);
    if (!ok) { free(form); return NULL; }

    char path[256];
    snprintf(path, sizeof(path), "/v1/Account/%s/Call/", p->auth_id);
    int status; char *body = NULL;
    int rc = plivo_request(p, "POST", path, "application/x-www-form-urlencoded",
                           form, &status, &body);
    free(form);
    if (rc != 0 || !is_2xx(status)) { free(body); return NULL; }
    catj_doc_t *doc = catj_parse(body ? body : "");
    free(body);
    if (!doc) return NULL;
    const char *uuid = catj_string(catj_get(catj_root(doc), "request_uuid"));
    if (!uuid) { catj_free(doc); return NULL; }
    ca_tel_call_info_t *info = ca_tel_call_info_new(uuid, CA_TEL_DIR_OUTBOUND, from, to,
                                                    "plivo", CA_TEL_FMT_MULAW8000,
                                                    (int64_t)time(NULL) * 1000);
    catj_free(doc);
    if (!info) return NULL;
    ca_tel_call_session_t *s = ca_tel_carrier_make_pending_session(info, carrier);
    ca_tel_call_info_destroy(info);
    return s;
}

static ca_tel_provisioned_number_t *plivo_list(void *self, size_t *count) {
    plivo_t *p = (plivo_t *)self;
    if (count) *count = 0;
    if (!plivo_is_configured_impl(p)) return NULL;
    char path[256];
    snprintf(path, sizeof(path), "/v1/Account/%s/Number/?limit=100", p->auth_id);
    int status; char *body = NULL;
    if (plivo_request(p, "GET", path, NULL, NULL, &status, &body) != 0) return NULL;
    if (!is_2xx(status)) { free(body); return NULL; }
    catj_doc_t *doc = catj_parse(body ? body : "");
    free(body);
    if (!doc) return NULL;
    const catj_node_t *arr = catj_get(catj_root(doc), "objects");
    size_t n = catj_array_len(arr);
    if (n == 0) { catj_free(doc); return NULL; }
    ca_tel_provisioned_number_t *list = (ca_tel_provisioned_number_t *)calloc(n, sizeof(*list));
    if (!list) { catj_free(doc); return NULL; }
    size_t out_n = 0;
    for (size_t i = 0; i < n; ++i) {
        const char *pn = catj_string(catj_get(catj_at(arr, i), "number"));
        if (!pn) continue;
        list[out_n].phone_number = plv_strdup(pn);
        list[out_n].carrier_id = plv_strdup("plivo");
        list[out_n].provisioned_at_utc_ms = (int64_t)time(NULL) * 1000;
        list[out_n].monthly_recurring_cost = 0;
        if (!list[out_n].phone_number || !list[out_n].carrier_id) {
            ca_tel_provisioned_number_free_array(list, out_n + 1);
            catj_free(doc);
            return NULL;
        }
        out_n++;
    }
    catj_free(doc);
    if (count) *count = out_n;
    if (out_n == 0) { free(list); return NULL; }
    return list;
}

static int plivo_end_call(void *self, const char *call_id) {
    plivo_t *p = (plivo_t *)self;
    if (!plivo_is_configured_impl(p)) return 0;
    if (!call_id) return 0;
    char path[320];
    snprintf(path, sizeof(path), "/v1/Account/%s/Call/%s/", p->auth_id, call_id);
    int status; char *body = NULL;
    plivo_request(p, "DELETE", path, NULL, NULL, &status, &body);
    free(body);
    return 0;
}

static int plivo_transfer_call(void *self, const char *call_id, const char *target) {
    plivo_t *p = (plivo_t *)self;
    if (!plivo_is_configured_impl(p)) return -1;   /* EnsureConfigured */
    if (!call_id || !target) return -1;
    /* aleg_url = data:application/xml,<escaped <Response><Dial><Number>target
     * </Number></Dial></Response>> */
    size_t xn = strlen("<Response><Dial><Number></Number></Dial></Response>") +
                strlen(target) + 1;
    char *xml = (char *)malloc(xn);
    if (!xml) return -1;
    snprintf(xml, xn, "<Response><Dial><Number>%s</Number></Dial></Response>", target);
    char *exml = ca_http_escape_data_string(xml);
    free(xml);
    if (!exml) return -1;
    size_t an = strlen("data:application/xml,") + strlen(exml) + 1;
    char *aleg = (char *)malloc(an);
    if (!aleg) { free(exml); return -1; }
    snprintf(aleg, an, "data:application/xml,%s", exml);
    free(exml);

    char path[320];
    snprintf(path, sizeof(path), "/v1/Account/%s/Call/%s/", p->auth_id, call_id);
    char *form = NULL; size_t flen = 0, fcap = 0;
    bool ok = form_append(&form, &flen, &fcap, "aleg_url", aleg) &&
              form_append(&form, &flen, &fcap, "aleg_method", "POST");
    free(aleg);
    if (!ok) { free(form); return -1; }
    int status; char *body = NULL;
    plivo_request(p, "POST", path, "application/x-www-form-urlencoded", form, &status, &body);
    free(form); free(body);
    return 0;   /* non-2xx only warns; session still latches Transferred */
}

static void plivo_destroy(void *self) {
    plivo_t *p = (plivo_t *)self;
    if (!p) return;
    free(p->base_address);
    free(p->auth_id);
    free(p->auth_token);
    free(p->answer_url_base);
    free(p->auth_header);
    free(p);
}

static const ca_tel_carrier_vtable_t PLIVO_VTABLE = {
    plivo_carrier_id, plivo_is_configured, plivo_provision, plivo_configure,
    plivo_dial, plivo_list, plivo_end_call, plivo_transfer_call, plivo_destroy
};

ca_tel_carrier_t *ca_tel_plivo_create(ca_tel_http_t http,
                                      const ca_tel_plivo_options_t *options) {
    plivo_t *p = (plivo_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->http = http;
    const char *base = (options && options->base_address) ? options->base_address
                                                          : "https://api.plivo.com";
    p->base_address = plv_strdup(base);
    if (options && options->auth_id)    p->auth_id = plv_strdup(options->auth_id);
    if (options && options->auth_token) p->auth_token = plv_strdup(options->auth_token);
    if (options && options->answer_url_base)
        p->answer_url_base = plv_strdup(options->answer_url_base);
    if (!p->base_address) { plivo_destroy(p); return NULL; }

    if (plivo_is_configured_impl(p)) {
        size_t need = strlen(p->auth_id) + 1 + strlen(p->auth_token) + 1;
        char *creds = (char *)malloc(need);
        if (!creds) { plivo_destroy(p); return NULL; }
        snprintf(creds, need, "%s:%s", p->auth_id, p->auth_token);
        char *b64 = ca_base64_encode((const uint8_t *)creds, strlen(creds));
        free(creds);
        if (!b64) { plivo_destroy(p); return NULL; }
        size_t hn = strlen("Basic ") + strlen(b64) + 1;
        p->auth_header = (char *)malloc(hn);
        if (p->auth_header) snprintf(p->auth_header, hn, "Basic %s", b64);
        free(b64);
        if (!p->auth_header) { plivo_destroy(p); return NULL; }
    }

    ca_tel_carrier_t *c = ca_tel_carrier_wrap(p, &PLIVO_VTABLE);
    if (!c) { plivo_destroy(p); return NULL; }
    return c;
}
