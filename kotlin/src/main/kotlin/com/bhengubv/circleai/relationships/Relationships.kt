// Relationships.kt
//
// Kotlin port of CircleAI.Relationships (RelationshipsPrimitives.cs +
// RelationshipsDomainContext.cs + RelationshipsCompanionAdapter.cs) — the C#
// reference is the EXACT spec. A deterministic in-memory CRM-lite for personal
// relationships: contacts, important dates, and last-contact tracking.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTime`/`DateTimeOffset` -> `Instant`.
//   * `Contacts` ordered by Name ASC.
//   * `UpcomingThisMonth` = important dates whose month == current UTC month,
//     ordered by day-of-month ASC.
//   * `LastContact` = timestamp of the newest touchpoint for the contact (or null).
//   * `NotContactedSince(cutoff)` = contacts whose last contact is null or < cutoff.

package com.bhengubv.circleai.relationships

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.time.ZoneOffset
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (RelationshipsPrimitives.cs)
// =====================================================================

/** A personal contact. Mirrors C# `PersonContact`. */
data class PersonContact(val contactId: String, val name: String, val relationship: String, val notes: String?)

/** An important date for a contact. Mirrors C# `ImportantDate`. */
data class ImportantDate(val dateId: String, val contactId: String, val kind: String, val date: Instant)

/** A recorded touchpoint. Mirrors C# `ContactEvent`. */
data class ContactEvent(val contactId: String, val kind: String, val atUtc: Instant, val note: String?)

/** Deterministic relationships board. Mirrors C# `IRelationshipsBoard`. */
interface IRelationshipsBoard {
    fun addContact(c: PersonContact)
    fun getContact(id: String): PersonContact?
    val contacts: List<PersonContact>
    fun addImportantDate(d: ImportantDate)
    fun upcomingThisMonth(): List<ImportantDate>
    fun recordTouchpoint(e: ContactEvent)
    fun lastContact(contactId: String): Instant?
    fun notContactedSince(cutoff: Instant): List<PersonContact>
}

/** In-memory [IRelationshipsBoard]. Mirrors C# `InMemoryRelationshipsBoard`. */
class InMemoryRelationshipsBoard : IRelationshipsBoard {
    private val contactsMap = ConcurrentHashMap<String, PersonContact>()
    private val dates = ConcurrentHashMap<String, ImportantDate>()
    private val events = mutableListOf<ContactEvent>()
    private val lock = Any()

    override fun addContact(c: PersonContact) { contactsMap[c.contactId] = c }
    override fun getContact(id: String): PersonContact? = contactsMap[id]
    override val contacts: List<PersonContact>
        get() = contactsMap.values.sortedBy { it.name }

    override fun addImportantDate(d: ImportantDate) { dates[d.dateId] = d }

    override fun upcomingThisMonth(): List<ImportantDate> {
        val nowMonth = Instant.now().atZone(ZoneOffset.UTC).monthValue
        return dates.values
            .filter { it.date.atZone(ZoneOffset.UTC).monthValue == nowMonth }
            .sortedBy { it.date.atZone(ZoneOffset.UTC).dayOfMonth }
    }

    override fun recordTouchpoint(e: ContactEvent) { synchronized(lock) { events.add(e) } }

    override fun lastContact(contactId: String): Instant? = synchronized(lock) {
        events.filter { it.contactId == contactId }.maxByOrNull { it.atUtc }?.atUtc
    }

    override fun notContactedSince(cutoff: Instant): List<PersonContact> =
        contactsMap.values.filter {
            val last = lastContact(it.contactId)
            last == null || last.isBefore(cutoff)
        }
}

// =====================================================================
// DomainContext (RelationshipsDomainContext.cs)
// =====================================================================

/** Static domain context for Relationships. Mirrors C# `RelationshipsDomainContext`. */
object RelationshipsDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Relationships] Empathetic relationship support companion. Help with communication " +
            "strategies, conflict resolution (NVC principles), relationship goal-setting, and self-reflection " +
            "prompts. Non-judgmental, no-advice-without-consent approach. Not a therapy service. Compliance: POPIA."

    val complianceFlags: List<String> = listOf("POPIA", "Not_Therapy")

    val suggestedTools: List<String> = listOf("journal", "mood_tracker", "calendar")
}

// =====================================================================
// CompanionAdapter (RelationshipsCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Relationships snippet + helpers. Mirrors C# `RelationshipsCompanionAdapter`. */
class RelationshipsCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${RelationshipsDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun guideConflictResolutionAsync(situation: String): String =
        inner.agentAsync("Guide me through resolving this conflict using Non-Violent Communication (NVC):\n$situation\nHelp me identify observations, feelings, needs, and requests.")

    suspend fun draftDifficultConversationAsync(topic: String, relationship: String): String =
        inner.agentAsync("Help me prepare for a difficult conversation about $topic with my $relationship. Draft key points using assertive but empathetic language.")

    suspend fun planCheckInAsync(relationship: String, lastTouch: String, occasion: String): String =
        inner.agentAsync("Plan a check-in with $relationship, last touched $lastTouch. Occasion: $occasion. Suggest channel, opener, generous question.")

    suspend fun draftMeaningfulMessageAsync(recipient: String, moment: String): String =
        inner.agentAsync("Draft a heartfelt message to $recipient for $moment. Specific, not generic; refer to shared history.")

    suspend fun resolveTensionAsync(conflictSummary: String, desiredOutcome: String): String =
        inner.agentAsync("Help resolve tension: $conflictSummary. Desired outcome: $desiredOutcome. NVC-style script + likely responses.")

    suspend fun rememberImportantDateAsync(personName: String, date: String, history: String): String =
        inner.agentAsync("Prep for $personName's important date ($date). History: $history. Suggest gift, message, gesture.")
}
