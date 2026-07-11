// RealEstate.kt
//
// Kotlin port of CircleAI.RealEstate (RealEstatePrimitives.cs +
// RealEstateDomainContext.cs + RealEstateCompanionAdapter.cs) — the C#
// reference is the EXACT spec. A deterministic in-memory real-estate
// board: properties, listings, valuations, viewings, and a suburb-average
// comparable.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `enum` -> `enum class`;
//     `decimal` -> `BigDecimal`; `DateTimeOffset` -> `Instant`.
//   * `Close` throws on unknown listing; flips IsActive false.
//   * `ActiveInSuburb` returns active listings whose property suburb matches
//     (OrdinalIgnoreCase), ordered by ListedUtc DESC.
//   * `SuburbAverage` = mean asking price of active-in-suburb listings, null
//     when none.
//   * `{monthlyRent:C}` currency formatting reproduced via [fmtC] (US culture).

package com.bhengubv.circleai.realestate

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.math.BigDecimal
import java.math.RoundingMode
import java.text.NumberFormat
import java.time.Instant
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (RealEstatePrimitives.cs)
// =====================================================================

/** Kind of property. Mirrors C# `PropertyKind`. */
enum class PropertyKind { Apartment, House, Townhouse, Commercial, Land }

/** A physical property. Mirrors C# `Property`. */
data class Property(
    val propertyId: String,
    val suburb: String,
    val kind: PropertyKind,
    val beds: Int,
    val baths: Int,
    val floorAreaM2: Double,
)

/** A market listing. Mirrors C# `Listing`. */
data class Listing(
    val listingId: String,
    val propertyId: String,
    val askingPrice: BigDecimal,
    val currency: String,
    val listedUtc: Instant,
    val isActive: Boolean,
)

/** A property valuation. Mirrors C# `Valuation`. */
data class Valuation(val propertyId: String, val estimatedValue: BigDecimal, val source: String, val atUtc: Instant)

/** A scheduled viewing. Mirrors C# `Viewing`. */
data class Viewing(val viewingId: String, val listingId: String, val attendeeName: String, val atUtc: Instant)

/** Deterministic real-estate board. Mirrors C# `IRealEstateBoard`. */
interface IRealEstateBoard {
    fun registerProperty(p: Property)
    fun list(l: Listing)
    fun close(listingId: String)
    fun value(v: Valuation)
    fun scheduleViewing(v: Viewing)
    fun activeInSuburb(suburb: String): List<Listing>
    fun suburbAverage(suburb: String): BigDecimal?
}

/** In-memory [IRealEstateBoard]. Mirrors C# `InMemoryRealEstateBoard`. */
class InMemoryRealEstateBoard : IRealEstateBoard {
    private val props = ConcurrentHashMap<String, Property>()
    private val listings = ConcurrentHashMap<String, Listing>()
    private val vals = mutableListOf<Valuation>()
    private val viewings = mutableListOf<Viewing>()
    private val lock = Any()

    override fun registerProperty(p: Property) { props[p.propertyId] = p }

    override fun list(l: Listing) { listings[l.listingId] = l }

    override fun close(listingId: String) {
        val l = listings[listingId] ?: throw IllegalStateException("Unknown listing $listingId")
        listings[listingId] = l.copy(isActive = false)
    }

    override fun value(v: Valuation) { synchronized(lock) { vals.add(v) } }
    override fun scheduleViewing(v: Viewing) { synchronized(lock) { viewings.add(v) } }

    override fun activeInSuburb(suburb: String): List<Listing> {
        if (suburb.isBlank()) throw IllegalArgumentException("suburb required")
        return listings.values
            .filter { l ->
                l.isActive && props[l.propertyId]?.suburb?.equals(suburb, ignoreCase = true) == true
            }
            .sortedByDescending { it.listedUtc }
    }

    override fun suburbAverage(suburb: String): BigDecimal? {
        val rows = activeInSuburb(suburb)
        if (rows.isEmpty()) return null
        val sum = rows.fold(BigDecimal.ZERO) { acc, l -> acc + l.askingPrice }
        return sum.divide(BigDecimal(rows.size), 10, RoundingMode.HALF_UP)
    }
}

// =====================================================================
// DomainContext (RealEstateDomainContext.cs)
// =====================================================================

/** Static domain context for RealEstate. Mirrors C# `RealEstateDomainContext`. */
object RealEstateDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: RealEstate] Expert real estate assistant. Help with property market analysis, valuation " +
            "frameworks, lease and sale agreement review, conveyancing timelines, sectional title rules, and " +
            "rental management. Ground advice in current market data. Compliance: Alienation of Land Act, " +
            "Rental Housing Act, PPRA, FICA, POPIA."

    val complianceFlags: List<String> = listOf("Alienation_of_Land_Act", "Rental_Housing_Act", "PPRA", "FICA", "POPIA")

    val suggestedTools: List<String> = listOf("property_listings", "document_editor", "map", "analytics")
}

// =====================================================================
// CompanionAdapter (RealEstateCompanionAdapter.cs)
// =====================================================================

/** Formats like .NET `{value:C}` under the US culture. */
internal fun fmtC(value: BigDecimal): String = NumberFormat.getCurrencyInstance(Locale.US).format(value)

/** Wraps an [ICompanionSession] with the RealEstate snippet + helpers. Mirrors C# `RealEstateCompanionAdapter`. */
class RealEstateCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${RealEstateDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun comparePropertiesAsync(prop1: String, prop2: String): String =
        inner.agentAsync("Compare these two properties and recommend which offers better investment value:\nProperty 1:\n$prop1\nProperty 2:\n$prop2")

    suspend fun draftLeaseAsync(landlordName: String, tenantName: String, address: String, monthlyRent: BigDecimal, months: Int): String =
        inner.agentAsync("Draft a residential lease agreement. Landlord: $landlordName. Tenant: $tenantName. Property: $address. Rent: ${fmtC(monthlyRent)}/month. Term: $months months. Include deposit, maintenance, and termination clauses per Rental Housing Act.")

    suspend fun valuePropertyAsync(propertyDescription: String, suburb: String, comparableSales: String): String =
        inner.agentAsync("Estimate value for $propertyDescription in $suburb. Comps: $comparableSales. Range, drivers, market caveats.")

    suspend fun draftListingAsync(propertyDescription: String, targetBuyer: String): String =
        inner.agentAsync("Draft a property listing for $propertyDescription targeting $targetBuyer. Headline, hero paragraph, features, lifestyle close.")

    suspend fun analyseOfferAsync(offerAmount: String, listingPrice: String, marketConditions: String): String =
        inner.agentAsync("Analyse offer $offerAmount vs list $listingPrice in market: $marketConditions. Counter strategy, negotiation levers.")

    suspend fun prepareViewingAsync(propertyType: String, targetSegment: String): String =
        inner.agentAsync("Plan an open viewing for $propertyType aimed at $targetSegment. Staging, route, FAQs, follow-up cadence.")
}
