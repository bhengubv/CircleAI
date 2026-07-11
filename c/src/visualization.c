/*
 * visualization.c — CircleAI.Visualization (C11 port).
 *
 * Dashboard store keyed by DashboardId. The ApiDoc + Site builders parse the
 * relevant subset of JSON with a small self-contained scanner (string decode +
 * value-skip) — enough to mirror the C# behaviour for well-formed specs without
 * pulling in an external JSON library. Deterministic. Pure C11 + libc. No
 * pthreads.
 */

#include "circle_ai/visualization.h"
#include "board_common.h"

/* Empty-GUID string used by the Null builders' record ids. */
#define VIZ_EMPTY_GUID "00000000-0000-0000-0000-000000000000"

/* ===========================================================================
 * Minimal JSON scanner (objects, arrays, strings; skips numbers/bools/null)
 * ===========================================================================
 * These operate on a NUL-terminated buffer and a moving cursor index. They are
 * deliberately small: they recover the values the C# code reads (info.title,
 * pages[].path, pages[].html) and can skip arbitrary values so array iteration
 * is robust.
 */

static void json_skip_ws(const char *s, size_t *i) {
    while (s[*i] == ' ' || s[*i] == '\t' || s[*i] == '\n' || s[*i] == '\r') (*i)++;
}

/* Append a UTF-8 encoding of a Unicode code point to a growable buffer. */
static bool sb_append_cp(char **buf, size_t *len, size_t *cap, unsigned cp) {
    char tmp[4]; size_t n;
    if (cp < 0x80) { tmp[0] = (char)cp; n = 1; }
    else if (cp < 0x800) {
        tmp[0] = (char)(0xC0 | (cp >> 6));
        tmp[1] = (char)(0x80 | (cp & 0x3F)); n = 2;
    } else if (cp < 0x10000) {
        tmp[0] = (char)(0xE0 | (cp >> 12));
        tmp[1] = (char)(0x80 | ((cp >> 6) & 0x3F));
        tmp[2] = (char)(0x80 | (cp & 0x3F)); n = 3;
    } else {
        tmp[0] = (char)(0xF0 | (cp >> 18));
        tmp[1] = (char)(0x80 | ((cp >> 12) & 0x3F));
        tmp[2] = (char)(0x80 | ((cp >> 6) & 0x3F));
        tmp[3] = (char)(0x80 | (cp & 0x3F)); n = 4;
    }
    if (*len + n + 1 > *cap) {
        size_t nc = (*cap ? *cap * 2 : 16);
        while (nc < *len + n + 1) nc *= 2;
        char *nb = (char *)realloc(*buf, nc);
        if (!nb) return false;
        *buf = nb; *cap = nc;
    }
    memcpy(*buf + *len, tmp, n);
    *len += n;
    (*buf)[*len] = '\0';
    return true;
}

