// CompanionSession.kt
//
// The conscious loop: a concrete ICompanionSession that recalls from fused
// memory, persists each turn, and encodes it into the graph off the hot path.
// Kotlin port of Circle.AI.Companion (CompanionSession) — the C# reference —
// mirroring the TypeScript pilot (companion/session.ts) and Go port
// (companion_session.go) 1:1.
//
// On every turn it (1) recalls the most relevant memories + the user's own facts
// and injects them into the system prompt, (2) calls the generator, (3) persists
// the exchange to episodic memory, and (4) hands it to the background encoder so
// the knowledge graph fills for future associative recall.
//
// Implements the existing com.bhengubv.circleai.companion.ICompanionSession and
// reuses its CompanionContext / CompanionTurn / CompanionProactiveEvent /
// InterfaceKind, and the existing inference IChatGenerator + models.ChatMessage.

package com.bhengubv.circleai.companion.brain

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.memory.brain.EpisodicEntry
import com.bhengubv.circleai.memory.brain.IEpisodicStore
import com.bhengubv.circleai.memory.brain.IRecall
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.flow
import java.time.Instant
import java.util.UUID

/** Optional embedder for associative episodic recall; returns null → recency recall. */
fun interface Embedder {
    suspend fun embed(text: String): FloatArray?
}

/** Construction-time configuration for a [CompanionSession]. */
data class CompanionSessionOptions(
    val sessionId: String,
    val identityId: String,
    val interfaceKind: InterfaceKind,
    val displayName: String = "",
    val preferredLanguage: String? = null,
    /** Static persona hint block prepended to the system prompt. */
    val personaHints: String = "",
    /** Static affect hint block prepended to the system prompt. */
    val affectSummary: String = "",
    val activeGoals: List<String> = emptyList(),
    /** How many memories to recall per turn. Default 5. */
    val recallTopK: Int = 5,
    /** Optional app context stamped onto persisted episodes. */
    val appContext: String? = null,
    /** Background graph/belief encoder. When null, turns are not encoded. */
    val encoder: CompanionMemoryEncoder? = null,
    /** The user's own facts, surfaced into the system prompt. */
    val beliefs: SelfBeliefStore? = null,
    /** Optional embedder for associative episodic recall; null → recency recall. */
    val embedder: Embedder? = null,
)

