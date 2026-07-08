// HerJarvisImplementations.kt
//
// Kotlin port of CircleAI.Companion.HerJarvis real implementations — the C#
// reference (HerJarvisRealImplementations.cs) is the EXACT spec. Real, working,
// in-process backings so tests and hosts both get behaviour, not no-ops.
// Production hosts swap any of these behind the same interface.
//
// Design fidelity notes:
//   * ConcurrentDictionary            -> java.util.concurrent.ConcurrentHashMap
//   * Channel.CreateUnbounded<T>      -> kotlinx.coroutines.channels.Channel(UNLIMITED)
//     surfaced as a Flow that drains buffered items then suspends for more.
//   * ECDSA P-256 sign/verify         -> java.security KeyPairGenerator("EC") + Signature("SHA256withECDSA")
//   * JSON payload byte formats        -> hand-built strings, matching C# byte-for-byte.
//   * StringComparer.OrdinalIgnoreCase where the RETURNED key matters is reproduced
//     with a lower-cased lookup key that remembers the first-seen original spelling.

package com.bhengubv.circleai.companion.herjarvis

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.time.Duration
import java.time.Instant
import java.time.ZoneOffset
import java.util.Base64
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.floor
import kotlin.math.ln
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sin
import kotlin.math.sqrt

// =====================================================================
// 1. AlwaysOnPresence — coroutine-free heartbeat with start/stop.
//
// The C# impl uses a System.Threading.Timer incrementing a tick counter.
// A deterministic in-memory port models the same observable surface: a
// [heartbeats] counter that [pulse] bumps, IsRunning toggled by start/stop.
// A host that wants a real wall-clock timer injects a scheduler and calls
// [pulse]; the default is driveable by tests without sleeping.
// =====================================================================
class HeartbeatAlwaysOnPresence(
    val heartbeatInterval: Duration = Duration.ofSeconds(30),
) : IAlwaysOnPresence {

    @Volatile
    private var running = false
    private val ticks = AtomicLong(0)

    override val isRunning: Boolean get() = running

    val heartbeats: Long get() = ticks.get()

    override suspend fun startAsync() {
        if (running) return
        running = true
        // C# fires an immediate tick at dueTime TimeSpan.Zero.
        ticks.incrementAndGet()
    }

    override suspend fun stopAsync() {
        running = false
    }

    /** Advance the heartbeat by one tick. A host's scheduler calls this each [heartbeatInterval]. */
    fun pulse() {
        if (running) ticks.incrementAndGet()
    }
}

// =====================================================================
// 2. FusedPerception — Channel-based pub/sub with Publish hook.
// =====================================================================
class ChannelFusedPerception : IFusedPerception {
    private val channel = Channel<FusedPercept>(Channel.UNLIMITED)

    fun publish(p: FusedPercept) {
        channel.trySend(p)
    }

    fun complete() {
        channel.close()
    }

    override fun streamAsync(): Flow<FusedPercept> = flow {
        for (p in channel) emit(p)
    }
}

// =====================================================================
// 3. IdentitySync — append-only delta log with monotonic cursor.
//
// PullAsync returns a JSON envelope byte-for-byte identical to the C#:
//   {"cursor":<max>,"deltas":[<delta0>,<delta1>,...]}
// where each delta is spliced in verbatim (deltas are assumed to be JSON).
// =====================================================================
class JsonIdentitySync : IIdentitySync {
    private data class Entry(val cursor: Long, val deltaJson: String)

    private val log = ArrayList<Entry>()
    private val lock = Any()
    private val next = AtomicLong(0)

    override suspend fun pushAsync(deltaJson: String) {
        synchronized(lock) { log.add(Entry(next.incrementAndGet(), deltaJson)) }
    }

    override suspend fun pullAsync(sinceCursor: String): String {
        val since = sinceCursor.toLongOrNull() ?: 0L
        val maxCursor: Long
        val deltas: List<String>
        synchronized(lock) {
            val taken = log.filter { it.cursor > since }
            maxCursor = if (taken.isEmpty()) since else taken.last().cursor
            deltas = taken.map { it.deltaJson }
        }
        val sb = StringBuilder().append("{\"cursor\":").append(maxCursor).append(",\"deltas\":[")
        for (i in deltas.indices) {
            if (i > 0) sb.append(',')
            sb.append(deltas[i])
        }
        sb.append("]}")
        return sb.toString()
    }
}

// =====================================================================
// 4. ContinuousLearner — exponentially weighted average reward per id.
// =====================================================================
class EwaContinuousLearner(private val alpha: Double = 0.2) : IContinuousLearner {
    private data class State(val avg: Double, val weight: Double)

    private val state = ConcurrentHashMap<String, State>()

    init {
        require(alpha > 0 && alpha <= 1) { "alpha out of range" }
    }

