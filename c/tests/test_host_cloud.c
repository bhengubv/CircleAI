/*
 * test_host_cloud.c — CircleAI.Hosting.CloudFallback (C11 port).
 *
 * Verifies the fake generator, CloudFallbackChain (start-of-call ordering +
 * fail-soft-frame skipping + sentinel), and BackupBrainOrchestrator
 * (degrade / cool-down / half-open retry, statuses).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_chat_msg_t USER_MSG = { "user", "hello", NULL, 0 };

static void test_fake_generator(void) {
    ca_fake_chat_generator_t *g = ca_fake_chat_generator_create("Groq", true);
    ca_chat_gen_iface_t vi = ca_fake_chat_generator_as_iface(g);
    assert(ca_chat_gen_is_configured(&vi) == true);
    assert(strcmp(ca_chat_gen_engine_label(&vi), "Groq") == 0);
    char *r = ca_chat_gen_generate(&vi, &USER_MSG, 1, NULL);
    assert(r && strcmp(r, "Groq: hello") == 0);
    free(r);
    ca_fake_chat_generator_destroy(g);

    /* unconfigured -> fail-soft frame */
    ca_fake_chat_generator_t *g2 = ca_fake_chat_generator_create("OpenAI", false);
    ca_chat_gen_iface_t vi2 = ca_fake_chat_generator_as_iface(g2);
    assert(ca_chat_gen_is_configured(&vi2) == false);
    r = ca_chat_gen_generate(&vi2, &USER_MSG, 1, NULL);
    assert(r && strstr(r, "not configured"));
    free(r);
    ca_fake_chat_generator_destroy(g2);
    printf("  fake generator: ok\n");
}

static int g_chunks = 0;
static char g_chunk_buf[256];
static bool on_chunk(void *u, const char *chunk) {
    (void)u;
    g_chunks++;
    snprintf(g_chunk_buf, sizeof(g_chunk_buf), "%s", chunk);
    return true;
}

static void test_chain(void) {
    /* [unconfigured OpenAI, configured Groq] -> Groq serves */
    ca_fake_chat_generator_t *openai = ca_fake_chat_generator_create("OpenAI", false);
    ca_fake_chat_generator_t *groq   = ca_fake_chat_generator_create("Groq", true);
    ca_chat_gen_iface_t gens[2] = {
        ca_fake_chat_generator_as_iface(openai),
        ca_fake_chat_generator_as_iface(groq),
    };
    ca_cloud_fallback_chain_t *chain = ca_cloud_fallback_chain_create(gens, 2, false);
    assert(ca_cloud_fallback_chain_count(chain) == 2);

    char *r = ca_cloud_fallback_chain_generate(chain, &USER_MSG, 1, NULL);
    /* OpenAI is unconfigured => IsReady false => skipped; Groq serves */
    assert(r && strcmp(r, "Groq: hello") == 0);
    free(r);

    /* stream: Groq yields a real frame */
    g_chunks = 0;
    long n = ca_cloud_fallback_chain_stream(chain, &USER_MSG, 1, NULL, on_chunk, NULL);
    assert(n == 1 && g_chunks == 1);
    assert(strcmp(g_chunk_buf, "Groq: hello") == 0);

    ca_cloud_fallback_chain_destroy(chain);
    ca_fake_chat_generator_destroy(openai);
    ca_fake_chat_generator_destroy(groq);

    /* all unconfigured -> sentinel */
    ca_fake_chat_generator_t *a = ca_fake_chat_generator_create("A", false);
    ca_fake_chat_generator_t *b = ca_fake_chat_generator_create("B", false);
    ca_chat_gen_iface_t gg[2] = { ca_fake_chat_generator_as_iface(a), ca_fake_chat_generator_as_iface(b) };
    ca_cloud_fallback_chain_t *ch2 = ca_cloud_fallback_chain_create(gg, 2, false);
    r = ca_cloud_fallback_chain_generate(ch2, &USER_MSG, 1, NULL);
    assert(strstr(r, "no configured generator"));
    free(r);
    ca_cloud_fallback_chain_destroy(ch2);
    ca_fake_chat_generator_destroy(a);
    ca_fake_chat_generator_destroy(b);

    /* first generator throws (configured but scripted to fail) -> chain falls
     * through to second */
    ca_fake_chat_generator_t *flaky = ca_fake_chat_generator_create("Flaky", true);
    ca_fake_chat_generator_set_fail_times(flaky, 1);
    ca_fake_chat_generator_t *steady = ca_fake_chat_generator_create("Steady", true);
    ca_chat_gen_iface_t gh[2] = { ca_fake_chat_generator_as_iface(flaky), ca_fake_chat_generator_as_iface(steady) };
    ca_cloud_fallback_chain_t *ch3 = ca_cloud_fallback_chain_create(gh, 2, false);
    r = ca_cloud_fallback_chain_generate(ch3, &USER_MSG, 1, NULL);
    assert(r && strcmp(r, "Steady: hello") == 0);
    free(r);
    ca_cloud_fallback_chain_destroy(ch3);
    ca_fake_chat_generator_destroy(flaky);
    ca_fake_chat_generator_destroy(steady);
    printf("  chain: ok\n");
}

