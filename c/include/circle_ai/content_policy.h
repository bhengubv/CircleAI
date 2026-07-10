#ifndef CIRCLE_AI_CONTENT_POLICY_H
#define CIRCLE_AI_CONTENT_POLICY_H

/*
 * content_policy.h — CircleAI.ContentPolicy safety guardrails (C11 port).
 *
 * Ports the CircleAI.ContentPolicy namespace:
 *   - SafetyVerdict enum, SafetyFinding record, SafetyAuditEntry record
 *   - IContentFilter        (Keyword / Null)
 *   - IRefusalPolicy        (Threshold / Null)
 *   - IPromptInjectionDetector (Keyword / Null)
 *   - ISafetyAuditLog       (InMemory / Null)
 *   - KeywordRule + CommonKeywordRules.Default rule set
 *
 * The C# filters/detectors wrap compiled System.Text.RegularExpressions.Regex.
 * C has no regex in the standard library, so each shipped rule/pattern is
 * ported to a hand-written matcher that reproduces the exact matching
 * semantics of its C# regex (case-insensitive; \b word boundaries where the
 * pattern uses them). A KeywordRule therefore carries a matcher function
 * pointer; hosts supplying custom rules provide their own matcher. This keeps
 * the "does this pattern match this text" contract intact without importing a
 * regex engine.
 *
 * Conventions: ca_ prefix, _t types, opaque handles with create/destroy,
 * strdup'd owning fields with matching *_free, deep-copy getters, arrays are
 * fresh copies the caller frees. Errors surface via NULL + count=SIZE_MAX.
 *
 * Pure C11 + libc.
 */

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── SafetyVerdict ──────────────────────────────────────────────────────── */

typedef enum {
    CA_SAFETY_VERDICT_ALLOW  = 0,
    CA_SAFETY_VERDICT_FLAG   = 1,
    CA_SAFETY_VERDICT_REFUSE = 2
} ca_safety_verdict_t;

/* ── SafetyFinding ──────────────────────────────────────────────────────── */

typedef struct {
    ca_safety_verdict_t verdict;
    char               *category;   /* owned */
    char               *reason;     /* owned */
    float               confidence;
} ca_safety_finding_t;

/* Free owned fields (not the struct). */
void ca_safety_finding_free(ca_safety_finding_t *f);
/* Free an array of findings + the array. */
void ca_safety_finding_free_array(ca_safety_finding_t *arr, size_t count);
/* Deep-copy src into dst. Returns dst. */
ca_safety_finding_t *ca_safety_finding_copy(ca_safety_finding_t *dst,
                                            const ca_safety_finding_t *src);

/* ── KeywordRule + CommonKeywordRules ───────────────────────────────────── */

/* Matcher: returns true iff the rule's pattern matches text. text is never
 * NULL when invoked. */
typedef bool (*ca_keyword_match_fn)(const char *text);

typedef struct {
    const char         *category;   /* borrowed literal */
    ca_safety_verdict_t on_match;
    float               confidence;
    ca_keyword_match_fn match;      /* required */
} ca_keyword_rule_t;

/* CommonKeywordRules.Default — pointer to a static array of 5 rules.
 * *out_count receives the element count. The array is static/borrowed; do not
 * free it. */
const ca_keyword_rule_t *ca_common_keyword_rules_default(size_t *out_count);

/* ── IContentFilter (Keyword / Null) ────────────────────────────────────── */

typedef struct ca_content_filter ca_content_filter_t;

/* KeywordContentFilter. rules==NULL uses CommonKeywordRules.Default. When
 * rules != NULL the array is copied (shallow — the category strings + matcher
 * pointers are borrowed and must outlive the filter, exactly as the C# rule
 * records are held by reference). */
ca_content_filter_t *ca_keyword_content_filter_create(const ca_keyword_rule_t *rules,
                                                      size_t rule_count);
/* NullContentFilter — fail-closed (always Refuse). */
ca_content_filter_t *ca_null_content_filter_create(void);

void        ca_content_filter_destroy(ca_content_filter_t *f);
const char *ca_content_filter_backend_id(const ca_content_filter_t *f);

/* ClassifyAsync — writes a fresh finding into *out (caller frees with
 * ca_safety_finding_free). Returns false when f/out is NULL or text is NULL
 * (C# throws ArgumentNullException on null text). */
