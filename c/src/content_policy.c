/*
 * content_policy.c — CircleAI.ContentPolicy safety guardrails (C11 port).
 *
 * Ports Contracts.cs, KeywordContentFilter.cs and NullImplementations.cs.
 *
 * The C# filters/detectors use compiled System.Text.RegularExpressions.Regex.
 * C has no standard regex, so every shipped pattern is reproduced with a
 * hand-written matcher whose behaviour is identical to the corresponding .NET
 * regex for the boolean/first-match question the filter asks:
 *
 *   self-harm       \b(kill myself|suicide|self\s*-?\s*harm)\b   (Refuse 0.95)
 *   explicit-sexual \b(porn|sexual content|nsfw)\b               (Flag   0.7)
 *   violence        \b(how to make a bomb|chemical weapon|murder)\b (Refuse 0.9)
 *   hate            \b(racial slur|hate speech)\b                (Refuse 0.9)
 *   pii-card        \b(?:\d[ -]*?){13,19}\b                       (Flag   0.8)
 *
 * Injection patterns (substring match, capture the matched span for the reason):
 *   ignore (all|the|any) (previous|prior) instructions
 *   forget (everything|all) (above|prior)
 *   you (are now|will be|are no longer)
 *   system prompt[:\s]
 *   reveal (your|the) (instructions|system prompt|hidden context)
 *   <\|im_(start|end)\|>
 *   (BEGIN|END)\s+(SYSTEM|DEVELOPER|ASSISTANT)\s+MESSAGE
 *
 * \b is the .NET word boundary between [A-Za-z0-9_] and a non-word char / edge;
 * \s is ASCII whitespace. Matching is case-insensitive as in the C#.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/content_policy.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>

static char *cp_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static bool cp_is_word(int ch) {
    return isalnum((unsigned char)ch) || ch == '_';
}
static bool cp_is_ws(int ch) {
    /* .NET \s over ASCII: space, tab, LF, VT, FF, CR. */
    return ch == ' ' || ch == '\t' || ch == '\n' || ch == '\v' || ch == '\f' || ch == '\r';
}
static char cp_lc(char c) { return (char)tolower((unsigned char)c); }

/* Case-insensitive compare of `lit` (already lowercase) against text[pos..].
 * Returns the literal length on match, or 0 on mismatch / overrun. */
static size_t cp_lit_at(const char *text, size_t pos, size_t tlen, const char *lit) {
    size_t l = strlen(lit);
    if (pos + l > tlen) return 0;
    for (size_t k = 0; k < l; ++k)
        if (cp_lc(text[pos + k]) != lit[k]) return 0;
    return l;
}

/* Left word boundary before index i: i==0 or text[i-1] is a non-word char. */
static bool cp_lb(const char *text, size_t i) {
    return i == 0 || !cp_is_word((unsigned char)text[i - 1]);
}
/* Right word boundary after index end (0-based, exclusive): end==tlen or
 * text[end] is a non-word char. */
static bool cp_rb(const char *text, size_t end, size_t tlen) {
    return end == tlen || !cp_is_word((unsigned char)text[end]);
}

/* ── SafetyFinding ──────────────────────────────────────────────────────── */

void ca_safety_finding_free(ca_safety_finding_t *f) {
    if (!f) return;
    free(f->category);
    free(f->reason);
    f->category = f->reason = NULL;
}
void ca_safety_finding_free_array(ca_safety_finding_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_safety_finding_free(&arr[i]);
    free(arr);
}
ca_safety_finding_t *ca_safety_finding_copy(ca_safety_finding_t *dst,
                                            const ca_safety_finding_t *src) {
    if (!dst || !src) return dst;
    dst->verdict    = src->verdict;
    dst->category   = cp_strdup(src->category);
    dst->reason     = cp_strdup(src->reason);
    dst->confidence = src->confidence;
    return dst;
}
static void cp_set_finding(ca_safety_finding_t *out, ca_safety_verdict_t v,
                           const char *category, const char *reason, float conf) {
    out->verdict    = v;
    out->category   = cp_strdup(category);
    out->reason     = cp_strdup(reason);
    out->confidence = conf;
}

