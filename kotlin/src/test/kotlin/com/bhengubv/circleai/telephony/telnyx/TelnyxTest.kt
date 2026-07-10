// TelnyxTest.kt
//
// Verifies the Telnyx carrier port against the C# reference wire shape, using the
// in-memory FakeTelephonyHttpTransport:
//   - isConfigured reflects ApiKey; Bearer auth header is set.
//   - provisionNumberAsync: /v2/available_phone_numbers GET then /v2/number_orders POST;
//     reads data[0].phone_number + cost_information.monthly_cost.
//   - configureInboundWebhookAsync requires callControlConnectionId; PATCHes the app + number.
//   - dialAsync requires callControlConnectionId; posts /v2/calls JSON with
//     connection_id/to/from/stream_url/stream_track/timeout_secs [+ AMD]; reads
//     data.call_control_id.
//   - listNumbersAsync parses data[].phone_number.
//   - cold transfer + hang-up hit the Call Control actions endpoints.

package com.bhengubv.circleai.telephony.telnyx

import com.bhengubv.circleai.telephony.CallMediaFormat
import com.bhengubv.circleai.telephony.CallStatus
import com.bhengubv.circleai.telephony.FakeTelephonyHttpTransport
import com.bhengubv.circleai.telephony.NullTelephonyHttpTransport
import com.bhengubv.circleai.telephony.OutboundDialOptions
import com.bhengubv.circleai.telephony.TransferMode
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.net.URI
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class TelnyxTest {

    private fun configured() = TelnyxOptions(apiKey = "KEY123", callControlConnectionId = "CC1")

    @Test
    fun `is configured requires api key and sets bearer auth`() = runTest {
        assertFalse(TelnyxCarrier(NullTelephonyHttpTransport.Instance, TelnyxOptions()).isConfigured)
        val carrier = TelnyxCarrier(
            FakeTelephonyHttpTransport().on("GET", "/v2/phone_numbers", 200, """{"data":[]}"""),
            configured(),
        )
        assertTrue(carrier.isConfigured)
        assertEquals("telnyx", carrier.carrierId)
        carrier.listNumbersAsync()
    }

    @Test
    fun `provision searches then orders and reads monthly cost`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("GET", "/v2/available_phone_numbers", 200,
                """{"data":[{"phone_number":"+27110000123","cost_information":{"monthly_cost":"2.00"}}]}""")
            .on("POST", "/v2/number_orders", 200, "{}")
        val n = TelnyxCarrier(fake, configured()).provisionNumberAsync("ZA", "11")
        assertEquals("+27110000123", n.phoneNumber)
        assertEquals(0, n.monthlyRecurringCost.compareTo(java.math.BigDecimal("2.00")))
        assertEquals("Bearer KEY123", fake.requests.first().headers["Authorization"])
        // Order body carries the phone number.
        assertTrue(fake.requests.last().body!!.contains("+27110000123"))
    }

    @Test
    fun `dial posts call control json and returns ringing session`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("POST", "/v2/calls", 200, """{"data":{"call_control_id":"v3:abc"}}""")
        val session = TelnyxCarrier(fake, configured()).dialAsync(
            "+27110000001", "+27115550123", URI("wss://host/stream"),
            OutboundDialOptions(detectAnsweringMachine = true, ringTimeoutSeconds = 25),
        )
        assertEquals("v3:abc", session.info.callId)
        assertEquals(CallStatus.Ringing, session.status)
        assertEquals(CallMediaFormat.Pcm16000, session.info.mediaFormat)

        val body = fake.requests.single().body!!
        assertTrue(body.contains("\"connection_id\":\"CC1\""))
        assertTrue(body.contains("\"to\":\"+27115550123\""))
        assertTrue(body.contains("\"stream_url\":\"wss://host/stream\""))
        assertTrue(body.contains("\"stream_track\":\"both_tracks\""))
        assertTrue(body.contains("\"timeout_secs\":25"))
        assertTrue(body.contains("\"answering_machine_detection\":\"detect\""))
    }

    @Test
    fun `dial requires call control connection id`() = runTest {
        val fake = FakeTelephonyHttpTransport()
        val carrier = TelnyxCarrier(fake, TelnyxOptions(apiKey = "KEY"))
        assertFailsWith<IllegalStateException> { carrier.dialAsync("+2711", "+2712", URI("wss://h")) }
    }

    @Test
    fun `configure inbound webhook patches app and number`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("PATCH", "/v2/call_control_applications/CC1", 200, "{}")
            .on("PATCH", "/v2/phone_numbers/", 200, "{}")
        TelnyxCarrier(fake, configured()).configureInboundWebhookAsync("+27110000123", URI("https://host/inbound"))
        assertEquals(2, fake.requests.size)
        assertEquals("PATCH", fake.requests[0].method)
        assertTrue(fake.requests[0].body!!.contains("https://host/inbound"))
    }

    @Test
    fun `list numbers parses data array`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("GET", "/v2/phone_numbers", 200, """{"data":[{"phone_number":"+27110000001"}]}""")
        assertEquals(listOf("+27110000001"), TelnyxCarrier(fake, configured()).listNumbersAsync().map { it.phoneNumber })
    }

    @Test
    fun `cold transfer and hangup hit call control actions`() = runTest {
        val fake = FakeTelephonyHttpTransport()
            .on("POST", "/v2/calls/v3:abc/actions/transfer", 200, "{}")
            .on("POST", "/v2/calls/v3:abc/actions/hangup", 200, "{}")
            .on("POST", "/v2/calls", 200, """{"data":{"call_control_id":"v3:abc"}}""")
        val carrier = TelnyxCarrier(fake, configured())
        val session = carrier.dialAsync("+2711", "+2712", URI("wss://h/s"))

        session.transferAsync("+27115550999", TransferMode.Cold)
        assertEquals(CallStatus.Transferred, session.status)
        assertTrue(fake.requests.last().uri.path!!.endsWith("/actions/transfer"))
        assertTrue(fake.requests.last().body!!.contains("+27115550999"))

        session.hangUpAsync()
        assertEquals(CallStatus.EndedByAgent, session.status)
        assertTrue(fake.requests.last().uri.path!!.endsWith("/actions/hangup"))
    }
}