/** A companion session that thinks with fused memory and remembers what it learns. */
class CompanionSession(
    private val generator: IChatGenerator,
    private val episodic: IEpisodicStore,
    private val recall: IRecall,
    private val opts: CompanionSessionOptions,
) : ICompanionSession {

    override val sessionId: String = opts.sessionId
    override val identityId: String = opts.identityId
    override val interfaceKind: InterfaceKind = opts.interfaceKind

    private val lock = Any()
    private val _history = ArrayList<CompanionTurn>()
    private var _context: CompanionContext = buildContext(emptyList())

    private val _proactiveEvents = MutableSharedFlow<CompanionProactiveEvent>()
    override val proactiveEvents: Flow<CompanionProactiveEvent> get() = _proactiveEvents

    override val history: List<CompanionTurn>
        get() = synchronized(lock) { _history.toList() }

    override suspend fun sendAsync(message: String): String {
        val prepared = prepare(message)
        val reply = generator.generateAsync(prepared.messages)
        recordTurn(message, reply, prepared.queryEmbedding, prepared.snippets)
        return reply
    }

    override fun streamAsync(message: String): Flow<String> = flow {
        val prepared = prepare(message)
        val sb = StringBuilder()
        generator.streamAsync(prepared.messages).collect { chunk ->
            sb.append(chunk)
            emit(chunk)
        }
        recordTurn(message, sb.toString(), prepared.queryEmbedding, prepared.snippets)
    }

    override suspend fun agentAsync(instruction: String): String {
        // Pilot: no tool-execution loop yet — agentic tool calling is a later slice.
        // Falls back to a plain reply so the surface is complete.
        return sendAsync(instruction)
    }

    override fun getContext(): CompanionContext = synchronized(lock) { _context }

    override suspend fun refreshContextAsync() {
        val hits = recall.recallAsync("", null, recallTopK())
        val snippets = hits.map { it.item.text }
        synchronized(lock) { _context = buildContext(snippets) }
    }

    override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) {
        // Pilot: feedback is accepted but not yet routed to a feedback store / affect
        // update. Wired in a later slice.
    }

    override fun close() {
        // No owned background resources here — the encoder is owned by the caller and
        // closed via its own closeAsync().
    }

    // ── internals ──────────────────────────────────────────────────────────────

    private class PreparedTurn(
        val messages: List<ChatMessage>,
        val queryEmbedding: FloatArray?,
        val snippets: List<String>,
    )

    /**
     * Recall runs BEFORE the current turn is persisted, so it draws on prior memory,
     * never echoes the message back.
     */
    private suspend fun prepare(message: String): PreparedTurn {
        val queryEmbedding = opts.embedder?.embed(message)
        val hits = recall.recallAsync(message, queryEmbedding, recallTopK())
        val snippets = hits.map { it.item.text }

        val messages = ArrayList<ChatMessage>()
        messages.add(msg("system", buildSystemPrompt(snippets)))
        synchronized(lock) {
            for (turn in _history) messages.add(msg(turn.role, turn.content))
        }
        messages.add(msg("user", message))

        return PreparedTurn(messages, queryEmbedding, snippets)
    }

    private suspend fun recordTurn(
        userText: String,
        reply: String,
        queryEmbedding: FloatArray?,
        snippets: List<String>,
    ) {
        val episodeId = UUID.randomUUID().toString()
        val entry = EpisodicEntry(
            id = episodeId,
            userText = userText,
            assistantText = reply,
            recordedAtUtc = Instant.now(),
            appContext = opts.appContext,
            embedding = queryEmbedding,
        )
        episodic.addAsync(entry)

        // Off the hot path: fill the graph + form attributed beliefs for next time.
        opts.encoder?.enqueue(userText, reply, episodeId)

        val now = Instant.now()
        synchronized(lock) {
            _history.add(CompanionTurn("user", userText, now))
            _history.add(CompanionTurn("assistant", reply, now))
            _context = buildContext(snippets)
        }
    }

    private fun buildSystemPrompt(snippets: List<String>): String {
        val parts = ArrayList<String>()
        if (opts.personaHints.isNotBlank()) parts.add(opts.personaHints.trim())
        if (opts.affectSummary.isNotBlank()) parts.add(opts.affectSummary.trim())

        val facts = userFacts()
        if (facts.isNotEmpty()) {
            parts.add("[What you know about the user]\n" + facts.joinToString("\n") { "- $it" })
        }
        if (snippets.isNotEmpty()) {
            parts.add("[Relevant memories]\n" + snippets.joinToString("\n") { "- $it" })
        }
        return parts.joinToString("\n\n")
    }

    private fun userFacts(): List<String> {
        val beliefs = opts.beliefs ?: return emptyList()
        return beliefs.selfFacts().map { it.obj }
    }

    private fun buildContext(snippets: List<String>): CompanionContext = CompanionContext(
        identityId = opts.identityId,
        displayName = opts.displayName,
        preferredLanguage = opts.preferredLanguage,
        interfaceKind = opts.interfaceKind,
        personaHints = opts.personaHints,
        affectSummary = opts.affectSummary,
        recentMemorySnippets = snippets,
        activeGoals = opts.activeGoals,
        contextBuiltAt = Instant.now(),
    )

    private fun recallTopK(): Int = if (opts.recallTopK <= 0) 5 else opts.recallTopK

    private companion object {
        fun msg(role: String, content: String): ChatMessage =
            ChatMessage(id = UUID.randomUUID().toString(), role = role, content = content)
    }
}