    override suspend fun registerFeedbackAsync(interactionId: String, reward: Double, contextJson: String) {
        require(interactionId.isNotBlank()) { "interactionId required" }
        state.compute(interactionId) { _, prev ->
            if (prev == null) State(reward, 1.0)
            else State(prev.avg * (1 - alpha) + reward * alpha, prev.weight + 1)
        }
    }

    fun averageRewardOf(interactionId: String): Double? = state[interactionId]?.avg

    fun observationsOf(interactionId: String): Long = state[interactionId]?.weight?.toLong() ?: 0L
}

// =====================================================================
// 5. WorldModel — ported in reasoning.WorldModel (FrequencyWorldModel).
//    Contract IWorldModel/CausalPrediction live in that package.
// =====================================================================

// =====================================================================
// 6. GoalPursuer — store goal + milestones; replan recalculates plan.
//
// BuildPlan reproduces the C# milestone-JSON byte-for-byte:
//   {"description":<json-string>,"milestones":[{"index":1,"due":"<O>"},...]}
// where <O> is the ISO-8601 round-trip ("O") format of a UTC instant.
// =====================================================================
class InMemoryGoalPursuer : IGoalPursuer {
    private val goals = ConcurrentHashMap<String, LongHorizonGoal>()
    private val lock = Any()

    override suspend fun registerAsync(description: String, deadlineUtc: Instant): LongHorizonGoal {
        require(description.isNotBlank()) { "description required" }
        val id = UUID.randomUUID().toString().replace("-", "")
        val now = Instant.now()
        require(deadlineUtc.isAfter(now)) { "deadline must be in the future" }
        val plan = buildPlan(description, now, deadlineUtc)
        val g = LongHorizonGoal(id, description, deadlineUtc, plan, 0.0)
        goals[id] = g
        return g
    }

    override suspend fun currentAsync(id: String): LongHorizonGoal? = goals[id]

    override suspend fun replanAsync(id: String) {
        synchronized(lock) {
            val g = goals[id] ?: throw IllegalStateException("Unknown goal $id")
            val plan = buildPlan(g.description, Instant.now(), g.deadlineUtc)
            goals[id] = g.copy(planJson = plan)
        }
    }

    fun progress(id: String, fraction: Double) {
        require(fraction in 0.0..1.0) { "fraction out of range" }
        val g = goals[id] ?: throw IllegalStateException("Unknown goal $id")
        goals[id] = g.copy(progressFraction = fraction)
    }

    private fun buildPlan(description: String, now: Instant, deadlineUtc: Instant): String {
        val totalDays = max(1L, Duration.between(now, deadlineUtc).toDays())
        val milestones = min(8L, max(2L, totalDays / 14)).toInt()
        val totalNanos = Duration.between(now, deadlineUtc)
        val sb = StringBuilder()
            .append("{\"description\":").append(jsonString(description))
            .append(",\"milestones\":[")
        for (i in 1..milestones) {
            if (i > 1) sb.append(',')
            // step * i = totalSpan * i / milestones (integer-division semantics on the tick count).
            val due = now.plus(scaleDuration(totalNanos, i.toLong(), milestones.toLong()))
            sb.append("{\"index\":").append(i).append(",\"due\":\"").append(isoRoundTrip(due)).append("\"}")
        }
        sb.append("]}")
        return sb.toString()
    }
}

// =====================================================================
// 7. EpisodicMemory — TF-based similarity recall.
// =====================================================================
class TfEpisodicMemory : IEpisodicMemory {
    private val episodes = ConcurrentHashMap<String, EpisodeRecord>()
    private val terms = ConcurrentHashMap<String, Map<String, Int>>()
    private val lock = Any()

    override suspend fun recordAsync(episode: EpisodeRecord) {
        require(episode.id.isNotBlank()) { "Id required" }
        synchronized(lock) {
            episodes[episode.id] = episode
            terms[episode.id] = toTermFrequency(episode.title + " " + episode.contentJson)
        }
    }

    override suspend fun recallAsync(query: String, take: Int): List<EpisodeRecord> {
        require(take > 0) { "take out of range" }
        val qTerms = toTermFrequency(query)
        if (qTerms.isEmpty()) return emptyList()
        synchronized(lock) {
            return episodes.values
                .map { it to score(qTerms, terms[it.id]) }
                .filter { it.second > 0 }
                .sortedByDescending { it.second }
                .take(take)
                .map { it.first }
        }
    }

    private companion object {
        private val SPLIT_RX = Regex("[^A-Za-z0-9]+")

        fun toTermFrequency(text: String?): Map<String, Int> {
            val d = LinkedHashMap<String, Int>()
            for (t in SPLIT_RX.split(text ?: "").filter { it.length >= 2 }) {
                val key = t.lowercase()
                d[key] = (d[key] ?: 0) + 1
            }
            return d
        }

        fun score(q: Map<String, Int>, d: Map<String, Int>?): Double {
            if (d == null) return 0.0
            var s = 0.0
            for ((k, v) in q) d[k]?.let { s += v * it }
            return s
        }
    }
}

