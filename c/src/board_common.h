/*
 * board_common.h — shared static-inline helpers for the domain-board ports
 * (healthcare, banking, legal, education, commerce + sub-modules, personal.*).
 * NOT part of the public umbrella header — internal to the board .c files.
 *
 * Mirrors the tiny helper set each board needs: strdup (owning, empty-coalescing),
 * string.IsNullOrWhiteSpace, Ordinal / OrdinalIgnoreCase compares + CI substring,
 * and the C# `decimal` money surrogate (int64 scaled by 1e6). Pure C11 + libc.
 */

#ifndef CIRCLE_AI_BOARD_COMMON_H
#define CIRCLE_AI_BOARD_COMMON_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* C# `decimal` surrogate for money fields: a signed count of 1e-6 units so exact
 * small values round-trip and comparisons stay deterministic. */
typedef int64_t ca_decimal_t;
#define CA_DECIMAL_SCALE 1000000LL

/* strdup that returns NULL only on OOM (NULL input yields NULL). */
static inline char *cab_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* strdup coalescing NULL -> "" (mirrors non-null C# string fields). NULL on OOM. */
static inline char *cab_strdup_empty(const char *s) {
    return cab_strdup(s ? s : "");
}

/* string.IsNullOrWhiteSpace. */
static inline bool cab_is_ws(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (!isspace((unsigned char)*p)) return false;
    return true;
}

/* StringComparer.Ordinal equality (byte compare). */
static inline bool cab_ord_eq(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    return strcmp(a, b) == 0;
}

/* OrdinalIgnoreCase full-string comparison (ASCII case-fold). */
static inline int cab_ci_cmp(const char *a, const char *b) {
    const unsigned char *x = (const unsigned char *)a;
    const unsigned char *y = (const unsigned char *)b;
    for (;; ++x, ++y) {
        int ca = tolower(*x), cb = tolower(*y);
        if (ca != cb) return ca - cb;
        if (ca == 0) return 0;
    }
}

/* OrdinalIgnoreCase equality. */
static inline bool cab_ci_eq(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    return cab_ci_cmp(a, b) == 0;
}

/* Does `s` start with `prefix` (ASCII case-insensitive)? */
static inline bool cab_ci_cmp_prefix(const char *s, const char *prefix) {
    if (!s || !prefix) return false;
    for (; *prefix; ++s, ++prefix) {
        if (tolower((unsigned char)*s) != tolower((unsigned char)*prefix)) return false;
    }
    return true;
}

/* OrdinalIgnoreCase substring test: does needle occur in hay (ASCII CI)? An empty
 * needle matches (string.Contains("") is always true in C#). */
static inline bool cab_ci_contains(const char *hay, const char *needle) {
    if (!hay || !needle) return false;
    if (*needle == '\0') return true;
    size_t nl = strlen(needle);
    for (const char *h = hay; *h; ++h) {
        size_t k = 0;
        while (k < nl && h[k] &&
               tolower((unsigned char)h[k]) == tolower((unsigned char)needle[k]))
            k++;
        if (k == nl) return true;
    }
    return false;
}

/* Free an owned string array (each element + the block). */
static inline void cab_strv_free(char **v, size_t n) {
    if (!v) return;
    for (size_t i = 0; i < n; ++i) free(v[i]);
    free(v);
}

/* Deep-copy an owned string array (each element empty-coalesced). *out is set to
 * a fresh block (NULL when n==0). Returns false on OOM (leaving *out NULL). */
static inline bool cab_strv_copy(char ***out, char *const *src, size_t n) {
    *out = NULL;
    if (n == 0) return true;
    char **v = (char **)calloc(n, sizeof(char *));
    if (!v) return false;
    for (size_t i = 0; i < n; ++i) {
        v[i] = cab_strdup_empty(src ? src[i] : NULL);
        if (!v[i]) { cab_strv_free(v, i); return false; }
    }
    *out = v;
    return true;
}

/* Does an owned string array contain `tag` (OrdinalIgnoreCase)? Mirrors
 * Tags.Any(t => string.Equals(t, tag, OrdinalIgnoreCase)). */
static inline bool cab_strv_ci_contains(char *const *v, size_t n, const char *tag) {
    if (!v || !tag) return false;
    for (size_t i = 0; i < n; ++i)
        if (cab_ci_eq(v[i], tag)) return true;
    return false;
}

/* ── DateTime helpers (C# DateTime.Date truncation over Unix-ms) ─────────────
 * The boards carry DateTimeOffset/DateTime as int64 Unix ms UTC. C#'s `.Date`
 * floors to midnight; `now.DayOfWeek` and week-start follow from the day index. */

#define CAB_MS_PER_DAY 86400000LL

/* Floor-divide ms -> UTC day index (correct for negative/pre-epoch too). */
static inline int64_t cab_utc_day(int64_t ms) {
    int64_t d = ms / CAB_MS_PER_DAY;
    if (ms % CAB_MS_PER_DAY != 0 && ms < 0) d -= 1;
    return d;
}

/* Midnight (Unix ms) of the UTC calendar day containing `ms` (C# .Date). */
static inline int64_t cab_day_start_ms(int64_t ms) {
    return cab_utc_day(ms) * CAB_MS_PER_DAY;
}

/* C# DayOfWeek (Sunday=0 .. Saturday=6) for a UTC day index. 1970-01-01 (day 0)
 * was a Thursday == 4. */
static inline int cab_day_of_week(int64_t day_index) {
    int r = (int)(((day_index % 7) + 4) % 7);
    if (r < 0) r += 7;
    return r;
}

/* Midnight (Unix ms) of the Sunday that begins `ms`'s week: mirrors C#
 * now.Date.AddDays(-(int)now.DayOfWeek). */
static inline int64_t cab_week_start_ms(int64_t ms) {
    int64_t d = cab_utc_day(ms);
    return (d - cab_day_of_week(d)) * CAB_MS_PER_DAY;
}

/* Civil (year, month 1-12, day 1-31) from a UTC day index (days since epoch),
 * via Howard Hinnant's days_from_civil inverse. Used for C# DateTime.Month/.Day. */
static inline void cab_civil_from_day(int64_t z, int *year, int *month, int *day) {
    z += 719468; /* shift epoch to 0000-03-01 */
    int64_t era = (z >= 0 ? z : z - 146096) / 146097;
    unsigned doe = (unsigned)(z - era * 146097);            /* [0, 146096] */
    unsigned yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365; /* [0,399] */
    int64_t y = (int64_t)yoe + era * 400;
    unsigned doy = doe - (365 * yoe + yoe / 4 - yoe / 100);  /* [0, 365] */
    unsigned mp = (5 * doy + 2) / 153;                       /* [0, 11] */
    unsigned d = doy - (153 * mp + 2) / 5 + 1;               /* [1, 31] */
    unsigned m = mp < 10 ? mp + 3 : mp - 9;                  /* [1, 12] */
    if (year)  *year  = (int)(y + (m <= 2));
    if (month) *month = (int)m;
    if (day)   *day   = (int)d;
}

/* C# DateTime.Month (1-12) for a Unix-ms UTC instant. */
static inline int cab_utc_month(int64_t ms) {
    int month = 1;
    cab_civil_from_day(cab_utc_day(ms), NULL, &month, NULL);
    return month;
}

/* C# DateTime.Day (1-31) for a Unix-ms UTC instant. */
static inline int cab_utc_day_of_month(int64_t ms) {
    int day = 1;
    cab_civil_from_day(cab_utc_day(ms), NULL, NULL, &day);
    return day;
}

#endif /* CIRCLE_AI_BOARD_COMMON_H */
