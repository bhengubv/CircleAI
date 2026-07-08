/*
 * test_host_ai.c — CircleAI.Hosting core runtime (C11 port).
 *
 * Verifies ParseToolCall, AIService (lifecycle / chat / agentic tool loop /
 * feedback / brownout), observers (push / aether), memory-pressure sources,
 * FallbackAIService (RAM gate), AIApiClient <-> HttpLoopbackEndpoint round-trip
 * (over the loopback transport), and InProcessEndpoint.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* ── ParseToolCall ─────────────────────────────────────────────────────── */

static void test_parse_tool_call(void) {
    char *name = NULL, *args = NULL;

    /* {"name":...,"arguments":{...}} */
    const char *r1 = "sure<tool_call>{\"name\": \"weather\", \"arguments\": {\"city\": \"paris\"}}</tool_call>done";
    assert(ca_ai_parse_tool_call(r1, &name, &args));
    assert(strcmp(name, "weather") == 0);
    assert(strstr(args, "paris"));
    free(name); free(args); name = args = NULL;

    /* tool_name spelling */
    const char *r2 = "<tool_call>{\"tool_name\": \"calc\"}</tool_call>";
    assert(ca_ai_parse_tool_call(r2, &name, &args));
    assert(strcmp(name, "calc") == 0);
    assert(strcmp(args, "{}") == 0); /* no arguments -> {} */
    free(name); free(args); name = args = NULL;

    /* no tool call */
    assert(ca_ai_parse_tool_call("just text", &name, &args) == false);
    /* unclosed */
    assert(ca_ai_parse_tool_call("<tool_call>{\"name\":\"x\"}", &name, &args) == false);
    /* empty body */
    assert(ca_ai_parse_tool_call("<tool_call></tool_call>", &name, &args) == false);
    /* missing name */
    assert(ca_ai_parse_tool_call("<tool_call>{\"foo\":\"bar\"}</tool_call>", &name, &args) == false);

    printf("  parse tool call: ok\n");
}

/* ── AIService basic ───────────────────────────────────────────────────── */

static int g_started = 0, g_stopped = 0, g_chats = 0;
static void obs_started(void *u) { (void)u; g_started++; }
static void obs_stopped(void *u) { (void)u; g_stopped++; }
static void obs_chat(void *u, const ca_ai_chat_event_t *ev) { (void)u; (void)ev; g_chats++; }

static void test_ai_service(void) {
    ca_ai_options_t2 opts; assert(ca_ai_options_init(&opts));
    ca_ai_observer_v2_t obs; memset(&obs, 0, sizeof(obs));
    obs.on_started = obs_started; obs.on_stopped = obs_stopped; obs.on_chat_completed = obs_chat;
    opts.observer = &obs;

    ca_ai_service_impl_t *impl = ca_ai_service_impl_create(&opts);
    ca_ai_service_t *svc = ca_ai_service_impl_as_service(impl);

    assert(ca_ai_service_is_ready(svc) == false);
    g_started = g_stopped = g_chats = 0;
    assert(ca_ai_service_start(svc));
    assert(ca_ai_service_is_ready(svc));
    assert(g_started == 1);
    assert(strcmp(ca_ai_service_impl_resolved_model(impl), "local-default") == 0);

    char *ans = ca_ai_service_ask(svc, "hello there");
    assert(ans && strlen(ans) > 0);
    free(ans);
    assert(g_chats == 1);

    /* chat with explicit messages */
    ca_chat_msg_t msgs[2] = {
        { "system", "be brief", NULL, 0 },
        { "user", "ping", NULL, 0 },
    };
    char *reply = ca_ai_service_chat(svc, msgs, 2, NULL);
    assert(reply && strlen(reply) > 0);
    free(reply);

    assert(ca_ai_service_stop(svc));
    assert(g_stopped == 1);
    assert(ca_ai_service_is_ready(svc) == false);

    ca_ai_service_impl_destroy(impl);
    ca_ai_options_free(&opts);
    printf("  ai service: ok\n");
}

/* ── agentic tool loop ─────────────────────────────────────────────────── */

/* A tool bridge that echoes success once, so the agentic loop terminates on
 * the following plain-text turn. The deterministic generator won't emit a
 * <tool_call> on its own, so we verify the no-tool-call path terminates in one
 * iteration and the tool bridge is reachable via InvokeTool. */
static int g_tool_calls = 0;
static bool tool_invoke(void *user, const char *name, const char *args,
                        char **out_result, char **out_error) {
    (void)user; (void)args;
    g_tool_calls++;
    if (strcmp(name, "fail") == 0) { *out_error = strdup("boom"); return false; }
    *out_result = strdup("{\"ok\":true}");
    return true;
}

