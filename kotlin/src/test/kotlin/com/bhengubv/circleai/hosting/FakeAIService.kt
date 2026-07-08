// FakeAIService.kt
//
// Deterministic in-memory IAIService test double shared by the hosting tests.
// askAsync echoes a scripted reply (default: "answer:<question>"); records every
// call so tests can assert on interaction. Not a stub — a complete working
// implementation of the contract.

package com.bhengubv.circleai.hosting

import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.memory.FeedbackSignal
import com.bhengubv.circleai.models.ChatMessage
import com.bhengubv.circleai.tools.ToolInvocation
import com.bhengubv.circleai.tools.ToolResult
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow

class FakeAIService(
    private val replyFor: (String) -> String = { "answer:$it" },
    private val failAsk: Boolean = false,
) : IAIService {

    val asks = ArrayList<String>()
    var startCount = 0
        private set
    var prewarmCount = 0
        private set
    override var isReady: Boolean = false
        private set

    override suspend fun startAsync() {
        startCount++
        isReady = true
    }

    override suspend fun stopAsync() {
        isReady = false
    }

    override suspend fun askAsync(question: String): String {
        asks.add(question)
        if (failAsk) throw RuntimeException("ask failed")
        return replyFor(question)
    }

    override suspend fun chatAsync(messages: List<ChatMessage>, options: GenerationOptions?): String =
        replyFor(messages.lastOrNull()?.content ?: "")

    override fun streamAsync(messages: List<ChatMessage>, options: GenerationOptions?): Flow<String> = flow {
        emit(replyFor(messages.lastOrNull()?.content ?: ""))
    }

    override suspend fun invokeToolAsync(invocation: ToolInvocation): ToolResult =
        ToolResult.ok(invocation.toolName)

    override suspend fun agenticChatAsync(prompt: String, options: GenerationOptions?): String =
        replyFor(prompt)

    override suspend fun submitFeedbackAsync(signal: FeedbackSignal) {}

    override suspend fun prewarmAsync() {
        prewarmCount++
    }

    override suspend fun disposeAsync() {
        isReady = false
    }
}
