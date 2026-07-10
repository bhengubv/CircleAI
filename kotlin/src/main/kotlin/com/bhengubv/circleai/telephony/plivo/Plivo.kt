// Plivo.kt
//
// Kotlin port of CircleAI.Telephony.Plivo (PlivoOptions.cs, PlivoCarrier.cs,
// PlivoCallSession.cs) — the C# reference is the EXACT spec. A Plivo v1 REST adapter:
// Basic auth (AuthId + AuthToken), the /v1/Account/{AuthId}/ namespace, and the
// AnswerUrl-driven Audio Streaming flow.
//
// The C# adapter uses HttpClient; the Kotlin port routes through the injected
// `TelephonyHttpTransport`. Wire shape preserved exactly: GET PhoneNumber/ search +
// POST PhoneNumber/{n}/ buy, POST Number/{n}/ (answer_url/answer_method) for inbound,
// POST Call/ with from/to/answer_url(+?stream=<wss>)/answer_method/ring_timeout
// [+machine_detection], DELETE Call/{uuid}/ hangup, POST Call/{uuid}/ (data: aleg_url
// XML) transfer, and the same JSON reads (objects[0].number, request_uuid). Fail-soft
// when AuthId/AuthToken are missing.

package com.bhengubv.circleai.telephony.plivo

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
import java.util.Base64
import java.util.concurrent.CopyOnWriteArrayList

/**
 * Plivo account credentials + AnswerUrl base for media-stream XML. Mirrors C#
 * `PlivoOptions`. Empty AuthId/AuthToken → fail-soft.
 */
data class PlivoOptions(
    /** Plivo v1 API base address. Default https://api.plivo.com. */
    val baseAddress: URI = URI("https://api.plivo.com"),

    /** Plivo Auth ID (starts with "MA..." or similar). */
    val authId: String? = null,

    /** Plivo Auth Token. */
    val authToken: String? = null,

    /**
     * (Required for dial) HTTPS URL the host serves that, given a
     * `?stream=<url-encoded wss://...>` query parameter, returns Plivo XML containing
     * the matching `<Stream/>` verb.
     */
    val answerUrlBase: URI? = null,
)

/**
 * [ITelephonyCarrier] backed by Plivo's v1 REST API. Fail-soft when credentials are
 * missing. Mirrors C# `PlivoCarrier`.
 */
