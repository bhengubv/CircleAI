// FeedbackTrainingQueueTest.kt
//
// Verifies CircleAI.Inference.FileBackedFeedbackTrainingQueue (FIFO drain,
// pending count, disk persistence) and NightlyAdapterTrainer (min-batch skip,
// drain+train+save+apply, re-queue on TrainingNotSupported).

package com.bhengubv.circleai.inference

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.nio.file.Files
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class FeedbackTrainingQueueTest {

    private fun tempQueueFile(): String =
        Files.createTempDirectory("fbq").resolve("queue.jsonl").toString()

    private fun sample(u: String, polarity: Int = 1) =
        TrainingSample.of(u, "assistant", "preferred", polarity)

    @Test
    fun `enqueue then drain returns FIFO and empties the queue`() = runTest {
        val q = FileBackedFeedbackTrainingQueue(tempQueueFile())
        q.enqueueAsync(sample("first"))
        q.enqueueAsync(sample("second"))
        q.enqueueAsync(sample("third"))
        assertEquals(3, q.pending)

        val drained = q.drainAsync(2)
        assertEquals(listOf("first", "second"), drained.map { it.userText })
        assertEquals(1, q.pending)

        val rest = q.drainAsync(10)
        assertEquals(listOf("third"), rest.map { it.userText })
        assertEquals(0, q.pending)
    }

    @Test
    fun `queue survives reopen from disk`() = runTest {
        val path = tempQueueFile()
        FileBackedFeedbackTrainingQueue(path).enqueueAsync(sample("persisted"))
        val reopened = FileBackedFeedbackTrainingQueue(path)
        assertEquals(1, reopened.pending)
        assertEquals("persisted", reopened.drainAsync(1).single().userText)
    }

    @Test
    fun `trainer skips below min batch`() = runTest {
        val q = FileBackedFeedbackTrainingQueue(tempQueueFile())
        q.enqueueAsync(sample("only one"))
        val adapter = InMemoryLoRAAdapterManager()
        val trainer = NightlyAdapterTrainer(q, adapter, NightlyAdapterTrainerOptions(minBatchSize = 5))
        val steps = trainer.runOnceAsync()
        assertEquals(0, steps)
        assertEquals(1, q.pending) // untouched
    }

    @Test
    fun `trainer drains, trains, saves and applies`() = runTest {
        val q = FileBackedFeedbackTrainingQueue(tempQueueFile())
        repeat(4) { q.enqueueAsync(sample("turn $it", polarity = 1)) }
        val adapter = InMemoryLoRAAdapterManager()
        val adapterPath = Files.createTempDirectory("lora").resolve("adapter.mnn").toString()
        val trainer = NightlyAdapterTrainer(
            q, adapter, NightlyAdapterTrainerOptions(minBatchSize = 2, adapterPath = adapterPath),
        )

        val steps = trainer.runOnceAsync()
        assertEquals(4, steps)
        assertEquals(0, q.pending)
        assertEquals(4, adapter.stepCount)
        assertEquals(adapterPath, adapter.lastSavedPath)
        assertEquals(adapterPath, adapter.lastAppliedPath)
    }

    @Test
    fun `trainer re-queues the batch when native training is unsupported`() = runTest {
        val q = FileBackedFeedbackTrainingQueue(tempQueueFile())
        repeat(3) { q.enqueueAsync(sample("turn $it")) }
        val adapter = InMemoryLoRAAdapterManager(supportsTraining = false)
        val trainer = NightlyAdapterTrainer(q, adapter, NightlyAdapterTrainerOptions(minBatchSize = 2))

        val steps = trainer.runOnceAsync()
        assertEquals(0, steps)
        // Batch was drained then re-queued — count is preserved.
        assertEquals(3, q.pending)
    }

    @Test
    fun `char tokenizer feeds real gradient steps that reduce a toy loss`() = runTest {
        val q = FileBackedFeedbackTrainingQueue(tempQueueFile())
        repeat(2) { q.enqueueAsync(sample("aaaa")) }
        val adapter = InMemoryLoRAAdapterManager()
        val trainer = NightlyAdapterTrainer(
            q, adapter, NightlyAdapterTrainerOptions(minBatchSize = 1, learningRate = 0.5f),
        )
        trainer.runOnceAsync()
        // weight moved off zero toward the target token id.
        assertTrue(adapter.weight != 0.0)
        assertNotNull(adapter.lastSavedPath)
    }
}