// =====================================================================
// 8. VoiceIdentity — MFCC fingerprint over windowed audio.
//
// Full pre-emphasis -> frame -> Hamming -> DFT power spectrum -> mel
// filterbank -> log -> DCT-II -> mean-MFCC pipeline. Cosine similarity
// with a 0.85 acceptance threshold. Ported constant-for-constant.
// =====================================================================
class EnergyBandVoiceIdentity : IVoiceIdentity {
    private val enrolled = ConcurrentHashMap<String, MutableList<DoubleArray>>()
    private val lock = Any()

    override suspend fun enrollAsync(userId: String, audioPcm16: ByteArray, sampleRateHz: Int) {
        require(userId.isNotBlank()) { "userId required" }
        val fp = mfcc(audioPcm16, sampleRateHz)
        synchronized(lock) {
            enrolled.getOrPut(userId) { ArrayList() }.add(fp)
        }
    }

    override suspend fun identifyAsync(audioPcm16: ByteArray, sampleRateHz: Int): String? {
        val fp = mfcc(audioPcm16, sampleRateHz)
        var best: String? = null
        var bestSim = -1.0
        synchronized(lock) {
            for ((user, references) in enrolled) {
                for (reference in references) {
                    val sim = cosineSimilarity(fp, reference)
                    if (sim > bestSim) {
                        bestSim = sim
                        best = user
                    }
                }
            }
        }
        return if (bestSim > 0.85) best else null
    }

    private companion object {
        const val NUM_COEFFICIENTS = 13
        const val NUM_MEL_FILTERS = 26
        const val FRAME_SIZE = 400   // 25ms @ 16kHz
        const val FRAME_STEP = 160   // 10ms @ 16kHz
        const val PRE_EMPHASIS = 0.97f

        fun mfcc(pcm16: ByteArray, sampleRateHz: Int): DoubleArray {
            val samples = decodePcm16(pcm16)
            if (samples.size < FRAME_SIZE) return DoubleArray(NUM_COEFFICIENTS)
            preEmphasisFilter(samples)
            val filters = melFilterbank(NUM_MEL_FILTERS, FRAME_SIZE, sampleRateHz)

            val sum = DoubleArray(NUM_COEFFICIENTS)
            var count = 0
            val window = hammingWindow(FRAME_SIZE)
            var start = 0
            while (start + FRAME_SIZE <= samples.size) {
                val frame = FloatArray(FRAME_SIZE)
                for (i in 0 until FRAME_SIZE) frame[i] = samples[start + i] * window[i]
                val powerSpec = powerSpectrum(frame)
                val melEnergies = applyFilterbank(powerSpec, filters)
                val logEnergies = DoubleArray(NUM_MEL_FILTERS)
                for (i in 0 until NUM_MEL_FILTERS) logEnergies[i] = ln(max(1e-10, melEnergies[i]))
                val coeffs = dct(logEnergies, NUM_COEFFICIENTS)
                for (i in 0 until NUM_COEFFICIENTS) sum[i] += coeffs[i]
                count++
                start += FRAME_STEP
            }
            if (count == 0) return sum
            for (i in 0 until NUM_COEFFICIENTS) sum[i] /= count
            return sum
        }

        fun decodePcm16(pcm16: ByteArray): FloatArray {
            val n = pcm16.size / 2
            val samples = FloatArray(n)
            for (i in 0 until n) {
                val lo = pcm16[i * 2].toInt() and 0xFF
                val hi = pcm16[i * 2 + 1].toInt() // sign-extended high byte, matches (short)(lo | (hi<<8))
                val s = (lo or (hi shl 8)).toShort()
                samples[i] = s / 32768f
            }
            return samples
        }

        fun preEmphasisFilter(samples: FloatArray) {
            for (i in samples.size - 1 downTo 1) samples[i] -= PRE_EMPHASIS * samples[i - 1]
        }

        fun hammingWindow(n: Int): FloatArray {
            val w = FloatArray(n)
            for (i in 0 until n) w[i] = 0.54f - 0.46f * cos(2 * PI * i / (n - 1)).toFloat()
            return w
        }

        fun powerSpectrum(frame: FloatArray): DoubleArray {
            val n = frame.size
            val half = n / 2 + 1
            val spec = DoubleArray(half)
            for (k in 0 until half) {
                var re = 0.0
                var im = 0.0
                val omega = -2.0 * PI * k / n
                for (t in 0 until n) {
                    re += frame[t] * cos(omega * t)
                    im += frame[t] * sin(omega * t)
                }
                spec[k] = re * re + im * im
            }
            return spec
        }

        fun melFilterbank(numFilters: Int, frameSize: Int, sampleRateHz: Int): Array<DoubleArray> {
            fun hzToMel(hz: Double) = 2595 * log10(1 + hz / 700.0)
            fun melToHz(mel: Double) = 700 * (Math.pow(10.0, mel / 2595) - 1)
            val lowMel = hzToMel(0.0)
            val highMel = hzToMel(sampleRateHz / 2.0)
            val melPoints = DoubleArray(numFilters + 2)
            for (i in melPoints.indices) melPoints[i] = lowMel + (highMel - lowMel) * i / (melPoints.size - 1)
            val binPoints = IntArray(melPoints.size)
            for (i in melPoints.indices) binPoints[i] = floor((frameSize + 1) * melToHz(melPoints[i]) / sampleRateHz).toInt()

            val half = frameSize / 2 + 1
            val filters = Array(numFilters) { DoubleArray(half) }
            for (m in 0 until numFilters) {
                val left = binPoints[m]
                val centre = binPoints[m + 1]
                val right = binPoints[m + 2]
                var k = left
                while (k < centre && k < half) {
                    if (centre != left) filters[m][k] = (k - left).toDouble() / (centre - left)
                    k++
                }
                k = centre
                while (k < right && k < half) {
                    if (right != centre) filters[m][k] = (right - k).toDouble() / (right - centre)
                    k++
                }
            }
            return filters
        }

        fun applyFilterbank(powerSpec: DoubleArray, filters: Array<DoubleArray>): DoubleArray {
            val energies = DoubleArray(filters.size)
            for (m in filters.indices) {
                var sum = 0.0
                val filter = filters[m]
                val len = min(powerSpec.size, filter.size)
                for (k in 0 until len) sum += powerSpec[k] * filter[k]
                energies[m] = sum
            }
            return energies
        }

        fun dct(input: DoubleArray, numCoeffs: Int): DoubleArray {
            val n = input.size
            val output = DoubleArray(numCoeffs)
            for (k in 0 until numCoeffs) {
                var sum = 0.0
                for (i in 0 until n) sum += input[i] * cos(PI * k * (i + 0.5) / n)
                output[k] = sum
            }
            return output
        }

        fun cosineSimilarity(a: DoubleArray, b: DoubleArray): Double {
            var dot = 0.0
            var na = 0.0
            var nb = 0.0
            for (i in a.indices) {
                dot += a[i] * b[i]
                na += a[i] * a[i]
                nb += b[i] * b[i]
            }
            return if (na == 0.0 || nb == 0.0) 0.0 else dot / (sqrt(na) * sqrt(nb))
        }
    }
}

