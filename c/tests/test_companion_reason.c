/*
 * test_companion_reason.c — companion reasoning core (C11).
 *
 * FrequencyWorldModel + BayesianWorldModel + HistogramPredictiveEngine +
 * SequencePredictiveEngine + TemplateInnerMonologue + ReasoningLoopInnerMonologue
 * + BeliefTrackerTheoryOfMind. Ported from the C# reference (CircleAI.Companion);
 * belief-JSON strings and probabilities are asserted against C#-generated
 * reference values.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include <math.h>
#include "circle_ai/circle_ai.h"

static bool approx(double a, double b) { return fabs(a - b) < 1e-9; }

/* ---- fixed UTC epoch helpers (avoid wall-clock in deterministic tests) ---- */
/* 2021-06-14T13:00:00Z is a Monday. Unix ms. */
#define MON_1300Z 1623675600000LL

/* ===========================================================================
 * FrequencyWorldModel
 * =========================================================================== */
static void test_frequency_world_model(void) {
    ca_frequency_world_model_t *m = ca_frequency_world_model_create();
    assert(m);

    const char *rain[] = { "weather=rainy" };
    ca_frequency_world_model_observe(m, rain, 1, "carry_umbrella");
    ca_frequency_world_model_observe(m, rain, 1, "carry_umbrella");
    ca_frequency_world_model_observe(m, rain, 1, "stay_in");

    /* predict {"weather":"rainy"} → carry_umbrella, 2/3, supporters=[weather=rainy] */
    ca_causal_prediction_t p;
    assert(ca_frequency_world_model_predict(m, "{\"weather\":\"rainy\"}", &p));
    assert(strcmp(p.outcome, "carry_umbrella") == 0);
    assert(approx(p.probability, 2.0 / 3.0));
    assert(p.factor_count == 1);
    assert(strcmp(p.supporting_factors[0], "weather=rainy") == 0);
    ca_causal_prediction_free(&p);

    /* no matching observation → ("unknown", 0.5, []) */
    assert(ca_frequency_world_model_predict(m, "{\"weather\":\"sunny\"}", &p));
    assert(strcmp(p.outcome, "unknown") == 0);
    assert(p.probability == 0.5);
    assert(p.factor_count == 0);
    ca_causal_prediction_free(&p);

    /* malformed JSON → empty observations → unknown */
    assert(ca_frequency_world_model_predict(m, "{not json", &p));
    assert(strcmp(p.outcome, "unknown") == 0 && p.probability == 0.5 && p.factor_count == 0);
    ca_causal_prediction_free(&p);

    /* non-object JSON → unknown */
    assert(ca_frequency_world_model_predict(m, "[1,2,3]", &p));
    assert(strcmp(p.outcome, "unknown") == 0);
    ca_causal_prediction_free(&p);

    /* case-insensitive observation keys (OrdinalIgnoreCase) */
    assert(ca_frequency_world_model_predict(m, "{\"Weather\":\"Rainy\"}", &p));
    assert(strcmp(p.outcome, "carry_umbrella") == 0);
    /* supporter preserves the queried spelling */
    assert(strcmp(p.supporting_factors[0], "Weather=Rainy") == 0);
    ca_causal_prediction_free(&p);

    /* NULL out / model guards */
    assert(!ca_frequency_world_model_predict(m, "{}", NULL));
    assert(!ca_frequency_world_model_predict(NULL, "{}", &p));

    ca_frequency_world_model_destroy(m);
    printf("  frequency_world_model: ok\n");
}

/* ===========================================================================
 * BayesianWorldModel
 * =========================================================================== */
