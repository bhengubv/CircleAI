/*
 * test_inference_rt.c — CircleAI.Inference runtime surface (C11 port).
 *
 * Mirrors the C# unit coverage: PowerBudgetPolicy.Resolve mapping +
 * auto-downgrades, VisionInput, deterministic IChatGenerator (generate /
 * stream fragments / structured response / session marker / Qwen prompt),
 * ContextWindowBudgetManager, PrefixCacheService keying + eviction,
 * ModelDownloadService (single-file + bundle + SHA verify + strip-prefix +
 * URLs + manifest), LayerStreaming (discover + orchestrate), and the
 * FeedbackTrainingQueue + NightlyAdapterTrainer.
 */

#include "circle_ai/inference_rt.h"
#include "circle_ai/model_runtime.h"  /* ca_mr_sha256, ca_mr_sha256_hex */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <assert.h>
#include <sys/stat.h>
#if defined(_WIN32)
  #include <direct.h>
  #define TEST_MKDIR(p) _mkdir(p)
#else
  #define TEST_MKDIR(p) mkdir((p), 0777)
#endif

/* ─────────────── scratch dir helpers ─────────────── */

static const char *SCRATCH =
    "C:/Users/tbeng/AppData/Local/Temp/claude/C--Dev-Solutions-com-bhengubv/"
    "e98cefca-6b18-4158-b2da-1afec6f0ed83/scratchpad/inf_rt_test";

/* Recursively create every parent of path (path itself is created too when it
 * has no trailing separator). */
static void ensure_dirs(const char *path) {
    char buf[1024];
    snprintf(buf, sizeof(buf), "%s", path);
    for (char *q = buf + 1; *q; q++) {
        if (*q == '/' || *q == '\\') {
            char saved = *q; *q = 0;
            TEST_MKDIR(buf);
            *q = saved;
        }
    }
    TEST_MKDIR(buf);
}

static void scratch_path(char *out, size_t cap, const char *leaf) {
    snprintf(out, cap, "%s/%s", SCRATCH, leaf);
}

/* Compute the lowercase-hex SHA-256 of bytes into out[65]. */
static void sha_hex(const void *data, size_t len, char out[65]) {
    uint8_t d[32];
    ca_mr_sha256((const uint8_t *)data, len, d);
    ca_mr_sha256_hex(d, out);
}

/* ─────────────── PowerBudgetPolicy ─────────────── */

static void test_power_budget_resolve(void) {
    ca_power_budget_resolution_t r;

    r = ca_power_budget_resolve(CA_POWER_BUDGET_NONE, 5000, -1, false);
    assert(r.max_tokens == 5000);
    assert(r.preferred_kv_mode == CA_KV_TURBO_QUANT_4BIT);
    assert(!r.prefer_smaller_model_in_chain);

    r = ca_power_budget_resolve(CA_POWER_BUDGET_LOW, 5000, -1, false);
    assert(r.max_tokens == 64);
    assert(r.prefer_smaller_model_in_chain);

    r = ca_power_budget_resolve(CA_POWER_BUDGET_NORMAL, 5000, -1, false);
    assert(r.max_tokens == 512);

    r = ca_power_budget_resolve(CA_POWER_BUDGET_HIGH, 5000, -1, false);
    assert(r.max_tokens == 2048);
    assert(r.preferred_kv_mode == CA_KV_OFF);

    /* under-request is honoured (min) */
    r = ca_power_budget_resolve(CA_POWER_BUDGET_NORMAL, 100, -1, false);
    assert(r.max_tokens == 100);

    /* Normal auto-downgrades to Low below 15% battery. */
    r = ca_power_budget_resolve(CA_POWER_BUDGET_NORMAL, 5000, 10, false);
    assert(r.max_tokens == 64);
    assert(r.prefer_smaller_model_in_chain);

    /* At 15% exactly: no downgrade. */
    r = ca_power_budget_resolve(CA_POWER_BUDGET_NORMAL, 5000, 15, false);
    assert(r.max_tokens == 512);

    /* High auto-throttles to Normal on thermal warning. */
    r = ca_power_budget_resolve(CA_POWER_BUDGET_HIGH, 5000, -1, true);
    assert(r.max_tokens == 512);
    assert(r.preferred_kv_mode == CA_KV_TURBO_QUANT_4BIT);
}

