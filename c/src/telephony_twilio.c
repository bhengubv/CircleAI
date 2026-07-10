/*
 * telephony_twilio.c — CircleAI.Telephony.Twilio (C11 port).
 *
 * TwilioCarrier over an injected ca_tel_http_t transport (no real network). The
 * binding builds the exact REST path + form/TwiML body + Basic auth header the
 * C# TwilioCarrier would, issues it through the transport, and parses the JSON
 * response. TwilioCallSession semantics live in the shared MediaCallSession
 * (telephony.c) driven via the carrier vtable's transfer_call/end_call.
 *
 * Pure C11 + libc. Reuses ca_base64_encode (compression.h) and
 * ca_http_escape_data_string (net_http.h).
 */

#include "circle_ai/telephony_twilio.h"
#include "circle_ai/compression.h"   /* ca_base64_encode */
#include "circle_ai/net_http.h"      /* ca_http_escape_data_string */
#include "telephony_carrier_internal.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <time.h>

/* ── small string helpers (local to the bindings) ───────────────────────── */

static char *twl_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static bool twl_blank(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p) if (*p != ' ' && *p != '\t' && *p != '\n' && *p != '\r') return false;
    return true;
}

/* WebUtility.HtmlEncode — encodes & < > " ' (the reserved set that matters for
 * embedding a URL / number inside TwiML). Returns a fresh owned string. */
static char *html_encode(const char *s) {
    if (!s) return twl_strdup("");
    size_t cap = strlen(s) * 6 + 1;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    size_t j = 0;
    for (const char *p = s; *p; ++p) {
        switch (*p) {
            case '&': memcpy(out + j, "&amp;", 5); j += 5; break;
            case '<': memcpy(out + j, "&lt;", 4);  j += 4; break;
            case '>': memcpy(out + j, "&gt;", 4);  j += 4; break;
            case '"': memcpy(out + j, "&quot;", 6); j += 6; break;
            case '\'': memcpy(out + j, "&#39;", 5); j += 5; break;
            default: out[j++] = *p; break;
        }
    }
    out[j] = '\0';
    return out;
}

/* application/x-www-form-urlencoded value encoding (FormUrlEncodedContent):
 * space -> '+', unreserved kept, everything else percent-encoded. */
static char *form_encode(const char *s) {
    if (!s) return twl_strdup("");
    static const char hex[] = "0123456789ABCDEF";
    size_t cap = strlen(s) * 3 + 1;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    size_t j = 0;
    for (const char *p = s; *p; ++p) {
        unsigned char c = (unsigned char)*p;
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
            (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~') {
            out[j++] = (char)c;
        } else if (c == ' ') {
            out[j++] = '+';
        } else {
            out[j++] = '%'; out[j++] = hex[c >> 4]; out[j++] = hex[c & 0xF];
        }
    }
    out[j] = '\0';
    return out;
}

