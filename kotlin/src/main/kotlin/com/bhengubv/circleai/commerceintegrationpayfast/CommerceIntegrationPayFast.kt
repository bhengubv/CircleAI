// CommerceIntegrationPayFast.kt
//
// Kotlin port of CircleAI.Commerce.Integration.PayFast (PayFastPrimitives.cs +
// CommerceIntegrationPayFastDomainContext.cs +
// CommerceIntegrationPayFastCompanionAdapter.cs) — the C# reference is the
// EXACT spec. Real PayFast signature builder, ITN validation, and an in-memory
// webhook recorder. The HTTP-side callbacks are wired by the host.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `decimal` -> `java.math.BigDecimal`.
//   * C# `ConcurrentDictionary`/`List` -> a plain list behind a lock.
//   * SIGNATURE PARITY (load-bearing): C# builds the string with
//     `WebUtility.UrlEncode(value).Replace("%20","+")` per field, appends
//     `passphrase=` when a passphrase is set (else trims the trailing '&'), then
//     MD5-hashes and lower-cases the hex. [payFastUrlEncode] reproduces
//     `WebUtility.UrlEncode` byte-for-byte — verified against a .NET 10 probe:
//       unreserved = A-Z a-z 0-9 ! ( ) * - . _  ; space -> '+' ; all else ->
//       UTF-8 bytes as uppercase %XX (e.g. '&'->%26, 'é'->%C3%A9). The
//       `.Replace("%20","+")` is a documented no-op (space already becomes '+')
//       and is applied verbatim for literal fidelity.
//   * `VerifyItn` accepts iff the payload MerchantId matches the config.
//   * `RecentWebhooks` returns the most-recent-first slice (reverse insertion
//     order), capped at `limit`.

package com.bhengubv.circleai.commerceintegrationpayfast

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.math.BigDecimal
import java.security.MessageDigest

// =====================================================================
// Primitives (PayFastPrimitives.cs)
// =====================================================================

/** PayFast merchant configuration. Mirrors C# `PayFastConfig`. */
data class PayFastConfig(val merchantId: String, val merchantKey: String, val passphrase: String, val sandbox: Boolean)

/** A PayFast ITN (Instant Transaction Notification) payload. Mirrors C# `PayFastItnPayload`. */
data class PayFastItnPayload(
    val merchantId: String,
    val paymentId: String,
    val paymentStatus: String,
    val amount: BigDecimal,
    val mPaymentId: String,
    val signature: String,
)

/** Deterministic PayFast integration board. Mirrors C# `IPayFastBoard`. */
interface IPayFastBoard {
    val config: PayFastConfig
    fun signatureFor(orderedFields: Map<String, String>): String
    fun verifyItn(p: PayFastItnPayload): Boolean
    fun recordWebhook(p: PayFastItnPayload)
    fun recentWebhooks(limit: Int = 20): List<PayFastItnPayload>
}

/**
 * Faithful port of C# `System.Net.WebUtility.UrlEncode` followed by the
 * `.Replace("%20","+")` the reference applies. Encodes UTF-8 bytes; leaves the
 * unreserved set `A-Za-z0-9!()*-._` untouched; maps space to '+' and every
 * other byte to an uppercase `%XX` escape.
 */
internal fun payFastUrlEncode(value: String): String {
    val sb = StringBuilder()
    for (b in value.toByteArray(Charsets.UTF_8)) {
        val c = b.toInt() and 0xFF
        when {
            c == ' '.code -> sb.append('+')
            c in 'A'.code..'Z'.code ||
                c in 'a'.code..'z'.code ||
                c in '0'.code..'9'.code ||
                c == '!'.code || c == '('.code || c == ')'.code || c == '*'.code ||
                c == '-'.code || c == '.'.code || c == '_'.code ->
                sb.append(c.toChar())
            else -> {
                sb.append('%')
                sb.append("0123456789ABCDEF"[(c ushr 4) and 0xF])
                sb.append("0123456789ABCDEF"[c and 0xF])
            }
        }
    }
    // Reference applies this defensively; space already encodes to '+', so it is a no-op.
    return sb.toString().replace("%20", "+")
}

