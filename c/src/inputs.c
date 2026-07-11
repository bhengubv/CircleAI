/*
 * inputs.c — CircleAI.Inputs (C11 port).
 *
 * Scraper / stealth client / video-ingest are registry-backed (host registers
 * payloads keyed by URL / path; unknown keys yield an empty result, mirroring the
 * network/ffmpeg boundary being an injected dependency). MCP scrape wraps an
 * injected scraper vtable. The asciinema cast parser is the deterministic core of
 * the C# LoadAsync (file reading is the host's job) + RenderTranscript.
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/inputs.h"
#include "board_common.h"
#include <stdio.h>

/* ── shared kv helpers ──────────────────────────────────────────────────── */

static void kv_free(ca_inputs_kv_t *kv, size_t n) {
    if (!kv) return;
    for (size_t i = 0; i < n; ++i) { free(kv[i].key); free(kv[i].value); }
    free(kv);
}
static bool kv_copy(ca_inputs_kv_t **out, const ca_inputs_kv_t *src, size_t n) {
    *out = NULL;
    if (n == 0) return true;
    ca_inputs_kv_t *v = (ca_inputs_kv_t *)calloc(n, sizeof(*v));
    if (!v) return false;
    for (size_t i = 0; i < n; ++i) {
        v[i].key   = cab_strdup_empty(src ? src[i].key : NULL);
        v[i].value = cab_strdup_empty(src ? src[i].value : NULL);
        if (!v[i].key || !v[i].value) { kv_free(v, i + 1); return false; }
    }
    *out = v;
    return true;
}

/* ── ScrapedPage ────────────────────────────────────────────────────────── */

void ca_scraped_page_free(ca_scraped_page_t *p) {
    if (!p) return;
    free(p->url);
    free(p->text);
    free(p->title);
    kv_free(p->metadata, p->metadata_count);
    cab_strv_free(p->resolved_links, p->link_count);
    memset(p, 0, sizeof(*p));
}
static bool page_copy(ca_scraped_page_t *dst, const ca_scraped_page_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->url   = cab_strdup_empty(src->url);
    dst->text  = cab_strdup_empty(src->text);
    dst->title = src->title ? cab_strdup(src->title) : NULL;
    if (!dst->url || !dst->text || (src->title && !dst->title)) {
        ca_scraped_page_free(dst); return false;
    }
    if (!kv_copy(&dst->metadata, src->metadata, src->metadata_count)) {
        ca_scraped_page_free(dst); return false;
    }
    dst->metadata_count = src->metadata_count;
    if (!cab_strv_copy(&dst->resolved_links, src->resolved_links, src->link_count)) {
        ca_scraped_page_free(dst); return false;
    }
    dst->link_count = src->link_count;
    return true;
}
/* Build an empty page (Text "") for an unknown URL. */
static bool page_empty(ca_scraped_page_t *dst, const char *url) {
    memset(dst, 0, sizeof(*dst));
    dst->url  = cab_strdup_empty(url);
    dst->text = cab_strdup_empty("");
    if (!dst->url || !dst->text) { ca_scraped_page_free(dst); return false; }
    return true;
}

/* ── VideoIngestResult ──────────────────────────────────────────────────── */

void ca_video_ingest_result_free(ca_video_ingest_result_t *r) {
    if (!r) return;
    free(r->transcript);
    cab_strv_free(r->shots, r->shot_count);
    memset(r, 0, sizeof(*r));
}
static bool video_copy(ca_video_ingest_result_t *dst,
                       const ca_video_ingest_result_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->duration_ms = src->duration_ms;
    dst->frame_count = src->frame_count;
    dst->transcript = cab_strdup_empty(src->transcript);
    if (!dst->transcript) return false;
    if (!cab_strv_copy(&dst->shots, src->shots, src->shot_count)) {
        ca_video_ingest_result_free(dst); return false;
    }
    dst->shot_count = src->shot_count;
    return true;
}

/* ── TerminalCast ───────────────────────────────────────────────────────── */

void ca_terminal_cast_free(ca_terminal_cast_t *c) {
    if (!c) return;
    if (c->segments) {
        for (size_t i = 0; i < c->segment_count; ++i) free(c->segments[i].text);
        free(c->segments);
    }
    memset(c, 0, sizeof(*c));
}

/* ── generic keyed URL/path -> page/result registry ─────────────────────── */

typedef struct {
    char             *key;  /* owned */
    ca_scraped_page_t page; /* owned */
} page_entry_t;

struct ca_web_scraper {
    page_entry_t *items;
    size_t        count, cap;
};

