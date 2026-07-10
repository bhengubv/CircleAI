// Telnyx.kt
//
// Kotlin port of CircleAI.Telephony.Telnyx (TelnyxOptions.cs, TelnyxCarrier.cs,
// TelnyxCallSession.cs) — the C# reference is the EXACT spec. A Telnyx v2 REST
// adapter: Bearer-token auth, the /v2 namespace, and the Call Control surface for
// number provisioning + outbound dial + termination + transfer.
//
// The C# adapter uses HttpClient; the Kotlin port routes through the injected
// `TelephonyHttpTransport`. Wire shape preserved exactly: /v2/available_phone_numbers
// search + /v2/number_orders purchase, PATCH /v2/call_control_applications/{id} +
// PATCH /v2/phone_numbers/{n} for inbound, POST /v2/calls with
// {connection_id,to,from,stream_url,stream_track:"both_tracks",timeout_secs[,answering_machine_detection:"detect"]},
// POST /v2/calls/{id}/actions/hangup, POST /v2/calls/{id}/actions/transfer, and the
// same JSON reads (data[0].phone_number, data.call_control_id). Fail-soft when the
// API key is missing.

package com.bhengubv.circleai.telephony.telnyx

import com.bhengubv.circleai.telephony.AudioFrame
import com.bhengubv.circleai.telephony.BriefingSynthesiser
import com.bhengubv.circleai.telephony.CallDirection
import com.bhengubv.circleai.telephony.CallInfo
import com.bhengubv.circleai.telephony.CallMediaFormat
import com.bhengubv.circleai.telephony.CallStatus
import com.bhengubv.circleai.telephony.DefaultWarmTransferOrchestrator
import com.bhengubv.circleai.telephony.DtmfEvent
import com.bhengubv.circleai.telephony.DtmfToneGenerator
import com.bhengubv.circleai.telephony.ICallSession
import com.bhengubv.circleai.telephony.IDtmfSendable
import com.bhengubv.circleai.telephony.IMediaStream
import com.bhengubv.circleai.telephony.ITelephonyCarrier
import com.bhengubv.circleai.telephony.OutboundDialOptions
import com.bhengubv.circleai.telephony.ProvisionedNumber
import com.bhengubv.circleai.telephony.TelephonyHttpRequest
import com.bhengubv.circleai.telephony.TelephonyHttpTransport
import com.bhengubv.circleai.telephony.TransferMode
import com.bhengubv.circleai.telephony.WarmTransferRequest
import com.bhengubv.circleai.telephony.internalJsonParse
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.math.BigDecimal
import java.net.URI
import java.net.URLEncoder
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList

/**
 * Telnyx account credentials + Call Control application id. Mirrors C# `TelnyxOptions`.
 * Empty key → fail-soft.
 */
data class TelnyxOptions(
    /** Telnyx v2 API base address. Default https://api.telnyx.com. */
    val baseAddress: URI = URI("https://api.telnyx.com"),

    /** Telnyx v2 API key (Bearer). Found in the portal under "API Keys". */
    val apiKey: String? = null,

    /**
     * (Optional) Telnyx Call Control Application id used as the Connection for outbound
     * calls and as the webhook owner for inbound calls. Required to dial.
     */
    val callControlConnectionId: String? = null,
)

/**
 * [ITelephonyCarrier] backed by Telnyx's v2 REST API. Fail-soft when credentials are
 * missing. Mirrors C# `TelnyxCarrier`.
 */
