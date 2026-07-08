/*
 * test_inference_server.c — CircleAI.Inference.Server contracts + handlers.
 *
 * Mirrors the C# server behaviour: backend/tier parsing, the model registry
 * (register/resolve/deregister/list, chat+embed), the lifecycle-manager
 * admission gate (already-loaded / insufficient VRAM / insufficient RAM /
 * factory-failed / loaded + unload), the companion session resolver (cache +
 * single-flight + no-poison-on-failure), NativeRuntimeStatus, the ApiKeyAuth
 * handler (disabled passthrough / no-result / fail / constant-time match), and
 * the /v1/chat/completions + /v1/embeddings routing (validation → resolve →
 * bridge → OpenAI-shaped response, plus the streaming frames).
 */

#include "circle_ai/inference_server.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>

/* ─────────────── parse ─────────────── */

static void test_parse(void) {
    ca_backend_kind_t b;
    assert(ca_backend_kind_parse("cpu", &b) && b == CA_BACKEND_CPU);
    assert(ca_backend_kind_parse("CUDA", &b) && b == CA_BACKEND_CUDA);
    assert(ca_backend_kind_parse("Metal", &b) && b == CA_BACKEND_METAL);
    assert(!ca_backend_kind_parse("nope", &b));

    ca_capability_tier_t t;
    assert(ca_capability_tier_parse("Tier0_Tiny", &t) && t == CA_TIER0_TINY);
    assert(ca_capability_tier_parse("tier4_frontier", &t) && t == CA_TIER4_FRONTIER);
    assert(!ca_capability_tier_parse("Tier9", &t));
}

/* ─────────────── registry ─────────────── */

static void test_registry(void) {
    ca_inference_server_registry_t *r = ca_inference_server_registry_create();
    assert(r);

    assert(ca_inference_server_registry_resolve(r, "m1") == NULL);

    ca_inference_bridge_t *b1 = ca_echo_inference_bridge_create();
    assert(ca_inference_server_registry_register(r, "m1", b1));
    assert(ca_inference_server_registry_resolve(r, "m1") == b1);

    /* re-register replaces + destroys the old bridge (no leak/dbl-free) */
    ca_inference_bridge_t *b1b = ca_echo_inference_bridge_create();
    assert(ca_inference_server_registry_register(r, "m1", b1b));
    assert(ca_inference_server_registry_resolve(r, "m1") == b1b);

    ca_text_embedder_t *e1 = ca_hashing_text_embedder_create(8);
    assert(ca_inference_server_registry_register_embedder(r, "emb", e1));
    assert(ca_inference_server_registry_resolve_embedder(r, "emb") == e1);

    size_t chat_n = 0, all_n = 0;
    char **chat = ca_inference_server_registry_chat_model_ids(r, &chat_n);
    assert(chat_n == 1 && strcmp(chat[0], "m1") == 0);
    for (size_t i = 0; i < chat_n; i++) free(chat[i]);
    free(chat);

    char **all = ca_inference_server_registry_all_model_ids(r, &all_n);
    assert(all_n == 2);
    for (size_t i = 0; i < all_n; i++) free(all[i]);
    free(all);

    assert(ca_inference_server_registry_deregister(r, "m1"));
    assert(ca_inference_server_registry_resolve(r, "m1") == NULL);
    assert(!ca_inference_server_registry_deregister(r, "m1"));

    ca_inference_server_registry_destroy(r); /* destroys the embedder */
}

/* ─────────────── lifecycle manager ─────────────── */