static int registry_put_page(page_entry_t **items, size_t *count, size_t *cap,
                             const char *url, const ca_scraped_page_t *page) {
    for (size_t i = 0; i < *count; ++i) {
        if (cab_ord_eq((*items)[i].key, url)) {
            ca_scraped_page_t copy;
            if (!page_copy(&copy, page)) return -1;
            ca_scraped_page_free(&(*items)[i].page);
            (*items)[i].page = copy;
            return 0;
        }
    }
    ca_scraped_page_t copy;
    if (!page_copy(&copy, page)) return -1;
    char *k = cab_strdup_empty(url);
    if (!k) { ca_scraped_page_free(&copy); return -1; }
    if (*count == *cap) {
        size_t nc = *cap ? *cap * 2 : 4;
        void *n = realloc(*items, nc * sizeof(page_entry_t));
        if (!n) { ca_scraped_page_free(&copy); free(k); return -1; }
        *items = (page_entry_t *)n;
        *cap = nc;
    }
    (*items)[*count].key = k;
    (*items)[*count].page = copy;
    (*count)++;
    return 0;
}
static bool registry_get_page(const page_entry_t *items, size_t count,
                              const char *url, ca_scraped_page_t *out) {
    for (size_t i = 0; i < count; ++i)
        if (cab_ord_eq(items[i].key, url))
            return page_copy(out, &items[i].page);
    return page_empty(out, url);
}
static void registry_free(page_entry_t *items, size_t count) {
    if (!items) return;
    for (size_t i = 0; i < count; ++i) {
        free(items[i].key);
        ca_scraped_page_free(&items[i].page);
    }
    free(items);
}

/* ── RegistryWebScraper ─────────────────────────────────────────────────── */

ca_web_scraper_t *ca_web_scraper_create(void) {
    return (ca_web_scraper_t *)calloc(1, sizeof(ca_web_scraper_t));
}
void ca_web_scraper_destroy(ca_web_scraper_t *s) {
    if (!s) return;
    registry_free(s->items, s->count);
    free(s);
}
const char *ca_web_scraper_backend_id(const ca_web_scraper_t *s) {
    (void)s; return "registry";
}
int ca_web_scraper_register(ca_web_scraper_t *s, const char *url,
                            const ca_scraped_page_t *page) {
    if (!s || cab_is_ws(url) || !page) return -1;
    return registry_put_page(&s->items, &s->count, &s->cap, url, page);
}
bool ca_web_scraper_fetch(const ca_web_scraper_t *s, const char *url,
                          ca_scraped_page_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || cab_is_ws(url) || !out) return false;
    return registry_get_page(s->items, s->count, url, out);
}
const char *ca_inputs_null_web_scraper_backend_id(void) { return "null"; }

/* ── RegistryStealthClient ──────────────────────────────────────────────── */

struct ca_stealth_client {
    page_entry_t *items;
    size_t        count, cap;
    int           seq; /* rotating header counter (deterministic) */
};

ca_stealth_client_t *ca_stealth_client_create(void) {
    return (ca_stealth_client_t *)calloc(1, sizeof(ca_stealth_client_t));
}
void ca_stealth_client_destroy(ca_stealth_client_t *s) {
    if (!s) return;
    registry_free(s->items, s->count);
    free(s);
}
const char *ca_stealth_client_backend_id(const ca_stealth_client_t *s) {
    (void)s; return "stealth-registry";
}
int ca_stealth_client_register(ca_stealth_client_t *s, const char *url,
                               const ca_scraped_page_t *page) {
    if (!s || cab_is_ws(url) || !page) return -1;
    return registry_put_page(&s->items, &s->count, &s->cap, url, page);
}
bool ca_stealth_client_get(ca_stealth_client_t *s, const char *url,
                           const ca_inputs_kv_t *headers, size_t header_count,
                           ca_scraped_page_t *out) {
    (void)headers; (void)header_count;
    if (out) memset(out, 0, sizeof(*out));
    if (!s || cab_is_ws(url) || !out) return false;
    s->seq++; /* rotate header set (side-effect parity; deterministic) */
    return registry_get_page(s->items, s->count, url, out);
}
const char *ca_inputs_null_stealth_client_backend_id(void) { return "null"; }

/* ── DefaultMcpWebScrape ────────────────────────────────────────────────── */

struct ca_mcp_web_scrape {
    ca_web_scraper_vtable_t inner; /* borrowed contents */
    char                   *backend_id; /* owned "mcp:<inner>" */
};

