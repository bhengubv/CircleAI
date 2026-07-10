/*
 * telephony_telnyx.c — CircleAI.Telephony.Telnyx (C11 port).
 *
 * TelnyxCarrier over an injected ca_tel_http_t transport (no real network).
 * Bearer auth, /v2 namespace, JSON bodies, Call Control actions. Builds the exact
 * path + JSON body + Bearer header the C# adapter would; parses the JSON response.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/telephony_telnyx.h"
#include "telephony_carrier_internal.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <time.h>

static char *tnx_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static bool tnx_blank(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p) if (*p != ' ' && *p != '\t' && *p != '\n' && *p != '\r') return false;
    return true;
}
static bool is_2xx(int s) { return s >= 200 && s < 300; }

/* JSON-escape a string value for embedding in a JSON body. */
static char *json_esc(const char *s) {
    if (!s) return tnx_strdup("");
    size_t cap = strlen(s) * 2 + 1;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    size_t j = 0;
    for (const char *p = s; *p; ++p) {
        unsigned char c = (unsigned char)*p;
        if (c == '"' || c == '\\') { out[j++] = '\\'; out[j++] = (char)c; }
        else if (c == '\n') { out[j++] = '\\'; out[j++] = 'n'; }
        else if (c == '\r') { out[j++] = '\\'; out[j++] = 'r'; }
        else if (c == '\t') { out[j++] = '\\'; out[j++] = 't'; }
        else out[j++] = (char)c;
    }
    out[j] = '\0';
    return out;
}

typedef struct {
    ca_tel_http_t http;
    char         *base_address;   /* owned */
    char         *api_key;        /* owned or NULL */
    char         *connection_id;  /* owned or NULL */
    char         *auth_header;    /* owned "Bearer <key>" or NULL */
} telnyx_t;

static bool telnyx_is_configured_impl(const telnyx_t *t) {
    return !tnx_blank(t->api_key);
}

static int telnyx_request(telnyx_t *t, const char *method, const char *path,
                          const char *body, int *status, char **out_body) {
    *status = 0; *out_body = NULL;
    const char *ct = body ? "application/json" : NULL;
    return t->http.request(t->http.self, method, path, t->auth_header, ct, body,
                           status, out_body);
}

static const char *telnyx_carrier_id(void *self) { (void)self; return "telnyx"; }
static bool telnyx_is_configured(void *self) { return telnyx_is_configured_impl((telnyx_t *)self); }

