#ifndef CIRCLE_AI_INPUTS_H
#define CIRCLE_AI_INPUTS_H

/*
 * inputs.h — CircleAI.Inputs (C11 port of Contracts.cs + InMemoryInputs.cs +
 * NullImplementations.cs). Input adapters normalise a payload (URL / file /
 * video / terminal cast) into a model-ready text stream. The real network / ffmpeg
 * boundaries are injected; the in-memory adapters here are deterministic.
 *
 *   Records : ScrapedPage(Url, Text, Title?, Metadata?, ResolvedLinks?);
 *             VideoIngestResult(Transcript, Shots[], Duration, FrameCount);
 *             McpScrapeJob(Url, Headers?);
 *             TerminalCastSegment(Offset, Text);
 *             TerminalCast(Segments[], Width, Height).
 *   Scraper : IWebScraper -> RegistryWebScraper. Register(url, page); Fetch(url)
 *               returns a copy of the registered page, or an empty page (Text "")
 *               for an unknown URL (url required). BackendId "registry".
 *   Stealth : IStealthHttpClient -> RegistryStealthClient. Same registry as the
 *               scraper; Get(url, headers?) rotates a header set (tracked but
 *               deterministic). BackendId "stealth-registry".
 *   Mcp     : IMcpWebScrape -> DefaultMcpWebScrape. Wraps an IWebScraper vtable;
 *               Scrape(job) delegates Fetch(job.Url). BackendId "mcp:<inner>".
 *   Video   : IVideoIngest -> RegistryVideoIngest. Register(path, result);
 *               Ingest(path) returns the registered result, or empty when absent.
 *               BackendId "registry".
 *   Cast    : ITerminalCast -> AsciinemaTerminalCast. Parse(castText) reads the
 *               asciinema v2 header (width/height) + "[t,\"o\",data]" output
 *               events; RenderTranscript(cast) concatenates segment text.
 *               BackendId "asciinema".
 *   Null variants return empty pages / results / casts.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Offset /
 * Duration as int64 ms. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* key/value pair for metadata / headers. */
typedef struct {
    char *key;   /* owned */
    char *value; /* owned */
} ca_inputs_kv_t;

/* ScrapedPage(Url, Text, Title?, Metadata?, ResolvedLinks?). */
typedef struct {
    char           *url;            /* owned, non-null */
    char           *text;           /* owned, non-null */
    char           *title;          /* owned, or NULL */
    ca_inputs_kv_t *metadata;       /* owned; NULL when metadata_count == 0 */
    size_t          metadata_count;
    char          **resolved_links; /* owned; NULL when link_count == 0 */
    size_t          link_count;
} ca_scraped_page_t;

void ca_scraped_page_free(ca_scraped_page_t *p);

/* VideoIngestResult(Transcript, Shots[], Duration, FrameCount). */
typedef struct {
    char   *transcript;  /* owned, non-null */
    char  **shots;       /* owned; NULL when shot_count == 0 */
    size_t  shot_count;
    int64_t duration_ms;
    int     frame_count;
} ca_video_ingest_result_t;

void ca_video_ingest_result_free(ca_video_ingest_result_t *r);

/* TerminalCastSegment(Offset, Text). */
typedef struct {
    int64_t offset_ms;
    char   *text; /* owned, non-null */
} ca_terminal_cast_segment_t;

/* TerminalCast(Segments[], Width, Height). */
typedef struct {
    ca_terminal_cast_segment_t *segments; /* owned; NULL when segment_count == 0 */
    size_t                      segment_count;
    int                         width;
    int                         height;
} ca_terminal_cast_t;

void ca_terminal_cast_free(ca_terminal_cast_t *c);

/* ── IWebScraper -> RegistryWebScraper ──────────────────────────────────── */

typedef struct ca_web_scraper ca_web_scraper_t;

ca_web_scraper_t *ca_web_scraper_create(void); /* NULL on OOM */
void ca_web_scraper_destroy(ca_web_scraper_t *s);
const char *ca_web_scraper_backend_id(const ca_web_scraper_t *s); /* "registry" */

/* Register(url, page) — keyed by Url (replace). 0 / -1 on bad args / OOM. */
int ca_web_scraper_register(ca_web_scraper_t *s, const char *url,
                            const ca_scraped_page_t *page);
/* Fetch(url) -> fresh page into *out, true; a registered copy or an empty page
 * (Text "") for an unknown URL. false on bad args (url required). */