/* ─────────────── VisionInput ─────────────── */

static void test_vision_input(void) {
    uint8_t bytes[] = { 1, 2, 3, 4, 5 };
    ca_vision_input_t *v = ca_vision_input_create(bytes, sizeof(bytes), "image/jpeg");
    assert(v);
    assert(v->image_len == 5);
    assert(v->image_bytes[0] == 1 && v->image_bytes[4] == 5);
    assert(v->mime_type && strcmp(v->mime_type, "image/jpeg") == 0);
    /* independent copy */
    bytes[0] = 99;
    assert(v->image_bytes[0] == 1);
    ca_vision_input_destroy(v);

    ca_vision_input_t *v2 = ca_vision_input_create(bytes, sizeof(bytes), NULL);
    assert(v2 && v2->mime_type == NULL);
    ca_vision_input_destroy(v2);

    assert(ca_vision_input_create(NULL, 5, NULL) == NULL);
    assert(ca_vision_input_create(bytes, 0, NULL) == NULL);
}

/* ─────────────── deterministic generator ─────────────── */

typedef struct { int content_frags; int reasoning_frags; char last_content[256]; } frag_capture;

static void on_frag(const ca_chat_fragment_t *f, void *user) {
    frag_capture *c = (frag_capture *)user;
    if (f->kind == CA_CHAT_FRAGMENT_CONTENT) {
        c->content_frags++;
        snprintf(c->last_content, sizeof(c->last_content), "%s", f->text ? f->text : "");
    } else {
        c->reasoning_frags++;
    }
}

static void test_chat_generator(void) {
    ca_local_chat_generator_t *g = ca_local_chat_generator_create("qwen3-0.6b", 4096);
    assert(g);

    ca_chat_msg_t msgs[] = {
        { "system", "Be helpful.", NULL, 0 },
        { "user",   "hello world", NULL, 0 },
    };

    /* generate: content only */
    char *out = ca_local_chat_generator_generate(g, msgs, 2, NULL);
    assert(out);
    assert(strcmp(out, "You said: hello world") == 0);
    free(out);

    /* stream: reasoning + content (include_reasoning default) */
    ca_generation_options_t opts;
    ca_generation_options_init(&opts);
    frag_capture cap = {0};
    assert(ca_local_chat_generator_stream_fragments(g, msgs, 2, &opts, on_frag, &cap));
    assert(cap.reasoning_frags == 1);
    assert(cap.content_frags == 1);
    assert(strcmp(cap.last_content, "You said: hello world") == 0);

    /* include_reasoning = 0 drops the reasoning fragment */
    opts.include_reasoning = 0;
    frag_capture cap2 = {0};
    assert(ca_local_chat_generator_stream_fragments(g, msgs, 2, &opts, on_frag, &cap2));
    assert(cap2.reasoning_frags == 0);
    assert(cap2.content_frags == 1);

    /* structured response */
    ca_chat_gen_response_t resp;
    ca_generation_options_init(&opts);
    assert(ca_local_chat_generator_generate_response(g, msgs, 2, &opts, &resp));
    assert(resp.text && strcmp(resp.text, "You said: hello world") == 0);
    assert(resp.reasoning_content != NULL); /* include_reasoning default */
    assert(resp.finish_reason == CA_FINISH_STOP);
    assert(resp.tokens_out >= 1);
    ca_chat_gen_response_free(&resp);

    /* reasoning dropped when include_reasoning = 0 */
    opts.include_reasoning = 0;
    assert(ca_local_chat_generator_generate_response(g, msgs, 2, &opts, &resp));
    assert(resp.reasoning_content == NULL);
    ca_chat_gen_response_free(&resp);

    ca_local_chat_generator_destroy(g);
}

