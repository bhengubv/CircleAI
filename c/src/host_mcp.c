/*
 * host_mcp.c — CircleAI.Hosting.Mcp (C11 port). See host_mcp.h.
 *
 * McpEndpoints.DispatchAsync JSON-RPC 2.0 dispatcher, ported faithfully:
 *   - id is re-emitted as a JSON string (matches id?.ToJsonString()).
 *   - error codes: -32700 parse, -32600 invalid request, -32601 method not
 *     found, -32602 invalid params, -32603 internal.
 *   - tools/call wraps results in {content:[{type:"text",text:JSON}],isError}.
 *
 * Registration mirrors DI's GetServices<>() via an McpRegistry the host fills.
 *
 * Pure C11 + libc. No HTTP.
 */

#include "circle_ai/host_mcp.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>

static char *m_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

typedef struct { char *data; size_t len, cap; } sb;
static void sb_reserve(sb *b, size_t extra) {
    if (b->len + extra + 1 <= b->cap) return;
    size_t nc = b->cap ? b->cap : 128;
    while (nc < b->len + extra + 1) nc *= 2;
    char *n = (char *)realloc(b->data, nc);
    if (n) { b->data = n; b->cap = nc; }
}
static void sb_add(sb *b, const char *s) { if (!s) return; size_t n = strlen(s); sb_reserve(b, n); memcpy(b->data + b->len, s, n); b->len += n; b->data[b->len] = 0; }
static void sb_addc(sb *b, char c) { sb_reserve(b, 1); b->data[b->len++] = c; b->data[b->len] = 0; }
static char *sb_take(sb *b) { return b->data ? b->data : m_strdup(""); }
static void json_escape(sb *b, const char *s) {
    sb_addc(b, '"');
    for (const char *p = s ? s : ""; *p; p++) {
        unsigned char ch = (unsigned char)*p;
        switch (ch) {
            case '"':  sb_add(b, "\\\""); break;
            case '\\': sb_add(b, "\\\\"); break;
            case '\n': sb_add(b, "\\n");  break;
            case '\r': sb_add(b, "\\r");  break;
            case '\t': sb_add(b, "\\t");  break;
            default:
                if (ch < 0x20) { char u[8]; snprintf(u, sizeof(u), "\\u%04x", ch); sb_add(b, u); }
                else sb_addc(b, (char)ch);
        }
    }
    sb_addc(b, '"');
}

/* ── tiny JSON extractors over a request object ─────────────────────────── */

/* Return a pointer to the value text of top-level "key" within the (assumed
 * single) JSON object `json`, or NULL. Does not descend nested objects beyond
 * the top level. Skips string/obj/array values correctly. */
static const char *find_value(const char *json, const char *key, const char **out_end) {
    if (!json) return NULL;
    size_t klen = strlen(key);
    const char *p = json;
    /* find the opening object */
    while (*p && *p != '{') p++;
    if (*p != '{') return NULL;
    p++;
    int depth = 1;
    bool instr = false;
    while (*p && depth > 0) {
        if (instr) {
            if (*p == '\\' && p[1]) { p += 2; continue; }
            if (*p == '"') instr = false;
            p++;
            continue;
        }
        if (*p == '"') {
            /* key candidate at depth 1 */
            if (depth == 1 && strncmp(p + 1, key, klen) == 0 && p[1 + klen] == '"') {
                const char *q = p + 1 + klen + 1;
                while (*q && isspace((unsigned char)*q)) q++;
                if (*q == ':') {
                    q++;
                    while (*q && isspace((unsigned char)*q)) q++;
                    /* value start; find its end */
                    const char *vs = q;
                    if (*q == '"') {
                        const char *r = q + 1;
                        while (*r && *r != '"') { if (*r == '\\' && r[1]) r++; r++; }
                        if (*r == '"') r++;
                        if (out_end) *out_end = r;
                    } else if (*q == '{' || *q == '[') {
                        char open = *q, close = open == '{' ? '}' : ']';
                        int d = 0; bool s2 = false; const char *r = q;
                        for (; *r; r++) {
                            if (s2) { if (*r == '\\' && r[1]) { r++; continue; } if (*r == '"') s2 = false; }
                            else { if (*r == '"') s2 = true; else if (*r == open) d++; else if (*r == close) { d--; if (d == 0) { r++; break; } } }
                        }
                        if (out_end) *out_end = r;
                    } else {
                        const char *r = q;
                        while (*r && *r != ',' && *r != '}' && *r != ']') r++;
                        if (out_end) *out_end = r;
                    }
                    return vs;
                }
            }
            /* skip this string */
            instr = true; p++;
            continue;
        }
        if (*p == '{' || *p == '[') depth++;
        else if (*p == '}' || *p == ']') depth--;
        p++;
    }
    return NULL;
}

