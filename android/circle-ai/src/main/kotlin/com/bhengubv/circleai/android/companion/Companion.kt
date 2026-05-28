// Companion.kt
//
// Android/Kotlin port of Circle.AI.Companion portable layer.
//
// Covers:
//   InterfaceKind              — enum of surfaces (Mobile, Wearable, Desktop, Web, IoT, Ambient, Headless)
//   CompanionContext           — snapshot of all context injected into the system prompt
//   CompanionTurn              — a single turn in the session conversation log
//   CompanionProactiveEvent    — metadata emitted on proactive companion contact
//   FaceAffectMapper           — applies facial expression deltas to AffectState
//   FaceCompanionBridge        — observes face metrics and emits proactive events
//   ICompanionSession          — primary contract for a Circle AI Companion session

package com.bhengubv.circleai.android.companion

import com.bhengubv.circleai.android.memory.AffectState
import com.bhengubv.circleai.android.tools.FaceExpressionClassification
import com.bhengubv.circleai.android.tools.FacialMetricMatrix
import kotlinx.coroutines.flow.Flow
import java.time.Instant

// ---------------------------------------------------------------------------
// InterfaceKind
// ---------------------------------------------------------------------------

/**
 * The surface on which the Companion session is running.
 */
enum class InterfaceKind {
    /** Mobile phone or tablet (MAUI). */
    Mobile,
    /** Smartwatch or fitness band with a small display. */
    Wearable,
    /** Desktop or laptop computer (MAUI or WPF). */
    Desktop,
    /** Browser-based experience (Blazor). */
    Web,
    /** Embedded IoT device — voice in, voice out, minimal compute. */
    IoT,
    /** Always-on ambient surface — smart speaker, room display, car. */
    Ambient,
    /** Programmatic / background / testing context (no UI). */
    Headless,
}

// ---------------------------------------------------------------------------
// CompanionContext
// ---------------------------------------------------------------------------

/**
 * Snapshot of all context injected into the Companion's system prompt.
 */
data class CompanionContext(
    val identityId: String,
    val displayName: String,
    val preferredLanguage: String?,
    val interfaceKind: InterfaceKind,
    val personaHints: String,
    val affectSummary: String,
    val recentMemorySnippets: List<String>,
    val activeGoals: List<String>,
    val contextBuiltAt: Instant
)

// ---------------------------------------------------------------------------
// CompanionTurn
// ---------------------------------------------------------------------------

/**
 * A single turn in the Companion conversation log.
 * [role] is "user" or "assistant".
 */
data class CompanionTurn(
    val role: String,
    val content: String,
    val timestamp: Instant
)

// ---------------------------------------------------------------------------
// CompanionProactiveEvent
// ---------------------------------------------------------------------------

/**
 * Metadata emitted when the Companion proactively initiates contact.
 */
data class CompanionProactiveEvent(
    val sessionId: String,
    val identityId: String,
    val interfaceKind: InterfaceKind,
    val message: String,
    val triggerName: String,
    val generatedAt: Instant
)

// ---------------------------------------------------------------------------
// FaceAffectMapper
// ---------------------------------------------------------------------------

/**
 * Applies facial expression deltas to an [AffectState].
 *
 * CRITICAL: The delta constants below must match the reference implementation
 * exactly — they are cross-validated against fixtures/facex_biometric_vectors.json.
 * Confidence scores below [MIN_CONFIDENCE] are discarded without mutation.
 */
object FaceAffectMapper {

    private const val MIN_CONFIDENCE = 0.5f

    /**
     * Applies expression-driven affect deltas to [affect].
     * No-op if [matrix.confidenceScore] < [MIN_CONFIDENCE].
     *
     * Deltas (all values coerced to [0f, 1f] after application):
     *   Happy     → engagement +0.03, energy +0.02
     *   Surprised → curiosity  +0.04
     *   Confused  → uncertainty +0.05
     *   Stressed  → uncertainty +0.08, energy -0.05
     *   Angry     → engagement -0.04, rapport -0.02
     *   All other expressions (Neutral, Sad, Unknown) → no change
     */
    fun apply(matrix: FacialMetricMatrix, affect: AffectState) {
        if (matrix.confidenceScore < MIN_CONFIDENCE) return

        when (matrix.expression) {
            FaceExpressionClassification.Happy -> {
                affect.engagement = (affect.engagement + 0.03f).coerceIn(0f, 1f)
                affect.energy     = (affect.energy     + 0.02f).coerceIn(0f, 1f)
            }
            FaceExpressionClassification.Surprised ->
                affect.curiosity = (affect.curiosity + 0.04f).coerceIn(0f, 1f)
            FaceExpressionClassification.Confused ->
                affect.uncertainty = (affect.uncertainty + 0.05f).coerceIn(0f, 1f)
            FaceExpressionClassification.Stressed -> {
                affect.uncertainty = (affect.uncertainty + 0.08f).coerceIn(0f, 1f)
                affect.energy      = (affect.energy      - 0.05f).coerceIn(0f, 1f)
            }
            FaceExpressionClassification.Angry -> {
                affect.engagement = (affect.engagement - 0.04f).coerceIn(0f, 1f)
                affect.rapport    = (affect.rapport    - 0.02f).coerceIn(0f, 1f)
            }
            else -> return
        }

        affect.lastUpdatedAt = Instant.now()
    }
}

