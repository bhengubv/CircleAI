// PacaAgents.kt
//
// Kotlin port of CircleAI.Workflows/PacaAgents.cs.
//
// (3.3.0) AI agents as first-class project members (paca port). One store for
// humans + agents — they both have an identity, handle, role, avatar. Agents
// add: LLM config, system prompts (task/doc/chat), capability flags, iteration
// limits + timeout, git identity. Five preset templates ship out of the box.

package com.bhengubv.circleai.workflows

import java.net.URI
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/** (3.3.0) Member kind. */
enum class MemberKind { Human, Agent }

/** (3.3.0) Shared identity for humans + agents in a project. */
data class ProjectMember(
    val id: String,
    val projectId: String,
    val kind: MemberKind,
    val displayName: String,
    val handle: String,           // "@sipho" or "@billing-agent"
    val role: String,             // "owner" / "developer" / "agent" / etc.
    val avatarUrl: String?,
    val createdAtUtc: Instant,
    val deletedAtUtc: Instant?,
)

/** (3.3.0) Per-agent LLM config. */
data class AgentLlmConfig(
    val provider: String,
    val model: String,
    val apiKey: String?,
    val baseAddress: URI?,
)

/** (3.3.0) Per-agent context-specific system prompts. */
data class AgentSystemPrompts(
    val taskPrompt: String?,
    val docPrompt: String?,
    val chatPrompt: String?,
)

/** (3.3.0) Capability flags an agent is permitted to do. */
data class AgentCapabilities(
    val canCloneRepos: Boolean,
    val canCreatePRs: Boolean,
    val canWriteFiles: Boolean,
    val canCallExternalTools: Boolean,
)

/** (3.3.0) Runtime limits an agent must respect. */
data class AgentLimits(val maxIterations: Int, val timeout: Duration)

/** (3.3.0) Git identity an agent uses when committing. */
data class AgentGitIdentity(val name: String, val email: String)

/** (3.3.0) Trigger keywords that wake the agent for each event class. */
data class AgentTriggers(
    val taskCreated: String?,
    val chatMention: String?,
    val docEdit: String?,
    val directMention: String?,
)

/** (3.3.0) Full agent profile. */
data class AgentProfile(
    val memberId: String,
    val llm: AgentLlmConfig,
    val prompts: AgentSystemPrompts,
    val capabilities: AgentCapabilities,
    val limits: AgentLimits,
    val gitIdentity: AgentGitIdentity,
    val triggers: AgentTriggers,
)

/** (3.3.0) Five preset agent templates from paca. */
object AgentTemplates {

    fun developmentAgent(memberId: String, apiKey: String, baseAddress: URI? = null): AgentProfile = AgentProfile(
        memberId = memberId,
        llm = AgentLlmConfig("openai", "gpt-4o-mini", apiKey, baseAddress),
        prompts = AgentSystemPrompts(
            taskPrompt = "You are a senior developer. Implement requested changes, write tests, open PRs.",
            docPrompt = "You write engineering docs that are precise and example-driven.",
            chatPrompt = "You answer engineering questions with concrete code samples.",
        ),
        capabilities = AgentCapabilities(canCloneRepos = true, canCreatePRs = true, canWriteFiles = true, canCallExternalTools = true),
        limits = AgentLimits(maxIterations = 25, timeout = Duration.ofMinutes(10)),
        gitIdentity = AgentGitIdentity("CircleAI Dev Agent", "dev-agent@circleai.local"),
        triggers = AgentTriggers("dev", "@dev", null, "dev"),
    )

    fun productManagerAgent(memberId: String, apiKey: String): AgentProfile = AgentProfile(
        memberId = memberId,
        llm = AgentLlmConfig("openai", "gpt-4o-mini", apiKey, null),
        prompts = AgentSystemPrompts(
            taskPrompt = "You are a product manager. Triage tasks, break them down, assign owners.",
            docPrompt = "You write product specs and PRDs.",
            chatPrompt = "You answer product/priority questions.",
        ),
        capabilities = AgentCapabilities(canCloneRepos = false, canCreatePRs = false, canWriteFiles = true, canCallExternalTools = true),
        limits = AgentLimits(maxIterations = 15, timeout = Duration.ofMinutes(5)),
        gitIdentity = AgentGitIdentity("CircleAI PM Agent", "pm-agent@circleai.local"),
        triggers = AgentTriggers("pm", "@pm", "@pm", "pm"),
    )

    fun designerAgent(memberId: String, apiKey: String): AgentProfile = AgentProfile(
        memberId = memberId,
        llm = AgentLlmConfig("openai", "gpt-4o-mini", apiKey, null),
        prompts = AgentSystemPrompts(
            taskPrompt = "You are a designer. Sketch UI ideas, write copy, propose flows.",
            docPrompt = "You write design memos.",
            chatPrompt = "You answer design questions and propose concepts.",
        ),
        capabilities = AgentCapabilities(canCloneRepos = false, canCreatePRs = false, canWriteFiles = true, canCallExternalTools = false),
        limits = AgentLimits(maxIterations = 10, timeout = Duration.ofMinutes(5)),
        gitIdentity = AgentGitIdentity("CircleAI Design Agent", "design-agent@circleai.local"),
        triggers = AgentTriggers("design", "@design", "@design", "design"),
    )

