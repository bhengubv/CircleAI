// EmbeddingsLocal.kt
//
// Kotlin port of CircleAI.Embeddings.Local:
//   • IEmbeddingEncoder + EmbeddingDocument + EmbeddingSearchHit  — ICircleEmbeddingStore.cs
//   • ICircleEmbeddingStore                                       — ICircleEmbeddingStore.cs
//   • IEmbeddingIndex + EmbeddingIndexHit                         — IEmbeddingIndex.cs
//   • InMemoryEmbeddingStore  — InMemoryEmbeddingStore.cs (brute-force, TurboQuant-compressed)
//   • InMemoryEmbeddingIndex  — deterministic in-memory brute-force IEmbeddingIndex
//     (stands in for the native turbovec-backed TurboVecEmbeddingIndex; same
//      contract, no native dependency)
//
// The store's on-disk format is a genuine cross-device wire format, so the
// binary layout is byte-identical to the C# BinaryWriter/BinaryReader output:
//   int32-LE   magic  = 0x4C455143 ("CELQ")
//   uint16-LE  version = 1
//   uint16-LE  bitsPerDim
//   int32-LE   dimension
//   int32-LE   count
//   per entry:
//     string   id            (7-bit-encoded length prefix + UTF-8)
//     string   text
//     int32-LE metadataCount
//       (string key, string value) * metadataCount
//     float32-LE norm
//     int32-LE   packedLen
//     byte[]     packedIndices
//
// TurboQuant compression reuses the already-ported codec in
// com.bhengubv.circleai.memory.brain (byte-identical across every SDK language).

package com.bhengubv.circleai.embeddings.local

import com.bhengubv.circleai.memory.brain.TurboQuantCodec
import com.bhengubv.circleai.memory.brain.TurboQuantPayload
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.InputStream
import java.io.OutputStream
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.sqrt

// ===========================================================================
// Documents / hits — CircleAI.Embeddings.Local records
// ===========================================================================

/**
 * One document in the store. [id] is caller-chosen and uniquely identifies the
 * document for delete / update.
 */
data class EmbeddingDocument(
    val id: String,
    val text: String,
    val metadata: Map<String, String>? = null,
)

/**
 * One hit from [ICircleEmbeddingStore.searchAsync]. Higher [score] = closer.
 * Cosine similarity: 1.0 = identical, -1.0 = opposite, 0.0 = orthogonal.
 */
data class EmbeddingSearchHit(
    val document: EmbeddingDocument,
    val score: Float,
)

/**
 * One hit returned by [IEmbeddingIndex.searchAsync]. [internalId] is the
 * insertion-order id assigned by [IEmbeddingIndex.addAsync]. Higher [score] =
 * closer.
 */
data class EmbeddingIndexHit(
    val internalId: Long,
    val score: Float,
)

// ===========================================================================
// IEmbeddingEncoder — CircleAI.Embeddings.Local.IEmbeddingEncoder
// ===========================================================================

/**
 * Translates text into a dense vector. Bring your own — sentence-transformers
 * via ONNX, a small MNN encoder, or a cloud API.
 */
interface IEmbeddingEncoder {
    /**
     * Vector dimension this encoder produces. All vectors fed to the store from
     * the same encoder must agree.
     */
    val dimension: Int

    /** Encode one text into a dense vector. */
    suspend fun encodeAsync(text: String): FloatArray
}

// ===========================================================================
// ICircleEmbeddingStore — CircleAI.Embeddings.Local.ICircleEmbeddingStore
// ===========================================================================

/**
 * On-device embedding store with a built-in RAG primitive. Add documents once,
 * search by text or vector. Vectors are TurboQuant-compressed so the store fits
 * many more documents in the same footprint as raw FP32.
 */
interface ICircleEmbeddingStore : AutoCloseable {
    /** Vector dimension this store was created with. */
    val dimension: Int

    /** How many documents are currently in the store. */
    val count: Int

    /** Add (or replace) one document; the encoder produces the vector. */
    suspend fun addAsync(document: EmbeddingDocument)

    /** Add a document with a caller-supplied vector (length must equal [dimension]). */
    suspend fun addAsync(document: EmbeddingDocument, vector: FloatArray)

    /** Remove a document by id. Returns true if a document was removed. */
    suspend fun removeAsync(id: String): Boolean

    /** Search by text — returns the [topK] closest documents by cosine similarity. */
    suspend fun searchAsync(queryText: String, topK: Int = 5): List<EmbeddingSearchHit>

