/*
 * test_host_mcp.c — CircleAI.Hosting.Mcp (C11 port).
 *
 * Verifies the JSON-RPC 2.0 dispatcher: initialize, tools/list, tools/call
 * (success + tool-level error), resources/list, resources/read (found +
 * not-found + no-provider), notifications, and error codes.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* an echo tool */
static void echo_execute(void *user, const char *args_json, char **out, bool *is_err) {
    (void)user;
    *is_err = false;
    /* return the arguments back as the "result data" */
    size_t n = strlen(args_json) + 16;
    *out = (char *)malloc(n);
    if (*out) snprintf(*out, n, "echo:%s", args_json);
}
static void fail_execute(void *user, const char *args_json, char **out, bool *is_err) {
    (void)user; (void)args_json;
    *is_err = true;
    *out = strdup("tool blew up");
}

/* a resource provider */
static ca_mcp_resource_t *vault_list(void *user, size_t *out_count) {
    (void)user;
    ca_mcp_resource_t *arr = (ca_mcp_resource_t *)calloc(1, sizeof(ca_mcp_resource_t));
    arr[0].uri = strdup("vault://secret/1");
    arr[0].name = strdup("Secret One");
    arr[0].description = strdup("the first secret");
    arr[0].mime_type = strdup("text/plain");
    *out_count = 1;
    return arr;
}
static bool vault_read(void *user, const char *uri, ca_mcp_resource_content_t *out) {
    (void)user;
    if (strcmp(uri, "vault://secret/1") != 0) return false;
    out->uri = strdup(uri);
    out->mime_type = strdup("text/plain");
    out->text = strdup("s3cr3t");
    return true;
}

static ca_mcp_registry_t *build_registry(void) {
    ca_mcp_registry_t *r = ca_mcp_registry_create();
    ca_mcp_tool_t echo = { "echo", "Echoes arguments", "{\"type\":\"object\"}", echo_execute, NULL };
    ca_mcp_tool_t fail = { "boom", "Always errors", "{}", fail_execute, NULL };
    ca_mcp_registry_add_tool(r, &echo);
    ca_mcp_registry_add_tool(r, &fail);
    ca_mcp_resource_provider_t vault = { "vault://", vault_list, vault_read, NULL };
    ca_mcp_registry_add_provider(r, &vault);
    return r;
}

static void test_initialize(void) {
    ca_mcp_registry_t *r = build_registry();
    char *resp = ca_mcp_dispatch(r, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}", NULL);
    assert(resp);
    assert(strstr(resp, "\"protocolVersion\":\"2024-11-05\""));
    assert(strstr(resp, "circleai-mcp"));
    assert(strstr(resp, "\"id\":\"1\"")); /* id re-emitted as string */
    free(resp);
    ca_mcp_registry_destroy(r);
    printf("  initialize: ok\n");
}

static void test_tools_list(void) {
    ca_mcp_registry_t *r = build_registry();
    char *resp = ca_mcp_dispatch(r, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", NULL);
    assert(strstr(resp, "\"name\":\"echo\""));
    assert(strstr(resp, "\"name\":\"boom\""));
    assert(strstr(resp, "\"inputSchema\":{\"type\":\"object\"}")); /* verbatim schema */
    free(resp);
    ca_mcp_registry_destroy(r);
    printf("  tools/list: ok\n");
}

static void test_tools_call(void) {
    ca_mcp_registry_t *r = build_registry();

    /* success */
    char *resp = ca_mcp_dispatch(r,
        "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\","
        "\"params\":{\"name\":\"echo\",\"arguments\":{\"x\":1}}}", NULL);
    assert(strstr(resp, "\"isError\":false"));
    assert(strstr(resp, "echo:")); /* our tool echoed the args */
    free(resp);

    /* tool-level error */
    resp = ca_mcp_dispatch(r,
        "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\","
        "\"params\":{\"name\":\"boom\",\"arguments\":{}}}", NULL);
    assert(strstr(resp, "\"isError\":true"));
    assert(strstr(resp, "tool blew up"));
    free(resp);

    /* unknown tool -> -32602 */
    resp = ca_mcp_dispatch(r,
        "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\","
        "\"params\":{\"name\":\"ghost\"}}", NULL);
    assert(strstr(resp, "-32602"));
    assert(strstr(resp, "Unknown tool: ghost"));
    free(resp);

    /* missing name -> -32602 */
    resp = ca_mcp_dispatch(r,
        "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{}}", NULL);
    assert(strstr(resp, "-32602"));
    free(resp);

    ca_mcp_registry_destroy(r);
    printf("  tools/call: ok\n");
}

static void test_resources(void) {
    ca_mcp_registry_t *r = build_registry();

    char *resp = ca_mcp_dispatch(r, "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"resources/list\"}", NULL);
    assert(strstr(resp, "vault://secret/1"));
    assert(strstr(resp, "Secret One"));
    free(resp);

    resp = ca_mcp_dispatch(r,
        "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"resources/read\","
        "\"params\":{\"uri\":\"vault://secret/1\"}}", NULL);
    assert(strstr(resp, "s3cr3t"));
    assert(strstr(resp, "\"mimeType\":\"text/plain\""));
    free(resp);

    /* not found (matching scheme, unknown uri) -> -32602 */
    resp = ca_mcp_dispatch(r,
        "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"resources/read\","
        "\"params\":{\"uri\":\"vault://secret/999\"}}", NULL);
    assert(strstr(resp, "-32602"));
    assert(strstr(resp, "Resource not found"));
    free(resp);

    /* no provider for scheme -> -32602 */
    resp = ca_mcp_dispatch(r,
        "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"resources/read\","
        "\"params\":{\"uri\":\"models://x\"}}", NULL);
    assert(strstr(resp, "No provider for URI scheme"));
    free(resp);

    ca_mcp_registry_destroy(r);
    printf("  resources: ok\n");
}

static void test_errors_and_notifications(void) {
    ca_mcp_registry_t *r = build_registry();

    /* notification -> NULL response */
    char *resp = ca_mcp_dispatch(r,
        "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", NULL);
    assert(resp == NULL);

    /* unknown method -> -32601 */
    resp = ca_mcp_dispatch(r, "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"frobnicate\"}", NULL);
    assert(strstr(resp, "-32601"));
    assert(strstr(resp, "Method not found: frobnicate"));
    free(resp);

    /* missing method / not jsonrpc 2.0 -> -32600 */
    resp = ca_mcp_dispatch(r, "{\"id\":12,\"method\":\"initialize\"}", NULL);
    assert(strstr(resp, "-32600"));
    free(resp);

    /* empty request -> -32600 */
    resp = ca_mcp_dispatch(r, "", NULL);
    assert(strstr(resp, "-32600"));
    free(resp);

    ca_mcp_registry_destroy(r);
    printf("  errors + notifications: ok\n");
}

int main(void) {
    test_initialize();
    test_tools_list();
    test_tools_call();
    test_resources();
    test_errors_and_notifications();
    printf("test_host_mcp: all assertions passed\n");
    return 0;
}
