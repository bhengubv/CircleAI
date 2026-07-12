// telephony/voice_loop_as_tool.ts
//
// Expose the CircleAI voice loop as a tool an external agent framework
// (LangGraph, OpenAI Agents, CrewAI) can call — faithful port of
// VoiceLoopAsTool.cs. The framework hands us a number to call + a script, we
// drive the call to completion, return a structured result.
//
// The actual call runner is injected. `MaxDuration` (TimeSpan) → milliseconds;
// the per-call timeout links to the caller's signal via {@link LinkedTimeout}
// and distinguishes a timeout from a caller-cancellation, exactly as the C#.

import type { ToolDefinition } from "./tool_calling.js";
import { toolDefinition } from "./tool_calling.js";
import { LinkedTimeout, isCancellation } from "./internal.js";

/** Request to make one outbound voice call as a tool invocation. Mirrors `VoiceLoopToolRequest`. */
export interface VoiceLoopToolRequest {
  /** E.164 destination number. */
  readonly toNumber: string;
  /** Plain-English goal ("Book a haircut for Sipho on Saturday"). */
  readonly goal: string;
  /** Extra structured context the agent needs. */
  readonly contextJson?: string;
  /** Persona / script for the voice agent. */
  readonly systemPrompt?: string;
  /** Hard ceiling on call length, in milliseconds. */
  readonly maxDurationMs?: number;
}

/** Result of the call returned to the calling agent. Mirrors `VoiceLoopToolResult`. */
export interface VoiceLoopToolResult {
  /** True if the AI reports it completed the goal. */
  readonly goalAchieved: boolean;
  /** Natural-language summary the AI wrote. */
  readonly summary: string;
  /** Carrier call id. */
  readonly callId: string;
  /** Actual call duration, in milliseconds. */
  readonly durationMs: number;
  /** Full conversation transcript. */
  readonly transcript: string;
  /** Optional JSON the AI extracted (e.g. appointment time). */
  readonly structuredOutputJson?: string;
}

/** Voice-loop-as-a-tool surface. Mirrors `IVoiceLoopTool`. */
export interface IVoiceLoopTool {
  /** Make the call and report back. */
  invokeAsync(request: VoiceLoopToolRequest, signal?: AbortSignal): Promise<VoiceLoopToolResult>;
}

/** The host-supplied runner that actually drives the call. */
export type VoiceLoopRunner = (
  request: VoiceLoopToolRequest,
  signal?: AbortSignal,
) => Promise<VoiceLoopToolResult>;

/** Driver that delegates the actual call to a host-supplied runner. Mirrors `VoiceLoopAsTool`. */
export class VoiceLoopAsTool implements IVoiceLoopTool {
  private readonly runner: VoiceLoopRunner;
  private readonly defaultMaxDurationMs: number;

  constructor(runner: VoiceLoopRunner, defaultMaxDurationMs?: number) {
    if (runner === null || runner === undefined) throw new Error("runner is required");
    this.runner = runner;
    this.defaultMaxDurationMs = defaultMaxDurationMs ?? 5 * 60 * 1000; // 5 minutes
  }

  async invokeAsync(
    request: VoiceLoopToolRequest,
    signal?: AbortSignal,
  ): Promise<VoiceLoopToolResult> {
    if (request === null || request === undefined) throw new Error("request is required");
    if (!request.toNumber || request.toNumber.trim().length === 0) {
      throw new Error("ToNumber is required.");
    }
    if (!request.goal || request.goal.trim().length === 0) {
      throw new Error("Goal is required.");
    }

    const maxDurationMs = request.maxDurationMs ?? this.defaultMaxDurationMs;
    const linked = new LinkedTimeout(signal, maxDurationMs);
    try {
      return await this.runner(request, linked.signal);
    } catch (ex) {
      if (isCancellation(ex) && linked.timedOut) {
        return {
          goalAchieved: false,
          summary: `Call timed out after ${(maxDurationMs / 60000).toFixed(1)} minutes.`,
          callId: "",
          durationMs: maxDurationMs,
          transcript: "",
          structuredOutputJson: undefined,
        };
      }
      throw ex;
    } finally {
      linked.dispose();
    }
  }

  /** Tool descriptor for use with {@link IToolCallRegistry}. Mirrors static `Descriptor`. */
  static get descriptor(): ToolDefinition {
    return toolDefinition(
      "make_voice_call",
      "Place an outbound phone call and follow the supplied goal/script. Returns whether the goal was achieved.",
      [
        "{",
        '  "type": "object",',
        '  "properties": {',
        '    "to_number":     { "type": "string", "description": "E.164 destination." },',
        '    "goal":          { "type": "string" },',
        '    "context_json":  { "type": "string", "nullable": true },',
        '    "system_prompt": { "type": "string", "nullable": true },',
        '    "max_duration_seconds": { "type": "integer", "nullable": true }',
        "  },",
        '  "required": ["to_number", "goal"]',
        "}",
      ].join("\n"),
    );
  }
}
