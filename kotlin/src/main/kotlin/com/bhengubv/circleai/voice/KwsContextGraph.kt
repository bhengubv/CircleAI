// KwsContextGraph.kt
//
// The keyword-spotting context graph: an Aho-Corasick trie over token ids, so
// several wake phrases can be watched for at once in a single pass over the
// decoder output rather than one pass each.
//
// Port of CircleAI.Voice/KwsContextGraph.cs and the audio-out seam from
// VoiceLoop.cs.

package com.bhengubv.circleai.voice

import java.time.Instant
import kotlin.math.max

/**
 * One node.
 *
 * A reference type on purpose: the fail and output links point sideways and
 * upwards through the trie, which a value type cannot express.
 */
class KwsContextState {
    var token: Int = -1
        internal set
    var tokenScore: Float = 0f
        internal set
    var nodeScore: Float = 0f
        internal set
    var outputScore: Float = 0f
        internal set
    var level: Int = 0
        internal set
    var acThreshold: Float = 0f
        internal set
    var isEnd: Boolean = false
        internal set
    var phrase: String = ""
        internal set
    var prefixPhrase: String = ""
        internal set
    var prefixLength: Int = 0
        internal set

    internal val next = HashMap<Int, KwsContextState>()

    /**
     * Where to continue when the next token does not extend this node. The root
     * fails to ITSELF, which is what terminates every walk.
     */
    internal var fail: KwsContextState? = null

    /** The longest phrase that ENDS here as a suffix, if any. */
    internal var output: KwsContextState? = null
}

/** One step of the walk: what it scored, where it landed, what it completed. */
data class KwsStep(val score: Float, val state: KwsContextState, val matched: KwsContextState?)

