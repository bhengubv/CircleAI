// Logistics.kt
//
// Kotlin port of CircleAI.Logistics (LogisticsPrimitives.cs +
// LogisticsDomainContext.cs + LogisticsCompanionAdapter.cs) — the C#
// reference is the EXACT spec. A deterministic in-memory logistics board:
// shipments, vehicles, route legs, and a simple route-cost estimator.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `decimal` -> `BigDecimal`;
//     `DateTimeOffset` -> `Instant`.
//   * `PlanRoute` throws on unknown vehicle; totalKm = Σ leg distances;
//     cost = (decimal)(totalKm * CostPerKm) — i.e. a `double` product widened
//     to `BigDecimal` (via `BigDecimal(double)` to mirror the .NET cast exactly).
//   * Plan ids are sequenced atomically ("plan-N"); legs are copied into the plan.
//   * `Vehicles` orders by VehicleId ASC.

package com.bhengubv.circleai.logistics

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.math.BigDecimal
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong

// =====================================================================
// Primitives (LogisticsPrimitives.cs)
// =====================================================================

/** A shipment to move. Mirrors C# `Shipment`. */
data class Shipment(
    val shipmentId: String,
    val origin: String,
    val destination: String,
    val weightKg: Double,
    val volumeM3: Double,
    val incoterm: String,
    val pickupAtUtc: Instant,
)

/** A delivery vehicle. Mirrors C# `Vehicle`. */
data class Vehicle(val vehicleId: String, val capacityKg: Double, val capacityM3: Double, val costPerKm: Double)

/** One leg of a route. Mirrors C# `RouteLeg`. */
data class RouteLeg(val fromCode: String, val toCode: String, val distanceKm: Double)

/** A planned route with cost. Mirrors C# `RoutePlan`. */
data class RoutePlan(
    val planId: String,
    val vehicleId: String,
    val legs: List<RouteLeg>,
    val totalDistanceKm: Double,
    val estimatedCost: BigDecimal,
)

/** Deterministic logistics board. Mirrors C# `ILogisticsBoard`. */
interface ILogisticsBoard {
    fun registerShipment(s: Shipment)
    fun registerVehicle(v: Vehicle)
    fun getShipment(id: String): Shipment?
    val vehicles: List<Vehicle>
    fun planRoute(vehicleId: String, legs: List<RouteLeg>): RoutePlan
}

/** In-memory [ILogisticsBoard]. Mirrors C# `InMemoryLogisticsBoard`. */
class InMemoryLogisticsBoard : ILogisticsBoard {
    private val shipments = ConcurrentHashMap<String, Shipment>()
    private val vehicles_ = ConcurrentHashMap<String, Vehicle>()
    private val seq = AtomicLong(0)

    override fun registerShipment(s: Shipment) {
        if (s.shipmentId.isBlank()) throw IllegalArgumentException("ShipmentId required")
        shipments[s.shipmentId] = s
    }

    override fun registerVehicle(v: Vehicle) {
        if (v.vehicleId.isBlank()) throw IllegalArgumentException("VehicleId required")
        vehicles_[v.vehicleId] = v
    }

    override fun getShipment(id: String): Shipment? = shipments[id]
    override val vehicles: List<Vehicle>
        get() = vehicles_.values.sortedBy { it.vehicleId }

    override fun planRoute(vehicleId: String, legs: List<RouteLeg>): RoutePlan {
        if (vehicleId.isBlank()) throw IllegalArgumentException("vehicleId required")
        val vehicle = vehicles_[vehicleId] ?: throw IllegalStateException("Unknown vehicle '$vehicleId'.")
        val totalKm = legs.sumOf { it.distanceKm }
        // Parity with C# `(decimal)(totalKm * vehicle.CostPerKm)`: compute the
        // product in double first, then widen to BigDecimal.
        val cost = BigDecimal(totalKm * vehicle.costPerKm)
        return RoutePlan("plan-${seq.incrementAndGet()}", vehicleId, legs.toList(), totalKm, cost)
    }
}

// =====================================================================
// DomainContext (LogisticsDomainContext.cs)
// =====================================================================

/** Static domain context for Logistics. Mirrors C# `LogisticsDomainContext`. */
object LogisticsDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Logistics] Expert logistics and supply chain assistant. Help with route optimisation, " +
            "fleet maintenance scheduling, customs documentation, incoterms, 3PL management, warehouse layout, " +
            "and last-mile delivery strategy. Apply cost-per-km and load efficiency metrics. Compliance: RTMS, " +
            "SARS customs regulations, AARTO, POPIA."

    val complianceFlags: List<String> = listOf("RTMS", "SARS_Customs", "AARTO", "POPIA", "Incoterms_2020")

    val suggestedTools: List<String> = listOf("route_planner", "fleet_tracker", "customs_portal", "analytics")
}

// =====================================================================
// CompanionAdapter (LogisticsCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Logistics snippet + helpers. Mirrors C# `LogisticsCompanionAdapter`. */
class LogisticsCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${LogisticsDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun optimiseRouteAsync(origin: String, destinations: String, constraints: String): String =
        inner.agentAsync("Optimise delivery routes from $origin to: $destinations. Constraints: $constraints. Minimise total distance and time while respecting load limits and delivery windows.")

    suspend fun prepareCustomsDocAsync(shipmentDetails: String, incoterm: String): String =
        inner.agentAsync("Prepare a customs documentation checklist for: $shipmentDetails. Incoterm: $incoterm. Include required forms, HS codes guidance, and SARS requirements.")

    suspend fun draftCustomsDeclarationAsync(goodsDescription: String, fromCountry: String, toCountry: String): String =
        inner.agentAsync("Draft a customs declaration outline for: $goodsDescription from $fromCountry to $toCountry. HS code lookup, duty, docs list.")

    suspend fun diagnoseDelayAsync(shipmentDetails: String, delayCause: String): String =
        inner.agentAsync("Diagnose this shipment delay: $shipmentDetails, cause: $delayCause. List recovery options + customer comms template.")

    suspend fun planWarehouseSlottingAsync(skuVelocityList: String, warehouseLayout: String): String =
        inner.agentAsync("Plan warehouse slotting for SKUs: $skuVelocityList in layout: $warehouseLayout. Optimise for pick-distance + ergonomics.")
}
