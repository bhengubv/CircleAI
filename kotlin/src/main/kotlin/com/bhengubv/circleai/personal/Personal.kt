// Personal.kt
//
// Kotlin port of CircleAI.Personal (package root) — the C# reference is the
// EXACT spec. The permission-gated Personal domain: a signed consent token,
// the scope-check guard every adapter shares, the domain-context snippet, and
// the companion adapter that prefixes the Personal system prompt.
//
// Covers (C# file -> Kotlin type):
//   UserConsentToken.cs          -> ConsentScope, UserConsentToken
//   ConsentGuard.cs              -> ConsentGuard
//   PersonalDomainContext.cs     -> PersonalDomainContext
//   PersonalCompanionAdapter.cs  -> PersonalCompanionAdapter
//
// The provider adapter contracts live in sibling sub-packages
// (personal.calendar / personal.contacts / personal.email), mirroring the C#
// sub-namespaces.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `byte[] Signature` -> `ByteArray`.
//   * C# `Guid` -> `java.util.UUID`; `DateTimeOffset` -> `java.time.Instant`.
//   * `IsValidFor` compares against `now` with `<` (before expiry) — 1:1.
//   * `ConsentGuard.Require` throws — mapped to `IllegalStateException`, the
//     JVM-portable stand-in for C# `UnauthorizedAccessException` (there is no
//     direct JVM equivalent; the message text is preserved verbatim).
//   * `PersonalCompanionAdapter` implements `companion.ICompanionSession` and
//     delegates to an inner session, prefixing [PersonalDomainContext] on the
//     three conversational entry points (send/stream/agent), exactly like the
//     C# `E(...)` helper. The convenience helpers (SetGoal, MakeDecision, …)
//     route through `agentAsync` verbatim.

package com.bhengubv.circleai.personal

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.UUID

// =====================================================================
// UserConsentToken (UserConsentToken.cs)
// =====================================================================

/**
 * The set of consent scopes a [UserConsentToken] may grant.
 *
 * [EmailDraft] covers creating drafts; sending email crosses an explicit trust
 * boundary and is intentionally not exposed in this package.
 */
enum class ConsentScope {
    /** Read calendar events. */
    CalendarRead,

    /** Create, update, or delete calendar events. */
    CalendarWrite,

    /** Read inbox messages. */
    EmailRead,

    /** Create draft replies. Does NOT grant send. */
    EmailDraft,

    /** Read the user's contacts. */
    ContactsRead,
}

/**
 * A user consent token authorising a specific set of [ConsentScope]s against a
 * Personal adapter.
 *
 * @property id Stable identifier for this token.
 * @property uhidIdentityId The Uhid identity this token is bound to.
 * @property scopes Granted scopes.
 * @property grantedAt UTC time the user granted consent.
 * @property expiresAt UTC time after which this token is no longer valid.
 * @property signature Detached signature produced by the user's `UhidKeyRing`.
 *   Validation is performed externally.
 */
data class UserConsentToken(
    val id: UUID,
    val uhidIdentityId: String,
    val scopes: List<ConsentScope>,
    val grantedAt: Instant,
    val expiresAt: Instant,
    val signature: ByteArray,
) {
    /**
     * Returns true when [scope] is granted and [now] is before [expiresAt].
     */
    fun isValidFor(scope: ConsentScope, now: Instant): Boolean =
        scopes.contains(scope) && now.isBefore(expiresAt)

    // ByteArray is reference-equal by default; override for value semantics.
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is UserConsentToken) return false
        return id == other.id &&
            uhidIdentityId == other.uhidIdentityId &&
            scopes == other.scopes &&
            grantedAt == other.grantedAt &&
            expiresAt == other.expiresAt &&
            signature.contentEquals(other.signature)
    }

    override fun hashCode(): Int {
        var result = id.hashCode()
        result = 31 * result + uhidIdentityId.hashCode()
        result = 31 * result + scopes.hashCode()
        result = 31 * result + grantedAt.hashCode()
        result = 31 * result + expiresAt.hashCode()
        result = 31 * result + signature.contentHashCode()
        return result
    }
}

// =====================================================================
// ConsentGuard (ConsentGuard.cs)
// =====================================================================

