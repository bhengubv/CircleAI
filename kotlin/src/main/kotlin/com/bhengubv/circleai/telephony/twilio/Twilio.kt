// Twilio.kt
//
// Kotlin port of CircleAI.Telephony.Twilio (TwilioOptions.cs, TwilioCarrier.cs,
// TwilioCallSession.cs) — the C# reference is the EXACT spec. A Twilio-REST-backed
// ITelephonyCarrier: number provisioning, inbound webhook config, outbound dial, and
// call termination, authenticated via HTTP Basic (AccountSid + AuthToken).
//
// The C# adapter talks to HttpClient directly; the Kotlin port routes every request
// through the injected `TelephonyHttpTransport` (see Telephony.kt) so the adapter is
// deterministic + offline in tests. The wire shape is preserved exactly: same REST
// paths (/2010-04-01/Accounts/{Sid}/...), same form fields (PhoneNumber, VoiceUrl,
// From/To/Twiml/Timeout/MachineDetection, Status), same Basic-auth header, same inline
// <Connect><Stream/> TwiML, same JSON field reads (available_phone_numbers[0].phone_number,
// incoming_phone_numbers[].sid, sid). Fail-soft when credentials are missing.

package com.bhengubv.circleai.telephony.twilio

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
import kotlinx.serialization.json.JsonArray
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
 * Twilio account credentials + endpoint. Mirrors C# `TwilioOptions`. Empty key →
 * fail-soft (carrier reports [TwilioCarrier.isConfigured] = false; operations throw
 * a helpful message).
 */
data class TwilioOptions(
    /** Twilio REST API base address. Default https://api.twilio.com. */
    val baseAddress: URI = URI("https://api.twilio.com"),

    /** Twilio Account SID (starts with "AC..."). */
    val accountSid: String? = null,

    /** Twilio Auth Token. */
    val authToken: String? = null,
)

/**
 * [ITelephonyCarrier] backed by Twilio's REST API. Fail-soft when credentials are
 * missing. Mirrors C# `TwilioCarrier`.
 */
