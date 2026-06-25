// PacaAgents.cs
//
// (3.3.0) AI agents as first-class project members (paca port). One
// table for humans + agents — they both have an identity, handle,
// role, avatar. Agents add: LLM config, system prompts (task/doc/chat),
// capability flags, iteration limits + timeout, git identity. Five
// preset templates ship out of the box.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Workflows;

/// <summary>(3.3.0) Member kind.</summary>
public enum MemberKind { Human, Agent }

/// <summary>(3.3.0) Shared identity for humans + agents in a project.</summary>
public sealed record ProjectMember(
    string         Id,
    string         ProjectId,
    MemberKind     Kind,
    string         DisplayName,
    string         Handle,           // "@sipho" or "@billing-agent"
    string         Role,             // "owner" / "developer" / "agent" / etc.
    string?        AvatarUrl,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeletedAtUtc);

/// <summary>(3.3.0) Per-agent LLM config.</summary>
public sealed record AgentLlmConfig(
    string         Provider,
    string         Model,
    string?        ApiKey,
    Uri?           BaseAddress);

/// <summary>(3.3.0) Per-agent context-specific system prompts.</summary>
public sealed record AgentSystemPrompts(
    string?        TaskPrompt,
    string?        DocPrompt,
    string?        ChatPrompt);

/// <summary>(3.3.0) Capability flags an agent is permitted to do.</summary>
public sealed record AgentCapabilities(
    bool           CanCloneRepos,
    bool           CanCreatePRs,
    bool           CanWriteFiles,
    bool           CanCallExternalTools);

/// <summary>(3.3.0) Runtime limits an agent must respect.</summary>
public sealed record AgentLimits(int MaxIterations, TimeSpan Timeout);

/// <summary>(3.3.0) Git identity an agent uses when committing.</summary>
public sealed record AgentGitIdentity(string Name, string Email);

/// <summary>(3.3.0) Trigger keywords that wake the agent for each event class.</summary>
public sealed record AgentTriggers(
    string?        TaskCreated,
    string?        ChatMention,
    string?        DocEdit,
    string?        DirectMention);

/// <summary>(3.3.0) Full agent profile.</summary>
public sealed record AgentProfile(
    string             MemberId,
    AgentLlmConfig     Llm,
    AgentSystemPrompts Prompts,
    AgentCapabilities  Capabilities,
    AgentLimits        Limits,
    AgentGitIdentity   GitIdentity,
    AgentTriggers      Triggers);

/// <summary>(3.3.0) Five preset agent templates from paca.</summary>
public static class AgentTemplates
{
    public static AgentProfile DevelopmentAgent(string memberId, string apiKey, Uri? baseAddress = null) => new(
        MemberId: memberId,
        Llm:      new AgentLlmConfig("openai", "gpt-4o-mini", apiKey, baseAddress),
        Prompts:  new AgentSystemPrompts(
            TaskPrompt: "You are a senior developer. Implement requested changes, write tests, open PRs.",
            DocPrompt:  "You write engineering docs that are precise and example-driven.",
            ChatPrompt: "You answer engineering questions with concrete code samples."),
        Capabilities: new AgentCapabilities(CanCloneRepos: true, CanCreatePRs: true, CanWriteFiles: true, CanCallExternalTools: true),
        Limits:       new AgentLimits(MaxIterations: 25, Timeout: TimeSpan.FromMinutes(10)),
        GitIdentity:  new AgentGitIdentity("CircleAI Dev Agent", "dev-agent@circleai.local"),
        Triggers:     new AgentTriggers("dev", "@dev", null, "dev"));

    public static AgentProfile ProductManagerAgent(string memberId, string apiKey) => new(
        MemberId: memberId,
        Llm:      new AgentLlmConfig("openai", "gpt-4o-mini", apiKey, null),
        Prompts:  new AgentSystemPrompts(
            TaskPrompt: "You are a product manager. Triage tasks, break them down, assign owners.",
            DocPrompt:  "You write product specs and PRDs.",
            ChatPrompt: "You answer product/priority questions."),
        Capabilities: new AgentCapabilities(CanCloneRepos: false, CanCreatePRs: false, CanWriteFiles: true, CanCallExternalTools: true),
        Limits:       new AgentLimits(MaxIterations: 15, Timeout: TimeSpan.FromMinutes(5)),
        GitIdentity:  new AgentGitIdentity("CircleAI PM Agent", "pm-agent@circleai.local"),
        Triggers:     new AgentTriggers("pm", "@pm", "@pm", "pm"));

    public static AgentProfile DesignerAgent(string memberId, string apiKey) => new(
        MemberId: memberId,
        Llm:      new AgentLlmConfig("openai", "gpt-4o-mini", apiKey, null),
        Prompts:  new AgentSystemPrompts(
            TaskPrompt: "You are a designer. Sketch UI ideas, write copy, propose flows.",
            DocPrompt:  "You write design memos.",
            ChatPrompt: "You answer design questions and propose concepts."),
        Capabilities: new AgentCapabilities(CanCloneRepos: false, CanCreatePRs: false, CanWriteFiles: true, CanCallExternalTools: false),
        Limits:       new AgentLimits(MaxIterations: 10, Timeout: TimeSpan.FromMinutes(5)),
        GitIdentity:  new AgentGitIdentity("CircleAI Design Agent", "design-agent@circleai.local"),
        Triggers:     new AgentTriggers("design", "@design", "@design", "design"));

