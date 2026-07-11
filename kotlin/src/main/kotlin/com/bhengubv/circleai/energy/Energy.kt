// Energy.kt
//
// Kotlin port of CircleAI.Energy (EnergyPrimitives.cs + EnergyDomainContext.cs +
// EnergyCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory energy board: meter readings, tariffs, and outages.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `decimal` -> `BigDecimal`;
//     `DateTimeOffset` -> `Instant`.
//   * `ReadingsFor` = readings at/after `since` for the meter, ASC.
//   * `TotalKwhSince` = last.Kwh − first.Kwh over the window (0 when < 2 rows).
//   * `EstimateCost` = kwh × tariff.PeakKwhRate, computed in double then widened
//     to BigDecimal (matches C#'s `(decimal)(kwh * rate)`); unknown tariff throws.
//   * `ActiveOutages` = outages with no EndUtc.

package com.bhengubv.circleai.energy

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
// Primitives (EnergyPrimitives.cs)
// =====================================================================

/** A meter reading. Mirrors C# `MeterReading`. */
data class MeterReading(val meterId: String, val kwh: Double, val atUtc: Instant)

/** An energy tariff. Mirrors C# `EnergyTariff`. */
data class EnergyTariff(val tariffId: String, val name: String, val peakKwhRate: Double, val offPeakKwhRate: Double, val currency: String)

/** An outage. Mirrors C# `Outage`. */
data class Outage(val outageId: String, val area: String, val startUtc: Instant, val endUtc: Instant?, val reason: String?)

/** Deterministic energy board. Mirrors C# `IEnergyBoard`. */
interface IEnergyBoard {
    fun record(r: MeterReading)
    fun readingsFor(meterId: String, since: Instant): List<MeterReading>
    fun totalKwhSince(meterId: String, since: Instant): Double
    fun setTariff(t: EnergyTariff)
    fun getTariff(id: String): EnergyTariff?
    fun estimateCost(meterId: String, tariffId: String, since: Instant): BigDecimal
    fun logOutage(o: Outage)
    fun activeOutages(): List<Outage>
}

/** In-memory [IEnergyBoard]. Mirrors C# `InMemoryEnergyBoard`. */
class InMemoryEnergyBoard : IEnergyBoard {
    private val readings = mutableListOf<MeterReading>()
    private val tariffs = ConcurrentHashMap<String, EnergyTariff>()
    private val outages = ConcurrentHashMap<String, Outage>()
    private val lock = Any()

    override fun record(r: MeterReading) { synchronized(lock) { readings.add(r) } }
    override fun readingsFor(meterId: String, since: Instant): List<MeterReading> = synchronized(lock) {
        readings.filter { it.meterId == meterId && !it.atUtc.isBefore(since) }.sortedBy { it.atUtc }
    }

    override fun totalKwhSince(meterId: String, since: Instant): Double {
        val rows = readingsFor(meterId, since)
        if (rows.size < 2) return 0.0
        return rows[rows.size - 1].kwh - rows[0].kwh
    }

    override fun setTariff(t: EnergyTariff) { tariffs[t.tariffId] = t }
    override fun getTariff(id: String): EnergyTariff? = tariffs[id]

    override fun estimateCost(meterId: String, tariffId: String, since: Instant): BigDecimal {
        val t = tariffs[tariffId] ?: throw IllegalStateException("Unknown tariff $tariffId")
        val kwh = totalKwhSince(meterId, since)
        return BigDecimal.valueOf(kwh * t.peakKwhRate)
    }

    override fun logOutage(o: Outage) { outages[o.outageId] = o }
    override fun activeOutages(): List<Outage> = outages.values.filter { it.endUtc == null }
}

// =====================================================================
// DomainContext (EnergyDomainContext.cs)
// =====================================================================

/** Static domain context for Energy. Mirrors C# `EnergyDomainContext`. */
object EnergyDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Energy] Expert energy management and renewable energy assistant. Help with solar/wind " +
            "feasibility, load flow analysis, tariff optimisation, battery storage sizing, grid connection " +
            "requirements, and energy efficiency audits. Apply NERSA and SABS standards. Compliance: " +
            "Electricity Act, NERSA regulations, Municipal By-laws, Renewable Energy IPP."

    val complianceFlags: List<String> = listOf("Electricity_Act", "NERSA", "SABS", "Municipal_Energy_By_laws", "POPIA")

    val suggestedTools: List<String> = listOf("energy_model", "analytics", "document_editor", "web_search")
}

// =====================================================================
// CompanionAdapter (EnergyCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Energy snippet + helpers. Mirrors C# `EnergyCompanionAdapter`. */
class EnergyCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${EnergyDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun sizeSolarSystemAsync(monthlyConsumptionKwh: String, location: String, gridTied: Boolean): String =
        inner.agentAsync("Size a solar PV system for $monthlyConsumptionKwh kWh/month in $location. Grid-tied: $gridTied. Include panel capacity, inverter size, battery sizing (if off-grid), estimated generation, and payback period.")

    suspend fun analyseTariffAsync(tariffSchedule: String, consumptionProfile: String): String =
        inner.agentAsync("Analyse this tariff schedule for cost optimisation opportunities:\n$tariffSchedule\nConsumption profile:\n$consumptionProfile\nRecommend demand management and TOU strategies.")

    suspend fun optimiseTariffChoiceAsync(usagePattern: String, availableTariffs: String): String =
        inner.agentAsync("Recommend the best tariff for usage $usagePattern from: $availableTariffs. Show annual cost compare + breakeven assumptions.")

    suspend fun explainBillSpikeAsync(priorBill: String, currentBill: String, conditions: String): String =
        inner.agentAsync("Explain bill change from $priorBill to $currentBill. Conditions: $conditions. Cover usage, tariff, weather, meter issues.")

    suspend fun planSolarSizingAsync(averageDailyKwh: String, roofOrientation: String, budget: String): String =
        inner.agentAsync("Size a solar PV system for $averageDailyKwh kWh/day, $roofOrientation, budget $budget. Output panels, inverter, battery, payback years.")

    suspend fun draftLoadSheddingPlanAsync(householdSize: String, criticalLoads: String): String =
        inner.agentAsync("Draft a load-shedding plan for $householdSize-person home, critical: $criticalLoads. Cover backup priority, run-time budget, safety.")
}
