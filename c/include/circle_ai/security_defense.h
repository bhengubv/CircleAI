#ifndef CIRCLE_AI_SECURITY_DEFENSE_H
#define CIRCLE_AI_SECURITY_DEFENSE_H

/*
 * security_defense.h - CircleAI.Security.Defense (C11).
 *
 * Watching for known-bad indicators and reporting what is seen.
 *
 * OBSERVATION IS NOT ENFORCEMENT, and the split is the design. A monitor
 * reports; a sink decides what to do about it. Collapsing the two would put the
 * component that can read your network traffic in charge of blocking it, and
 * the blast radius of a false positive goes from a notification to a device
 * that will not reach its owner's bank.
 *
 * THE BLOCKLIST IS LOCAL AND SO IS THE MATCHING. A device does not ask a remote
 * service "is this address bad", because that question tells the service every
 * address the device visits — which is the surveillance the blocklist is
 * supposed to protect against.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false / SIZE_MAX. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── indicators ───────────────────────────────────────────────────────────── */

typedef enum {
    CA_INDICATOR_UNKNOWN = 0,
    CA_INDICATOR_DOMAIN,
    CA_INDICATOR_IPV4,
    CA_INDICATOR_IPV4_CIDR,
    CA_INDICATOR_URL,
    CA_INDICATOR_SHA256
} ca_indicator_kind_t;

typedef struct {
    ca_indicator_kind_t kind;
    char *value;
    /* Which list it came from. Kept because "this was flagged" is not
     * actionable without "by whom" — one list's false positive is another
     * list's deliberate policy. */
    char *source;
} ca_parsed_indicator_t;

void ca_parsed_indicator_free(ca_parsed_indicator_t *indicator);

/* ── parsing ──────────────────────────────────────────────────────────────── */

/*
 * Parses one blocklist line.
 *
 * Handles the formats these lists actually ship in: bare values, hosts-file
 * lines (`0.0.0.0 bad.example`), and comments after `#`. Returns false for a
 * blank or comment line, which is not an error — a real list is a third
 * comments.
 */
bool ca_blocklist_parse_line(const char *line, const char *source,
                             ca_parsed_indicator_t *out_indicator);

/* Parses a whole list. Returns a heap array of `*out_count`, or NULL. */
ca_parsed_indicator_t *ca_blocklist_parse(const char *text, const char *source,
                                          size_t *out_count);

/* Classifies a value on its own. Exposed because a caller that already has a
 * value still needs to know what KIND it is before it can match it. */
ca_indicator_kind_t ca_blocklist_classify(const char *value);

/* ── CIDR ─────────────────────────────────────────────────────────────────── */

typedef struct {
    uint32_t network;   /* host byte order, already masked */
    uint8_t prefix;     /* 0-32 */
} ca_ipv4_cidr_t;

/*
 * Parses "10.0.0.0/8". A bare address is /32.
 *
 * The network is MASKED at parse time, so "10.1.2.3/8" and "10.0.0.0/8" compare
 * equal — a list that wrote the former should not fail to match the latter.
 */
bool ca_ipv4_cidr_parse(const char *text, ca_ipv4_cidr_t *out_cidr);

bool ca_ipv4_parse(const char *text, uint32_t *out_address);

bool ca_ipv4_cidr_contains(ca_ipv4_cidr_t cidr, uint32_t address);

/* ── the source seam ──────────────────────────────────────────────────────── */

typedef struct {
    ca_parsed_indicator_t indicator;
    /* What was being checked when it matched. */
    char *observed;
} ca_indicator_match_t;

void ca_indicator_match_free(ca_indicator_match_t *match);

/*
 * Where indicators come from. A struct of function pointers, because a host
 * may have a file, a bundled list, or a feed it refreshes.
 */
typedef struct ca_indicator_source {
    void *state;
    const char *(*name)(void *state);
    /* Fills `out_match` and returns true when `value` is listed. */
    bool (*lookup)(void *state, const char *value, ca_indicator_match_t *out_match);
    size_t (*count)(void *state);
    void (*free_fn)(void *state);
} ca_indicator_source_t;

void ca_indicator_source_free(ca_indicator_source_t *source);