static void test_lifecycle(void) {
    ca_inference_server_registry_t *reg = ca_inference_server_registry_create();
    /* 8 GiB RAM, 4 GiB VRAM */
    ca_model_lifecycle_manager_t *m = ca_model_lifecycle_manager_create(
        reg, (int64_t)8 << 30, (int64_t)4 << 30);
    assert(m);

    ca_bridge_factory_t *echo = ca_echo_bridge_factory_create();

    /* CPU load: RAM gate only. Load 2 GiB. */
    ca_load_result_t res;
    assert(ca_model_lifecycle_manager_load(
        m, "qwen", CA_BACKEND_CPU, CA_TIER1_SMALL,
        0, (int64_t)2 << 30, echo, &res));
    assert(res.outcome == CA_LOAD_LOADED);
    assert(res.has_state);
    assert(strcmp(res.state.model_id, "qwen") == 0);
    ca_load_result_free(&res);
    assert(ca_inference_server_registry_resolve(reg, "qwen") != NULL);
    assert(ca_model_lifecycle_manager_total_ram(m) == (int64_t)2 << 30);

    /* Idempotent: same id -> AlreadyLoaded. */
    assert(ca_model_lifecycle_manager_load(
        m, "qwen", CA_BACKEND_CPU, CA_TIER1_SMALL, 0, (int64_t)2 << 30, echo, &res));
    assert(res.outcome == CA_LOAD_ALREADY_LOADED);
    ca_load_result_free(&res);

    /* Insufficient RAM: request 10 GiB (only ~6 left). */
    assert(ca_model_lifecycle_manager_load(
        m, "huge", CA_BACKEND_CPU, CA_TIER4_FRONTIER, 0, (int64_t)10 << 30, echo, &res));
    assert(res.outcome == CA_LOAD_INSUFFICIENT_RAM);
    assert(!res.has_state);
    ca_load_result_free(&res);

    /* Insufficient VRAM: CUDA backend needs 8 GiB, only 4 GiB VRAM. */
    assert(ca_model_lifecycle_manager_load(
        m, "gpu", CA_BACKEND_CUDA, CA_TIER3_LARGE, (int64_t)8 << 30, (int64_t)1 << 30, echo, &res));
    assert(res.outcome == CA_LOAD_INSUFFICIENT_VRAM);
    ca_load_result_free(&res);

    /* Factory failure: unconfigured factory refuses -> FactoryFailed, no leak. */
    ca_bridge_factory_t *unconf = ca_unconfigured_bridge_factory_create();
    assert(ca_model_lifecycle_manager_load(
        m, "willfail", CA_BACKEND_CPU, CA_TIER0_TINY, 0, (int64_t)1 << 30, unconf, &res));
    assert(res.outcome == CA_LOAD_FACTORY_FAILED);
    ca_load_result_free(&res);
    /* reservation rolled back */
    assert(ca_model_lifecycle_manager_total_ram(m) == (int64_t)2 << 30);

    /* list shows exactly the one loaded model */
    size_t ln = 0;
    ca_model_load_state_t *list = ca_model_lifecycle_manager_list(m, &ln);
    assert(ln == 1 && strcmp(list[0].model_id, "qwen") == 0);
    ca_model_load_states_free(list, ln);

    /* unload */
    assert(ca_model_lifecycle_manager_unload(m, "qwen") == CA_UNLOAD_UNLOADED);
    assert(ca_model_lifecycle_manager_unload(m, "qwen") == CA_UNLOAD_NOT_LOADED);
    assert(ca_inference_server_registry_resolve(reg, "qwen") == NULL);
    assert(ca_model_lifecycle_manager_total_ram(m) == 0);

    ca_bridge_factory_destroy(unconf);
    ca_bridge_factory_destroy(echo);
    ca_model_lifecycle_manager_destroy(m);
    ca_inference_server_registry_destroy(reg);
}

/* ─────────────── companion session resolver ─────────────── */

typedef struct { int create_calls; int destroy_calls; bool fail_next; } sess_ctx;

static void *sess_create(void *state, const char *identity_id) {
    sess_ctx *c = (sess_ctx *)state;
    c->create_calls++;
    if (c->fail_next) return NULL;
    /* return an owned "session" carrying the identity id */
    char *sess = malloc(strlen(identity_id) + 1);
    if (sess) strcpy(sess, identity_id);
    return sess;
}
static void sess_destroy(void *session) { free(session); }
static void sess_state_destroy(void *state) { (void)state; }