static void test_bayesian_world_model(void) {
    /* alpha <= 0 rejected */
    assert(ca_bayesian_world_model_create(0.0) == NULL);
    assert(ca_bayesian_world_model_create(-1.0) == NULL);

    ca_bayesian_world_model_t *m = ca_bayesian_world_model_create(1.0);
    assert(m);

    /* untrained → unknown/0.5/empty */
    ca_causal_prediction_t p;
    assert(ca_bayesian_world_model_predict(m, "{\"a\":\"1\"}", &p));
    assert(strcmp(p.outcome, "unknown") == 0 && p.probability == 0.5 && p.factor_count == 0);
    ca_causal_prediction_free(&p);

    const char *rain[] = { "weather=rainy", "season=autumn" };
    const char *sun[]  = { "weather=sunny", "season=summer" };
    for (int i = 0; i < 5; ++i) ca_bayesian_world_model_observe(m, rain, 2, "umbrella");
    for (int i = 0; i < 5; ++i) ca_bayesian_world_model_observe(m, sun, 2, "sunglasses");
    ca_bayesian_world_model_observe(m, rain, 2, "sunglasses"); /* one noisy example */

    /* rainy scenario → umbrella is the MAP outcome */
    assert(ca_bayesian_world_model_predict(m, "{\"weather\":\"rainy\",\"season\":\"autumn\"}", &p));
    assert(strcmp(p.outcome, "umbrella") == 0);
    assert(p.probability > 0.5 && p.probability <= 1.0);
    /* SupportingFactors == extracted observations, in property order */
    assert(p.factor_count == 2);
    assert(strcmp(p.supporting_factors[0], "weather=rainy") == 0);
    assert(strcmp(p.supporting_factors[1], "season=autumn") == 0);
    ca_causal_prediction_free(&p);

    /* sunny scenario → sunglasses */
    assert(ca_bayesian_world_model_predict(m, "{\"weather\":\"sunny\",\"season\":\"summer\"}", &p));
    assert(strcmp(p.outcome, "sunglasses") == 0);
    ca_causal_prediction_free(&p);

    /* empty-observation scenario ({}) with a trained model → unknown/0.5 */
    assert(ca_bayesian_world_model_predict(m, "{}", &p));
    assert(strcmp(p.outcome, "unknown") == 0 && p.probability == 0.5 && p.factor_count == 0);
    ca_causal_prediction_free(&p);

    /* probabilities across the two trained outcomes softmax-normalise to 1. */
    assert(ca_bayesian_world_model_predict(m, "{\"weather\":\"rainy\"}", &p));
    double pr_umbrella = p.probability;
    assert(strcmp(p.outcome, "umbrella") == 0);
    ca_causal_prediction_free(&p);
    assert(pr_umbrella > 0.0 && pr_umbrella < 1.0);

    ca_bayesian_world_model_destroy(m);
    printf("  bayesian_world_model: ok\n");
}

/* ===========================================================================
 * HistogramPredictiveEngine
 * =========================================================================== */
