// NightlyAdapterTrainer.kt
//
// Kotlin port of CircleAI.Inference.NightlyAdapterTrainer (Phase D3). C# is the
// EXACT spec. Periodically drains the FeedbackTrainingQueue, runs LoRA gradient
// steps against the current model handle, saves the adapter to disk, and
// applies it atomically. Idle-and-charging gating is host-supplied via the
// shouldFireNow predicate.
//
// The native LoRAAdapterManager (TrainStep / SaveAdapter / Apply) is injected
// behind ILoRAAdapterManager. A build without native training throws
// TrainingNotSupportedException from trainStep — the trainer re-queues the
// drained batch and bails, exactly like the C# NotSupportedException path.

package com.bhengubv.circleai.inference

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.File
import kotlin.coroutines.coroutineContext

/** Thrown by [ILoRAAdapterManager.trainStep] when native training isn't built. */
class TrainingNotSupportedException(message: String) : RuntimeException(message)

/**
 * The LoRA-adapter native seam. In C# this is the concrete `LoRAAdapterManager`
 * that P/Invokes MNN's training primitives. Injected here so the trainer's
 * drain/train/save/apply loop is testable without a native runtime.
 */
interface ILoRAAdapterManager {
    /**
     * Run one gradient step against [input]/[target] token ids and return the
     * scalar loss. Throws [TrainingNotSupportedException] when the native build
     * lacks training support.
     */
    fun trainStep(input: IntArray, target: IntArray, learningRate: Float, loRARank: Int): Float

    /** Persist the trained adapter to [path]. */
    fun saveAdapter(path: String)

    /** Apply a saved adapter from [path] to the live model handle atomically. */
    fun apply(path: String)
}

/**
 * A working in-memory [ILoRAAdapterManager]. Maintains an accumulating scalar
 * "weight" updated by each step (SGD on a toy squared-error objective) so the
 * loss is real and decreasing, and records save/apply calls. No native runtime.
 */
class InMemoryLoRAAdapterManager(
    private val supportsTraining: Boolean = true,
) : ILoRAAdapterManager {

    /** Running adapter weight (toy scalar parameter). */
    var weight: Double = 0.0
        private set

    /** Count of successful [trainStep] calls. */
    var stepCount: Int = 0
        private set

    /** Last path passed to [saveAdapter]. */
    var lastSavedPath: String? = null
        private set

    /** Last path passed to [apply]. */
    var lastAppliedPath: String? = null
        private set

    override fun trainStep(input: IntArray, target: IntArray, learningRate: Float, loRARank: Int): Float {
        if (!supportsTraining) {
            throw TrainingNotSupportedException("MNN not built with training support.")
        }
        // Toy objective: drive `weight` toward the mean target token id.
        val goal = if (target.isEmpty()) 0.0 else target.average()
        val error = goal - weight
        weight += learningRate.toDouble() * error
        stepCount++
        val loss = error * error
        return loss.toFloat()
    }

    override fun saveAdapter(path: String) {
        lastSavedPath = path
        // Persist a tiny marker so the file exists on disk (parity with a real save).
        val f = File(path)
        f.parentFile?.mkdirs()
        f.writeText("circleai-lora-adapter\nweight:$weight\nsteps:$stepCount\n")
    }

    override fun apply(path: String) {
        lastAppliedPath = path
    }
}

/**
 * Options for [NightlyAdapterTrainer]. Ports NightlyAdapterTrainerOptions.
 *
 * @param minBatchSize Minimum samples to bother training. Skip otherwise.
 * @param maxSamplesPerRun Cap per nightly run so a backlog can't lock the device.
 * @param learningRate Adam-style LR for the LoRA adapter parameters.
 * @param loRARank Rank of the LoRA decomposition; lower = smaller adapter.
 * @param adapterPath Where to persist the trained adapter file.
 * @param intervalMillis How often to check whether to train. Default 6 hours.
 * @param shouldFireNow Optional gate (battery, charging, idle) — defaults to "always".
 * @param tokenizer Text → int IDs. Falls back to char-level mapping when null.
 */
