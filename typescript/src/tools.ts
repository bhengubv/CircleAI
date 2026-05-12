// tools.ts
//
// Tool definitions and bridge contract compatible with OpenAI/Qwen function-call schema.
// The IToolBridge routes tool calls to the appropriate API client.

// ---------------------------------------------------------------------------
// Tool definition types
// ---------------------------------------------------------------------------

/**
 * A single parameter descriptor for a ToolDefinition.
 * type is one of: "string" | "number" | "boolean" | "object" | "array"
 */
export interface ToolParameter {
  /** JSON Schema type: "string", "number", "boolean", "object", "array". */
  readonly type: string;
  readonly description: string;
  /** Allowed enum values (for string parameters). */
  readonly enum?: readonly string[];
}

/**
 * Describes a tool the model can call.
 * Compatible with OpenAI/Qwen function-call schema.
 */
export interface ToolDefinition {
  readonly name: string;
  readonly description: string;
  readonly parameters: Record<string, ToolParameter>;
  readonly requiredParameters: readonly string[];
}

// ---------------------------------------------------------------------------
// Invocation and result types
// ---------------------------------------------------------------------------

/** A tool call produced by the model. */
export interface ToolInvocation {
  readonly toolName:  string;
  readonly arguments: Record<string, unknown>;
}

/** The outcome of executing a ToolInvocation. */
export interface ToolResult {
  readonly toolName: string;
  readonly success:  boolean;
  readonly result:   unknown;
  readonly error:    string | null;
}

/** Convenience factory for a failed tool result. */
export function toolFailure(toolName: string, error: string): ToolResult {
  return { toolName, success: false, result: undefined, error };
}

/** Convenience factory for a successful tool result. */
export function toolSuccess(toolName: string, result?: unknown): ToolResult {
  return { toolName, success: true, result: result ?? null, error: null };
}

// ---------------------------------------------------------------------------
// IToolBridge
// ---------------------------------------------------------------------------

/**
 * Bridge between the local LLM and the TheGeekNetwork APIs.
 * Implementations route tool calls to the appropriate API client
 * (HTTP, in-process service, etc.).
 */
export abstract class IToolBridge {
  /** The synchronously-known list of available tools. */
  abstract readonly availableTools: readonly ToolDefinition[];

  /** Execute a single tool invocation. */
  abstract invoke(invocation: ToolInvocation): Promise<ToolResult>;

  /**
   * Returns the tools available through this bridge by querying the remote service.
   * The default implementation returns the synchronous availableTools list.
   */
  async getAvailableTools(): Promise<readonly ToolDefinition[]> {
    return this.availableTools;
  }
}
