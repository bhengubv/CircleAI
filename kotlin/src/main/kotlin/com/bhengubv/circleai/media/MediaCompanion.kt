// MediaCompanion.kt
//
// The Media domain context and the Companion adapter that carries it.

package com.bhengubv.circleai.media

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow

/** Static domain context for Media. */
object MediaDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Media] Expert media and content production assistant. Help with editorial " +
            "calendars, content briefs, video production schedules, audience analytics " +
            "interpretation, social media strategy, and IP rights management. Apply data-driven " +
            "creative strategy. Compliance: ICASA, BCCSA, Copyright Act 98/1978, POPIA."

    val complianceFlags: List<String> =
        listOf("ICASA", "BCCSA", "Copyright_Act_98_1978", "POPIA")

    val suggestedTools: List<String> =
        listOf("content_planner", "analytics", "video_editor", "social_media_api")
}

/** Wraps a session with the Media snippet and the production helpers. */
class MediaCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {

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

    private fun enrich(m: String): String = MediaDomainContext.SYSTEM_PROMPT_SNIPPET + "\n\n" + m

    suspend fun createContentBriefAsync(topic: String, audience: String, platform: String): String =
        inner.agentAsync(
            "Create a detailed content brief for " + platform + ": Topic: " + topic +
                ". Target audience: " + audience +
                ". Include angle, key messages, SEO keywords, call to action, and production notes.",
        )

    suspend fun analyseAudienceDataAsync(analyticsData: String): String =
        inner.agentAsync(
            "Analyse this audience/analytics data and provide actionable content strategy " +
                "recommendations:\n" + analyticsData,
        )

    suspend fun draftPressReleaseAsync(announcement: String, audience: String): String =
        inner.agentAsync(
            "Draft a press release on: " + announcement + " for " + audience +
                ". AP style, inverted pyramid, quote from leadership, boilerplate.",
        )

    suspend fun suggestThumbnailConceptsAsync(videoTopic: String, channelStyle: String): String =
        inner.agentAsync(
            "Suggest 3 thumbnail concepts for a video on " + videoTopic + " in " + channelStyle +
                " style. Hook, composition, text.",
        )

    suspend fun structureNarrativeAsync(topic: String, format: String, durationMinutes: Int): String =
        inner.agentAsync(
            "Structure a " + durationMinutes + "-min " + format + " on " + topic +
                ". Hook, beats, payoff, CTA.",
        )

    suspend fun writeCaptionAsync(mediaDescription: String, platform: String, voice: String): String =
        inner.agentAsync(
            "Write a " + platform + " caption for: " + mediaDescription + ". Voice: " + voice +
                ". Optimise for platform algorithm and accessibility.",
        )
}
