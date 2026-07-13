// Agriculture.kt
//
// Kotlin port of CircleAI.Agriculture (AgriculturePrimitives.cs +
// AgricultureDomainContext.cs + AgricultureCompanionAdapter.cs) — the C#
// reference is the EXACT spec. A deterministic in-memory farm board: fields,
// crops, and yield records.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTime`/`DateTime?` -> `Instant`/`Instant?`.
//   * `CropsForField` ordered by PlantedOn ASC.
//   * `AvgYieldOfVariety` = mean t/ha across yields whose crop matches the variety
//     (case-insensitive); 0.0 when none.

package com.bhengubv.circleai.agriculture

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (AgriculturePrimitives.cs)
// =====================================================================

/** A field. Mirrors C# `Field`. */
data class Field(val fieldId: String, val areaHa: Double, val soilType: String, val irrigationKind: String)

/** A planted crop. Mirrors C# `Crop`. */
data class Crop(val cropId: String, val fieldId: String, val variety: String, val plantedOn: Instant, val expectedHarvest: Instant?)

/** A yield record. Mirrors C# `YieldRecord`. */
data class YieldRecord(val cropId: String, val tonsPerHa: Double, val harvestedOn: Instant)

/** Deterministic farm board. Mirrors C# `IFarmBoard`. */
interface IFarmBoard {
    fun addField(f: Field)
    fun plant(c: Crop)
    fun recordYield(y: YieldRecord)
    fun getField(id: String): Field?
    fun cropsForField(fieldId: String): List<Crop>
    fun avgYieldOfVariety(variety: String): Double
}

/** In-memory [IFarmBoard]. Mirrors C# `InMemoryFarmBoard`. */
class InMemoryFarmBoard : IFarmBoard {
    private val fields = ConcurrentHashMap<String, Field>()
    private val crops = ConcurrentHashMap<String, Crop>()
    private val yields = mutableListOf<YieldRecord>()
    private val lock = Any()

    override fun addField(f: Field) { fields[f.fieldId] = f }
    override fun plant(c: Crop) { crops[c.cropId] = c }
    override fun recordYield(y: YieldRecord) { synchronized(lock) { yields.add(y) } }
    override fun getField(id: String): Field? = fields[id]

    override fun cropsForField(fieldId: String): List<Crop> =
        crops.values.filter { it.fieldId == fieldId }.sortedBy { it.plantedOn }

    override fun avgYieldOfVariety(variety: String): Double = synchronized(lock) {
        val rows = yields.filter { y ->
            val c = crops[y.cropId]
            c != null && c.variety.equals(variety, ignoreCase = true)
        }
        if (rows.isEmpty()) 0.0 else rows.map { it.tonsPerHa }.average()
    }

    /** Number of fields registered. */
    val fieldCount: Int get() = fields.size

    /** Remove a field by id. Returns true if one was present. */
    fun removeField(fieldId: String): Boolean = fields.remove(fieldId) != null

    /** Total area (ha) across every field. */
    fun totalAreaHa(): Double = fields.values.sumOf { it.areaHa }

    /** Fields of a given soil type (case-insensitive), largest area first. */
    fun fieldsBySoil(soilType: String): List<Field> =
        fields.values.filter { it.soilType.equals(soilType, ignoreCase = true) }
            .sortedByDescending { it.areaHa }

    /** Crops whose expected harvest is on or before [asOf], earliest first. */
    fun dueForHarvest(asOf: Instant): List<Crop> =
        crops.values.filter { c -> c.expectedHarvest?.let { !it.isAfter(asOf) } ?: false }
            .sortedBy { it.expectedHarvest }

    /**
     * The variety with the highest mean yield (t/ha) across recorded yields whose
     * crop is known, grouped case-insensitively; null when there are no yields.
     * Returns the original casing of the variety.
     */
    fun bestYieldingVariety(): String? = synchronized(lock) {
        yields.filter { crops.containsKey(it.cropId) }
            .groupBy { crops.getValue(it.cropId).variety.lowercase(java.util.Locale.US) }
            .map { (_, rows) ->
                val variety = crops.getValue(rows.first().cropId).variety
                variety to rows.map { it.tonsPerHa }.average()
            }
            .maxByOrNull { it.second }
            ?.first
    }
}

// =====================================================================
// DomainContext (AgricultureDomainContext.cs)
// =====================================================================

/** Static domain context for Agriculture. Mirrors C# `AgricultureDomainContext`. */
object AgricultureDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Agriculture] Expert agricultural advisor. Help with crop planning, soil management, pest " +
            "and disease identification, livestock health, market price analysis, irrigation scheduling, and " +
            "agri-finance applications. Adapt advice to the specific region, climate zone, and crop type. " +
            "Compliance: DAFF regulations, Conservation of Agricultural Resources Act, POPIA."

    val complianceFlags: List<String> = listOf("DAFF_regs", "CARA", "Fertilizer_Act", "POPIA")

    val suggestedTools: List<String> = listOf("weather_api", "market_prices", "soil_data", "document_editor")
}

// =====================================================================
// CompanionAdapter (AgricultureCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Agriculture snippet + helpers. Mirrors C# `AgricultureCompanionAdapter`. */
class AgricultureCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${AgricultureDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun diagnosePestAsync(cropType: String, symptoms: String): String =
        inner.agentAsync("Diagnose this crop problem and recommend treatment. Crop: $cropType. Symptoms: $symptoms. Include integrated pest management (IPM) options and registered chemical controls.")

    suspend fun planCropRotationAsync(farmContext: String, seasons: Int): String =
        inner.agentAsync("Design a $seasons-season crop rotation plan for: $farmContext. Optimise soil health, disease break cycles, and profitability.")

    suspend fun diagnoseCropIssueAsync(crop: String, symptoms: String, region: String): String =
        inner.agentAsync("Diagnose this $crop issue in $region: $symptoms. Cover likely pests/disease/deficiency, confidence, and an integrated-pest-management plan.")

    suspend fun optimisePlantingScheduleAsync(crop: String, climate: String, areaHa: Double): String =
        inner.agentAsync("Plan planting for ${areaHa}ha of $crop in $climate. Include sowing dates, density, irrigation, fertiliser, and harvest window.")

    suspend fun estimateYieldAsync(crop: String, areaHa: Double, conditions: String): String =
        inner.agentAsync("Estimate yield (t/ha and total tons) for ${areaHa}ha of $crop under: $conditions. Show baseline, best, worst case.")

    suspend fun draftSustainabilityReportAsync(operationSummary: String): String =
        inner.agentAsync("Draft a sustainability report for: $operationSummary. Cover soil health, water use, biodiversity, GHG, and SDG alignment.")
}
