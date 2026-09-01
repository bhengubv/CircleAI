// SentencePieceTokenizer.kt
//
// Reading a SentencePiece model and segmenting text with it.
//
// Port of CircleAI.Voice/SentencePieceTokenizer.cs. The protobuf reader is the
// smallest one that does this job - a real dependency would be larger than the
// four fields it has to understand.

package com.bhengubv.circleai.voice

import java.io.File

enum class SentencePieceKind(val wire: Int) {
    NORMAL(1),
    UNKNOWN(2),
    CONTROL(3),
    USER_DEFINED(4),
    BYTE(5),
    UNUSED(6),
    ;

    companion object {
        /** An unknown type from a newer trainer reads as NORMAL rather than dropping the piece. */
        fun of(wire: Int): SentencePieceKind = entries.firstOrNull { it.wire == wire } ?: NORMAL
    }
}

data class SentencePiece(
    val piece: String,
    val score: Float,
    val kind: SentencePieceKind,
    val id: Int,
)

/** Segments text into the pieces a model was trained on. */
class SentencePieceTokenizer(model: ByteArray) {

    val pieces: List<SentencePiece> = readPieces(model)

    private val byPiece: Map<String, SentencePiece>

    /**
     * Some vocabularies are entirely upper case. DETECTED rather than
     * configured, because getting it wrong makes every word unknown and the
     * symptom is a tokenizer that segments everything into single characters.
     */
    val vocabularyIsUpperCase: Boolean

    private val unknownPenalty: Float
    private val longest: Int

    init {
        val map = LinkedHashMap<String, SentencePiece>()
        for (p in pieces) if (!map.containsKey(p.piece)) map[p.piece] = p
        byPiece = map

        // Worse than any real piece, so a segmentation that covers the text with
        // known pieces always beats one that gives up partway.
        unknownPenalty = if (pieces.isEmpty()) -100f else (pieces.minOf { it.score } - 10f)

        var lower = 0
        var upper = 0
        for (p in pieces) {
            if (p.kind != SentencePieceKind.NORMAL) continue
            for (c in p.piece) {
                if (c.isLowerCase()) lower++ else if (c.isUpperCase()) upper++
            }
        }
        vocabularyIsUpperCase = upper > lower * 8

        longest = map.keys.maxOfOrNull { it.length } ?: 1
    }

    /**
     * Viterbi over the string: best[i] is the score of the best segmentation of
     * the first i characters, back[i] the length of the piece ending there.
     *
     * A SINGLE CHARACTER always has a way through, at a penalty, so no input can
     * be unsegmentable - a tokenizer that can return nothing for real text is a
     * listener that silently ignores a wake word.
     */
    fun encode(text: String): List<String> {
        val norm = normalise(text)
        if (norm.isEmpty()) return emptyList()

        val n = norm.length
        val best = FloatArray(n + 1) { Float.NEGATIVE_INFINITY }
        val back = IntArray(n + 1)
        best[0] = 0f

        for (end in 1..n) {
            for (len in 1..minOf(longest, end)) {
                val start = end - len
                if (best[start] == Float.NEGATIVE_INFINITY) continue
                val span = norm.substring(start, end)

                val piece = byPiece[span]
                val score = when {
                    piece != null &&
                        (piece.kind == SentencePieceKind.NORMAL ||
                            piece.kind == SentencePieceKind.USER_DEFINED) -> piece.score
                    len == 1 -> unknownPenalty
                    else -> continue
                }

                val total = best[start] + score
                if (total > best[end]) { best[end] = total; back[end] = len }
            }
        }

        val out = mutableListOf<String>()
        var at = n
        while (at > 0) {
            val len = maxOf(1, back[at])
            out.add(norm.substring(at - len, at))
            at -= len
        }
        return out.reversed()
    }

    /**
     * Whether every piece the segmentation produced is one the model knows.
     *
     * Returns the unknown pieces, so a caller can tell somebody WHICH sounds the
     * listener does not have rather than just refusing their wake word.
     */
    fun canRepresent(text: String): Pair<Boolean, List<String>> {
        val seen = LinkedHashSet<String>()
        for (p in encode(text)) if (!byPiece.containsKey(p)) seen.add(p)
        return seen.isEmpty() to seen.toList()
    }