/* ── keyword rule matchers ──────────────────────────────────────────────── */

/* Match \b(alt0|alt1|...)\b where each alternative is a plain literal (may
 * contain spaces, which are literal). Returns true iff any alternative matches
 * at some position bounded by word boundaries. */
static bool cp_match_word_alts(const char *text, const char *const *alts, size_t n_alts) {
    size_t tlen = strlen(text);
    for (size_t i = 0; i < tlen; ++i) {
        if (!cp_lb(text, i)) continue;
        for (size_t a = 0; a < n_alts; ++a) {
            size_t l = cp_lit_at(text, i, tlen, alts[a]);
            if (l == 0) continue;
            if (cp_rb(text, i + l, tlen)) return true;
        }
    }
    return false;
}

/* self-harm: \b(kill myself|suicide|self\s*-?\s*harm)\b */
static bool cp_match_self_harm(const char *text) {
    static const char *plain[] = { "kill myself", "suicide" };
    if (cp_match_word_alts(text, plain, 2)) return true;
    /* self \s* -? \s* harm — \b before 'self', \b after 'harm'. */
    size_t tlen = strlen(text);
    for (size_t i = 0; i < tlen; ++i) {
        if (!cp_lb(text, i)) continue;
        size_t p = cp_lit_at(text, i, tlen, "self");
        if (p == 0) continue;
        size_t j = i + p;
        while (j < tlen && cp_is_ws((unsigned char)text[j])) j++;   /* \s* */
        if (j < tlen && text[j] == '-') j++;                        /* -?  */
        while (j < tlen && cp_is_ws((unsigned char)text[j])) j++;   /* \s* */
        size_t h = cp_lit_at(text, j, tlen, "harm");
        if (h == 0) continue;
        if (cp_rb(text, j + h, tlen)) return true;
    }
    return false;
}

/* explicit-sexual: \b(porn|sexual content|nsfw)\b */
static bool cp_match_explicit_sexual(const char *text) {
    static const char *alts[] = { "porn", "sexual content", "nsfw" };
    return cp_match_word_alts(text, alts, 3);
}

/* violence: \b(how to make a bomb|chemical weapon|murder)\b */
static bool cp_match_violence(const char *text) {
    static const char *alts[] = { "how to make a bomb", "chemical weapon", "murder" };
    return cp_match_word_alts(text, alts, 3);
}

/* hate: \b(racial slur|hate speech)\b */
static bool cp_match_hate(const char *text) {
    static const char *alts[] = { "racial slur", "hate speech" };
    return cp_match_word_alts(text, alts, 2);
}

/* pii-card: \b(?:\d[ -]*?){13,19}\b
 *
 * Each unit is one digit followed by a lazily-minimal run of [ -]. The outer
 * {13,19} matches 13..19 units; the group always ends on a digit (trailing
 * lazy [ -]*? consumes nothing at the group end). A match exists at start i
 * (with a left boundary and text[i] a digit) iff, walking digit-to-digit over
 * only [ -] separators, some k in [13,19] lands on a digit immediately followed
 * by a non-word char (right boundary). */
static bool cp_match_card(const char *text) {
    size_t tlen = strlen(text);
    for (size_t i = 0; i < tlen; ++i) {
        if (!isdigit((unsigned char)text[i])) continue;
        if (!cp_lb(text, i)) continue;
        /* walk the chain of up to 19 digits linked by [ -]* separators */
        size_t pos = i;
        size_t digits = 0;
        while (digits < 19) {
            if (pos >= tlen || !isdigit((unsigned char)text[pos])) break;
            size_t digit_end = pos + 1;   /* index right after this digit */
            digits++;
            /* right boundary check after this digit (lazy trailing seps = 0) */
            if (digits >= 13 && cp_rb(text, digit_end, tlen)) return true;
            /* advance over separators [ -] to the next digit */
            size_t sep = digit_end;
            while (sep < tlen && (text[sep] == ' ' || text[sep] == '-')) sep++;
            pos = sep;
        }
    }
    return false;
}