class TwilioCarrier(
    private val http: TelephonyHttpTransport,
    private val options: TwilioOptions,
) : ITelephonyCarrier {

    private val baseHeaders: Map<String, String> =
        if (isConfigured) {
            val creds = Base64.getEncoder()
                .encodeToString("${options.accountSid}:${options.authToken}".toByteArray(Charsets.UTF_8))
            mapOf("Authorization" to "Basic $creds")
        } else {
            emptyMap()
        }

    override val carrierId: String get() = "twilio"

    override val isConfigured: Boolean
        get() = !options.accountSid.isNullOrBlank() && !options.authToken.isNullOrBlank()

    private fun url(path: String): URI = options.baseAddress.resolve(path)

    override suspend fun provisionNumberAsync(countryCode: String, areaCode: String?): ProvisionedNumber {
        ensureConfigured()

        // GET AvailablePhoneNumbers/{Country}/Local.json to find one, then reserve.
        var path = "/2010-04-01/Accounts/${options.accountSid}/AvailablePhoneNumbers/$countryCode/Local.json"
        path += if (!areaCode.isNullOrBlank()) "?AreaCode=${enc(areaCode)}&Limit=1" else "?Limit=1"

        val availableResp = http.sendAsync(TelephonyHttpRequest("GET", url(path), baseHeaders))
        require(availableResp.isSuccess) { "Twilio available-numbers query failed: ${availableResp.statusCode}" }

        val root = internalJsonParse(availableResp.body).jsonObject
        val arr: JsonArray = root["available_phone_numbers"]?.jsonArray ?: JsonArray(emptyList())
        val first = arr.firstOrNull()
            ?: throw IllegalStateException("Twilio has no available numbers in country='$countryCode', areaCode='$areaCode'.")
        val firstObj = first.jsonObject
        val phoneNumber = firstObj["phone_number"]!!.jsonPrimitive.content

        // Reserve it on the account.
        val reservePath = "/2010-04-01/Accounts/${options.accountSid}/IncomingPhoneNumbers.json"
        val reserveResp = http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = url(reservePath),
                headers = baseHeaders + ("Content-Type" to "application/x-www-form-urlencoded"),
                body = form("PhoneNumber" to phoneNumber),
            ),
        )
        require(reserveResp.isSuccess) { "Twilio reserve number failed: ${reserveResp.statusCode}" }

        return ProvisionedNumber(
            phoneNumber = phoneNumber,
            carrierId = carrierId,
            provisionedAtUtc = Instant.now(),
            monthlyRecurringCost = parseDecimal(firstObj, "price") ?: BigDecimal.ZERO,
        )
    }

    override suspend fun configureInboundWebhookAsync(phoneNumber: String, inboundWebhook: URI) {
        ensureConfigured()

        // Find the SID of the IncomingPhoneNumber resource for this E.164 number.
        val listPath =
            "/2010-04-01/Accounts/${options.accountSid}/IncomingPhoneNumbers.json?PhoneNumber=${enc(phoneNumber)}"
        val listResp = http.sendAsync(TelephonyHttpRequest("GET", url(listPath), baseHeaders))
        require(listResp.isSuccess) { "Twilio number lookup failed: ${listResp.statusCode}" }

        val arr = internalJsonParse(listResp.body).jsonObject["incoming_phone_numbers"]?.jsonArray
            ?: JsonArray(emptyList())
        val entry = arr.firstOrNull()
            ?: throw IllegalStateException("Phone number '$phoneNumber' is not owned on this Twilio account.")
        val sid = entry.jsonObject["sid"]!!.jsonPrimitive.content

        val configPath = "/2010-04-01/Accounts/${options.accountSid}/IncomingPhoneNumbers/$sid.json"
        val updateResp = http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = url(configPath),
                headers = baseHeaders + ("Content-Type" to "application/x-www-form-urlencoded"),
                body = form("VoiceUrl" to inboundWebhook.toString(), "VoiceMethod" to "POST"),
            ),
        )
        require(updateResp.isSuccess) { "Twilio configure webhook failed: ${updateResp.statusCode}" }
    }

    override suspend fun dialAsync(
        fromNumber: String,
        toNumber: String,
        streamUrl: URI,
        options: OutboundDialOptions?,
    ): ICallSession {
        ensureConfigured()
        val opts = options ?: OutboundDialOptions()

        // Inline TwiML: <Connect><Stream url='wss://...'/></Connect>.
        val twiml =
            "<Response><Connect><Stream url='${htmlEncode(streamUrl.toString())}'/></Connect></Response>"

        val pairs = mutableListOf(
            "From" to (opts.callerIdOverride ?: fromNumber),
            "To" to toNumber,
            "Twiml" to twiml,
            "Timeout" to opts.ringTimeoutSeconds.toString(),
        )
        if (opts.detectAnsweringMachine) {
            pairs.add("MachineDetection" to "Enable")
        }

        val callsPath = "/2010-04-01/Accounts/${this.options.accountSid}/Calls.json"
        val resp = http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = url(callsPath),
                headers = baseHeaders + ("Content-Type" to "application/x-www-form-urlencoded"),
                body = form(*pairs.toTypedArray()),
            ),
        )
        require(resp.isSuccess) { "Twilio dial failed: ${resp.statusCode}" }
        val callSid = internalJsonParse(resp.body).jsonObject["sid"]!!.jsonPrimitive.content

        // The real ICallSession is materialised by the host once the Twilio Media
        // Streams WebSocket connects to streamUrl. We hand back a session shell rooted
        // on a pending media stream that the host's stream handler completes.
        val pending = TwilioPendingMediaStream(
            CallInfo(
                callId = callSid,
                direction = CallDirection.Outbound,
                from = fromNumber,
                to = toNumber,
                carrierId = carrierId,
                mediaFormat = CallMediaFormat.Mulaw8000,
                startedAtUtc = Instant.now(),
            ),
        )
        return TwilioCallSession(pending, this)
    }

    override suspend fun listNumbersAsync(): List<ProvisionedNumber> {
        if (!isConfigured) return emptyList()

        val path = "/2010-04-01/Accounts/${options.accountSid}/IncomingPhoneNumbers.json?PageSize=100"
        val resp = http.sendAsync(TelephonyHttpRequest("GET", url(path), baseHeaders))
        if (!resp.isSuccess) return emptyList()

        val arr = internalJsonParse(resp.body).jsonObject["incoming_phone_numbers"]?.jsonArray
            ?: return emptyList()
        return arr.map {
            ProvisionedNumber(
                phoneNumber = it.jsonObject["phone_number"]!!.jsonPrimitive.content,
                carrierId = carrierId,
                provisionedAtUtc = Instant.now(),
                monthlyRecurringCost = BigDecimal.ZERO,
            )
        }
    }

    /** Redirect an in-progress call to fresh TwiML. Used by sessions on cold transfer. */
    internal suspend fun redirectCallAsync(callSid: String, twiml: String) {
        ensureConfigured()
        val path = "/2010-04-01/Accounts/${options.accountSid}/Calls/$callSid.json"
        http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = url(path),
                headers = baseHeaders + ("Content-Type" to "application/x-www-form-urlencoded"),
                body = form("Twiml" to twiml),
            ),
        )
    }

    /** End a call by Twilio CallSid via the REST API. Used by sessions on HangUp. */
    internal suspend fun endCallAsync(callSid: String) {
        if (!isConfigured) return
        val path = "/2010-04-01/Accounts/${options.accountSid}/Calls/$callSid.json"
        http.sendAsync(
            TelephonyHttpRequest(
                method = "POST",
                uri = url(path),
                headers = baseHeaders + ("Content-Type" to "application/x-www-form-urlencoded"),
                body = form("Status" to "completed"),
            ),
        )
    }

    private fun ensureConfigured() {
        if (!isConfigured) {
            throw IllegalStateException(
                "Twilio carrier is not configured. Set TwilioOptions.accountSid and authToken before calling REST operations.",
            )
        }
    }

    companion object {
        private fun enc(s: String): String = URLEncoder.encode(s, "UTF-8")

        private fun form(vararg pairs: Pair<String, String>): String =
            pairs.joinToString("&") { (k, v) -> "${enc(k)}=${enc(v)}" }

        /** Minimal HTML entity encode for the TwiML url attribute — mirrors WebUtility.HtmlEncode. */
        private fun htmlEncode(s: String): String = s
            .replace("&", "&amp;")
            .replace("<", "&lt;")
            .replace(">", "&gt;")
            .replace("\"", "&quot;")
            .replace("'", "&#39;")

        private fun parseDecimal(obj: kotlinx.serialization.json.JsonObject, property: String): BigDecimal? {
            val p = obj[property] ?: return null
            val content = p.jsonPrimitive.contentOrNull ?: return null
            return content.toBigDecimalOrNull()
        }
    }
}