ca_mcp_web_scrape_t *ca_mcp_web_scrape_create(const ca_web_scraper_vtable_t *inner) {
    if (!inner || !inner->fetch) return NULL;
    ca_mcp_web_scrape_t *m = (ca_mcp_web_scrape_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    m->inner = *inner;
    const char *ib = inner->backend_id ? inner->backend_id(inner->user) : "unknown";
    size_t n = strlen(ib) + 5; /* "mcp:" + NUL */
    m->backend_id = (char *)malloc(n);
    if (!m->backend_id) { free(m); return NULL; }
    snprintf(m->backend_id, n, "mcp:%s", ib);
    return m;
}
void ca_mcp_web_scrape_destroy(ca_mcp_web_scrape_t *m) {
    if (!m) return;
    free(m->backend_id);
    free(m);
}
const char *ca_mcp_web_scrape_backend_id(const ca_mcp_web_scrape_t *m) {
    return m ? m->backend_id : NULL;
}
bool ca_mcp_web_scrape_scrape(const ca_mcp_web_scrape_t *m, const char *url,
                              const ca_inputs_kv_t *headers, size_t header_count,
                              ca_scraped_page_t *out) {
    (void)headers; (void)header_count;
    if (out) memset(out, 0, sizeof(*out));
    if (!m || cab_is_ws(url) || !out) return false;
    return m->inner.fetch(m->inner.user, url, out);
}
const char *ca_inputs_null_mcp_web_scrape_backend_id(void) { return "null"; }

/* ── RegistryVideoIngest ────────────────────────────────────────────────── */

typedef struct {
    char                     *key;    /* owned */
    ca_video_ingest_result_t  result; /* owned */
} video_entry_t;

struct ca_video_ingest {
    video_entry_t *items;
    size_t         count, cap;
};

ca_video_ingest_t *ca_video_ingest_create(void) {
    return (ca_video_ingest_t *)calloc(1, sizeof(ca_video_ingest_t));
}
void ca_video_ingest_destroy(ca_video_ingest_t *v) {
    if (!v) return;
    for (size_t i = 0; i < v->count; ++i) {
        free(v->items[i].key);
        ca_video_ingest_result_free(&v->items[i].result);
    }
    free(v->items);
    free(v);
}
const char *ca_video_ingest_backend_id(const ca_video_ingest_t *v) {
    (void)v; return "registry";
}
int ca_video_ingest_register(ca_video_ingest_t *v, const char *file_path,
                             const ca_video_ingest_result_t *result) {
    if (!v || cab_is_ws(file_path) || !result) return -1;
    for (size_t i = 0; i < v->count; ++i) {
        if (cab_ord_eq(v->items[i].key, file_path)) {
            ca_video_ingest_result_t copy;
            if (!video_copy(&copy, result)) return -1;
            ca_video_ingest_result_free(&v->items[i].result);
            v->items[i].result = copy;
            return 0;
        }
    }
    ca_video_ingest_result_t copy;
    if (!video_copy(&copy, result)) return -1;
    char *k = cab_strdup_empty(file_path);
    if (!k) { ca_video_ingest_result_free(&copy); return -1; }
    if (v->count == v->cap) {
        size_t nc = v->cap ? v->cap * 2 : 4;
        void *n = realloc(v->items, nc * sizeof(video_entry_t));
        if (!n) { ca_video_ingest_result_free(&copy); free(k); return -1; }
        v->items = (video_entry_t *)n;
        v->cap = nc;
    }
    v->items[v->count].key = k;
    v->items[v->count].result = copy;
    v->count++;
    return 0;
}
bool ca_video_ingest_ingest(const ca_video_ingest_t *v, const char *file_path,
                            ca_video_ingest_result_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!v || cab_is_ws(file_path) || !out) return false;
    for (size_t i = 0; i < v->count; ++i)
        if (cab_ord_eq(v->items[i].key, file_path))
            return video_copy(out, &v->items[i].result);
    /* empty result when absent */
    out->transcript = cab_strdup_empty("");
    if (!out->transcript) return false;
    out->shots = NULL; out->shot_count = 0;
    out->duration_ms = 0; out->frame_count = 0;
    return true;
}
const char *ca_inputs_null_video_ingest_backend_id(void) { return "null"; }

/* ── AsciinemaTerminalCast ──────────────────────────────────────────────── */

/* Extract an integer value for `key` from a header line like {"width":80,...}. */
static bool header_int(const char *line, const char *key, int *out) {
    /* find "key" */
    size_t klen = strlen(key);
    const char *p = line;
    while ((p = strchr(p, '"')) != NULL) {
        p++;
        if (strncmp(p, key, klen) == 0 && p[klen] == '"') {
            p += klen + 1;
            while (*p == ' ' || *p == ':' ) p++;
            if (*p == '-' || (*p >= '0' && *p <= '9')) {
                *out = (int)strtol(p, NULL, 10);
                return true;
            }
            return false;
        }
        /* skip to closing quote of this string to avoid nested matches */
        const char *q = strchr(p, '"');
        if (!q) break;
        p = q + 1;
    }
    return false;
}