    fun qaAgent(memberId: String, apiKey: String): AgentProfile = AgentProfile(
        memberId = memberId,
        llm = AgentLlmConfig("openai", "gpt-4o-mini", apiKey, null),
        prompts = AgentSystemPrompts(
            taskPrompt = "You are a QA engineer. Write test plans, generate test cases, validate against AC.",
            docPrompt = "You write QA reports.",
            chatPrompt = "You answer QA questions and propose test strategies.",
        ),
        capabilities = AgentCapabilities(canCloneRepos = true, canCreatePRs = false, canWriteFiles = true, canCallExternalTools = true),
        limits = AgentLimits(maxIterations = 20, timeout = Duration.ofMinutes(7)),
        gitIdentity = AgentGitIdentity("CircleAI QA Agent", "qa-agent@circleai.local"),
        triggers = AgentTriggers("qa", "@qa", null, "qa"),
    )

    fun codeReviewerAgent(memberId: String, apiKey: String): AgentProfile = AgentProfile(
        memberId = memberId,
        llm = AgentLlmConfig("openai", "gpt-4o-mini", apiKey, null),
        prompts = AgentSystemPrompts(
            taskPrompt = "You are a senior code reviewer. Comment for clarity, correctness, security.",
            docPrompt = "You write code review checklists.",
            chatPrompt = "You answer questions about code patterns and best practices.",
        ),
        capabilities = AgentCapabilities(canCloneRepos = true, canCreatePRs = false, canWriteFiles = false, canCallExternalTools = true),
        limits = AgentLimits(maxIterations = 15, timeout = Duration.ofMinutes(7)),
        gitIdentity = AgentGitIdentity("CircleAI Reviewer Agent", "reviewer-agent@circleai.local"),
        triggers = AgentTriggers(null, "@review", null, "review"),
    )

    val presetNames: List<String> = listOf("development", "pm", "design", "qa", "review")
}

/** (3.3.0) In-memory store for members + agent profiles. */
class InMemoryPacaMemberStore(private val clock: () -> Instant = { Instant.now() }) {

    private val members = ConcurrentHashMap<String, ProjectMember>()
    private val profiles = ConcurrentHashMap<String, AgentProfile>()

    fun addHuman(
        id: String,
        projectId: String,
        displayName: String,
        handle: String,
        role: String = "developer",
        avatar: String? = null,
    ): ProjectMember = addMember(id, projectId, MemberKind.Human, displayName, handle, role, avatar)

    fun addAgent(
        id: String,
        projectId: String,
        displayName: String,
        handle: String,
        profile: AgentProfile,
        avatar: String? = null,
    ): ProjectMember {
        val member = addMember(id, projectId, MemberKind.Agent, displayName, handle, role = "agent", avatar = avatar)
        profiles[id] = profile.copy(memberId = id)
        return member
    }

    private fun addMember(
        id: String,
        projectId: String,
        kind: MemberKind,
        displayName: String,
        handle: String,
        role: String,
        avatar: String?,
    ): ProjectMember {
        require(id.isNotBlank()) { "id required" }
        require(projectId.isNotBlank()) { "projectId required" }
        require(displayName.isNotBlank()) { "displayName required" }
        require(handle.isNotBlank()) { "handle required" }

        val member = ProjectMember(id, projectId, kind, displayName, handle, role, avatar, clock(), null)
        if (members.putIfAbsent(id, member) != null) {
            throw IllegalStateException("Member '$id' already exists.")
        }
        return member
    }

    fun getMember(id: String): ProjectMember? =
        members[id]?.takeIf { it.deletedAtUtc == null }

    fun getAgentProfile(memberId: String): AgentProfile? = profiles[memberId]

    fun listMembers(projectId: String, kind: MemberKind? = null): List<ProjectMember> {
        return members.values
            .filter { it.projectId == projectId && it.deletedAtUtc == null && (kind == null || it.kind == kind) }
            .sortedBy { it.displayName }
    }

    fun removeMember(id: String) {
        val existing = members[id] ?: return
        if (existing.deletedAtUtc != null) return
        members[id] = existing.copy(deletedAtUtc = clock())
    }

    fun updateAgentProfile(memberId: String, updated: AgentProfile): AgentProfile {
        val member = getMember(memberId)
        if (member == null || member.kind != MemberKind.Agent) {
            throw IllegalStateException("Member '$memberId' is not an agent.")
        }
        val stored = updated.copy(memberId = memberId)
        profiles[memberId] = stored
        return stored
    }
}
