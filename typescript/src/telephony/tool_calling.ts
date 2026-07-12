// telephony/tool_calling.ts
//
// Tool-calling for the voice loop — faithful port of ToolCalling.cs. The AI
// emits a tool call during a turn; the orchestrator dispatches it to either a
// local handler or an HTTPS webhook and returns the result for the next turn.
//
// HTTP SEAM. The C# uses `HttpClient` + `JsonContent`. We inject {@link IHttpClient}
// (contracts.ts) and build the request body as a JSON string, so the dispatch is
// deterministic and framework-free. Webhook payload shape is preserved exactly:
// `{ call_id, tool, arguments }` where `arguments` is the parsed argument JSON.

import type { HttpRequest, IHttpClient } from "./contracts.js";
import { isSuccessStatusCode } from "./contracts.js";

/**
 * Tool definition surfaced to the LLM. Mirrors telephony's `ToolDefinition`
 * (note: a distinct shape from `CircleAI.Tools.ToolDefinition`; at the package
 * root this is re-exported as `TelephonyToolDefinition`).
 */
export interface ToolDefinition {
  /** Tool name (function call name). */
  readonly name: string;
  /** Human description used to pick the tool. */
  readonly description: string;
  /** JSON Schema describing the arguments. */
  readonly argumentsJsonSchema: string;
}

/** Constructs a {@link ToolDefinition}. */
export function toolDefinition(
  name: string,
  description: string,
  argumentsJsonSchema: string,
): ToolDefinition {
  return { name, description, argumentsJsonSchema };
}

/** An invocation of one tool by the model. Mirrors telephony's `ToolInvocation`. */
export interface ToolInvocation {
  readonly callId: string;
  readonly toolName: string;
  readonly argumentsJson: string;
}

/** Constructs a {@link ToolInvocation}. */
export function toolInvocation(
  callId: string,
  toolName: string,
  argumentsJson: string,
): ToolInvocation {
  return { callId, toolName, argumentsJson };
}

/** Result of a tool invocation. Mirrors telephony's `ToolResult`. */
export interface ToolResult {
  readonly callId: string;
  readonly succeeded: boolean;
  readonly resultJson: string;
  readonly error?: string;
}

/** Constructs a {@link ToolResult}. */
export function toolResult(
  callId: string,
  succeeded: boolean,
  resultJson: string,
  error?: string,
): ToolResult {
  return { callId, succeeded, resultJson, error };
}

/** In-process tool handler. Mirrors the `LocalToolHandler` delegate. */
export type LocalToolHandler = (argumentsJson: string, signal?: AbortSignal) => Promise<string>;

/**
 * Tool registry: register local handlers OR HTTPS webhook URLs against a tool
 * name; the orchestrator dispatches. Mirrors `IToolCallRegistry`.
 */
export interface IToolCallRegistry {
  /** All registered tool definitions. */
  readonly definitions: readonly ToolDefinition[];

  /** Register a local handler for `definition`. */
  registerLocal(definition: ToolDefinition, handler: LocalToolHandler): void;

  /** Register a webhook URL; the orchestrator POSTs arguments JSON. */
  registerWebhook(definition: ToolDefinition, webhook: string): void;

  /** Invoke one tool call. */
  invokeAsync(invocation: ToolInvocation, signal?: AbortSignal): Promise<ToolResult>;
}

/** Optional sink the default registry logs warnings to (mirrors `ILogger`). */
export interface ILogger {
  warn(message: string, error?: unknown): void;
}

interface ToolEntry {
  readonly def: ToolDefinition;
  readonly local?: LocalToolHandler;
  readonly webhook?: string;
}

function truncate(s: string, max: number): string {
  return s.length <= max ? s : s.slice(0, max) + "…";
}

function isAbsoluteUrl(url: string): boolean {
  try {
    // eslint-disable-next-line no-new
    new URL(url);
    return true;
  } catch {
    return false;
  }
}

/** Default in-memory registry. Mirrors `DefaultToolCallRegistry`. */
export class DefaultToolCallRegistry implements IToolCallRegistry {
  private readonly tools = new Map<string, ToolEntry>(); // key: lowercased tool name
  private readonly http: IHttpClient;
  private readonly logger?: ILogger;

  constructor(http: IHttpClient, logger?: ILogger) {
    if (http === null || http === undefined) throw new Error("http is required");
    this.http = http;
    this.logger = logger;
  }

  get definitions(): readonly ToolDefinition[] {
    const list: ToolDefinition[] = [];
    for (const entry of this.tools.values()) list.push(entry.def);
    return list;
  }

  registerLocal(definition: ToolDefinition, handler: LocalToolHandler): void {
    if (definition === null || definition === undefined) throw new Error("definition is required");
    if (handler === null || handler === undefined) throw new Error("handler is required");
    if (!definition.name || definition.name.trim().length === 0) {
      throw new Error("Tool name is required");
    }
    this.tools.set(definition.name.toLowerCase(), { def: definition, local: handler });
  }

  registerWebhook(definition: ToolDefinition, webhook: string): void {
    if (definition === null || definition === undefined) throw new Error("definition is required");
    if (webhook === null || webhook === undefined) throw new Error("webhook is required");
    if (!isAbsoluteUrl(webhook)) throw new Error("Webhook URL must be absolute.");
    if (!definition.name || definition.name.trim().length === 0) {
      throw new Error("Tool name is required");
    }
    this.tools.set(definition.name.toLowerCase(), { def: definition, webhook });
  }

  async invokeAsync(invocation: ToolInvocation, signal?: AbortSignal): Promise<ToolResult> {
    if (invocation === null || invocation === undefined) throw new Error("invocation is required");
    const entry = this.tools.get(invocation.toolName.toLowerCase());
    if (entry === undefined) {
      return toolResult(
        invocation.callId,
        false,
        "{}",
        `Tool '${invocation.toolName}' is not registered.`,
      );
    }

    try {
      if (entry.local !== undefined) {
        const resultJson = await entry.local(invocation.argumentsJson, signal);
        return toolResult(invocation.callId, true, resultJson ?? "{}");
      }

      if (entry.webhook !== undefined) {
        const parsedArgs: unknown = JSON.parse(invocation.argumentsJson);
        const body = JSON.stringify({
          call_id: invocation.callId,
          tool: invocation.toolName,
          arguments: parsedArgs,
        });
        const req: HttpRequest = {
          method: "POST",
          url: entry.webhook,
          headers: new Map([["Content-Type", "application/json"]]),
          body,
        };
        const resp = await this.http.send(req, signal);
        if (!isSuccessStatusCode(resp.statusCode)) {
          this.logger?.warn(
            `Tool webhook ${invocation.toolName} returned ${resp.statusCode}`,
          );
          return toolResult(
            invocation.callId,
            false,
            "{}",
            `Webhook ${resp.statusCode}: ${truncate(resp.body, 240)}`,
          );
        }
        const bodyOut = resp.body;
        return toolResult(
          invocation.callId,
          true,
          bodyOut && bodyOut.trim().length > 0 ? bodyOut : "{}",
        );
      }

      return toolResult(
        invocation.callId,
        false,
        "{}",
        `Tool '${invocation.toolName}' is registered without a local handler or webhook.`,
      );
    } catch (ex) {
      this.logger?.warn(`Tool ${invocation.toolName} invocation failed`, ex);
      return toolResult(invocation.callId, false, "{}", ex instanceof Error ? ex.message : String(ex));
    }
  }
}