/* append "key=value&" (value form-encoded) to a growing buffer. */
static bool form_append(char **buf, size_t *len, size_t *cap,
                        const char *key, const char *value) {
    char *ev = form_encode(value);
    if (!ev) return false;
    size_t need = strlen(key) + 1 + strlen(ev) + 1 + 1;   /* key=ev& \0 */
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

/* ── binding state ──────────────────────────────────────────────────────── */

typedef struct {
    ca_tel_http_t http;
    char         *base_address;   /* owned */
    char         *account_sid;    /* owned or NULL */
    char         *auth_token;     /* owned or NULL */
    char         *auth_header;    /* owned "Basic <b64>" or NULL */
} twilio_t;

static bool twilio_is_configured_impl(const twilio_t *t) {
    return !twl_blank(t->account_sid) && !twl_blank(t->auth_token);
}

/* issue a request; on transport success returns 0 and fills status + out_body
 * (out_body may be NULL); returns -1 on a transport exception. body may be NULL. */
static int twilio_request(twilio_t *t, const char *method, const char *path,
                          const char *content_type, const char *body,
                          int *status, char **out_body) {
    *status = 0; *out_body = NULL;
    return t->http.request(t->http.self, method, path, t->auth_header,
                           content_type, body, status, out_body);
}

static bool is_2xx(int s) { return s >= 200 && s < 300; }

/* ── ITelephonyCarrier vtable ───────────────────────────────────────────── */

static const char *twilio_carrier_id(void *self) { (void)self; return "twilio"; }
static bool twilio_is_configured(void *self) { return twilio_is_configured_impl((twilio_t *)self); }

static int twilio_provision(void *self, const char *cc, const char *ac,
                            ca_tel_provisioned_number_t *out) {
    twilio_t *t = (twilio_t *)self;
    if (!twilio_is_configured_impl(t)) return -1;
    if (!cc) return -1;

    /* GET AvailablePhoneNumbers/{cc}/Local.json[?AreaCode=&Limit=1 | ?Limit=1] */
    char path[512];
    if (!twl_blank(ac)) {
        char *eac = ca_http_escape_data_string(ac);
        if (!eac) return -1;
        snprintf(path, sizeof(path),
                 "/2010-04-01/Accounts/%s/AvailablePhoneNumbers/%s/Local.json?AreaCode=%s&Limit=1",
                 t->account_sid, cc, eac);
        free(eac);
    } else {
        snprintf(path, sizeof(path),
                 "/2010-04-01/Accounts/%s/AvailablePhoneNumbers/%s/Local.json?Limit=1",
                 t->account_sid, cc);
    }

    int status; char *body = NULL;
    if (twilio_request(t, "GET", path, NULL, NULL, &status, &body) != 0) return -1;
    if (!is_2xx(status)) { free(body); return -1; }  /* EnsureSuccessStatusCode */

    catj_doc_t *doc = catj_parse(body ? body : "");
    free(body);
    if (!doc) return -1;
    const catj_node_t *arr = catj_get(catj_root(doc), "available_phone_numbers");
    const catj_node_t *first = catj_at(arr, 0);
    if (!first) { catj_free(doc); return -1; }        /* no availability */
    const char *pn = catj_string(catj_get(first, "phone_number"));
    if (!pn) { catj_free(doc); return -1; }
    char *pn_owned = twl_strdup(pn);
    ca_tel_decimal_t cost = 0;
    ca_tel_carrier_parse_decimal(catj_get(first, "price"), &cost);
    catj_free(doc);
    if (!pn_owned) return -1;

    /* POST IncomingPhoneNumbers.json (PhoneNumber=pn) */
    char reserve_path[256];
    snprintf(reserve_path, sizeof(reserve_path),
             "/2010-04-01/Accounts/%s/IncomingPhoneNumbers.json", t->account_sid);
    char *form = NULL; size_t flen = 0, fcap = 0;
    if (!form_append(&form, &flen, &fcap, "PhoneNumber", pn_owned)) { free(form); free(pn_owned); return -1; }
    int rstatus; char *rbody = NULL;
    int rc = twilio_request(t, "POST", reserve_path,
                            "application/x-www-form-urlencoded", form, &rstatus, &rbody);
    free(form); free(rbody);
    if (rc != 0 || !is_2xx(rstatus)) { free(pn_owned); return -1; }

    memset(out, 0, sizeof(*out));
    out->phone_number = pn_owned;
    out->carrier_id = twl_strdup("twilio");
    out->provisioned_at_utc_ms = (int64_t)time(NULL) * 1000;
    out->monthly_recurring_cost = cost;
    if (!out->carrier_id) { ca_tel_provisioned_number_free(out); return -1; }
    return 0;
}

static int twilio_configure(void *self, const char *phone_number, const char *webhook) {
    twilio_t *t = (twilio_t *)self;
    if (!twilio_is_configured_impl(t)) return -1;
    if (!phone_number || !webhook) return -1;

    char *epn = ca_http_escape_data_string(phone_number);
    if (!epn) return -1;
    char path[384];
    snprintf(path, sizeof(path),
             "/2010-04-01/Accounts/%s/IncomingPhoneNumbers.json?PhoneNumber=%s",
             t->account_sid, epn);
    free(epn);

    int status; char *body = NULL;
    if (twilio_request(t, "GET", path, NULL, NULL, &status, &body) != 0) return -1;
    if (!is_2xx(status)) { free(body); return -1; }
    catj_doc_t *doc = catj_parse(body ? body : "");
    free(body);
    if (!doc) return -1;
    const catj_node_t *arr = catj_get(catj_root(doc), "incoming_phone_numbers");
    const catj_node_t *entry = catj_at(arr, 0);
    if (!entry) { catj_free(doc); return -1; }        /* not owned */
    const char *sid = catj_string(catj_get(entry, "sid"));
    if (!sid) { catj_free(doc); return -1; }
    char *sid_owned = twl_strdup(sid);
    catj_free(doc);
    if (!sid_owned) return -1;

    char cfg_path[320];
    snprintf(cfg_path, sizeof(cfg_path),
             "/2010-04-01/Accounts/%s/IncomingPhoneNumbers/%s.json",
             t->account_sid, sid_owned);
    free(sid_owned);
    char *form = NULL; size_t flen = 0, fcap = 0;
    bool ok = form_append(&form, &flen, &fcap, "VoiceUrl", webhook) &&
              form_append(&form, &flen, &fcap, "VoiceMethod", "POST");
    if (!ok) { free(form); return -1; }
    int ustatus; char *ubody = NULL;
    int rc = twilio_request(t, "POST", cfg_path,
                            "application/x-www-form-urlencoded", form, &ustatus, &ubody);
    free(form); free(ubody);
    if (rc != 0 || !is_2xx(ustatus)) return -1;
    return 0;
}

static ca_tel_call_session_t *twilio_dial(void *self, ca_tel_carrier_t *carrier,
                                          const char *from, const char *to,
                                          const char *stream_url,
                                          const ca_tel_dial_options_t *options) {
    twilio_t *t = (twilio_t *)self;
    if (!twilio_is_configured_impl(t)) return NULL;
    if (!from || !to || !stream_url) return NULL;

    int ring = options ? options->ring_timeout_seconds : 30;
    const char *cid = (options && options->caller_id_override) ? options->caller_id_override : from;
    bool amd = options ? options->detect_answering_machine : false;

    /* Twiml=<Response><Connect><Stream url='<html-encoded streamUrl>'/></Connect></Response> */
    char *enc_url = html_encode(stream_url);
    if (!enc_url) return NULL;
    size_t tw_need = strlen("<Response><Connect><Stream url='") + strlen(enc_url) +
                     strlen("'/></Connect></Response>") + 1;
    char *twiml = (char *)malloc(tw_need);
    if (!twiml) { free(enc_url); return NULL; }
    snprintf(twiml, tw_need, "<Response><Connect><Stream url='%s'/></Connect></Response>", enc_url);
    free(enc_url);

    char ringbuf[16];
    snprintf(ringbuf, sizeof(ringbuf), "%d", ring);
    char *form = NULL; size_t flen = 0, fcap = 0;
    bool ok = form_append(&form, &flen, &fcap, "From", cid) &&
              form_append(&form, &flen, &fcap, "To", to) &&
              form_append(&form, &flen, &fcap, "Twiml", twiml) &&
              form_append(&form, &flen, &fcap, "Timeout", ringbuf);
    if (ok && amd) ok = form_append(&form, &flen, &fcap, "MachineDetection", "Enable");
    free(twiml);
    if (!ok) { free(form); return NULL; }

    char calls_path[256];
    snprintf(calls_path, sizeof(calls_path),
             "/2010-04-01/Accounts/%s/Calls.json", t->account_sid);
    int status; char *body = NULL;
    int rc = twilio_request(t, "POST", calls_path,
                            "application/x-www-form-urlencoded", form, &status, &body);
    free(form);
    if (rc != 0 || !is_2xx(status)) { free(body); return NULL; }

    catj_doc_t *doc = catj_parse(body ? body : "");
    free(body);
    if (!doc) return NULL;
    const char *sid = catj_string(catj_get(catj_root(doc), "sid"));
    if (!sid) { catj_free(doc); return NULL; }
    ca_tel_call_info_t *info = ca_tel_call_info_new(sid, CA_TEL_DIR_OUTBOUND, from, to,
                                                    "twilio", CA_TEL_FMT_MULAW8000,
                                                    (int64_t)time(NULL) * 1000);
    catj_free(doc);
    if (!info) return NULL;
    ca_tel_call_session_t *s = ca_tel_carrier_make_pending_session(info, carrier);
    ca_tel_call_info_destroy(info);
    return s;
}

static ca_tel_provisioned_number_t *twilio_list(void *self, size_t *count) {
    twilio_t *t = (twilio_t *)self;
    if (count) *count = 0;
    if (!twilio_is_configured_impl(t)) return NULL;   /* Array.Empty */

    char path[256];
    snprintf(path, sizeof(path),
             "/2010-04-01/Accounts/%s/IncomingPhoneNumbers.json?PageSize=100",
             t->account_sid);
    int status; char *body = NULL;
    if (twilio_request(t, "GET", path, NULL, NULL, &status, &body) != 0) return NULL;
    if (!is_2xx(status)) { free(body); return NULL; }  /* warn + empty */
    catj_doc_t *doc = catj_parse(body ? body : "");
    free(body);
    if (!doc) return NULL;
    const catj_node_t *arr = catj_get(catj_root(doc), "incoming_phone_numbers");
    size_t n = catj_array_len(arr);
    if (n == 0) { catj_free(doc); return NULL; }
    ca_tel_provisioned_number_t *list =
        (ca_tel_provisioned_number_t *)calloc(n, sizeof(*list));
    if (!list) { catj_free(doc); return NULL; }
    size_t out_n = 0;
    for (size_t i = 0; i < n; ++i) {
        const char *pn = catj_string(catj_get(catj_at(arr, i), "phone_number"));
        if (!pn) continue;   /* GetProperty!.GetString()! would throw; skip defensively */
        list[out_n].phone_number = twl_strdup(pn);
        list[out_n].carrier_id = twl_strdup("twilio");
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

/* EndCall: POST Calls/{sid}.json (Status=completed). Fail-soft. */
static int twilio_end_call(void *self, const char *call_id) {
    twilio_t *t = (twilio_t *)self;
    if (!twilio_is_configured_impl(t)) return 0;   /* if (!IsConfigured) return; */
    if (!call_id) return 0;
    char path[320];
    snprintf(path, sizeof(path), "/2010-04-01/Accounts/%s/Calls/%s.json",
             t->account_sid, call_id);
    char *form = NULL; size_t flen = 0, fcap = 0;
    if (!form_append(&form, &flen, &fcap, "Status", "completed")) { free(form); return -1; }
    int status; char *body = NULL;
    twilio_request(t, "POST", path, "application/x-www-form-urlencoded", form, &status, &body);
    free(form); free(body);
    return 0;   /* logs a warning on non-2xx; no throw */
}

/* Cold transfer: RedirectCall with <Response><Dial>target</Dial></Response>. */
static int twilio_transfer_call(void *self, const char *call_id, const char *target) {
    twilio_t *t = (twilio_t *)self;
    if (!twilio_is_configured_impl(t)) return -1;   /* RedirectCall EnsureConfigured */
    if (!call_id || !target) return -1;
    char *enc = html_encode(target);
    if (!enc) return -1;
    size_t need = strlen("<Response><Dial>") + strlen(enc) + strlen("</Dial></Response>") + 1;
    char *twiml = (char *)malloc(need);
    if (!twiml) { free(enc); return -1; }
    snprintf(twiml, need, "<Response><Dial>%s</Dial></Response>", enc);
    free(enc);
    char path[320];
    snprintf(path, sizeof(path), "/2010-04-01/Accounts/%s/Calls/%s.json",
             t->account_sid, call_id);
    char *form = NULL; size_t flen = 0, fcap = 0;
    bool ok = form_append(&form, &flen, &fcap, "Twiml", twiml);
    free(twiml);
    if (!ok) { free(form); return -1; }
    int status; char *body = NULL;
    twilio_request(t, "POST", path, "application/x-www-form-urlencoded", form, &status, &body);
    free(form); free(body);
    /* C# RedirectCall logs a warning on non-2xx but does not throw; the session
     * still latches Transferred. Return success. */
    return 0;
}

static void twilio_destroy(void *self) {
    twilio_t *t = (twilio_t *)self;
    if (!t) return;
    free(t->base_address);
    free(t->account_sid);
    free(t->auth_token);
    free(t->auth_header);
    free(t);
}

static const ca_tel_carrier_vtable_t TWILIO_VTABLE = {
    twilio_carrier_id, twilio_is_configured, twilio_provision, twilio_configure,
    twilio_dial, twilio_list, twilio_end_call, twilio_transfer_call, twilio_destroy
};

ca_tel_carrier_t *ca_tel_twilio_create(ca_tel_http_t http,
                                       const ca_tel_twilio_options_t *options) {
    twilio_t *t = (twilio_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->http = http;
    const char *base = (options && options->base_address) ? options->base_address
                                                          : "https://api.twilio.com";
    t->base_address = twl_strdup(base);
    if (options && options->account_sid) t->account_sid = twl_strdup(options->account_sid);
    if (options && options->auth_token)  t->auth_token  = twl_strdup(options->auth_token);
    if (!t->base_address) { twilio_destroy(t); return NULL; }

    if (twilio_is_configured_impl(t)) {
        /* Basic base64(AccountSid:AuthToken) */
        size_t need = strlen(t->account_sid) + 1 + strlen(t->auth_token) + 1;
        char *creds = (char *)malloc(need);
        if (!creds) { twilio_destroy(t); return NULL; }
        snprintf(creds, need, "%s:%s", t->account_sid, t->auth_token);
        char *b64 = ca_base64_encode((const uint8_t *)creds, strlen(creds));
        free(creds);
        if (!b64) { twilio_destroy(t); return NULL; }
        size_t hn = strlen("Basic ") + strlen(b64) + 1;
        t->auth_header = (char *)malloc(hn);
        if (t->auth_header) snprintf(t->auth_header, hn, "Basic %s", b64);
        free(b64);
        if (!t->auth_header) { twilio_destroy(t); return NULL; }
    }

    ca_tel_carrier_t *c = ca_tel_carrier_wrap(t, &TWILIO_VTABLE);
    if (!c) { twilio_destroy(t); return NULL; }
    return c;
}