    /** Search by a pre-computed query vector (length must equal [dimension]). */
    suspend fun searchAsync(queryVector: FloatArray, topK: Int = 5): List<EmbeddingSearchHit>

    /** Persist the entire store to [path]. Atomic via write-tmp-then-rename. */
    suspend fun saveAsync(path: String)

    /** Load a previously-saved store from [path], replacing all in-memory state. */
    suspend fun loadAsync(path: String)
}

// ===========================================================================
// IEmbeddingIndex — CircleAI.Embeddings.Local.IEmbeddingIndex
// ===========================================================================

/**
 * Vector index contract. The store layers documents + metadata + persistence on
 * top; the index is the search primitive.
 */
interface IEmbeddingIndex : AutoCloseable {
    /** Vector dimensionality. Locked at construction. */
    val dimension: Int

    /** How many vectors are currently in the index. */
    val count: Long

    /**
     * Append one vector. Returns the internal id the index assigned — callers
     * map it back to a document id.
     */
    suspend fun addAsync(vector: FloatArray): Long

    /** Search for the top-[topK] nearest neighbours. */
    suspend fun searchAsync(queryVector: FloatArray, topK: Int): Array<EmbeddingIndexHit>

    /** Persist the index to [path]. */
    suspend fun saveAsync(path: String)

    /** Reload from [path], replacing the in-memory state. */
    suspend fun loadAsync(path: String)
}

// ===========================================================================
// C#-compatible BinaryWriter / BinaryReader helpers
// ===========================================================================

/**
 * Little-endian, C#-BinaryWriter-compatible primitives. Strings use the .NET
 * 7-bit-encoded (LEB128) length prefix followed by UTF-8 bytes, so the produced
 * bytes match `System.IO.BinaryWriter` exactly.
 */
internal object CsBinary {
    fun writeInt32(out: OutputStream, v: Int) {
        out.write(v and 0xFF)
        out.write((v ushr 8) and 0xFF)
        out.write((v ushr 16) and 0xFF)
        out.write((v ushr 24) and 0xFF)
    }

    fun writeUInt16(out: OutputStream, v: Int) {
        out.write(v and 0xFF)
        out.write((v ushr 8) and 0xFF)
    }

    fun writeFloat(out: OutputStream, v: Float) {
        writeInt32(out, java.lang.Float.floatToRawIntBits(v))
    }

    fun writeString(out: OutputStream, s: String) {
        val bytes = s.toByteArray(Charsets.UTF_8)
        write7BitEncodedInt(out, bytes.size)
        out.write(bytes)
    }

    fun writeBytes(out: OutputStream, b: ByteArray) {
        out.write(b)
    }

    private fun write7BitEncodedInt(out: OutputStream, value: Int) {
        var v = value
        while (v >= 0x80) {
            out.write((v and 0x7F) or 0x80)
            v = v ushr 7
        }
        out.write(v)
    }

    fun readInt32(inp: InputStream): Int {
        val b0 = readByteStrict(inp)
        val b1 = readByteStrict(inp)
        val b2 = readByteStrict(inp)
        val b3 = readByteStrict(inp)
        return b0 or (b1 shl 8) or (b2 shl 16) or (b3 shl 24)
    }

    fun readUInt16(inp: InputStream): Int {
        val b0 = readByteStrict(inp)
        val b1 = readByteStrict(inp)
        return b0 or (b1 shl 8)
    }

    fun readFloat(inp: InputStream): Float = java.lang.Float.intBitsToFloat(readInt32(inp))

    fun readString(inp: InputStream): String {
        val len = read7BitEncodedInt(inp)
        val bytes = ByteArray(len)
        var read = 0
        while (read < len) {
            val n = inp.read(bytes, read, len - read)
            if (n < 0) throw java.io.EOFException("Unexpected EOF reading string of length $len.")
            read += n
        }
        return String(bytes, Charsets.UTF_8)
    }

    fun readBytes(inp: InputStream, count: Int): ByteArray {
        val bytes = ByteArray(count)
        var read = 0
        while (read < count) {
            val n = inp.read(bytes, read, count - read)
            if (n < 0) throw java.io.EOFException("Unexpected EOF reading $count bytes.")
            read += n
        }
        return bytes
    }

    private fun read7BitEncodedInt(inp: InputStream): Int {
        var count = 0
        var shift = 0
        while (true) {
            if (shift == 5 * 7) throw java.io.IOException("Malformed 7-bit-encoded int.")
            val b = readByteStrict(inp)
            count = count or ((b and 0x7F) shl shift)
            shift += 7
            if (b and 0x80 == 0) break
        }
        return count
    }

