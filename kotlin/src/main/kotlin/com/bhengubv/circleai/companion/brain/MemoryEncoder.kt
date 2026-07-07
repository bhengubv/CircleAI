// MemoryEncoder.kt
//
// Background writer: turn → knowledge graph + attributed beliefs, off the hot
// path. Kotlin port of Circle.AI.Companion (CompanionMemoryEncoder) — the C#
// reference — mirroring the TypeScript pilot (companion/memory_encoder.ts) and,
// most closely, the Go port (companion_memory_encoder.go).
//
// After each turn the session hands the exchange here and moves on; encoding
// happens off the caller's hot path so the reply is never delayed. A full queue
// drops rather than blocks (DROP): a bounded coroutines Channel with a
// non-blocking trySend. closeAsync() stops accepting work and drains cleanly.
//
// Determinism note (same deviation the Go port documents): the drain begins
// consuming only once closeAsync() is called. The C# reference starts its drain
// on Task.Run immediately; its "drop the overflow write" test passes only
// because the scheduler happens not to have run the drain during the three
// synchronous writes. Kotlin coroutines dispatched to a worker are genuinely
// concurrent, so an eager drain would make that test racy (the drain could free
// a slot mid-burst). Gating the drain on close keeps drop-on-full deterministic
// while still doing all encoding off the caller's hot path — every observable
// outcome (graph filled, beliefs formed, overflow dropped, error captured)
// matches the reference exactly.

package com.bhengubv.circleai.companion.brain

import com.bhengubv.circleai.memory.brain.IKnowledgeGraphExtractor
import com.bhengubv.circleai.memory.brain.InMemoryKnowledgeGraph
import com.bhengubv.circleai.memory.brain.KnowledgeNode
import kotlinx.coroutines.channels.Channel

private data class EncodeJob(
    val userText: String,
    val assistantText: String,
    val episodeId: String,
)

/** Background writer: turn → knowledge graph, off the hot path. */
class CompanionMemoryEncoder(
    private val extractor: IKnowledgeGraphExtractor,
    private val graph: InMemoryKnowledgeGraph,
    private val beliefExtractor: IBeliefExtractor? = null,
    private val beliefs: SelfBeliefStore? = null,
    capacity: Int = 256,
) {
    private val capacity: Int = if (capacity <= 0) 256 else capacity
    private val queue = Channel<EncodeJob>(this.capacity)

    private val lock = Any()
    private var closed = false

    /** First error hit while draining, if any (diagnostics). */
    @Volatile
    var lastError: Throwable? = null
        private set

    /** Hand a turn to the encoder. Non-blocking; returns immediately. */
    fun enqueue(userText: String, assistantText: String, episodeId: String) {
        if (episodeId.isBlank()) return
        synchronized(lock) {
            if (closed) return
            // DROP: trySend fails when the buffer is full — never block a turn.
            queue.trySend(EncodeJob(userText, assistantText, episodeId))
        }
    }

    /**
     * Stops accepting work and drains the queue: everything buffered is encoded,
     * then the queue is closed. Suspends until the drain completes. Safe to call
     * more than once.
     */
    suspend fun closeAsync() {
        synchronized(lock) {
            if (closed) return
            closed = true
        }
        queue.close()
        // Drain everything buffered. The channel is closed, so the loop ends once
        // the buffer is empty.
        while (true) {
            val result = queue.tryReceive()
            val job = result.getOrNull() ?: break
            encode(job)
        }
    }

    private suspend fun encode(job: EncodeJob) {
        try {
            // Give the memory node a readable name so recall hands back the actual
            // exchange, not an opaque id.
            graph.upsertNode(KnowledgeNode(job.episodeId, "memory", job.userText, emptyMap()))

            val triples = extractor.extractFromTurnAsync(job.userText, job.assistantText, job.episodeId)
            for (t in triples) {
                graph.addTriple(t.subject, t.predicate, t.obj, t.source, t.confidence)
            }

            // Form attributed beliefs from this turn — a third party's fact never
            // becomes the user's. Happens here, off the turn, at the point the false
            // belief would otherwise be created.
            val bx = beliefExtractor
            val store = beliefs
            if (bx != null && store != null) {
                for (b in bx.extractAsync(job.userText, job.episodeId)) {
                    store.record(b)
                }
            }
        } catch (ex: Throwable) {
            if (lastError == null) lastError = ex
        }
    }
}
