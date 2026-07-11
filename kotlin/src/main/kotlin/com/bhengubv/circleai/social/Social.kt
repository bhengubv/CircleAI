// Social.kt
//
// Kotlin port of CircleAI.Social (SocialPrimitives.cs + SocialDomainContext.cs +
// SocialCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory social board: posts, reactions, follows, and a feed.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTimeOffset` -> `Instant`.
//   * `ReactionCount` case-insensitive on the reaction kind.
//   * `Follow` rejects self-follows (throws); `Unfollow` removes all matches.
//   * `FeedFor` = posts by followees, newest-first, capped at `limit`
//     (default 20; limit<=0 throws).
//   * `Followers` = follower ids of the given user.

package com.bhengubv.circleai.social

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (SocialPrimitives.cs)
// =====================================================================

/** A social post. Mirrors C# `SocialPost`. */
data class SocialPost(val postId: String, val authorId: String, val body: String, val atUtc: Instant, val tags: List<String>)

/** A reaction to a post. Mirrors C# `Reaction`. */
data class Reaction(val postId: String, val userId: String, val kind: String, val atUtc: Instant)

/** A follow edge. Mirrors C# `Follow`. */
data class Follow(val followerId: String, val followeeId: String, val atUtc: Instant)

/** Deterministic social board. Mirrors C# `ISocialBoard`. */
interface ISocialBoard {
    fun post(p: SocialPost)
    fun getPost(id: String): SocialPost?
    fun react(r: Reaction)
    fun reactionCount(postId: String, kind: String): Int
    fun follow(f: Follow)
    fun unfollow(followerId: String, followeeId: String)
    fun feedFor(userId: String, limit: Int = 20): List<SocialPost>
    fun followers(userId: String): List<String>
}

/** In-memory [ISocialBoard]. Mirrors C# `InMemorySocialBoard`. */
class InMemorySocialBoard : ISocialBoard {
    private val posts = ConcurrentHashMap<String, SocialPost>()
    private val reacts = mutableListOf<Reaction>()
    private val follows = mutableListOf<Follow>()
    private val lock = Any()

    override fun post(p: SocialPost) { posts[p.postId] = p }
    override fun getPost(id: String): SocialPost? = posts[id]

    override fun react(r: Reaction) { synchronized(lock) { reacts.add(r) } }

    override fun reactionCount(postId: String, kind: String): Int = synchronized(lock) {
        reacts.count { it.postId == postId && it.kind.equals(kind, ignoreCase = true) }
    }

    override fun follow(f: Follow) {
        if (f.followerId == f.followeeId) throw IllegalStateException("Cannot follow yourself.")
        synchronized(lock) { follows.add(f) }
    }

    override fun unfollow(followerId: String, followeeId: String) {
        synchronized(lock) { follows.removeAll { it.followerId == followerId && it.followeeId == followeeId } }
    }

    override fun feedFor(userId: String, limit: Int): List<SocialPost> {
        if (limit <= 0) throw IllegalArgumentException("limit")
        val following = synchronized(lock) {
            follows.filter { it.followerId == userId }.map { it.followeeId }.toHashSet()
        }
        return posts.values.filter { it.authorId in following }.sortedByDescending { it.atUtc }.take(limit)
    }

    override fun followers(userId: String): List<String> = synchronized(lock) {
        follows.filter { it.followeeId == userId }.map { it.followerId }
    }
}

// =====================================================================
// DomainContext (SocialDomainContext.cs)
// =====================================================================

/** Static domain context for Social. Mirrors C# `SocialDomainContext`. */
object SocialDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Social] Expert social media and community management assistant. Help with platform-specific " +
            "content creation (LinkedIn, Instagram, TikTok, X, Facebook), engagement strategy, hashtag " +
            "research, influencer brief writing, community moderation guidelines, and social analytics. Apply " +
            "scroll-stopping creative principles. Compliance: POPIA, ASA Advertising Code, platform community " +
            "standards."

    val complianceFlags: List<String> = listOf("POPIA", "ASA_Advertising_Code", "Platform_Community_Standards")

    val suggestedTools: List<String> = listOf("social_media_api", "analytics", "content_planner", "image_tools")
}

// =====================================================================
// CompanionAdapter (SocialCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Social snippet + helpers. Mirrors C# `SocialCompanionAdapter`. */
class SocialCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${SocialDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun writePostAsync(platform: String, message: String, tone: String): String =
        inner.agentAsync("Write an engaging $platform post. Core message: $message. Tone: $tone. Include relevant hashtags, call to action, and emoji where appropriate for the platform.")

    suspend fun planContentCalendarAsync(brand: String, month: String, goals: String): String =
        inner.agentAsync("Plan a social media content calendar for $brand in $month. Goals: $goals. Include content mix, posting frequency, themes, and key dates.")

    suspend fun draftPostAsync(topic: String, platform: String, voice: String): String =
        inner.agentAsync("Draft a $platform post on '$topic' in $voice voice. Hook, payload, CTA, platform-appropriate length.")

    suspend fun analyseEngagementAsync(postPerformance: String, baseline: String): String =
        inner.agentAsync("Analyse post performance: $postPerformance vs baseline: $baseline. Why it over/under-performed + what to try next.")

    suspend fun responseToCriticAsync(critique: String, ourPosition: String): String =
        inner.agentAsync("Respond to public critique: $critique. Our position: $ourPosition. De-escalate, acknowledge, offer path forward.")

    suspend fun designContentSeriesAsync(theme: String, episodeCount: Int, platform: String): String =
        inner.agentAsync("Design a $episodeCount-episode content series on '$theme' for $platform. Per-episode hook + cumulative arc.")
}