    private fun readByteStrict(inp: InputStream): Int {
        val b = inp.read()
        if (b < 0) throw java.io.EOFException("Unexpected EOF.")
        return b and 0xFF
    }
}

// ===========================================================================
// InMemoryEmbeddingStore — CircleAI.Embeddings.Local.InMemoryEmbeddingStore
// ===========================================================================

/**
 * Default [ICircleEmbeddingStore]: brute-force search over TurboQuant-compressed
 * vectors held in memory. Byte-identical persistence to the C# implementation.
 */
class InMemoryEmbeddingStore(
    private val encoder: IEmbeddingEncoder,
    private val bitsPerDim: Int = DEFAULT_BITS_PER_DIM,
) : ICircleEmbeddingStore {

    private val gate = Mutex()
    private val entries = ConcurrentHashMap<String, Entry>()

    @Volatile
    private var disposed = false

    override val dimension: Int get() = encoder.dimension
    override val count: Int get() = entries.size

    init {
        if (bitsPerDim < 1 || bitsPerDim > 8) {
            throw IndexOutOfBoundsException("Valid range: 1–8.")
        }
    }

    override suspend fun addAsync(document: EmbeddingDocument) {
        val vector = encoder.encodeAsync(document.text)
        addAsync(document, vector)
    }

    override suspend fun addAsync(document: EmbeddingDocument, vector: FloatArray) {
        throwIfDisposed()
        if (vector.size != dimension) {
            throw IllegalArgumentException("Vector length ${vector.size} != store dimension $dimension.")
        }
        val payload = TurboQuantCodec.encode(vector, bitsPerDim)
        entries[document.id] = Entry(document, payload)
    }

    override suspend fun removeAsync(id: String): Boolean {
        require(id.isNotBlank()) { "id must be non-blank." }
        throwIfDisposed()
        return entries.remove(id) != null
    }

    override suspend fun searchAsync(queryText: String, topK: Int): List<EmbeddingSearchHit> {
        require(queryText.isNotEmpty()) { "queryText must be non-empty." }
        val vector = encoder.encodeAsync(queryText)
        return searchAsync(vector, topK)
    }

    override suspend fun searchAsync(queryVector: FloatArray, topK: Int): List<EmbeddingSearchHit> {
        throwIfDisposed()
        if (queryVector.size != dimension) {
            throw IllegalArgumentException("Vector length ${queryVector.size} != store dimension $dimension.")
        }
        if (topK <= 0) throw IndexOutOfBoundsException("topK")

        val qNorm = normSafe(queryVector)
        val q = queryVector.copyOf()
        if (qNorm > 0) for (i in q.indices) q[i] /= qNorm

        // Brute-force cosine. Running top-K via a comparator-ordered sorted set,
        // mirroring the C# SortedSet<(float Score, string Id)> with tie-break on
        // ordinal id.
        val heap = java.util.TreeSet(ScoreComparator)
        for ((id, entry) in entries) {
            val decoded = TurboQuantCodec.decode(entry.payload, dimension, bitsPerDim)
            val entryNorm = normSafe(decoded)
            if (entryNorm <= 0) continue
            var dot = 0f
            for (i in 0 until dimension) dot += q[i] * (decoded[i] / entryNorm)

            if (heap.size < topK) {
                heap.add(ScoredId(dot, id))
            } else if (dot > heap.first().score) {
                heap.pollFirst()
                heap.add(ScoredId(dot, id))
            }
        }

        return heap.sortedWith(compareByDescending { it.score })
            .map { EmbeddingSearchHit(entries.getValue(it.id).document, it.score) }
    }

    override suspend fun saveAsync(path: String) {
        require(path.isNotBlank()) { "path must be non-blank." }
        throwIfDisposed()

        gate.withLock {
            File(path).parentFile?.mkdirs()
            val tmp = "$path.tmp"
            val buffer = ByteArrayOutputStream()
            CsBinary.writeInt32(buffer, FILE_MAGIC)
            CsBinary.writeUInt16(buffer, FILE_VERSION)
            CsBinary.writeUInt16(buffer, bitsPerDim)
            CsBinary.writeInt32(buffer, dimension)
            CsBinary.writeInt32(buffer, entries.size)
            for ((id, entry) in entries) {
                CsBinary.writeString(buffer, id)
                CsBinary.writeString(buffer, entry.document.text)
                val meta = entry.document.metadata
                CsBinary.writeInt32(buffer, meta?.size ?: 0)
                if (meta != null) {
                    for ((k, v) in meta) {
                        CsBinary.writeString(buffer, k)
                        CsBinary.writeString(buffer, v)
                    }
                }
                CsBinary.writeFloat(buffer, entry.payload.norm)
                CsBinary.writeInt32(buffer, entry.payload.packedIndices.size)
                CsBinary.writeBytes(buffer, entry.payload.packedIndices)
            }
            File(tmp).writeBytes(buffer.toByteArray())
            val dest = File(path)
            if (dest.exists()) dest.delete()
            if (!File(tmp).renameTo(dest)) {
                // Fallback: copy + delete if rename across boundaries fails.
                dest.writeBytes(File(tmp).readBytes())
                File(tmp).delete()
            }
        }
    }

    override suspend fun loadAsync(path: String) {
        require(path.isNotBlank()) { "path must be non-blank." }
        throwIfDisposed()
        if (!File(path).exists()) {
            throw java.io.FileNotFoundException("Embedding store file not found: $path")
        }

        gate.withLock {
            ByteArrayInputStream(File(path).readBytes()).use { inp ->
                val magic = CsBinary.readInt32(inp)
                if (magic != FILE_MAGIC) throw java.io.IOException("Not a CircleAI embedding store file.")
                val version = CsBinary.readUInt16(inp)
                if (version != FILE_VERSION) throw java.io.IOException("Unsupported file version $version.")
                val fileBits = CsBinary.readUInt16(inp)
                if (fileBits != bitsPerDim) {
                    throw java.io.IOException("Bits-per-dim mismatch: store=$bitsPerDim, file=$fileBits.")
                }
                val fileDim = CsBinary.readInt32(inp)
                if (fileDim != dimension) {
                    throw java.io.IOException("Dimension mismatch: store=$dimension, file=$fileDim.")
                }

                val count = CsBinary.readInt32(inp)
                entries.clear()
                for (i in 0 until count) {
                    val id = CsBinary.readString(inp)
                    val text = CsBinary.readString(inp)
                    val metaCount = CsBinary.readInt32(inp)
                    var metadata: MutableMap<String, String>? = null
                    if (metaCount > 0) {
                        metadata = LinkedHashMap(metaCount)
                        for (m in 0 until metaCount) {
                            metadata[CsBinary.readString(inp)] = CsBinary.readString(inp)
                        }
                    }
                    val norm = CsBinary.readFloat(inp)
                    val packedLen = CsBinary.readInt32(inp)
                    val packed = CsBinary.readBytes(inp, packedLen)
                    entries[id] = Entry(
                        EmbeddingDocument(id, text, metadata),
                        TurboQuantPayload(norm, packed),
                    )
                }
            }
        }
    }

    override fun close() {
        if (disposed) return
        disposed = true
        entries.clear()
    }

    private fun throwIfDisposed() {
        if (disposed) throw IllegalStateException("InMemoryEmbeddingStore is disposed.")
    }

    private class Entry(val document: EmbeddingDocument, val payload: TurboQuantPayload)

    private data class ScoredId(val score: Float, val id: String)

    private object ScoreComparator : Comparator<ScoredId> {
        override fun compare(a: ScoredId, b: ScoredId): Int {
            val c = a.score.compareTo(b.score)
            return if (c != 0) c else a.id.compareTo(b.id)
        }
    }

    companion object {
        const val FILE_MAGIC: Int = 0x4C455143 // "CELQ" little-endian
        const val FILE_VERSION: Int = 1
        const val DEFAULT_BITS_PER_DIM: Int = 4

        internal fun normSafe(v: FloatArray): Float {
            var sum = 0.0
            for (x in v) sum += x.toDouble() * x
            return sqrt(sum).toFloat()
        }
    }
}