static void test_qwen_prompt(void) {
    ca_chat_msg_t msgs[] = {
        { "System", "sys", NULL, 0 },
        { "  USER ", "hi", NULL, 0 },
    };
    char *p = ca_build_qwen_chat_prompt(msgs, 2);
    assert(p);
    /* role lowercased + trimmed, ends with an open assistant turn */
    assert(strstr(p, "<|im_start|>system\nsys\n<|im_end|>\n") != NULL);
    assert(strstr(p, "<|im_start|>user\nhi\n<|im_end|>\n") != NULL);
    size_t len = strlen(p);
    const char *tail = "<|im_start|>assistant\n";
    assert(len >= strlen(tail));
    assert(strcmp(p + len - strlen(tail), tail) == 0);
    free(p);
}

static void test_session_marker(void) {
    ca_local_chat_generator_t *g = ca_local_chat_generator_create("m", 512);
    assert(g);
    char path[512];
    scratch_path(path, sizeof(path), "session.bin");
    assert(ca_local_chat_generator_save_session(g, path));
    assert(ca_local_chat_generator_load_session(g, path));
    /* empty path rejected */
    assert(!ca_local_chat_generator_save_session(g, ""));
    /* missing file -> false */
    assert(!ca_local_chat_generator_load_session(g, "C:/nonexistent/nope.bin"));
    ca_local_chat_generator_destroy(g);
}

/* ─────────────── ContextWindowBudgetManager ─────────────── */

static void test_context_budget(void) {
    ca_context_window_budget_t *b = ca_context_window_budget_create(1000, 0.85);
    assert(b);
    assert(ca_context_window_budget_context_size(b) == 1000);
    assert(ca_context_window_budget_used_tokens(b) == 0);
    assert(ca_context_window_budget_remaining_tokens(b) == 1000);
    assert(!ca_context_window_budget_should_evict(b));

    assert(ca_context_window_budget_record_exchange(b, 400, 100));
    assert(ca_context_window_budget_used_tokens(b) == 500);
    assert(fabs(ca_context_window_budget_fill_ratio(b) - 0.5) < 1e-9);
    assert(!ca_context_window_budget_should_evict(b));

    assert(ca_context_window_budget_record_exchange(b, 300, 60));
    assert(ca_context_window_budget_used_tokens(b) == 860);
    assert(ca_context_window_budget_should_evict(b)); /* 0.86 >= 0.85 */

    /* evict back to 0.50 -> drop 860 - 500 = 360 */
    assert(ca_context_window_budget_calculate_eviction_count(b, 0.50) == 360);

    /* already below target -> 0 */
    assert(ca_context_window_budget_calculate_eviction_count(b, 0.95) == 0);

    /* negative counts rejected */
    assert(!ca_context_window_budget_record_exchange(b, -1, 0));

    /* out-of-range target -> -1 */
    assert(ca_context_window_budget_calculate_eviction_count(b, 1.5) == -1);

    ca_context_window_budget_reset(b);
    assert(ca_context_window_budget_used_tokens(b) == 0);
    ca_context_window_budget_destroy(b);

    /* constructor guards */
    assert(ca_context_window_budget_create(0, 0.85) == NULL);
    assert(ca_context_window_budget_create(100, 1.5) == NULL);
    assert(ca_context_window_budget_create(100, -0.1) == NULL);
}

/* ─────────────── PrefixCacheService ─────────────── */

