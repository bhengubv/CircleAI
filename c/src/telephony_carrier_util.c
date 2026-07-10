/*
 * telephony_carrier_util.c — shared internals for the carrier bindings.
 *
 * A compact recursive-descent JSON reader (object/array/string/number/bool/null)
 * used to walk carrier REST responses, decimal parsing, and PendingMediaStream
 * session assembly. Pure C11 + libc.
 */

#include "telephony_carrier_internal.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* ===========================================================================
 * JSON node model
 * =========================================================================== */

typedef struct {
    char        *key;    /* owned (object members only) */
    catj_node_t *val;
} catj_member_t;

struct catj_node {
    catj_type_t type;
    /* string / number: decoded/raw text (owned) */
    char *text;
    bool  boolean;
    /* array: items */
    catj_node_t **items;
    size_t        item_count;
    /* object: members */
    catj_member_t *members;
    size_t         member_count;
};

struct catj_doc {
    catj_node_t *root;
};

/* recursive free of a node tree */
static void catj_node_free(catj_node_t *n) {
    if (!n) return;
    free(n->text);
    for (size_t i = 0; i < n->item_count; ++i) catj_node_free(n->items[i]);
    free(n->items);
    for (size_t i = 0; i < n->member_count; ++i) {
        free(n->members[i].key);
        catj_node_free(n->members[i].val);
    }
    free(n->members);
    free(n);
}

/* ── parser ─────────────────────────────────────────────────────────────── */

typedef struct { const char *p; } catj_ctx_t;

static void skip_ws(catj_ctx_t *c) {
    while (*c->p == ' ' || *c->p == '\t' || *c->p == '\n' || *c->p == '\r') c->p++;
}

static catj_node_t *parse_value(catj_ctx_t *c);

/* decode a JSON string literal (assumes *p == '"'); returns owned decoded string
 * and advances past the closing quote. NULL on malformed. */
static char *parse_string_lit(catj_ctx_t *c) {
    if (*c->p != '"') return NULL;
    c->p++;
    size_t cap = 16, len = 0;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    while (*c->p && *c->p != '"') {
        char ch = *c->p++;
        if (ch == '\\') {
            char e = *c->p++;
            switch (e) {
                case '"':  ch = '"';  break;
                case '\\': ch = '\\'; break;
                case '/':  ch = '/';  break;
                case 'n':  ch = '\n'; break;
                case 't':  ch = '\t'; break;
                case 'r':  ch = '\r'; break;
                case 'b':  ch = '\b'; break;
                case 'f':  ch = '\f'; break;
                case 'u': {
                    /* decode \uXXXX into UTF-8 (BMP only; enough for carrier data) */
                    if (!isxdigit((unsigned char)c->p[0]) || !isxdigit((unsigned char)c->p[1]) ||
                        !isxdigit((unsigned char)c->p[2]) || !isxdigit((unsigned char)c->p[3])) {
                        free(out); return NULL;
                    }
                    char hex[5] = { c->p[0], c->p[1], c->p[2], c->p[3], 0 };
                    unsigned cp = (unsigned)strtoul(hex, NULL, 16);
                    c->p += 4;
                    /* encode cp as UTF-8 */
                    char utf[4]; int n = 0;
                    if (cp < 0x80) { utf[n++] = (char)cp; }
                    else if (cp < 0x800) {
                        utf[n++] = (char)(0xC0 | (cp >> 6));
                        utf[n++] = (char)(0x80 | (cp & 0x3F));
                    } else {
                        utf[n++] = (char)(0xE0 | (cp >> 12));
                        utf[n++] = (char)(0x80 | ((cp >> 6) & 0x3F));
                        utf[n++] = (char)(0x80 | (cp & 0x3F));
                    }
                    for (int k = 0; k < n; ++k) {
                        if (len + 1 >= cap) { cap *= 2; char *nn = realloc(out, cap); if (!nn) { free(out); return NULL; } out = nn; }
                        out[len++] = utf[k];
                    }
                    continue;
                }
                default: free(out); return NULL;
            }
        }
        if (len + 1 >= cap) { cap *= 2; char *nn = realloc(out, cap); if (!nn) { free(out); return NULL; } out = nn; }
        out[len++] = ch;
    }
    if (*c->p != '"') { free(out); return NULL; }
    c->p++;   /* closing quote */
    out[len] = '\0';
    return out;
}

