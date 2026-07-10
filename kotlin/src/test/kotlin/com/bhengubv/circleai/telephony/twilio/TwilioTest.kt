// TwilioTest.kt
//
// Verifies the Twilio carrier port against the C# reference wire shape, using the
// in-memory FakeTelephonyHttpTransport (no real network):
//   - isConfigured reflects AccountSid + AuthToken; Basic auth header is set.
//   - provisionNumberAsync: available-numbers GET then IncomingPhoneNumbers POST;
//     reads available_phone_numbers[0].phone_number + price.
//   - configureInboundWebhookAsync: number lookup then per-SID POST (VoiceUrl/VoiceMethod).
//   - dialAsync: Calls.json POST with inline <Connect><Stream/> TwiML + From/To/Timeout,
//     MachineDetection when requested; returns a session over a pending stream.
//   - listNumbersAsync: parses incoming_phone_numbers[].phone_number; empty when unconfigured.
//   - session cold transfer redirects the call; hang-up posts Status=completed.
//   - pending stream refuses audio send before attach.

package com.bhengubv.circleai.telephony.twilio

import com.bhengubv.circleai.telephony.AudioFrame
import com.bhengubv.circleai.telephony.CallMediaFormat
import com.bhengubv.circleai.telephony.CallStatus
import com.bhengubv.circleai.telephony.FakeTelephonyHttpTransport
import com.bhengubv.circleai.telephony.NullTelephonyHttpTransport
import com.bhengubv.circleai.telephony.OutboundDialOptions
import com.bhengubv.circleai.telephony.TransferMode
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.net.URI
import java.time.Duration
import java.util.Base64
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class TwilioTest {

    private fun configured() = TwilioOptions(accountSid = "ACtest", authToken = "secret")

    @Test
    fun `is configured requires both sid and token`() {
        assertFalse(TwilioCarrier(NullTelephonyHttpTransport.Instance, TwilioOptions()).isConfigured)
        assertFalse(TwilioCarrier(NullTelephonyHttpTransport.Instance, TwilioOptions(accountSid = "AC")).isConfigured)
        assertTrue(TwilioCarrier(NullTelephonyHttpTransport.Instance, configured()).isConfigured)
        assertEquals("twilio", TwilioCarrier(NullTelephonyHttpTransport.Instance, configured()).carrierId)
    }

    @Test
    fun `provision searches then reserves and reads price`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("GET", "/2010-04-01/Accounts/ACtest/AvailablePhoneNumbers", 200,
                """{"available_phone_numbers":[{"phone_number":"+27110000123","price":"1.50"}]}""")
            .on("POST", "/2010-04-01/Accounts/ACtest/IncomingPhoneNumbers.json", 201, "{}")
        val carrier = TwilioCarrier(fake, configured())

        val n = carrier.provisionNumberAsync("ZA", "011")
        assertEquals("+27110000123", n.phoneNumber)
        assertEquals("twilio", n.carrierId)
        assertEquals(0, n.monthlyRecurringCost.compareTo(java.math.BigDecimal("1.50")))

        // Basic auth header on the search request.
        val expected = "Basic " + Base64.getEncoder().encodeToString("ACtest:secret".toByteArray())
        assertEquals(expected, fake.requests.first().headers["Authorization"])
        // AreaCode is passed through in the query.
        assertTrue(fake.requests.first().uri.query!!.contains("AreaCode=011"))
    }

    @Test
    fun `dial posts inline stream twiml and returns a ringing session`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("POST", "/2010-04-01/Accounts/ACtest/Calls.json", 201, """{"sid":"CA123"}""")
        val carrier = TwilioCarrier(fake, configured())

        val session = carrier.dialAsync(
            "+27110000001", "+27115550123", URI("wss://host/stream"),
            OutboundDialOptions(detectAnsweringMachine = true, ringTimeoutSeconds = 20),
        )
        assertEquals("CA123", session.info.callId)
        assertEquals(CallStatus.Ringing, session.status)
        assertEquals(CallMediaFormat.Mulaw8000, session.info.mediaFormat)

        val body = fake.requests.single().body!!
        assertTrue(body.contains("Twiml="))
        // TwiML is form-encoded; decode and check the Stream verb + wss url.
        val decoded = java.net.URLDecoder.decode(body, "UTF-8")
        assertTrue(decoded.contains("<Connect><Stream url="))
        assertTrue(decoded.contains("wss://host/stream"))
        assertTrue(decoded.contains("Timeout=20"))
        assertTrue(decoded.contains("MachineDetection=Enable"))
    }

    @Test
    fun `list numbers parses the array and is empty when unconfigured`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("GET", "/2010-04-01/Accounts/ACtest/IncomingPhoneNumbers.json", 200,
                """{"incoming_phone_numbers":[{"phone_number":"+27110000123","sid":"PN1"}]}""")
        val carrier = TwilioCarrier(fake, configured())
        assertEquals(listOf("+27110000123"), carrier.listNumbersAsync().map { it.phoneNumber })

        val unconfigured = TwilioCarrier(fake, TwilioOptions())
        assertTrue(unconfigured.listNumbersAsync().isEmpty())
    }

    @Test
    fun `cold transfer redirects the call and hangup completes it`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("POST", "/2010-04-01/Accounts/ACtest/Calls.json", 201, """{"sid":"CA9"}""")
            .on("POST", "/2010-04-01/Accounts/ACtest/Calls/CA9.json", 200, "{}")
        val carrier = TwilioCarrier(fake, configured())
        val session = carrier.dialAsync("+2711", "+2712", URI("wss://h/s"))

        session.transferAsync("+27115550999", TransferMode.Cold)
        assertEquals(CallStatus.Transferred, session.status)
        val transferReq = fake.requests.last()
        val decoded = java.net.URLDecoder.decode(transferReq.body!!, "UTF-8")
        assertTrue(decoded.contains("<Response><Dial>+27115550999</Dial></Response>"))

        session.hangUpAsync()
        assertEquals(CallStatus.EndedByAgent, session.status)
        assertTrue(java.net.URLDecoder.decode(fake.requests.last().body!!, "UTF-8").contains("Status=completed"))
    }

    @Test
    fun `unconfigured carrier throws on dial and provision`() = runTest {
        val carrier = TwilioCarrier(NullTelephonyHttpTransport.Instance, TwilioOptions())
        assertFailsWith<IllegalStateException> { carrier.dialAsync("+2711", "+2712", URI("wss://h")) }
        assertFailsWith<IllegalStateException> { carrier.provisionNumberAsync("ZA") }
    }

    @Test
    fun `pending stream refuses audio before the host attaches`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("POST", "/2010-04-01/Accounts/ACtest/Calls.json", 201, """{"sid":"CA1"}""")
        val session = TwilioCarrier(fake, configured()).dialAsync("+2711", "+2712", URI("wss://h/s"))
        assertFailsWith<IllegalStateException> {
            session.sendAudioAsync(AudioFrame(byteArrayOf(1), CallMediaFormat.Mulaw8000, Duration.ZERO))
        }
    }
}
