#ifndef CIRCLE_AI_HOST_MCP_H
#define CIRCLE_AI_HOST_MCP_H

/*
 * host_mcp.h — CircleAI.Hosting.Mcp (C11 port).
 *
 * Ports (from src/CircleAI.Hosting.Mcp):
 *   IMcpTool               — Name / Description / InputSchema (JSON string) +
 *                            Execute(argumentsJson) -> result JSON (or a
 *                            tool-level error signalled McpToolException)
 *   IMcpResourceProvider   — UriScheme + List + Read
 *   McpResource / McpResourceContent
 *   McpToolException (signalled via the execute seam's is_error out flag)
 *   McpEndpoints.DispatchAsync — the JSON-RPC 2.0 dispatcher:
 *     initialize / notifications/initialized / tools/list / tools/call /
 *     resources/list / resources/read, with the exact JSON-RPC error codes
 *     (-32700/-32600/-32601/-32602/-32603) and result envelopes.
 *
 * The C# uses DI's GetServices<IMcpTool>(); here the host registers tools +
 * providers on an McpRegistry the dispatcher walks. All I/O is string in →
 * string out (the JSON-RPC request text → response text), no HTTP.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup owning fields,
 * returned strings are malloc'd (caller frees).
 */

#include <stddef.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * McpResource / McpResourceContent
 * =========================================================================== */

typedef struct {
    char *uri;          /* owned */
    char *name;         /* owned */
    char *description;  /* owned, or NULL */
    char *mime_type;    /* owned */
} ca_mcp_resource_t;

void ca_mcp_resource_free(ca_mcp_resource_t *r);
void ca_mcp_resource_free_array(ca_mcp_resource_t *arr, size_t count);

typedef struct {
    char *uri;          /* owned */
    char *mime_type;    /* owned */
    char *text;         /* owned */
} ca_mcp_resource_content_t;

void ca_mcp_resource_content_free(ca_mcp_resource_content_t *c);

/* ===========================================================================
 * IMcpTool seam
 * ===========================================================================
 *
 * execute(user, arguments_json, out_result_json, out_is_error): produce a
 * malloc'd result. When the tool signals a tool-level error (McpToolException),
 * set *out_is_error = true and put the message in *out_result_json.
 */
typedef struct {
    const char *name;
    const char *description;
    const char *input_schema;   /* JSON string (verbatim) */
    void (*execute)(void *user, const char *arguments_json,
                    char **out_result_json, bool *out_is_error);
    void *user;
} ca_mcp_tool_t;

/* ===========================================================================
 * IMcpResourceProvider seam
 * =========================================================================== */
typedef struct {
    const char *uri_scheme;     /* e.g. "vault://" */
    /* list(user, out_count): fresh McpResource array (caller frees). */
    ca_mcp_resource_t *(*list)(void *user, size_t *out_count);
    /* read(user, uri, out): fill *out + return true, or false on not-found. */
    bool (*read)(void *user, const char *uri, ca_mcp_resource_content_t *out);
    void *user;
} ca_mcp_resource_provider_t;

/* ===========================================================================
 * McpRegistry (stands in for DI's service collection)
 * =========================================================================== */

typedef struct ca_mcp_registry ca_mcp_registry_t;

ca_mcp_registry_t *ca_mcp_registry_create(void);
void ca_mcp_registry_destroy(ca_mcp_registry_t *r);
/* Register a tool / provider (copied by value; the seams' pointers are
 * borrowed). Returns false on OOM / NULL. */
bool ca_mcp_registry_add_tool(ca_mcp_registry_t *r, const ca_mcp_tool_t *tool);
bool ca_mcp_registry_add_provider(ca_mcp_registry_t *r, const ca_mcp_resource_provider_t *provider);

/* ===========================================================================
 * McpEndpoints.DispatchAsync
 * =========================================================================== */

/* Server info (McpServerInfo). NULL fields default to the C# values. */
typedef struct {
    const char *name;        /* "circleai-mcp" */
    const char *version;     /* "3.2.0" */
    const char *description; /* "CircleAI MCP endpoint" */
} ca_mcp_server_info_t;

/* Dispatch one JSON-RPC 2.0 request (request_json). Returns a malloc'd response
 * JSON (caller frees), or NULL for a notification (e.g.
 * notifications/initialized) — mirroring DispatchAsync returning null. info may
 * be NULL (defaults applied). */
char *ca_mcp_dispatch(ca_mcp_registry_t *registry, const char *request_json,
                      const ca_mcp_server_info_t *info);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_HOST_MCP_H */