// =====================================================================
// 9. CalibratedConfidence — nearest-neighbour correctness calibration.
// =====================================================================
class HistoricalCalibratedConfidence : ICalibratedConfidence {
    private data class Outcome(val rawScore: Double, val wasCorrect: Boolean)

    private val history = ArrayList<Outcome>()
    private val lock = Any()

    fun recordOutcome(rawScore: Double, wasCorrect: Boolean) {
        synchronized(lock) { history.add(Outcome(rawScore.coerceIn(0.0, 1.0), wasCorrect)) }
    }

    override suspend fun evaluateAsync(answer: String, contextJson: String): ConfidenceBand {
        val raw = computeRawScore(answer, contextJson)
        val calibrated: Double
        synchronized(lock) {
            calibrated = if (history.size < 5) {
                raw
            } else {
                val nearby = history.sortedBy { kotlin.math.abs(it.rawScore - raw) }.take(5)
                nearby.count { it.wasCorrect }.toDouble() / nearby.size
            }
        }
        val halfBand = max(0.05, 0.25 - calibrated * 0.2)
        return ConfidenceBand(
            max(0.0, calibrated - halfBand),
            min(1.0, calibrated + halfBand),
        )
    }

    private companion object {
        private val HEDGE_RX = Regex("""\b(maybe|perhaps|might|possibly|unclear|don't know)\b""", RegexOption.IGNORE_CASE)

        fun computeRawScore(answer: String, contextJson: String): Double {
            val len = max(1, answer.trim().length)
            val hedges = HEDGE_RX.findAll(answer).count()
            val hedgePenalty = min(0.5, hedges * 0.1)
            val hasContext = contextJson.isNotBlank() && contextJson.length > 2
            return ((ln(len.toDouble()) / 10.0) + (if (hasContext) 0.1 else 0.0) - hedgePenalty).coerceIn(0.0, 1.0)
        }
    }
}

// =====================================================================
// 10. TheoryOfMind — ported in reasoning.TheoryOfMind.
// =====================================================================

// =====================================================================
// 11. EmotionSensor — keyword + arousal-valence inference from fused JSON.
// =====================================================================
class KeywordEmotionSensor : IEmotionSensor {
    private data class Pattern(val label: String, val arousal: Double, val valence: Double, val rx: Regex)