// ===========================================================================
// InMemoryEmbeddingIndex — deterministic IEmbeddingIndex (no native dependency)
// ===========================================================================

/**
 * Deterministic in-memory [IEmbeddingIndex]. Brute-force cosine search over
 * float vectors held verbatim; stands in for the native turbovec-backed
 * TurboVecEmbeddingIndex. Same public contract, no native dependency.
 *
 * Persistence format (self-contained, little-endian):
 *   int32-LE magic = 0x49455143 ("CEIQ")
 *   uint16-LE version = 1
 *   int32-LE dimension
 *   int32-LE count
 *   per vector: dimension * float32-LE
 */
class InMemoryEmbeddingIndex(dimension: Int) : IEmbeddingIndex {

    private val writeGate = Mutex()
    private val vectors = ArrayList<FloatArray>()

    @Volatile
    private var disposed = false

    override val dimension: Int = dimension

    override val count: Long
        get() {
            throwIfDisposed()
            synchronized(vectors) { return vectors.size.toLong() }
        }

    init {
        if (dimension <= 0) throw IndexOutOfBoundsException("Dimension must be positive.")
    }

    override suspend fun addAsync(vector: FloatArray): Long {
        throwIfDisposed()
        if (vector.size != dimension) {
            throw IllegalArgumentException("Vector length ${vector.size} != index dimension $dimension.")
        }
        writeGate.withLock {
            synchronized(vectors) {
                vectors.add(vector.copyOf())
                return (vectors.size - 1).toLong()
            }
        }
    }