static void test_histogram_predictive_engine(void) {
    ca_histogram_predictive_engine_t *e = ca_histogram_predictive_engine_create();
    assert(e);

    /* blank description ignored */
    ca_histogram_predictive_engine_observe(e, "   ", MON_1300Z);

    /* Observe "coffee" three times in the Monday-13:00 slot, "email" once. */
    for (int i = 0; i < 3; ++i) ca_histogram_predictive_engine_observe(e, "coffee", MON_1300Z);
    ca_histogram_predictive_engine_observe(e, "email", MON_1300Z);

    /* horizon <= 0 → error (NULL + SIZE_MAX) */
    size_t n = 0;
    assert(ca_histogram_predictive_engine_anticipate(e, 0, MON_1300Z, &n) == NULL);
    assert(n == (size_t)-1);
    assert(ca_histogram_predictive_engine_anticipate(e, -5, MON_1300Z, &n) == NULL && n == (size_t)-1);

    /* Anticipate with a 20-min horizon: the loop steps only m=0 (30 > 20), so a
     * single slot is summed → coffee/email each prob 1.0 (upcoming==total). */
    ca_anticipated_need_t *needs = ca_histogram_predictive_engine_anticipate(e, 20, MON_1300Z, &n);
    assert(n == 2);
    bool saw_coffee = false, saw_email = false;
    for (size_t i = 0; i < n; ++i) {
        assert(approx(needs[i].probability, 1.0));
        assert(needs[i].expected_by_ms == MON_1300Z + (int64_t)(20 / 2) * 60000);
        if (strcmp(needs[i].description, "coffee") == 0) saw_coffee = true;
        if (strcmp(needs[i].description, "email") == 0)  saw_email = true;
    }
    assert(saw_coffee && saw_email);
    ca_anticipated_need_free_array(needs, n);

    /* Faithful double-count quirk: a 60-min horizon steps m=0,30,60. m=0 and m=30
     * both land in hour 13 (counting that slot twice) while m=60 is hour 14
     * (empty). So coffee upcoming = 3+3+0 = 6 vs total 3 → probability 2.0 (the C#
     * sums per-step without de-duplicating slots, so it can exceed 1.0). */
    needs = ca_histogram_predictive_engine_anticipate(e, 60, MON_1300Z, &n);
    for (size_t i = 0; i < n; ++i)
        if (strcmp(needs[i].description, "coffee") == 0)
            assert(approx(needs[i].probability, 2.0));
    ca_anticipated_need_free_array(needs, n);

    /* Anticipate at a DIFFERENT hour (3h later, no observations there) → empty. */
    int64_t later = MON_1300Z + 3LL * 3600 * 1000;
    needs = ca_histogram_predictive_engine_anticipate(e, 20, later, &n);
    assert(n == 0 && needs == NULL);

    /* An engine with observations spread so probability < 1: coffee also at a
     * neighbouring hour that the horizon does NOT reach. */
    int64_t plus1h = MON_1300Z + 3600LL * 1000; /* 14:00 slot */
    ca_histogram_predictive_engine_observe(e, "coffee", plus1h); /* total now 4, in-slot 3 */
    needs = ca_histogram_predictive_engine_anticipate(e, 20, MON_1300Z, &n); /* 20min stays in 13:00 */
    /* find coffee: upcoming=3 (13:00 slot only), total=4 → 0.75 */
    for (size_t i = 0; i < n; ++i)
        if (strcmp(needs[i].description, "coffee") == 0)
            assert(approx(needs[i].probability, 0.75));
    ca_anticipated_need_free_array(needs, n);

    printf("  histogram_predictive_engine: ok\n");
    ca_histogram_predictive_engine_destroy(e);
}

/* ===========================================================================
 * SequencePredictiveEngine
 * =========================================================================== */
static void test_sequence_predictive_engine(void) {
    /* order bounds */
    assert(ca_sequence_predictive_engine_create(0) == NULL);
    assert(ca_sequence_predictive_engine_create(7) == NULL);

    ca_sequence_predictive_engine_t *e = ca_sequence_predictive_engine_create(3);
    assert(e);

    /* empty timeline → empty (not error) */
    size_t n = 0;
    assert(ca_sequence_predictive_engine_anticipate(e, 60, 0, &n) == NULL && n == 0);

    /* horizon <= 0 → error */
    assert(ca_sequence_predictive_engine_anticipate(e, 0, 0, &n) == NULL && n == (size_t)-1);

    /* Scenario A: cycle wake→coffee→work repeated, 60s cadence. No event is ever
     * immediately preceded by itself, so interArrivals stays EMPTY. The only
     * learned continuation of "work" is "wake" → sole prediction wake, prob 1.0,
     * and (absent inter-arrival) meanInterval = horizonSec*0.5. */
    int64_t t = 1000000LL;
    const char *cycle[] = { "wake", "coffee", "work" };
    for (int rep = 0; rep < 4; ++rep)
        for (int j = 0; j < 3; ++j) {
            ca_sequence_predictive_engine_observe(e, cycle[j], t);
            t += 60000; /* +60s */
        }
    int64_t now = t; /* = 1720000 */
    ca_anticipated_need_t *needs = ca_sequence_predictive_engine_anticipate(e, 60, now, &n);
    assert(n == 1);
    assert(strcmp(needs[0].description, "wake") == 0);
    assert(approx(needs[0].probability, 1.0));
    /* meanInterval = 3600*0.5 = 1800s → expected_by = now + 1800000ms */
    assert(needs[0].expected_by_ms == now + 1800000LL);
    ca_anticipated_need_free_array(needs, n);

    /* blank event ignored */
    ca_sequence_predictive_engine_observe(e, "  ", now);

    ca_sequence_predictive_engine_destroy(e);

    /* Scenario B: consecutive "ping" repeats at 100s spacing so interArrival mean
     * is 100s. A 1-min (60s) horizon filters ping out (100 > 60); a 2-min (120s)
     * horizon keeps it (100 <= 120), prob 1.0. */
    ca_sequence_predictive_engine_t *e2 = ca_sequence_predictive_engine_create(3);
    assert(e2);
    int64_t tb = 5000000LL;
    for (int i = 0; i < 4; ++i) { ca_sequence_predictive_engine_observe(e2, "ping", tb); tb += 100000; }
    int64_t nowb = tb;

    needs = ca_sequence_predictive_engine_anticipate(e2, 1, nowb, &n);
    assert(n == 0 && needs == NULL); /* filtered by horizon */

    needs = ca_sequence_predictive_engine_anticipate(e2, 2, nowb, &n);
    assert(n == 1);
    assert(strcmp(needs[0].description, "ping") == 0);
    assert(approx(needs[0].probability, 1.0));
    assert(needs[0].expected_by_ms == nowb + 100000LL); /* mean interval 100s */
    ca_anticipated_need_free_array(needs, n);

    ca_sequence_predictive_engine_destroy(e2);
    printf("  sequence_predictive_engine: ok\n");
}

