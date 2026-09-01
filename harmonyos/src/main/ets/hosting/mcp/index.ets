/**
 * hosting/mcp/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/mcp/index.ts
//
// Port of CircleAI.Hosting.Mcp:
//   • Contracts.cs — IMcpTool, IMcpResourceProvider, McpResource,
//     McpResourceContent, McpToolException
//   • McpEndpoints.cs — the JSON-RPC 2.0 dispatcher (initialize, tools/list,
//     tools/call, resources/list, resources/read) plus the manifest shape
//
// The C# endpoint resolves IMcpTool / IMcpResourceProvider from the ASP.NET DI
// container and maps POST /mcp. The TS port keeps the pure, framework-free
// DispatchAsync entry point — the same one C# exposes for testing — and takes a
// {@link McpRegistry} (the DI seam) holding the tool + provider collections.
// Request/response bodies are plain JSON objects (JsonNode → unknown).

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/** One MCP tool the host exposes. Mirrors CircleAI.Hosting.Mcp.IMcpTool. */
export interface IMcpTool {
  /** Unique tool name (snake_case by convention). */
  readonly name: string;
  /** One-line description shown in tool listings. */
  readonly description: string;
  /** JSON Schema describing the tool's arguments object. */
  readonly inputSchema: unknown;
  /**
   * Execute the tool. Return any JSON-serialisable value; the dispatcher wraps
   * it in MCP's {content:[{type:"text",text}]} envelope. Throw
   * {@link McpToolException} to signal a tool-level error (isError:true).
   */
  executeAsync(args: Record<string, unknown>): Promise<unknown>;
}

/** One MCP resource provider. Mirrors IMcpResourceProvider. */
export interface IMcpResourceProvider {
  /** e.g. "vault://", "models://". */
  readonly uriScheme: string;
  /** List every resource this provider serves. */
  listAsync(): Promise<readonly McpResource[]>;
  /** Read one resource by uri. Returns null on not-found. */
  readAsync(uri: string): Promise<McpResourceContent | null>;
}

/** One MCP resource descriptor. Mirrors McpResource. */
export interface McpResource {
  readonly uri: string;
  readonly name: string;
  readonly description: string | null;
  readonly mimeType: string;
}

/** One MCP resource content (returned by resources/read). Mirrors McpResourceContent. */
export interface McpResourceContent {
  readonly uri: string;
  readonly mimeType: string;
  readonly text: string;
}

/**
 * Thrown from inside {@link IMcpTool.executeAsync} to signal a tool-level error
 * (vs an MCP protocol error). Mirrors McpToolException.
 */
