// Embeddings.kt
//
// Kotlin port of CircleAI.Embeddings:
//   • ITextEmbedder  — ITextEmbedder.cs
//   • TextEmbedder   — TextEmbedder.cs (+ its internal IEmbeddingBackend seam)
//
// The C# production backend (MnnEmbeddingBackend) P/Invokes MNN. That native
// path is lifted behind [EmbeddingBackend] and injected; the default supplied
// to tests is a deterministic in-memory backend. TextEmbedder itself ports
// faithfully: lazy, semaphore-gated init that resolves + verifies the model via
// IModelManager, embeds on a worker dispatcher, and L2-normalises.

package com.bhengubv.circleai.embeddings

import com.bhengubv.circleai.core.IModelManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlin.math.sqrt

// ---------------------------------------------------------------------------
// ITextEmbedder — CircleAI.Embeddings.ITextEmbedder
// ---------------------------------------------------------------------------

/** On-device text embedder contract. */
interface ITextEmbedder {
    /** Embed [text] into a dense vector. */
    suspend fun generateAsync(text: String): FloatArray
}

// ---------------------------------------------------------------------------
// EmbeddingBackend — internal seam (was C# internal IEmbeddingBackend)
// ---------------------------------------------------------------------------

/**
 * The embedding-backend seam. Production hosts back this with MNN; tests inject
 * a fake. Must return an L2-normalised vector. Not thread-safe — [TextEmbedder]
 * serialises access.
 */
interface EmbeddingBackend : AutoCloseable {
    /** Number of floats returned by [embed]. */
    val dimension: Int

    /** Embed [text] and return an L2-normalised vector. */
    fun embed(text: String): FloatArray
}

// ---------------------------------------------------------------------------
// TextEmbedder — CircleAI.Embeddings.TextEmbedder
// ---------------------------------------------------------------------------

/**
 * On-device text embedder. Returns L2-normalised [FloatArray] vectors suitable
 * for cosine-similarity retrieval.
 *
 * @param modelManager resolves + verifies the model path.
 * @param expectedChecksum SHA-256 the resolved model must match.
 * @param backendFactory builds the backend from the verified model path.
 */
class TextEmbedder(
    private val modelManager: IModelManager,
    expectedChecksum: ByteArray,
    private val backendFactory: (String) -> EmbeddingBackend,
) : ITextEmbedder, AutoCloseable {

    private val expectedChecksum: ByteArray = expectedChecksum.copyOf()
    private val initGate = Mutex()

    @Volatile
    private var backend: EmbeddingBackend? = null

    @Volatile
    private var disposed = false

    override suspend fun generateAsync(text: String): FloatArray {
        check(!disposed) { "TextEmbedder is disposed." }
        require(text.isNotBlank()) { "Text cannot be empty." }

        val b = ensureBackend()
        // Embed is CPU-bound; hop to the default (worker) dispatcher.
        return withContext(Dispatchers.Default) { b.embed(text) }
    }

    override fun close() {
        if (disposed) return
        disposed = true
        backend?.close()
    }

    private suspend fun ensureBackend(): EmbeddingBackend {
        backend?.let { return it }

        initGate.withLock {
            backend?.let { return it }

            val path = modelManager.getModelPathAsync("embedding")
            val verified = modelManager.verifyModelAsync(path, expectedChecksum)
            if (!verified) {
                throw java.io.IOException(
                    "Embedding model checksum verification failed. " +
                        "The file may be corrupt or tampered with.",
                )
            }

            val created = backendFactory(path)
            backend = created
            return created
        }
    }

    companion object {
        /**
         * L2-normalise [v] in place so cosine similarity reduces to a dot
         * product. Matches the C# MnnEmbeddingBackend.L2Normalize: accumulate in
         * double, leave a near-zero vector untouched.
         */
        fun l2Normalize(v: FloatArray) {
            var norm = 0.0
            for (x in v) norm += x.toDouble() * x
            norm = sqrt(norm)
            if (norm < 1e-12) return // zero vector — leave as-is
            val scale = (1.0 / norm).toFloat()
            for (i in v.indices) v[i] *= scale
        }
    }
}