class PlivoCarrier(
    private val http: TelephonyHttpTransport,
    private val options: PlivoOptions,
) : ITelephonyCarrier {

    private val baseHeaders: Map<String, String> =
        if (isConfigured) {
            val creds = Base64.getEncoder()
                .encodeToString("${options.authId}:${options.authToken}".toByteArray(Charsets.UTF_8))
            mapOf("Authorization" to "Basic $creds")
        } else {
            emptyMap()
        }

    override val carrierId: String get() = "plivo"

    override val isConfigured: Boolean
        get() = !options.authId.isNullOrBlank() && !options.authToken.isNullOrBlank()

    private fun url(path: String): URI = options.baseAddress.resolve(path)

    override suspend fun provisionNumberAsync(countryCode: String, areaCode: String?): ProvisionedNumber {
        ensureConfigured()

        // GET PhoneNumber/?country_iso={cc}&limit=1[&pattern={area}]
        var path = "/v1/Account/${options.authId}/PhoneNumber/?country_iso=$countryCode&limit=1"
        if (!areaCode.isNullOrBlank()) {
            path += "&pattern=${enc(areaCode)}"
        }

        val searchResp = http.sendAsync(TelephonyHttpRequest("GET", url(path), baseHeaders))
        require(searchResp.isSuccess) { "Plivo availability search failed: ${searchResp.statusCode}" }

        val objects = internalJsonParse(searchResp.body).jsonObject["objects"]?.jsonArray
            ?: kotlinx.serialization.json.JsonArray(emptyList())
        val first = objects.firstOrNull()
            ?: throw IllegalStateException("Plivo has no available numbers in country='$countryCode', areaCode='$areaCode'.")
        val firstObj = first.jsonObject
        val phoneNumber = firstObj["number"]!!.jsonPrimitive.content

        // POST PhoneNumber/{number}/ — buy it.
        val buyPath = "/v1/Account/${options.authId}/PhoneNumber/$phoneNumber/"
        val buyResp = http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = url(buyPath),
                headers = baseHeaders + ("Content-Type" to "application/x-www-form-urlencoded"),
                body = form("app_id" to ""),
            ),
        )
        require(buyResp.isSuccess) { "Plivo buy number failed: ${buyResp.statusCode}" }

        return ProvisionedNumber(
            phoneNumber = phoneNumber,
            carrierId = carrierId,
            provisionedAtUtc = Instant.now(),
            monthlyRecurringCost = parseDecimal(firstObj, "monthly_rental_rate") ?: BigDecimal.ZERO,
        )
    }

    override suspend fun configureInboundWebhookAsync(phoneNumber: String, inboundWebhook: URI) {
        ensureConfigured()

        // Plivo uses POST for updates on the Number/ resource.
        val path = "/v1/Account/${options.authId}/Number/$phoneNumber/"
        val resp = http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = url(path),
                headers = baseHeaders + ("Content-Type" to "application/x-www-form-urlencoded"),
                body = form("answer_url" to inboundWebhook.toString(), "answer_method" to "POST"),
            ),
        )
        require(resp.isSuccess) { "Plivo configure webhook failed: ${resp.statusCode}" }
    }

    override suspend fun dialAsync(
        fromNumber: String,
        toNumber: String,
        streamUrl: URI,
        options: OutboundDialOptions?,
    ): ICallSession {
        ensureConfigured()
        val answerBase = this.options.answerUrlBase
            ?: throw IllegalStateException(
                "Plivo dialAsync requires PlivoOptions.answerUrlBase. The host must serve XML containing a <Stream/> verb pointing to the streamUrl.",
            )
        val opts = options ?: OutboundDialOptions()

        // Compose the answer URL with the stream wss:// embedded as a query param.
        val answerUrl = composeAnswerUrl(answerBase, streamUrl)

        val pairs = mutableListOf(
            "from" to (opts.callerIdOverride ?: fromNumber),
            "to" to toNumber,
            "answer_url" to answerUrl,
            "answer_method" to "POST",
            "ring_timeout" to opts.ringTimeoutSeconds.toString(),
        )
        if (opts.detectAnsweringMachine) {
            pairs.add("machine_detection" to "true")
        }

        val path = "/v1/Account/${this.options.authId}/Call/"
        val resp = http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = url(path),
                headers = baseHeaders + ("Content-Type" to "application/x-www-form-urlencoded"),
                body = form(*pairs.toTypedArray()),
            ),
        )
        require(resp.isSuccess) { "Plivo dial failed: ${resp.statusCode}" }
        val requestUuid = internalJsonParse(resp.body).jsonObject["request_uuid"]!!.jsonPrimitive.content

        val pending = PlivoPendingMediaStream(
            CallInfo(
                callId = requestUuid,
                direction = CallDirection.Outbound,
                from = fromNumber,
                to = toNumber,
                carrierId = carrierId,
                mediaFormat = CallMediaFormat.Mulaw8000,
                startedAtUtc = Instant.now(),
            ),
        )
        return PlivoCallSession(pending, this)
    }

    override suspend fun listNumbersAsync(): List<ProvisionedNumber> {
        if (!isConfigured) return emptyList()

        val path = "/v1/Account/${options.authId}/Number/?limit=100"
        val resp = http.sendAsync(TelephonyHttpRequest("GET", url(path), baseHeaders))
        if (!resp.isSuccess) return emptyList()

        val arr = internalJsonParse(resp.body).jsonObject["objects"]?.jsonArray ?: return emptyList()
        return arr.map {
            ProvisionedNumber(
                phoneNumber = it.jsonObject["number"]!!.jsonPrimitive.content,
                carrierId = carrierId,
                provisionedAtUtc = Instant.now(),
                monthlyRecurringCost = BigDecimal.ZERO,
            )
        }
    }

    /** Hang up an in-progress call. Used by sessions on HangUp. */
    internal suspend fun endCallAsync(callUuid: String) {
        if (!isConfigured) return
        http.sendAsync(
            TelephonyHttpRequest(
                method = "DELETE",
                uri = url("/v1/Account/${options.authId}/Call/$callUuid/"),
                headers = baseHeaders,
            ),
        )
    }

    /** Transfer an in-progress call by replaying the answer XML. */
    internal suspend fun transferCallAsync(callUuid: String, targetNumber: String) {
        ensureConfigured()
        val alegXml = "<Response><Dial><Number>$targetNumber</Number></Dial></Response>"
        val alegUrl = "data:application/xml,${enc(alegXml)}"
        http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = url("/v1/Account/${options.authId}/Call/$callUuid/"),
                headers = baseHeaders + ("Content-Type" to "application/x-www-form-urlencoded"),
                body = form("aleg_url" to alegUrl, "aleg_method" to "POST"),
            ),
        )
    }

    private fun ensureConfigured() {
        if (!isConfigured) {
            throw IllegalStateException(
                "Plivo carrier is not configured. Set PlivoOptions.authId and authToken before calling REST operations.",
            )
        }
    }

    companion object {
        private fun enc(s: String): String = URLEncoder.encode(s, "UTF-8")

        private fun form(vararg pairs: Pair<String, String>): String =
            pairs.joinToString("&") { (k, v) -> "${enc(k)}=${enc(v)}" }

        /**
         * Append `stream=<url-encoded streamUrl>` to the answer base, preserving any
         * existing query. Reproduces C# `UriBuilder` semantics: the query string is
         * assembled verbatim and NOT re-quoted (the multi-arg java.net.URI constructor
         * would double-encode the `%` escapes, so the string is built directly).
         */
        internal fun composeAnswerUrl(answerBase: URI, streamUrl: URI): String {
            val existingQuery = answerBase.rawQuery?.trimStart('?') ?: ""
            val separator = if (existingQuery.isEmpty()) "" else "&"
            val newQuery = existingQuery + separator + "stream=" + enc(streamUrl.toString())

            val sb = StringBuilder()
            answerBase.scheme?.let { sb.append(it).append("://") }
            answerBase.authority?.let { sb.append(it) }
            answerBase.rawPath?.let { sb.append(it) }
            sb.append('?').append(newQuery)
            answerBase.rawFragment?.let { sb.append('#').append(it) }
            return sb.toString()
        }

        private fun parseDecimal(obj: kotlinx.serialization.json.JsonObject, property: String): BigDecimal? {
            val p = obj[property] ?: return null
            return p.jsonPrimitive.contentOrNull?.toBigDecimalOrNull()
        }
    }
}

/** [ICallSession] wrapping a Plivo media stream. Mirrors C# `PlivoCallSession`. */
class PlivoCallSession(
    private val media: IMediaStream,
    private val carrier: PlivoCarrier,
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

/** Pending stream returned while the host's WebSocket attaches. Mirrors C# `PlivoPendingMediaStream`. */
class PlivoPendingMediaStream(override val callInfo: CallInfo) : IMediaStream {

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