static int hex_val(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

/* Parse a JSON string literal at s[*i] (which must be '"'); advance past the
 * closing quote. Returns a freshly-malloc'd decoded string, or NULL on a
 * malformed literal / OOM. */
static char *json_parse_string(const char *s, size_t *i) {
    if (s[*i] != '"') return NULL;
    (*i)++;
    size_t cap = 16, len = 0;
    char *buf = (char *)malloc(cap);
    if (!buf) return NULL;
    buf[0] = '\0';
    for (;;) {
        char c = s[*i];
        if (c == '\0') { free(buf); return NULL; }
        if (c == '"') { (*i)++; return buf; }
        if (c == '\\') {
            (*i)++;
            char e = s[*i];
            unsigned cp;
            switch (e) {
                case '"':  cp = '"';  break;
                case '\\': cp = '\\'; break;
                case '/':  cp = '/';  break;
                case 'b':  cp = '\b'; break;
                case 'f':  cp = '\f'; break;
                case 'n':  cp = '\n'; break;
                case 'r':  cp = '\r'; break;
                case 't':  cp = '\t'; break;
                case 'u': {
                    int h0 = hex_val(s[*i + 1]), h1 = hex_val(s[*i + 2]),
                        h2 = hex_val(s[*i + 3]), h3 = hex_val(s[*i + 4]);
                    if (h0 < 0 || h1 < 0 || h2 < 0 || h3 < 0) { free(buf); return NULL; }
                    cp = (unsigned)((h0 << 12) | (h1 << 8) | (h2 << 4) | h3);
                    *i += 4;
                    /* surrogate pair */
                    if (cp >= 0xD800 && cp <= 0xDBFF && s[*i + 1] == '\\' && s[*i + 2] == 'u') {
                        int g0 = hex_val(s[*i + 3]), g1 = hex_val(s[*i + 4]),
                            g2 = hex_val(s[*i + 5]), g3 = hex_val(s[*i + 6]);
                        if (g0 >= 0 && g1 >= 0 && g2 >= 0 && g3 >= 0) {
                            unsigned lo = (unsigned)((g0 << 12) | (g1 << 8) | (g2 << 4) | g3);
                            if (lo >= 0xDC00 && lo <= 0xDFFF) {
                                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                                *i += 6;
                            }
                        }
                    }
                    break;
                }
                default: free(buf); return NULL;
            }
            (*i)++;
            if (!sb_append_cp(&buf, &len, &cap, cp)) { free(buf); return NULL; }
        } else {
            (*i)++;
            if (!sb_append_cp(&buf, &len, &cap, (unsigned char)c)) { free(buf); return NULL; }
        }
    }
}

/* Skip a full JSON value at s[*i]. Returns false on malformed input. */
static bool json_skip_value(const char *s, size_t *i) {
    json_skip_ws(s, i);
    char c = s[*i];
    if (c == '"') { char *tmp = json_parse_string(s, i); if (!tmp) return false; free(tmp); return true; }
    if (c == '{' || c == '[') {
        char open = c, close = (c == '{') ? '}' : ']';
        (*i)++;
        for (;;) {
            json_skip_ws(s, i);
            if (s[*i] == '\0') return false;
            if (s[*i] == close) { (*i)++; return true; }
            if (s[*i] == ',' || s[*i] == ':') { (*i)++; continue; }
            if (s[*i] == '"') { char *tmp = json_parse_string(s, i); if (!tmp) return false; free(tmp); continue; }
            if (s[*i] == '{' || s[*i] == '[') { if (!json_skip_value(s, i)) return false; continue; }
            /* primitive (number / true / false / null) */
            while (s[*i] && s[*i] != ',' && s[*i] != close &&
                   s[*i] != ' ' && s[*i] != '\t' && s[*i] != '\n' && s[*i] != '\r') (*i)++;
        }
        (void)open;
    }
    /* bare primitive */
    if (c == '\0') return false;
    while (s[*i] && s[*i] != ',' && s[*i] != '}' && s[*i] != ']' &&
           s[*i] != ' ' && s[*i] != '\t' && s[*i] != '\n' && s[*i] != '\r') (*i)++;
    return true;
}

/* Within the object beginning at s[*i]=='{', find key `key` and return its
 * string value (decoded), advancing *i past the value. Returns NULL if the
 * key is absent or its value is not a string. Non-destructive scan on failure
 * is not guaranteed — callers pass a private cursor. */
static char *json_object_get_string(const char *s, size_t obj_start,
                                     const char *key) {
    size_t i = obj_start;
    json_skip_ws(s, &i);
    if (s[i] != '{') return NULL;
    i++;
    for (;;) {
        json_skip_ws(s, &i);
        if (s[i] == '}' || s[i] == '\0') return NULL;
        if (s[i] == ',') { i++; continue; }
        if (s[i] != '"') return NULL;
        char *k = json_parse_string(s, &i);
        if (!k) return NULL;
        json_skip_ws(s, &i);
        if (s[i] != ':') { free(k); return NULL; }
        i++;
        json_skip_ws(s, &i);
        bool match = strcmp(k, key) == 0;
        free(k);
        if (match) {
            if (s[i] == '"') return json_parse_string(s, &i);
            return NULL; /* value is not a string */
        }
        if (!json_skip_value(s, &i)) return NULL;
    }
}

/* Locate the object value of a nested key path "info" -> "title": returns the
 * string, or NULL. */
static char *json_get_info_title(const char *s) {
    /* find top-level "info" object start */
    size_t i = 0;
    json_skip_ws(s, &i);
    if (s[i] != '{') return NULL;
    i++;
    for (;;) {
        json_skip_ws(s, &i);
        if (s[i] == '}' || s[i] == '\0') return NULL;
        if (s[i] == ',') { i++; continue; }
        if (s[i] != '"') return NULL;
        char *k = json_parse_string(s, &i);
        if (!k) return NULL;
        json_skip_ws(s, &i);
        if (s[i] != ':') { free(k); return NULL; }
        i++;
        json_skip_ws(s, &i);
        bool is_info = strcmp(k, "info") == 0;
        free(k);
        if (is_info) {
            if (s[i] != '{') return NULL;
            return json_object_get_string(s, i, "title");
        }
        if (!json_skip_value(s, &i)) return NULL;
    }
}

/* ── DashboardDefinition ────────────────────────────────────────────────── */

void ca_dashboard_definition_free(ca_dashboard_definition_t *d) {
    if (!d) return;
    free(d->dashboard_id);
    free(d->title);
    free(d->json_spec);
    d->dashboard_id = d->title = d->json_spec = NULL;
}
void ca_dashboard_definition_free_array(ca_dashboard_definition_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_dashboard_definition_free(&arr[i]);
    free(arr);
}
static bool defn_copy(ca_dashboard_definition_t *dst,
                      const ca_dashboard_definition_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->dashboard_id = cab_strdup_empty(src->dashboard_id);
    dst->title        = cab_strdup_empty(src->title);
    dst->json_spec    = cab_strdup_empty(src->json_spec);
    if (!dst->dashboard_id || !dst->title || !dst->json_spec) {
        ca_dashboard_definition_free(dst); return false;
    }
    return true;
}

/* ── ApiDoc ─────────────────────────────────────────────────────────────── */

void ca_api_doc_free(ca_api_doc_t *d) {
    if (!d) return;
    free(d->doc_id);
    free(d->title);
    free(d->open_api_json);
    d->doc_id = d->title = d->open_api_json = NULL;
}

/* ── GeneratedSite ──────────────────────────────────────────────────────── */

void ca_generated_site_free(ca_generated_site_t *s) {
    if (!s) return;
    free(s->site_id);
    if (s->files) {
        for (size_t i = 0; i < s->file_count; ++i) {
            free(s->files[i].path);
            free(s->files[i].bytes);
        }
        free(s->files);
    }
    s->site_id = NULL; s->files = NULL; s->file_count = 0;
}

/* ── InMemoryDashboardStore ─────────────────────────────────────────────── */

struct ca_dashboard_definition_store {
    ca_dashboard_definition_t *items;
    size_t                     count, cap;
};

ca_dashboard_definition_store_t *ca_dashboard_definition_store_create(void) {
    return (ca_dashboard_definition_store_t *)calloc(
        1, sizeof(ca_dashboard_definition_store_t));
}
void ca_dashboard_definition_store_destroy(ca_dashboard_definition_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_dashboard_definition_free(&s->items[i]);
    free(s->items);
    free(s);
}
const char *ca_dashboard_definition_store_backend_id(
    const ca_dashboard_definition_store_t *s) {
    (void)s; return "in-memory";
}

int ca_dashboard_definition_store_upsert(ca_dashboard_definition_store_t *s,
                                         const ca_dashboard_definition_t *d) {
    if (!s || !d || cab_is_ws(d->dashboard_id)) return -1;
    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ord_eq(s->items[i].dashboard_id, d->dashboard_id)) {
            ca_dashboard_definition_t copy;
            if (!defn_copy(&copy, d)) return -1;
            ca_dashboard_definition_free(&s->items[i]);
            s->items[i] = copy;
            return 0;
        }
    }
    ca_dashboard_definition_t copy;
    if (!defn_copy(&copy, d)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->items, nc * sizeof(*s->items));
        if (!n) { ca_dashboard_definition_free(&copy); return -1; }
        s->items = (ca_dashboard_definition_t *)n;
        s->cap = nc;
    }
    s->items[s->count++] = copy;
    return 0;
}