static int telnyx_provision(void *self, const char *cc, const char *ac,
                            ca_tel_provisioned_number_t *out) {
    telnyx_t *t = (telnyx_t *)self;
    if (!telnyx_is_configured_impl(t)) return -1;
    if (!cc) return -1;

    char path[512];
    if (!tnx_blank(ac)) {
        /* national_destination_code is NOT escaped in the C# search path... it
         * uses Uri.EscapeDataString(areaCode) — mirror that. */
        char *eac = NULL;
        {
            /* inline percent-encode (unreserved kept) */
            static const char hex[] = "0123456789ABCDEF";
            size_t cap = strlen(ac) * 3 + 1;
            eac = (char *)malloc(cap);
            if (!eac) return -1;
            size_t j = 0;
            for (const char *p = ac; *p; ++p) {
                unsigned char c = (unsigned char)*p;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
                    eac[j++] = (char)c;
                else { eac[j++] = '%'; eac[j++] = hex[c >> 4]; eac[j++] = hex[c & 0xF]; }
            }
            eac[j] = '\0';
        }
        snprintf(path, sizeof(path),
                 "/v2/available_phone_numbers?filter[country_code]=%s&filter[limit]=1&filter[national_destination_code]=%s",
                 cc, eac);
        free(eac);
    } else {
        snprintf(path, sizeof(path),
                 "/v2/available_phone_numbers?filter[country_code]=%s&filter[limit]=1", cc);
    }

    int status; char *body = NULL;
    if (telnyx_request(t, "GET", path, NULL, &status, &body) != 0) return -1;
    if (!is_2xx(status)) { free(body); return -1; }
    catj_doc_t *doc = catj_parse(body ? body : "");
    free(body);
    if (!doc) return -1;
    const catj_node_t *data = catj_get(catj_root(doc), "data");
    const catj_node_t *first = catj_at(data, 0);
    if (!first) { catj_free(doc); return -1; }
    const char *pn = catj_string(catj_get(first, "phone_number"));
    if (!pn) { catj_free(doc); return -1; }
    char *pn_owned = tnx_strdup(pn);
    ca_tel_decimal_t cost = 0;
    const catj_node_t *ci = catj_get(first, "cost_information");
    if (ci) ca_tel_carrier_parse_decimal(catj_get(ci, "monthly_cost"), &cost);
    catj_free(doc);
    if (!pn_owned) return -1;

    /* POST /v2/number_orders {"phone_numbers":[{"phone_number":"pn"}]} */
    char *epn = json_esc(pn_owned);
    if (!epn) { free(pn_owned); return -1; }
    size_t need = strlen("{\"phone_numbers\":[{\"phone_number\":\"\"}]}") + strlen(epn) + 1;
    char *obody = (char *)malloc(need);
    if (!obody) { free(epn); free(pn_owned); return -1; }
    snprintf(obody, need, "{\"phone_numbers\":[{\"phone_number\":\"%s\"}]}", epn);
    free(epn);
    int ostatus; char *orbody = NULL;
    int rc = telnyx_request(t, "POST", "/v2/number_orders", obody, &ostatus, &orbody);
    free(obody); free(orbody);
    if (rc != 0 || !is_2xx(ostatus)) { free(pn_owned); return -1; }

    memset(out, 0, sizeof(*out));
    out->phone_number = pn_owned;
    out->carrier_id = tnx_strdup("telnyx");
    out->provisioned_at_utc_ms = (int64_t)time(NULL) * 1000;
    out->monthly_recurring_cost = cost;
    if (!out->carrier_id) { ca_tel_provisioned_number_free(out); return -1; }
    return 0;
}

static int telnyx_configure(void *self, const char *phone_number, const char *webhook) {
    telnyx_t *t = (telnyx_t *)self;
    if (!telnyx_is_configured_impl(t)) return -1;
    if (tnx_blank(t->connection_id)) return -1;   /* requires CallControlConnectionId */
    if (!phone_number || !webhook) return -1;

    /* PATCH /v2/call_control_applications/{id} {"webhook_event_url":"webhook"} */
    char path[320];
    snprintf(path, sizeof(path), "/v2/call_control_applications/%s", t->connection_id);
    char *ewh = json_esc(webhook);
    if (!ewh) return -1;
    size_t need = strlen("{\"webhook_event_url\":\"\"}") + strlen(ewh) + 1;
    char *b = (char *)malloc(need);
    if (!b) { free(ewh); return -1; }
    snprintf(b, need, "{\"webhook_event_url\":\"%s\"}", ewh);
    free(ewh);
    int status; char *body = NULL;
    int rc = telnyx_request(t, "PATCH", path, b, &status, &body);
    free(b); free(body);
    if (rc != 0 || !is_2xx(status)) return -1;   /* EnsureSuccessStatusCode */

    /* PATCH /v2/phone_numbers/{escaped number} {"connection_id":"id"} — non-2xx
     * only warns (may already be assigned), so we tolerate it. */
    char *epn = NULL;
    {
        static const char hex[] = "0123456789ABCDEF";
        size_t cap = strlen(phone_number) * 3 + 1;
        epn = (char *)malloc(cap);
        if (!epn) return -1;
        size_t j = 0;
        for (const char *p = phone_number; *p; ++p) {
            unsigned char c = (unsigned char)*p;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
                epn[j++] = (char)c;
            else { epn[j++] = '%'; epn[j++] = hex[c >> 4]; epn[j++] = hex[c & 0xF]; }
        }
        epn[j] = '\0';
    }
    char apath[320];
    snprintf(apath, sizeof(apath), "/v2/phone_numbers/%s", epn);
    free(epn);
    char *eid = json_esc(t->connection_id);
    if (!eid) return -1;
    size_t an = strlen("{\"connection_id\":\"\"}") + strlen(eid) + 1;
    char *abody = (char *)malloc(an);
    if (!abody) { free(eid); return -1; }
    snprintf(abody, an, "{\"connection_id\":\"%s\"}", eid);
    free(eid);
    int astatus; char *arbody = NULL;
    telnyx_request(t, "PATCH", apath, abody, &astatus, &arbody);
    free(abody); free(arbody);
    return 0;   /* assign failure only warns */
}

