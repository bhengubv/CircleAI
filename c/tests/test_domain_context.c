/*
 * test_domain_context.c - CircleAI's forty-four domain contexts (C11 port),
 * verified against the *DomainContext.cs reference classes.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/domain_context.h"

static void test_table_is_complete(void) {
    /* Forty-four domains in the C#, forty-four here. A domain that quietly
     * failed to generate would otherwise only be noticed by whoever went
     * looking for it. */
    assert(ca_domain_context_count() == 44);

    for (size_t i = 0; i < ca_domain_context_count(); i++) {
        const ca_domain_context_t *d = ca_domain_context_at(i);
        assert(d);
        assert(d->domain && d->domain[0]);
        assert(d->system_prompt_snippet && d->system_prompt_snippet[0]);

        /* NEVER EMPTY, either of them. A domain with no compliance flags is a
         * domain nobody wrote the rules down for, and a domain with no tools
         * gives the model nothing to reach for. */
        assert(d->compliance_flag_count > 0);
        assert(d->suggested_tool_count > 0);

        for (size_t j = 0; j < d->compliance_flag_count; j++)
            assert(d->compliance_flags[j] && d->compliance_flags[j][0]);
        for (size_t j = 0; j < d->suggested_tool_count; j++)
            assert(d->suggested_tools[j] && d->suggested_tools[j][0]);
    }

    assert(ca_domain_context_at(ca_domain_context_count()) == NULL);
    assert(ca_domain_context_at((size_t)-1) == NULL);
    printf("  table_is_complete: ok\n");
}

static void test_every_domain_is_named_once(void) {
    /* Two domains with one name would make the lookup return whichever came
     * first, silently. */
    for (size_t i = 0; i < ca_domain_context_count(); i++) {
        for (size_t j = i + 1; j < ca_domain_context_count(); j++) {
            assert(strcmp(ca_domain_context_at(i)->domain,
                          ca_domain_context_at(j)->domain) != 0);
        }
    }
    printf("  every_domain_is_named_once: ok\n");
}

static void test_lookup_by_name(void) {
    const ca_domain_context_t *civic = ca_domain_context_find("civic");
    assert(civic);
    assert(strcmp(civic->domain, "civic") == 0);

    /* The named accessor and the lookup are the same object, not two copies. */
    assert(ca_civic_domain_context() == civic);

    /* A multi-word domain keeps its underscores. */
    assert(ca_domain_context_find("commerce_finance") == ca_commerce_finance_domain_context());
    assert(ca_domain_context_find("safety_child") == ca_safety_child_domain_context());
    assert(ca_domain_context_find("personal_health") == ca_personal_health_domain_context());
    printf("  lookup_by_name: ok\n");
}

static void test_an_unknown_domain_is_null_not_a_fallback(void) {
    /* Handing back some other domain's compliance flags because a name was
     * misspelled is worse than handing back nothing: the caller would go on to
     * claim it was following rules it had never read. */
    assert(ca_domain_context_find("civics") == NULL);
    assert(ca_domain_context_find("CIVIC") == NULL);
    assert(ca_domain_context_find("") == NULL);
    assert(ca_domain_context_find(NULL) == NULL);
    printf("  an_unknown_domain_is_null_not_a_fallback: ok\n");
}

static void test_the_snippet_says_which_domain_it_is(void) {
    /* Every snippet in the reference opens with [DOMAIN: X]. It is what tells
     * a model which hat it is wearing, and a snippet without it is a snippet
     * that got truncated somewhere. */
    for (size_t i = 0; i < ca_domain_context_count(); i++) {
        const char *s = ca_domain_context_at(i)->system_prompt_snippet;
        assert(strncmp(s, "[DOMAIN: ", 9) == 0);
        assert(strchr(s, ']') != NULL);
    }
    printf("  the_snippet_says_which_domain_it_is: ok\n");
}

static void test_enrich_is_snippet_blank_line_message(void) {
    const ca_domain_context_t *civic = ca_civic_domain_context();
    char *out = ca_domain_context_enrich(civic, "how do I apply for a permit");
    assert(out);

    const size_t n = strlen(civic->system_prompt_snippet);
    assert(strncmp(out, civic->system_prompt_snippet, n) == 0);
    assert(out[n] == '\n' && out[n + 1] == '\n');
    assert(strcmp(out + n + 2, "how do I apply for a permit") == 0);
    free(out);
    printf("  enrich_is_snippet_blank_line_message: ok\n");
}

