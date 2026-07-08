// HerJarvisContracts.kt
//
// Kotlin port of CircleAI.Companion.HerJarvis contracts — the C# reference
// (HerJarvisContracts.cs) is the EXACT spec. These are the HER/Jarvis-level
// companion contracts plus their supporting records.
//
// FOUR of the 24 contracts already live under com.bhengubv.circleai.companion.reasoning
// and are NOT duplicated here (they are re-used by name):
//   5.  IWorldModel / CausalPrediction        -> reasoning.WorldModel
//   10. ITheoryOfMind / OtherMindEstimate      -> reasoning.TheoryOfMind
//   13. IInnerMonologue / SelfReflection       -> reasoning.InnerMonologue
//   14. IPredictiveEngine / AnticipatedNeed    -> reasoning.PredictiveEngine
//
// The remaining twenty contracts are ported here. C# `record` -> Kotlin
// `data class`; C# `IAsyncEnumerable<T>` -> `kotlinx.coroutines.flow.Flow<T>`;
// C# `ValueTask<T>` -> `suspend fun`. IReadOnlyDictionary/IReadOnlyList map to
// Map/List. Real, working implementations live in HerJarvisImplementations.kt.

package com.bhengubv.circleai.companion.herjarvis

import kotlinx.coroutines.flow.Flow
import java.time.Duration
import java.time.Instant

// =====================================================================
// 1. Always-on background presence across all devices.
// =====================================================================

/** Always-on background presence: a start/stop lifecycle with a running flag. */
interface IAlwaysOnPresence {
    val isRunning: Boolean
    suspend fun startAsync()
    suspend fun stopAsync()
}

// =====================================================================
// 2. Fused perceptual stream.
// =====================================================================

/**
 * A single fused perception frame at [at], with optional [vision]/[audio]/[text]
 * summaries and a bag of named scalar [sensors]. Mirrors C# `FusedPercept`.
 */
data class FusedPercept(
    val at: Instant,
    val vision: String?,
    val audio: String?,
    val text: String?,
    val sensors: Map<String, Double>,
)

/** Fused perceptual stream: a cold [Flow] of [FusedPercept] frames. */
interface IFusedPerception {
    fun streamAsync(): Flow<FusedPercept>
}

// =====================================================================
// 3. Memory + identity sync across devices.
// =====================================================================

/** Memory + identity sync across devices via an append-only delta log. */
interface IIdentitySync {
    suspend fun pushAsync(deltaJson: String)
    suspend fun pullAsync(sinceCursor: String): String
}

// =====================================================================
// 4. Continuous online learning.
// =====================================================================

/** Continuous online learning: register a reward signal for an interaction. */
interface IContinuousLearner {
    suspend fun registerFeedbackAsync(interactionId: String, reward: Double, contextJson: String)
}

// =====================================================================
// 6. Multi-month goal pursuit with replanning.
// =====================================================================

/**
 * A long-horizon goal with a serialised [planJson] milestone plan and a
 * [progressFraction] in [0,1]. Mirrors C# `LongHorizonGoal`.
 */
data class LongHorizonGoal(
    val id: String,
    val description: String,
    val deadlineUtc: Instant,
    val planJson: String,
    val progressFraction: Double,
)

/** Multi-month goal pursuit: register, look up, and replan long-horizon goals. */
interface IGoalPursuer {
    suspend fun registerAsync(description: String, deadlineUtc: Instant): LongHorizonGoal
    suspend fun currentAsync(id: String): LongHorizonGoal?
    suspend fun replanAsync(id: String)
}

// =====================================================================
// 7. Episodic memory of lived experiences.
// =====================================================================

/** One recorded episode. Mirrors C# `EpisodeRecord`. */
data class EpisodeRecord(
    val id: String,
    val at: Instant,
    val title: String,
    val contentJson: String,
)

/** Episodic memory of lived experiences: record episodes and recall by query. */
interface IEpisodicMemory {
    suspend fun recordAsync(episode: EpisodeRecord)
    suspend fun recallAsync(query: String, take: Int = 10): List<EpisodeRecord>
}

// =====================================================================
// 8. Per-user voice continuity.
// =====================================================================

/** Per-user voice continuity: enroll a speaker and identify future audio. */
interface IVoiceIdentity {
    /** Returns a stable voice fingerprint id (or null if unknown). */
    suspend fun identifyAsync(audioPcm16: ByteArray, sampleRateHz: Int): String?
    suspend fun enrollAsync(userId: String, audioPcm16: ByteArray, sampleRateHz: Int)
}

// =====================================================================
// 9. Calibrated uncertainty at orchestration.
// =====================================================================

/** A calibrated confidence interval [lower, upper]. Mirrors C# `ConfidenceBand`. */
data class ConfidenceBand(val lower: Double, val upper: Double)

/** Calibrated uncertainty: evaluate a confidence band for an answer. */
interface ICalibratedConfidence {
    suspend fun evaluateAsync(answer: String, contextJson: String): ConfidenceBand
}

// =====================================================================
// 11. Emotion sensing.
// =====================================================================

/** A sensed emotion frame: [label] with [arousal] and [valence]. Mirrors C# `EmotionFrame`. */
data class EmotionFrame(val label: String, val arousal: Double, val valence: Double)

/** Emotion sensing: infer an emotion frame from a fused-signal JSON payload. */
interface IEmotionSensor {
    suspend fun senseAsync(fusedJson: String): EmotionFrame
}

// =====================================================================
// 12. Skill acquisition.
// =====================================================================

/** A skill acquired from a demonstration. Mirrors C# `AcquiredSkill`. */
data class AcquiredSkill(val id: String, val name: String, val descriptionJson: String)