static ca_tel_call_session_t *telnyx_dial(void *self, ca_tel_carrier_t *carrier,
                                          const char *from, const char *to,
                                          const char *stream_url,
                                          const ca_tel_dial_options_t *options) {
    telnyx_t *t = (telnyx_t *)self;
    if (!telnyx_is_configured_impl(t)) return NULL;
    if (tnx_blank(t->connection_id)) return NULL;   /* requires connection id */
    if (!from || !to || !stream_url) return NULL;

    int ring = options ? options->ring_timeout_seconds : 30;
    const char *cid = (options && options->caller_id_override) ? options->caller_id_override : from;
    bool amd = options ? options->detect_answering_machine : false;

    char *econn = json_esc(t->connection_id);
    char *eto   = json_esc(to);
    char *efrom = json_esc(cid);
    char *eurl  = json_esc(stream_url);
    if (!econn || !eto || !efrom || !eurl) { free(econn); free(eto); free(efrom); free(eurl); return NULL; }
    /* {"connection_id":"..","to":"..","from":"..","stream_url":"..",
     *  "stream_track":"both_tracks","timeout_secs":N[,"answering_machine_detection":"detect"]} */
    size_t need = 256 + strlen(econn) + strlen(eto) + strlen(efrom) + strlen(eurl);
    char *body = (char *)malloc(need);
    if (!body) { free(econn); free(eto); free(efrom); free(eurl); return NULL; }
    int w = snprintf(body, need,
        "{\"connection_id\":\"%s\",\"to\":\"%s\",\"from\":\"%s\",\"stream_url\":\"%s\","
        "\"stream_track\":\"both_tracks\",\"timeout_secs\":%d",
        econn, eto, efrom, eurl, ring);
    free(econn); free(eto); free(efrom); free(eurl);
    if (amd) w += snprintf(body + w, need - (size_t)w, ",\"answering_machine_detection\":\"detect\"");
    snprintf(body + w, need - (size_t)w, "}");

    int status; char *rbody = NULL;
    int rc = telnyx_request(t, "POST", "/v2/calls", body, &status, &rbody);
    free(body);
    if (rc != 0 || !is_2xx(status)) { free(rbody); return NULL; }
    catj_doc_t *doc = catj_parse(rbody ? rbody : "");
    free(rbody);
    if (!doc) return NULL;
    const catj_node_t *data = catj_get(catj_root(doc), "data");
    const char *ccid = catj_string(catj_get(data, "call_control_id"));
    if (!ccid) { catj_free(doc); return NULL; }
    ca_tel_call_info_t *info = ca_tel_call_info_new(ccid, CA_TEL_DIR_OUTBOUND, from, to,
                                                    "telnyx", CA_TEL_FMT_PCM16000,
                                                    (int64_t)time(NULL) * 1000);
    catj_free(doc);
    if (!info) return NULL;
    ca_tel_call_session_t *s = ca_tel_carrier_make_pending_session(info, carrier);
    ca_tel_call_info_destroy(info);
    return s;
}