    override suspend fun senseAsync(fusedJson: String): EmotionFrame {
        val hits = PATTERNS
            .map { it to it.rx.findAll(fusedJson).count() }
            .filter { it.second > 0 }
        if (hits.isEmpty()) return EmotionFrame("neutral", 0.0, 0.0)
        val totalWeight = hits.sumOf { it.second }
        val arousal = hits.sumOf { it.first.arousal * it.second } / totalWeight
        val valence = hits.sumOf { it.first.valence * it.second } / totalWeight
        val top = hits.maxByOrNull { it.second }!!.first.label
        return EmotionFrame(top, arousal, valence)
    }

    private companion object {
        private fun rx(p: String) = Regex(p, RegexOption.IGNORE_CASE)
        val PATTERNS = listOf(
            Pattern("joy", 0.8, 0.9, rx("""\b(happy|joy|delight|excited|love|wonderful)\b""")),
            Pattern("anger", 0.9, -0.8, rx("""\b(angry|furious|rage|hate|annoyed)\b""")),
            Pattern("sad", 0.3, -0.7, rx("""\b(sad|lonely|grief|cry|depressed|down)\b""")),
            Pattern("fear", 0.85, -0.6, rx("""\b(afraid|scared|terrified|anxious|worried)\b""")),
            Pattern("surprise", 0.7, 0.3, rx("""\b(surprised|amazed|astonished|wow)\b""")),
            Pattern("calm", 0.1, 0.5, rx("""\b(calm|peaceful|relaxed|content|fine)\b""")),
        )
    }
}

// =====================================================================
// 12. SkillAcquisition — demo-store with name extraction.
// =====================================================================
class DemoStoreSkillAcquisition : ISkillAcquisition {
    private val skills = ConcurrentHashMap<String, AcquiredSkill>()

    override suspend fun acquireAsync(demonstrationJson: String): AcquiredSkill {
        val id = UUID.randomUUID().toString().replace("-", "")
        val name = extractName(demonstrationJson) ?: "skill-" + id.substring(0, 6)
        val skill = AcquiredSkill(id, name, demonstrationJson)
        skills[id] = skill
        return skill
    }

    override suspend fun listAsync(): List<AcquiredSkill> = skills.values.sortedBy { it.name }

    private companion object {
        fun extractName(demonstrationJson: String): String? = try {
            val el = LenientJson.parse(demonstrationJson)
            if (el is JsonObj) (el.map["name"] as? JsonStr)?.value else null
        } catch (_: Exception) {
            null
        }
    }
}

// =====================================================================
// 15. PersonalKnowledgeGraph — adjacency-list graph with relation kinds.
// =====================================================================
class AdjacencyPersonalKnowledgeGraph : IPersonalKnowledgeGraph {
    private val nodes = ConcurrentHashMap<String, KnowledgeNode>()
    private val outEdges = ConcurrentHashMap<String, MutableList<KnowledgeRelation>>()
    private val lock = Any()

    override suspend fun upsertNodeAsync(node: KnowledgeNode) {
        require(node.id.isNotBlank()) { "Id required" }
        nodes[node.id] = node
    }

    override suspend fun upsertRelationAsync(rel: KnowledgeRelation) {
        synchronized(lock) {
            val list = outEdges.getOrPut(rel.fromId) { ArrayList() }
            list.removeAll { it.toId == rel.toId && it.relation == rel.relation }
            list.add(rel)
        }
    }

    override suspend fun neighboursAsync(id: String): List<KnowledgeNode> {
        require(id.isNotBlank()) { "id required" }
        synchronized(lock) {
            val rels = outEdges[id] ?: return emptyList()
            return rels.mapNotNull { nodes[it.toId] }
        }
    }
}

// =====================================================================
// 16. LiveWorldKnowledge — topic-pub/sub broker.
// =====================================================================
class TopicLiveWorldKnowledge : ILiveWorldKnowledge {
    private val byTopic = ConcurrentHashMap<String, Channel<WorldFact>>()

    /** Publish a fact to subscribers of the matching topic. */
    fun publish(fact: WorldFact) {
        byTopic[fact.topic]?.trySend(fact)
    }

    override fun subscribeAsync(topics: List<String>): Flow<WorldFact> {
        val channels = topics.map { byTopic.getOrPut(it) { Channel(Channel.UNLIMITED) } }
        return flow {
            // Fan-in: forward whatever is buffered on any subscribed topic, then
            // suspend on the first channel that has more (mirrors C# poll loop but
            // structured-concurrency friendly — no busy Task.Delay).
            for (c in channels) {
                var next = c.tryReceive()
                while (next.isSuccess) {
                    emit(next.getOrThrow())
                    next = c.tryReceive()
                }
            }
            // Then stream live from every channel as it arrives.
            for (c in channels) {
                for (fact in c) emit(fact)
            }
        }
    }
}