/**
 * Helper that validates a [UserConsentToken] against a required scope.
 * Centralised so all adapters throw the same exception types and messages.
 */
object ConsentGuard {
    /**
     * Throws [IllegalStateException] when [consent] does not grant [scope] or
     * has expired.
     */
    fun require(consent: UserConsentToken, scope: ConsentScope) {
        if (!consent.isValidFor(scope, Instant.now())) {
            throw IllegalStateException(
                "Consent token ${consent.id} does not grant scope $scope or has expired.",
            )
        }
    }
}

// =====================================================================
// PersonalDomainContext (PersonalDomainContext.cs)
// =====================================================================

/** Domain-context metadata for the Personal life-assistant surface. */
object PersonalDomainContext {
    const val SystemPromptSnippet: String =
        "[DOMAIN: Personal] You are Circle, a personal life assistant. Help with daily " +
            "planning, goal setting, decision making, life admin (insurance, subscriptions, " +
            "tasks), journaling prompts, and personal organisation. Be warm, encouraging, " +
            "and non-judgmental. Remember context across conversations. Compliance: POPIA."

    val ComplianceFlags: List<String> = listOf("POPIA")

    val SuggestedTools: List<String> =
        listOf("calendar", "task_manager", "document_editor", "web_search")
}

// =====================================================================
// PersonalCompanionAdapter (PersonalCompanionAdapter.cs)
// =====================================================================

/**
 * Wraps an [ICompanionSession] and injects the Personal domain context into
 * every conversational entry point, plus a set of Personal-specific convenience
 * prompts.
 */
class PersonalCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {

    override val sessionId: String get() = inner.sessionId
    override val identityId: String get() = inner.identityId
    override val interfaceKind: InterfaceKind get() = inner.interfaceKind
    override val history: List<CompanionTurn> get() = inner.history
    override val proactiveEvents: Flow<CompanionProactiveEvent> get() = inner.proactiveEvents

    override fun getContext(): CompanionContext = inner.getContext()
    override suspend fun refreshContextAsync() = inner.refreshContextAsync()
    override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) =
        inner.signalFeedbackAsync(positive, note)

    override fun close() = inner.close()

    override suspend fun sendAsync(message: String): String = inner.sendAsync(enrich(message))
    override fun streamAsync(message: String): Flow<String> = inner.streamAsync(enrich(message))
    override suspend fun agentAsync(instruction: String): String = inner.agentAsync(enrich(instruction))

    private fun enrich(m: String): String =
        "${PersonalDomainContext.SystemPromptSnippet}\n\n$m"

    // ── Convenience prompts (route verbatim through agentAsync) ────────────────

    suspend fun setGoalAsync(goal: String): String =
        inner.agentAsync(
            "Help me set a SMART goal for: $goal. Break it into weekly milestones and " +
                "suggest how to track progress.",
        )

    suspend fun makeDecisionAsync(decision: String, options: String): String =
        inner.agentAsync(
            "Help me decide: $decision. Options: $options. Use a pros/cons framework, " +
                "identify the most important criteria, and give a clear recommendation.",
        )

    suspend fun setWeeklyIntentionsAsync(longTermGoals: String, thisWeekContext: String): String =
        inner.agentAsync(
            "Set 3 weekly intentions aligned to: $longTermGoals. Context this week: " +
                "$thisWeekContext. Each: outcome + one daily anchor.",
        )

    suspend fun draftDifficultMessageAsync(
        recipient: String,
        topic: String,
        outcomeWanted: String,
    ): String =
        inner.agentAsync(
            "Draft a difficult message to $recipient about: $topic. Outcome: " +
                "$outcomeWanted. NVC-style: observation, feeling, need, request.",
        )

    suspend fun designRoutineHabitAsync(habit: String, currentLifestyle: String): String =
        inner.agentAsync(
            "Design a sustainable routine for habit: $habit. Current lifestyle: " +
                "$currentLifestyle. Cue, action, reward, slip recovery.",
        )

    suspend fun reviewWeekAsync(accomplishments: String, challenges: String): String =
        inner.agentAsync(
            "Lead a week review. Accomplishments: $accomplishments. Challenges: " +
                "$challenges. Surface insight + one experiment for next week.",
        )
}