/* extract a top-level string value (unescaped) or NULL. */
static char *find_string(const char *json, const char *key) {
    const char *end = NULL;
    const char *v = find_value(json, key, &end);
    if (!v || *v != '"') return NULL;
    sb out = {0};
    const char *q = v + 1;
    while (q < end - 1 && *q) {
        if (*q == '\\' && q[1]) {
            q++;
            switch (*q) {
                case 'n': sb_addc(&out, '\n'); break;
                case 't': sb_addc(&out, '\t'); break;
                case 'r': sb_addc(&out, '\r'); break;
                case '"': sb_addc(&out, '"');  break;
                case '\\': sb_addc(&out, '\\'); break;
                case '/': sb_addc(&out, '/');  break;
                default:  sb_addc(&out, *q);   break;
            }
            q++;
        } else sb_addc(&out, *q++);
    }
    return sb_take(&out);
}

/* extract the raw token text of a top-level value (for id / params). malloc'd,
 * or NULL when absent. */
static char *find_raw(const char *json, const char *key) {
    const char *end = NULL;
    const char *v = find_value(json, key, &end);
    if (!v) return NULL;
    size_t n = (size_t)(end - v);
    char *r = (char *)malloc(n + 1);
    if (r) { memcpy(r, v, n); r[n] = '\0'; }
    return r;
}

/* ── registry ───────────────────────────────────────────────────────────── */

struct ca_mcp_registry {
    ca_mcp_tool_t             *tools;
    size_t                     tool_count, tool_cap;
    ca_mcp_resource_provider_t *providers;
    size_t                     provider_count, provider_cap;
};

ca_mcp_registry_t *ca_mcp_registry_create(void) {
    return (ca_mcp_registry_t *)calloc(1, sizeof(ca_mcp_registry_t));
}
void ca_mcp_registry_destroy(ca_mcp_registry_t *r) {
    if (!r) return;
    free(r->tools); free(r->providers); free(r);
}
bool ca_mcp_registry_add_tool(ca_mcp_registry_t *r, const ca_mcp_tool_t *tool) {
    if (!r || !tool) return false;
    if (r->tool_count == r->tool_cap) {
        size_t nc = r->tool_cap ? r->tool_cap * 2 : 4;
        void *n = realloc(r->tools, nc * sizeof(*r->tools));
        if (!n) return false;
        r->tools = (ca_mcp_tool_t *)n; r->tool_cap = nc;
    }
    r->tools[r->tool_count++] = *tool;
    return true;
}
bool ca_mcp_registry_add_provider(ca_mcp_registry_t *r, const ca_mcp_resource_provider_t *provider) {
    if (!r || !provider) return false;
    if (r->provider_count == r->provider_cap) {
        size_t nc = r->provider_cap ? r->provider_cap * 2 : 4;
        void *n = realloc(r->providers, nc * sizeof(*r->providers));
        if (!n) return false;
        r->providers = (ca_mcp_resource_provider_t *)n; r->provider_cap = nc;
    }
    r->providers[r->provider_count++] = *provider;
    return true;
}

/* ── resource frees ─────────────────────────────────────────────────────── */

void ca_mcp_resource_free(ca_mcp_resource_t *r) {
    if (!r) return;
    free(r->uri); free(r->name); free(r->description); free(r->mime_type);
    r->uri = r->name = r->description = r->mime_type = NULL;
}
void ca_mcp_resource_free_array(ca_mcp_resource_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_mcp_resource_free(&arr[i]);
    free(arr);
}
void ca_mcp_resource_content_free(ca_mcp_resource_content_t *c) {
    if (!c) return;
    free(c->uri); free(c->mime_type); free(c->text);
    c->uri = c->mime_type = c->text = NULL;
}

/* ── response builders ──────────────────────────────────────────────────── */

/* id_raw is the raw id token text (e.g. "1" or "\"abc\"" or NULL). The C#
 * emits id?.ToJsonString(), i.e. the id serialized then placed as a JSON string
 * value. We reproduce: take the raw token, JSON-string-escape it, and emit that
 * quoted. NULL id => null. */
static void emit_id(sb *b, const char *id_raw) {
    sb_add(b, "\"id\":");
    if (!id_raw) { sb_add(b, "null"); return; }
    /* trim whitespace */
    const char *s = id_raw;
    while (*s && isspace((unsigned char)*s)) s++;
    size_t n = strlen(s);
    while (n > 0 && isspace((unsigned char)s[n - 1])) n--;
    char *tmp = (char *)malloc(n + 1);
    if (!tmp) { sb_add(b, "null"); return; }
    memcpy(tmp, s, n); tmp[n] = '\0';
    json_escape(b, tmp);
    free(tmp);
}