class KwsContextGraph(
    tokenIds: List<List<Int>>,
    private val contextScore: Float,
    private val acThreshold: Float,
    scores: List<Float>? = null,
    phrases: List<String>? = null,
    acThresholds: List<Float>? = null,
) {
    val root = KwsContextState()

    private val shadowed = mutableListOf<Pair<String, String>>()

    /**
     * Phrases that can NEVER fire because a shorter phrase ends inside them.
     *
     * Reported rather than silently dropped: somebody configured a wake word
     * that will not work, and they need to be told which one and why.
     */
    val shadowedPhrases: List<Pair<String, String>> get() = shadowed.toList()

    init {
        root.fail = root
        build(tokenIds, scores, phrases, acThresholds)
    }

    private fun build(
        tokenIds: List<List<Int>>,
        scores: List<Float>?,
        phrases: List<String>?,
        acThresholds: List<Float>?,
    ) {
        for (i in tokenIds.indices) {
            var node = root

            // A ZERO means "not set", so the graph-wide default applies.
            var score = if (!scores.isNullOrEmpty()) scores[i] else 0f
            if (score == 0f) score = contextScore
            var threshold = if (!acThresholds.isNullOrEmpty()) acThresholds[i] else 0f
            if (threshold == 0f) threshold = acThreshold
            val phrase = if (!phrases.isNullOrEmpty()) phrases[i] else ""
            val length = tokenIds[i].size

            for (j in 0 until length) {
                val token = tokenIds[i][j]
                val isEnd = j == length - 1

                val existing = node.next[token]
                if (existing != null) {
                    // A SHARED PREFIX keeps the HIGHER boost, so one phrase
                    // cannot quietly weaken another that starts the same way.
                    existing.tokenScore = max(score, existing.tokenScore)
                    existing.nodeScore = node.nodeScore + existing.tokenScore
                    existing.isEnd = isEnd || existing.isEnd
                    existing.outputScore = if (existing.isEnd) existing.nodeScore else 0f
                    if (isEnd) {
                        existing.phrase = phrase
                        existing.acThreshold = threshold
                    }
                    if (existing.prefixPhrase.isEmpty()) {
                        existing.prefixPhrase = phrase
                        existing.prefixLength = length
                    }
                    node = existing
                } else {
                    val child = KwsContextState()
                    child.token = token
                    child.tokenScore = score
                    child.nodeScore = node.nodeScore + score
                    child.outputScore = if (isEnd) node.nodeScore + score else 0f
                    child.level = j + 1
                    child.acThreshold = if (isEnd) threshold else 0f
                    child.isEnd = isEnd
                    child.phrase = if (isEnd) phrase else ""
                    child.prefixPhrase = phrase
                    child.prefixLength = length
                    node.next[token] = child
                    node = child
                }
            }
        }

        // A phrase whose PREFIX is itself a complete phrase can never fire: the
        // shorter one matches first and the walk never reaches the longer end.
        for (i in tokenIds.indices) {
            var node = root
            val name = if (!phrases.isNullOrEmpty()) phrases[i] else "#" + i
            for (j in tokenIds[i].indices) {
                val child = node.next[tokenIds[i][j]] ?: break
                node = child
                if (child.isEnd && j < tokenIds[i].size - 1) {
                    shadowed.add(name to child.phrase)
                    break
                }
            }
        }

        fillFailOutput()
    }

    /** Advance one token: the score contributed, the state landed on, and any completion. */
    fun forwardOneStep(state: KwsContextState, token: Int): KwsStep {
        val node: KwsContextState
        val score: Float

        val direct = state.next[token]
        if (direct != null) {
            node = direct
            score = node.tokenScore
        } else {
            // Fall back along the fail links until a node can take this token,
            // or the root is reached and there is nowhere left to fall.
            var walk = state.fail!!
            while (walk.next[token] == null) {
                walk = walk.fail!!
                if (walk.token == -1) break
            }
            node = walk.next[token] ?: walk
            // The score is the DIFFERENCE, so falling back does not re-award the
            // shared prefix that has already been counted.
            score = node.nodeScore - state.nodeScore
        }

        val matched = if (node.isEnd) node else node.output
        return KwsStep(score + node.outputScore, node, matched)
    }

    fun isMatched(state: KwsContextState): Pair<Boolean, KwsContextState?> {
        if (state.isEnd) return true to state
        state.output?.let { return true to it }
        return false to null
    }

    /** Breadth-first, so a node fail link is set before its children need it. */
    private fun fillFailOutput() {
        val queue = ArrayList<KwsContextState>()
        for (child in root.next.values) {
            child.fail = root
            queue.add(child)
        }

        var head = 0
        while (head < queue.size) {
            val current = queue[head]
            head++

            for ((token, child) in current.next) {
                var fail = current.fail!!
                val direct = fail.next[token]
                if (direct != null) {
                    fail = direct
                } else {
                    fail = fail.fail!!
                    while (fail.next[token] == null) {
                        fail = fail.fail!!
                        if (fail.token == -1) break
                    }
                    fail = fail.next[token] ?: fail
                }
                child.fail = fail

                // The OUTPUT link is what stops a shorter phrase that finishes
                // INSIDE a longer one from being swallowed by it.
                var output: KwsContextState? = fail
                while (output != null && !output.isEnd) {
                    output = output.fail
                    if (output?.token == -1) { output = null; break }
                }
                child.output = output
                child.outputScore += output?.outputScore ?: 0f

                queue.add(child)
            }
        }
    }
}

// -------------------------------------------------------- Audio out

interface IAudioPlayer {
    suspend fun play(pcm: ByteArray, sampleRate: Int, channels: Int, bitsPerSample: Int)
    suspend fun close()
}

/**
 * Swallows the audio. For a pipeline being exercised without a speaker - a
 * test, or a build with no audio output wired.
 */
class NullAudioPlayer : IAudioPlayer {
    override suspend fun play(pcm: ByteArray, sampleRate: Int, channels: Int, bitsPerSample: Int) {}
    override suspend fun close() {}

    companion object { val instance = NullAudioPlayer() }
}

/** One complete turn: what was heard and what was said back. */
data class VoiceExchange(val heard: String, val replied: String, val at: Instant)
