// FeedbackTrainingQueue.kt
//
// Kotlin port of CircleAI.Inference.FeedbackTrainingQueue (Phase D2). C# is the
// EXACT spec. Append-only queue of user feedback signals that the
// NightlyAdapterTrainer drains into LoRA training batches. Disk-backed so
// survival across process restarts is preserved without a database. Each line
// of the file is one JSON-encoded sample.

package com.bhengubv.circleai.inference

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.File
import java.time.Instant
import java.time.format.DateTimeFormatter

/**
 * (Phase D2) One feedback-tagged turn that will inform fine-tuning.
 *
 * @param userText What the user said.
 * @param assistantText What we replied (the "current" answer).
 * @param preferredText User's correction or the accepted form. Falls back to
 *   [assistantText] for thumbs-up.
 * @param polarity +1 (positive) / -1 (negative) / 0 (correction).
 * @param atUtc When the feedback was given (ISO-8601 round-trip string).
 */
@Serializable
data class TrainingSample(
    @SerialName("UserText") val userText: String,
    @SerialName("AssistantText") val assistantText: String,
    @SerialName("PreferredText") val preferredText: String,
    @SerialName("Polarity") val polarity: Int,
    @SerialName("AtUtc") val atUtc: String,
) {
    companion object {
        /** Convenience constructor stamping [atUtc] from an [Instant]. */
        fun of(
            userText: String,
            assistantText: String,
            preferredText: String,
            polarity: Int,
            atUtc: Instant = Instant.now(),
        ): TrainingSample = TrainingSample(
            userText, assistantText, preferredText, polarity,
            DateTimeFormatter.ISO_INSTANT.format(atUtc),
        )
    }
}

/** Contract for the feedback queue (ports C# IFeedbackTrainingQueue). */
interface IFeedbackTrainingQueue {
    /** Append one sample to the tail of the queue. */
    suspend fun enqueueAsync(sample: TrainingSample)

    /** Remove and return up to [maxSamples] from the head, in FIFO order. */
    suspend fun drainAsync(maxSamples: Int): List<TrainingSample>

    /** Number of samples currently queued. */
    val pending: Int
}

/**
 * (Phase D2) Append-only line-delimited JSON file queue. Ports
 * CircleAI.Inference.FileBackedFeedbackTrainingQueue.
 *
 * @param path Path to the queue file (created empty if absent).
 */
class FileBackedFeedbackTrainingQueue(path: String) : IFeedbackTrainingQueue {

    private val file: File
    private val writeLock = Mutex()

    init {
        require(path.isNotBlank()) { "path required" }
        file = File(path)
        file.parentFile?.mkdirs()
        if (!file.exists()) file.writeText("")
    }

    override val pending: Int
        get() {
            if (!file.exists()) return 0
            return file.useLines { it.count() }
        }

    override suspend fun enqueueAsync(sample: TrainingSample) {
        val line = JSON.encodeToString(TrainingSample.serializer(), sample)
        writeLock.withLock {
            file.appendText(line + "\n")
        }
    }

    override suspend fun drainAsync(maxSamples: Int): List<TrainingSample> {
        require(maxSamples > 0) { "maxSamples must be > 0" }
        if (!file.exists()) return emptyList()

        return writeLock.withLock {
            val allLines = file.readLines()
            val takeCount = minOf(maxSamples, allLines.size)
            val taken = ArrayList<TrainingSample>(takeCount)
            for (i in 0 until takeCount) {
                try {
                    taken.add(JSON.decodeFromString(TrainingSample.serializer(), allLines[i]))
                } catch (_: Exception) {
                    // malformed line skipped (matches C# Debug.WriteLine + skip)
                }
            }
            val remaining = allLines.subList(takeCount, allLines.size)
            // Rewrite the file with the untaken tail. Preserve trailing newline
            // semantics of the C# WriteAllLines (one line each, newline-joined).
            file.writeText(if (remaining.isEmpty()) "" else remaining.joinToString("\n") + "\n")
            taken
        }
    }

    private companion object {
        val JSON = Json { encodeDefaults = true }
    }
}