static void test_agentic_and_tools(void) {
    ca_ai_options_t2 opts; ca_ai_options_init(&opts);
    ca_tool_bridge_t bridge = { tool_invoke, NULL };
    opts.tool_bridge = &bridge;
    opts.agentic_max_iterations = 3;

    ca_ai_service_impl_t *impl = ca_ai_service_impl_create(&opts);
    ca_ai_service_t *svc = ca_ai_service_impl_as_service(impl);
    ca_ai_service_start(svc);

    /* agentic: no tool call from deterministic gen -> single turn, returns text */
    char *out = ca_ai_service_agentic_chat(svc, "do a thing", NULL);
    assert(out && strlen(out) > 0);
    free(out);

    /* direct tool invoke */
    g_tool_calls = 0;
    char *res = NULL, *err = NULL;
    bool ok = ca_ai_service_invoke_tool(svc, "greet", "{}", &res, &err);
    assert(ok && res && strstr(res, "ok"));
    assert(g_tool_calls == 1);
    free(res); free(err); res = err = NULL;

    ok = ca_ai_service_invoke_tool(svc, "fail", "{}", &res, &err);
    assert(!ok && err && strcmp(err, "boom") == 0 && res == NULL);
    free(res); free(err);

    ca_ai_service_impl_destroy(impl);
    ca_ai_options_free(&opts);
    printf("  agentic + tools: ok\n");
}

/* ── no bridge -> failure result ───────────────────────────────────────── */
static void test_no_bridge(void) {
    ca_ai_options_t2 opts; ca_ai_options_init(&opts);
    ca_ai_service_impl_t *impl = ca_ai_service_impl_create(&opts);
    ca_ai_service_t *svc = ca_ai_service_impl_as_service(impl);
    ca_ai_service_start(svc);
    char *res = NULL, *err = NULL;
    bool ok = ca_ai_service_invoke_tool(svc, "x", "{}", &res, &err);
    assert(!ok && err && strcmp(err, "No tool bridge configured.") == 0);
    free(res); free(err);
    ca_ai_service_impl_destroy(impl);
    ca_ai_options_free(&opts);
    printf("  no bridge: ok\n");
}

/* ── feedback + brownout ───────────────────────────────────────────────── */

static int g_brownouts = 0;
static char g_brownout_to[64];
static void obs_brownout(void *u, const char *from, const char *to, ca_brownout_reason_t r) {
    (void)u; (void)from; (void)r;
    g_brownouts++;
    snprintf(g_brownout_to, sizeof(g_brownout_to), "%s", to);
}

static void test_feedback_and_brownout(void) {
    ca_ai_options_t2 opts; ca_ai_options_init(&opts);
    ca_ai_observer_v2_t obs; memset(&obs, 0, sizeof(obs));
    obs.on_brownout = obs_brownout;
    opts.observer = &obs;
    opts.model_id = strdup("big-model");
    ca_memory_pressure_source_t *press = ca_manual_memory_pressure_source_create();
    opts.pressure_source = press;

    ca_ai_service_impl_t *impl = ca_ai_service_impl_create(&opts);
    ca_ai_service_impl_set_fallback_model(impl, "small-model");
    ca_ai_service_t *svc = ca_ai_service_impl_as_service(impl);
    ca_ai_service_start(svc);
    assert(strcmp(ca_ai_service_impl_resolved_model(impl), "big-model") == 0);

    /* feedback tallies (CircleAI.Memory FeedbackSignal == ca_feedback_signal_rec_t) */
    ca_feedback_signal_rec_t pos; memset(&pos, 0, sizeof(pos));
    pos.id = strdup("f1"); pos.polarity = CA_FEEDBACK_POLARITY_POSITIVE;
    pos.user_text = strdup("u"); pos.assistant_text = strdup("a");
    ca_ai_service_submit_feedback(svc, &pos);
    ca_feedback_signal_free(&pos);
    ca_feedback_signal_rec_t neg; memset(&neg, 0, sizeof(neg));
    neg.id = strdup("f2"); neg.polarity = CA_FEEDBACK_POLARITY_NEGATIVE;
    neg.user_text = strdup("u"); neg.assistant_text = strdup("a");
    ca_ai_service_submit_feedback(svc, &neg);
    ca_feedback_signal_free(&neg);
    assert(ca_ai_service_impl_positive_signals(impl) == 1);
    assert(ca_ai_service_impl_negative_signals(impl) == 1);
    assert(ca_ai_service_impl_total_interactions(impl) == 2);

    /* brownout via pressure Critical */
    g_brownouts = 0;
    ca_memory_pressure_raise(press, CA_MEM_PRESSURE_TRIM);   /* not critical -> no swap */
    assert(g_brownouts == 0);
    ca_memory_pressure_raise(press, CA_MEM_PRESSURE_CRITICAL);
    assert(g_brownouts == 1 && strcmp(g_brownout_to, "small-model") == 0);
    assert(strcmp(ca_ai_service_impl_resolved_model(impl), "small-model") == 0);
    /* second critical (same level, no transition) -> nothing */
    ca_memory_pressure_raise(press, CA_MEM_PRESSURE_CRITICAL);
    assert(g_brownouts == 1);

    ca_ai_service_impl_destroy(impl);
    ca_memory_pressure_source_destroy(press);
    ca_ai_options_free(&opts);
    printf("  feedback + brownout: ok\n");
}