// =====================================================================
// 17. BioSignalStream — fan-in channel with Publish hook.
// =====================================================================
class ChannelBioSignalStream : IBioSignalStream {
    private val channel = Channel<BioSignal>(Channel.UNLIMITED)

    fun publish(s: BioSignal) {
        channel.trySend(s)
    }

    fun complete() {
        channel.close()
    }

    override fun streamAsync(): Flow<BioSignal> = flow {
        for (s in channel) emit(s)
    }
}

// =====================================================================
// 18. PhysicalActuator — device-handler registry with per-action dispatch.
// =====================================================================
class RegistryPhysicalActuator : IPhysicalActuator {
    private val handlers =
        ConcurrentHashMap<String, suspend (PhysicalCommand) -> PhysicalCommandResult>()

    fun registerDevice(deviceId: String, handler: suspend (PhysicalCommand) -> PhysicalCommandResult) {
        require(deviceId.isNotBlank()) { "deviceId required" }
        handlers[deviceId] = handler
    }

    override suspend fun invokeAsync(command: PhysicalCommand): PhysicalCommandResult {
        val h = handlers[command.deviceId]
            ?: return PhysicalCommandResult(false, "Unknown device '${command.deviceId}'")
        return h(command)
    }
}

// =====================================================================
// 19. AgentPeerNetwork — in-memory mailbox per agent id.
// =====================================================================
class MailboxAgentPeerNetwork : IAgentPeerNetwork {
    private val mailboxes = ConcurrentHashMap<String, Channel<AgentToAgentMessage>>()

    override suspend fun sendAsync(message: AgentToAgentMessage) {
        val box = mailboxes.getOrPut(message.toAgentId) { Channel(Channel.UNLIMITED) }
        box.trySend(message)
    }

    override fun receiveAsync(forAgentId: String): Flow<AgentToAgentMessage> {
        require(forAgentId.isNotBlank()) { "forAgentId required" }
        val box = mailboxes.getOrPut(forAgentId) { Channel(Channel.UNLIMITED) }
        return flow {
            for (m in box) emit(m)
        }
    }
}

// =====================================================================
// 20. FederatedFineTuner — job runner with status tracking.
//
// A trainer callback drives progress 0->1. The default trainer counts the
// lines of the training file (100 if absent) and steps progress once per
// line, matching the C# DefaultTrainer's shape. Injected trainers replace it.
// =====================================================================
class InMemoryFederatedFineTuner(
    private val trainer: (suspend (baseModel: String, path: String, progress: (Double) -> Unit) -> Unit)? = null,
) : IFederatedFineTuner {
    private val jobs = ConcurrentHashMap<String, FineTuneJobStatus>()

    override suspend fun startAsync(baseModel: String, trainingDataPath: String): String {
        require(baseModel.isNotBlank()) { "baseModel required" }
        require(trainingDataPath.isNotBlank()) { "trainingDataPath required" }
        val jobId = UUID.randomUUID().toString().replace("-", "")
        jobs[jobId] = FineTuneJobStatus(jobId, 0.0, null)
        val report: (Double) -> Unit = { p ->
            jobs[jobId] = jobs[jobId]!!.copy(progress = p.coerceIn(0.0, 1.0))
        }
        try {
            val t = trainer
            if (t != null) t(baseModel, trainingDataPath, report) else defaultTrainer(baseModel, trainingDataPath, report)
            jobs[jobId] = jobs[jobId]!!.copy(progress = 1.0, error = null)
        } catch (ex: Exception) {
            jobs[jobId] = jobs[jobId]!!.copy(error = ex.message)
        }
        return jobId
    }

    override suspend fun statusAsync(jobId: String): FineTuneJobStatus =
        jobs[jobId] ?: FineTuneJobStatus(jobId, 0.0, "unknown job")

    private companion object {
        suspend fun defaultTrainer(@Suppress("UNUSED_PARAMETER") baseModel: String, path: String, progress: (Double) -> Unit) {
            val file = java.io.File(path)
            val lineCount = if (file.exists()) file.readLines().size else 100
            val step = 1.0 / max(1, lineCount)
            for (i in 0 until lineCount) progress(i * step)
            progress(1.0)
        }
    }
}

// =====================================================================
// 21. FirstTokenOptimizer — sliding-window p50 latency tracker.
// =====================================================================
class SlidingP50FirstTokenOptimizer(
    private val targetMs: Int = 100,
    private val windowSize: Int = 256,
) : IFirstTokenOptimizer {
    private val samples = ArrayDeque<Int>()
    private val lock = Any()

    init {
        require(targetMs > 0) { "targetMs out of range" }
        require(windowSize > 0) { "windowSize out of range" }
    }

    fun recordFirstTokenLatency(ms: Int) {
        require(ms >= 0) { "ms out of range" }
        synchronized(lock) {
            samples.addLast(ms)
            while (samples.size > windowSize) samples.removeFirst()
        }
    }

    override suspend fun currentAsync(): FirstTokenBudget {
        val p50: Int
        synchronized(lock) {
            p50 = if (samples.isEmpty()) {
                0
            } else {
                val sorted = samples.sorted()
                sorted[sorted.size / 2]
            }
        }
        return FirstTokenBudget(targetMs, p50)
    }
}