static void test_prefix_cache_key(void) {
    char *k1 = ca_prefix_cache_key_for("model-a", "You are a helpful assistant.");
    assert(k1);
    assert(strlen(k1) == 33); /* 16 + '_' + 16 */
    assert(k1[16] == '_');
    /* stable */
    char *k2 = ca_prefix_cache_key_for("model-a", "You are a helpful assistant.");
    assert(strcmp(k1, k2) == 0);
    /* different system prompt -> different key */
    char *k3 = ca_prefix_cache_key_for("model-a", "different");
    assert(strcmp(k1, k3) != 0);
    free(k1); free(k2); free(k3);

    /* null / empty inputs -> NULL */
    assert(ca_prefix_cache_key_for("", "sys") == NULL);
    assert(ca_prefix_cache_key_for("m", NULL) == NULL);
    assert(ca_prefix_cache_key_for("m", "") == NULL);
}

static void test_prefix_cache_paths(void) {
    char root[512];
    scratch_path(root, sizeof(root), "prefix_cache");
    ca_prefix_cache_t *c = ca_prefix_cache_create(root);
    assert(c);

    char *key = ca_prefix_cache_key_for("m", "sys");
    assert(key);

    char *path = ca_prefix_cache_path_for(c, key);
    assert(path);
    /* Ensure a deterministic starting state across repeated ctest runs (the
     * scratchpad persists on disk between runs). */
    remove(path);
    assert(!ca_prefix_cache_has_entry(c, key));

    /* write a session file */
    FILE *f = fopen(path, "wb");
    assert(f);
    char buf[1024]; memset(buf, 'x', sizeof(buf));
    fwrite(buf, 1, sizeof(buf), f);
    fclose(f);
    assert(ca_prefix_cache_has_entry(c, key));

    ca_prefix_cache_touch(c, key); /* best-effort, no crash */
    ca_prefix_cache_evict_if_needed(c); /* under cap -> keeps file */
    assert(ca_prefix_cache_has_entry(c, key));

    free(key); free(path);
    ca_prefix_cache_destroy(c);
}

/* ─────────────── ModelDownloadService ─────────────── */

static void test_strip_sha_prefix(void) {
    char *a = ca_strip_sha_algorithm_prefix("sha256:ABCDEF");
    assert(a && strcmp(a, "ABCDEF") == 0); free(a);
    char *b = ca_strip_sha_algorithm_prefix("ABCDEF");
    assert(b && strcmp(b, "ABCDEF") == 0); free(b);
    char *c = ca_strip_sha_algorithm_prefix("  sha256:  ABC  ");
    assert(c && strcmp(c, "ABC") == 0); free(c);
    /* a URL-with-colon is not an algorithm prefix (prefix too long) */
    char *d = ca_strip_sha_algorithm_prefix("this_is_a_very_long_thing:tail");
    assert(d && strcmp(d, "this_is_a_very_long_thing:tail") == 0); free(d);
    char *e = ca_strip_sha_algorithm_prefix("");
    assert(e && strcmp(e, "") == 0); free(e);
}

static void test_modelscope_urls(void) {
    char *p = ca_modelscope_primary_url("MNN/Qwen3-0.6B-MNN", "config.json");
    assert(p);
    assert(strstr(p, "https://modelscope.cn/api/v1/models/MNN/Qwen3-0.6B-MNN/repo") != NULL);
    assert(strstr(p, "FilePath=config.json") != NULL);
    free(p);
    char *f = ca_modelscope_fallback_url("MNN/Qwen3-0.6B-MNN", "a b.txt");
    assert(f);
    assert(strstr(f, "/resolve/master/a%20b.txt") != NULL);
    free(f);
}

/* Fetch stub: serves registered (url -> bytes). */
typedef struct { const char *url; const uint8_t *data; size_t len; } fetch_entry;
typedef struct { fetch_entry *entries; size_t count; int fetch_calls; } fetch_ctx;

