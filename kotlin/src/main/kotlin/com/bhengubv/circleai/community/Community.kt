// Community.kt
//
// Kotlin port of CircleAI.Community (CommunityPrimitives.cs +
// CommunityDomainContext.cs + CommunityCompanionAdapter.cs) — the C# reference
// is the EXACT spec. A deterministic in-memory community board: groups,
// announcements, and volunteer opportunities.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTimeOffset` -> `Instant`.
//   * `GroupsForMember` = groups whose MemberIds contain the member.
//   * `AnnouncementsFor` newest-first, capped at `limit` (default 20).
//   * `Opportunities` = future opportunities (UTC now), ASC.

package com.bhengubv.circleai.community

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (CommunityPrimitives.cs)
// =====================================================================

/** A community group. Mirrors C# `CommunityGroup`. */
data class CommunityGroup(val groupId: String, val name: String, val purpose: String, val memberIds: List<String>)

/** An announcement. Mirrors C# `Announcement`. */
data class Announcement(val announcementId: String, val groupId: String, val title: String, val body: String, val atUtc: Instant)

/** A volunteer opportunity. Mirrors C# `VolunteerOpportunity`. */
data class VolunteerOpportunity(val oppId: String, val groupId: String, val description: String, val volunteersNeeded: Int, val whenUtc: Instant)

/** Deterministic community board. Mirrors C# `ICommunityBoard`. */
interface ICommunityBoard {
    fun create(g: CommunityGroup)
    fun getGroup(id: String): CommunityGroup?
    fun groupsForMember(memberId: String): List<CommunityGroup>
    fun post(a: Announcement)
    fun announcementsFor(groupId: String, limit: Int = 20): List<Announcement>
    fun list(o: VolunteerOpportunity)
    fun opportunities(): List<VolunteerOpportunity>
}

/** In-memory [ICommunityBoard]. Mirrors C# `InMemoryCommunityBoard`. */
class InMemoryCommunityBoard : ICommunityBoard {
    private val groups = ConcurrentHashMap<String, CommunityGroup>()
    private val annc = mutableListOf<Announcement>()
    private val opps = ConcurrentHashMap<String, VolunteerOpportunity>()
    private val lock = Any()

    override fun create(g: CommunityGroup) { groups[g.groupId] = g }
    override fun getGroup(id: String): CommunityGroup? = groups[id]
    override fun groupsForMember(memberId: String): List<CommunityGroup> =
        groups.values.filter { memberId in it.memberIds }

    override fun post(a: Announcement) { synchronized(lock) { annc.add(a) } }
    override fun announcementsFor(groupId: String, limit: Int): List<Announcement> = synchronized(lock) {
        annc.filter { it.groupId == groupId }.sortedByDescending { it.atUtc }.take(limit)
    }

    override fun list(o: VolunteerOpportunity) { opps[o.oppId] = o }
    override fun opportunities(): List<VolunteerOpportunity> {
        val now = Instant.now()
        return opps.values.filter { !it.whenUtc.isBefore(now) }.sortedBy { it.whenUtc }
    }

    /** Number of groups. */
    val groupCount: Int get() = groups.size

    /** Remove a group by id. Returns true if one was present. */
    fun removeGroup(groupId: String): Boolean = groups.remove(groupId) != null

    /** Add [memberId] to a group. Returns false if the group is unknown or already a member. */
    fun addMember(groupId: String, memberId: String): Boolean {
        val g = groups[groupId] ?: return false
        if (memberId in g.memberIds) return false
        groups[groupId] = g.copy(memberIds = g.memberIds + memberId)
        return true
    }

    /** Remove [memberId] from a group. Returns false if the group is unknown or not a member. */
    fun removeMember(groupId: String, memberId: String): Boolean {
        val g = groups[groupId] ?: return false
        if (memberId !in g.memberIds) return false
        groups[groupId] = g.copy(memberIds = g.memberIds.filter { it != memberId })
        return true
    }

    /** Volunteer opportunities for a group (all, not only future), earliest first. */
    fun opportunitiesForGroup(groupId: String): List<VolunteerOpportunity> =
        opps.values.filter { it.groupId == groupId }.sortedBy { it.whenUtc }

    /** Total volunteers needed across all upcoming opportunities. */
    fun totalVolunteersNeeded(): Int = opportunities().sumOf { it.volunteersNeeded }
}

// =====================================================================
// DomainContext (CommunityDomainContext.cs)
// =====================================================================

/** Static domain context for Community. Mirrors C# `CommunityDomainContext`. */
object CommunityDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Community] Community organising and engagement assistant. Help with community event " +
            "planning, volunteer coordination, advocacy letter writing, fundraising strategies, and " +
            "neighbourhood communication. Empower grassroots action. Compliance: NPO Act, POPIA, Fundraising Act."

    val complianceFlags: List<String> = listOf("NPO_Act", "Fundraising_Act", "POPIA")

    val suggestedTools: List<String> = listOf("event_manager", "document_editor", "communication_tools", "volunteer_tracker")
}

// =====================================================================
// CompanionAdapter (CommunityCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Community snippet + helpers. Mirrors C# `CommunityCompanionAdapter`. */
class CommunityCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${CommunityDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun planCommunityEventAsync(eventType: String, size: String, budget: String): String =
        inner.agentAsync("Plan a community $eventType for $size people. Budget: $budget. Include logistics checklist, volunteer roles, publicity plan, and risk management.")

    suspend fun writeAdvocacyLetterAsync(issue: String, authority: String): String =
        inner.agentAsync("Write a compelling advocacy letter about $issue to $authority. Include evidence, community impact, and specific ask.")

    suspend fun writeAnnouncementAsync(groupName: String, subject: String, callToAction: String): String =
        inner.agentAsync("Write a community announcement for $groupName about '$subject'. CTA: $callToAction. Warm, concise, 80 words.")

    suspend fun draftConflictMediationOpenerAsync(conflictSummary: String, partiesInvolved: String): String =
        inner.agentAsync("Draft a mediator-style opener for: $conflictSummary involving $partiesInvolved. Acknowledge feelings, set ground rules, propose next step.")

    suspend fun designVolunteerCampaignAsync(need: String, peopleNeeded: Int, whenText: String): String =
        inner.agentAsync("Design a volunteer drive: need $need, $peopleNeeded people, $whenText. Cover signup channel, shift design, recognition, retention.")

    suspend fun writeCommunityNewsletterAsync(highlights: String, upcoming: String): String =
        inner.agentAsync("Write a 200-word community newsletter. Highlights: $highlights. Upcoming: $upcoming. Friendly, scan-friendly.")
}