/* ===========================================================================
 * TemplateInnerMonologue
 * =========================================================================== */
static void test_template_inner_monologue(void) {
    ca_self_reflection_t r;

    /* error keyword → "diagnose the failure first" appears in the thought */
    assert(ca_template_inner_monologue_reflect("{\"status\":\"error occurred\"}", 42, &r));
    assert(strstr(r.thought, "diagnose the failure first") != NULL);
    /* summary contains the stripped tokens (no braces/quotes) */
    assert(strstr(r.thought, "status") != NULL);
    assert(strstr(r.thought, "error") != NULL);
    assert(r.at_ms == 42);
    ca_self_reflection_free(&r);

    /* goal keyword (no error) → advance toward the stated goal */
    assert(ca_template_inner_monologue_reflect("{\"goal\":\"ship it\"}", 1, &r));
    assert(strstr(r.thought, "advance toward the stated goal") != NULL);
    ca_self_reflection_free(&r);

    /* user keyword (no error/goal) → respond to the user */
    assert(ca_template_inner_monologue_reflect("{\"user\":\"alice\"}", 1, &r));
    assert(strstr(r.thought, "respond to the user") != NULL);
    ca_self_reflection_free(&r);

    /* none → gather more context */
    assert(ca_template_inner_monologue_reflect("{\"weather\":\"nice\"}", 1, &r));
    assert(strstr(r.thought, "gather more context") != NULL);
    ca_self_reflection_free(&r);

    /* priority: error beats goal beats user */
    assert(ca_template_inner_monologue_reflect("{\"goal\":\"x\",\"error\":\"y\",\"user\":\"z\"}", 1, &r));
    assert(strstr(r.thought, "diagnose the failure first") != NULL);
    ca_self_reflection_free(&r);

    /* summary keeps only first 12 tokens */
    assert(ca_template_inner_monologue_reflect(
        "a b c d e f g h i j k l m n o p", 1, &r));
    /* token 13+ ("m","n","o","p") must NOT be in the summary portion; but the
     * frame text is fixed. Check "l" present and "m n o p" absent as a run. */
    assert(strstr(r.thought, " l") != NULL || strstr(r.thought, "l ") != NULL);
    assert(strstr(r.thought, "m n o p") == NULL);
    ca_self_reflection_free(&r);

    /* deterministic: same input → same thought twice */
    ca_self_reflection_t r2;
    assert(ca_template_inner_monologue_reflect("{\"k\":\"v\"}", 7, &r));
    assert(ca_template_inner_monologue_reflect("{\"k\":\"v\"}", 7, &r2));
    assert(strcmp(r.thought, r2.thought) == 0);
    ca_self_reflection_free(&r);
    ca_self_reflection_free(&r2);

    /* guards */
    assert(!ca_template_inner_monologue_reflect(NULL, 0, &r));
    assert(!ca_template_inner_monologue_reflect("{}", 0, NULL));

    printf("  template_inner_monologue: ok\n");
}