/* Default rule set (CommonKeywordRules.Default), order-preserving. */
static const ca_keyword_rule_t CP_DEFAULT_RULES[] = {
    { "self-harm",       CA_SAFETY_VERDICT_REFUSE, 0.95f, cp_match_self_harm       },
    { "explicit-sexual", CA_SAFETY_VERDICT_FLAG,   0.70f, cp_match_explicit_sexual },
    { "violence",        CA_SAFETY_VERDICT_REFUSE, 0.90f, cp_match_violence        },
    { "hate",            CA_SAFETY_VERDICT_REFUSE, 0.90f, cp_match_hate            },
    { "pii-card",        CA_SAFETY_VERDICT_FLAG,   0.80f, cp_match_card            },
};

const ca_keyword_rule_t *ca_common_keyword_rules_default(size_t *out_count) {
    if (out_count) *out_count = sizeof(CP_DEFAULT_RULES) / sizeof(CP_DEFAULT_RULES[0]);
    return CP_DEFAULT_RULES;
}

/* ── IContentFilter ─────────────────────────────────────────────────────── */

typedef enum { CP_CF_KEYWORD, CP_CF_NULL } cp_cf_kind_t;

struct ca_content_filter {
    cp_cf_kind_t       kind;
    ca_keyword_rule_t *rules;      /* copied array (shallow) */
    size_t             rule_count;
};

ca_content_filter_t *ca_keyword_content_filter_create(const ca_keyword_rule_t *rules,
                                                      size_t rule_count) {
    ca_content_filter_t *f = (ca_content_filter_t *)calloc(1, sizeof(*f));
    if (!f) return NULL;
    f->kind = CP_CF_KEYWORD;
    if (!rules) {
        rules = ca_common_keyword_rules_default(&rule_count);
    }
    if (rule_count > 0) {
        f->rules = (ca_keyword_rule_t *)calloc(rule_count, sizeof(ca_keyword_rule_t));
        if (!f->rules) { free(f); return NULL; }
        memcpy(f->rules, rules, rule_count * sizeof(ca_keyword_rule_t));
        f->rule_count = rule_count;
    }
    return f;
}
ca_content_filter_t *ca_null_content_filter_create(void) {
    ca_content_filter_t *f = (ca_content_filter_t *)calloc(1, sizeof(*f));
    if (f) f->kind = CP_CF_NULL;
    return f;
}
void ca_content_filter_destroy(ca_content_filter_t *f) {
    if (!f) return;
    free(f->rules);
    free(f);
}
const char *ca_content_filter_backend_id(const ca_content_filter_t *f) {
    if (!f) return NULL;
    return f->kind == CP_CF_NULL ? "null" : "keyword";
}

bool ca_content_filter_classify(const ca_content_filter_t *f, const char *text,
                                ca_safety_finding_t *out) {
    if (!f || !out) return false;
    if (!text) return false;   /* C# throws ArgumentNullException on null text */

    if (f->kind == CP_CF_NULL) {
        cp_set_finding(out, CA_SAFETY_VERDICT_REFUSE, "no-filter-configured",
                       "Fail-closed default — wire a real IContentFilter to relax.", 1.0f);
        return true;
    }

    for (size_t i = 0; i < f->rule_count; ++i) {
        const ca_keyword_rule_t *r = &f->rules[i];
        if (r->match && r->match(text)) {
            char reason[128];
            snprintf(reason, sizeof(reason), "Matched rule '%s'", r->category ? r->category : "");
            cp_set_finding(out, r->on_match, r->category ? r->category : "", reason, r->confidence);
            return true;
        }
    }
    cp_set_finding(out, CA_SAFETY_VERDICT_ALLOW, "ok", "No rule matched", 1.0f);
    return true;
}

/* ── IRefusalPolicy ─────────────────────────────────────────────────────── */

typedef enum { CP_RP_THRESHOLD, CP_RP_NULL } cp_rp_kind_t;

struct ca_refusal_policy {
    cp_rp_kind_t kind;
    float        refuse_threshold;
    int          flag_ceiling;
};

