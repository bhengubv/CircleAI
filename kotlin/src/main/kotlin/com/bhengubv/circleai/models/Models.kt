// Models.kt
//
// Shared primitive types used across multiple Circle AI modules.
// ChatMessage lives here alongside DownloadProgress so that modules that
// only need the message type don't have to import the full inference module.

package com.bhengubv.circleai.models

/**
 * A single message in a chat history.
 * [role] is one of "system", "user", or "assistant".
 */
data class ChatMessage(
    val role: String,
    val content: String
)

/**
 * Progress report for a model or asset download.
 * [totalBytes] is null when content-length is unknown.
 */
data class DownloadProgress(
    val bytesReceived: Long,
    val totalBytes: Long?
) {
    /**
     * 0.0–1.0 fraction complete, or null when total is unknown.
     */
    val fraction: Double?
        get() = if (totalBytes == null || totalBytes == 0L) null
                else bytesReceived.toDouble() / totalBytes.toDouble()
}
