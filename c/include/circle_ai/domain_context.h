/*
 * domain_context.h - what a companion is told about the domain it is working in.
 *
 * FORTY-FOUR DOMAINS, ONE SHAPE. Every domain in CircleAI answers the same
 * three questions: what should the model be told it is doing, which rules apply
 * to this kind of work, and which tools are worth reaching for. The C# spells
 * that as a static class per domain; here it is one struct and a table, because
 * forty-four copies of the same three fields is a table.
 *
 * IT IS ALL STATIC AND CONST. A domain context is a FACT about a domain, not
 * state: it is identical on every device and in every session, so it needs no
 * allocation, cannot be freed by mistake, and costs nothing to hand out a
 * pointer to. Nothing in this header returns memory the caller owns except
 * ca_domain_context_enrich, which says so.
 *
 * THE ADAPTER IS THE PROMPT, NOT THE SESSION. The C# CompanionAdapter wraps a
 * live session and forwards to it. The half that is worth porting - and the
 * half that can be tested without a model - is what it does to the TEXT on the
 * way through: the snippet, then a blank line, then the message. That is
 * ca_domain_context_enrich, and it is the whole behavioural contract.
 */

#ifndef CIRCLE_AI_DOMAIN_CONTEXT_H
#define CIRCLE_AI_DOMAIN_CONTEXT_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    /* Lower-case snake, e.g. "commerce_finance". Stable; used for lookup. */
    const char *domain;

    /* Prepended to what the caller says, on every turn. */
    const char *system_prompt_snippet;

    /* The regimes this domain has to answer to. Never empty. */
    const char *const *compliance_flags;
    size_t compliance_flag_count;

    /* What is worth reaching for here. Never empty. */
    const char *const *suggested_tools;
    size_t suggested_tool_count;
} ca_domain_context_t;

/* How many domains there are. */
size_t ca_domain_context_count(void);

/* The domain at an index, or NULL past the end. For iterating the table. */
const ca_domain_context_t *ca_domain_context_at(size_t index);

/*
 * By name, or NULL when there is no such domain.
 *
 * NULL rather than a fallback ON PURPOSE. Handing back some other domain's
 * compliance flags because a name was misspelled is worse than handing back
 * nothing: the caller would go on to claim it was following rules it had never
 * read.
 */
const ca_domain_context_t *ca_domain_context_find(const char *domain);

/*
 * The snippet, a blank line, then the message. Caller frees.
 *
 * Returns NULL only if the allocation fails. A NULL or empty message still
 * produces the snippet, because the snippet is what the model is being told
 * about itself and an empty turn does not change that.
 */
char *ca_domain_context_enrich(const ca_domain_context_t *ctx, const char *message);

/* True when this domain has to answer to that regime. Case-sensitive, as the
 * flags are identifiers rather than prose. */
int ca_domain_context_has_flag(const ca_domain_context_t *ctx, const char *flag);

/* True when this domain suggests that tool. */
int ca_domain_context_suggests_tool(const ca_domain_context_t *ctx, const char *tool);

/* Named accessors: no lookup, and a misspelling is a compile error rather
 * than a NULL at run time. */
const ca_domain_context_t *ca_accessibility_domain_context(void);
const ca_domain_context_t *ca_agriculture_domain_context(void);
const ca_domain_context_t *ca_beauty_domain_context(void);
const ca_domain_context_t *ca_business_domain_context(void);
const ca_domain_context_t *ca_civic_domain_context(void);
const ca_domain_context_t *ca_commerce_domain_context(void);
const ca_domain_context_t *ca_commerce_accounting_domain_context(void);
const ca_domain_context_t *ca_commerce_finance_domain_context(void);
const ca_domain_context_t *ca_commerce_integration_pay_fast_domain_context(void);
const ca_domain_context_t *ca_commerce_integration_xero_domain_context(void);
const ca_domain_context_t *ca_community_domain_context(void);
const ca_domain_context_t *ca_construction_domain_context(void);
const ca_domain_context_t *ca_creative_domain_context(void);
const ca_domain_context_t *ca_education_domain_context(void);
const ca_domain_context_t *ca_elderly_domain_context(void);
const ca_domain_context_t *ca_energy_domain_context(void);
const ca_domain_context_t *ca_faith_domain_context(void);
const ca_domain_context_t *ca_family_domain_context(void);
const ca_domain_context_t *ca_fitness_domain_context(void);
const ca_domain_context_t *ca_food_domain_context(void);
const ca_domain_context_t *ca_gaming_domain_context(void);
const ca_domain_context_t *ca_hr_domain_context(void);
const ca_domain_context_t *ca_healthcare_domain_context(void);
const ca_domain_context_t *ca_home_domain_context(void);
const ca_domain_context_t *ca_hospitality_domain_context(void);
const ca_domain_context_t *ca_kids_domain_context(void);
const ca_domain_context_t *ca_legal_domain_context(void);
const ca_domain_context_t *ca_logistics_domain_context(void);
const ca_domain_context_t *ca_media_domain_context(void);
const ca_domain_context_t *ca_parenting_domain_context(void);
const ca_domain_context_t *ca_personal_domain_context(void);
const ca_domain_context_t *ca_personal_finance_domain_context(void);
const ca_domain_context_t *ca_personal_health_domain_context(void);
const ca_domain_context_t *ca_personal_mental_domain_context(void);
const ca_domain_context_t *ca_pets_domain_context(void);
const ca_domain_context_t *ca_real_estate_domain_context(void);
const ca_domain_context_t *ca_relationships_domain_context(void);
const ca_domain_context_t *ca_retail_domain_context(void);
const ca_domain_context_t *ca_safety_domain_context(void);
const ca_domain_context_t *ca_safety_child_domain_context(void);
const ca_domain_context_t *ca_social_domain_context(void);
const ca_domain_context_t *ca_sports_domain_context(void);
const ca_domain_context_t *ca_tourism_domain_context(void);
const ca_domain_context_t *ca_travel_domain_context(void);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_DOMAIN_CONTEXT_H */