static void test_companion_resolver(void) {
    sess_ctx ctx = {0};
    ca_companion_session_factory_vtable_t vt = {
        sess_create, sess_destroy, sess_state_destroy, &ctx
    };
    ca_companion_session_resolver_t *r = ca_companion_session_resolver_create(vt);
    assert(r);
    assert(ca_companion_session_resolver_cached_count(r) == 0);

    void *s1 = ca_companion_session_resolver_resolve(r, "sess-1", "id-1");
    assert(s1 && strcmp((char *)s1, "id-1") == 0);
    assert(ctx.create_calls == 1);
    assert(ca_companion_session_resolver_cached_count(r) == 1);

    /* same key -> cached, factory not re-invoked */
    void *s1b = ca_companion_session_resolver_resolve(r, "sess-1", "id-1");
    assert(s1b == s1);
    assert(ctx.create_calls == 1);

    /* different key -> new session */
    void *s2 = ca_companion_session_resolver_resolve(r, "sess-2", "id-2");
    assert(s2 && s2 != s1);
    assert(ctx.create_calls == 2);
    assert(ca_companion_session_resolver_cached_count(r) == 2);

    /* blank ids -> NULL, no create */
    assert(ca_companion_session_resolver_resolve(r, "", "id") == NULL);
    assert(ca_companion_session_resolver_resolve(r, "s", "") == NULL);
    assert(ctx.create_calls == 2);

    /* failed construction does not poison the cache */
    ctx.fail_next = true;
    assert(ca_companion_session_resolver_resolve(r, "sess-3", "id-3") == NULL);
    assert(ca_companion_session_resolver_cached_count(r) == 2);
    ctx.fail_next = false;
    void *s3 = ca_companion_session_resolver_resolve(r, "sess-3", "id-3");
    assert(s3 != NULL);
    assert(ca_companion_session_resolver_cached_count(r) == 3);

    ca_companion_session_resolver_destroy(r);
}

/* ─────────────── native runtime status ─────────────── */

static void test_native_status(void) {
    ca_native_runtime_status_t *s = ca_native_runtime_status_create();
    assert(s);

    ca_native_runtime_paths_t p;
    assert(!ca_native_runtime_status_latest(s, &p)); /* nothing yet */
    assert(p.mnnbridge_path == NULL);

    assert(ca_native_runtime_status_update(s, "/x/mnnbridge.dll", "/x/MNN.dll", "/x/root"));
    assert(ca_native_runtime_status_latest(s, &p));
    assert(strcmp(p.mnnbridge_path, "/x/mnnbridge.dll") == 0);
    assert(strcmp(p.mnn_core_path, "/x/MNN.dll") == 0);
    assert(strcmp(p.extracted_root, "/x/root") == 0);
    ca_native_runtime_paths_free(&p);

    /* update overwrites */
    assert(ca_native_runtime_status_update(s, "/y/b.dll", NULL, "/y/root"));
    assert(ca_native_runtime_status_latest(s, &p));
    assert(strcmp(p.mnnbridge_path, "/y/b.dll") == 0);
    assert(p.mnn_core_path == NULL);
    ca_native_runtime_paths_free(&p);

    ca_native_runtime_status_destroy(s);
}

/* ─────────────── api key auth ─────────────── */

static void test_api_key_auth(void) {
    /* disabled -> anonymous regardless of presented value */
    ca_api_key_auth_t *off = ca_api_key_auth_create(false, "X-Api-Key", NULL, 0);
    assert(off);
    assert(ca_api_key_auth_authenticate(off, NULL) == CA_AUTH_SUCCESS_ANONYMOUS);
    assert(ca_api_key_auth_authenticate(off, "anything") == CA_AUTH_SUCCESS_ANONYMOUS);
    assert(strcmp(ca_api_key_auth_header_name(off), "X-Api-Key") == 0);
    ca_api_key_auth_destroy(off);

    const char *keys[] = { "secret-key-1", "secret-key-2" };
    ca_api_key_auth_t *on = ca_api_key_auth_create(true, "X-Api-Key", keys, 2);
    assert(on);
    /* missing header -> no result */
    assert(ca_api_key_auth_authenticate(on, NULL) == CA_AUTH_NO_RESULT);
    assert(ca_api_key_auth_authenticate(on, "   ") == CA_AUTH_NO_RESULT);
    /* wrong key -> fail */
    assert(ca_api_key_auth_authenticate(on, "wrong") == CA_AUTH_FAIL);
    /* correct keys -> success */
    assert(ca_api_key_auth_authenticate(on, "secret-key-1") == CA_AUTH_SUCCESS);
    assert(ca_api_key_auth_authenticate(on, "secret-key-2") == CA_AUTH_SUCCESS);
    /* length-differing near-miss -> fail */
    assert(ca_api_key_auth_authenticate(on, "secret-key-11") == CA_AUTH_FAIL);
    ca_api_key_auth_destroy(on);
}