static bool test_fetch(void *user, const char *url, const char *dest_path,
                       ca_download_progress_ratio_fn progress, void *progress_user) {
    fetch_ctx *ctx = (fetch_ctx *)user;
    ctx->fetch_calls++;
    for (size_t i = 0; i < ctx->count; i++) {
        if (strcmp(ctx->entries[i].url, url) == 0) {
            FILE *f = fopen(dest_path, "wb");
            if (!f) return false;
            fwrite(ctx->entries[i].data, 1, ctx->entries[i].len, f);
            fclose(f);
            if (progress) progress(progress_user, 1.0);
            return true;
        }
    }
    return false; /* unknown url */
}

static void test_download_single_file(void) {
    char storage[512];
    scratch_path(storage, sizeof(storage), "dl_single");

    const char *payload = "MODEL-WEIGHTS-v1";
    char hex[65]; sha_hex(payload, strlen(payload), hex);

    fetch_entry entries[] = {
        { "https://example/model.gguf", (const uint8_t *)payload, strlen(payload) },
    };
    fetch_ctx ctx = { entries, 1, 0 };

    ca_model_download_service_t *s =
        ca_model_download_service_create(storage, test_fetch, &ctx);
    assert(s);

    char sha_field[80];
    snprintf(sha_field, sizeof(sha_field), "sha256:%s", hex);

    /* Deterministic starting state across repeated ctest runs. */
    ca_model_download_service_delete_model(s, "mymodel");

    char *path = NULL;
    assert(ca_model_download_service_ensure_model(
        s, "mymodel", "https://example/model.gguf", sha_field, NULL, NULL, &path));
    assert(path);
    assert(ctx.fetch_calls == 1);

    /* cached + valid on second call -> no fetch */
    char *path2 = NULL;
    assert(ca_model_download_service_ensure_model(
        s, "mymodel", "https://example/model.gguf", sha_field, NULL, NULL, &path2));
    assert(ctx.fetch_calls == 1); /* unchanged */
    free(path2);

    assert(ca_model_download_service_is_model_cached(s, "mymodel"));

    /* SHA mismatch fails and deletes */
    fetch_ctx ctx2 = { entries, 1, 0 };
    ca_model_download_service_t *s2 =
        ca_model_download_service_create(storage, test_fetch, &ctx2);
    char *badpath = NULL;
    bool ok = ca_model_download_service_ensure_model(
        s2, "othermodel", "https://example/model.gguf", "sha256:deadbeef", NULL, NULL, &badpath);
    assert(!ok);
    assert(badpath == NULL);
    assert(!ca_model_download_service_is_model_cached(s2, "othermodel"));
    ca_model_download_service_destroy(s2);

    ca_model_download_service_delete_model(s, "mymodel");
    assert(!ca_model_download_service_is_model_cached(s, "mymodel"));

    free(path);
    ca_model_download_service_destroy(s);
}

static void test_download_bundle(void) {
    char storage[512];
    scratch_path(storage, sizeof(storage), "dl_bundle");

    const char *cfg = "{ \"config\": true }";
    const char *wts = "WEIGHTS";
    char cfg_hex[65], wts_hex[65];
    sha_hex(cfg, strlen(cfg), cfg_hex);
    sha_hex(wts, strlen(wts), wts_hex);

    /* the service tries the primary URL first; register that. */
    char *cfg_url = ca_modelscope_primary_url("MNN/Test", "config.json");
    char *wts_url = ca_modelscope_primary_url("MNN/Test", "model.mnn");
    assert(cfg_url && wts_url);

    fetch_entry entries[] = {
        { cfg_url, (const uint8_t *)cfg, strlen(cfg) },
        { wts_url, (const uint8_t *)wts, strlen(wts) },
    };
    fetch_ctx ctx = { entries, 2, 0 };

    ca_model_download_service_t *s =
        ca_model_download_service_create(storage, test_fetch, &ctx);
    assert(s);

    ca_bundle_file_spec_t files[] = {
        { "config.json", cfg_hex, (int64_t)strlen(cfg) },
        { "model.mnn",   wts_hex, (int64_t)strlen(wts) },
    };
    /* Deterministic starting state: the scratchpad persists across ctest runs,
     * so clear any prior bundle before asserting the fetch count. */
    ca_model_download_service_delete_model(s, "bundlemodel");

    char *dir = NULL;
    assert(ca_model_download_service_ensure_bundle(
        s, "bundlemodel", "MNN/Test", files, 2, NULL, NULL, &dir));
    assert(dir);
    assert(ctx.fetch_calls == 2);

    /* re-run: cached + valid -> no fetch */
    char *dir2 = NULL;
    assert(ca_model_download_service_ensure_bundle(
        s, "bundlemodel", "MNN/Test", files, 2, NULL, NULL, &dir2));
    assert(ctx.fetch_calls == 2);
    free(dir2);

    /* installed.json manifest */
    assert(ca_model_download_service_write_installed_manifest(
        dir, "bundlemodel", "1.0.0", "MNN/Test", files, 2));

    /* empty repo / empty list rejected */
    char *d3 = NULL;
    assert(!ca_model_download_service_ensure_bundle(s, "x", "", files, 2, NULL, NULL, &d3));
    assert(!ca_model_download_service_ensure_bundle(s, "x", "MNN/Test", files, 0, NULL, NULL, &d3));

    free(cfg_url); free(wts_url); free(dir);
    ca_model_download_service_destroy(s);
}