static ca_tel_provisioned_number_t *telnyx_list(void *self, size_t *count) {
    telnyx_t *t = (telnyx_t *)self;
    if (count) *count = 0;
    if (!telnyx_is_configured_impl(t)) return NULL;
    int status; char *body = NULL;
    if (telnyx_request(t, "GET", "/v2/phone_numbers?page[size]=100", NULL, &status, &body) != 0) return NULL;
    if (!is_2xx(status)) { free(body); return NULL; }
    catj_doc_t *doc = catj_parse(body ? body : "");
    free(body);
    if (!doc) return NULL;
    const catj_node_t *arr = catj_get(catj_root(doc), "data");
    size_t n = catj_array_len(arr);
    if (n == 0) { catj_free(doc); return NULL; }
    ca_tel_provisioned_number_t *list = (ca_tel_provisioned_number_t *)calloc(n, sizeof(*list));
    if (!list) { catj_free(doc); return NULL; }
    size_t out_n = 0;
    for (size_t i = 0; i < n; ++i) {
        const char *pn = catj_string(catj_get(catj_at(arr, i), "phone_number"));
        if (!pn) continue;
        list[out_n].phone_number = tnx_strdup(pn);
        list[out_n].carrier_id = tnx_strdup("telnyx");
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

static int telnyx_end_call(void *self, const char *call_id) {
    telnyx_t *t = (telnyx_t *)self;
    if (!telnyx_is_configured_impl(t)) return 0;
    if (!call_id) return 0;
    char path[320];
    snprintf(path, sizeof(path), "/v2/calls/%s/actions/hangup", call_id);
    int status; char *body = NULL;
    telnyx_request(t, "POST", path, "{}", &status, &body);
    free(body);
    return 0;
}

static int telnyx_transfer_call(void *self, const char *call_id, const char *target) {
    telnyx_t *t = (telnyx_t *)self;
    if (!telnyx_is_configured_impl(t)) return -1;   /* EnsureConfigured */
    if (!call_id || !target) return -1;
    char path[320];
    snprintf(path, sizeof(path), "/v2/calls/%s/actions/transfer", call_id);
    char *etgt = json_esc(target);
    if (!etgt) return -1;
    size_t need = strlen("{\"to\":\"\"}") + strlen(etgt) + 1;
    char *body = (char *)malloc(need);
    if (!body) { free(etgt); return -1; }
    snprintf(body, need, "{\"to\":\"%s\"}", etgt);
    free(etgt);
    int status; char *rbody = NULL;
    telnyx_request(t, "POST", path, body, &status, &rbody);
    free(body); free(rbody);
    return 0;   /* non-2xx only warns; session still latches Transferred */
}

static void telnyx_destroy(void *self) {
    telnyx_t *t = (telnyx_t *)self;
    if (!t) return;
    free(t->base_address);
    free(t->api_key);
    free(t->connection_id);
    free(t->auth_header);
    free(t);
}

static const ca_tel_carrier_vtable_t TELNYX_VTABLE = {
    telnyx_carrier_id, telnyx_is_configured, telnyx_provision, telnyx_configure,
    telnyx_dial, telnyx_list, telnyx_end_call, telnyx_transfer_call, telnyx_destroy
};

ca_tel_carrier_t *ca_tel_telnyx_create(ca_tel_http_t http,
                                       const ca_tel_telnyx_options_t *options) {
    telnyx_t *t = (telnyx_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->http = http;
    const char *base = (options && options->base_address) ? options->base_address
                                                          : "https://api.telnyx.com";
    t->base_address = tnx_strdup(base);
    if (options && options->api_key) t->api_key = tnx_strdup(options->api_key);
    if (options && options->call_control_connection_id)
        t->connection_id = tnx_strdup(options->call_control_connection_id);
    if (!t->base_address) { telnyx_destroy(t); return NULL; }

    if (telnyx_is_configured_impl(t)) {
        size_t hn = strlen("Bearer ") + strlen(t->api_key) + 1;
        t->auth_header = (char *)malloc(hn);
        if (t->auth_header) snprintf(t->auth_header, hn, "Bearer %s", t->api_key);
        if (!t->auth_header) { telnyx_destroy(t); return NULL; }
    }

    ca_tel_carrier_t *c = ca_tel_carrier_wrap(t, &TELNYX_VTABLE);
    if (!c) { telnyx_destroy(t); return NULL; }
    return c;
}
