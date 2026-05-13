package com.bhengubv.circleai

import kotlinx.coroutines.flow.Flow

data class GenerationOptions(
    val model: String? = null,
    val maxTokens: Int? = null,
    val temperature: Float? = null,
    val stream: Boolean = false,
    val stopSequences: List<String> = emptyList()
)

interface IChatGenerator {
    suspend fun generate(messages: List<ChatMessage>, options: GenerationOptions = GenerationOptions()): String
    fun stream(messages: List<ChatMessage>, options: GenerationOptions = GenerationOptions()): Flow<String>
}