static void test_disk_space(void) {
    char storage[512];
    scratch_path(storage, sizeof(storage), "dl_disk");
    ca_model_download_service_t *s = ca_model_download_service_create(storage, NULL, NULL);
    assert(s);
    int64_t free_bytes = ca_model_download_service_available_disk_space(s);
    assert(free_bytes >= 0);
    ca_model_download_service_destroy(s);
}

/* ─────────────── LayerStreaming ─────────────── */

/* runner: multiplies the input by 2 each layer; out len == in len (or 1). */
static bool runner_run(void *user, const ca_layer_weight_shard_t *shard,
                       const float *in_hidden, size_t in_len,
                       float **out_hidden, size_t *out_len) {
    int *evicted = (int *)user;
    (void)shard; (void)evicted;
    size_t n = in_len > 0 ? in_len : 1;
    float *out = (float *)malloc(n * sizeof(float));
    if (!out) return false;
    for (size_t i = 0; i < n; i++) out[i] = (in_len > 0 ? in_hidden[i] : 0.0f) + 1.0f;
    *out_hidden = out; *out_len = n;
    return true;
}
static void runner_evict(void *user, int layer_index) {
    (void)layer_index;
    int *evicted = (int *)user;
    (*evicted)++;
}

static void test_layer_streaming(void) {
    char modeldir[512];
    scratch_path(modeldir, sizeof(modeldir), "layers");
    ensure_dirs(modeldir);

    /* create 3 layer shard files (out of order) */
    const char *names[] = { "layer_002.safetensors", "layer_000.bin", "layer_001.safetensors" };
    const char *body = "SHARD-BYTES";
    for (int i = 0; i < 3; i++) {
        char p[600]; snprintf(p, sizeof(p), "%s/%s", modeldir, names[i]);
        FILE *f = fopen(p, "wb"); assert(f);
        fwrite(body, 1, strlen(body), f); fclose(f);
    }
    /* a non-layer file must be ignored */
    { char p[600]; snprintf(p, sizeof(p), "%s/config.json", modeldir);
      FILE *f = fopen(p, "wb"); assert(f); fputs("{}", f); fclose(f); }

    ca_layer_streaming_plan_t plan;
    assert(ca_layer_shard_discover("m70b", modeldir, &plan));
    assert(plan.shard_count == 3);
    assert(plan.total_layers == 3);
    /* sorted by index */
    assert(plan.shards[0].layer_index == 0);
    assert(plan.shards[1].layer_index == 1);
    assert(plan.shards[2].layer_index == 2);
    assert(plan.approx_parameter_bytes == (int64_t)(3 * strlen(body)));

    int evicted = 0;
    ca_layer_streaming_runner_t runner = {
        "test", true, runner_run, runner_evict, &evicted
    };
    float init[2] = { 0.0f, 10.0f };
    float *out = NULL; size_t out_len = 0;
    assert(ca_layer_streaming_forward(&runner, &plan, init, 2, NULL, NULL, &out, &out_len));
    assert(out_len == 2);
    /* each of 3 layers adds 1 */
    assert(fabsf(out[0] - 3.0f) < 1e-6f);
    assert(fabsf(out[1] - 13.0f) < 1e-6f);
    assert(evicted == 3);
    free(out);

    /* unavailable runner -> false */
    ca_layer_streaming_runner_t bad = { "null", false, runner_run, NULL, NULL };
    float *o2 = NULL; size_t l2 = 0;
    assert(!ca_layer_streaming_forward(&bad, &plan, init, 2, NULL, NULL, &o2, &l2));

    ca_layer_streaming_plan_free(&plan);

    /* empty model_id rejected */
    ca_layer_streaming_plan_t p2;
    assert(!ca_layer_shard_discover("", modeldir, &p2));
}