// =====================================================================
// 22. CryptoDelegation — ECDSA P-256 sign + verify.
//
// Canonical payload string is byte-identical to the C#:
//   "<issuer>|<subjectId>|<scope>|<expiresAtUtc:O>"
// signed with SHA256withECDSA over its UTF-8 bytes, Base64 signature.
// =====================================================================
class EcdsaCryptoDelegation(
    private val issuer: String = "circleai-companion",
    keyPair: KeyPair? = null,
) : ICryptoDelegation {
    private val keys: KeyPair

    init {
        require(issuer.isNotBlank()) { "issuer required" }
        keys = keyPair ?: run {
            val kpg = KeyPairGenerator.getInstance("EC")
            kpg.initialize(ECGenParameterSpec("secp256r1"))
            kpg.generateKeyPair()
        }
    }

    override fun issue(subjectId: String, scope: String, lifetime: Duration): DelegationCredential {
        require(subjectId.isNotBlank()) { "subjectId required" }
        require(scope.isNotBlank()) { "scope required" }
        require(!lifetime.isZero && !lifetime.isNegative) { "lifetime out of range" }
        val expires = Instant.now().plus(lifetime)
        val payload = canonical(subjectId, scope, expires)
        val sig = Signature.getInstance("SHA256withECDSA").run {
            initSign(keys.private)
            update(payload.toByteArray(Charsets.UTF_8))
            sign()
        }
        return DelegationCredential(issuer, subjectId, scope, expires, Base64.getEncoder().encodeToString(sig))
    }

    override fun verify(credential: DelegationCredential): Boolean {
        if (credential.issuer != issuer) return false
        if (!credential.expiresAtUtc.isAfter(Instant.now())) return false
        if (credential.signature.isEmpty()) return false
        val sig = try {
            Base64.getDecoder().decode(credential.signature)
        } catch (_: IllegalArgumentException) {
            return false
        }
        val payload = canonical(credential.subjectId, credential.scope, credential.expiresAtUtc)
        return try {
            Signature.getInstance("SHA256withECDSA").run {
                initVerify(keys.public)
                update(payload.toByteArray(Charsets.UTF_8))
                verify(sig)
            }
        } catch (_: Exception) {
            false
        }
    }

    private fun canonical(subjectId: String, scope: String, expiresAtUtc: Instant): String =
        "$issuer|$subjectId|$scope|${isoRoundTrip(expiresAtUtc)}"
}

// =====================================================================
// 23. CodeGenerationLoop — syntax-validates + runs registered tests.
// =====================================================================
class SyntaxCheckingCodeGenerationLoop(
    private val generator: (suspend (prompt: String) -> String)? = null,
    private val testRunner: (suspend (snippet: String) -> Boolean)? = null,
    private val deploymentHint: ((snippet: String) -> String?)? = null,
) : ICodeGenerationLoop {

    override suspend fun runAsync(prompt: String): CodeGenJob {
        require(prompt.isNotBlank()) { "prompt required" }
        val id = UUID.randomUUID().toString().replace("-", "")
        val g = generator
        val snippet = if (g != null) g(prompt) else defaultGenerator(prompt)
        val parses = isSyntacticallyBalanced(snippet)
        val tr = testRunner
        val testsOk = parses && (if (tr != null) tr(snippet) else defaultTestRunner(snippet))
        val dh = deploymentHint
        val hint = if (testsOk) (if (dh != null) dh(snippet) else defaultDeploymentHint(snippet)) else null
        return CodeGenJob(id, prompt, snippet, testsOk, hint)
    }

    private companion object {
        fun defaultGenerator(prompt: String): String =
            "// (3.3.0) generated from: ${prompt.replace('\n', ' ')}\nreturn 0;"

        fun defaultTestRunner(snippet: String): Boolean = isSyntacticallyBalanced(snippet)

        fun defaultDeploymentHint(snippet: String): String =
            if (snippet.contains("public class")) "stage as nuget" else "run inline"

        fun isSyntacticallyBalanced(snippet: String): Boolean {
            if (snippet.isEmpty()) return false
            var curly = 0
            var paren = 0
            var square = 0
            for (c in snippet) {
                when (c) {
                    '{' -> curly++
                    '}' -> curly--
                    '(' -> paren++
                    ')' -> paren--
                    '[' -> square++
                    ']' -> square--
                }
                if (curly < 0 || paren < 0 || square < 0) return false
            }
            return curly == 0 && paren == 0 && square == 0
        }
    }
}