static void test_an_empty_turn_still_gets_the_snippet(void) {
    /* What the model is told about itself does not depend on whether the
     * person said anything. Dropping the snippet here would silently take the
     * domain off for exactly the turn nobody was watching. */
    const ca_domain_context_t *civic = ca_civic_domain_context();
    const size_t n = strlen(civic->system_prompt_snippet);

    char *a = ca_domain_context_enrich(civic, "");
    char *b = ca_domain_context_enrich(civic, NULL);
    assert(a && b);
    assert(strlen(a) == n + 2);
    assert(strcmp(a, b) == 0);
    free(a);
    free(b);
    printf("  an_empty_turn_still_gets_the_snippet: ok\n");
}

static void test_enrich_handles_a_long_message(void) {
    /* The snippets are long and so are real turns; the length arithmetic has to
     * hold with both. */
    char big[4096];
    memset(big, 'x', sizeof(big) - 1);
    big[sizeof(big) - 1] = '\0';

    const ca_domain_context_t *d = ca_healthcare_domain_context();
    char *out = ca_domain_context_enrich(d, big);
    assert(out);
    assert(strlen(out) == strlen(d->system_prompt_snippet) + 2 + strlen(big));
    assert(strcmp(out + strlen(d->system_prompt_snippet) + 2, big) == 0);
    free(out);
    printf("  enrich_handles_a_long_message: ok\n");
}

static void test_enrich_refuses_a_null_context(void) {
    assert(ca_domain_context_enrich(NULL, "anything") == NULL);
    printf("  enrich_refuses_a_null_context: ok\n");
}

static void test_flags_and_tools_are_queryable(void) {
    const ca_domain_context_t *civic = ca_civic_domain_context();
    assert(ca_domain_context_has_flag(civic, "PAJA"));
    assert(ca_domain_context_has_flag(civic, "POPIA"));
    assert(!ca_domain_context_has_flag(civic, "HIPAA"));

    /* Case-sensitive: these are identifiers, not prose. */
    assert(!ca_domain_context_has_flag(civic, "paja"));

    assert(ca_domain_context_suggests_tool(civic, "map"));
    assert(!ca_domain_context_suggests_tool(civic, "scalpel"));

    assert(!ca_domain_context_has_flag(NULL, "PAJA"));
    assert(!ca_domain_context_has_flag(civic, NULL));
    assert(!ca_domain_context_suggests_tool(NULL, "map"));
    assert(!ca_domain_context_suggests_tool(civic, NULL));
    printf("  flags_and_tools_are_queryable: ok\n");
}

static void test_popia_reaches_every_domain_except_three(void) {
    /* South African privacy law applies to forty-one of the forty-four, and a
     * domain that lost POPIA in the port would be quietly claiming it does not
     * handle personal information.
     *
     * THE THREE EXCEPTIONS ARE DELIBERATE, and each is worth knowing:
     *   - commerce_accounting is about a company's books - IFRS, SARS, the VAT
     *     Act - and not about people at all
     *   - kids and safety_child carry a STRICTER children's variant instead of
     *     the general flag, which is the right way round
     *
     * The two child domains spell that variant DIFFERENTLY - POPIA_Childrens_Data
     * and POPIA_Children - which is an inconsistency in the reference rather
     * than in this port. It is asserted here so that it is visible: anything
     * matching on the flag string has to know about both spellings.
     */
    size_t with_popia = 0;
    for (size_t i = 0; i < ca_domain_context_count(); i++) {
        if (ca_domain_context_has_flag(ca_domain_context_at(i), "POPIA")) with_popia++;
    }
    assert(with_popia == 41);

    assert(!ca_domain_context_has_flag(ca_commerce_accounting_domain_context(), "POPIA"));
    assert(ca_domain_context_has_flag(ca_commerce_accounting_domain_context(), "SARS"));

    assert(!ca_domain_context_has_flag(ca_kids_domain_context(), "POPIA"));
    assert(ca_domain_context_has_flag(ca_kids_domain_context(), "POPIA_Childrens_Data"));

    assert(!ca_domain_context_has_flag(ca_safety_child_domain_context(), "POPIA"));
    assert(ca_domain_context_has_flag(ca_safety_child_domain_context(), "POPIA_Children"));

    printf("  popia_reaches_every_domain_except_three: ok\n");
}

