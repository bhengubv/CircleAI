// hosting/tool_bridge.ts
//
// IToolBridge — the seam between the butler and host-provided tools. Mirrors
// CircleAI.Tools.IToolBridge. Kept as an injectable interface so AIService
// can route InvokeTool without a concrete dependency. A no-op bridge is
// provided for tests / default wiring.

import type {
  ToolDefinition,
  ToolInvocation,
  ToolResult,
} from "../tools/index.js";
import { toolResultFailure } from "../tools/index.js";

/**
 * Bridge between the local LLM and host tools/APIs. Implementations route tool
 * calls to the appropriate client (HTTP, in-process service, etc.).
 */
export interface IToolBridge {
  /** The synchronously-known list of available tools. */
  readonly availableTools: readonly ToolDefinition[];

  /** Execute a single tool invocation. */
  invoke(invocation: ToolInvocation): Promise<ToolResult>;

  /**
   * Returns the tools available through this bridge, optionally by querying a
   * remote service. Default returns the synchronous {@link availableTools}.
   */
  getAvailableTools?(): Promise<readonly ToolDefinition[]>;
}

/**
 * Bridge that exposes no tools and fails every invocation. Handy for tests and
 * as an explicit "tools disabled" wiring.
 */
export class NullToolBridge implements IToolBridge {
  readonly availableTools: readonly ToolDefinition[] = [];

  invoke(invocation: ToolInvocation): Promise<ToolResult> {
    return Promise.resolve(
      toolResultFailure(invocation.toolName, "No tool bridge configured."),
    );
  }

  async getAvailableTools(): Promise<readonly ToolDefinition[]> {
    return this.availableTools;
  }
}
