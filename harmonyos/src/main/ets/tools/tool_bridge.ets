/**
 * tools/tool_bridge
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// tools/tool_bridge.ts
//
// IToolBridge — the bridge between the local LLM and the TheGeekNetwork APIs.
// Port of CircleAI.Tools.IToolBridge. Implementations route tool calls to the
// appropriate API client (HTTP, in-process service, etc.).

import type { ToolDefinition, ToolInvocation, ToolResult } from "./index";

/**
 * Bridge between the local LLM and the TheGeekNetwork APIs. Mirrors
 * `CircleAI.Tools.IToolBridge`.
 */
export interface IToolBridge {
  /** The tools available through this bridge (synchronous view). */
  readonly availableTools: readonly ToolDefinition[];

  /** Invoke a tool and return its structured result. */
  invokeAsync(invocation: ToolInvocation, signal?: AbortSignal): Promise<ToolResult>;

  /**
   * Returns the tools available through this bridge by querying the remote
   * service. Optional — implementations that expose a static tool list may
   * return {@link availableTools}. (C# ships a default returning `AvailableTools`.)
   */
  getAvailableToolsAsync?(signal?: AbortSignal): Promise<readonly ToolDefinition[]>;
}