bool ca_content_filter_classify(const ca_content_filter_t *f, const char *text,
                                ca_safety_finding_t *out);

/* ── IRefusalPolicy (Threshold / Null) ──────────────────────────────────── */

typedef struct ca_refusal_policy ca_refusal_policy_t;

/* ThresholdRefusalPolicy — refuse_threshold default 0.5, flag_ceiling default 3
 * (pass those defaults explicitly). */
ca_refusal_policy_t *ca_threshold_refusal_policy_create(float refuse_threshold,
                                                        int flag_ceiling);
/* NullRefusalPolicy — always refuses. */
ca_refusal_policy_t *ca_null_refusal_policy_create(void);

void        ca_refusal_policy_destroy(ca_refusal_policy_t *p);
const char *ca_refusal_policy_backend_id(const ca_refusal_policy_t *p);

/* ShouldRefuseAsync — findings may be NULL only when count==0. Returns the
 * refuse decision via *out_refuse; function returns false (and leaves
 * *out_refuse untouched) only on NULL p / NULL out_refuse (C# throws on null
 * findings — represented here as findings==NULL && count>0 → returns false). */
bool ca_refusal_policy_should_refuse(const ca_refusal_policy_t *p,
                                     const ca_safety_finding_t *findings,
                                     size_t count, bool *out_refuse);

/* ── IPromptInjectionDetector (Keyword / Null) ──────────────────────────── */

typedef struct ca_prompt_injection_detector ca_prompt_injection_detector_t;

ca_prompt_injection_detector_t *ca_keyword_prompt_injection_detector_create(void);
ca_prompt_injection_detector_t *ca_null_prompt_injection_detector_create(void);

void        ca_prompt_injection_detector_destroy(ca_prompt_injection_detector_t *d);
const char *ca_prompt_injection_detector_backend_id(const ca_prompt_injection_detector_t *d);

/* InspectAsync — writes a fresh finding into *out (caller frees). Returns false
 * when d/out is NULL or untrusted_content is NULL (C# throws on null content).
 * source_label may be NULL (rendered as empty). */
bool ca_prompt_injection_detector_inspect(const ca_prompt_injection_detector_t *d,
                                          const char *untrusted_content,
                                          const char *source_label,
                                          ca_safety_finding_t *out);

/* ── SafetyAuditEntry + ISafetyAuditLog (InMemory / Null) ───────────────── */

typedef struct {
    int64_t             at_utc_ms;  /* Unix ms UTC */
    char               *user_id;    /* owned */
    char               *action;     /* owned */
    ca_safety_verdict_t verdict;
    char               *reason;     /* owned */
} ca_safety_audit_entry_t;

void ca_safety_audit_entry_free(ca_safety_audit_entry_t *e);
void ca_safety_audit_entry_free_array(ca_safety_audit_entry_t *arr, size_t count);
ca_safety_audit_entry_t *ca_safety_audit_entry_copy(ca_safety_audit_entry_t *dst,
                                                    const ca_safety_audit_entry_t *src);

typedef struct ca_safety_audit_log ca_safety_audit_log_t;

/* InMemorySafetyAuditLog — append-only. NB: C# ships only a Null log; the
 * in-memory log is the working (non-stub) implementation of the same contract
 * so the append-only surface is testable. */
ca_safety_audit_log_t *ca_in_memory_safety_audit_log_create(void);
/* NullSafetyAuditLog — accepts writes, always reads empty. */
ca_safety_audit_log_t *ca_null_safety_audit_log_create(void);

void        ca_safety_audit_log_destroy(ca_safety_audit_log_t *log);
const char *ca_safety_audit_log_backend_id(const ca_safety_audit_log_t *log);

/* LogAsync — deep-copies entry in. Returns false on NULL log/entry. */
bool ca_safety_audit_log_log(ca_safety_audit_log_t *log,
                             const ca_safety_audit_entry_t *entry);

/* ReadAsync — most-recent-first, at most `limit` entries, filtered by user_id
 * when user_id != NULL (else all). Fresh array the caller frees with
 * ca_safety_audit_entry_free_array. *out_count receives the count (0 → NULL).
 * limit<=0 yields an empty result. NULL log → *out_count SIZE_MAX + NULL. */
ca_safety_audit_entry_t *ca_safety_audit_log_read(ca_safety_audit_log_t *log,
                                                  const char *user_id, int limit,
                                                  size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CONTENT_POLICY_H */