/** Skill acquisition: learn a skill from a demonstration and list what is known. */
interface ISkillAcquisition {
    suspend fun acquireAsync(demonstrationJson: String): AcquiredSkill
    suspend fun listAsync(): List<AcquiredSkill>
}

// =====================================================================
// 15. Personal knowledge graph.
// =====================================================================

/** A node in the personal knowledge graph. Mirrors C# `KnowledgeNode`. */
data class KnowledgeNode(
    val id: String,
    val kind: String,
    val name: String,
    val properties: Map<String, String>,
)

/** A directed, typed relation between two nodes. Mirrors C# `KnowledgeRelation`. */
data class KnowledgeRelation(val fromId: String, val toId: String, val relation: String)

/** Personal knowledge graph: upsert nodes/relations and traverse out-neighbours. */
interface IPersonalKnowledgeGraph {
    suspend fun upsertNodeAsync(node: KnowledgeNode)
    suspend fun upsertRelationAsync(rel: KnowledgeRelation)
    suspend fun neighboursAsync(id: String): List<KnowledgeNode>
}

// =====================================================================
// 16. Live world-knowledge stream.
// =====================================================================

/** A live world fact on a [topic] at [at]. Mirrors C# `WorldFact`. */
data class WorldFact(val topic: String, val summaryJson: String, val at: Instant)

/** Live world-knowledge stream: subscribe to facts on a set of topics. */
interface ILiveWorldKnowledge {
    fun subscribeAsync(topics: List<String>): Flow<WorldFact>
}

// =====================================================================
// 17. Bio-signal integration.
// =====================================================================

/** A single bio-signal reading. Mirrors C# `BioSignal`. */
data class BioSignal(val kind: String, val value: Double, val at: Instant)

/** Bio-signal integration: a cold [Flow] of bio-signal readings. */
interface IBioSignalStream {
    fun streamAsync(): Flow<BioSignal>
}

// =====================================================================
// 18. Robotics / physical actuation.
// =====================================================================

/** A physical command to a device. Mirrors C# `PhysicalCommand`. */
data class PhysicalCommand(val deviceId: String, val action: String, val args: Map<String, String>)

/** The outcome of a physical command. Mirrors C# `PhysicalCommandResult`. */
data class PhysicalCommandResult(val succeeded: Boolean, val error: String?)

/** Robotics / physical actuation: dispatch a command to a registered device. */
interface IPhysicalActuator {
    suspend fun invokeAsync(command: PhysicalCommand): PhysicalCommandResult
}

// =====================================================================
// 19. Agent-to-agent peer protocol.
// =====================================================================

/** One agent-to-agent message. Mirrors C# `AgentToAgentMessage`. */
data class AgentToAgentMessage(
    val fromAgentId: String,
    val toAgentId: String,
    val payload: String,
    val at: Instant,
)

/** Agent-to-agent peer protocol: send to a mailbox and receive an agent's inbox. */
interface IAgentPeerNetwork {
    suspend fun sendAsync(message: AgentToAgentMessage)
    fun receiveAsync(forAgentId: String): Flow<AgentToAgentMessage>
}

// =====================================================================
// 20. Federated / on-device fine-tune pipeline.
// =====================================================================

/** Status of a fine-tune job: [progress] in [0,1], non-null [error] on failure. */
data class FineTuneJobStatus(val jobId: String, val progress: Double, val error: String?)

/** Federated / on-device fine-tune pipeline: start a job and poll its status. */
interface IFederatedFineTuner {
    suspend fun startAsync(baseModel: String, trainingDataPath: String): String
    suspend fun statusAsync(jobId: String): FineTuneJobStatus
}

// =====================================================================
// 21. Sub-100ms first-token latency on cheap phones.
// =====================================================================

/** First-token latency budget: [targetMs] goal vs observed [currentP50Ms]. */
data class FirstTokenBudget(val targetMs: Int, val currentP50Ms: Int)

/** First-token optimiser: report the current first-token latency budget. */
interface IFirstTokenOptimizer {
    suspend fun currentAsync(): FirstTokenBudget
}

// =====================================================================
// 22. Cryptographic delegation framework.
// =====================================================================

/** A signed delegation credential. Mirrors C# `DelegationCredential`. */
data class DelegationCredential(
    val issuer: String,
    val subjectId: String,
    val scope: String,
    val expiresAtUtc: Instant,
    val signature: String,
)

/** Cryptographic delegation: issue and verify signed delegation credentials. */
interface ICryptoDelegation {
    fun issue(subjectId: String, scope: String, lifetime: Duration): DelegationCredential
    fun verify(credential: DelegationCredential): Boolean
}

// =====================================================================
// 23. Live code generation + test + deploy loop.
// =====================================================================

/** One code-generation job outcome. Mirrors C# `CodeGenJob`. */
data class CodeGenJob(
    val id: String,
    val prompt: String,
    val outputSnippet: String,
    val testsPass: Boolean,
    val deployHint: String?,
)

/** Live code generation + test + deploy loop: run a prompt end-to-end. */
interface ICodeGenerationLoop {
    suspend fun runAsync(prompt: String): CodeGenJob
}

// =====================================================================
// 24. Self-debugging / self-improvement loop.
// =====================================================================

/** The verdict of one self-improvement cycle. Mirrors C# `SelfImprovementVerdict`. */
data class SelfImprovementVerdict(val improvementsApplied: String, val newBenchScore: Double)

/** Self-debugging / self-improvement loop: run one improvement cycle over a bench suite. */
interface ISelfImprovementLoop {
    suspend fun cycleAsync(benchSuiteId: String): SelfImprovementVerdict
}