class TelnyxCarrier(
    private val http: TelephonyHttpTransport,
    private val options: TelnyxOptions,
) : ITelephonyCarrier {

    private val baseHeaders: Map<String, String> =
        if (isConfigured) mapOf("Authorization" to "Bearer ${options.apiKey}") else emptyMap()

    override val carrierId: String get() = "telnyx"

    override val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    private fun url(path: String): URI = options.baseAddress.resolve(path)

    override suspend fun provisionNumberAsync(countryCode: String, areaCode: String?): ProvisionedNumber {
        ensureConfigured()

        // 1) Search availability.
        var searchPath = "/v2/available_phone_numbers?filter[country_code]=$countryCode&filter[limit]=1"
        if (!areaCode.isNullOrBlank()) {
            searchPath += "&filter[national_destination_code]=${enc(areaCode)}"
        }

        val searchResp = http.sendAsync(TelephonyHttpRequest("GET", url(searchPath), baseHeaders))
        require(searchResp.isSuccess) { "Telnyx availability search failed: ${searchResp.statusCode}" }

        val data = internalJsonParse(searchResp.body).jsonObject["data"]?.jsonArray
            ?: kotlinx.serialization.json.JsonArray(emptyList())
        val first = data.firstOrNull()
            ?: throw IllegalStateException("Telnyx has no available numbers in country='$countryCode', areaCode='$areaCode'.")
        val firstObj = first.jsonObject
        val phoneNumber = firstObj["phone_number"]!!.jsonPrimitive.content

        // 2) Place a Number Order to purchase it.
        val orderBody = """{"phone_numbers":[{"phone_number":"$phoneNumber"}]}"""
        val orderResp = http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = url("/v2/number_orders"),
                headers = baseHeaders + ("Content-Type" to "application/json"),
                body = orderBody,
            ),
        )
        require(orderResp.isSuccess) { "Telnyx number order failed: ${orderResp.statusCode}" }

        return ProvisionedNumber(
            phoneNumber = phoneNumber,
            carrierId = carrierId,
            provisionedAtUtc = Instant.now(),
            monthlyRecurringCost = parseMonthlyCost(firstObj) ?: BigDecimal.ZERO,
        )
    }

    override suspend fun configureInboundWebhookAsync(phoneNumber: String, inboundWebhook: URI) {
        ensureConfigured()
        if (options.callControlConnectionId.isNullOrBlank()) {
            throw IllegalStateException(
                "Telnyx configureInboundWebhook requires callControlConnectionId on TelnyxOptions.",
            )
        }

        // Update the Call Control Application's webhook URL.
        val appPath = "/v2/call_control_applications/${options.callControlConnectionId}"
        val appBody = """{"webhook_event_url":"$inboundWebhook"}"""
        val resp = http.sendAsync(
            TelephonyHttpRequest(
                method = "PATCH",
                uri = url(appPath),
                headers = baseHeaders + ("Content-Type" to "application/json"),
                body = appBody,
            ),
        )
        require(resp.isSuccess) { "Telnyx configure webhook failed: ${resp.statusCode}" }

        // Ensure the number is assigned to this connection (best-effort).
        val assignBody = """{"connection_id":"${options.callControlConnectionId}"}"""
        val assignPath = "/v2/phone_numbers/${enc(phoneNumber)}"
        http.sendAsync(
            TelephonyHttpRequest(
                method = "PATCH",
                uri = url(assignPath),
                headers = baseHeaders + ("Content-Type" to "application/json"),
                body = assignBody,
            ),
        )
    }

    override suspend fun dialAsync(
        fromNumber: String,
        toNumber: String,
        streamUrl: URI,
        options: OutboundDialOptions?,
    ): ICallSession {
        ensureConfigured()
        if (this.options.callControlConnectionId.isNullOrBlank()) {
            throw IllegalStateException("Telnyx dialAsync requires callControlConnectionId on TelnyxOptions.")
        }
        val opts = options ?: OutboundDialOptions()

        val body = buildString {
            append("{")
            append("\"connection_id\":\"${this@TelnyxCarrier.options.callControlConnectionId}\",")
            append("\"to\":\"$toNumber\",")
            append("\"from\":\"${opts.callerIdOverride ?: fromNumber}\",")
            append("\"stream_url\":\"$streamUrl\",")
            append("\"stream_track\":\"both_tracks\",")
            append("\"timeout_secs\":${opts.ringTimeoutSeconds}")
            if (opts.detectAnsweringMachine) {
                append(",\"answering_machine_detection\":\"detect\"")
            }
            append("}")
        }

        val resp = http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = url("/v2/calls"),
                headers = baseHeaders + ("Content-Type" to "application/json"),
                body = body,
            ),
        )
        require(resp.isSuccess) { "Telnyx dial failed: ${resp.statusCode}" }
        val callControlId = internalJsonParse(resp.body).jsonObject["data"]!!
            .jsonObject["call_control_id"]!!.jsonPrimitive.content

        val pending = TelnyxPendingMediaStream(
            CallInfo(
                callId = callControlId,
                direction = CallDirection.Outbound,
                from = fromNumber,
                to = toNumber,
                carrierId = carrierId,
                mediaFormat = CallMediaFormat.Pcm16000,
                startedAtUtc = Instant.now(),
            ),
        )
        return TelnyxCallSession(pending, this)
    }

    override suspend fun listNumbersAsync(): List<ProvisionedNumber> {
        if (!isConfigured) return emptyList()

        val resp = http.sendAsync(TelephonyHttpRequest("GET", url("/v2/phone_numbers?page[size]=100"), baseHeaders))
        if (!resp.isSuccess) return emptyList()

        val arr = internalJsonParse(resp.body).jsonObject["data"]?.jsonArray ?: return emptyList()
        return arr.map {
            ProvisionedNumber(
                phoneNumber = it.jsonObject["phone_number"]!!.jsonPrimitive.content,
                carrierId = carrierId,
                provisionedAtUtc = Instant.now(),
                monthlyRecurringCost = BigDecimal.ZERO,
            )
        }
    }

    /** Hang up an in-progress call. Used by sessions on HangUp. */
    internal suspend fun endCallAsync(callControlId: String) {
        if (!isConfigured) return
        http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = url("/v2/calls/$callControlId/actions/hangup"),
                headers = baseHeaders + ("Content-Type" to "application/json"),
                body = "{}",
            ),
        )
    }

    /** Transfer an in-progress call to a new destination. */
    internal suspend fun transferCallAsync(callControlId: String, targetNumber: String) {
        ensureConfigured()
        http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = url("/v2/calls/$callControlId/actions/transfer"),
                headers = baseHeaders + ("Content-Type" to "application/json"),
                body = """{"to":"$targetNumber"}""",
            ),
        )
    }

    private fun ensureConfigured() {
        if (!isConfigured) {
            throw IllegalStateException(
                "Telnyx carrier is not configured. Set TelnyxOptions.apiKey before calling REST operations.",
            )
        }
    }

    companion object {
        private fun enc(s: String): String = URLEncoder.encode(s, "UTF-8")

        private fun parseMonthlyCost(obj: kotlinx.serialization.json.JsonObject): BigDecimal? {
            val cost = obj["cost_information"]?.jsonObject ?: return null
            val monthly = cost["monthly_cost"] ?: return null
            return monthly.jsonPrimitive.contentOrNull?.toBigDecimalOrNull()
        }
    }
}

