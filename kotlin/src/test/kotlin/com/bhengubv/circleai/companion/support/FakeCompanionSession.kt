// FakeCompanionSession.kt
//
// A minimal in-memory ICompanionSession test double shared by the domain-board
// CompanionAdapter tests. It records the last message/instruction it received
// (so tests can assert enrichment + prompt wording) and echoes it back as the
// reply. streamAsync yields the message as a single chunk. All calls are
// deterministic — no external dependency.

package com.bhengubv.circleai.companion.support

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.emptyFlow
import java.time.Instant

class FakeCompanionSession(
    override val sessionId: String = "sess-1",
    override val identityId: String = "id-1",
    override val interfaceKind: InterfaceKind = InterfaceKind.Headless,
) : ICompanionSession {

    /** The most recent message/instruction handed to send/stream/agent. */
    var lastMessage: String? = null
        private set

    /** Count of feedback signals received. */
    var feedbackCount: Int = 0
        private set

    var closed: Boolean = false
        private set

    private val turns = mutableListOf<CompanionTurn>()
    override val history: List<CompanionTurn> get() = turns
    override val proactiveEvents: Flow<CompanionProactiveEvent> get() = emptyFlow()

    override suspend fun sendAsync(message: String): String {
        lastMessage = message
        turns.add(CompanionTurn("user", message, Instant.EPOCH))
        return message
    }

    override fun streamAsync(message: String): Flow<String> {
        lastMessage = message
        return flowOf(message)
    }

    override suspend fun agentAsync(instruction: String): String {
        lastMessage = instruction
        return instruction
    }

    override fun getContext(): CompanionContext = CompanionContext(
        identityId = identityId,
        displayName = "Test User",
        preferredLanguage = null,
        interfaceKind = interfaceKind,
        personaHints = "",
        affectSummary = "",
        recentMemorySnippets = emptyList(),
        activeGoals = emptyList(),
        contextBuiltAt = Instant.EPOCH,
    )

    override suspend fun refreshContextAsync() { /* no-op */ }

    override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) { feedbackCount++ }

    override fun close() { closed = true }
}