export class McpToolException extends Error {
  constructor(message: string) {
    super(message);
    this.name = "McpToolException";
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Server info + registry (DI seam)
// ─────────────────────────────────────────────────────────────────────────────

/** Server descriptor. Mirrors McpEndpoints.McpServerInfo. */
export interface McpServerInfo {
  readonly name?: string;
  readonly version?: string;
  readonly description?: string;
}

const DEFAULT_INFO: Required<McpServerInfo> = {
  name: "circleai-mcp",
  version: "3.2.0",
  description: "CircleAI MCP endpoint",
};

/**
 * The DI seam the dispatcher resolves tools + providers from (C# uses
 * IServiceProvider.GetServices).
 */
export interface McpRegistry {
  readonly tools: readonly IMcpTool[];
  readonly resourceProviders: readonly IMcpResourceProvider[];
}

// ─────────────────────────────────────────────────────────────────────────────
// Dispatcher
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Pure-DI JSON-RPC 2.0 dispatcher. Returns null for notifications. Mirrors
 * McpEndpoints.DispatchAsync.
 */
export async function dispatchAsync(
  req: unknown,
  registry: McpRegistry,
  info: McpServerInfo = DEFAULT_INFO,
): Promise<unknown | null> {
  const resolved: Required<McpServerInfo> = { ...DEFAULT_INFO, ...info };

  if (req == null || !isObject(req)) return mcpError(null, -32600, "Invalid Request");

  const id = req["id"] ?? null;
  const jsonrpc = req["jsonrpc"];
  const method = jsonrpc === "2.0" ? (req["method"] as string | undefined) : undefined;
  if (method == null)
    return mcpError(id, -32600, "Invalid Request: missing jsonrpc or method");

  const params = req["params"];
  try {
    switch (method) {
      case "initialize":
        return handleInitialize(id, resolved);
      case "notifications/initialized":
        return null;
      case "tools/list":
        return handleToolsList(id, registry);
      case "tools/call":
        return await handleToolsCall(id, params, registry);
      case "resources/list":
        return await handleResourcesList(id, registry);
      case "resources/read":
        return await handleResourcesRead(id, params, registry);
      default:
        return mcpError(id, -32601, `Method not found: ${method}`);
    }
  } catch (ex) {
    return mcpError(id, -32603, `Internal error: ${ex instanceof Error ? ex.message : String(ex)}`);
  }
}

/**
 * The legacy GET /mcp/manifest body. Mirrors the manifest object built in
 * McpEndpoints.MapMcpApi.
 */
export function buildManifest(
  registry: McpRegistry,
  info: McpServerInfo = DEFAULT_INFO,
): unknown {
  const resolved: Required<McpServerInfo> = { ...DEFAULT_INFO, ...info };
  return {
    name: resolved.name,
    version: resolved.version,
    description: resolved.description,
    deprecated: true,
    deprecationNotice: "Use POST /mcp with JSON-RPC 2.0 instead.",
    tools: registry.tools.map((t) => ({
      name: t.name,
      description: t.description,
      inputSchema: t.inputSchema,
    })),
  };
}

function handleInitialize(id: unknown, info: Required<McpServerInfo>): unknown {
  return mcpResult(id, {
    protocolVersion: "2024-11-05",
    serverInfo: { name: info.name, version: info.version },
    capabilities: {
      tools: { listChanged: false },
      resources: { listChanged: false, subscribe: false },
    },
  });
}

function handleToolsList(id: unknown, registry: McpRegistry): unknown {
  const tools = registry.tools.map((t) => ({
    name: t.name,
    description: t.description,
    inputSchema: t.inputSchema,
  }));
  return mcpResult(id, { tools });
}

async function handleToolsCall(
  id: unknown,
  params: unknown,
  registry: McpRegistry,
): Promise<unknown> {
  const toolName = isObject(params) ? (params["name"] as string | undefined) : undefined;
  if (toolName == null || toolName.trim().length === 0)
    return mcpError(id, -32602, "Invalid params: 'name' is required");

  const tool = registry.tools.find((t) => t.name === toolName);
  if (tool === undefined) return mcpError(id, -32602, `Unknown tool: ${toolName}`);

  const argsNode = isObject(params) ? params["arguments"] : undefined;
  const args: Record<string, unknown> = isObject(argsNode) ? argsNode : {};
  try {
    const result = await tool.executeAsync(args);
    return mcpToolResult(id, result);
  } catch (ex) {
    if (ex instanceof McpToolException) return mcpToolError(id, ex.message);
    throw ex;
  }
}

async function handleResourcesList(id: unknown, registry: McpRegistry): Promise<unknown> {
  const resources: McpResource[] = [];
  for (const p of registry.resourceProviders) {
    const page = await p.listAsync();
    resources.push(...page);
  }
  return mcpResult(id, {
    resources: resources.map((r) => ({
      uri: r.uri,
      name: r.name,
      description: r.description ?? r.name,
      mimeType: r.mimeType,
    })),
  });
}

async function handleResourcesRead(
  id: unknown,
  params: unknown,
  registry: McpRegistry,
): Promise<unknown> {
  const uri = isObject(params) ? (params["uri"] as string | undefined) : undefined;
  if (uri == null || uri.trim().length === 0)
    return mcpError(id, -32602, "Invalid params: 'uri' is required");

  const provider = registry.resourceProviders.find((p) =>
    uri.toLowerCase().startsWith(p.uriScheme.toLowerCase()),
  );
  if (provider === undefined)
    return mcpError(id, -32602, `No provider for URI scheme: ${uri}`);

  const content = await provider.readAsync(uri);
  if (content == null) return mcpError(id, -32602, `Resource not found: ${uri}`);

  return mcpResult(id, {
    contents: [{ uri: content.uri, mimeType: content.mimeType, text: content.text }],
  });
}

// ── envelope helpers (match C# McpResult / McpToolResult / McpErrorObj) ──────────
//
// C# serialises `id` via id?.ToJsonString(), i.e. the JSON *text* of the id
// node (e.g. the number 1 becomes the string "1"). We reproduce that exactly.

function idToJsonString(id: unknown): string | null {
  return id == null ? null : JSON.stringify(id);
}

function mcpResult(id: unknown, result: unknown): unknown {
  return { jsonrpc: "2.0", id: idToJsonString(id), result };
}

function mcpToolResult(id: unknown, data: unknown): unknown {
  return mcpResult(id, {
    content: [{ type: "text", text: JSON.stringify(data) }],
    isError: false,
  });
}

function mcpToolError(id: unknown, message: string): unknown {
  return mcpResult(id, {
    content: [{ type: "text", text: message }],
    isError: true,
  });
}

function mcpError(id: unknown, code: number, message: string): unknown {
  return { jsonrpc: "2.0", id: idToJsonString(id), error: { code, message } };
}

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}