/* ─────────────── chat completion routing ─────────────── */

static ca_chat_completion_request_t make_chat_req(const char *model) {
    ca_chat_completion_request_t req;
    memset(&req, 0, sizeof(req));
    req.model = model ? strdup(model) : NULL;
    req.messages = calloc(2, sizeof(ca_chat_completion_message_t));
    req.messages[0].role = strdup("system");
    req.messages[0].content = strdup("You are helpful.");
    req.messages[1].role = strdup("user");
    req.messages[1].content = strdup("Hi there");
    req.message_count = 2;
    return req;
}

static void test_chat_routing(void) {
    ca_inference_server_registry_t *reg = ca_inference_server_registry_create();
    assert(ca_inference_server_registry_register(reg, "echo-model", ca_echo_inference_bridge_create()));

    /* missing model */
    {
        ca_chat_completion_request_t req; memset(&req, 0, sizeof(req));
        req.messages = calloc(1, sizeof(ca_chat_completion_message_t));
        req.messages[0].role = strdup("user");
        req.messages[0].content = strdup("hi");
        req.message_count = 1;
        ca_chat_completion_response_t resp; ca_error_response_t err;
        ca_handler_status_t st = ca_handle_chat_completion(reg, &req, &resp, &err);
        assert(st == CA_HANDLER_BAD_REQUEST);
        assert(err.message && strstr(err.message, "model") != NULL);
        assert(strcmp(err.code, "missing_model") == 0);
        ca_error_response_free(&err);
        ca_chat_completion_request_free(&req);
    }

    /* missing messages */
    {
        ca_chat_completion_request_t req; memset(&req, 0, sizeof(req));
        req.model = strdup("echo-model");
        ca_chat_completion_response_t resp; ca_error_response_t err;
        ca_handler_status_t st = ca_handle_chat_completion(reg, &req, &resp, &err);
        assert(st == CA_HANDLER_BAD_REQUEST);
        assert(strcmp(err.code, "missing_messages") == 0);
        ca_error_response_free(&err);
        ca_chat_completion_request_free(&req);
    }

    /* model not loaded */
    {
        ca_chat_completion_request_t req = make_chat_req("nonexistent");
        ca_chat_completion_response_t resp; ca_error_response_t err;
        ca_handler_status_t st = ca_handle_chat_completion(reg, &req, &resp, &err);
        assert(st == CA_HANDLER_NOT_FOUND);
        assert(strcmp(err.code, "model_not_found") == 0);
        ca_error_response_free(&err);
        ca_chat_completion_request_free(&req);
    }

    /* happy path */
    {
        ca_chat_completion_request_t req = make_chat_req("echo-model");
        ca_chat_completion_response_t resp; ca_error_response_t err;
        ca_handler_status_t st = ca_handle_chat_completion(reg, &req, &resp, &err);
        assert(st == CA_HANDLER_OK);
        assert(resp.choice_count == 1);
        assert(strcmp(resp.choices[0].message.role, "assistant") == 0);
        /* echo bridge prepends "echo:" to the joined prompt */
        assert(strncmp(resp.choices[0].message.content, "echo:", 5) == 0);
        assert(strstr(resp.choices[0].message.content, "Hi there") != NULL);
        assert(strcmp(resp.choices[0].finish_reason, "stop") == 0);
        assert(strcmp(resp.object, "chat.completion") == 0);
        assert(strncmp(resp.id, "chatcmpl-", 9) == 0);
        assert(resp.usage.total_tokens == resp.usage.prompt_tokens + resp.usage.completion_tokens);
        ca_chat_completion_response_free(&resp);
        ca_chat_completion_request_free(&req);
    }

    ca_inference_server_registry_destroy(reg);
}

/* streaming */
typedef struct { int role_frames; int content_frames; int final_frames; char content[256]; } stream_capture;
static void on_delta(const ca_chat_stream_delta_t *d, void *user) {
    stream_capture *c = (stream_capture *)user;
    if (d->is_final) { c->final_frames++; assert(d->finish_reason); return; }
    if (d->text == NULL) { c->role_frames++; return; }
    if (d->kind == 0) { c->content_frames++; snprintf(c->content, sizeof(c->content), "%s", d->text); }
}

