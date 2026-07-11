// Hospitality.kt
//
// Kotlin port of CircleAI.Hospitality (HospitalityPrimitives.cs +
// HospitalityDomainContext.cs + HospitalityCompanionAdapter.cs) — the C#
// reference is the EXACT spec. A deterministic in-memory hospitality board:
// rooms, reservations, and front-desk notes.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `decimal` -> `BigDecimal`;
//     `DateTime` -> `Instant` (dates compared as instants).
//   * `AvailableOn(date)` = rooms with no reservation spanning `date`
//     (CheckIn <= date < CheckOut) AND currently clean.
//   * `CheckOut` marks the room unclean when `roomNeedsCleaning` (unknown
//     reservation throws).
//   * `NotesFor` newest-first.

package com.bhengubv.circleai.hospitality

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.math.BigDecimal
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (HospitalityPrimitives.cs)
// =====================================================================

/** A hotel room. Mirrors C# `HotelRoom`. */
data class HotelRoom(val roomId: String, val type: String, val nightlyRate: BigDecimal, val currency: String, val isClean: Boolean)

/** A guest reservation. Mirrors C# `GuestReservation`. */
data class GuestReservation(val reservationId: String, val guestName: String, val roomId: String, val checkIn: Instant, val checkOut: Instant)

/** A front-desk note. Mirrors C# `FrontDeskNote`. */
data class FrontDeskNote(val noteId: String, val reservationId: String, val body: String, val atUtc: Instant)

/** Deterministic hospitality board. Mirrors C# `IHospitalityBoard`. */
interface IHospitalityBoard {
    fun addRoom(r: HotelRoom)
    fun getRoom(id: String): HotelRoom?
    fun availableOn(date: Instant): List<HotelRoom>
    fun reserve(r: GuestReservation)
    fun checkOut(reservationId: String, roomNeedsCleaning: Boolean)
    fun getReservation(id: String): GuestReservation?
    fun addNote(n: FrontDeskNote)
    fun notesFor(reservationId: String): List<FrontDeskNote>
}

/** In-memory [IHospitalityBoard]. Mirrors C# `InMemoryHospitalityBoard`. */
class InMemoryHospitalityBoard : IHospitalityBoard {
    private val rooms = ConcurrentHashMap<String, HotelRoom>()
    private val res = ConcurrentHashMap<String, GuestReservation>()
    private val notes = mutableListOf<FrontDeskNote>()
    private val lock = Any()

    override fun addRoom(r: HotelRoom) { rooms[r.roomId] = r }
    override fun getRoom(id: String): HotelRoom? = rooms[id]

    override fun availableOn(date: Instant): List<HotelRoom> {
        val booked = res.values
            .filter { !it.checkIn.isAfter(date) && it.checkOut.isAfter(date) }
            .map { it.roomId }
            .toHashSet()
        return rooms.values.filter { it.roomId !in booked && it.isClean }
    }

    override fun reserve(r: GuestReservation) { res[r.reservationId] = r }

    override fun checkOut(reservationId: String, roomNeedsCleaning: Boolean) {
        val r = res[reservationId] ?: throw IllegalStateException("Unknown reservation $reservationId")
        if (roomNeedsCleaning) {
            val room = rooms[r.roomId]
            if (room != null) rooms[r.roomId] = room.copy(isClean = false)
        }
    }

    override fun getReservation(id: String): GuestReservation? = res[id]
    override fun addNote(n: FrontDeskNote) { synchronized(lock) { notes.add(n) } }
    override fun notesFor(reservationId: String): List<FrontDeskNote> = synchronized(lock) {
        notes.filter { it.reservationId == reservationId }.sortedByDescending { it.atUtc }
    }
}

// =====================================================================
// DomainContext (HospitalityDomainContext.cs)
// =====================================================================

/** Static domain context for Hospitality. Mirrors C# `HospitalityDomainContext`. */
object HospitalityDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Hospitality] Expert hospitality operations assistant. Help with PMS integration, RevPAR " +
            "optimisation, F&B menu costing, housekeeping scheduling, guest satisfaction recovery, and MICE " +
            "event coordination. Apply yield management principles. Compliance: Tourism Act, CATHSSETA, " +
            "Liquor Act, Health regulations, POPIA."

    val complianceFlags: List<String> = listOf("Tourism_Act", "CATHSSETA", "Liquor_Act", "Health_Regs", "POPIA")

    val suggestedTools: List<String> = listOf("pms_system", "analytics", "document_editor", "reservation_engine")
}

// =====================================================================
// CompanionAdapter (HospitalityCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Hospitality snippet + helpers. Mirrors C# `HospitalityCompanionAdapter`. */
class HospitalityCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${HospitalityDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun optimiseRevParAsync(occupancyData: String, rateData: String): String =
        inner.agentAsync("Analyse RevPAR performance and recommend rate and distribution strategies:\nOccupancy: $occupancyData\nRates: $rateData")

    suspend fun handleGuestComplaintAsync(complaint: String, context: String): String =
        inner.agentAsync("Draft a service recovery response for this guest complaint. Complaint: $complaint. Context: $context. Apply LAST (Listen, Apologise, Solve, Thank) framework.")

    suspend fun draftGuestWelcomeAsync(guestName: String, roomType: String, lengthOfStay: String): String =
        inner.agentAsync("Draft a warm welcome message for $guestName in $roomType, staying $lengthOfStay. Include wifi, breakfast, local pick.")

    suspend fun handleComplaintAsync(complaint: String, sentiment: String): String =
        inner.agentAsync("Handle this guest complaint ($sentiment): $complaint. Apologise, recover, prevent — concrete next step in each.")

    suspend fun suggestExperienceAsync(guestProfile: String, lengthOfStay: String, budget: BigDecimal): String =
        inner.agentAsync("Suggest a $lengthOfStay experience for guest: $guestProfile on $budget budget. Mix dining, activity, downtime.")

    suspend fun optimiseHousekeepingRouteAsync(roomList: String, staffCount: Int): String =
        inner.agentAsync("Optimise housekeeping route for rooms $roomList with $staffCount staff. Sequence for minimum dead-walk + checkout-priority first.")
}