    /**
     * Trim, upper-case when the vocabulary is, then replace every run of
     * whitespace with a single word-start marker - INCLUDING a leading one.
     */
    internal fun normalise(text: String): String {
        var s = text.trim()
        if (vocabularyIsUpperCase) s = s.uppercase()

        val out = StringBuilder()
        out.append(WORD_START)
        var lastWasSpace = true
        for (c in s) {
            if (c.isWhitespace()) {
                if (!lastWasSpace) out.append(WORD_START)
                lastWasSpace = true
            } else {
                out.append(c)
                lastWasSpace = false
            }
        }
        return out.toString()
    }

    companion object {
        /**
         * U+2581 LOWER ONE EIGHTH BLOCK - what SentencePiece uses for a word
         * boundary. NOT a space: a space is a character the model never sees.
         */
        const val WORD_START: Char = '▁'

        fun fromFile(path: String): SentencePieceTokenizer? {
            val f = File(path)
            if (!f.isFile) return null
            return SentencePieceTokenizer(f.readBytes())
        }

        // ---- The smallest protobuf reader that does this job
        //
        //   ModelProto    { repeated SentencePiece pieces = 1; ... }
        //   SentencePiece { string piece = 1; float score = 2; Type type = 3; }
        //
        // Unknown fields are skipped BY WIRE TYPE, so a model carrying a trainer
        // spec or a normaliser blob - which every real one does - still reads.

        internal fun readPieces(data: ByteArray): List<SentencePiece> {
            val pieces = mutableListOf<SentencePiece>()
            val i = intArrayOf(0)
            while (i[0] < data.size) {
                val key = readVarint(data, i) ?: break
                val field = (key shr 3).toInt()
                val wire = (key and 7L).toInt()

                if (field == 1 && wire == 2) {
                    val len = readVarint(data, i) ?: break
                    if (i[0] + len.toInt() > data.size) break
                    pieces.add(
                        readPiece(data.copyOfRange(i[0], i[0] + len.toInt()), pieces.size),
                    )
                    i[0] += len.toInt()
                } else if (!skipField(data, i, wire)) {
                    break
                }
            }
            return pieces
        }

        internal fun readPiece(data: ByteArray, id: Int): SentencePiece {
            var piece = ""
            var score = 0f
            var kind = SentencePieceKind.NORMAL
            val i = intArrayOf(0)

            while (i[0] < data.size) {
                val key = readVarint(data, i) ?: break
                val field = (key shr 3).toInt()
                val wire = (key and 7L).toInt()

                if (field == 1 && wire == 2) {
                    val len = readVarint(data, i) ?: break
                    if (i[0] + len.toInt() > data.size) break
                    piece = String(data, i[0], len.toInt(), Charsets.UTF_8)
                    i[0] += len.toInt()
                } else if (field == 2 && wire == 5) {
                    if (i[0] + 4 > data.size) break
                    // fixed32, little-endian IEEE-754.
                    val bits = (data[i[0]].toInt() and 0xFF) or
                        ((data[i[0] + 1].toInt() and 0xFF) shl 8) or
                        ((data[i[0] + 2].toInt() and 0xFF) shl 16) or
                        ((data[i[0] + 3].toInt() and 0xFF) shl 24)
                    score = Float.fromBits(bits)
                    i[0] += 4
                } else if (field == 3 && wire == 0) {
                    val v = readVarint(data, i) ?: break
                    kind = SentencePieceKind.of(v.toInt())
                } else if (!skipField(data, i, wire)) {
                    break
                }
            }
            return SentencePiece(piece, score, kind, id)
        }

        /**
         * Base-128, low group first, high bit set while more follow. BOUNDED at
         * ten groups so a corrupt file cannot spin.
         */
        internal fun readVarint(data: ByteArray, i: IntArray): Long? {
            var result = 0L
            var shift = 0
            var groups = 0
            while (i[0] < data.size && groups < 10) {
                val b = data[i[0]].toInt() and 0xFF
                i[0]++
                groups++
                result = result or ((b and 0x7F).toLong() shl shift)
                if (b and 0x80 == 0) return result
                shift += 7
            }
            return null
        }

        internal fun skipField(data: ByteArray, i: IntArray, wire: Int): Boolean = when (wire) {
            0 -> readVarint(data, i) != null
            1 -> { i[0] += 8; i[0] <= data.size }
            2 -> {
                val len = readVarint(data, i)
                if (len == null) false else { i[0] += len.toInt(); i[0] <= data.size }
            }
            5 -> { i[0] += 4; i[0] <= data.size }
            // Groups (3 and 4) are long gone from proto3.
            else -> false
        }
    }
}
