// Tourism.kt
//
// Kotlin port of CircleAI.Tourism (TourismPrimitives.cs + TourismDomainContext.cs +
// TourismCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory tourism board: attractions, itineraries, and bookings.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `TimeSpan` -> `java.time.Duration`,
//     `DateTime` -> `java.time.Instant`.
//   * `AttractionsInCity` / `ByTag` are case-insensitive, ordered by Name ASC
//     (blank arg throws).
//   * `Bookings` is a snapshot in insertion order.

package com.bhengubv.circleai.tourism

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.math.BigDecimal
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (TourismPrimitives.cs)
// =====================================================================

/** An attraction. Mirrors C# `Attraction`. */
data class Attraction(
    val attractionId: String,
    val name: String,
    val city: String,
    val country: String,
    val lat: Double,
    val lon: Double,
    val tags: List<String>,
)

/** One itinerary item. Mirrors C# `ItineraryItem`. */
data class ItineraryItem(val dayIndex: Int, val startLocal: Duration, val endLocal: Duration, val attractionId: String, val note: String?)

/** An itinerary. Mirrors C# `Itinerary`. */
data class Itinerary(val itineraryId: String, val title: String, val items: List<ItineraryItem>)

/** A tourism booking. Mirrors C# `TourismBooking`. */
data class TourismBooking(val bookingId: String, val itineraryId: String, val startDate: Instant, val travelers: Int, val totalPrice: BigDecimal, val currency: String)

/** Deterministic tourism board. Mirrors C# `ITourismBoard`. */
interface ITourismBoard {
    fun add(a: Attraction)
    fun attractionsInCity(city: String): List<Attraction>
    fun byTag(tag: String): List<Attraction>
    fun plan(i: Itinerary)
    fun getItinerary(id: String): Itinerary?
    fun book(b: TourismBooking)
    val bookings: List<TourismBooking>
}

/** In-memory [ITourismBoard]. Mirrors C# `InMemoryTourismBoard`. */
class InMemoryTourismBoard : ITourismBoard {
    private val attractions = ConcurrentHashMap<String, Attraction>()
    private val itineraries = ConcurrentHashMap<String, Itinerary>()
    private val bookingsList = mutableListOf<TourismBooking>()
    private val lock = Any()

    override fun add(a: Attraction) { attractions[a.attractionId] = a }

    override fun attractionsInCity(city: String): List<Attraction> {
        if (city.isBlank()) throw IllegalArgumentException("city required")
        return attractions.values.filter { it.city.equals(city, ignoreCase = true) }.sortedBy { it.name }
    }

    override fun byTag(tag: String): List<Attraction> {
        if (tag.isBlank()) throw IllegalArgumentException("tag required")
        return attractions.values.filter { a -> a.tags.any { it.equals(tag, ignoreCase = true) } }.sortedBy { it.name }
    }

    override fun plan(i: Itinerary) { itineraries[i.itineraryId] = i }
    override fun getItinerary(id: String): Itinerary? = itineraries[id]

    override fun book(b: TourismBooking) { synchronized(lock) { bookingsList.add(b) } }
    override val bookings: List<TourismBooking>
        get() = synchronized(lock) { bookingsList.toList() }
}

// =====================================================================
// DomainContext (TourismDomainContext.cs)
// =====================================================================

/** Static domain context for Tourism. Mirrors C# `TourismDomainContext`. */
object TourismDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Tourism] Expert tourism and travel operations assistant. Help with itinerary design, tour " +
            "package costing, guide briefing notes, destination marketing, and safety management plans. Apply " +
            "experiential travel principles. Compliance: Tourism Act 3/2014, SABS tour operator standards, " +
            "SATSA, POPIA."

    val complianceFlags: List<String> = listOf("Tourism_Act_3_2014", "SABS_Tour_Ops", "SATSA", "POPIA")

    val suggestedTools: List<String> = listOf("mapping", "booking_system", "document_editor", "weather_api")
}

// =====================================================================
// CompanionAdapter (TourismCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Tourism snippet + helpers. Mirrors C# `TourismCompanionAdapter`. */
class TourismCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${TourismDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun designItineraryAsync(destination: String, nights: Int, guestProfile: String): String =
        inner.agentAsync("Design a $nights-night itinerary for $destination tailored to: $guestProfile. Include daily schedule, accommodation category, transport, meals, and activities with timing.")

    suspend fun costPackageAsync(itinerary: String, pax: Int): String =
        inner.agentAsync("Cost this tour package for $pax passengers:\n$itinerary\nProvide cost per person, breakeven point, and suggested selling price at 25% margin.")

    suspend fun buildItineraryAsync(destination: String, days: Int, travelerProfile: String): String =
        inner.agentAsync("Build a $days-day $destination itinerary for $travelerProfile. Day-by-day rhythm, must-sees, hidden gems, food.")

    suspend fun estimateBudgetAsync(destination: String, travellers: Int, days: Int, standard: String): String =
        inner.agentAsync("Estimate budget for $travellers pax, $days days in $destination, $standard standard. Categories + total range.")

    suspend fun handleTravelDisruptionAsync(disruption: String, itineraryContext: String): String =
        inner.agentAsync("Handle travel disruption: $disruption. Itinerary context: $itineraryContext. Recovery options, comms templates, rebook checklist.")

    suspend fun recommendExperienceAsync(interests: String, timeOfDay: String, location: String): String =
        inner.agentAsync("Recommend an experience for $interests at $timeOfDay in $location. Why-it-fits + booking practicalities.")
}