static void test_named_accessors_all_resolve(void) {
    /* Forty-four accessors, and each has to point at the domain its name
     * claims. A mis-indexed one would hand a caller the wrong compliance
     * regime with no error anywhere. */
    struct { const ca_domain_context_t *(*fn)(void); const char *name; } all[] = {
        { ca_accessibility_domain_context, "accessibility" },
        { ca_agriculture_domain_context, "agriculture" },
        { ca_beauty_domain_context, "beauty" },
        { ca_business_domain_context, "business" },
        { ca_civic_domain_context, "civic" },
        { ca_commerce_domain_context, "commerce" },
        { ca_commerce_accounting_domain_context, "commerce_accounting" },
        { ca_commerce_finance_domain_context, "commerce_finance" },
        { ca_commerce_integration_pay_fast_domain_context, "commerce_integration_pay_fast" },
        { ca_commerce_integration_xero_domain_context, "commerce_integration_xero" },
        { ca_community_domain_context, "community" },
        { ca_construction_domain_context, "construction" },
        { ca_creative_domain_context, "creative" },
        { ca_education_domain_context, "education" },
        { ca_elderly_domain_context, "elderly" },
        { ca_energy_domain_context, "energy" },
        { ca_faith_domain_context, "faith" },
        { ca_family_domain_context, "family" },
        { ca_fitness_domain_context, "fitness" },
        { ca_food_domain_context, "food" },
        { ca_gaming_domain_context, "gaming" },
        { ca_hr_domain_context, "hr" },
        { ca_healthcare_domain_context, "healthcare" },
        { ca_home_domain_context, "home" },
        { ca_hospitality_domain_context, "hospitality" },
        { ca_kids_domain_context, "kids" },
        { ca_legal_domain_context, "legal" },
        { ca_logistics_domain_context, "logistics" },
        { ca_media_domain_context, "media" },
        { ca_parenting_domain_context, "parenting" },
        { ca_personal_domain_context, "personal" },
        { ca_personal_finance_domain_context, "personal_finance" },
        { ca_personal_health_domain_context, "personal_health" },
        { ca_personal_mental_domain_context, "personal_mental" },
        { ca_pets_domain_context, "pets" },
        { ca_real_estate_domain_context, "real_estate" },
        { ca_relationships_domain_context, "relationships" },
        { ca_retail_domain_context, "retail" },
        { ca_safety_domain_context, "safety" },
        { ca_safety_child_domain_context, "safety_child" },
        { ca_social_domain_context, "social" },
        { ca_sports_domain_context, "sports" },
        { ca_tourism_domain_context, "tourism" },
        { ca_travel_domain_context, "travel" },
    };
    const size_t n = sizeof(all) / sizeof(all[0]);
    assert(n == ca_domain_context_count());

    for (size_t i = 0; i < n; i++) {
        const ca_domain_context_t *d = all[i].fn();
        assert(d);
        assert(strcmp(d->domain, all[i].name) == 0);
        assert(d == ca_domain_context_find(all[i].name));
    }
    printf("  named_accessors_all_resolve: ok\n");
}

int main(void) {
    printf("test_domain_context\n");
    test_table_is_complete();
    test_every_domain_is_named_once();
    test_lookup_by_name();
    test_an_unknown_domain_is_null_not_a_fallback();
    test_the_snippet_says_which_domain_it_is();
    test_enrich_is_snippet_blank_line_message();
    test_an_empty_turn_still_gets_the_snippet();
    test_enrich_handles_a_long_message();
    test_enrich_refuses_a_null_context();
    test_flags_and_tools_are_queryable();
    test_popia_reaches_every_domain_except_three();
    test_named_accessors_all_resolve();
    printf("test_domain_context: all ok\n");
    return 0;
}