/* ── observers: push + aether ──────────────────────────────────────────── */

static char g_push_title[32], g_push_body[256];
static bool push_send(void *u, const char *tok, const char *title, const char *body) {
    (void)u; (void)tok;
    snprintf(g_push_title, sizeof(g_push_title), "%s", title);
    snprintf(g_push_body, sizeof(g_push_body), "%s", body);
    return true;
}
static char g_aether_topic[32]; static char g_aether_payload[256];
static bool aether_pub(void *u, const char *topic, const uint8_t *payload, size_t len) {
    (void)u;
    snprintf(g_aether_topic, sizeof(g_aether_topic), "%s", topic);
    size_t n = len < sizeof(g_aether_payload) - 1 ? len : sizeof(g_aether_payload) - 1;
    memcpy(g_aether_payload, payload, n); g_aether_payload[n] = '\0';
    return true;
}

static void test_observers(void) {
    /* push observer */
    ca_push_observer_t *po = ca_push_observer_create(push_send, NULL, "device-abc");
    assert(po);
    ca_ai_observer_v2_t pv = ca_push_observer_as_observer(po);
    ca_ai_chat_event_t ev; memset(&ev, 0, sizeof(ev));
    ev.response = "the answer";
    pv.on_chat_completed(pv.user, &ev);
    assert(strcmp(g_push_title, "B!") == 0 && strcmp(g_push_body, "the answer") == 0);
    ca_push_observer_on_error(po, "kaboom");
    assert(strcmp(g_push_title, "B! Error") == 0);
    /* blank token rejected */
    assert(ca_push_observer_create(push_send, NULL, "  ") == NULL);
    ca_push_observer_destroy(po);

    /* aether observer */
    ca_aether_observer_t *ao = ca_aether_observer_create(aether_pub, NULL);
    ca_ai_observer_v2_t av = ca_aether_observer_as_observer(ao);
    ca_ai_chat_event_t ev2; memset(&ev2, 0, sizeof(ev2));
    ev2.response = "hi";
    av.on_chat_completed(av.user, &ev2);
    assert(strcmp(g_aether_topic, "butler/response") == 0);
    assert(strstr(g_aether_payload, "\"response\":\"hi\""));
    ca_aether_observer_on_error(ao, "IOError", "disk full");
    assert(strcmp(g_aether_topic, "butler/error") == 0);
    assert(strstr(g_aether_payload, "IOError") && strstr(g_aether_payload, "disk full"));
    ca_aether_observer_destroy(ao);
    printf("  observers: ok\n");
}

/* ── FallbackAIService ─────────────────────────────────────────────────── */

static int64_t ram_high(void *u) { (void)u; return 8LL * 1024 * 1024 * 1024; }
static int64_t ram_low(void *u) { (void)u; return 512LL * 1024 * 1024; }

static void test_fallback(void) {
    ca_ai_options_t2 lo; ca_ai_options_init(&lo);
    lo.model_id = strdup("local");
    ca_ai_service_impl_t *local = ca_ai_service_impl_create(&lo);

    ca_ai_options_t2 co; ca_ai_options_init(&co);
    co.model_id = strdup("cloud");
    ca_ai_service_impl_t *cloud = ca_ai_service_impl_create(&co);

    /* high RAM -> uses local */
    ca_fallback_ai_service_t *fb = ca_fallback_ai_service_create(
        ca_ai_service_impl_as_service(local), ca_ai_service_impl_as_service(cloud),
        2LL * 1024 * 1024 * 1024, ram_high, NULL);
    ca_ai_service_t *fbs = ca_fallback_ai_service_as_service(fb);
    assert(ca_ai_service_start(fbs));
    assert(ca_fallback_ai_service_using_cloud(fb) == false);
    char *a = ca_ai_service_ask(fbs, "q");
    assert(a && strlen(a) > 0); free(a);
    ca_ai_service_stop(fbs);
    ca_fallback_ai_service_destroy(fb);

    /* low RAM -> uses cloud */
    fb = ca_fallback_ai_service_create(
        ca_ai_service_impl_as_service(local), ca_ai_service_impl_as_service(cloud),
        2LL * 1024 * 1024 * 1024, ram_low, NULL);
    fbs = ca_fallback_ai_service_as_service(fb);
    assert(ca_ai_service_start(fbs));
    assert(ca_fallback_ai_service_using_cloud(fb) == true);
    ca_fallback_ai_service_destroy(fb);

    ca_ai_service_impl_destroy(local);
    ca_ai_service_impl_destroy(cloud);
    ca_ai_options_free(&lo);
    ca_ai_options_free(&co);
    printf("  fallback: ok\n");
}