/* A source over a parsed list. Takes ownership of `indicators`. */
ca_indicator_source_t *ca_blocklist_indicator_source_new(ca_parsed_indicator_t *indicators,
                                                         size_t count,
                                                         const char *name);

/* ── sinks ────────────────────────────────────────────────────────────────── */

/* Where a match goes. */
typedef struct ca_threat_sink {
    void *state;
    void (*report)(void *state, const ca_indicator_match_t *match);
    void (*free_fn)(void *state);
} ca_threat_sink_t;

void ca_threat_sink_free(ca_threat_sink_t *sink);

/* Swallows everything. For a build with no reporting wired: the monitor still
 * runs, so a wiring mistake does not silently disable the matching too. */
ca_threat_sink_t *ca_null_threat_sink_new(void);

/* Calls a function. */
ca_threat_sink_t *ca_delegate_threat_sink_new(void (*fn)(void *, const ca_indicator_match_t *),
                                              void *user_state);

/*
 * Fans out to several.
 *
 * One sink throwing must not stop the others: a logger that fails should not
 * also prevent the user being told. Takes ownership of the array and of every
 * sink in it.
 */
ca_threat_sink_t *ca_composite_threat_sink_new(ca_threat_sink_t **sinks, size_t count);

/*
 * Carries matches to the watchdog, deduplicated within a window.
 *
 * The same condition seen every thirty seconds is one problem, not a hundred. A
 * person who gets a hundred notifications turns notifications off, and then
 * gets none of the ones that mattered.
 */
ca_threat_sink_t *ca_watchdog_threat_sink_new(ca_threat_sink_t *inner,
                                              int64_t window_seconds);

/* ── monitors ─────────────────────────────────────────────────────────────── */

typedef struct ca_threat_monitor {
    void *state;
    const char *(*name)(void *state);
    /* Checks one value and reports to the sink if it matches. Returns whether
     * it matched, so a caller can act without re-reading the sink. */
    bool (*check)(void *state, const char *value);
    void (*free_fn)(void *state);
} ca_threat_monitor_t;

void ca_threat_monitor_free(ca_threat_monitor_t *monitor);

/* A monitor over an indicator source. Borrows both; frees neither. */
ca_threat_monitor_t *ca_blocklist_threat_monitor_new(ca_indicator_source_t *source,
                                                     ca_threat_sink_t *sink);

/* ── options and module ───────────────────────────────────────────────────── */

typedef struct {
    /* OFF by default. A defence that turns itself on is a defence nobody chose,
     * and on a metered connection a list refresh is somebody's money. */
    bool enabled;
    /* How long a repeat of the same match is suppressed for. */
    int64_t dedupe_window_seconds;
    /* Refresh interval for a source that has one. 0 means never. */
    int64_t refresh_seconds;
    char *blocklist_path;
} ca_defense_options_t;

ca_defense_options_t ca_defense_options_default(void);
void ca_defense_options_free(ca_defense_options_t *options);

typedef struct ca_defense_module ca_defense_module_t;

ca_defense_module_t *ca_defense_module_new(const ca_defense_options_t *options);
void ca_defense_module_free(ca_defense_module_t *module);

bool ca_defense_module_add_monitor(ca_defense_module_t *module,
                                   ca_threat_monitor_t *monitor);

/* Runs every monitor over one value. Returns how many matched. */
size_t ca_defense_module_check(ca_defense_module_t *module, const char *value);

/* ── the sentinel ─────────────────────────────────────────────────────────── */

/*
 * The always-on half.
 *
 * AUTONOMIC MEANS IT RUNS WITHOUT BEING ASKED, which is exactly why it is
 * separately switchable and reports what it did. A component that acts on its
 * own and cannot be inspected is indistinguishable from malware.
 */
typedef struct ca_autonomic_defense {
    void *state;
    bool (*is_running)(void *state);
    bool (*start)(void *state);
    void (*stop)(void *state);
    /* How many checks it has run since it started. The number a person looks at
     * to decide whether it is actually working. */
    size_t (*checks_run)(void *state);
    void (*free_fn)(void *state);
} ca_autonomic_defense_t;

void ca_autonomic_defense_free(ca_autonomic_defense_t *defense);

ca_autonomic_defense_t *ca_always_on_defense_sentinel_new(ca_defense_module_t *module);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SECURITY_DEFENSE_H */