static char *mcp_error(const char *id_raw, int code, const char *message) {
    sb b = {0};
    sb_add(&b, "{\"jsonrpc\":\"2.0\",");
    emit_id(&b, id_raw);
    sb_add(&b, ",\"error\":{\"code\":");
    char num[16]; snprintf(num, sizeof(num), "%d", code); sb_add(&b, num);
    sb_add(&b, ",\"message\":"); json_escape(&b, message); sb_add(&b, "}}");
    return sb_take(&b);
}

/* ── method handlers ────────────────────────────────────────────────────── */

static char *handle_initialize(const char *id_raw, const ca_mcp_server_info_t *info) {
    sb b = {0};
    sb_add(&b, "{\"jsonrpc\":\"2.0\","); emit_id(&b, id_raw);
    sb_add(&b, ",\"result\":{\"protocolVersion\":\"2024-11-05\",\"serverInfo\":{\"name\":");
    json_escape(&b, info->name); sb_add(&b, ",\"version\":"); json_escape(&b, info->version);
    sb_add(&b, "},\"capabilities\":{\"tools\":{\"listChanged\":false},"
               "\"resources\":{\"listChanged\":false,\"subscribe\":false}}}}");
    return sb_take(&b);
}

static char *handle_tools_list(const char *id_raw, ca_mcp_registry_t *reg) {
    sb b = {0};
    sb_add(&b, "{\"jsonrpc\":\"2.0\","); emit_id(&b, id_raw);
    sb_add(&b, ",\"result\":{\"tools\":[");
    for (size_t i = 0; i < reg->tool_count; ++i) {
        if (i) sb_addc(&b, ',');
        sb_add(&b, "{\"name\":");        json_escape(&b, reg->tools[i].name);
        sb_add(&b, ",\"description\":");  json_escape(&b, reg->tools[i].description);
        sb_add(&b, ",\"inputSchema\":");
        sb_add(&b, (reg->tools[i].input_schema && reg->tools[i].input_schema[0]) ? reg->tools[i].input_schema : "{}");
        sb_addc(&b, '}');
    }
    sb_add(&b, "]}}");
    return sb_take(&b);
}

static char *handle_tools_call(const char *id_raw, const char *params_raw, ca_mcp_registry_t *reg) {
    char *tool_name = find_string(params_raw, "name");
    if (!tool_name || !tool_name[0]) { free(tool_name); return mcp_error(id_raw, -32602, "Invalid params: 'name' is required"); }

    ca_mcp_tool_t *tool = NULL;
    for (size_t i = 0; i < reg->tool_count; ++i)
        if (reg->tools[i].name && strcmp(reg->tools[i].name, tool_name) == 0) { tool = &reg->tools[i]; break; }
    if (!tool) {
        size_t n = strlen(tool_name) + 24;
        char *msg = (char *)malloc(n);
        if (msg) snprintf(msg, n, "Unknown tool: %s", tool_name);
        char *e = mcp_error(id_raw, -32602, msg ? msg : "Unknown tool");
        free(msg); free(tool_name);
        return e;
    }
    free(tool_name);

    char *args = find_raw(params_raw, "arguments");
    const char *args_json = (args && args[0] == '{') ? args : "{}";

    char *result = NULL; bool is_error = false;
    if (tool->execute) tool->execute(tool->user, args_json, &result, &is_error);
    free(args);

    /* McpToolResult / McpToolError envelope. */
    sb b = {0};
    sb_add(&b, "{\"jsonrpc\":\"2.0\","); emit_id(&b, id_raw);
    sb_add(&b, ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":");
    if (is_error) {
        json_escape(&b, result ? result : "");
        sb_add(&b, "}],\"isError\":true}}");
    } else {
        /* McpToolResult serializes the tool's return value as JSON, then puts
         * THAT string as the "text" — i.e. a JSON-encoded string of a JSON
         * document. We treat the tool's result string as the serialized data
         * and JSON-string-escape it. */
        json_escape(&b, result ? result : "null");
        sb_add(&b, "}],\"isError\":false}}");
    }
    free(result);
    return sb_take(&b);
}

static char *handle_resources_list(const char *id_raw, ca_mcp_registry_t *reg) {
    sb b = {0};
    sb_add(&b, "{\"jsonrpc\":\"2.0\","); emit_id(&b, id_raw);
    sb_add(&b, ",\"result\":{\"resources\":[");
    bool first = true;
    for (size_t p = 0; p < reg->provider_count; ++p) {
        if (!reg->providers[p].list) continue;
        size_t n = 0;
        ca_mcp_resource_t *page = reg->providers[p].list(reg->providers[p].user, &n);
        for (size_t i = 0; i < n; ++i) {
            if (!first) sb_addc(&b, ',');
            first = false;
            sb_add(&b, "{\"uri\":");         json_escape(&b, page[i].uri);
            sb_add(&b, ",\"name\":");         json_escape(&b, page[i].name);
            sb_add(&b, ",\"description\":");  json_escape(&b, page[i].description ? page[i].description : page[i].name);
            sb_add(&b, ",\"mimeType\":");     json_escape(&b, page[i].mime_type);
            sb_addc(&b, '}');
        }
        ca_mcp_resource_free_array(page, n);
    }
    sb_add(&b, "]}}");
    return sb_take(&b);
}

