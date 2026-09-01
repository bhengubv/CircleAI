/**
 * telephony/agent_handoff
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// telephony/agent_handoff.ts
//
// Multi-agent handoff — faithful port of AgentHandoff.cs. Swap the AI persona
// mid-call without dropping the carrier leg. Caller A is talking to "Reception";
// the reception agent decides this is a billing question, hands off to
// "Billing" — same call, same audio stream, different system prompt and toolset.

import type { BriefingSynthesiser, ICallSession } from "./contracts";
import type { ILogger } from "./tool_calling";
import { audioFrame, CallMediaFormat } from "./primitives";

/** One AI agent persona that can be handed control of a call. Mirrors `CallAgent`. */
export interface CallAgent {
  /** Stable id ("reception" / "billing" / "tier2-support"). */
  readonly agentId: string;
  /** Friendly name surfaced to logging + analytics. */
  readonly displayName: string;
  /** Persona instructions. */
  readonly systemPrompt: string;
  /** Optional first sentence the agent says when it takes over. */
  readonly greetingText?: string;
}

/** Constructs a {@link CallAgent}. */
export function callAgent(
  agentId: string,
  displayName: string,
  systemPrompt: string,
  greetingText?: string,
): CallAgent {
  return { agentId, displayName, systemPrompt, greetingText };
}

/** Outcome of a handoff attempt. Mirrors `HandoffResult`. */
export interface HandoffResult {
  readonly succeeded: boolean;
  readonly failureReason?: string;
  readonly activeAgent?: CallAgent;
}

/** Drives mid-call agent handoff. Mirrors `IAgentHandoffOrchestrator`. */
export interface IAgentHandoffOrchestrator {
  /** The agent currently in control of the call. */
  readonly currentAgent: CallAgent | null;

  /** Available agents indexed by id. */
  readonly agentCatalog: ReadonlyMap<string, CallAgent>;

  /** Hand the call over to `targetAgentId`; speaks the greeting via the supplied TTS. */
  handoffAsync(
    session: ICallSession,
    targetAgentId: string,
    tts: BriefingSynthesiser,
    signal?: AbortSignal,
  ): Promise<HandoffResult>;

  /** Register / replace an agent in the catalog at runtime. */
  registerAgent(agent: CallAgent): void;

  /** Set the initial agent on a fresh call without TTS (no greeting). */
  setInitialAgent(agentId: string): void;
}

/** Default in-memory orchestrator. Mirrors `DefaultAgentHandoffOrchestrator`. */
export class DefaultAgentHandoffOrchestrator implements IAgentHandoffOrchestrator {
  private readonly agents = new Map<string, CallAgent>(); // key: lowercased agent id
  private current: CallAgent | null = null;
  private readonly logger?: ILogger;

  constructor(seed?: Iterable<CallAgent>, logger?: ILogger) {
    this.logger = logger;
    if (seed !== undefined && seed !== null) {
      for (const agent of seed) {
        this.agents.set(agent.agentId.toLowerCase(), agent);
      }
    }
  }

  get currentAgent(): CallAgent | null {
    return this.current;
  }

  get agentCatalog(): ReadonlyMap<string, CallAgent> {
    // Return a copy keyed by the original (case-preserving) agent id.
    const copy = new Map<string, CallAgent>();
    for (const agent of this.agents.values()) copy.set(agent.agentId, agent);
    return copy;
  }

  registerAgent(agent: CallAgent): void {
    if (agent === null || agent === undefined) throw new Error("agent is required");
    if (!agent.agentId || agent.agentId.trim().length === 0) {
      throw new Error("AgentId is required.");
    }
    this.agents.set(agent.agentId.toLowerCase(), agent);
  }

  setInitialAgent(agentId: string): void {
    const agent = this.agents.get(agentId.toLowerCase());
    if (agent === undefined) {
      throw new Error(`Agent '${agentId}' is not registered.`);
    }
    this.current = agent;
  }

  async handoffAsync(
    session: ICallSession,
    targetAgentId: string,
    tts: BriefingSynthesiser,
    signal?: AbortSignal,
  ): Promise<HandoffResult> {
    if (session === null || session === undefined) throw new Error("session is required");
    if (tts === null || tts === undefined) throw new Error("tts is required");
    if (!targetAgentId || targetAgentId.trim().length === 0) {
      return { succeeded: false, failureReason: "targetAgentId is required", activeAgent: this.current ?? undefined };
    }

    const target = this.agents.get(targetAgentId.toLowerCase());
    if (target === undefined) {
      return {
        succeeded: false,
        failureReason: `Agent '${targetAgentId}' is not registered.`,
        activeAgent: this.current ?? undefined,
      };
    }
    const previous = this.current;
    if (previous?.agentId.toLowerCase() === target.agentId.toLowerCase()) {
      return { succeeded: true, activeAgent: previous };
    }
    this.current = target;

    this.logger?.warn(
      `Call ${session.info.callId} handed off from ${previous?.displayName ?? "(none)"} to ${target.displayName}`,
    );

    if (target.greetingText && target.greetingText.trim().length > 0) {
      try {
        const greetingPcm = await tts(target.greetingText, signal);
        if (greetingPcm.length > 0) {
          await session.sendAudioAsync(audioFrame(greetingPcm, CallMediaFormat.Pcm24000, 0), signal);
        }
      } catch (ex) {
        this.logger?.warn(`Greeting playback failed during handoff to ${target.agentId}`, ex);
      }
    }

    return { succeeded: true, activeAgent: target };
  }
}