static void test_chat_streaming(void) {
    ca_inference_server_registry_t *reg = ca_inference_server_registry_create();
    assert(ca_inference_server_registry_register(reg, "echo-model", ca_echo_inference_bridge_create()));

    ca_chat_completion_request_t req = make_chat_req("echo-model");
    req.stream = true;
    stream_capture cap = {0};
    ca_error_response_t err;
    ca_handler_status_t st = ca_handle_chat_completion_stream(reg, &req, on_delta, &cap, &err);
    assert(st == CA_HANDLER_OK);
    assert(cap.role_frames == 1);   /* leading role frame */
    assert(cap.content_frames == 1);
    assert(cap.final_frames == 1);  /* trailing stop frame */
    assert(strncmp(cap.content, "echo:", 5) == 0);
    ca_chat_completion_request_free(&req);

    /* validation error -> no callback, err filled */
    ca_chat_completion_request_t bad; memset(&bad, 0, sizeof(bad));
    stream_capture cap2 = {0};
    st = ca_handle_chat_completion_stream(reg, &bad, on_delta, &cap2, &err);
    assert(st == CA_HANDLER_BAD_REQUEST);
    assert(cap2.role_frames == 0 && cap2.content_frames == 0);
    ca_error_response_free(&err);
    ca_chat_completion_request_free(&bad);

    ca_inference_server_registry_destroy(reg);
}

/* ─────────────── embeddings routing ─────────────── */

static void test_embeddings_routing(void) {
    ca_inference_server_registry_t *reg = ca_inference_server_registry_create();
    assert(ca_inference_server_registry_register_embedder(reg, "embed-model", ca_hashing_text_embedder_create(16)));

    /* not loaded */
    {
        ca_embeddings_request_t req; memset(&req, 0, sizeof(req));
        req.model = strdup("nope");
        req.inputs = calloc(1, sizeof(char *)); req.inputs[0] = strdup("hi"); req.input_count = 1;
        ca_embeddings_response_t resp; ca_error_response_t err;
        ca_handler_status_t st = ca_handle_embeddings(reg, &req, &resp, &err);
        assert(st == CA_HANDLER_NOT_FOUND);
        ca_error_response_free(&err);
        ca_embeddings_request_free(&req);
    }

    /* happy path: two inputs -> two rows, dim 16 each, unit-normed */
    {
        ca_embeddings_request_t req; memset(&req, 0, sizeof(req));
        req.model = strdup("embed-model");
        req.inputs = calloc(2, sizeof(char *));
        req.inputs[0] = strdup("hello");
        req.inputs[1] = strdup("world foo bar");
        req.input_count = 2;
        ca_embeddings_response_t resp; ca_error_response_t err;
        ca_handler_status_t st = ca_handle_embeddings(reg, &req, &resp, &err);
        assert(st == CA_HANDLER_OK);
        assert(resp.data_count == 2);
        assert(resp.data[0].dim == 16 && resp.data[1].dim == 16);
        assert(resp.data[0].index == 0 && resp.data[1].index == 1);
        assert(strcmp(resp.object, "list") == 0);
        assert(resp.usage.prompt_tokens >= 1);
        assert(resp.usage.completion_tokens == 0);
        /* determinism: same text -> same vector */
        size_t dim = 0;
        ca_text_embedder_t *e = ca_inference_server_registry_resolve_embedder(reg, "embed-model");
        float *v = ca_text_embedder_generate(e, "hello", &dim);
        assert(v && dim == 16);
        for (size_t i = 0; i < 16; i++) assert(v[i] == resp.data[0].embedding[i]);
        free(v);
        ca_embeddings_response_free(&resp);
        ca_embeddings_request_free(&req);
    }

    ca_inference_server_registry_destroy(reg);
}

int main(void) {
    test_parse();
    test_registry();
    test_lifecycle();
    test_companion_resolver();
    test_native_status();
    test_api_key_auth();
    test_chat_routing();
    test_chat_streaming();
    test_embeddings_routing();
    printf("test_inference_server: all passed\n");
    return 0;
}