data class NightlyAdapterTrainerOptions(
    val minBatchSize: Int = 16,
    val maxSamplesPerRun: Int = 256,
    val learningRate: Float = 1e-4f,
    val loRARank: Int = 8,
    val adapterPath: String = "circleai-lora.mnn",
    val intervalMillis: Long = 6L * 60 * 60 * 1000,
    val shouldFireNow: (() -> Boolean)? = null,
    val tokenizer: ((String) -> IntArray)? = null,
)

/**
 * (Phase D3) Drains [IFeedbackTrainingQueue] and trains a LoRA adapter. Runs a
 * background loop between [NightlyAdapterTrainerOptions.intervalMillis] ticks;
 * [runOnceAsync] is public so a host can trigger manually.
 */
class NightlyAdapterTrainer(
    private val queue: IFeedbackTrainingQueue,
    private val adapter: ILoRAAdapterManager,
    private val opts: NightlyAdapterTrainerOptions = NightlyAdapterTrainerOptions(),
) {
    private var scope: CoroutineScope? = null
    private var loop: Job? = null

    /** Start the background training loop. Idempotent. */
    fun start(scope: CoroutineScope) {
        if (loop != null) return
        this.scope = scope
        loop = scope.launch(Dispatchers.Default) { loopAsync() }
    }

    /** Stop the background loop and wait for it to unwind. */
    suspend fun stop() {
        loop?.cancelAndJoin()
        loop = null
        scope = null
    }

    private suspend fun loopAsync() {
        while (coroutineContext.isActive) {
            try {
                if (opts.shouldFireNow == null || opts.shouldFireNow!!.invoke()) {
                    runOnceAsync()
                }
            } catch (_: Exception) {
                // run failed — log-and-continue (matches C# LogWarning)
            }
            try {
                delay(opts.intervalMillis)
            } catch (_: Exception) {
                return
            }
        }
    }

    /**
     * (Phase D3) Drain + train in one pass. Skips when fewer than
     * [NightlyAdapterTrainerOptions.minBatchSize] samples are pending. On
     * [TrainingNotSupportedException] the drained batch is re-queued and the
     * run is abandoned. Saves + applies the adapter only when at least one step
     * succeeded. Returns the number of gradient steps taken.
     */
    suspend fun runOnceAsync(): Int {
        if (queue.pending < opts.minBatchSize) return 0

        val samples = queue.drainAsync(opts.maxSamplesPerRun)
        if (samples.isEmpty()) return 0

        val tokenizer = opts.tokenizer ?: ::charTokenizer
        var totalLoss = 0f
        var stepCount = 0

        for (sample in samples) {
            coroutineContext.ensureActive()
            try {
                val input = tokenizer(sample.userText)
                val target = tokenizer(if (sample.polarity >= 0) sample.preferredText else sample.assistantText)
                if (input.isEmpty() || target.isEmpty()) continue

                val loss = adapter.trainStep(input, target, opts.learningRate, opts.loRARank)
                totalLoss += loss
                stepCount++
            } catch (_: TrainingNotSupportedException) {
                // Native MNN not built with training — re-queue and bail out.
                for (s in samples) queue.enqueueAsync(s)
                return 0
            } catch (_: Exception) {
                // step failed for this sample — skip (matches C# LogWarning)
            }
        }

        if (stepCount > 0) {
            try {
                adapter.saveAdapter(opts.adapterPath)
                adapter.apply(opts.adapterPath)
            } catch (_: Exception) {
                // adapter save/apply failed — log-and-continue
            }
        }
        return stepCount
    }

    private companion object {
        /** Char-level tokenizer fallback — every char becomes its UTF-16 code-unit value. */
        fun charTokenizer(text: String): IntArray {
            if (text.isEmpty()) return IntArray(0)
            return IntArray(text.length) { text[it].code }
        }
    }
}