/* ── AIApiClient <-> HttpLoopbackEndpoint round-trip ───────────────────── */

static int g_stream_pieces = 0;
static bool piece_cb(void *u, const char *p) { (void)u; (void)p; g_stream_pieces++; return true; }

static void test_endpoint_roundtrip(void) {
    /* real butler behind a loopback endpoint */
    ca_ai_options_t2 opts; ca_ai_options_init(&opts);
    ca_ai_service_impl_t *impl = ca_ai_service_impl_create(&opts);
    ca_ai_service_t *butler = ca_ai_service_impl_as_service(impl);
    ca_ai_service_start(butler);

    ca_ai_endpoint_t *ep = ca_http_loopback_endpoint_create("secret-token", 0);
    assert(strcmp(ca_http_loopback_endpoint_token(ep), "secret-token") == 0);
    assert(ca_http_loopback_endpoint_port(ep) > 0);
    assert(ca_ai_endpoint_start(ep, butler));

    /* direct dispatch: wrong token -> 401 */
    int status = 0; char *body = NULL;
    assert(ca_http_loopback_endpoint_dispatch(ep, "wrong", "POST", "/butler/ask",
                                              "{\"question\":\"hi\"}", &status, &body));
    assert(status == 401);
    free(body);

    /* correct token ask -> 200 */
    assert(ca_http_loopback_endpoint_dispatch(ep, "secret-token", "POST", "/butler/ask",
                                              "{\"question\":\"hi\"}", &status, &body));
    assert(status == 200 && body && strlen(body) > 0);
    free(body);

    /* GET not allowed -> 405 */
    assert(ca_http_loopback_endpoint_dispatch(ep, "secret-token", "GET", "/butler/ask",
                                              "{}", &status, &body));
    assert(status == 405);
    free(body);

    /* unknown route -> 404 */
    assert(ca_http_loopback_endpoint_dispatch(ep, "secret-token", "POST", "/nope",
                                              "{}", &status, &body));
    assert(status == 404);
    free(body);

    /* now via AIApiClient over the loopback transport */
    ca_http_transport_t transport;
    assert(ca_http_loopback_transport(ep, &transport));
    ca_ai_api_client_t *client = ca_ai_api_client_create(&transport);
    ca_ai_service_t *cs = ca_ai_api_client_as_service(client);
    assert(ca_ai_service_start(cs)); /* health */
    assert(ca_ai_service_is_ready(cs));

    char *ans = ca_ai_service_ask(cs, "hello");
    assert(ans && strlen(ans) > 0);
    free(ans);

    ca_chat_msg_t m[1] = { { "user", "chat please", NULL, 0 } };
    char *chat = ca_ai_service_chat(cs, m, 1, NULL);
    assert(chat && strlen(chat) > 0);
    free(chat);

    /* stream via endpoint */
    g_stream_pieces = 0;
    long pieces = ca_ai_service_stream(cs, m, 1, NULL, piece_cb, NULL);
    assert(pieces >= 0);
    assert((long)g_stream_pieces == pieces);

    ca_ai_api_client_destroy(client);
    ca_ai_endpoint_destroy(ep);
    ca_ai_service_impl_destroy(impl);
    ca_ai_options_free(&opts);
    printf("  endpoint roundtrip: ok\n");
}

static void test_inprocess_endpoint(void) {
    ca_ai_options_t2 opts; ca_ai_options_init(&opts);
    ca_ai_service_impl_t *impl = ca_ai_service_impl_create(&opts);
    ca_ai_service_t *butler = ca_ai_service_impl_as_service(impl);

    ca_ai_endpoint_t *ep = ca_inprocess_endpoint_create();
    assert(ca_inprocess_endpoint_service(ep) == NULL);
    assert(ca_ai_endpoint_start(ep, butler));
    assert(ca_inprocess_endpoint_service(ep) == butler);
    assert(ca_ai_endpoint_start(ep, butler)); /* idempotent */
    ca_ai_endpoint_stop(ep);
    assert(ca_inprocess_endpoint_service(ep) == NULL);
    ca_ai_endpoint_destroy(ep);

    ca_ai_service_impl_destroy(impl);
    ca_ai_options_free(&opts);
    printf("  inprocess endpoint: ok\n");
}

int main(void) {
    test_parse_tool_call();
    test_ai_service();
    test_agentic_and_tools();
    test_no_bridge();
    test_feedback_and_brownout();
    test_observers();
    test_fallback();
    test_endpoint_roundtrip();
    test_inprocess_endpoint();
    printf("test_host_ai: all assertions passed\n");
    return 0;
}
