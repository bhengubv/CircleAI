// Accessibility.kt
//
// Kotlin port of CircleAI.Accessibility (AccessibilityPrimitives.cs +
// AccessibilityDomainContext.cs + AccessibilityCompanionAdapter.cs) — the C#
// reference is the EXACT spec. A deterministic in-memory accessibility board:
// per-user profiles and derived adaptation hints.
//
// Fidelity notes:
//   * C# `enum AccessibilityNeed` -> Kotlin `enum class`.
//   * C# `record` -> Kotlin `data class`.
//   * `HintsFor` emits hints in fixed order: contrast, motion, aria, text-scale
//     (only when > 1, formatted to 2 dp), then one "need" hint per need. Empty
//     when the user has no profile.
//   * The two C# `AuditWcagAsync` overloads (1-arg vs 2-arg-with-target) are
//     preserved as distinct-arity methods so both prompt bodies survive; the
//     2-arg form intentionally has no default value to keep call resolution
//     identical to C#.

package com.bhengubv.circleai.accessibility

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (AccessibilityPrimitives.cs)
// =====================================================================

/** An accessibility need. Mirrors C# `AccessibilityNeed`. */
enum class AccessibilityNeed { Visual, Hearing, Motor, Cognitive, Speech }

/** A user accessibility profile. Mirrors C# `UserAccessibilityProfile`. */
data class UserAccessibilityProfile(
    val userId: String,
    val needs: List<AccessibilityNeed>,
    val textScale: Double,
    val highContrast: Boolean,
    val reducedMotion: Boolean,
    val screenReader: Boolean,
)

/** A derived adaptation hint. Mirrors C# `AdaptationHint`. */
data class AdaptationHint(val kind: String, val value: String)

/** Deterministic accessibility board. Mirrors C# `IAccessibilityBoard`. */
interface IAccessibilityBoard {
    fun setProfile(p: UserAccessibilityProfile)
    fun getProfile(userId: String): UserAccessibilityProfile?
    fun hintsFor(userId: String): List<AdaptationHint>
}

/** In-memory [IAccessibilityBoard]. Mirrors C# `InMemoryAccessibilityBoard`. */
class InMemoryAccessibilityBoard : IAccessibilityBoard {
    private val profiles = ConcurrentHashMap<String, UserAccessibilityProfile>()

    override fun setProfile(p: UserAccessibilityProfile) { profiles[p.userId] = p }
    override fun getProfile(userId: String): UserAccessibilityProfile? = profiles[userId]

    override fun hintsFor(userId: String): List<AdaptationHint> {
        val p = profiles[userId] ?: return emptyList()
        val hints = mutableListOf<AdaptationHint>()
        if (p.highContrast) hints.add(AdaptationHint("contrast", "high"))
        if (p.reducedMotion) hints.add(AdaptationHint("motion", "reduced"))
        if (p.screenReader) hints.add(AdaptationHint("aria", "verbose"))
        if (p.textScale > 1) hints.add(AdaptationHint("text-scale", String.format(Locale.US, "%.2f", p.textScale)))
        for (n in p.needs) hints.add(AdaptationHint("need", n.name))
        return hints
    }
}

// =====================================================================
// DomainContext (AccessibilityDomainContext.cs)
// =====================================================================

/** Static domain context for Accessibility. Mirrors C# `AccessibilityDomainContext`. */
object AccessibilityDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Accessibility] Expert accessibility and inclusive design assistant. Help with WCAG 2.2 " +
            "compliance audits, screen reader compatibility, alternative text guidance, disability " +
            "accommodation requests, and assistive technology selection. Always centre the lived experience " +
            "of disabled users. Compliance: WCAG 2.2, UNCRPD, SA Promotion of Equality Act, POPIA."

    val complianceFlags: List<String> = listOf("WCAG_2_2", "UNCRPD", "Equality_Act", "POPIA")

    val suggestedTools: List<String> = listOf("screen_reader_test", "document_editor", "web_audit", "analytics")
}

// =====================================================================
// CompanionAdapter (AccessibilityCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Accessibility snippet + helpers. Mirrors C# `AccessibilityCompanionAdapter`. */
class AccessibilityCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${AccessibilityDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun auditWcagAsync(htmlOrDescription: String): String =
        inner.agentAsync("Audit this interface for WCAG 2.2 AA compliance. Identify violations, their impact on disabled users, and remediation steps:\n$htmlOrDescription")

    suspend fun writeAltTextAsync(imageDescription: String, context: String): String =
        inner.agentAsync("Write descriptive alt text for an image. Image: $imageDescription. Context: $context. Follow WCAG 2.2 alt text best practices.")

    suspend fun auditWcagAsync(content: String, targetLevel: String): String =
        inner.agentAsync("Audit this content/UI for WCAG 2.2 $targetLevel compliance: $content. List violations by criterion id, severity, and a concrete fix.")

    suspend fun describeImageForScreenReaderAsync(imageContext: String): String =
        inner.agentAsync("Write a screen-reader alt-text for the image. Context: $imageContext. Aim for 1-2 sentences, no 'image of', present tense.")

    suspend fun simplifyLanguageAsync(text: String, readingAge: String = "plain English"): String =
        inner.agentAsync("Rewrite this for $readingAge: $text. Keep the meaning, drop jargon, use short sentences.")

    suspend fun suggestKeyboardShortcutAsync(action: String, platform: String): String =
        inner.agentAsync("Suggest an accessible keyboard shortcut for '$action' on $platform. Avoid chords that conflict with screen-reader defaults.")
}
