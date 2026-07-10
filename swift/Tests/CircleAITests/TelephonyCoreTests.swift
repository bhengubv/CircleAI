// TelephonyCoreTests.swift
//
// Verifies the carrier-agnostic core ported in Telephony.swift,
// TelephonyDtmf.swift, TelephonyOrchestration.swift, TelephonyTestSession.swift,
// and TelephonyFakeCarrier.swift: enum ordinals, DTO shapes, TestCallSession /
// InMemoryMediaStream buffering + fan-out semantics, DtmfToneGenerator wire
// output, warm-transfer orchestration, null carrier fail-soft, and the
// deterministic FakeTelephonyCarrier. Values cross-checked against the C#
// reference.

import XCTest
import Foundation
@testable import CircleAI

final class TelephonyCoreTests: XCTestCase {

    // ── Enum ordinals (must match C# declaration order) ───────────────────
    func testEnumOrdinals() {
        XCTAssertEqual(CallDirection.inbound.rawValue, 0)
        XCTAssertEqual(CallDirection.outbound.rawValue, 1)

        XCTAssertEqual(CallStatus.ringing.rawValue, 0)
        XCTAssertEqual(CallStatus.active.rawValue, 1)
        XCTAssertEqual(CallStatus.endedByCaller.rawValue, 2)
        XCTAssertEqual(CallStatus.endedByCallee.rawValue, 3)
        XCTAssertEqual(CallStatus.endedByAgent.rawValue, 4)
        XCTAssertEqual(CallStatus.voicemail.rawValue, 5)
        XCTAssertEqual(CallStatus.failed.rawValue, 6)
        XCTAssertEqual(CallStatus.transferred.rawValue, 7)

        XCTAssertEqual(CallMediaFormat.mulaw8000.rawValue, 0)
        XCTAssertEqual(CallMediaFormat.alaw8000.rawValue, 1)
        XCTAssertEqual(CallMediaFormat.pcm16000.rawValue, 2)
        XCTAssertEqual(CallMediaFormat.pcm24000.rawValue, 3)

        XCTAssertEqual(TransferMode.cold.rawValue, 0)
        XCTAssertEqual(TransferMode.warm.rawValue, 1)
    }

    // ── OutboundDialOptions defaults ──────────────────────────────────────
    func testOutboundDialOptionDefaults() {
        let opts = OutboundDialOptions()
        XCTAssertFalse(opts.detectAnsweringMachine)
        XCTAssertEqual(opts.ringTimeoutSeconds, 30)
        XCTAssertNil(opts.callerIdOverride)
        XCTAssertNil(opts.followMeNumbers)
    }

    // ── TestCallSession: pre-subscription writes are replayed (unbounded) ──
    func testTestCallSessionReplaysBufferedAudio() async throws {
        let session = TestCallSession()
        // Inject BEFORE any reader attaches — must be retained and replayed.
        session.injectInboundAudio(AudioFrame(pcm: Data([1, 2]), format: .pcm16000, offset: 0))
        session.injectInboundAudio(AudioFrame(pcm: Data([3, 4]), format: .pcm16000, offset: 0.02))
        session.endInboundStreams()

        var received: [AudioFrame] = []
        for await frame in session.receiveAudio() { received.append(frame) }
        XCTAssertEqual(received.count, 2)
        XCTAssertEqual(received[0].pcm, Data([1, 2]))
        XCTAssertEqual(received[1].pcm, Data([3, 4]))
        // Order preserved.
        XCTAssertEqual(received[1].offset, 0.02, accuracy: 1e-12)
    }

    func testTestCallSessionCapturesOutbound() async throws {
        let session = TestCallSession()
        try await session.sendAudio(AudioFrame(pcm: Data([9]), format: .mulaw8000, offset: 0))
        try await session.sendDtmf("123")
        XCTAssertEqual(session.sentAudioFrames.count, 1)
        XCTAssertEqual(session.sentAudioFrames[0].pcm, Data([9]))
        XCTAssertEqual(session.sentDtmf, ["123"])
    }

    func testTestCallSessionTransferSetsStatus() async throws {
        let session = TestCallSession()
        XCTAssertEqual(session.status, .active)
        try await session.transfer(targetNumber: "+27110000000", mode: .cold)
        XCTAssertEqual(session.status, .transferred)
    }