    public static AgentProfile QaAgent(string memberId, string apiKey) => new(
        MemberId: memberId,
        Llm:      new AgentLlmConfig("openai", "gpt-4o-mini", apiKey, null),
        Prompts:  new AgentSystemPrompts(
            TaskPrompt: "You are a QA engineer. Write test plans, generate test cases, validate against AC.",
            DocPrompt:  "You write QA reports.",
            ChatPrompt: "You answer QA questions and propose test strategies."),
        Capabilities: new AgentCapabilities(CanCloneRepos: true, CanCreatePRs: false, CanWriteFiles: true, CanCallExternalTools: true),
        Limits:       new AgentLimits(MaxIterations: 20, Timeout: TimeSpan.FromMinutes(7)),
        GitIdentity:  new AgentGitIdentity("CircleAI QA Agent", "qa-agent@circleai.local"),
        Triggers:     new AgentTriggers("qa", "@qa", null, "qa"));

    public static AgentProfile CodeReviewerAgent(string memberId, string apiKey) => new(
        MemberId: memberId,
        Llm:      new AgentLlmConfig("openai", "gpt-4o-mini", apiKey, null),
        Prompts:  new AgentSystemPrompts(
            TaskPrompt: "You are a senior code reviewer. Comment for clarity, correctness, security.",
            DocPrompt:  "You write code review checklists.",
            ChatPrompt: "You answer questions about code patterns and best practices."),
        Capabilities: new AgentCapabilities(CanCloneRepos: true, CanCreatePRs: false, CanWriteFiles: false, CanCallExternalTools: true),
        Limits:       new AgentLimits(MaxIterations: 15, Timeout: TimeSpan.FromMinutes(7)),
        GitIdentity:  new AgentGitIdentity("CircleAI Reviewer Agent", "reviewer-agent@circleai.local"),
        Triggers:     new AgentTriggers(null, "@review", null, "review"));

    public static IReadOnlyList<string> PresetNames { get; } =
        new[] { "development", "pm", "design", "qa", "review" };
}

/// <summary>(3.3.0) In-memory store for members + agent profiles.</summary>
public sealed class InMemoryPacaMemberStore
{
    private readonly ConcurrentDictionary<string, ProjectMember> _members = new();
    private readonly ConcurrentDictionary<string, AgentProfile>  _profiles = new();
    private readonly Func<DateTimeOffset> _clock;

    public InMemoryPacaMemberStore(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ProjectMember AddHuman(string id, string projectId, string displayName, string handle, string role = "developer", string? avatar = null)
        => AddMember(id, projectId, MemberKind.Human, displayName, handle, role, avatar);

    public ProjectMember AddAgent(string id, string projectId, string displayName, string handle, AgentProfile profile, string? avatar = null)
    {
        var member = AddMember(id, projectId, MemberKind.Agent, displayName, handle, role: "agent", avatar);
        _profiles[id] = profile with { MemberId = id };
        return member;
    }

    private ProjectMember AddMember(string id, string projectId, MemberKind kind, string displayName, string handle, string role, string? avatar)
    {
        if (string.IsNullOrWhiteSpace(id))          throw new ArgumentException("id required",         nameof(id));
        if (string.IsNullOrWhiteSpace(projectId))   throw new ArgumentException("projectId required",  nameof(projectId));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("displayName required", nameof(displayName));
        if (string.IsNullOrWhiteSpace(handle))      throw new ArgumentException("handle required",     nameof(handle));

        var member = new ProjectMember(id, projectId, kind, displayName, handle, role, avatar, _clock(), null);
        if (!_members.TryAdd(id, member))
        {
            throw new InvalidOperationException($"Member '{id}' already exists.");
        }
        return member;
    }

    public ProjectMember? GetMember(string id)
        => _members.TryGetValue(id, out var m) && m.DeletedAtUtc is null ? m : null;

    public AgentProfile? GetAgentProfile(string memberId)
        => _profiles.TryGetValue(memberId, out var p) ? p : null;

    public IReadOnlyList<ProjectMember> ListMembers(string projectId, MemberKind? kind = null)
    {
        return _members.Values
            .Where(m => m.ProjectId == projectId && m.DeletedAtUtc is null && (kind is null || m.Kind == kind))
            .OrderBy(m => m.DisplayName)
            .ToList();
    }

    public void RemoveMember(string id)
    {
        if (!_members.TryGetValue(id, out var existing) || existing.DeletedAtUtc is not null) return;
        _members[id] = existing with { DeletedAtUtc = _clock() };
    }

    public AgentProfile UpdateAgentProfile(string memberId, AgentProfile updated)
    {
        if (GetMember(memberId) is not { Kind: MemberKind.Agent })
        {
            throw new InvalidOperationException($"Member '{memberId}' is not an agent.");
        }
        _profiles[memberId] = updated with { MemberId = memberId };
        return _profiles[memberId];
    }
}