ca_refusal_policy_t *ca_threshold_refusal_policy_create(float refuse_threshold,
                                                        int flag_ceiling) {
    ca_refusal_policy_t *p = (ca_refusal_policy_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->kind = CP_RP_THRESHOLD;
    p->refuse_threshold = refuse_threshold;
    p->flag_ceiling = flag_ceiling;
    return p;
}
ca_refusal_policy_t *ca_null_refusal_policy_create(void) {
    ca_refusal_policy_t *p = (ca_refusal_policy_t *)calloc(1, sizeof(*p));
    if (p) p->kind = CP_RP_NULL;
    return p;
}
void ca_refusal_policy_destroy(ca_refusal_policy_t *p) { free(p); }
const char *ca_refusal_policy_backend_id(const ca_refusal_policy_t *p) {
    if (!p) return NULL;
    return p->kind == CP_RP_NULL ? "null" : "threshold";
}

bool ca_refusal_policy_should_refuse(const ca_refusal_policy_t *p,
                                     const ca_safety_finding_t *findings,
                                     size_t count, bool *out_refuse) {
    if (!p || !out_refuse) return false;
    if (!findings && count > 0) return false;   /* C# ThrowIfNull(findings) */

    if (p->kind == CP_RP_NULL) { *out_refuse = true; return true; }

    /* Any Refuse finding with Confidence >= threshold → refuse. */
    for (size_t i = 0; i < count; ++i) {
        if (findings[i].verdict == CA_SAFETY_VERDICT_REFUSE &&
            findings[i].confidence >= p->refuse_threshold) {
            *out_refuse = true;
            return true;
        }
    }
    /* Else refuse iff count of Flag findings exceeds the ceiling. */
    int flag_count = 0;
    for (size_t i = 0; i < count; ++i)
        if (findings[i].verdict == CA_SAFETY_VERDICT_FLAG) flag_count++;
    *out_refuse = flag_count > p->flag_ceiling;
    return true;
}

/* ── IPromptInjectionDetector ───────────────────────────────────────────── */

typedef enum { CP_PI_KEYWORD, CP_PI_NULL } cp_pi_kind_t;

struct ca_prompt_injection_detector {
    cp_pi_kind_t kind;
};

/* Each injection pattern is a matcher that, from a start index, returns the
 * matched length (0 = no match at that index). The detector scans left-to-right
 * and reports the first (leftmost, first-pattern-order-at-that-index) match —
 * mirroring foreach(pattern) { Match(...) } which returns the leftmost match of
 * whichever pattern is tested; since the C# tests patterns in order and returns
 * on the first pattern that matches anywhere, we reproduce that order. */

/* Match a literal (case-insensitive) at pos; return length or 0. */
static size_t cp_pi_lit(const char *text, size_t pos, size_t tlen, const char *lit) {
    return cp_lit_at(text, pos, tlen, lit);
}
/* Match one of the lowercase alternatives at pos; return matched length or 0
 * (leftmost alternative in array order, as regex alternation prefers order). */
static size_t cp_pi_alt(const char *text, size_t pos, size_t tlen,
                        const char *const *alts, size_t n) {
    for (size_t a = 0; a < n; ++a) {
        size_t l = cp_lit_at(text, pos, tlen, alts[a]);
        if (l) return l;
    }
    return 0;
}
/* Consume \s+ at pos; return count consumed (0 = fails, need >=1). */
static size_t cp_pi_ws1(const char *text, size_t pos, size_t tlen) {
    size_t n = 0;
    while (pos + n < tlen && cp_is_ws((unsigned char)text[pos + n])) n++;
    return n;
}

/* "ignore (all|the|any) (previous|prior) instructions" */
static size_t cp_pi_ignore(const char *text, size_t i, size_t tlen) {
    static const char *g1[] = { "all", "the", "any" };
    static const char *g2[] = { "previous", "prior" };
    size_t p = i, l;
    l = cp_pi_lit(text, p, tlen, "ignore "); if (!l) return 0; p += l;
    l = cp_pi_alt(text, p, tlen, g1, 3);     if (!l) return 0; p += l;
    l = cp_pi_lit(text, p, tlen, " ");       if (!l) return 0; p += l;
    l = cp_pi_alt(text, p, tlen, g2, 2);     if (!l) return 0; p += l;
    l = cp_pi_lit(text, p, tlen, " instructions"); if (!l) return 0; p += l;
    return p - i;
}
/* "forget (everything|all) (above|prior)" */
static size_t cp_pi_forget(const char *text, size_t i, size_t tlen) {
    static const char *g1[] = { "everything", "all" };
    static const char *g2[] = { "above", "prior" };
    size_t p = i, l;
    l = cp_pi_lit(text, p, tlen, "forget "); if (!l) return 0; p += l;
    l = cp_pi_alt(text, p, tlen, g1, 2);     if (!l) return 0; p += l;
    l = cp_pi_lit(text, p, tlen, " ");       if (!l) return 0; p += l;
    l = cp_pi_alt(text, p, tlen, g2, 2);     if (!l) return 0; p += l;
    return p - i;
}
/* "you (are now|will be|are no longer)" */
static size_t cp_pi_you(const char *text, size_t i, size_t tlen) {
    static const char *g1[] = { "are now", "will be", "are no longer" };
    size_t p = i, l;
    l = cp_pi_lit(text, p, tlen, "you ");    if (!l) return 0; p += l;
    l = cp_pi_alt(text, p, tlen, g1, 3);     if (!l) return 0; p += l;
    return p - i;
}
/* "system prompt[:\s]" — one char that is ':' or whitespace. */
static size_t cp_pi_sysprompt(const char *text, size_t i, size_t tlen) {
    size_t p = i, l;
    l = cp_pi_lit(text, p, tlen, "system prompt"); if (!l) return 0; p += l;
    if (p >= tlen) return 0;
    char c = text[p];
    if (c == ':' || cp_is_ws((unsigned char)c)) { p += 1; return p - i; }
    return 0;
}
/* "reveal (your|the) (instructions|system prompt|hidden context)" */
static size_t cp_pi_reveal(const char *text, size_t i, size_t tlen) {
    static const char *g1[] = { "your", "the" };
    static const char *g2[] = { "instructions", "system prompt", "hidden context" };
    size_t p = i, l;
    l = cp_pi_lit(text, p, tlen, "reveal "); if (!l) return 0; p += l;
    l = cp_pi_alt(text, p, tlen, g1, 2);     if (!l) return 0; p += l;
    l = cp_pi_lit(text, p, tlen, " ");       if (!l) return 0; p += l;
    l = cp_pi_alt(text, p, tlen, g2, 3);     if (!l) return 0; p += l;
    return p - i;
}
/* "<\|im_(start|end)\|>" → literal "<|im_" alt("start","end") "|>" */
static size_t cp_pi_im(const char *text, size_t i, size_t tlen) {
    static const char *g1[] = { "start", "end" };
    size_t p = i, l;
    l = cp_pi_lit(text, p, tlen, "<|im_"); if (!l) return 0; p += l;
    l = cp_pi_alt(text, p, tlen, g1, 2);   if (!l) return 0; p += l;
    l = cp_pi_lit(text, p, tlen, "|>");    if (!l) return 0; p += l;
    return p - i;
}
/* "(BEGIN|END)\s+(SYSTEM|DEVELOPER|ASSISTANT)\s+MESSAGE" */
static size_t cp_pi_beginend(const char *text, size_t i, size_t tlen) {
    static const char *g1[] = { "begin", "end" };
    static const char *g2[] = { "system", "developer", "assistant" };
    size_t p = i, l;
    l = cp_pi_alt(text, p, tlen, g1, 2);   if (!l) return 0; p += l;
    l = cp_pi_ws1(text, p, tlen);          if (!l) return 0; p += l;   /* \s+ */
    l = cp_pi_alt(text, p, tlen, g2, 3);   if (!l) return 0; p += l;
    l = cp_pi_ws1(text, p, tlen);          if (!l) return 0; p += l;   /* \s+ */
    l = cp_pi_lit(text, p, tlen, "message"); if (!l) return 0; p += l;
    return p - i;
}

typedef size_t (*cp_pi_match_fn)(const char *text, size_t i, size_t tlen);
static const cp_pi_match_fn CP_PI_PATTERNS[] = {
    cp_pi_ignore, cp_pi_forget, cp_pi_you, cp_pi_sysprompt,
    cp_pi_reveal, cp_pi_im, cp_pi_beginend,
};
#define CP_PI_N (sizeof(CP_PI_PATTERNS) / sizeof(CP_PI_PATTERNS[0]))

ca_prompt_injection_detector_t *ca_keyword_prompt_injection_detector_create(void) {
    ca_prompt_injection_detector_t *d =
        (ca_prompt_injection_detector_t *)calloc(1, sizeof(*d));
    if (d) d->kind = CP_PI_KEYWORD;
    return d;
}
ca_prompt_injection_detector_t *ca_null_prompt_injection_detector_create(void) {
    ca_prompt_injection_detector_t *d =
        (ca_prompt_injection_detector_t *)calloc(1, sizeof(*d));
    if (d) d->kind = CP_PI_NULL;
    return d;
}
void ca_prompt_injection_detector_destroy(ca_prompt_injection_detector_t *d) { free(d); }
const char *ca_prompt_injection_detector_backend_id(const ca_prompt_injection_detector_t *d) {
    if (!d) return NULL;
    return d->kind == CP_PI_NULL ? "null" : "keyword";
}

/* Truncate a UTF-8-agnostic byte string to `max` chars appending the ellipsis
 * "…" (U+2026, 3 bytes in UTF-8) when longer — matches C# Truncate. C# measures
 * by UTF-16 units; for ASCII payloads (all shipped patterns) this coincides. */
static char *cp_truncate(const char *s, size_t len, size_t max) {
    if (len <= max) {
        char *r = (char *)malloc(len + 1);
        if (r) { memcpy(r, s, len); r[len] = '\0'; }
        return r;
    }
    /* s[..max] + "…" */
    const char *ell = "\xE2\x80\xA6";
    char *r = (char *)malloc(max + strlen(ell) + 1);
    if (r) { memcpy(r, s, max); memcpy(r + max, ell, strlen(ell)); r[max + strlen(ell)] = '\0'; }
    return r;
}

bool ca_prompt_injection_detector_inspect(const ca_prompt_injection_detector_t *d,
                                          const char *untrusted_content,
                                          const char *source_label,
                                          ca_safety_finding_t *out) {
    if (!d || !out) return false;
    if (!untrusted_content) return false;   /* C# throws on null content */
    const char *src = source_label ? source_label : "";

    if (d->kind == CP_PI_NULL) {
        cp_set_finding(out, CA_SAFETY_VERDICT_REFUSE, "no-detector-configured",
                       "Fail-closed default.", 1.0f);
        return true;
    }

    size_t tlen = strlen(untrusted_content);
    /* Reproduce foreach(pattern){ Match(...).Success } — the first pattern (in
     * array order) that matches anywhere wins; within it, use the leftmost
     * start index (regex returns the leftmost match). */
    for (size_t pi = 0; pi < CP_PI_N; ++pi) {
        for (size_t i = 0; i < tlen; ++i) {
            size_t mlen = CP_PI_PATTERNS[pi](untrusted_content, i, tlen);
            if (mlen) {
                char *val = cp_truncate(untrusted_content + i, mlen, 60);
                size_t rn = strlen(src) + (val ? strlen(val) : 0) + 40;
                char *reason = (char *)malloc(rn);
                if (reason)
                    snprintf(reason, rn, "Pattern matched in %s: \"%s\"", src, val ? val : "");
                out->verdict    = CA_SAFETY_VERDICT_REFUSE;
                out->category   = cp_strdup("prompt-injection");
                out->reason     = reason ? reason : cp_strdup("prompt-injection");
                out->confidence = 0.9f;
                free(val);
                return true;
            }
        }
    }
    cp_set_finding(out, CA_SAFETY_VERDICT_ALLOW, "ok", "No injection patterns", 1.0f);
    return true;
}

/* ── SafetyAuditEntry + ISafetyAuditLog ─────────────────────────────────── */

void ca_safety_audit_entry_free(ca_safety_audit_entry_t *e) {
    if (!e) return;
    free(e->user_id);
    free(e->action);
    free(e->reason);
    e->user_id = e->action = e->reason = NULL;
}
void ca_safety_audit_entry_free_array(ca_safety_audit_entry_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_safety_audit_entry_free(&arr[i]);
    free(arr);
}
ca_safety_audit_entry_t *ca_safety_audit_entry_copy(ca_safety_audit_entry_t *dst,
                                                    const ca_safety_audit_entry_t *src) {
    if (!dst || !src) return dst;
    dst->at_utc_ms = src->at_utc_ms;
    dst->user_id   = cp_strdup(src->user_id);
    dst->action    = cp_strdup(src->action);
    dst->verdict   = src->verdict;
    dst->reason    = cp_strdup(src->reason);
    return dst;
}

typedef enum { CP_AL_IN_MEMORY, CP_AL_NULL } cp_al_kind_t;

struct ca_safety_audit_log {
    cp_al_kind_t             kind;
    ca_safety_audit_entry_t *entries;   /* append order */
    size_t                   count, cap;
};

ca_safety_audit_log_t *ca_in_memory_safety_audit_log_create(void) {
    ca_safety_audit_log_t *l = (ca_safety_audit_log_t *)calloc(1, sizeof(*l));
    if (l) l->kind = CP_AL_IN_MEMORY;
    return l;
}
ca_safety_audit_log_t *ca_null_safety_audit_log_create(void) {
    ca_safety_audit_log_t *l = (ca_safety_audit_log_t *)calloc(1, sizeof(*l));
    if (l) l->kind = CP_AL_NULL;
    return l;
}
void ca_safety_audit_log_destroy(ca_safety_audit_log_t *log) {
    if (!log) return;
    for (size_t i = 0; i < log->count; ++i) ca_safety_audit_entry_free(&log->entries[i]);
    free(log->entries);
    free(log);
}
const char *ca_safety_audit_log_backend_id(const ca_safety_audit_log_t *log) {
    if (!log) return NULL;
    return log->kind == CP_AL_NULL ? "null" : "in-memory";
}

bool ca_safety_audit_log_log(ca_safety_audit_log_t *log,
                             const ca_safety_audit_entry_t *entry) {
    if (!log || !entry) return false;
    if (log->kind == CP_AL_NULL) return true;   /* accepted, not stored */
    if (log->count == log->cap) {
        size_t nc = log->cap ? log->cap * 2 : 8;
        void *n = realloc(log->entries, nc * sizeof(*log->entries));
        if (!n) return false;
        log->entries = n; log->cap = nc;
    }
    ca_safety_audit_entry_copy(&log->entries[log->count], entry);
    log->count++;
    return true;
}

ca_safety_audit_entry_t *ca_safety_audit_log_read(ca_safety_audit_log_t *log,
                                                  const char *user_id, int limit,
                                                  size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!log) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    if (log->kind == CP_AL_NULL || limit <= 0) return NULL;

    /* pick matching indices; most-recent-first = reverse append order. */
    size_t *pick = (size_t *)malloc(log->count ? log->count * sizeof(size_t) : 1);
    if (!pick) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    size_t m = 0;
    for (size_t r = log->count; r-- > 0; ) {
        if (user_id == NULL ||
            (log->entries[r].user_id && strcmp(log->entries[r].user_id, user_id) == 0))
            pick[m++] = r;
    }
    if (m == 0) { free(pick); return NULL; }
    size_t take = m < (size_t)limit ? m : (size_t)limit;
    ca_safety_audit_entry_t *res = (ca_safety_audit_entry_t *)calloc(take, sizeof(*res));
    if (!res) { free(pick); if (out_count) *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < take; ++i) ca_safety_audit_entry_copy(&res[i], &log->entries[pick[i]]);
    free(pick);
    if (out_count) *out_count = take;
    return res;
}
