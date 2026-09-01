/**
 * tools/composio_tool_bridge
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// tools/composio_tool_bridge.ts
//
// Routes B! tool calls to a Composio MCP server via JSON-RPC 2.0 over HTTP.
// Composio provides 250+ integrations (Gmail, Slack, GitHub, Calendar, …)
// through a single MCP endpoint. Port of CircleAI.Tools.ComposioToolBridge.
//
// The .NET original owns an HttpClient; here the transport is injected behind
// the shared IHttpClient seam (integration/index.ts). The bridge sends every
// tool invocation as a `tools/call` JSON-RPC 2.0 request and interprets the
// response envelope to produce a ToolResult. Tool discovery calls
// GET {serverUri}/tools and maps each returned entry to a ToolDefinition.
//
// Response bodies (integration HttpResponse.body is a string) are parsed with
// JSON.parse — the analogue of ReadFromJsonAsync<JsonElement>; the parsed value
// is walked defensively (the C# JsonElement.TryGetProperty pattern).

import {
  isSuccessStatusCode,
  resolveUrl,
  type HttpRequest,
  type HttpResponse,
  type IHttpClient,
} from "../integration/index";
import type { IToolBridge } from "./tool_bridge";
import { toolResultFailure, toolResultOk, type ToolDefinition, type ToolInvocation, type ToolParameter, type ToolResult } from "./index";

const DEFAULT_SERVER_URI = "https://mcp.composio.dev/";

/**
 * Routes tool calls to a Composio MCP server via JSON-RPC 2.0 over HTTP. Mirrors
 * `CircleAI.Tools.ComposioToolBridge`.
 */
export class ComposioToolBridge implements IToolBridge {
  private readonly apiKey: string;
  private readonly serverUri: string;
  private readonly http: IHttpClient;
  private available: readonly ToolDefinition[] = [];

  /**
   * @param composioApiKey Composio API key sent in the `X-API-Key` header.
   * @param httpClient Injected HTTP transport (the wire seam).
   * @param serverUri Base URI of the Composio MCP endpoint. Defaults to
   *   `https://mcp.composio.dev/`.
   */
  constructor(composioApiKey: string, httpClient: IHttpClient, serverUri?: string) {
    if (composioApiKey === null || composioApiKey === undefined || composioApiKey.trim().length === 0) {
      throw new Error("composioApiKey must not be null or whitespace.");
    }
    if (httpClient === null || httpClient === undefined) throw new Error("httpClient is required.");

    this.apiKey = composioApiKey;
    this.http = httpClient;
    this.serverUri = ensureTrailingSlash(serverUri ?? DEFAULT_SERVER_URI);
  }

  /**
   * Synchronous available-tools list. Empty by default; call
   * {@link getAvailableToolsAsync} to populate via the Composio API.
   */
  get availableTools(): readonly ToolDefinition[] {
    return this.available;
  }

  async invokeAsync(invocation: ToolInvocation, signal?: AbortSignal): Promise<ToolResult> {
    if (invocation === null || invocation === undefined) throw new Error("invocation is required.");
    if (invocation.toolName === null || invocation.toolName === undefined || invocation.toolName.trim().length === 0) {
      throw new Error("ToolName must not be null or whitespace.");
    }

    const requestBody = {
      jsonrpc: "2.0",
      method: "tools/call",
      id: 1,
      params: {
        name: invocation.toolName,
        arguments: invocation.arguments ?? {},
      },
    };

    const endpoint = resolveUrl(this.serverUri, `tools/${encodeURIComponent(invocation.toolName)}/invoke`);

    try {
      const request = this.buildRequest("POST", endpoint, requestBody);
      const response: HttpResponse = await this.http.send(request, signal);

      const body = parseJson(response.body);

      if (!isSuccessStatusCode(response.statusCode)) {
        const httpError = `HTTP ${response.statusCode}`;
        return toolResultFailure(invocation.toolName, extractError(body, httpError));
      }

      // Standard JSON-RPC 2.0 response: { "result": ..., "error": ... }
      if (isObject(body) && "error" in body && body.error !== null && body.error !== undefined) {
        const err = body.error;
        const msg = isObject(err) && typeof err.message === "string" ? err.message : jsonToString(err);
        return toolResultFailure(invocation.toolName, msg);
      }

      if (isObject(body) && "result" in body) {
        return toolResultOk(invocation.toolName, body.result);
      }

      // No result / error — treat as success with null payload.
      return toolResultOk(invocation.toolName);
    } catch (ex) {
      if (isAbort(ex, signal)) throw ex;
      return toolResultFailure(invocation.toolName, errorMessage(ex));
    }
  }

