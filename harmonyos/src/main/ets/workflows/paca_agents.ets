/**
 * workflows/paca_agents
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// workflows/paca_agents.ts
//
// (3.3.0) AI agents as first-class project members (paca port,
// PacaAgents.cs). One table for humans + agents — they both have an identity,
// handle, role, avatar. Agents add: LLM config, system prompts
// (task/doc/chat), capability flags, iteration limits + timeout, git identity.
// Five preset templates ship out of the box.
//
// Type mappings: TimeSpan → number (milliseconds); Uri → string | null.

/** (3.3.0) Member kind. Mirrors C# `MemberKind`. */
export enum MemberKind {
  Human = 0,
  Agent = 1,
}

/** (3.3.0) Shared identity for humans + agents in a project. Mirrors C# `ProjectMember`. */
export interface ProjectMember {
  readonly id: string;
  readonly projectId: string;
  readonly kind: MemberKind;
  readonly displayName: string;
  /** "@sipho" or "@billing-agent". */
  readonly handle: string;
  /** "owner" / "developer" / "agent" / etc. */
  readonly role: string;
  readonly avatarUrl: string | null;
  readonly createdAtUtc: Date;
  readonly deletedAtUtc: Date | null;
}

/** (3.3.0) Per-agent LLM config. Mirrors C# `AgentLlmConfig`. */
export interface AgentLlmConfig {
  readonly provider: string;
  readonly model: string;
  readonly apiKey: string | null;
  readonly baseAddress: string | null;
}

/** (3.3.0) Per-agent context-specific system prompts. Mirrors C# `AgentSystemPrompts`. */
export interface AgentSystemPrompts {
  readonly taskPrompt: string | null;
  readonly docPrompt: string | null;
  readonly chatPrompt: string | null;
}

/** (3.3.0) Capability flags an agent is permitted to do. Mirrors C# `AgentCapabilities`. */
export interface AgentCapabilities {
  readonly canCloneRepos: boolean;
  readonly canCreatePRs: boolean;
  readonly canWriteFiles: boolean;
  readonly canCallExternalTools: boolean;
}

/** (3.3.0) Runtime limits an agent must respect. Mirrors C# `AgentLimits`. `timeoutMs` in milliseconds. */
export interface AgentLimits {
  readonly maxIterations: number;
  readonly timeoutMs: number;
}

/** (3.3.0) Git identity an agent uses when committing. Mirrors C# `AgentGitIdentity`. */
export interface AgentGitIdentity {
  readonly name: string;
  readonly email: string;
}

/** (3.3.0) Trigger keywords that wake the agent for each event class. Mirrors C# `AgentTriggers`. */
export interface AgentTriggers {
  readonly taskCreated: string | null;
  readonly chatMention: string | null;
  readonly docEdit: string | null;
  readonly directMention: string | null;
}

/** (3.3.0) Full agent profile. Mirrors C# `AgentProfile`. */
export interface AgentProfile {
  readonly memberId: string;
  readonly llm: AgentLlmConfig;
  readonly prompts: AgentSystemPrompts;
  readonly capabilities: AgentCapabilities;
  readonly limits: AgentLimits;
  readonly gitIdentity: AgentGitIdentity;
  readonly triggers: AgentTriggers;
}

const MINUTE_MS = 60 * 1000;