/** In-memory [IPayFastBoard]. Mirrors C# `InMemoryPayFastBoard`. */
class InMemoryPayFastBoard(override val config: PayFastConfig) : IPayFastBoard {
    private val webhooks = mutableListOf<PayFastItnPayload>()
    private val lock = Any()

    override fun signatureFor(orderedFields: Map<String, String>): String {
        val sb = StringBuilder()
        for ((k, v) in orderedFields) {
            sb.append(k).append('=').append(payFastUrlEncode(v)).append('&')
        }
        if (config.passphrase.isNotEmpty()) {
            sb.append("passphrase=").append(payFastUrlEncode(config.passphrase))
        } else if (sb.isNotEmpty() && sb[sb.length - 1] == '&') {
            sb.setLength(sb.length - 1)
        }
        val hash = MessageDigest.getInstance("MD5").digest(sb.toString().toByteArray(Charsets.UTF_8))
        return hash.joinToString("") { "%02x".format(it) }
    }

    override fun verifyItn(p: PayFastItnPayload): Boolean = p.merchantId == config.merchantId

    override fun recordWebhook(p: PayFastItnPayload) { synchronized(lock) { webhooks.add(p) } }

    override fun recentWebhooks(limit: Int): List<PayFastItnPayload> =
        synchronized(lock) { webhooks.asReversed().take(limit) }
}

// =====================================================================
// DomainContext (CommerceIntegrationPayFastDomainContext.cs)
// =====================================================================

/** Static domain context for Commerce.Integration.PayFast. Mirrors C# `CommerceIntegrationPayFastDomainContext`. */
object CommerceIntegrationPayFastDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Commerce.Integration.PayFast] You are a PayFast payment gateway integration expert. " +
            "Help with PayFast ITN (Instant Transaction Notification) webhook handling, payment flow debugging, " +
            "refund processing, subscription billing, split payments, and PCI-DSS compliance guidance. " +
            "Compliance: PCI-DSS, POPIA, PASA, Consumer Protection Act."

    val complianceFlags: List<String> = listOf("PCI_DSS", "POPIA", "PASA", "Consumer_Protection_Act")

    val suggestedTools: List<String> = listOf("payfast_api", "webhook_debugger", "document_editor")
}

// =====================================================================
// CompanionAdapter (CommerceIntegrationPayFastCompanionAdapter.cs)
// =====================================================================

/**
 * Wraps an [ICompanionSession] with the Commerce.Integration.PayFast snippet +
 * helpers. Mirrors C# `CommerceIntegrationPayFastCompanionAdapter`.
 */
class CommerceIntegrationPayFastCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${CommerceIntegrationPayFastDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun diagnoseItnAsync(itnPayload: String): String =
        inner.agentAsync("Diagnose this PayFast ITN payload. Validate signature, check payment_status, and identify any issues:\n$itnPayload")

    suspend fun guideRefundAsync(transactionId: String, reason: String): String =
        inner.agentAsync("Guide me through processing a PayFast refund for transaction $transactionId. Reason: $reason. Include API call, required fields, and customer communication.")

    suspend fun reviewIntegrationAsync(codeSnippet: String): String =
        inner.agentAsync("Review this PayFast integration code for security, PCI-DSS compliance, and correctness:\n$codeSnippet")

    suspend fun explainItnStatusAsync(itnPayload: String): String =
        inner.agentAsync("Decode this PayFast ITN payload and explain its status: $itnPayload. Cover payment_status, m_payment_id, signature validity.")

    suspend fun draftPayFastBuyButtonAsync(itemName: String, amount: BigDecimal, returnUrl: String): String =
        inner.agentAsync("Draft a PayFast Buy Button form for '$itemName' at $amount, return to $returnUrl. Include all required fields + signature placeholder.")

    suspend fun troubleshootSignatureMismatchAsync(requestParams: String): String =
        inner.agentAsync("Troubleshoot a PayFast signature mismatch. Request params: $requestParams. List the 5 most common causes + how to verify each.")

    suspend fun reconcilePayoutAsync(payoutId: String, expectedAmount: BigDecimal, actualAmount: BigDecimal): String =
        inner.agentAsync("Reconcile PayFast payout $payoutId: expected $expectedAmount, actual $actualAmount. List likely fee / refund / hold reasons.")
}