/* Parse one event line "[t, \"o\", \"data\"]". On a match with type "o",
 * appends a segment. Returns false only on OOM. */
static bool parse_event_line(const char *line, ca_terminal_cast_t *cast) {
    const char *p = line;
    while (*p == ' ' || *p == '\t') p++;
    if (*p != '[') return true; /* not an event array; skip */
    p++;
    /* time (double seconds) */
    char *endp = NULL;
    double t = strtod(p, &endp);
    if (endp == p) return true;
    p = endp;
    while (*p == ' ' || *p == ',') p++;
    /* type string */
    if (*p != '"') return true;
    p++;
    char type_c = *p;           /* first char of type; "o" expected */
    /* advance to closing quote */
    while (*p && *p != '"') { if (*p == '\\' && p[1]) p++; p++; }
    if (*p != '"') return true;
    p++;
    while (*p == ' ' || *p == ',') p++;
    /* data string */
    if (*p != '"') return true;
    p++;
    /* decode the data string (handle \" \\ \n \r \t) */
    size_t cap = 16, len = 0;
    char *data = (char *)malloc(cap);
    if (!data) return false;
    data[0] = '\0';
    while (*p && *p != '"') {
        char c = *p;
        char dec;
        if (c == '\\' && p[1]) {
            p++;
            switch (*p) {
                case 'n': dec = '\n'; break;
                case 'r': dec = '\r'; break;
                case 't': dec = '\t'; break;
                case '"': dec = '"';  break;
                case '\\': dec = '\\'; break;
                case '/': dec = '/'; break;
                default: dec = *p; break;
            }
        } else {
            dec = c;
        }
        if (len + 2 > cap) {
            size_t nc = cap * 2;
            char *nb = (char *)realloc(data, nc);
            if (!nb) { free(data); return false; }
            data = nb; cap = nc;
        }
        data[len++] = dec;
        data[len] = '\0';
        p++;
    }

    if (type_c != 'o') { free(data); return true; } /* only output events */

    ca_terminal_cast_segment_t *ns = (ca_terminal_cast_segment_t *)realloc(
        cast->segments, (cast->segment_count + 1) * sizeof(*ns));
    if (!ns) { free(data); return false; }
    cast->segments = ns;
    cast->segments[cast->segment_count].offset_ms = (int64_t)(t * 1000.0);
    cast->segments[cast->segment_count].text = data;
    cast->segment_count++;
    return true;
}

bool ca_terminal_cast_parse(const char *cast_text, ca_terminal_cast_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out || cab_is_ws(cast_text)) return false;
    out->width = 80;
    out->height = 24;

    const char *p = cast_text;
    bool first_line = true;
    while (*p) {
        const char *nl = strchr(p, '\n');
        size_t linelen = nl ? (size_t)(nl - p) : strlen(p);
        char *line = (char *)malloc(linelen + 1);
        if (!line) { ca_terminal_cast_free(out); return false; }
        memcpy(line, p, linelen);
        line[linelen] = '\0';
        /* strip trailing CR */
        if (linelen > 0 && line[linelen - 1] == '\r') line[linelen - 1] = '\0';

        bool blank = cab_is_ws(line);
        if (first_line) {
            if (!blank) {
                int w, h;
                if (header_int(line, "width", &w))  out->width = w;
                if (header_int(line, "height", &h)) out->height = h;
            }
            first_line = false;
        } else if (!blank) {
            if (!parse_event_line(line, out)) { free(line); ca_terminal_cast_free(out); return false; }
        }
        free(line);
        if (!nl) break;
        p = nl + 1;
    }
    return true;
}

char *ca_terminal_cast_render_transcript(const ca_terminal_cast_t *cast) {
    if (!cast) return NULL;
    size_t total = 0;
    for (size_t i = 0; i < cast->segment_count; ++i)
        total += strlen(cast->segments[i].text);
    char *out = (char *)malloc(total + 1);
    if (!out) return NULL;
    size_t k = 0;
    for (size_t i = 0; i < cast->segment_count; ++i) {
        size_t n = strlen(cast->segments[i].text);
        memcpy(out + k, cast->segments[i].text, n);
        k += n;
    }
    out[k] = '\0';
    return out;
}
const char *ca_terminal_cast_backend_id(void) { return "asciinema"; }
const char *ca_inputs_null_terminal_cast_backend_id(void) { return "null"; }
