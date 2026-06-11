// ModelsV15.kt
//
// 1.5.0 parity additions to the models layer. Kept in a separate file
// from Models.kt to avoid touching the existing API surface beyond
// adding the new types.

package com.bhengubv.circleai.models

import java.time.Instant

/** Why a generation call stopped emitting tokens. */
enum class FinishReason {
    STOP, LENGTH, CANCELLED, ERROR, UNKNOWN
}

/**
 * Structured response from IChatGenerator.generateResponse.
 * Carries text + token counts + latency + finish reason.
 *
 * `reasoningContent` is the chain-of-thought emitted by reasoning models
 * (Qwen3 / DeepSeek-R1 / o1) inside `<think>…</think>`. `null` when the
 * model emitted no reasoning or `GenerationOptions.includeReasoning` was
 * `false`. Tags themselves are stripped — only the text content.
 */
data class ChatResponse(
    val text: String,
    val tokensIn: Int,
    val tokensOut: Int,
    val latencyMs: Double,
    val finishReason: FinishReason = FinishReason.STOP,
    val reasoningContent: String? = null,
)

/** Kind of fragment a streaming generator emits. */
enum class ChatFragmentKind {
    /** Part of the user-facing answer (goes into `content`). */
    CONTENT,
    /** Part of the model's reasoning trace (goes into `reasoning_content`). */
    REASONING,
}

/**
 * A single fragment yielded by `IChatGenerator.streamFragmentsAsync`.
 * Tagged so the caller can route the model's `<think>` block into a
 * separate `reasoning_content` field (o1 / DeepSeek style).
 */
data class ChatFragment(
    val kind: ChatFragmentKind,
    val text: String,
)

/** One file inside a model bundle. */
data class BundleFile(
    val name: String,
    val sha256: String,
    val sizeBytes: Long,
)

/**
 * On-disk record of what was installed for a given model. Written by the
 * downloader after every successful bundle install. Read by
 * ModelRegistryService.checkForUpgrades to detect drift.
 */
data class InstalledManifest(
    val modelId: String,
    val version: String,
    val repo: String? = null,
    val totalBytes: Long,
    val files: List<BundleFile>,
    val installedAtUtc: Instant,
)

/** Why checkForUpgrades flagged a model. */
enum class UpgradeReason {
    VERSION_CHANGED,
    SHA_CHANGED,
    BOTH,
    UNKNOWN,
}

/** One detected upgrade for a locally-installed model. */
data class UpgradeInfo(
    val modelId: String,
    val installedVersion: String?,
    val availableVersion: String,
    val reason: UpgradeReason,
    val estimatedDownloadBytes: Long,
    val detectedAt: Instant,
)

/**
 * Vision-capable chat message. Existing ChatMessage stays as-is for
 * backward compat; consumers who need image attachments use this variant.
 * Generators that don't support vision ignore the `imageBytes` field.
 */
data class VisionChatMessage(
    val role: String,
    val content: String,
    val imageBytes: ByteArray? = null,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is VisionChatMessage) return false
        if (role != other.role) return false
        if (content != other.content) return false
        if (imageBytes != null) {
            if (other.imageBytes == null) return false
            if (!imageBytes.contentEquals(other.imageBytes)) return false
        } else if (other.imageBytes != null) return false
        return true
    }

    override fun hashCode(): Int {
        var result = role.hashCode()
        result = 31 * result + content.hashCode()
        result = 31 * result + (imageBytes?.contentHashCode() ?: 0)
        return result
    }
}