/* ===========================================================================
 * ReasoningLoopInnerMonologue
 * =========================================================================== */

/* A driver that emits two reasoning fragments then one content fragment,
 * verifying the built message list along the way. */
static void driver_reason_then_content(void *user,
                                       const ca_chat_message_t *messages, size_t message_count,
                                       const ca_generation_options_t *options,
                                       ca_stream_fragment_callback emit, void *sink) {
    (void)user;
    /* messages: [system=reasoning-prompt, user="Context (raw JSON):\n...\n\nReflect..."] */
    assert(message_count == 2);
    assert(messages[0].role == CA_ROLE_SYSTEM);
    assert(strcmp(messages[0].content, ca_reasoning_inner_monologue_system_prompt()) == 0);
    assert(messages[1].role == CA_ROLE_USER);
    assert(strstr(messages[1].content, "Context (raw JSON):") != NULL);
    assert(strstr(messages[1].content, "Reflect on this in 2-3 sentences.") != NULL);
    /* options mirror the C#: MaxTokens=256, Temperature=0.5, IncludeReasoning=1 */
    assert(options->max_tokens == 256);
    assert(options->temperature == 0.5f);
    assert(options->include_reasoning == 1);

    ca_chat_fragment_t f;
    f.kind = CA_CHAT_FRAGMENT_REASONING; f.text = "  The user seems "; emit(&f, sink);
    f.kind = CA_CHAT_FRAGMENT_REASONING; f.text = "tired.  ";           emit(&f, sink);
    f.kind = CA_CHAT_FRAGMENT_CONTENT;   f.text = "You sound weary.";   emit(&f, sink);
}

/* A driver that emits only content. */
static void driver_content_only(void *user,
                                const ca_chat_message_t *messages, size_t message_count,
                                const ca_generation_options_t *options,
                                ca_stream_fragment_callback emit, void *sink) {
    (void)user; (void)messages; (void)message_count; (void)options;
    ca_chat_fragment_t f;
    f.kind = CA_CHAT_FRAGMENT_CONTENT; f.text = "  just an observation  "; emit(&f, sink);
}

/* A driver that emits nothing. */
static void driver_silent(void *user,
                          const ca_chat_message_t *messages, size_t message_count,
                          const ca_generation_options_t *options,
                          ca_stream_fragment_callback emit, void *sink) {
    (void)user; (void)messages; (void)message_count; (void)options; (void)emit; (void)sink;
}

static void test_reasoning_inner_monologue(void) {
    ca_self_reflection_t r;

    /* reasoning fragments preferred, trimmed */
    assert(ca_reasoning_inner_monologue_reflect(driver_reason_then_content, NULL,
                                                "{\"mood\":\"low\"}", 99, &r));
    assert(strcmp(r.thought, "The user seems tired.") == 0);
    assert(r.at_ms == 99);
    ca_self_reflection_free(&r);

    /* content used when no reasoning, trimmed */
    assert(ca_reasoning_inner_monologue_reflect(driver_content_only, NULL,
                                                "{\"x\":1}", 5, &r));
    assert(strcmp(r.thought, "just an observation") == 0);
    ca_self_reflection_free(&r);

    /* silent driver → "(no inner state)" */
    assert(ca_reasoning_inner_monologue_reflect(driver_silent, NULL, "{}", 0, &r));
    assert(strcmp(r.thought, "(no inner state)") == 0);
    ca_self_reflection_free(&r);

    /* guards */
    assert(!ca_reasoning_inner_monologue_reflect(NULL, NULL, "{}", 0, &r));
    assert(!ca_reasoning_inner_monologue_reflect(driver_silent, NULL, NULL, 0, &r));
    assert(!ca_reasoning_inner_monologue_reflect(driver_silent, NULL, "{}", 0, NULL));

    printf("  reasoning_inner_monologue: ok\n");
}