/**
 * [ICallSession] wrapping a Twilio media stream. Mirrors C# `TwilioCallSession`.
 * Audio/DTMF delegate to the media stream; termination + cold/warm transfer go through
 * the carrier REST API. When [briefingTts] + [bridgeStreamUrl] are supplied, a warm
 * transfer runs the full dial-brief-bridge flow via [DefaultWarmTransferOrchestrator].
 */
class TwilioCallSession(
    private val media: IMediaStream,
    private val carrier: TwilioCarrier,
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

    // Mirrors C#: prefer the session-tracked status once it has left Ringing while the
    // media stream is still Ringing; otherwise defer to the media stream.
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
        // Prefer carrier-native out-of-band DTMF (Twilio's JSON control frame) if the
        // host stream supports it; otherwise generate in-band tones over the audio channel.
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
            // Warm requested but no briefing pipeline configured — fall through to cold.
        }

        val transferTwiml = "<Response><Dial>${htmlEncode(targetNumber)}</Dial></Response>"
        carrier.redirectCallAsync(info.callId, transferTwiml)
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

    private companion object {
        fun htmlEncode(s: String): String = s
            .replace("&", "&amp;")
            .replace("<", "&lt;")
            .replace(">", "&gt;")
            .replace("\"", "&quot;")
            .replace("'", "&#39;")
    }
}

/**
 * [IMediaStream] for the moment between "carrier accepted dial" and "host's WebSocket
 * attached." Yields no audio. Calling send before attach raises a friendly error.
 * Mirrors C# `PendingMediaStream`.
 */
class TwilioPendingMediaStream(override val callInfo: CallInfo) : IMediaStream {

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
        throw IllegalStateException(
            "Cannot send audio before the host's WebSocket has attached its IMediaStream. Wire the Twilio host to complete the connection.",
        )

    override fun receiveDtmfAsync(): Flow<DtmfEvent> = flow { }

    override suspend fun endAsync() {
        statusField = CallStatus.EndedByAgent
        for (l in statusListeners) l(statusField)
    }

    override suspend fun disposeAsync() {}
}