// ---------------------------------------------------------------------------
// FaceCompanionBridge
// ---------------------------------------------------------------------------

/**
 * Observes a stream of [FacialMetricMatrix] frames, applies them to
 * [AffectState] via [FaceAffectMapper], and emits a [CompanionProactiveEvent]
 * when the user appears confused or stressed beyond [CONFUSION_THRESHOLD].
 */
object FaceCompanionBridge {

    /**
     * Uncertainty threshold above which a confusion/stress event is emitted.
     * Applied after the face-driven affect delta has been applied.
     */
    const val CONFUSION_THRESHOLD = 0.70f

    /**
     * Observes one [FacialMetricMatrix] frame. Applies affect deltas then
     * returns a [CompanionProactiveEvent] if the confusion threshold has been
     * crossed, or null otherwise.
     *
     * @param matrix     The facial metric data for this frame.
     * @param affect     The mutable affect state to update in-place.
     * @param sessionId  Companion session identifier for the event payload.
     * @param identityId Identity that owns this session.
     * @param surface    The interface kind of the running session.
     */
    fun observe(
        matrix: FacialMetricMatrix,
        affect: AffectState,
        sessionId: String,
        identityId: String,
        surface: InterfaceKind
    ): CompanionProactiveEvent? {
        FaceAffectMapper.apply(matrix, affect)

        val isConfusionExpression = matrix.expression == FaceExpressionClassification.Confused ||
            matrix.expression == FaceExpressionClassification.Stressed

        val crossed = affect.uncertainty >= CONFUSION_THRESHOLD && isConfusionExpression

        if (!crossed) return null

        return CompanionProactiveEvent(
            sessionId    = sessionId,
            identityId   = identityId,
            interfaceKind = surface,
            message      = "I notice you might be finding this a bit tricky. " +
                           "Would you like me to slow down or explain it differently?",
            triggerName  = "face.confusion_detected",
            generatedAt  = Instant.now()
        )
    }
}

// ---------------------------------------------------------------------------
// ICompanionSession
// ---------------------------------------------------------------------------

/**
 * A Companion conversation session. Combines identity awareness, cross-device
 * memory, language adaptation, affect sensing, and proactive reasoning into a
 * single coherent interface.
 */
interface ICompanionSession : AutoCloseable {

    // ── Identity ─────────────────────────────────────────────────────────────

    /** Stable unique identifier for this session. */
    val sessionId: String

    /** The authenticated identity driving this session. */
    val identityId: String

    /** The surface on which this session is running. */
    val interfaceKind: InterfaceKind

    // ── Core conversation ─────────────────────────────────────────────────────

    /**
     * Send a message to the Companion and receive a complete reply.
     */
    suspend fun sendAsync(message: String): String

    /**
     * Stream the Companion's reply token-by-token for low-latency rendering.
     */
    fun streamAsync(message: String): Flow<String>

    /**
     * Agentic mode: sends the instruction, detects tool calls in the reply,
     * executes them, and re-prompts until the model produces a plain-text answer.
     */
    suspend fun agentAsync(instruction: String): String

    // ── Context ──────────────────────────────────────────────────────────────

    /** Returns the most recent [CompanionContext] snapshot. */
    fun getContext(): CompanionContext

    /** Refreshes the context from backing stores. */
    suspend fun refreshContextAsync()

    // ── History ───────────────────────────────────────────────────────────────

    /** The in-session conversation history (this session only, not persisted). */
    val history: List<CompanionTurn>

    // ── Feedback ──────────────────────────────────────────────────────────────

    /** Signal satisfaction with the last reply. */
    suspend fun signalFeedbackAsync(positive: Boolean, note: String? = null)

    // ── Proactive ─────────────────────────────────────────────────────────────

    /**
     * Raised when the Companion initiates contact without being prompted.
     */
    val proactiveEvents: Flow<CompanionProactiveEvent>
}