/* ===========================================================================
 * BeliefTrackerTheoryOfMind — asserted against C#-generated reference strings.
 * =========================================================================== */
static void check_belief(const char *history, const char *want_json, double want_conf) {
    ca_other_mind_estimate_t e;
    assert(ca_belief_tracker_theory_of_mind_estimate("bob", history, &e));
    assert(strcmp(e.target_identifier, "bob") == 0);
    if (strcmp(e.likely_belief_json, want_json) != 0) {
        fprintf(stderr, "belief json mismatch:\n  history: %s\n  want:    %s\n  got:     %s\n",
                history, want_json, e.likely_belief_json);
        assert(0);
    }
    assert(approx(e.confidence, want_conf));
    ca_other_mind_estimate_free(&e);
}

static void test_belief_tracker_theory_of_mind(void) {
    /* reference outputs generated from the C# BeliefTrackerTheoryOfMind. */
    check_belief("she believes the sky is blue. he thinks it will rain",
                 "{\"believes:the sky is blue\":1,\"thinks:it will rain\":0.6363636363636364}",
                 0.32727272727272727);

    check_belief("he thinks it will rain. she thinks it will rain again",
                 "{\"thinks:it will rain\":0.7,\"thinks:it will rain again\":0.6363636363636364}",
                 0.2672727272727273);

    check_belief("I want coffee; she fears the dark! he hopes for peace? they think loudly",
                 "{\"want:coffee\":0.7,\"fears:the dark\":0.6363636363636364,"
                 "\"hopes:for peace\":0.5833333333333334,\"think:loudly\":0.5384615384615383}",
                 0.49163170163170167);

    check_belief("nothing to see here", "{}", 0.0);

    check_belief("a believes x. b believes y. c believes z. d believes w. e believes v. f believes u",
                 "{\"believes:x\":1,\"believes:y\":0.9090909090909091,\"believes:z\":0.8333333333333334,"
                 "\"believes:w\":0.7692307692307692,\"believes:v\":0.7142857142857143,"
                 "\"believes:u\":0.6666666666666666}",
                 0.9785214785214785);

    /* apostrophe → ' (JavaScriptEncoder.Default) */
    check_belief("she believes it's cold",
                 "{\"believes:it\\u0027s cold\":1}",
                 0.2);

    /* "beliefs" is NOT "believe(s)" → no match */
    check_belief("he beliefs things", "{}", 0.0);

    /* confidence caps at 1.0 with many beliefs */
    check_belief("a believes one. a believes two. a believes three. a believes four. "
                 "a believes five. a believes six. a believes seven. a believes eight",
                 "{\"believes:one\":1,\"believes:two\":0.9090909090909091,"
                 "\"believes:three\":0.8333333333333334,\"believes:four\":0.7692307692307692,"
                 "\"believes:five\":0.7142857142857143,\"believes:six\":0.6666666666666666,"
                 "\"believes:seven\":0.625,\"believes:eight\":0.588235294117647}",
                 1.0);

    /* guards */
    ca_other_mind_estimate_t e;
    assert(!ca_belief_tracker_theory_of_mind_estimate("   ", "x", &e));   /* blank target */
    assert(!ca_belief_tracker_theory_of_mind_estimate("bob", NULL, &e));  /* NULL history */
    assert(!ca_belief_tracker_theory_of_mind_estimate("bob", "x", NULL)); /* NULL out */

    printf("  belief_tracker_theory_of_mind: ok\n");
}

int main(void) {
    test_frequency_world_model();
    test_bayesian_world_model();
    test_histogram_predictive_engine();
    test_sequence_predictive_engine();
    test_template_inner_monologue();
    test_reasoning_inner_monologue();
    test_belief_tracker_theory_of_mind();
    printf("test_companion_reason: all assertions passed\n");
    return 0;
}
