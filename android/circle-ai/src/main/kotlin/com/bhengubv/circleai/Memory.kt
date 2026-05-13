package com.bhengubv.circleai

import java.time.Instant

data class AffectState(
    var curiosity: Float = 0.5f,
    var engagement: Float = 0.5f,
    var uncertainty: Float = 0.2f,
    var rapport: Float = 0.0f,
    var energy: Float = 0.5f,
    var lastUpdatedAt: Instant = Instant.now()
) {
    fun applyPositiveSignal() {
        engagement  = (engagement  + 0.02f).coerceIn(0f, 1f)
        rapport     = (rapport     + 0.01f).coerceIn(0f, 1f)
        uncertainty = (uncertainty - 0.02f).coerceIn(0f, 1f)
        lastUpdatedAt = Instant.now()
    }

    fun applyNegativeSignal() {
        engagement  = (engagement  - 0.03f).coerceIn(0f, 1f)
        uncertainty = (uncertainty + 0.03f).coerceIn(0f, 1f)
        lastUpdatedAt = Instant.now()
    }

    fun applyIdleDecay(idleHours: Float) {
        val decay = minOf(0.3f, idleHours * 0.02f)
        engagement = lerp(engagement, 0.5f, decay)
        energy     = lerp(energy, 0.5f, decay)
        lastUpdatedAt = Instant.now()
    }

    companion object {
        private fun lerp(a: Float, b: Float, t: Float): Float {
            val tc = t.coerceIn(0f, 1f)
            return a + (b - a) * tc
        }
    }
}

enum class FeedbackSignal { POSITIVE, NEGATIVE, NEUTRAL }
enum class VerbosityPreference { BRIEF, BALANCED, DETAILED }
enum class FormalityPreference { CASUAL, NEUTRAL, FORMAL }

data class PersonaState(
    val verbosity: VerbosityPreference = VerbosityPreference.BALANCED,
    val formality: FormalityPreference = FormalityPreference.NEUTRAL,
    val preferredLocale: String? = null
)

data class EpisodicMemoryEntry(
    val id: String,
    val sessionId: String,
    val createdAt: Instant,
    val speakerRole: String,
    val contentSummary: String,
    val embedding: FloatArray? = null,
    val importance: Int = 50
)

enum class GoalStatus { ACTIVE, COMPLETED, ABANDONED }

data class Goal(
    val id: String,
    val description: String,
    var status: GoalStatus = GoalStatus.ACTIVE,
    val createdAt: Instant = Instant.now(),
    var resolvedAt: Instant? = null
)

// Store interfaces
interface IAffectStore {
    suspend fun getAffectState(sessionId: String): AffectState?
    suspend fun saveAffectState(sessionId: String, state: AffectState)
}

interface IEpisodicMemoryStore {
    suspend fun saveEntry(entry: EpisodicMemoryEntry)
    suspend fun getEntries(sessionId: String, limit: Int = 50): List<EpisodicMemoryEntry>
    suspend fun searchSimilar(embedding: FloatArray, limit: Int = 10): List<EpisodicMemoryEntry>
}

interface IFeedbackStore {
    suspend fun recordFeedback(sessionId: String, signal: FeedbackSignal)
}

interface IPersonaStore {
    suspend fun getPersonaState(identityId: String): PersonaState?
    suspend fun savePersonaState(identityId: String, state: PersonaState)
}

interface IGoalStore {
    suspend fun saveGoal(goal: Goal)
    suspend fun getActiveGoals(identityId: String): List<Goal>
    suspend fun updateGoalStatus(goalId: String, status: GoalStatus)
}