  /**
   * Fetches the list of tools available on the Composio MCP server
   * (`GET {serverUri}/tools`) and caches it in {@link availableTools}.
   */
  async getAvailableToolsAsync(signal?: AbortSignal): Promise<readonly ToolDefinition[]> {
    const endpoint = resolveUrl(this.serverUri, "tools");

    try {
      const request = this.buildRequest("GET", endpoint, null);
      const response: HttpResponse = await this.http.send(request, signal);

      if (!isSuccessStatusCode(response.statusCode)) return [];

      const root = parseJson(response.body);
      const tools = parseToolList(root);
      this.available = tools;
      return tools;
    } catch (ex) {
      if (isAbort(ex, signal)) throw ex;
      return [];
    }
  }

  private buildRequest(
    method: "GET" | "POST",
    url: string,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    body: Object,
  ): HttpRequest {
    const headers = new Map<string, string>();
    headers.set("Accept", "application/json");
    headers.set("X-API-Key", this.apiKey);
    let serialized: string | undefined;
    if (body !== null && body !== undefined) {
      serialized = JSON.stringify(body);
      headers.set("Content-Type", "application/json");
    }
    return { method, url, headers, body: serialized };
  }
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function parseToolList(root: Object): ToolDefinition[] {
  // Composio may return an array at root, or { "tools": [...] }.
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  let toolsArray: Object[];
  if (Array.isArray(root)) {
    toolsArray = root;
  } else if (isObject(root) && Array.isArray(root.tools)) {
    toolsArray = root.tools;
  } else {
    return [];
  }

  const result: ToolDefinition[] = [];
  for (const item of toolsArray) {
    if (!isObject(item)) continue;
    const name = typeof item.name === "string" ? item.name : null;
    const desc = typeof item.description === "string" ? item.description : "";

    if (name === null || name.trim().length === 0) continue;

    const parameters: Map<string, ToolParameter> = {};
    const required: string[] = [];

    const schema = item.inputSchema;
    if (isObject(schema) && isObject(schema.properties)) {
      for (const propName of Object.keys(schema.properties)) {
        const prop = schema.properties[propName];
        const type = isObject(prop) && typeof prop.type === "string" ? prop.type : "string";
        const propDesc = isObject(prop) && typeof prop.description === "string" ? prop.description : "";
        parameters[propName] = { type, description: propDesc };
      }

      if (Array.isArray(schema.required)) {
        for (const r of schema.required) {
          if (typeof r === "string" && r.trim().length > 0) required.push(r);
        }
      }
    }

    result.push({
      name,
      description: desc,
      parameters,
      requiredParameters: required,
    });
  }

  return result;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function extractError(body: Object, fallback: string): string {
  if (isObject(body) && "error" in body) {
    const e = body.error;
    if (isObject(e) && typeof e.message === "string") return e.message;
    return jsonToString(e);
  }
  return fallback;
}

function ensureTrailingSlash(uri: string): string {
  return uri.endsWith("/") ? uri : uri + "/";
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function parseJson(body: string): Object {
  if (body === undefined || body === null || body.length === 0) return null;
  try {
    return JSON.parse(body);
  } catch {
    return null;
  }
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function isObject(v: Object): v is Map<string, any> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function jsonToString(v: Object): string {
  try {
    return JSON.stringify(v);
  } catch {
    return String(v);
  }
}

function isAbort(ex: Object, signal?: AbortSignal): boolean {
  return signal?.aborted === true || (ex instanceof Error && ex.name === "AbortError");
}

function errorMessage(ex: Object): string {
  return ex instanceof Error ? ex.message : String(ex);
}
