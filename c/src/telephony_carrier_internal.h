/*
 * telephony_carrier_internal.h — shared internals for the carrier bindings
 * (Twilio / Telnyx / Plivo). NOT part of the public umbrella header.
 *
 * Provides:
 *   - a compact read-only JSON value model + parser (enough to walk the carrier
 *     REST responses: object property lookup, array indexing, string/number
 *     extraction). No allocation of the source; nodes borrow into a parsed arena.
 *   - carrier decimal parsing (ParseDecimal / cost fields) into ca_tel_decimal_t.
 *   - a helper to build an ICallSession over a fresh PendingMediaStream.
 *
 * Pure C11 + libc. Used only by the three binding .c files.
 */

#ifndef CIRCLE_AI_TELEPHONY_CARRIER_INTERNAL_H
#define CIRCLE_AI_TELEPHONY_CARRIER_INTERNAL_H

#include "circle_ai/telephony.h"

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* ── minimal JSON reader ────────────────────────────────────────────────── */

typedef enum {
    CATJ_NULL, CATJ_BOOL, CATJ_NUMBER, CATJ_STRING, CATJ_ARRAY, CATJ_OBJECT
} catj_type_t;

typedef struct catj_node catj_node_t;

/* One parsed document (owns the node arena + decoded strings). */
typedef struct catj_doc catj_doc_t;

/* Parse `json` (NUL-terminated). Returns NULL on malformed input / OOM. */
catj_doc_t *catj_parse(const char *json);
void        catj_free(catj_doc_t *doc);
/* Root value (or NULL for an empty/failed doc). */
const catj_node_t *catj_root(const catj_doc_t *doc);

catj_type_t catj_type(const catj_node_t *n);

/* Object property by name (NULL if absent / not an object). */
const catj_node_t *catj_get(const catj_node_t *n, const char *key);
/* Array length (0 if not an array). */
size_t catj_array_len(const catj_node_t *n);
/* Array element by index (NULL if OOB / not an array). */
const catj_node_t *catj_at(const catj_node_t *n, size_t i);

/* Decoded string value (borrowed; NULL if the node is not a string). */
const char *catj_string(const catj_node_t *n);
/* Raw numeric text (borrowed; NULL if not a number). */
const char *catj_number_text(const catj_node_t *n);

/* ── carrier decimal ────────────────────────────────────────────────────── */

/* Parse a JSON node that is a number OR a numeric string into a decimal.
 * Returns true + *out on success; false when the node is absent/non-numeric. */
bool ca_tel_carrier_parse_decimal(const catj_node_t *n, ca_tel_decimal_t *out);

/* Convert a decimal literal string ("1.50") to ca_tel_decimal_t. false on parse
 * failure. */
bool ca_tel_carrier_decimal_from_str(const char *s, ca_tel_decimal_t *out);

/* ── session assembly ───────────────────────────────────────────────────── */

/* Build a MediaCallSession over a fresh PendingMediaStream carrying `info`,
 * bound to `carrier`. Ownership of the pending media transfers to the session.
 * NULL on OOM. `info` is copied. */
ca_tel_call_session_t *ca_tel_carrier_make_pending_session(
    const ca_tel_call_info_t *info, ca_tel_carrier_t *carrier);

#endif /* CIRCLE_AI_TELEPHONY_CARRIER_INTERNAL_H */