/** (3.3.0) Five preset agent templates from paca. Mirrors C# `AgentTemplates`. */
export const AgentTemplates = {
  developmentAgent(memberId: string, apiKey: string, baseAddress: string | null = null): AgentProfile {
    return {
      memberId,
      llm: { provider: "openai", model: "gpt-4o-mini", apiKey, baseAddress },
      prompts: {
        taskPrompt: "You are a senior developer. Implement requested changes, write tests, open PRs.",
        docPrompt: "You write engineering docs that are precise and example-driven.",
        chatPrompt: "You answer engineering questions with concrete code samples.",
      },
      capabilities: { canCloneRepos: true, canCreatePRs: true, canWriteFiles: true, canCallExternalTools: true },
      limits: { maxIterations: 25, timeoutMs: 10 * MINUTE_MS },
      gitIdentity: { name: "CircleAI Dev Agent", email: "dev-agent@circleai.local" },
      triggers: { taskCreated: "dev", chatMention: "@dev", docEdit: null, directMention: "dev" },
    };
  },

  productManagerAgent(memberId: string, apiKey: string): AgentProfile {
    return {
      memberId,
      llm: { provider: "openai", model: "gpt-4o-mini", apiKey, baseAddress: null },
      prompts: {
        taskPrompt: "You are a product manager. Triage tasks, break them down, assign owners.",
        docPrompt: "You write product specs and PRDs.",
        chatPrompt: "You answer product/priority questions.",
      },
      capabilities: { canCloneRepos: false, canCreatePRs: false, canWriteFiles: true, canCallExternalTools: true },
      limits: { maxIterations: 15, timeoutMs: 5 * MINUTE_MS },
      gitIdentity: { name: "CircleAI PM Agent", email: "pm-agent@circleai.local" },
      triggers: { taskCreated: "pm", chatMention: "@pm", docEdit: "@pm", directMention: "pm" },
    };
  },

  designerAgent(memberId: string, apiKey: string): AgentProfile {
    return {
      memberId,
      llm: { provider: "openai", model: "gpt-4o-mini", apiKey, baseAddress: null },
      prompts: {
        taskPrompt: "You are a designer. Sketch UI ideas, write copy, propose flows.",
        docPrompt: "You write design memos.",
        chatPrompt: "You answer design questions and propose concepts.",
      },
      capabilities: { canCloneRepos: false, canCreatePRs: false, canWriteFiles: true, canCallExternalTools: false },
      limits: { maxIterations: 10, timeoutMs: 5 * MINUTE_MS },
      gitIdentity: { name: "CircleAI Design Agent", email: "design-agent@circleai.local" },
      triggers: { taskCreated: "design", chatMention: "@design", docEdit: "@design", directMention: "design" },
    };
  },

  qaAgent(memberId: string, apiKey: string): AgentProfile {
    return {
      memberId,
      llm: { provider: "openai", model: "gpt-4o-mini", apiKey, baseAddress: null },
      prompts: {
        taskPrompt: "You are a QA engineer. Write test plans, generate test cases, validate against AC.",
        docPrompt: "You write QA reports.",
        chatPrompt: "You answer QA questions and propose test strategies.",
      },
      capabilities: { canCloneRepos: true, canCreatePRs: false, canWriteFiles: true, canCallExternalTools: true },
      limits: { maxIterations: 20, timeoutMs: 7 * MINUTE_MS },
      gitIdentity: { name: "CircleAI QA Agent", email: "qa-agent@circleai.local" },
      triggers: { taskCreated: "qa", chatMention: "@qa", docEdit: null, directMention: "qa" },
    };
  },

  codeReviewerAgent(memberId: string, apiKey: string): AgentProfile {
    return {
      memberId,
      llm: { provider: "openai", model: "gpt-4o-mini", apiKey, baseAddress: null },
      prompts: {
        taskPrompt: "You are a senior code reviewer. Comment for clarity, correctness, security.",
        docPrompt: "You write code review checklists.",
        chatPrompt: "You answer questions about code patterns and best practices.",
      },
      capabilities: { canCloneRepos: true, canCreatePRs: false, canWriteFiles: false, canCallExternalTools: true },
      limits: { maxIterations: 15, timeoutMs: 7 * MINUTE_MS },
      gitIdentity: { name: "CircleAI Reviewer Agent", email: "reviewer-agent@circleai.local" },
      triggers: { taskCreated: null, chatMention: "@review", docEdit: null, directMention: "review" },
    };
  },

  get presetNames(): readonly string[] {
    return ["development", "pm", "design", "qa", "review"];
  },
} as const;

/** (3.3.0) In-memory store for members + agent profiles. Mirrors C# `InMemoryPacaMemberStore`. */
export class InMemoryPacaMemberStore {
  private readonly members = new Map<string, ProjectMember>();
  private readonly profiles = new Map<string, AgentProfile>();
  private readonly clock: () => Date;

  constructor(clock?: (() => Date) | null) {
    this.clock = clock ?? ((): Date => new Date());
  }

  addHuman(
    id: string,
    projectId: string,
    displayName: string,
    handle: string,
    role = "developer",
    avatar: string | null = null,
  ): ProjectMember {
    return this.addMember(id, projectId, MemberKind.Human, displayName, handle, role, avatar);
  }

  addAgent(
    id: string,
    projectId: string,
    displayName: string,
    handle: string,
    profile: AgentProfile,
    avatar: string | null = null,
  ): ProjectMember {
    const member = this.addMember(id, projectId, MemberKind.Agent, displayName, handle, "agent", avatar);
    this.profiles.set(id, { ...profile, memberId: id });
    return member;
  }

  private addMember(
    id: string,
    projectId: string,
    kind: MemberKind,
    displayName: string,
    handle: string,
    role: string,
    avatar: string | null,
  ): ProjectMember {
    if (isBlank(id)) throw new Error("id required");
    if (isBlank(projectId)) throw new Error("projectId required");
    if (isBlank(displayName)) throw new Error("displayName required");
    if (isBlank(handle)) throw new Error("handle required");

    if (this.members.has(id)) {
      throw new Error(`Member '${id}' already exists.`);
    }
    const member: ProjectMember = {
      id,
      projectId,
      kind,
      displayName,
      handle,
      role,
      avatarUrl: avatar,
      createdAtUtc: this.clock(),
      deletedAtUtc: null,
    };
    this.members.set(id, member);
    return member;
  }

  getMember(id: string): ProjectMember | null {
    const m = this.members.get(id);
    return m !== undefined && m.deletedAtUtc === null ? m : null;
  }

  getAgentProfile(memberId: string): AgentProfile | null {
    return this.profiles.get(memberId) ?? null;
  }

  listMembers(projectId: string, kind: MemberKind | null = null): readonly ProjectMember[] {
    return [...this.members.values()]
      .filter((m) => m.projectId === projectId && m.deletedAtUtc === null && (kind === null || m.kind === kind))
      .sort((a, b) => a.displayName.localeCompare(b.displayName));
  }

  removeMember(id: string): void {
    const existing = this.members.get(id);
    if (existing === undefined || existing.deletedAtUtc !== null) return;
    this.members.set(id, { ...existing, deletedAtUtc: this.clock() });
  }

  updateAgentProfile(memberId: string, updated: AgentProfile): AgentProfile {
    const member = this.getMember(memberId);
    if (member === null || member.kind !== MemberKind.Agent) {
      throw new Error(`Member '${memberId}' is not an agent.`);
    }
    const next: AgentProfile = { ...updated, memberId };
    this.profiles.set(memberId, next);
    return next;
  }
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
