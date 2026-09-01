/**
 * telephony/mcp_tool_importer
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// telephony/mcp_tool_importer.ts
//
// Pull tool definitions from an MCP (Model Context Protocol) server at call
// start — faithful port of McpToolImporter.cs. Each remote tool registers into
// the local IToolCallRegistry as a webhook-style tool that forwards calls back
// to the MCP server.
//
// HTTP SEAM. The C# `HttpClient` + `JsonContent` → the injected {@link IHttpClient}
// with a JSON-string body. The JSON-RPC `tools/list` request and the
// `?remote_tool=<name>` invoke-URL construction are preserved exactly.

import type { HttpRequest, IHttpClient } from "./contracts";
import { isSuccessStatusCode } from "./contracts";
import type { IToolCallRegistry, ToolDefinition } from "./tool_calling";
import { toolDefinition } from "./tool_calling";
import type { ILogger } from "./tool_calling";

/** Description of one MCP tool returned from `tools/list`. Mirrors `McpToolDescriptor`. */
export interface McpToolDescriptor {
  readonly name: string;
  readonly description: string;
  readonly inputJsonSchema: string;
}

/** MCP server descriptor. Mirrors `McpServerConfig`. */
export interface McpServerConfig {
  /** HTTP endpoint of the MCP server. */
  readonly serverEndpoint: string;
  /** Optional `Authorization` header to attach (e.g. `Bearer ...`). */
  readonly authorizationHeader?: string;
  /** Optional prefix applied to imported tool names to avoid collisions. */
  readonly toolNamePrefix?: string;
}

/** Imports tools from MCP servers into a tool registry. Mirrors `IMcpToolImporter`. */
export interface IMcpToolImporter {
  importAsync(
    registry: IToolCallRegistry,
    server: McpServerConfig,
    signal?: AbortSignal,
  ): Promise<readonly ToolDefinition[]>;
}

function appendQuery(baseUrl: string, key: string, value: string): string {
  const u = new URL(baseUrl);
  u.searchParams.append(key, value);
  return u.toString();
}

/** HTTP-backed importer (tools list + invoke via JSON-RPC over HTTP). Mirrors `HttpMcpToolImporter`. */
export class HttpMcpToolImporter implements IMcpToolImporter {
  private readonly http: IHttpClient;
  private readonly logger?: ILogger;

  constructor(http: IHttpClient, logger?: ILogger) {
    if (http === null || http === undefined) throw new Error("http is required");
    this.http = http;
    this.logger = logger;
  }

  async importAsync(
    registry: IToolCallRegistry,
    server: McpServerConfig,
    signal?: AbortSignal,
  ): Promise<readonly ToolDefinition[]> {
    if (registry === null || registry === undefined) throw new Error("registry is required");
    if (server === null || server === undefined) throw new Error("server is required");

    const listRequest = {
      jsonrpc: "2.0",
      id: 1,
      method: "tools/list",
      params: {},
    };

    const headers = new Map<string, string>([["Content-Type", "application/json"]]);
    if (server.authorizationHeader && server.authorizationHeader.trim().length > 0) {
      headers.set("Authorization", server.authorizationHeader);
    }
    const req: HttpRequest = {
      method: "POST",
      url: server.serverEndpoint,
      headers,
      body: JSON.stringify(listRequest),
    };

    const resp = await this.http.send(req, signal);
    if (!isSuccessStatusCode(resp.statusCode)) {
      this.logger?.warn(`MCP server ${server.serverEndpoint} returned ${resp.statusCode}`);
      return [];
    }

    let root: Map<string, unknown>;
    try {
      root = JSON.parse(resp.body) as Map<string, unknown>;
    } catch {
      return [];
    }

    const result = root["result"];
    if (result === null || typeof result !== "object") return [];
    const tools = (result as Map<string, unknown>)["tools"];
    if (!Array.isArray(tools)) return [];

    const imported: ToolDefinition[] = [];
    for (const entry of tools) {
      if (entry === null || typeof entry !== "object") continue;
      const e = entry as Map<string, unknown>;
      const name = typeof e["name"] === "string" ? (e["name"] as string) : undefined;
      const description = typeof e["description"] === "string" ? (e["description"] as string) : "";
      const schema =
        e["inputSchema"] !== undefined ? JSON.stringify(e["inputSchema"]) : "{}";
      if (!name || name.trim().length === 0) continue;

      const localName =
        server.toolNamePrefix && server.toolNamePrefix.trim().length > 0
          ? `${server.toolNamePrefix}${name}`
          : name;
      const def = toolDefinition(localName, description, schema);

      // Register a webhook-style entry whose invocation forwards back to the MCP server's tools/call method.
      const invokeUrl = appendQuery(server.serverEndpoint, "remote_tool", name);
      registry.registerWebhook(def, invokeUrl);
      imported.push(def);
    }

    return imported;
  }
}
