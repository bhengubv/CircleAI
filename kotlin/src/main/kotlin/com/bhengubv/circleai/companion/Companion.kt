// Companion.kt
//
// Kotlin port of Circle.AI.Companion portable layer.
//
// Covers:
//   InterfaceKind              — enum of surfaces (Mobile, Wearable, Desktop, Web, IoT, Ambient, Headless)
//   CompanionContext           — snapshot of all context injected into the system prompt
//   CompanionTurn              — a single turn in the session conversation log
//   CompanionProactiveEvent    — metadata emitted on proactive companion contact
//   ICompanionSession          — primary contract for a Circle AI Companion session

package com.bhengubv.circleai.companion

import kotlinx.coroutines.flow.Flow
import java.time.Instant

// ---------------------------------------------------------------------------
// InterfaceKind
// ---------------------------------------------------------------------------

/**
 * The surface on which the Companion session is running.
 * Determines sensory capabilities, available UI affordances, and how the
 * Companion adapts its communication style.
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
 * Rebuilt at the start of each session and refreshed on request.
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
 * A single turn in the Companion conversation log, held in memory for the
 * duration of the session.
 * [role] is "user" or "assistant".
 */
data class CompanionTurn(
    /** "user" | "assistant" */
    val role: String,
    val content: String,
    val timestamp: Instant
)

// ---------------------------------------------------------------------------
// CompanionProactiveEvent
// ---------------------------------------------------------------------------

/**
 * Metadata emitted when the Companion proactively initiates contact.
 * Mirrors ProactiveMessageEventArgs in the hosting layer but enriched with
 * Companion session info.
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
// ICompanionSession
// ---------------------------------------------------------------------------

/**
 * A Companion conversation session. Combines identity awareness, cross-device
 * memory, language adaptation, affect sensing, and proactive reasoning into a
 * single coherent interface.
 *
 * Implementations should be [AutoCloseable] / released via a try-with-resources
 * equivalent when no longer needed.
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
     * Context enrichment (identity, memory, persona, affect, language) is
     * applied automatically.
     */
    suspend fun sendAsync(message: String): String

    /**
     * Stream the Companion's reply token-by-token for low-latency rendering.
     * Each emitted [String] is the next chunk to append to the output.
     */
    fun streamAsync(message: String): Flow<String>

    /**
     * Agentic mode: sends the instruction, detects tool calls in the reply,
     * executes them, and re-prompts until the model produces a plain-text answer.
     * Enables "do things, not just say things."
     */
    suspend fun agentAsync(instruction: String): String

    // ── Context ──────────────────────────────────────────────────────────────

    /**
     * Returns the most recent [CompanionContext] snapshot, including identity,
     * persona hints, affect summary, and recent memories.
     */
    fun getContext(): CompanionContext

    /**
     * Refreshes the context from backing stores (memory, persona, affect).
     * Call after significant state changes (e.g. new goal set, mood shift).
     */
    suspend fun refreshContextAsync()

    // ── History ───────────────────────────────────────────────────────────────

    /** The in-session conversation history (this session only, not persisted). */
    val history: List<CompanionTurn>

    // ── Feedback ──────────────────────────────────────────────────────────────

    /**
     * Signal satisfaction with the last reply. Used to evolve the persona and
     * communication style over time.
     */
    suspend fun signalFeedbackAsync(positive: Boolean, note: String? = null)

    // ── Proactive ─────────────────────────────────────────────────────────────

    /**
     * Raised when the Companion initiates contact without being prompted —
     * e.g. a goal check-in, a mood-triggered nudge, or a scheduled reminder.
     * Callers should observe this flow for proactive messages.
     */
    val proactiveEvents: Flow<CompanionProactiveEvent>
}