static void test_orchestrator(void) {
    ca_fake_chat_generator_t *primary = ca_fake_chat_generator_create("Primary", true);
    ca_fake_chat_generator_t *backup  = ca_fake_chat_generator_create("Backup", true);
    ca_chat_gen_iface_t brains[2] = {
        ca_fake_chat_generator_as_iface(primary),
        ca_fake_chat_generator_as_iface(backup),
    };
    ca_backup_brain_policy_t pol; ca_backup_brain_policy_init(&pol);
    assert(pol.degraded_after_failures == 2 && pol.max_retries_per_turn == 3);
    ca_backup_brain_orchestrator_t *o = ca_backup_brain_orchestrator_create(brains, 2, &pol, false);

    int64_t t = 100000;
    /* healthy: primary serves */
    char *r = ca_backup_brain_orchestrator_generate(o, &USER_MSG, 1, NULL, t);
    assert(r && strcmp(r, "Primary: hello") == 0);
    free(r);

    /* script primary to fail twice: turn 1 fails-once-then-backup, and mark
     * degraded after 2 consecutive failures */
    ca_fake_chat_generator_set_fail_times(primary, 2);
    /* turn A: primary fails (consecutive=1), backup serves */
    r = ca_backup_brain_orchestrator_generate(o, &USER_MSG, 1, NULL, t);
    assert(r && strcmp(r, "Backup: hello") == 0);
    free(r);
    /* turn B: primary fails again (consecutive=2 -> degraded), backup serves */
    r = ca_backup_brain_orchestrator_generate(o, &USER_MSG, 1, NULL, t);
    assert(r && strcmp(r, "Backup: hello") == 0);
    free(r);

    /* statuses: primary degraded now */
    size_t ns = 0;
    ca_brain_status_t *st = ca_backup_brain_orchestrator_statuses(o, t, &ns);
    assert(ns == 2);
    assert(strcmp(st[0].label, "Primary") == 0);
    assert(st[0].health == CA_BRAIN_DEGRADED);
    assert(st[0].consecutive_failures == 2);
    assert(st[1].health == CA_BRAIN_HEALTHY);
    ca_brain_status_free_array(st, ns);

    /* after cool-down (30s), primary is half-open (CoolingDown) and picked
     * first again; it now succeeds -> healthy */
    int64_t later = t + 31000;
    r = ca_backup_brain_orchestrator_generate(o, &USER_MSG, 1, NULL, later);
    assert(r && strcmp(r, "Primary: hello") == 0);
    free(r);
    st = ca_backup_brain_orchestrator_statuses(o, later, &ns);
    assert(st[0].health == CA_BRAIN_HEALTHY && st[0].consecutive_failures == 0);
    ca_brain_status_free_array(st, ns);

    ca_backup_brain_orchestrator_destroy(o);
    ca_fake_chat_generator_destroy(primary);
    ca_fake_chat_generator_destroy(backup);

    /* all brains fail this turn -> "[All brains failed.]" */
    ca_fake_chat_generator_t *x = ca_fake_chat_generator_create("X", true);
    ca_fake_chat_generator_set_fail_times(x, 5);
    ca_chat_gen_iface_t one[1] = { ca_fake_chat_generator_as_iface(x) };
    ca_backup_brain_orchestrator_t *o2 = ca_backup_brain_orchestrator_create(one, 1, NULL, false);
    char *r2 = ca_backup_brain_orchestrator_generate(o2, &USER_MSG, 1, NULL, 1);
    assert(strcmp(r2, "[All brains failed.]") == 0);
    free(r2);
    ca_backup_brain_orchestrator_destroy(o2);
    ca_fake_chat_generator_destroy(x);
    printf("  orchestrator: ok\n");
}

int main(void) {
    test_fake_generator();
    test_chain();
    test_orchestrator();
    printf("test_host_cloud: all assertions passed\n");
    return 0;
}