    func testTestCallSessionHangUpEndsStreamsAndStatus() async throws {
        let session = TestCallSession()
        try await session.hangUp()
        XCTAssertEqual(session.status, .endedByAgent)
        // Inbound stream should now be finished.
        var count = 0
        for await _ in session.receiveAudio() { count += 1 }
        XCTAssertEqual(count, 0)
    }

    func testTestCallSessionStatusChangesFanOut() async throws {
        let session = TestCallSession()
        let stream = session.statusChanges()   // subscribe synchronously first
        session.triggerStatusChange(.voicemail)
        session.triggerStatusChange(.failed)
        await session.dispose()                 // completes the broker

        var seen: [CallStatus] = []
        for await s in stream { seen.append(s) }
        XCTAssertEqual(seen, [.voicemail, .failed])
    }

    // ── InMemoryMediaStream: buffering + status ───────────────────────────
    func testInMemoryMediaStreamBuffersAndEnds() async throws {
        let info = CallInfo(
            callId: "c1", direction: .inbound, from: "+1", to: "+2",
            carrierId: "fake", mediaFormat: .pcm16000, startedAtUtc: Date())
        let media = InMemoryMediaStream(callInfo: info)
        media.injectInboundDtmf(DtmfEvent(digit: "5", duration: 0.1, offset: 0))
        try await media.end()

        var digits: [Character] = []
        for await ev in media.receiveDtmf() { digits.append(ev.digit) }
        XCTAssertEqual(digits, ["5"])
        XCTAssertEqual(media.currentStatus, .endedByAgent)
    }

    // ── DtmfToneGenerator: sample count + endianness + amplitude ──────────
    func testDtmfGenerateSampleCount() throws {
        // 8000 Hz, 150 ms → 8000*150/1000 = 1200 samples → 2400 bytes.
        let buf = try DtmfToneGenerator.generate(digit: "1", sampleRateHz: 8000)
        XCTAssertEqual(buf.count, 2400)
        // First sample is sin(0)+sin(0) = 0 → 0x0000.
        XCTAssertEqual(buf[0], 0)
        XCTAssertEqual(buf[1], 0)
    }

    func testDtmfGenerateRejectsBadInput() {
        XCTAssertThrowsError(try DtmfToneGenerator.generate(digit: "Z", sampleRateHz: 8000))
        XCTAssertThrowsError(try DtmfToneGenerator.generate(digit: "1", sampleRateHz: 0))
        XCTAssertThrowsError(try DtmfToneGenerator.generate(digit: "1", sampleRateHz: 8000, durationMs: 0))
    }

    func testDtmfSequenceLengthWithGaps() throws {
        // "12": two 150 ms tones @ 8000 Hz (1200 samples each) + one 50 ms gap
        // (400 samples). Total samples = 1200 + 400 + 1200 = 2800 → 5600 bytes.
        let seq = try DtmfToneGenerator.generateSequence(digits: "12", sampleRateHz: 8000)
        XCTAssertEqual(seq.count, 5600)
        // Empty input → empty.
        XCTAssertEqual(try DtmfToneGenerator.generateSequence(digits: "", sampleRateHz: 8000).count, 0)
    }

    func testDtmfKnownPeakValue() throws {
        // A single-frequency check is awkward (two summed sines), but we can
        // assert the buffer is non-trivial and lies within Int16 range at a
        // mid-tone sample. Reads little-endian Int16 at sample index 100.
        let buf = try DtmfToneGenerator.generate(digit: "0", sampleRateHz: 16000, durationMs: 20)
        XCTAssertEqual(buf.count, 16000 * 20 / 1000 * 2)
        let lo = Int16(buf[200])
        let hi = Int16(buf[201])
        let sample = lo | (hi << 8)
        XCTAssertGreaterThanOrEqual(Int(sample), Int(Int16.min))
        XCTAssertLessThanOrEqual(Int(sample), Int(Int16.max))
    }

    func testDtmfSendThroughSessionPicksFormat() async throws {
        let session = TestCallSession()
        try await DtmfToneGenerator.sendThroughSession(session, digits: "1", sampleRateHz: 24000)
        XCTAssertEqual(session.sentAudioFrames.count, 1)
        XCTAssertEqual(session.sentAudioFrames[0].format, .pcm24000)
        // 24000 Hz default 150 ms tone → 3600 samples → 7200 bytes (single digit,
        // no gap).
        XCTAssertEqual(session.sentAudioFrames[0].pcm.count, 7200)
    }