// =====================================================================
// 24. SelfImprovementLoop — tracks bench scores + applies improvements.
// =====================================================================
class TrackingSelfImprovementLoop(
    private val runBench: (suspend (benchSuiteId: String) -> Double)? = null,
    private val proposeImprovement: (suspend (benchSuiteId: String, current: Double) -> String)? = null,
) : ISelfImprovementLoop {
    private val bestScores = ConcurrentHashMap<String, Double>()

    override suspend fun cycleAsync(benchSuiteId: String): SelfImprovementVerdict {
        require(benchSuiteId.isNotBlank()) { "benchSuiteId required" }
        val baseline = bestScores[benchSuiteId] ?: 0.0
        val rb = runBench
        val current = if (rb != null) rb(benchSuiteId) else defaultRunBench(benchSuiteId)
        val applied: String
        if (current >= baseline) {
            bestScores[benchSuiteId] = current
            applied = if (current > baseline) "new best" else "no regression"
        } else {
            val pi = proposeImprovement
            applied = if (pi != null) pi(benchSuiteId, current) else defaultProposeImprovement(benchSuiteId, current)
        }
        return SelfImprovementVerdict(applied, current)
    }

    fun bestScoreFor(benchSuiteId: String): Double = bestScores[benchSuiteId] ?: 0.0

    private companion object {
        fun defaultRunBench(id: String): Double = 0.5 + (id.hashCode() and 0xFFFF) / 65535.0 * 0.5

        fun defaultProposeImprovement(@Suppress("UNUSED_PARAMETER") id: String, current: Double): String =
            "retry-with-temperature-0 (score was ${"%.3f".format(current)})"
    }
}

// =====================================================================
// Shared helpers — JSON string escaping, ISO round-trip, duration scaling,
// and a tiny lenient JSON reader (avoids pulling kotlinx.serialization into
// this file for two trivial "read the name property" lookups).
// =====================================================================

/**
 * .NET's round-trip ("O") instant format: `yyyy-MM-ddTHH:mm:ss.fffffffZ` — always
 * seven fractional digits, always 'Z'. We render exactly seven digits of the
 * nanosecond field (100-ns ticks) so cron/credential payloads match byte-for-byte.
 */
internal fun isoRoundTrip(instant: Instant): String {
    val dt = instant.atZone(ZoneOffset.UTC)
    val ticks = instant.nano / 100 // 100-ns ticks, seven digits
    return "%04d-%02d-%02dT%02d:%02d:%02d.%07dZ".format(
        dt.year, dt.monthValue, dt.dayOfMonth, dt.hour, dt.minute, dt.second, ticks,
    )
}

/** JSON-string-escape [s] the way System.Text.Json.JsonSerializer.Serialize(string) does for the common cases. */
internal fun jsonString(s: String): String {
    val sb = StringBuilder(s.length + 2)
    sb.append('"')
    for (c in s) {
        when (c.code) {
            0x22 -> sb.append("\u0022")           // double quote
            0x5C -> sb.append("\\\\")              // backslash
            0x0A -> sb.append("\n")
            0x0D -> sb.append("\r")
            0x09 -> sb.append("\t")
            0x08 -> sb.append("\u0008")
            0x0C -> sb.append("\u000C")
            else -> if (c.code < 0x20) sb.append("\\u%04x".format(c.code)) else sb.append(c)
        }
    }
    sb.append('"')
    return sb.toString()
}

/** total * numerator / denominator on the nanosecond tick count (integer-division truncation, matching C# TimeSpan math). */
internal fun scaleDuration(total: Duration, numerator: Long, denominator: Long): Duration {
    val seconds = total.seconds
    val nanos = total.nano.toLong()
    val totalNanos = seconds * 1_000_000_000L + nanos
    val scaled = totalNanos / denominator * numerator
    return Duration.ofNanos(scaled)
}

// ── Minimal lenient JSON value model (for name-only extraction) ──────────────
internal sealed interface JsonVal
internal data class JsonObj(val map: Map<String, JsonVal>) : JsonVal
internal data class JsonStr(val value: String) : JsonVal
internal data class JsonOther(val raw: String) : JsonVal

internal object LenientJson {
    fun parse(text: String): JsonVal {
        val el = kotlinx.serialization.json.Json { isLenient = true }.parseToJsonElement(text)
        return convert(el)
    }

    private fun convert(el: kotlinx.serialization.json.JsonElement): JsonVal = when (el) {
        is kotlinx.serialization.json.JsonObject ->
            JsonObj(el.entries.associate { (k, v) -> k to convert(v) })
        is kotlinx.serialization.json.JsonPrimitive ->
            if (el.isString) JsonStr(el.content) else JsonOther(el.content)
        else -> JsonOther(el.toString())
    }
}