    override suspend fun searchAsync(queryVector: FloatArray, topK: Int): Array<EmbeddingIndexHit> {
        throwIfDisposed()
        if (queryVector.size != dimension) {
            throw IllegalArgumentException("Query length ${queryVector.size} != index dimension $dimension.")
        }
        if (topK <= 0) throw IndexOutOfBoundsException("topK")

        val snapshot: List<Pair<Long, FloatArray>>
        synchronized(vectors) {
            if (vectors.isEmpty()) return emptyArray()
            snapshot = vectors.mapIndexed { i, v -> i.toLong() to v }
        }

        val qNorm = InMemoryEmbeddingStore.normSafe(queryVector)
        val q = queryVector.copyOf()
        if (qNorm > 0) for (i in q.indices) q[i] /= qNorm

        val scored = ArrayList<EmbeddingIndexHit>(snapshot.size)
        for ((id, v) in snapshot) {
            val vNorm = InMemoryEmbeddingStore.normSafe(v)
            if (vNorm <= 0) continue
            var dot = 0f
            for (i in 0 until dimension) dot += q[i] * (v[i] / vNorm)
            scored.add(EmbeddingIndexHit(id, dot))
        }
        // Sort by score desc, tie-break by ascending id for determinism.
        scored.sortWith(compareByDescending<EmbeddingIndexHit> { it.score }.thenBy { it.internalId })
        return scored.take(topK).toTypedArray()
    }

    override suspend fun saveAsync(path: String) {
        require(path.isNotBlank()) { "path must be non-blank." }
        throwIfDisposed()
        writeGate.withLock {
            File(path).parentFile?.mkdirs()
            val buffer = ByteArrayOutputStream()
            CsBinary.writeInt32(buffer, INDEX_MAGIC)
            CsBinary.writeUInt16(buffer, INDEX_VERSION)
            CsBinary.writeInt32(buffer, dimension)
            synchronized(vectors) {
                CsBinary.writeInt32(buffer, vectors.size)
                for (v in vectors) for (x in v) CsBinary.writeFloat(buffer, x)
            }
            File(path).writeBytes(buffer.toByteArray())
        }
    }

    override suspend fun loadAsync(path: String) {
        require(path.isNotBlank()) { "path must be non-blank." }
        throwIfDisposed()
        if (!File(path).exists()) throw java.io.FileNotFoundException("Index file not found: $path")

        writeGate.withLock {
            ByteArrayInputStream(File(path).readBytes()).use { inp ->
                val magic = CsBinary.readInt32(inp)
                if (magic != INDEX_MAGIC) throw java.io.IOException("Not an InMemoryEmbeddingIndex file.")
                val version = CsBinary.readUInt16(inp)
                if (version != INDEX_VERSION) throw java.io.IOException("Unsupported index version $version.")
                val fileDim = CsBinary.readInt32(inp)
                if (fileDim != dimension) {
                    throw java.io.IOException("Loaded index dim $fileDim != configured dim $dimension.")
                }
                val n = CsBinary.readInt32(inp)
                synchronized(vectors) {
                    vectors.clear()
                    for (i in 0 until n) {
                        val v = FloatArray(dimension)
                        for (d in 0 until dimension) v[d] = CsBinary.readFloat(inp)
                        vectors.add(v)
                    }
                }
            }
        }
    }

    override fun close() {
        if (disposed) return
        disposed = true
        synchronized(vectors) { vectors.clear() }
    }

    private fun throwIfDisposed() {
        if (disposed) throw IllegalStateException("InMemoryEmbeddingIndex is disposed.")
    }

    companion object {
        const val INDEX_MAGIC: Int = 0x49455143 // "CEIQ" little-endian
        const val INDEX_VERSION: Int = 1
    }
}