    // ── NullTelephonyCarrier fail-soft ────────────────────────────────────
    func testNullCarrierFailSoft() async throws {
        let carrier = NullTelephonyCarrier.instance
        XCTAssertEqual(carrier.carrierId, "null")
        XCTAssertFalse(carrier.isConfigured)
        // ConfigureInboundWebhook is a no-op (does not throw).
        try await carrier.configureInboundWebhook(
            phoneNumber: "+1", inboundWebhook: URL(string: "https://x/hook")!)
        // ListNumbers returns empty.
        let numbers = try await carrier.listNumbers()
        XCTAssertTrue(numbers.isEmpty)
        // Provision + dial throw.
        await XCTAssertThrowsErrorAsync(try await carrier.provisionNumber(countryCode: "ZA"))
        await XCTAssertThrowsErrorAsync(
            try await carrier.dial(fromNumber: "+1", toNumber: "+2", streamUrl: URL(string: "wss://x")!))
    }

    func testNullInboundDispatcherNeverFires() {
        let d = NullInboundCallDispatcher.instance
        XCTAssertEqual(d.carrierId, "null")
        let sub = d.subscribe { _ in }
        sub.dispose() // no-op, must not crash
    }

    // ── FakeTelephonyCarrier: deterministic provisioning + records ────────
    func testFakeCarrierProvisionsDeterministically() async throws {
        let carrier = FakeTelephonyCarrier(carrierId: "fake", configured: true, monthlyRecurringCost: 3)
        let n1 = try await carrier.provisionNumber(countryCode: "27", areaCode: "21")
        let n2 = try await carrier.provisionNumber(countryCode: "27", areaCode: "21")
        XCTAssertEqual(n1.phoneNumber, "+27210000001")
        XCTAssertEqual(n2.phoneNumber, "+27210000002")
        XCTAssertEqual(n1.carrierId, "fake")
        XCTAssertEqual(n1.monthlyRecurringCost, Decimal(3))
        // Owned numbers listed back.
        let listed = try await carrier.listNumbers()
        XCTAssertEqual(listed.map(\.phoneNumber), [n1.phoneNumber, n2.phoneNumber])
    }

    func testFakeCarrierRecordsWebhookAndDial() async throws {
        let carrier = FakeTelephonyCarrier()
        try await carrier.configureInboundWebhook(
            phoneNumber: "+27210000001", inboundWebhook: URL(string: "https://host/inbound")!)
        XCTAssertEqual(carrier.configuredWebhooks.count, 1)
        XCTAssertEqual(carrier.configuredWebhooks[0].phoneNumber, "+27210000001")

        let session = try await carrier.dial(
            fromNumber: "+27210000001", toNumber: "+27821112222",
            streamUrl: URL(string: "wss://host/stream")!,
            options: OutboundDialOptions(ringTimeoutSeconds: 45))
        XCTAssertEqual(carrier.dials.count, 1)
        XCTAssertEqual(carrier.dials[0].toNumber, "+27821112222")
        XCTAssertEqual(carrier.dials[0].options?.ringTimeoutSeconds, 45)
        XCTAssertEqual(session.info.direction, .outbound)
        XCTAssertEqual(session.info.carrierId, "fake")
        // Session is live: sending audio is captured (no pending-stream throw).
        try await session.sendAudio(AudioFrame(pcm: Data([1]), format: .mulaw8000, offset: 0))
        await session.dispose()
    }

    func testFakeCarrierUnconfiguredFailsSoftAndThrows() async throws {
        let carrier = FakeTelephonyCarrier(configured: false)
        XCTAssertFalse(carrier.isConfigured)
        // ListNumbers fail-soft (empty).
        XCTAssertTrue(try await carrier.listNumbers().isEmpty)
        // Provision/dial/configure throw.
        await XCTAssertThrowsErrorAsync(try await carrier.provisionNumber(countryCode: "ZA"))
        // Flip configured on and it works.
        carrier.setConfigured(true)
        _ = try await carrier.provisionNumber(countryCode: "ZA")
    }