/* ─────────────── FeedbackTrainingQueue ─────────────── */

static void test_feedback_queue(void) {
    char path[512];
    scratch_path(path, sizeof(path), "feedback/queue.jsonl");
    remove(path); /* clean */

    ca_feedback_training_queue_t *q = ca_feedback_training_queue_create(path);
    assert(q);
    assert(ca_feedback_training_queue_pending(q) == 0);

    ca_training_sample_t s1 = { (char *)"hi", (char *)"hello", (char *)"hey there", 0, 1000 };
    ca_training_sample_t s2 = { (char *)"multi\nline", (char *)"a\tb", (char *)"pref", 1, 2000 };
    ca_training_sample_t s3 = { (char *)"third", (char *)"3", (char *)"3p", -1, 3000 };
    assert(ca_feedback_training_queue_enqueue(q, &s1));
    assert(ca_feedback_training_queue_enqueue(q, &s2));
    assert(ca_feedback_training_queue_enqueue(q, &s3));
    assert(ca_feedback_training_queue_pending(q) == 3);

    /* drain 2 */
    ca_training_sample_t *drained = NULL; size_t n = 0;
    assert(ca_feedback_training_queue_drain(q, 2, &drained, &n));
    assert(n == 2);
    assert(strcmp(drained[0].user_text, "hi") == 0);
    assert(strcmp(drained[0].preferred_text, "hey there") == 0);
    assert(drained[0].polarity == 0);
    assert(drained[0].at_unix_ms == 1000);
    /* escaping round-trips tab + newline */
    assert(strcmp(drained[1].user_text, "multi\nline") == 0);
    assert(strcmp(drained[1].assistant_text, "a\tb") == 0);
    assert(drained[1].polarity == 1);
    ca_training_samples_free(drained, n);

    /* one remains */
    assert(ca_feedback_training_queue_pending(q) == 1);
    ca_training_sample_t *rest = NULL; size_t rn = 0;
    assert(ca_feedback_training_queue_drain(q, 100, &rest, &rn));
    assert(rn == 1);
    assert(strcmp(rest[0].user_text, "third") == 0);
    assert(rest[0].polarity == -1);
    ca_training_samples_free(rest, rn);

    assert(ca_feedback_training_queue_pending(q) == 0);

    /* drain guard */
    ca_training_sample_t *z = NULL; size_t zn = 0;
    assert(!ca_feedback_training_queue_drain(q, 0, &z, &zn));

    ca_feedback_training_queue_destroy(q);
}

/* ─────────────── NightlyAdapterTrainer ─────────────── */

typedef struct { int steps; int save_calls; int apply_calls; bool unsupported; } lora_state;

