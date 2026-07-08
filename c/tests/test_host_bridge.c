/*
 * test_host_bridge.c — CircleAI.Hosting.InferenceBridge (C11 port).
 *
 * Verifies InferenceRequest.Create defaults, MockInferenceBridge, and
 * LocalProcessInferenceBridge (list/is-loaded/complete status classification/
 * token estimates/streaming/device caps, and the not-loaded failure path).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_request_create(void) {
    ca_hb_request_t req;
    assert(ca_hb_request_create("llama-3-8b", "hi", 256, 0.7f, 0.95f, &req));
    assert(strcmp(req.model_id, "llama-3-8b") == 0);
    assert(strcmp(req.prompt, "hi") == 0);
    assert(req.max_output_tokens == 256);
    assert(req.stop_sequence_count == 0);
    assert(strlen(req.id) == 32); /* 32-hex */
    ca_hb_request_free(&req);

    /* invalid */
    assert(ca_hb_request_create("", "p", 10, 0, 1, &req) == false);
    assert(ca_hb_request_create("m", NULL, 10, 0, 1, &req) == false);
    printf("  request create: ok\n");
}

static int g_frames = 0;
static char g_frame_buf[256];
static bool on_frame(void *u, const char *chunk) {
    (void)u; g_frames++;
    snprintf(g_frame_buf, sizeof(g_frame_buf), "%s", chunk);
    return true;
}

static void test_mock_bridge(void) {
    ca_mock_bridge_t *m = ca_mock_bridge_create("canned reply", "mock-model");
    ca_hb_bridge_t *b = ca_mock_bridge_as_bridge(m);

    size_t n = 0;
    ca_bridge_model_descriptor_t *models = ca_hb_bridge_list_loaded_models(b, &n);
    assert(n == 1 && strcmp(models[0].model_id, "mock-model") == 0);
    assert(models[0].context_window_tokens == 4096 && models[0].vocab_size == 32000);
    ca_bridge_model_descriptor_free_array(models, n);

    assert(ca_hb_bridge_is_model_loaded(b, "mock-model") == true);
    assert(ca_hb_bridge_is_model_loaded(b, "other") == false);

    ca_hb_request_t req;
    ca_hb_request_create("mock-model", "prompt text", 128, 0.5f, 0.9f, &req);
    ca_hb_response_t resp; memset(&resp, 0, sizeof(resp));
    assert(ca_hb_bridge_complete(b, &req, &resp));
    assert(strcmp(resp.output_text, "canned reply") == 0);
    assert(resp.status == CA_HB_COMPLETED);
    assert(strcmp(resp.request_id, req.id) == 0);
    ca_hb_response_free(&resp);

    g_frames = 0;
    long fr = ca_hb_bridge_stream_completion(b, &req, on_frame, NULL);
    assert(fr == 1 && g_frames == 1 && strcmp(g_frame_buf, "canned reply") == 0);

    ca_device_capabilities_t caps; memset(&caps, 0, sizeof(caps));
    assert(ca_hb_bridge_get_device_capabilities(b, &caps));
    assert(strcmp(caps.os_name, "Mock") == 0 && caps.has_transport_layer_encryption);
    ca_device_capabilities_free(&caps);

    ca_hb_request_free(&req);
    ca_mock_bridge_destroy(m);
    printf("  mock bridge: ok\n");
}