/**
 * [ICallSession] wrapping a Telnyx media stream. Termination + transfer via Call
 * Control REST actions. Mirrors C# `TelnyxCallSession`.
 */
class TelnyxCallSession(
    private val media: IMediaStream,
    private val carrier: TelnyxCarrier,
    private val briefingTts: BriefingSynthesiser? = null,
    private val bridgeStreamUrl: URI? = null,
) : ICallSession {

    private val statusListeners = CopyOnWriteArrayList<(CallStatus) -> Unit>()

    @Volatile
    private var statusField: CallStatus = CallStatus.Ringing

    private val mediaListener: (CallStatus) -> Unit = { setStatus(it) }

    init {
        media.onStatusChanged(mediaListener)
    }

    override val info: CallInfo get() = media.callInfo

    override val status: CallStatus
        get() = if (media.currentStatus == CallStatus.Ringing && statusField != CallStatus.Ringing) {
            statusField
        } else {
            media.currentStatus
        }

    override fun onStatusChanged(listener: (CallStatus) -> Unit) {
        statusListeners.add(listener)
    }

    override fun removeStatusChanged(listener: (CallStatus) -> Unit) {
        statusListeners.remove(listener)
    }

    override fun receiveAudioAsync(): Flow<AudioFrame> = media.receiveAudioAsync()
    override fun receiveDtmfAsync(): Flow<DtmfEvent> = media.receiveDtmfAsync()

    override suspend fun sendAudioAsync(frame: AudioFrame) = media.sendAudioAsync(frame)

    override suspend fun sendDtmfAsync(digits: String) {
        if (digits.isEmpty()) return
        val native = media as? IDtmfSendable
        if (native != null) {
            native.sendDtmfAsync(digits)
            return
        }
        val sampleRate = when (info.mediaFormat) {
            CallMediaFormat.Pcm16000 -> 16000
            CallMediaFormat.Pcm24000 -> 24000
            CallMediaFormat.Mulaw8000 -> 8000
            else -> 8000
        }
        DtmfToneGenerator.sendThroughSessionAsync(this, digits, sampleRate)
    }

    override suspend fun transferAsync(targetNumber: String, mode: TransferMode, briefing: String?) {
        if (mode == TransferMode.Warm) {
            if (briefingTts != null && bridgeStreamUrl != null && !briefing.isNullOrBlank()) {
                val orchestrator = DefaultWarmTransferOrchestrator(carrier, briefingTts)
                val result = orchestrator.executeAsync(
                    WarmTransferRequest(this, targetNumber, briefing, bridgeStreamUrl),
                )
                if (!result.succeeded) {
                    throw IllegalStateException("Warm transfer failed: ${result.failureReason}")
                }
                return
            }
        }

        carrier.transferCallAsync(info.callId, targetNumber)
        setStatus(CallStatus.Transferred)
    }

    override suspend fun hangUpAsync() {
        setStatus(CallStatus.EndedByAgent)
        runCatching { media.endAsync() }
        carrier.endCallAsync(info.callId)
    }

    override suspend fun disposeAsync() {
        media.removeStatusChanged(mediaListener)
        media.disposeAsync()
    }

    private fun setStatus(status: CallStatus) {
        if (statusField == status) return
        statusField = status
        for (l in statusListeners) l(status)
    }
}

/** Pending media stream returned while the host's WebSocket attaches. Mirrors C# `TelnyxPendingMediaStream`. */
class TelnyxPendingMediaStream(override val callInfo: CallInfo) : IMediaStream {

    private val statusListeners = CopyOnWriteArrayList<(CallStatus) -> Unit>()

    @Volatile
    private var statusField: CallStatus = CallStatus.Ringing

    override val currentStatus: CallStatus get() = statusField

    override fun onStatusChanged(listener: (CallStatus) -> Unit) {
        statusListeners.add(listener)
    }

    override fun removeStatusChanged(listener: (CallStatus) -> Unit) {
        statusListeners.remove(listener)
    }

    override fun receiveAudioAsync(): Flow<AudioFrame> = flow { }

    override suspend fun sendAudioAsync(frame: AudioFrame): Unit =
        throw IllegalStateException("Cannot send audio before the host's WebSocket has attached its IMediaStream.")

    override fun receiveDtmfAsync(): Flow<DtmfEvent> = flow { }

    override suspend fun endAsync() {
        statusField = CallStatus.EndedByAgent
        for (l in statusListeners) l(statusField)
    }

    override suspend fun disposeAsync() {}
}