static float lora_train(void *user, const int *input, size_t input_len,
                        const int *target, size_t target_len,
                        float lr, int rank) {
    lora_state *st = (lora_state *)user;
    (void)input; (void)input_len; (void)target; (void)target_len; (void)lr; (void)rank;
    if (st->unsupported) return -1.0f; /* signal NotSupported */
    st->steps++;
    return 0.5f;
}
static bool lora_save(void *user, const char *path) {
    (void)path; ((lora_state *)user)->save_calls++; return true;
}
static bool lora_apply(void *user, const char *path) {
    (void)path; ((lora_state *)user)->apply_calls++; return true;
}

static void seed_queue(ca_feedback_training_queue_t *q, int n) {
    for (int i = 0; i < n; i++) {
        char u[32], a[32], p[32];
        snprintf(u, sizeof(u), "user%d", i);
        snprintf(a, sizeof(a), "asst%d", i);
        snprintf(p, sizeof(p), "pref%d", i);
        ca_training_sample_t s = { u, a, p, (i % 2 == 0) ? 1 : -1, 1000 + i };
        ca_feedback_training_queue_enqueue(q, &s);
    }
}

static void test_nightly_trainer(void) {
    char path[512];
    scratch_path(path, sizeof(path), "trainer/queue.jsonl");
    remove(path);

    ca_feedback_training_queue_t *q = ca_feedback_training_queue_create(path);
    assert(q);

    ca_nightly_trainer_options_t opts;
    ca_nightly_trainer_options_init(&opts);
    assert(opts.min_batch_size == 16);
    assert(opts.max_samples_per_run == 256);
    assert(opts.lora_rank == 8);

    lora_state st = {0};
    ca_lora_adapter_manager_t mgr = { lora_train, lora_save, lora_apply, &st };

    /* Below min batch: skip, no steps. */
    seed_queue(q, 5);
    int steps = -1; float loss = -1.0f;
    assert(ca_nightly_adapter_trainer_run_once(q, &mgr, &opts, &steps, &loss));
    assert(steps == 0);
    assert(st.steps == 0);
    assert(ca_feedback_training_queue_pending(q) == 5); /* untouched */

    /* Above min batch: trains, saves, applies. */
    seed_queue(q, 20); /* now 25 pending */
    steps = -1; loss = -1.0f;
    assert(ca_nightly_adapter_trainer_run_once(q, &mgr, &opts, &steps, &loss));
    assert(steps == 25);
    assert(st.steps == 25);
    assert(st.save_calls == 1);
    assert(st.apply_calls == 1);
    assert(fabsf(loss - 0.5f) < 1e-6f);
    assert(ca_feedback_training_queue_pending(q) == 0);

    /* Unsupported native training: re-queue + skip. */
    remove(path);
    ca_feedback_training_queue_t *q2 = ca_feedback_training_queue_create(path);
    seed_queue(q2, 20);
    lora_state st2 = { 0, 0, 0, true };
    ca_lora_adapter_manager_t mgr2 = { lora_train, lora_save, lora_apply, &st2 };
    steps = -1;
    assert(ca_nightly_adapter_trainer_run_once(q2, &mgr2, &opts, &steps, &loss));
    assert(steps == 0);
    assert(st2.save_calls == 0);
    /* samples re-queued (drained 20, re-enqueued 20) */
    assert(ca_feedback_training_queue_pending(q2) == 20);
    ca_feedback_training_queue_destroy(q2);

    ca_feedback_training_queue_destroy(q);
}

int main(void) {
    ensure_dirs(SCRATCH);
    test_power_budget_resolve();
    test_vision_input();
    test_chat_generator();
    test_qwen_prompt();
    test_session_marker();
    test_context_budget();
    test_prefix_cache_key();
    test_prefix_cache_paths();
    test_strip_sha_prefix();
    test_modelscope_urls();
    test_download_single_file();
    test_download_bundle();
    test_disk_space();
    test_layer_streaming();
    test_feedback_queue();
    test_nightly_trainer();
    printf("test_inference_rt: all passed\n");
    return 0;
}
