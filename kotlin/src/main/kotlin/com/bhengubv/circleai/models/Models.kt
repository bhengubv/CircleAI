// Models.kt
//
// Shared primitive types used across multiple Circle AI modules.
// ChatMessage lives here alongside DownloadProgress so that modules that
// only need the message type don't have to import the full inference module.

package com.bhengubv.circleai.models

import java.time.Instant

/**
 * A single message in a chat history.
 * [role] is one of "system", "user", or "assistant".
 */
data class ChatMessage(
    val id: String,
    val role: String,
    val content: String,
    val createdAt: Instant = Instant.now()
)

/**
 * Progress report for a model or asset download.
 */
data class DownloadProgress(
    val totalBytes: Long,
    val downloadedBytes: Long,
    val filename: String
) {
    /**
     * 0.0–1.0 fraction complete. Returns 0.0 when totalBytes is 0.
     */
    val fractionComplete: Double
        get() = if (totalBytes == 0L) 0.0 else downloadedBytes.toDouble() / totalBytes
}
