/*
 * test_vision_cloud.c — CircleAI.Vision.Cloud IImageGenerator (C11 port).
 *
 * Verifies NullImageGenerator, the deterministic fake generator (configured vs
 * not, Count clamp 1..4, deterministic clock), and the ImageGeneratorFallbackChain
 * (skip unconfigured, first non-empty wins, DisplayLabel/StatusMessage).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_null_generator(void) {
    ca_image_generator_t g = ca_null_image_generator();
    assert(strcmp(ca_image_generator_id(&g), "null") == 0);
    assert(strcmp(ca_image_generator_display_label(&g), "No image generator") == 0);
    assert(!ca_image_generator_is_configured(&g));
    assert(strstr(ca_image_generator_status_message(&g), "OpenAI:ApiKey") != NULL);

    ca_image_generation_request_t req;
    ca_image_generation_request_init(&req, "a cat");
    assert(req.size == 1024 && req.count == 1);
    size_t n = 99;
    ca_image_artifact_t *arr = ca_image_generator_generate(&g, &req, &n);
    assert(arr == NULL && n == 0);
    printf("  null_generator: ok\n");
}

static void test_fake_generator(void) {
    /* Unconfigured -> empty. */
    ca_fake_image_generator_t *fu = ca_fake_image_generator_create("openai-images", "OpenAI", false, 5000);
    ca_image_generator_t gu = ca_fake_image_generator_as_iface(fu);
    assert(!ca_image_generator_is_configured(&gu));
    ca_image_generation_request_t req;
    ca_image_generation_request_init(&req, "sunset");
    size_t n = 7;
    ca_image_artifact_t *arr = ca_image_generator_generate(&gu, &req, &n);
    assert(arr == NULL && n == 0);
    ca_fake_image_generator_destroy(fu);

    /* Configured -> Count clamped to 1..4, deterministic url + clock. */
    ca_fake_image_generator_t *fc = ca_fake_image_generator_create("stability", "Stability", true, 5000);
    ca_image_generator_t gc = ca_fake_image_generator_as_iface(fc);
    assert(ca_image_generator_is_configured(&gc));
    assert(strcmp(ca_image_generator_id(&gc), "stability") == 0);
    assert(strcmp(ca_image_generator_display_label(&gc), "Stability") == 0);

    req.count = 9;   /* clamps to 4 */
    arr = ca_image_generator_generate(&gc, &req, &n);
    assert(n == 4 && arr);
    for (size_t i = 0; i < n; ++i) {
        assert(strcmp(arr[i].generator_id, "stability") == 0);
        assert(strcmp(arr[i].prompt, "sunset") == 0);
        assert(strcmp(arr[i].mime_type, "image/png") == 0);
        assert(arr[i].url && strncmp(arr[i].url, "mem://stability/sunset/", 23) == 0);
        assert(arr[i].bytes == NULL);
        assert(arr[i].generated_at_utc_ms == 5000);
    }
    ca_image_artifact_free_array(arr, n);

    req.count = 0;   /* clamps to 1 */
    arr = ca_image_generator_generate(&gc, &req, &n);
    assert(n == 1 && arr);
    ca_image_artifact_free_array(arr, n);
    ca_fake_image_generator_destroy(fc);
    printf("  fake_generator: ok\n");
}

static void test_fallback_chain(void) {
    /* Chain: [unconfigured-A, configured-B, configured-C]. B serves first. */
    ca_fake_image_generator_t *a = ca_fake_image_generator_create("a", "A", false, 1);
    ca_fake_image_generator_t *b = ca_fake_image_generator_create("b", "B", true, 2);
    ca_fake_image_generator_t *c = ca_fake_image_generator_create("c", "C", true, 3);
    ca_image_generator_t members[3] = {
        ca_fake_image_generator_as_iface(a),
        ca_fake_image_generator_as_iface(b),
        ca_fake_image_generator_as_iface(c),
    };
    ca_image_generator_fallback_chain_t *chain =
        ca_image_generator_fallback_chain_create(members, 3, false);
    assert(ca_image_generator_fallback_chain_count(chain) == 3);
    ca_image_generator_t cg = ca_image_generator_fallback_chain_as_iface(chain);

    assert(strcmp(ca_image_generator_id(&cg), "fallback-chain") == 0);
    assert(strcmp(ca_image_generator_display_label(&cg), "Fallback (3)") == 0);
    assert(ca_image_generator_is_configured(&cg));
    /* StatusMessage lists configured ids in order: "Ready · b → c". */
    const char *status = ca_image_generator_status_message(&cg);
    assert(strstr(status, "Ready") != NULL);
    assert(strstr(status, "b") != NULL && strstr(status, "c") != NULL);

    ca_image_generation_request_t req;
    ca_image_generation_request_init(&req, "moon");
    size_t n = 0;
    ca_image_artifact_t *arr = ca_image_generator_generate(&cg, &req, &n);
    assert(n == 1 && arr);
    /* First non-empty came from B (2), NOT the unconfigured A nor C. */
    assert(strcmp(arr[0].generator_id, "b") == 0);
    assert(arr[0].generated_at_utc_ms == 2);
    ca_image_artifact_free_array(arr, n);

    ca_image_generator_fallback_chain_destroy(chain);
    ca_fake_image_generator_destroy(a);
    ca_fake_image_generator_destroy(b);
    ca_fake_image_generator_destroy(c);

    /* All-unconfigured chain -> empty result + "No configured generator" status. */
    ca_fake_image_generator_t *u1 = ca_fake_image_generator_create("u1", "U1", false, 1);
    ca_image_generator_t only = ca_fake_image_generator_as_iface(u1);
    ca_image_generator_fallback_chain_t *empty =
        ca_image_generator_fallback_chain_create(&only, 1, false);
    ca_image_generator_t eg = ca_image_generator_fallback_chain_as_iface(empty);
    assert(!ca_image_generator_is_configured(&eg));
    assert(strcmp(ca_image_generator_status_message(&eg), "No configured generator in chain.") == 0);
    arr = ca_image_generator_generate(&eg, &req, &n);
    assert(arr == NULL && n == 0);
    ca_image_generator_fallback_chain_destroy(empty);
    ca_fake_image_generator_destroy(u1);
    printf("  fallback_chain: ok\n");
}

/* Chain with own=true must destroy its members on destroy (leak-checked by ASan
 * in CI; here we just confirm it runs cleanly with heap-backed fakes). */
static void test_fallback_chain_owning(void) {
    ca_fake_image_generator_t *b = ca_fake_image_generator_create("b", "B", true, 2);
    /* Wrap the fake in an owning iface by supplying a destroy thunk. */
    ca_image_generator_t iface = ca_fake_image_generator_as_iface(b);
    /* as_iface leaves destroy NULL; emulate an owning member by setting it. */
    iface.destroy = (void (*)(void *))ca_fake_image_generator_destroy;
    iface.self = b;
    ca_image_generator_fallback_chain_t *chain =
        ca_image_generator_fallback_chain_create(&iface, 1, true);
    /* destroy the chain -> it calls destroy(b). */
    ca_image_generator_fallback_chain_destroy(chain);
    printf("  fallback_chain_owning: ok\n");
}

int main(void) {
    test_null_generator();
    test_fake_generator();
    test_fallback_chain();
    test_fallback_chain_owning();
    printf("test_vision_cloud: all assertions passed\n");
    return 0;
}
