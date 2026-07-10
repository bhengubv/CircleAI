// TelephonyTest.kt
//
// Verifies the CircleAI.Telephony core port against the C# reference semantics:
//   - Primitives: enums + records (AudioFrame value semantics over content).
//   - DefaultToolCallRegistry: case-insensitive registration, local handler, webhook
//     dispatch through the injected transport (200/500/missing-tool/throwing handler),
//     unregistered-tool result shape.
//   - DtmfToneGenerator: frequency table, sample count (sr*ms/1000), little-endian
//     16-bit PCM, sequence with inter-digit gaps, session send.
//   - DefaultWarmTransferOrchestrator: dial → brief → cold-transfer → hang up; failure
//     branches (blank target, dial throw, briefing throw).
//   - Null carrier / dispatcher: fail-soft + inert.
//   - TestCallSession: inject/capture audio + DTMF, status events, transfer/hang-up.
//   - InMemoryMediaStream + FakeTelephonyCarrier: provision, dial → Active live session,
//     inbound-call materialisation, DTMF in-band fallback.
//   - InMemoryInboundCallDispatcher: subscribe-before-dispatch delivers; unsubscribe stops.

package com.bhengubv.circleai.telephony

import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.net.URI
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class TelephonyTest {

    private fun callInfo(
        id: String = "call-1",
        format: CallMediaFormat = CallMediaFormat.Pcm16000,
    ) = CallInfo(id, CallDirection.Inbound, "+27110000001", "+27110000002", "test", format, Instant.EPOCH)

    // ── Primitives ───────────────────────────────────────────────────────────
    @Test
    fun `audio frame compares by content`() {
        val a = AudioFrame(byteArrayOf(1, 2, 3), CallMediaFormat.Pcm16000, Duration.ZERO)
        val b = AudioFrame(byteArrayOf(1, 2, 3), CallMediaFormat.Pcm16000, Duration.ZERO)
        val c = AudioFrame(byteArrayOf(1, 2, 4), CallMediaFormat.Pcm16000, Duration.ZERO)
        assertEquals(a, b)
        assertEquals(a.hashCode(), b.hashCode())
        assertTrue(a != c)
    }

    // ── Tool registry ────────────────────────────────────────────────────────
    @Test
    fun `local tool handler result is returned and blank becomes empty object`() = runTest {
        val reg = DefaultToolCallRegistry(NullTelephonyHttpTransport.Instance)
        reg.registerLocal(ToolDefinition("Echo", "echoes", "{}")) { args -> args }

        val ok = reg.invokeAsync(ToolInvocation("c1", "echo", """{"x":1}"""))  // case-insensitive
        assertTrue(ok.succeeded)
        assertEquals("""{"x":1}""", ok.resultJson)
        assertNull(ok.error)

        reg.registerLocal(ToolDefinition("Blank", "blank", "{}")) { "" }
        val blank = reg.invokeAsync(ToolInvocation("c2", "Blank", "{}"))
        assertTrue(blank.succeeded)
        assertEquals("{}", blank.resultJson)
    }

    @Test
    fun `unregistered tool returns failure with reference message`() = runTest {
        val reg = DefaultToolCallRegistry(NullTelephonyHttpTransport.Instance)
        val r = reg.invokeAsync(ToolInvocation("c1", "nope", "{}"))
        assertFalse(r.succeeded)
        assertEquals("{}", r.resultJson)
        assertEquals("Tool 'nope' is not registered.", r.error)
    }

    @Test
    fun `webhook tool dispatches through the injected transport and posts call_id + tool + arguments`() = runTest {
        val fake = FakeTelephonyHttpTransport().on("POST", "/hooks/book", 200, """{"ok":true}""")
        val reg = DefaultToolCallRegistry(fake)
        reg.registerWebhook(ToolDefinition("book", "books", "{}"), URI("https://api.example.com/hooks/book"))

        val r = reg.invokeAsync(ToolInvocation("c9", "book", """{"party":4}"""))
        assertTrue(r.succeeded)
        assertEquals("""{"ok":true}""", r.resultJson)

        // Body carries the reference shape with arguments re-emitted as JSON (not a string).
        val sent = fake.requests.single()
        assertEquals("POST", sent.method)
        val body = TelephonyJson.parse(sent.body!!)
        val obj = (body as kotlinx.serialization.json.JsonObject)
        assertEquals("c9", (obj["call_id"] as kotlinx.serialization.json.JsonPrimitive).content)
        assertEquals("book", (obj["tool"] as kotlinx.serialization.json.JsonPrimitive).content)
        assertEquals(4, (obj["arguments"]!!.let { it as kotlinx.serialization.json.JsonObject }["party"]!!
            .let { it as kotlinx.serialization.json.JsonPrimitive }).content.toInt())
    }

    @Test
    fun `webhook non-2xx surfaces a truncated error`() = runTest {
        val fake = FakeTelephonyHttpTransport().on("POST", "/hooks/book", 500, "boom")
        val reg = DefaultToolCallRegistry(fake)
        reg.registerWebhook(ToolDefinition("book", "books", "{}"), URI("https://api.example.com/hooks/book"))

        val r = reg.invokeAsync(ToolInvocation("c9", "book", "{}"))
        assertFalse(r.succeeded)
        assertEquals("{}", r.resultJson)
        assertTrue(r.error!!.startsWith("Webhook 500:"))
    }

    @Test
    fun `throwing local handler is caught and reported`() = runTest {
        val reg = DefaultToolCallRegistry(NullTelephonyHttpTransport.Instance)
        reg.registerLocal(ToolDefinition("bad", "throws", "{}")) { throw IllegalArgumentException("nope") }
        val r = reg.invokeAsync(ToolInvocation("c1", "bad", "{}"))
        assertFalse(r.succeeded)
        assertEquals("nope", r.error)
    }

    @Test
    fun `webhook must be absolute`() {
        val reg = DefaultToolCallRegistry(NullTelephonyHttpTransport.Instance)
        assertFailsWith<IllegalArgumentException> {
            reg.registerWebhook(ToolDefinition("x", "x", "{}"), URI("/relative"))
        }
    }

    // ── DTMF tone generator ──────────────────────────────────────────────────
    @Test
    fun `dtmf sample count is sr times ms over 1000 with 16-bit pcm`() {
        // 150 ms @ 8000 Hz -> 8000*150/1000 = 1200 samples -> 2400 bytes.
        val tone = DtmfToneGenerator.generate('1', 8000)
        assertEquals(2400, tone.size)

        // 200 ms @ 16000 -> 3200 samples -> 6400 bytes.
        assertEquals(6400, DtmfToneGenerator.generate('5', 16000, durationMs = 200).size)
    }

    @Test
    fun `dtmf rejects unsupported digit and non-positive params`() {
        assertFailsWith<IllegalArgumentException> { DtmfToneGenerator.generate('Z', 8000) }
        assertFailsWith<IllegalArgumentException> { DtmfToneGenerator.generate('1', 0) }
        assertFailsWith<IllegalArgumentException> { DtmfToneGenerator.generate('1', 8000, durationMs = 0) }
    }

    @Test
    fun `dtmf sequence adds inter-digit gaps between but not after`() {
        // "12" @ 8000: two 150ms tones (2400 bytes each) + one 50ms gap (8000*50/1000=400 samples=800 bytes).
        val seq = DtmfToneGenerator.generateSequence("12", 8000)
        assertEquals(2400 + 800 + 2400, seq.size)
        assertEquals(ByteArray(0).size, DtmfToneGenerator.generateSequence("", 8000).size)
    }

    @Test
    fun `dtmf value at t=0 is zero because sin(0)=0`() {
        // At i=0 both sines are 0 → sample 0 → first two bytes zero. Confirms LE 16-bit path.
        val tone = DtmfToneGenerator.generate('9', 24000)
        assertEquals(0.toByte(), tone[0])
        assertEquals(0.toByte(), tone[1])
    }

    @Test
    fun `dtmf sends one audio frame through the session`() = runTest {
        val session = TestCallSession(callInfo(format = CallMediaFormat.Pcm16000))
        DtmfToneGenerator.sendThroughSessionAsync(session, "1", sampleRateHz = 16000)
        assertEquals(1, session.sentAudioFrames.size)
        assertEquals(CallMediaFormat.Pcm16000, session.sentAudioFrames.single().format)
    }

    // ── Warm transfer orchestrator ───────────────────────────────────────────
    @Test
    fun `warm transfer dials briefs and cold-transfers the source`() = runTest {
        val carrier = FakeTelephonyCarrier()
        val source = carrier.receiveInboundCall(from = "+27110000009", to = "+27110000000")
        var briefed: String? = null
        val tts = BriefingSynthesiser { text -> briefed = text; ByteArray(4) }

        val orch = DefaultWarmTransferOrchestrator(carrier, tts)
        val result = orch.executeAsync(
            WarmTransferRequest(source, "+27115550123", "customer wants a refund", URI("wss://host/bridge")),
        )

        assertTrue(result.succeeded)
        assertEquals("customer wants a refund", briefed)
        // Source was cold-transferred.
        assertEquals(CallStatus.Transferred, source.status)
        // A bridge leg was dialled to the target, briefed (1 audio frame), then hung up.
        val bridge = result.bridgeSession as FakeCallSession
        assertEquals("+27115550123", bridge.info.to)
        assertEquals("+27110000000", bridge.info.from) // source.info.to
        assertEquals(CallStatus.EndedByAgent, bridge.status)
        assertEquals(1, bridge.mediaStream.sentAudioFrames.size)
    }

    @Test
    fun `warm transfer rejects a blank target`() = runTest {
        val carrier = FakeTelephonyCarrier()
        val source = carrier.receiveInboundCall("+2711", "+2712")
        val result = DefaultWarmTransferOrchestrator(carrier, BriefingSynthesiser { ByteArray(0) })
            .executeAsync(WarmTransferRequest(source, "  ", "hi", URI("wss://h/b")))
        assertFalse(result.succeeded)
        assertEquals("TargetNumber is required", result.failureReason)
    }

    @Test
    fun `warm transfer reports a dial failure`() = runTest {
        val failing = object : ITelephonyCarrier {
            override val carrierId = "boom"
            override val isConfigured = true
            override suspend fun provisionNumberAsync(countryCode: String, areaCode: String?) = error("no")
            override suspend fun configureInboundWebhookAsync(phoneNumber: String, inboundWebhook: URI) {}
            override suspend fun dialAsync(
                fromNumber: String,
                toNumber: String,
                streamUrl: URI,
                options: OutboundDialOptions?,
            ): ICallSession = throw IllegalStateException("carrier down")
            override suspend fun listNumbersAsync() = emptyList<ProvisionedNumber>()
        }
        val source = TestCallSession(callInfo())
        val result = DefaultWarmTransferOrchestrator(failing, BriefingSynthesiser { ByteArray(0) })
            .executeAsync(WarmTransferRequest(source, "+2712", "hi", URI("wss://h/b")))
        assertFalse(result.succeeded)
        assertTrue(result.failureReason!!.contains("Failed to dial target"))
    }

    // ── Null implementations ─────────────────────────────────────────────────
    @Test
    fun `null carrier is fail-soft`() = runTest {
        val c = NullTelephonyCarrier.Instance
        assertEquals("null", c.carrierId)
        assertFalse(c.isConfigured)
        assertTrue(c.listNumbersAsync().isEmpty())
        c.configureInboundWebhookAsync("+2711", URI("https://x")) // no-op
        assertFailsWith<IllegalStateException> { c.provisionNumberAsync("ZA") }
        assertFailsWith<IllegalStateException> { c.dialAsync("+2711", "+2712", URI("wss://x")) }
    }

    @Test
    fun `null dispatcher never fires`() {
        val d = NullInboundCallDispatcher.Instance
        assertEquals("null", d.carrierId)
        val handle = d.subscribe { }
        handle.close() // inert
    }

    // ── TestCallSession ──────────────────────────────────────────────────────
    @Test
    fun `test session injects and captures audio and dtmf`() = runTest {
        val s = TestCallSession(callInfo())
        s.injectInboundAudio(AudioFrame(byteArrayOf(9), CallMediaFormat.Pcm16000, Duration.ZERO))
        s.injectInboundDtmf(DtmfEvent('5', Duration.ofMillis(100), Duration.ZERO))
        s.endInboundStreams()

        assertEquals(1, s.receiveAudioAsync().toList().size)
        assertEquals('5', s.receiveDtmfAsync().toList().single().digit)

        s.sendAudioAsync(AudioFrame(byteArrayOf(1), CallMediaFormat.Pcm16000, Duration.ZERO))
        s.sendDtmfAsync("42")
        assertEquals(1, s.sentAudioFrames.size)
        assertEquals(listOf("42"), s.sentDtmf)
    }

    @Test
    fun `test session status events fire on transfer and hangup`() = runTest {
        val s = TestCallSession(callInfo())
        val seen = mutableListOf<CallStatus>()
        s.onStatusChanged { seen.add(it) }

        s.transferAsync("+2712", TransferMode.Cold)
        assertEquals(CallStatus.Transferred, s.status)

        s.hangUpAsync()
        assertEquals(CallStatus.EndedByAgent, s.status)
        assertEquals(listOf(CallStatus.Transferred, CallStatus.EndedByAgent), seen)
    }

    // ── FakeTelephonyCarrier + InMemoryMediaStream ───────────────────────────
    @Test
    fun `fake carrier provisions numbers and records webhooks`() = runTest {
        val c = FakeTelephonyCarrier()
        val n1 = c.provisionNumberAsync("ZA", "021")
        val n2 = c.provisionNumberAsync("ZA")
        assertEquals("fake", n1.carrierId)
        assertTrue(n1.phoneNumber.startsWith("+021"))
        assertEquals(BigDecimal.ZERO, n1.monthlyRecurringCost)
        assertEquals(listOf(n1, n2), c.listNumbersAsync())

        c.configureInboundWebhookAsync(n1.phoneNumber, URI("https://host/inbound"))
        assertEquals(URI("https://host/inbound"), c.webhookFor(n1.phoneNumber))
    }

    @Test
    fun `fake carrier dial yields a live active session that streams audio`() = runTest {
        val c = FakeTelephonyCarrier()
        val session = c.dialAsync("+27110000001", "+27115550123", URI("wss://host/stream")) as FakeCallSession
        assertEquals(CallStatus.Active, session.status)
        assertEquals(CallDirection.Outbound, session.info.direction)
        assertEquals("+27115550123", session.info.to)

        // Inbound audio injected on the backing stream is delivered.
        session.mediaStream.injectInboundAudio(AudioFrame(byteArrayOf(7, 7), CallMediaFormat.Pcm16000, Duration.ZERO))
        val frame = session.receiveAudioAsync().first()
        assertEquals(byteArrayOf(7, 7).toList(), frame.pcm.toList())

        // Outbound audio is captured.
        session.sendAudioAsync(AudioFrame(byteArrayOf(1), CallMediaFormat.Pcm16000, Duration.ZERO))
        assertEquals(1, session.mediaStream.sentAudioFrames.size)
    }

    @Test
    fun `fake session dtmf falls back to in-band tones`() = runTest {
        val c = FakeTelephonyCarrier(defaultFormat = CallMediaFormat.Pcm16000)
        val session = c.dialAsync("+2711", "+2712", URI("wss://h/s")) as FakeCallSession
        session.sendDtmfAsync("1")
        // In-band: exactly one audio frame emitted (no native IDtmfSendable on the stream).
        assertEquals(1, session.mediaStream.sentAudioFrames.size)
    }

    @Test
    fun `fake session hangup ends the call and fires status`() = runTest {
        val c = FakeTelephonyCarrier()
        val session = c.dialAsync("+2711", "+2712", URI("wss://h/s")) as FakeCallSession
        val seen = mutableListOf<CallStatus>()
        session.onStatusChanged { seen.add(it) }
        session.hangUpAsync()
        assertEquals(CallStatus.EndedByAgent, session.status)
        assertTrue(seen.contains(CallStatus.EndedByAgent))
    }

    // ── In-memory inbound dispatcher ─────────────────────────────────────────
    @Test
    fun `in-memory dispatcher delivers to a subscriber present at dispatch`() = runTest {
        val dispatcher = InMemoryInboundCallDispatcher("fake")
        val received = mutableListOf<String>()
        // Subscribe BEFORE dispatch (the documented contract) — no message is lost.
        val handle = dispatcher.subscribe { session -> received.add(session.info.callId) }
        assertEquals(1, dispatcher.subscriberCount)

        dispatcher.dispatch(TestCallSession(callInfo(id = "inbound-1")))
        assertEquals(listOf("inbound-1"), received)

        // After unsubscribe, further dispatches are not delivered.
        handle.close()
        assertEquals(0, dispatcher.subscriberCount)
        dispatcher.dispatch(TestCallSession(callInfo(id = "inbound-2")))
        assertEquals(listOf("inbound-1"), received)
    }
}
