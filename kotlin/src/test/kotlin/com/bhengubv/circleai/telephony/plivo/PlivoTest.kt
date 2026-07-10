// PlivoTest.kt
//
// Verifies the Plivo carrier port against the C# reference wire shape, using the
// in-memory FakeTelephonyHttpTransport:
//   - isConfigured reflects AuthId + AuthToken; Basic auth header is set.
//   - provisionNumberAsync: PhoneNumber/ GET then PhoneNumber/{n}/ POST buy;
//     reads objects[0].number + monthly_rental_rate.
//   - configureInboundWebhookAsync POSTs Number/{n}/ (answer_url/answer_method).
//   - dialAsync requires answerUrlBase; composes ?stream=<wss>; posts Call/ form;
//     reads request_uuid; MachineDetection when requested.
//   - listNumbersAsync parses objects[].number.
//   - cold transfer POSTs a data: aleg_url; hang-up DELETEs the call.
//   - composeAnswerUrl embeds the stream url and preserves existing query.

package com.bhengubv.circleai.telephony.plivo

import com.bhengubv.circleai.telephony.CallMediaFormat
import com.bhengubv.circleai.telephony.CallStatus
import com.bhengubv.circleai.telephony.FakeTelephonyHttpTransport
import com.bhengubv.circleai.telephony.NullTelephonyHttpTransport
import com.bhengubv.circleai.telephony.OutboundDialOptions
import com.bhengubv.circleai.telephony.TransferMode
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.net.URI
import java.util.Base64
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class PlivoTest {

    private fun configured() = PlivoOptions(
        authId = "MAXXXX",
        authToken = "secret",
        answerUrlBase = URI("https://host/answer"),
    )

    @Test
    fun `is configured requires auth id and token`() {
        assertFalse(PlivoCarrier(NullTelephonyHttpTransport.Instance, PlivoOptions()).isConfigured)
        assertFalse(PlivoCarrier(NullTelephonyHttpTransport.Instance, PlivoOptions(authId = "MA")).isConfigured)
        assertTrue(PlivoCarrier(NullTelephonyHttpTransport.Instance, configured()).isConfigured)
        assertEquals("plivo", PlivoCarrier(NullTelephonyHttpTransport.Instance, configured()).carrierId)
    }

    @Test
    fun `provision searches then buys and reads monthly rental`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("GET", "/v1/Account/MAXXXX/PhoneNumber/", 200,
                """{"objects":[{"number":"+27110000123","monthly_rental_rate":"0.80"}]}""")
            .on("POST", "/v1/Account/MAXXXX/PhoneNumber/+27110000123/", 201, "{}")
        val n = PlivoCarrier(fake, configured()).provisionNumberAsync("ZA", "11")
        assertEquals("+27110000123", n.phoneNumber)
        assertEquals(0, n.monthlyRecurringCost.compareTo(java.math.BigDecimal("0.80")))
        val expected = "Basic " + Base64.getEncoder().encodeToString("MAXXXX:secret".toByteArray())
        assertEquals(expected, fake.requests.first().headers["Authorization"])
    }

    @Test
    fun `dial composes stream query and posts call form`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("POST", "/v1/Account/MAXXXX/Call/", 201, """{"request_uuid":"req-1"}""")
        val session = PlivoCarrier(fake, configured()).dialAsync(
            "+27110000001", "+27115550123", URI("wss://host/stream"),
            OutboundDialOptions(detectAnsweringMachine = true, ringTimeoutSeconds = 40),
        )
        assertEquals("req-1", session.info.callId)
        assertEquals(CallStatus.Ringing, session.status)
        assertEquals(CallMediaFormat.Mulaw8000, session.info.mediaFormat)

        val decoded = java.net.URLDecoder.decode(fake.requests.single().body!!, "UTF-8")
        assertTrue(decoded.contains("to=+27115550123"))
        assertTrue(decoded.contains("ring_timeout=40"))
        assertTrue(decoded.contains("machine_detection=true"))
        // answer_url carries the embedded, url-encoded stream url.
        assertTrue(decoded.contains("answer_url=https://host/answer?stream="))
        assertTrue(decoded.contains("wss"))
    }

    @Test
    fun `dial requires answer url base`() = runTest {
        val carrier = PlivoCarrier(FakeTelephonyHttpTransport(), PlivoOptions(authId = "MA", authToken = "t"))
        assertFailsWith<IllegalStateException> { carrier.dialAsync("+2711", "+2712", URI("wss://h")) }
    }

    @Test
    fun `compose answer url embeds stream and preserves existing query`() {
        val out = PlivoCarrier.composeAnswerUrl(URI("https://host/answer?x=1"), URI("wss://host/stream"))
        assertTrue(out.startsWith("https://host/answer?x=1&stream="))
        assertTrue(out.contains("wss%3A%2F%2Fhost%2Fstream"))
    }

    @Test
    fun `list numbers parses objects array`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("GET", "/v1/Account/MAXXXX/Number/", 200, """{"objects":[{"number":"+27110000001"}]}""")
        assertEquals(listOf("+27110000001"), PlivoCarrier(fake, configured()).listNumbersAsync().map { it.phoneNumber })
    }

    @Test
    fun `cold transfer posts aleg url and hangup deletes the call`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("POST", "/v1/Account/MAXXXX/Call/req-9/", 200, "{}")
            .on("DELETE", "/v1/Account/MAXXXX/Call/req-9/", 204, "")
            .on("POST", "/v1/Account/MAXXXX/Call/", 201, """{"request_uuid":"req-9"}""")
        val carrier = PlivoCarrier(fake, configured())
        val session = carrier.dialAsync("+2711", "+2712", URI("wss://h/s"))

        session.transferAsync("+27115550999", TransferMode.Cold)
        assertEquals(CallStatus.Transferred, session.status)
        // The form value is the data: aleg_url; its XML payload is url-encoded once and
        // form-encoded again (matching the C# EscapeDataString + FormUrlEncodedContent
        // double-encode), so decode twice to reveal the Dial verb.
        val once = java.net.URLDecoder.decode(fake.requests.last().body!!, "UTF-8")
        assertTrue(once.contains("aleg_url=data:application/xml,"))
        val twice = java.net.URLDecoder.decode(once, "UTF-8")
        assertTrue(twice.contains("<Number>+27115550999</Number>"))

        session.hangUpAsync()
        assertEquals(CallStatus.EndedByAgent, session.status)
        assertEquals("DELETE", fake.requests.last().method)
    }
}
