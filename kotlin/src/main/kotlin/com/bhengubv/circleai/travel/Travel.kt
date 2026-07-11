// Travel.kt
//
// Kotlin port of CircleAI.Travel (TravelPrimitives.cs + TravelDomainContext.cs +
// TravelCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory travel board: flights, hotel stays, and trips.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `decimal` -> `BigDecimal`;
//     `DateTime`/`DateTimeOffset` -> `Instant`.
//   * `TripCost` = sum of the trip's flight prices + each stay's nightlyRate ×
//     max(1, whole nights between check-in and check-out); unknown trip throws.
//   * `UpcomingTrips(now)` = trips starting at/after `now`, ASC.

package com.bhengubv.circleai.travel

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
import kotlin.math.max

// =====================================================================
// Primitives (TravelPrimitives.cs)
// =====================================================================

/** A flight. Mirrors C# `Flight`. */
data class Flight(
    val flightId: String,
    val from: String,
    val to: String,
    val departUtc: Instant,
    val arriveUtc: Instant,
    val carrier: String,
    val cabin: String,
    val price: BigDecimal,
    val currency: String,
)

/** A hotel stay. Mirrors C# `HotelStay`. */
data class HotelStay(
    val stayId: String,
    val hotel: String,
    val city: String,
    val checkIn: Instant,
    val checkOut: Instant,
    val nightlyRate: BigDecimal,
    val currency: String,
)

/** A planned trip. Mirrors C# `TravelTrip`. */
data class TravelTrip(
    val tripId: String,
    val name: String,
    val startDate: Instant,
    val endDate: Instant,
    val flightIds: List<String>,
    val stayIds: List<String>,
)

/** Deterministic travel board. Mirrors C# `ITravelBoard`. */
interface ITravelBoard {
    fun add(f: Flight)
    fun add(s: HotelStay)
    fun plan(t: TravelTrip)
    fun getTrip(id: String): TravelTrip?
    fun getFlight(id: String): Flight?
    fun getStay(id: String): HotelStay?
    fun tripCost(tripId: String): BigDecimal
    fun upcomingTrips(now: Instant): List<TravelTrip>
}

/** In-memory [ITravelBoard]. Mirrors C# `InMemoryTravelBoard`. */
class InMemoryTravelBoard : ITravelBoard {
    private val flights = ConcurrentHashMap<String, Flight>()
    private val stays = ConcurrentHashMap<String, HotelStay>()
    private val trips = ConcurrentHashMap<String, TravelTrip>()

    override fun add(f: Flight) { flights[f.flightId] = f }
    override fun add(s: HotelStay) { stays[s.stayId] = s }
    override fun plan(t: TravelTrip) { trips[t.tripId] = t }

    override fun getTrip(id: String): TravelTrip? = trips[id]
    override fun getFlight(id: String): Flight? = flights[id]
    override fun getStay(id: String): HotelStay? = stays[id]

    override fun tripCost(tripId: String): BigDecimal {
        val t = trips[tripId] ?: throw IllegalStateException("Unknown trip $tripId")
        var total = BigDecimal.ZERO
        for (fid in t.flightIds) flights[fid]?.let { total += it.price }
        for (sid in t.stayIds) {
            stays[sid]?.let { s ->
                val nights = max(1L, Duration.between(s.checkIn, s.checkOut).toDays())
                total += s.nightlyRate.multiply(BigDecimal.valueOf(nights))
            }
        }
        return total
    }

    override fun upcomingTrips(now: Instant): List<TravelTrip> =
        trips.values.filter { !it.startDate.isBefore(now) }.sortedBy { it.startDate }
}

// =====================================================================
// DomainContext (TravelDomainContext.cs)
// =====================================================================

/** Static domain context for Travel. Mirrors C# `TravelDomainContext`. */
object TravelDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Travel] Expert travel planning companion. Help with trip itinerary building, visa and " +
            "entry requirements, budget travel strategies, packing lists, travel insurance guidance, and " +
            "safety advisories. Personalise to the traveller profile. Compliance: POPIA, Consumer Protection " +
            "Act (travel packages)."

    val complianceFlags: List<String> = listOf("POPIA", "Consumer_Protection_Act", "IATA_aware")

    val suggestedTools: List<String> = listOf("flight_search", "mapping", "currency_converter", "web_search")
}

// =====================================================================
// CompanionAdapter (TravelCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Travel snippet + helpers. Mirrors C# `TravelCompanionAdapter`. */
class TravelCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${TravelDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun planTripAsync(destination: String, nights: Int, travellers: String, budget: String): String =
        inner.agentAsync("Plan a $nights-night trip to $destination for $travellers. Budget: $budget. Include flights, accommodation tiers, daily activities, transport, and estimated total cost.")

    suspend fun createPackingListAsync(destination: String, duration: String, activities: String): String =
        inner.agentAsync("Create a packing list for $duration in $destination. Activities: $activities. Organise by category (clothing, toiletries, documents, tech, emergency) and note carry-on vs checked restrictions.")

    suspend fun optimiseTripAsync(origin: String, destinations: String, constraints: String): String =
        inner.agentAsync("Optimise trip from $origin through $destinations. Constraints: $constraints. Route, mode mix, lodging, pace.")

    suspend fun draftExpenseClaimAsync(tripSummary: String, expenses: String): String =
        inner.agentAsync("Draft expense claim for trip: $tripSummary. Items: $expenses. Categorise per company policy, flag missing receipts.")

    suspend fun packingListAsync(destination: String, days: Int, activities: String): String =
        inner.agentAsync("Generate packing list for $days days in $destination, activities: $activities. By category + weight optimisation.")

    suspend fun handleVisaQueryAsync(fromCountry: String, toCountry: String, travelPurpose: String): String =
        inner.agentAsync("Outline visa requirements: $fromCountry → $toCountry for $travelPurpose. Process, documents, timeline, common pitfalls.")
}