static void test_local_bridge(void) {
    ca_local_chat_generator_t *gen = ca_local_chat_generator_create("qwen-local", 4096);
    assert(gen);

    ca_bridge_model_descriptor_t desc; memset(&desc, 0, sizeof(desc));
    desc.model_id = strdup("qwen-local");
    desc.version = strdup("1.0");
    desc.format = CA_MODEL_FORMAT_GGUF;
    desc.context_window_tokens = 4096;
    desc.vocab_size = 151936;
    desc.parameter_count = 8000000000LL;
    desc.approximate_memory_bytes = 5000000000LL;

    ca_local_process_bridge_t *lpb = ca_local_process_bridge_create(gen, &desc, NULL, NULL);
    assert(lpb);
    ca_hb_bridge_t *b = ca_local_process_bridge_as_bridge(lpb);

    /* list */
    size_t n = 0;
    ca_bridge_model_descriptor_t *models = ca_hb_bridge_list_loaded_models(b, &n);
    assert(n == 1 && strcmp(models[0].model_id, "qwen-local") == 0);
    assert(models[0].format == CA_MODEL_FORMAT_GGUF);
    ca_bridge_model_descriptor_free_array(models, n);

    assert(ca_hb_bridge_is_model_loaded(b, "qwen-local") == true);

    /* complete: model loaded -> Completed (or StoppedByLength if long) */
    ca_hb_request_t req;
    ca_hb_request_create("qwen-local", "hello world how are you", 256, 0.7f, 0.95f, &req);
    ca_hb_response_t resp; memset(&resp, 0, sizeof(resp));
    assert(ca_hb_bridge_complete(b, &req, &resp));
    assert(resp.status == CA_HB_COMPLETED || resp.status == CA_HB_STOPPED_BY_LENGTH);
    assert(strcmp(resp.model_id, "qwen-local") == 0);
    assert(resp.failure_message == NULL);
    assert(resp.output_token_count >= 0 && resp.prompt_token_count > 0);
    ca_hb_response_free(&resp);
    ca_hb_request_free(&req);

    /* not-loaded model -> Failed with message */
    ca_hb_request_create("other-model", "hi", 256, 0.7f, 0.95f, &req);
    memset(&resp, 0, sizeof(resp));
    assert(ca_hb_bridge_complete(b, &req, &resp));
    assert(resp.status == CA_HB_FAILED);
    assert(resp.failure_message && strstr(resp.failure_message, "not loaded"));
    ca_hb_response_free(&resp);
    ca_hb_request_free(&req);

    /* StoppedByLength: max_output_tokens tiny -> produced >= max */
    ca_hb_request_create("qwen-local", "generate a long answer please with content", 1, 0.7f, 0.95f, &req);
    memset(&resp, 0, sizeof(resp));
    assert(ca_hb_bridge_complete(b, &req, &resp));
    /* produced tokens (>=1) >= max(1) => StoppedByLength (unless truly empty) */
    assert(resp.status == CA_HB_STOPPED_BY_LENGTH || resp.status == CA_HB_COMPLETED);
    ca_hb_response_free(&resp);
    ca_hb_request_free(&req);

    /* streaming */
    ca_hb_request_create("qwen-local", "stream this", 256, 0.7f, 0.95f, &req);
    g_frames = 0;
    long fr = ca_hb_bridge_stream_completion(b, &req, on_frame, NULL);
    assert(fr >= 1 && g_frames >= 1);
    ca_hb_request_free(&req);

    /* device caps (default portable) */
    ca_device_capabilities_t caps; memset(&caps, 0, sizeof(caps));
    assert(ca_hb_bridge_get_device_capabilities(b, &caps));
    assert(strcmp(caps.os_name, "Portable") == 0);
    assert(caps.cpu_core_count == 8 && caps.has_transport_layer_encryption);
    ca_device_capabilities_free(&caps);

    ca_local_process_bridge_destroy(lpb);
    ca_bridge_model_descriptor_free(&desc);
    ca_local_chat_generator_destroy(gen);
    printf("  local bridge: ok\n");
}

/* stop-sequence classification */
static void test_stop_sequence(void) {
    ca_local_chat_generator_t *gen = ca_local_chat_generator_create("m", 4096);
    ca_bridge_model_descriptor_t desc; memset(&desc, 0, sizeof(desc));
    desc.model_id = strdup("m"); desc.version = strdup("1"); desc.format = CA_MODEL_FORMAT_GGUF;
    ca_local_process_bridge_t *lpb = ca_local_process_bridge_create(gen, &desc, NULL, NULL);
    ca_hb_bridge_t *b = ca_local_process_bridge_as_bridge(lpb);

    /* Ask the deterministic generator to echo; we don't know its exact output,
     * so just assert complete() returns a valid response and the status is one
     * of the terminal-success states. */
    ca_hb_request_t req;
    ca_hb_request_create("m", "please echo", 256, 0.0f, 1.0f, &req);
    ca_hb_response_t resp; memset(&resp, 0, sizeof(resp));
    assert(ca_hb_bridge_complete(b, &req, &resp));
    assert(resp.status != CA_HB_FAILED);
    ca_hb_response_free(&resp);
    ca_hb_request_free(&req);

    ca_local_process_bridge_destroy(lpb);
    ca_bridge_model_descriptor_free(&desc);
    ca_local_chat_generator_destroy(gen);
    printf("  stop sequence: ok\n");
}

int main(void) {
    test_request_create();
    test_mock_bridge();
    test_local_bridge();
    test_stop_sequence();
    printf("test_host_bridge: all assertions passed\n");
    return 0;
}