static catj_node_t *node_new(catj_type_t t) {
    catj_node_t *n = (catj_node_t *)calloc(1, sizeof(*n));
    if (n) n->type = t;
    return n;
}

static catj_node_t *parse_string(catj_ctx_t *c) {
    char *s = parse_string_lit(c);
    if (!s) return NULL;
    catj_node_t *n = node_new(CATJ_STRING);
    if (!n) { free(s); return NULL; }
    n->text = s;
    return n;
}

static catj_node_t *parse_number(catj_ctx_t *c) {
    const char *start = c->p;
    if (*c->p == '-') c->p++;
    while (isdigit((unsigned char)*c->p)) c->p++;
    if (*c->p == '.') { c->p++; while (isdigit((unsigned char)*c->p)) c->p++; }
    if (*c->p == 'e' || *c->p == 'E') {
        c->p++;
        if (*c->p == '+' || *c->p == '-') c->p++;
        while (isdigit((unsigned char)*c->p)) c->p++;
    }
    size_t len = (size_t)(c->p - start);
    if (len == 0) return NULL;
    catj_node_t *n = node_new(CATJ_NUMBER);
    if (!n) return NULL;
    n->text = (char *)malloc(len + 1);
    if (!n->text) { catj_node_free(n); return NULL; }
    memcpy(n->text, start, len);
    n->text[len] = '\0';
    return n;
}

static catj_node_t *parse_array(catj_ctx_t *c) {
    c->p++;   /* '[' */
    catj_node_t *n = node_new(CATJ_ARRAY);
    if (!n) return NULL;
    skip_ws(c);
    if (*c->p == ']') { c->p++; return n; }
    for (;;) {
        skip_ws(c);
        catj_node_t *v = parse_value(c);
        if (!v) { catj_node_free(n); return NULL; }
        catj_node_t **ni = realloc(n->items, (n->item_count + 1) * sizeof(*n->items));
        if (!ni) { catj_node_free(v); catj_node_free(n); return NULL; }
        n->items = ni;
        n->items[n->item_count++] = v;
        skip_ws(c);
        if (*c->p == ',') { c->p++; continue; }
        if (*c->p == ']') { c->p++; break; }
        catj_node_free(n); return NULL;
    }
    return n;
}

static catj_node_t *parse_object(catj_ctx_t *c) {
    c->p++;   /* '{' */
    catj_node_t *n = node_new(CATJ_OBJECT);
    if (!n) return NULL;
    skip_ws(c);
    if (*c->p == '}') { c->p++; return n; }
    for (;;) {
        skip_ws(c);
        if (*c->p != '"') { catj_node_free(n); return NULL; }
        char *key = parse_string_lit(c);
        if (!key) { catj_node_free(n); return NULL; }
        skip_ws(c);
        if (*c->p != ':') { free(key); catj_node_free(n); return NULL; }
        c->p++;
        skip_ws(c);
        catj_node_t *v = parse_value(c);
        if (!v) { free(key); catj_node_free(n); return NULL; }
        catj_member_t *nm = realloc(n->members, (n->member_count + 1) * sizeof(*n->members));
        if (!nm) { free(key); catj_node_free(v); catj_node_free(n); return NULL; }
        n->members = nm;
        n->members[n->member_count].key = key;
        n->members[n->member_count].val = v;
        n->member_count++;
        skip_ws(c);
        if (*c->p == ',') { c->p++; continue; }
        if (*c->p == '}') { c->p++; break; }
        catj_node_free(n); return NULL;
    }
    return n;
}

static catj_node_t *parse_literal(catj_ctx_t *c, const char *lit, catj_type_t t,
                                  bool boolean) {
    size_t len = strlen(lit);
    if (strncmp(c->p, lit, len) != 0) return NULL;
    c->p += len;
    catj_node_t *n = node_new(t);
    if (n) n->boolean = boolean;
    return n;
}

static catj_node_t *parse_value(catj_ctx_t *c) {
    skip_ws(c);
    char ch = *c->p;
    if (ch == '"') return parse_string(c);
    if (ch == '{') return parse_object(c);
    if (ch == '[') return parse_array(c);
    if (ch == 't') return parse_literal(c, "true", CATJ_BOOL, true);
    if (ch == 'f') return parse_literal(c, "false", CATJ_BOOL, false);
    if (ch == 'n') return parse_literal(c, "null", CATJ_NULL, false);
    if (ch == '-' || isdigit((unsigned char)ch)) return parse_number(c);
    return NULL;
}