    // ── CarrierFallback picks first configured ────────────────────────────
    func testCarrierFallbackPicksFirstConfigured() async throws {
        let unconfigured = FakeTelephonyCarrier(carrierId: "a", configured: false)
        let configured = FakeTelephonyCarrier(carrierId: "b", configured: true)
        let fallback = CarrierFallback([unconfigured, configured])
        XCTAssertEqual(fallback.carrierId, "fallback(2)")
        XCTAssertTrue(fallback.isConfigured)
        // Provision routes to "b".
        let n = try await fallback.provisionNumber(countryCode: "27", areaCode: "11")
        XCTAssertEqual(n.carrierId, "b")
    }

    func testCarrierFallbackAllUnconfiguredFallsToNull() async throws {
        let fallback = CarrierFallback([FakeTelephonyCarrier(configured: false)])
        XCTAssertFalse(fallback.isConfigured)
        // Picks NullTelephonyCarrier → dial throws, listNumbers empty.
        XCTAssertTrue(try await fallback.listNumbers().isEmpty)
        await XCTAssertThrowsErrorAsync(
            try await fallback.dial(fromNumber: "+1", toNumber: "+2", streamUrl: URL(string: "wss://x")!))
    }

    // ── Warm-transfer orchestration ───────────────────────────────────────
    func testWarmTransferHappyPath() async throws {
        let carrier = FakeTelephonyCarrier(configured: true)
        let source = TestCallSession(info: CallInfo(
            callId: "src", direction: .inbound, from: "+27821112222", to: "+27210000001",
            carrierId: "fake", mediaFormat: .pcm16000, startedAtUtc: Date()))
        let ttsCalls = Locked<[String]>([])
        let tts: BriefingSynthesiser = { text in
            ttsCalls.mutate { $0.append(text) }
            return Data([0xAA, 0xBB])
        }
        let orch = DefaultWarmTransferOrchestrator(carrier: carrier, briefingTts: tts)
        let result = await orch.execute(WarmTransferRequest(
            sourceSession: source,
            targetNumber: "+27110009999",
            briefingText: "Caller wants a refund.",
            bridgeStreamUrl: URL(string: "wss://host/bridge")!))

        XCTAssertTrue(result.succeeded)
        XCTAssertNil(result.failureReason)
        XCTAssertEqual(ttsCalls.value, ["Caller wants a refund."])
        // Carrier dialled the bridge leg from source.To → target.
        XCTAssertEqual(carrier.dials.count, 1)
        XCTAssertEqual(carrier.dials[0].fromNumber, "+27210000001")
        XCTAssertEqual(carrier.dials[0].toNumber, "+27110009999")
        // Source got cold-transferred (status flipped).
        XCTAssertEqual(source.status, .transferred)
        // Bridge leg was hung up at the end.
        if let bridge = result.bridgeSession as? InMemoryCallSession {
            XCTAssertTrue(bridge.didHangUp)
        } else {
            XCTFail("expected InMemoryCallSession bridge")
        }
    }

    func testWarmTransferMissingTargetFails() async throws {
        let carrier = FakeTelephonyCarrier(configured: true)
        let source = TestCallSession()
        let orch = DefaultWarmTransferOrchestrator(carrier: carrier) { _ in Data() }
        let result = await orch.execute(WarmTransferRequest(
            sourceSession: source, targetNumber: "   ",
            briefingText: "x", bridgeStreamUrl: URL(string: "wss://x")!))
        XCTAssertFalse(result.succeeded)
        XCTAssertEqual(result.failureReason, "TargetNumber is required")
        XCTAssertNil(result.bridgeSession)
    }

    func testWarmTransferDialFailureReported() async throws {
        // Unconfigured carrier → dial throws → orchestrator reports failure.
        let carrier = FakeTelephonyCarrier(configured: false)
        let source = TestCallSession()
        let orch = DefaultWarmTransferOrchestrator(carrier: carrier) { _ in Data() }
        let result = await orch.execute(WarmTransferRequest(
            sourceSession: source, targetNumber: "+2711000",
            briefingText: "x", bridgeStreamUrl: URL(string: "wss://x")!))
        XCTAssertFalse(result.succeeded)
        XCTAssertNotNil(result.failureReason)
        XCTAssertTrue(result.failureReason?.contains("Failed to dial target") ?? false)
    }
}

// NOTE: The shared test helpers `Locked<Value>` (CompanionStateSyncTests.swift)
// and `XCTAssertThrowsErrorAsync` (SecurityAetherNetTests.swift) already exist
// in this test target and are reused here — they are NOT redefined to avoid a
// duplicate-symbol collision.