bool ca_dashboard_definition_store_get(const ca_dashboard_definition_store_t *s,
                                       const char *id,
                                       ca_dashboard_definition_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || cab_is_ws(id) || !out) return false;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].dashboard_id, id))
            return defn_copy(out, &s->items[i]);
    return false;
}

ca_dashboard_definition_t *ca_dashboard_definition_store_list(
    const ca_dashboard_definition_store_t *s, size_t *out_count) {
    if (!out_count) return NULL;
    if (!s) { *out_count = (size_t)-1; return NULL; }
    if (s->count == 0) { *out_count = 0; return NULL; }
    ca_dashboard_definition_t *out =
        (ca_dashboard_definition_t *)calloc(s->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < s->count; ++i) {
        if (!defn_copy(&out[i], &s->items[i])) {
            ca_dashboard_definition_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = s->count;
    return out;
}

const char *ca_viz_null_dashboard_store_backend_id(void) { return "null"; }

/* ── JsonApiDocBuilder ──────────────────────────────────────────────────── */

const char *ca_api_doc_builder_backend_id(void) { return "json-normaliser"; }
const char *ca_viz_null_api_doc_builder_backend_id(void) { return "null"; }

/* Trim leading/trailing ASCII whitespace into a fresh string. */
static char *trim_dup(const char *s) {
    while (*s == ' ' || *s == '\t' || *s == '\n' || *s == '\r') s++;
    size_t n = strlen(s);
    while (n > 0 && (s[n - 1] == ' ' || s[n - 1] == '\t' ||
                     s[n - 1] == '\n' || s[n - 1] == '\r')) n--;
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    memcpy(out, s, n);
    out[n] = '\0';
    return out;
}

bool ca_api_doc_build(const char *open_api_spec, ca_api_doc_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out || cab_is_ws(open_api_spec)) return false;

    char *title = json_get_info_title(open_api_spec);
    if (!title) title = cab_strdup_empty("API");
    if (!title) return false;

    /* DocId = title, spaces -> '-', lowercased (invariant/ASCII). */
    size_t tn = strlen(title);
    char *doc_id = (char *)malloc(tn + 1);
    if (!doc_id) { free(title); return false; }
    for (size_t i = 0; i < tn; ++i) {
        char ch = title[i];
        if (ch == ' ') ch = '-';
        else ch = (char)tolower((unsigned char)ch);
        doc_id[i] = ch;
    }
    doc_id[tn] = '\0';

    char *canonical = trim_dup(open_api_spec);
    if (!canonical) { free(title); free(doc_id); return false; }

    out->doc_id        = doc_id;
    out->title         = title;
    out->open_api_json = canonical;
    return true;
}

/* ── StaticSiteBuilder ──────────────────────────────────────────────────── */

const char *ca_site_builder_backend_id(void) { return "static"; }
const char *ca_viz_null_site_builder_backend_id(void) { return "null"; }

static bool site_push_file(ca_generated_site_t *site, char *path, char *html) {
    /* replace if path already present (Dictionary semantics) */
    size_t hlen = strlen(html);
    for (size_t i = 0; i < site->file_count; ++i) {
        if (cab_ord_eq(site->files[i].path, path)) {
            uint8_t *buf = NULL;
            if (hlen > 0) { buf = (uint8_t *)malloc(hlen); if (!buf) return false; memcpy(buf, html, hlen); }
            free(site->files[i].bytes);
            site->files[i].bytes = buf;
            site->files[i].len = hlen;
            free(path);
            return true;
        }
    }
    ca_site_file_t *nf = (ca_site_file_t *)realloc(
        site->files, (site->file_count + 1) * sizeof(*nf));
    if (!nf) return false;
    site->files = nf;
    ca_site_file_t *slot = &site->files[site->file_count];
    slot->path = path;
    slot->len = hlen;
    slot->bytes = NULL;
    if (hlen > 0) {
        slot->bytes = (uint8_t *)malloc(hlen);
        if (!slot->bytes) return false;
        memcpy(slot->bytes, html, hlen);
    }
    site->file_count++;
    return true;
}

bool ca_site_build(const char *site_spec, ca_generated_site_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out || cab_is_ws(site_spec)) return false;

    /* Find top-level "pages" array. */
    const char *s = site_spec;
    size_t i = 0;
    json_skip_ws(s, &i);
    if (s[i] != '{') return false;
    i++;
    size_t pages_start = 0;
    bool found = false;
    for (;;) {
        json_skip_ws(s, &i);
        if (s[i] == '}' || s[i] == '\0') break;
        if (s[i] == ',') { i++; continue; }
        if (s[i] != '"') return false;
        char *k = json_parse_string(s, &i);
        if (!k) return false;
        json_skip_ws(s, &i);
        if (s[i] != ':') { free(k); return false; }
        i++;
        json_skip_ws(s, &i);
        bool is_pages = strcmp(k, "pages") == 0;
        free(k);
        if (is_pages) {
            if (s[i] != '[') return false; /* pages must be an array */
            pages_start = i;
            found = true;
            break;
        }
        if (!json_skip_value(s, &i)) return false;
    }
    if (!found) return false;

    out->site_id = cab_strdup_empty("site-0");
    if (!out->site_id) return false;

    /* Iterate the array of page objects. */
    size_t p = pages_start + 1; /* past '[' */
    for (;;) {
        json_skip_ws(s, &p);
        if (s[p] == ']' || s[p] == '\0') break;
        if (s[p] == ',') { p++; continue; }
        if (s[p] != '{') { if (!json_skip_value(s, &p)) { ca_generated_site_free(out); return false; } continue; }

        size_t obj_start = p;
        char *path = json_object_get_string(s, obj_start, "path");
        char *html = json_object_get_string(s, obj_start, "html");
        /* advance p past this object */
        if (!json_skip_value(s, &p)) { free(path); free(html); ca_generated_site_free(out); return false; }

        if (!path || cab_is_ws(path) || !html) { free(path); free(html); continue; }
        if (!site_push_file(out, path, html)) {
            free(html); ca_generated_site_free(out); return false;
        }
        free(html);
    }
    return true;
}