catj_doc_t *catj_parse(const char *json) {
    if (!json) return NULL;
    catj_doc_t *doc = (catj_doc_t *)calloc(1, sizeof(*doc));
    if (!doc) return NULL;
    catj_ctx_t c = { json };
    doc->root = parse_value(&c);
    if (!doc->root) { free(doc); return NULL; }
    skip_ws(&c);
    /* trailing garbage tolerated (carrier bodies are single values) */
    return doc;
}
void catj_free(catj_doc_t *doc) {
    if (!doc) return;
    catj_node_free(doc->root);
    free(doc);
}
const catj_node_t *catj_root(const catj_doc_t *doc) { return doc ? doc->root : NULL; }

catj_type_t catj_type(const catj_node_t *n) { return n ? n->type : CATJ_NULL; }

const catj_node_t *catj_get(const catj_node_t *n, const char *key) {
    if (!n || n->type != CATJ_OBJECT || !key) return NULL;
    for (size_t i = 0; i < n->member_count; ++i)
        if (strcmp(n->members[i].key, key) == 0) return n->members[i].val;
    return NULL;
}
size_t catj_array_len(const catj_node_t *n) {
    return (n && n->type == CATJ_ARRAY) ? n->item_count : 0;
}
const catj_node_t *catj_at(const catj_node_t *n, size_t i) {
    if (!n || n->type != CATJ_ARRAY || i >= n->item_count) return NULL;
    return n->items[i];
}
const char *catj_string(const catj_node_t *n) {
    return (n && n->type == CATJ_STRING) ? n->text : NULL;
}
const char *catj_number_text(const catj_node_t *n) {
    return (n && n->type == CATJ_NUMBER) ? n->text : NULL;
}

/* ===========================================================================
 * decimal
 * =========================================================================== */

bool ca_tel_carrier_decimal_from_str(const char *s, ca_tel_decimal_t *out) {
    if (!s || !out) return false;
    /* parse [-]digits[.digits] into value*1e6, invariant culture. */
    const char *p = s;
    while (*p == ' ') p++;
    bool neg = false;
    if (*p == '-') { neg = true; p++; }
    else if (*p == '+') { p++; }
    if (!isdigit((unsigned char)*p) && *p != '.') return false;

    int64_t whole = 0;
    bool any = false;
    while (isdigit((unsigned char)*p)) { whole = whole * 10 + (*p - '0'); p++; any = true; }
    int64_t frac = 0;
    int64_t scale = CA_TEL_DECIMAL_SCALE;   /* 1e6 */
    if (*p == '.') {
        p++;
        int64_t div = 1;
        int digits = 0;
        while (isdigit((unsigned char)*p) && digits < 6) {
            frac = frac * 10 + (*p - '0');
            div *= 10;
            p++; digits++; any = true;
        }
        /* skip any excess fractional digits (truncate to 6 dp) */
        while (isdigit((unsigned char)*p)) p++;
        /* scale frac up to 1e6 */
        frac = frac * (CA_TEL_DECIMAL_SCALE / div);
        (void)scale;
    }
    if (!any) return false;
    /* allow exponent-free trailing */
    int64_t v = whole * CA_TEL_DECIMAL_SCALE + frac;
    *out = neg ? -v : v;
    return true;
}

bool ca_tel_carrier_parse_decimal(const catj_node_t *n, ca_tel_decimal_t *out) {
    if (!n || !out) return false;
    if (n->type == CATJ_NUMBER)
        return ca_tel_carrier_decimal_from_str(catj_number_text(n), out);
    if (n->type == CATJ_STRING)
        return ca_tel_carrier_decimal_from_str(catj_string(n), out);
    return false;
}

/* ===========================================================================
 * session assembly
 * =========================================================================== */

ca_tel_call_session_t *ca_tel_carrier_make_pending_session(
    const ca_tel_call_info_t *info, ca_tel_carrier_t *carrier) {
    if (!info || !carrier) return NULL;
    ca_tel_media_stream_t *pending = ca_tel_pending_media_create(info);
    if (!pending) return NULL;
    ca_tel_call_session_t *s = ca_tel_media_call_session_create(pending, carrier);
    if (!s) { ca_tel_media_stream_destroy(pending); return NULL; }
    return s;
}