static bool starts_with_ci(const char *s, const char *prefix) {
    if (!s || !prefix) return false;
    while (*prefix) {
        if (tolower((unsigned char)*s) != tolower((unsigned char)*prefix)) return false;
        s++; prefix++;
    }
    return true;
}

static char *handle_resources_read(const char *id_raw, const char *params_raw, ca_mcp_registry_t *reg) {
    char *uri = find_string(params_raw, "uri");
    if (!uri || !uri[0]) { free(uri); return mcp_error(id_raw, -32602, "Invalid params: 'uri' is required"); }

    ca_mcp_resource_provider_t *provider = NULL;
    for (size_t p = 0; p < reg->provider_count; ++p)
        if (reg->providers[p].uri_scheme && starts_with_ci(uri, reg->providers[p].uri_scheme)) { provider = &reg->providers[p]; break; }
    if (!provider) {
        size_t n = strlen(uri) + 40;
        char *msg = (char *)malloc(n);
        if (msg) snprintf(msg, n, "No provider for URI scheme: %s", uri);
        char *e = mcp_error(id_raw, -32602, msg ? msg : "No provider");
        free(msg); free(uri);
        return e;
    }
    ca_mcp_resource_content_t content; memset(&content, 0, sizeof(content));
    bool ok = provider->read ? provider->read(provider->user, uri, &content) : false;
    if (!ok) {
        size_t n = strlen(uri) + 24;
        char *msg = (char *)malloc(n);
        if (msg) snprintf(msg, n, "Resource not found: %s", uri);
        char *e = mcp_error(id_raw, -32602, msg ? msg : "Resource not found");
        free(msg); free(uri);
        return e;
    }
    free(uri);

    sb b = {0};
    sb_add(&b, "{\"jsonrpc\":\"2.0\","); emit_id(&b, id_raw);
    sb_add(&b, ",\"result\":{\"contents\":[{\"uri\":"); json_escape(&b, content.uri);
    sb_add(&b, ",\"mimeType\":"); json_escape(&b, content.mime_type);
    sb_add(&b, ",\"text\":"); json_escape(&b, content.text);
    sb_add(&b, "}]}}");
    ca_mcp_resource_content_free(&content);
    return sb_take(&b);
}

/* ── dispatcher ─────────────────────────────────────────────────────────── */

char *ca_mcp_dispatch(ca_mcp_registry_t *registry, const char *request_json,
                      const ca_mcp_server_info_t *info) {
    ca_mcp_server_info_t def = {
        info && info->name ? info->name : "circleai-mcp",
        info && info->version ? info->version : "3.2.0",
        info && info->description ? info->description : "CircleAI MCP endpoint",
    };

    if (!request_json || !request_json[0]) return mcp_error(NULL, -32600, "Invalid Request");

    char *id_raw = find_raw(request_json, "id");
    char *jsonrpc = find_string(request_json, "jsonrpc");
    char *method = NULL;
    if (jsonrpc && strcmp(jsonrpc, "2.0") == 0) method = find_string(request_json, "method");
    free(jsonrpc);

    if (!method) {
        char *e = mcp_error(id_raw, -32600, "Invalid Request: missing jsonrpc or method");
        free(id_raw);
        return e;
    }

    char *params_raw = find_raw(request_json, "params");
    char *response = NULL;

    if (strcmp(method, "initialize") == 0) {
        response = handle_initialize(id_raw, &def);
    } else if (strcmp(method, "notifications/initialized") == 0) {
        response = NULL; /* notification */
    } else if (strcmp(method, "tools/list") == 0) {
        response = handle_tools_list(id_raw, registry);
    } else if (strcmp(method, "tools/call") == 0) {
        response = handle_tools_call(id_raw, params_raw, registry);
    } else if (strcmp(method, "resources/list") == 0) {
        response = handle_resources_list(id_raw, registry);
    } else if (strcmp(method, "resources/read") == 0) {
        response = handle_resources_read(id_raw, params_raw, registry);
    } else {
        size_t n = strlen(method) + 24;
        char *msg = (char *)malloc(n);
        if (msg) snprintf(msg, n, "Method not found: %s", method);
        response = mcp_error(id_raw, -32601, msg ? msg : "Method not found");
        free(msg);
    }

    free(id_raw); free(method); free(params_raw);
    return response;
}
