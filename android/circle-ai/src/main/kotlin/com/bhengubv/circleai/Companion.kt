package com.bhengubv.circleai

import java.time.Instant
import kotlinx.coroutines.flow.Flow

enum class InterfaceKind { VOICE, TEXT, VISUAL, AMBIENT }

data class CompanionContext(
    val sessionId: String,
    val identityId: String,
    val interfaceKind: InterfaceKind,
    val locale: String,
    val startedAt: Instant = Instant.now()
)

data class CompanionTurn(
    val turnId: String,
    val sessionId: String,
    val userInput: String,
    val assistantResponse: String,
    val createdAt: Instant = Instant.now(),
    val turnIndex: Int
)

enum class ProactiveEventKind {
    IDLE_TOO_LONG, TOPIC_SHIFT, GOAL_COMPLETED, GOAL_SUGGESTED, MEMORY_RECALLED
}

data class CompanionProactiveEvent(
    val kind: ProactiveEventKind,
    val payload: String
)

interface ICompanionSession {
    suspend fun sendMessage(ctx: CompanionContext, message: String): String
    fun streamMessage(ctx: CompanionContext, message: String): Flow<String>
    suspend fun close()
}