bool ca_web_scraper_fetch(const ca_web_scraper_t *s, const char *url,
                          ca_scraped_page_t *out);

const char *ca_inputs_null_web_scraper_backend_id(void); /* "null" */

/* ── IStealthHttpClient -> RegistryStealthClient ────────────────────────── */

typedef struct ca_stealth_client ca_stealth_client_t;

ca_stealth_client_t *ca_stealth_client_create(void); /* NULL on OOM */
void ca_stealth_client_destroy(ca_stealth_client_t *s);
const char *ca_stealth_client_backend_id(const ca_stealth_client_t *s);

/* Register(url, page) — keyed by Url (replace). 0 / -1. */
int ca_stealth_client_register(ca_stealth_client_t *s, const char *url,
                               const ca_scraped_page_t *page);
/* Get(url, headers?, header_count) -> fresh page into *out, true; registered copy
 * or empty page for an unknown URL. false on bad args (url required). */
bool ca_stealth_client_get(ca_stealth_client_t *s, const char *url,
                           const ca_inputs_kv_t *headers, size_t header_count,
                           ca_scraped_page_t *out);

const char *ca_inputs_null_stealth_client_backend_id(void); /* "null" */

/* ── IMcpWebScrape -> DefaultMcpWebScrape ───────────────────────────────── */

/* Injected scraper seam: fetch a URL into *out. Returns true on success. */
typedef struct {
    bool (*fetch)(void *user, const char *url, ca_scraped_page_t *out);
    const char *(*backend_id)(void *user);
    void *user;
} ca_web_scraper_vtable_t;

typedef struct ca_mcp_web_scrape ca_mcp_web_scrape_t;

/* Create over an injected scraper (borrowed; must outlive the wrapper). NULL on
 * a NULL vtable / OOM. */
ca_mcp_web_scrape_t *ca_mcp_web_scrape_create(const ca_web_scraper_vtable_t *inner);
void ca_mcp_web_scrape_destroy(ca_mcp_web_scrape_t *m);
/* BackendId "mcp:<inner backend id>" — borrowed, valid until destroy. */
const char *ca_mcp_web_scrape_backend_id(const ca_mcp_web_scrape_t *m);

/* Scrape(url, headers?, header_count) -> delegates the inner scraper's Fetch.
 * Headers are accepted for signature parity (unused by the default wrapper). */
bool ca_mcp_web_scrape_scrape(const ca_mcp_web_scrape_t *m, const char *url,
                              const ca_inputs_kv_t *headers, size_t header_count,
                              ca_scraped_page_t *out);

const char *ca_inputs_null_mcp_web_scrape_backend_id(void); /* "null" */

/* ── IVideoIngest -> RegistryVideoIngest ────────────────────────────────── */

typedef struct ca_video_ingest ca_video_ingest_t;

ca_video_ingest_t *ca_video_ingest_create(void); /* NULL on OOM */
void ca_video_ingest_destroy(ca_video_ingest_t *v);
const char *ca_video_ingest_backend_id(const ca_video_ingest_t *v); /* "registry" */

/* Register(path, result) — keyed by path (replace). 0 / -1. */
int ca_video_ingest_register(ca_video_ingest_t *v, const char *file_path,
                             const ca_video_ingest_result_t *result);
/* Ingest(path) -> fresh result into *out, true; a registered copy or an empty
 * result when absent. false on bad args (path required). */
bool ca_video_ingest_ingest(const ca_video_ingest_t *v, const char *file_path,
                            ca_video_ingest_result_t *out);

const char *ca_inputs_null_video_ingest_backend_id(void); /* "null" */

/* ── ITerminalCast -> AsciinemaTerminalCast ─────────────────────────────── */

/* Parse asciinema v2 text (header line with width/height, then output events
 * "[t,\"o\",data]") into *out, true; false on bad args / empty text. Defaults
 * width 80, height 24. BackendId "asciinema". */
bool ca_terminal_cast_parse(const char *cast_text, ca_terminal_cast_t *out);
/* RenderTranscript(cast) -> fresh concatenation of every segment's text. NULL on
 * OOM / NULL cast. */
char *ca_terminal_cast_render_transcript(const ca_terminal_cast_t *cast);
const char *ca_terminal_cast_backend_id(void); /* "asciinema" */

const char *ca_inputs_null_terminal_cast_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_INPUTS_H */
