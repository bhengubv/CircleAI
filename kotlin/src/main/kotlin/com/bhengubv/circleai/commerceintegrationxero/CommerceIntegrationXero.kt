// CommerceIntegrationXero.kt
//
// Kotlin port of CircleAI.Commerce.Integration.Xero (XeroPrimitives.cs +
// CommerceIntegrationXeroDomainContext.cs +
// CommerceIntegrationXeroCompanionAdapter.cs) — the C# reference is the EXACT
// spec. Xero integration primitives — token storage, tenant tracking, webhook
// recorder. HTTP plumbing is host-supplied.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `DateTimeOffset` -> `java.time.Instant`.
//   * C# `ConcurrentDictionary<string,_>` (Ordinal) -> `ConcurrentHashMap`;
//     tenant lists + the event list live behind a lock (mirrors `List` + `lock`).
//   * `TokensExpired` returns true when no tokens are stored, else now >= expiry.
//   * `AddTenant` de-duplicates by TenantId per user.
//   * `TenantsFor` returns a snapshot copy; `RecentEvents` orders by AtUtc DESC
//     then takes `limit`.

package com.bhengubv.circleai.commerceintegrationxero

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (XeroPrimitives.cs)
// =====================================================================

/** Stored OAuth token set. Mirrors C# `XeroTokens`. */
data class XeroTokens(val accessToken: String, val refreshToken: String, val expiresAtUtc: Instant, val idToken: String)

/** A connected Xero tenant/organisation. Mirrors C# `XeroTenant`. */
data class XeroTenant(val tenantId: String, val tenantName: String, val tenantType: String)

/** A received Xero webhook event. Mirrors C# `XeroWebhookEvent`. */
data class XeroWebhookEvent(val tenantId: String, val resourceType: String, val resourceId: String, val atUtc: Instant)

/** Deterministic Xero integration board. Mirrors C# `IXeroBoard`. */
interface IXeroBoard {
    fun storeTokens(userId: String, t: XeroTokens)
    fun getTokens(userId: String): XeroTokens?
    fun tokensExpired(userId: String, now: Instant): Boolean
    fun addTenant(userId: String, t: XeroTenant)
    fun tenantsFor(userId: String): List<XeroTenant>
    fun recordWebhook(e: XeroWebhookEvent)
    fun recentEvents(limit: Int = 20): List<XeroWebhookEvent>
}

/** In-memory [IXeroBoard]. Mirrors C# `InMemoryXeroBoard`. */
class InMemoryXeroBoard : IXeroBoard {
    private val tokens = ConcurrentHashMap<String, XeroTokens>()
    private val tenants = ConcurrentHashMap<String, MutableList<XeroTenant>>()
    private val events = mutableListOf<XeroWebhookEvent>()
    private val lock = Any()

    override fun storeTokens(userId: String, t: XeroTokens) { tokens[userId] = t }
    override fun getTokens(userId: String): XeroTokens? = tokens[userId]

    override fun tokensExpired(userId: String, now: Instant): Boolean {
        val t = tokens[userId] ?: return true
        return !now.isBefore(t.expiresAtUtc)
    }

    override fun addTenant(userId: String, t: XeroTenant) {
        synchronized(lock) {
            val list = tenants.getOrPut(userId) { mutableListOf() }
            if (list.none { it.tenantId == t.tenantId }) list.add(t)
        }
    }

    override fun tenantsFor(userId: String): List<XeroTenant> =
        synchronized(lock) { tenants[userId]?.toList() ?: emptyList() }

    override fun recordWebhook(e: XeroWebhookEvent) { synchronized(lock) { events.add(e) } }

    override fun recentEvents(limit: Int): List<XeroWebhookEvent> =
        synchronized(lock) { events.sortedByDescending { it.atUtc }.take(limit) }
}

// =====================================================================
// DomainContext (CommerceIntegrationXeroDomainContext.cs)
// =====================================================================

/** Static domain context for Commerce.Integration.Xero. Mirrors C# `CommerceIntegrationXeroDomainContext`. */
object CommerceIntegrationXeroDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Commerce.Integration.Xero] You are a Xero accounting platform expert. " +
            "Help with Xero chart of accounts, invoice creation, bank feeds, reconciliation workflows, " +
            "Xero reporting, and API integration troubleshooting. Reference Xero HQ documentation for accuracy. " +
            "Compliance: SARS, IFRS for SMEs, Xero data handling standards."

    val complianceFlags: List<String> = listOf("SARS", "IFRS", "Xero_Data_Standards", "POPIA")

    val suggestedTools: List<String> = listOf("xero_api", "spreadsheet", "document_editor")
}

// =====================================================================
// CompanionAdapter (CommerceIntegrationXeroCompanionAdapter.cs)
// =====================================================================

/**
 * Wraps an [ICompanionSession] with the Commerce.Integration.Xero snippet +
 * helpers. Mirrors C# `CommerceIntegrationXeroCompanionAdapter`.
 */
class CommerceIntegrationXeroCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${CommerceIntegrationXeroDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun explainXeroCodeAsync(transactionCode: String): String =
        inner.agentAsync("Explain Xero transaction code '$transactionCode' and suggest the correct account code mapping under South African chart of accounts.")

    suspend fun troubleshootBankFeedAsync(feedError: String): String =
        inner.agentAsync("Troubleshoot this Xero bank feed error and provide resolution steps:\n$feedError")

    suspend fun generateXeroReportingGuideAsync(businessType: String): String =
        inner.agentAsync("Generate a Xero reporting guide for a $businessType. Include recommended reports, frequency, and key metrics to track.")

    suspend fun mapTransactionToXeroAsync(transactionDescription: String): String =
        inner.agentAsync("Map this transaction to a Xero entry: $transactionDescription. Pick contact, account code, tax rate; output the API payload outline.")

    suspend fun resolveXeroErrorAsync(xeroErrorJson: String): String =
        inner.agentAsync("Resolve this Xero API error: $xeroErrorJson. Explain the root cause + the exact fix (header, scope, validation, etc.).")

    suspend fun generateXeroReportPromptAsync(reportType: String, period: String): String =
        inner.agentAsync("Generate the Xero report request for a $reportType for $period. Include endpoint, query params, response fields to surface.")

    suspend fun mapVatToXeroTaxRateAsync(countryIso: String, supplyType: String): String =
        inner.agentAsync("Map this VAT context to the correct Xero tax-rate code: country $countryIso, supply $supplyType. Show the code + a one-line justification.")
}
