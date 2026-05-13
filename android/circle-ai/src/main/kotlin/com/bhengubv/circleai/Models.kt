package com.bhengubv.circleai

data class ChatMessage(
    val role: String,       // "user" | "assistant" | "system"
    val content: String,
    val createdAt: Long     // unix ms
)

data class DownloadProgress(
    val bytesReceived: Long,
    val bytesTotal: Long,   // 0 = unknown
    val progress: Float     // 0.0–1.0
)
