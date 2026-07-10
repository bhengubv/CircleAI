// RealtimeTests.swift
//
// Validates the CircleAI.Realtime port (Realtime.swift): enum ordinals, DTO
// Codable + the RealtimeEvent `.at` accessor, the LoopbackRealtimeService/Session
// (silence-sizing math, sample rates, RMS silence detection, audio echo
// loopback, speech-started/ended toggling, sendText's delta/audio/final/
// turnComplete sequence with offset accounting, sendToolResult truncation +
// guard, cancelResponse, pre-subscription buffering, dispose→finish), and the
// Null defaults (service throws, session yields nothing).

import XCTest
import Foundation
@testable import CircleAI

final class RealtimeTests: XCTestCase {

    // ── Enum ordinals ────────────────────────────────────────────────────────

    func testAudioFormatOrdinals() {
        XCTAssertEqual(RealtimeAudioFormat.pcm16k.rawValue, 0)
        XCTAssertEqual(RealtimeAudioFormat.pcm24k.rawValue, 1)
        XCTAssertEqual(RealtimeAudioFormat.mulaw8k.rawValue, 2)
        XCTAssertEqual(RealtimeAudioFormat.allCases.count, 3)
    }

    func testDirectionOrdinals() {
        XCTAssertEqual(RealtimeDirection.inbound.rawValue, 0)
        XCTAssertEqual(RealtimeDirection.outbound.rawValue, 1)
    }

    // ── DTO Codable ──────────────────────────────────────────────────────────

    func testSessionConfigCodableRoundTrip() throws {
        let cfg = RealtimeSessionConfig(
            model: "m", voiceId: "alloy", systemPrompt: "be nice",
            audioFormat: .pcm16k, languageHint: "en-US",
            tools: [RealtimeTool(name: "t", description: "d", jsonSchema: "{}")])
        let back = try JSONDecoder().decode(RealtimeSessionConfig.self, from: try JSONEncoder().encode(cfg))
        XCTAssertEqual(cfg, back)
    }

    func testSessionConfigDefaults() {
        let cfg = RealtimeSessionConfig(model: "m")
        XCTAssertNil(cfg.voiceId)
        XCTAssertNil(cfg.systemPrompt)
        XCTAssertEqual(cfg.audioFormat, .pcm24k) // C# default
        XCTAssertNil(cfg.languageHint)
        XCTAssertNil(cfg.tools)
    }

    func testRealtimeToolCodableRoundTrip() throws {
        let t = RealtimeTool(name: "search", description: "look up", jsonSchema: "{\"type\":\"object\"}")
        let back = try JSONDecoder().decode(RealtimeTool.self, from: try JSONEncoder().encode(t))
        XCTAssertEqual(t, back)
    }

    func testRealtimeEventAtAccessor() {
        let d = Date(timeIntervalSince1970: 42)
        XCTAssertEqual(RealtimeEvent.speechStarted(at: d).at, d)
        XCTAssertEqual(RealtimeEvent.speechEnded(at: d).at, d)
        XCTAssertEqual(RealtimeEvent.transcriptDelta(at: d, delta: "x", direction: .inbound).at, d)
        XCTAssertEqual(RealtimeEvent.transcriptFinal(at: d, text: "x", direction: .outbound).at, d)
        XCTAssertEqual(RealtimeEvent.toolCall(at: d, callId: "c", toolName: "t", argumentsJson: "{}").at, d)
        XCTAssertEqual(RealtimeEvent.turnComplete(at: d).at, d)
        XCTAssertEqual(RealtimeEvent.sessionError(at: d, message: "m").at, d)
    }

    // ── SampleRateOf / SilenceTextToAudio / IsSilent ─────────────────────────

    func testSampleRateOf() {
        XCTAssertEqual(LoopbackRealtimeSession.sampleRateOf(.pcm16k), 16_000)
        XCTAssertEqual(LoopbackRealtimeSession.sampleRateOf(.pcm24k), 24_000)
        XCTAssertEqual(LoopbackRealtimeSession.sampleRateOf(.mulaw8k), 8_000)
    }

    func testSilenceTextToAudioSizing() async throws {
        // 2 words @ 24kHz → durationMs = max(50, 2*80)=160 → sampleCount=3840 → 7680 bytes.
        let pcm = try await LoopbackRealtimeService.silenceTextToAudio("hello world", .pcm24k)
        XCTAssertEqual(pcm.count, 7680)
        XCTAssertTrue(pcm.allSatisfy { $0 == 0 }) // real silence bytes
    }

    func testSilenceTextToAudioEmptyUsesMin50ms() async throws {
        // 0 words → durationMs = max(50, 0) = 50 → sampleCount = 16000*50/1000 = 800 → 1600 bytes.
        let pcm = try await LoopbackRealtimeService.silenceTextToAudio("   ", .pcm16k)
        XCTAssertEqual(pcm.count, 1600)
    }

    func testIsSilentThresholds() {
        // < 64 bytes → silent regardless of content.
        XCTAssertTrue(LoopbackRealtimeSession.isSilent(Data(repeating: 0xFF, count: 32)))
        // 64 zero bytes → RMS 0 → silent.
        XCTAssertTrue(LoopbackRealtimeSession.isSilent(Data(count: 64)))
        // 64 bytes of amplitude-1000 little-endian samples → RMS 1000 → speech.
        var loud = Data()
        for _ in 0..<32 { loud.append(0xE8); loud.append(0x03) } // 1000 = 0x03E8 LE
        XCTAssertFalse(LoopbackRealtimeSession.isSilent(loud))
    }

    // ── Loopback service / session ───────────────────────────────────────────

    func testServiceIdentity() async throws {
        let svc = LoopbackRealtimeService()
        XCTAssertEqual(svc.providerId, "loopback")
        XCTAssertTrue(svc.isConfigured)
        let session = try await svc.startSession(RealtimeSessionConfig(model: "m"))
        XCTAssertTrue(session.sessionId.hasPrefix("loop-"))
        XCTAssertEqual(session.sessionId.count, 5 + 32) // "loop-" + 32 hex
        await session.dispose()
    }

    func testAudioLoopbackEchoesFrame() async throws {
        let session = LoopbackRealtimeSession(config: RealtimeSessionConfig(model: "m", audioFormat: .pcm16k))
        let stream = session.receiveAudio()
        let frame = RealtimeAudioFrame(pcm: Data([1, 2, 3, 4]), format: .pcm16k, offset: 0)
        try await session.sendAudio(frame)
        await session.dispose() // finishes the stream so iteration terminates

        var got: [Data] = []
        for await f in stream { got.append(f.pcm) }
        XCTAssertEqual(got, [Data([1, 2, 3, 4])]) // echoed back
    }

    func testSpeechStartedThenEndedEventsOnToggle() async throws {
        let session = LoopbackRealtimeSession(config: RealtimeSessionConfig(model: "m", audioFormat: .pcm16k))
        let events = session.receiveEvents()

        // Loud frame → speech started.
        var loud = Data(); for _ in 0..<64 { loud.append(0xE8); loud.append(0x03) }
        try await session.sendAudio(RealtimeAudioFrame(pcm: loud, format: .pcm16k, offset: 0))
        // Silent frame → speech ended.
        try await session.sendAudio(RealtimeAudioFrame(pcm: Data(count: 128), format: .pcm16k, offset: 0))
        await session.dispose()

        var kinds: [String] = []
        for await e in events {
            switch e {
            case .speechStarted: kinds.append("start")
            case .speechEnded: kinds.append("end")
            default: break
            }
        }
        XCTAssertEqual(kinds, ["start", "end"])
    }

    func testSendTextEmitsDeltaAudioFinalTurnCompleteWithOffsetAccounting() async throws {
        let session = LoopbackRealtimeSession(config: RealtimeSessionConfig(model: "m", audioFormat: .pcm24k))
        let audio = session.receiveAudio()
        let events = session.receiveEvents()

        try await session.sendText("hello world") // 2 words → 7680 bytes, 0.16s
        try await session.sendText("hi")          // 1 word → durationMs=80 → 3840 bytes @24k
        await session.dispose()

        // Audio frames: first at offset 0, second at offset 0.16 (first's duration).
        var frames: [RealtimeAudioFrame] = []
        for await f in audio { frames.append(f) }
        XCTAssertEqual(frames.count, 2)
        XCTAssertEqual(frames[0].offset, 0, accuracy: 1e-9)
        XCTAssertEqual(frames[0].pcm.count, 7680)
        XCTAssertEqual(frames[1].offset, 0.16, accuracy: 1e-9)
        XCTAssertEqual(frames[1].pcm.count, 3840) // 24000*80/1000*2

        // Event sequence per sendText: delta(out), final(out), turnComplete.
        var seq: [String] = []
        for await e in events {
            switch e {
            case let .transcriptDelta(_, delta, dir):
                seq.append("delta:\(delta):\(dir == .outbound ? "out" : "in")")
            case let .transcriptFinal(_, text, dir):
                seq.append("final:\(text):\(dir == .outbound ? "out" : "in")")
            case .turnComplete:
                seq.append("turn")
            default: break
            }
        }
        XCTAssertEqual(seq, [
            "delta:hello world:out", "final:hello world:out", "turn",
            "delta:hi:out", "final:hi:out", "turn",
        ])
    }

    func testSendToolResultTruncatesAndGuards() async throws {
        let session = LoopbackRealtimeSession(config: RealtimeSessionConfig(model: "m"))
        let events = session.receiveEvents()

        let longJson = String(repeating: "a", count: 100)
        try await session.sendToolResult(callId: "call-1", resultJson: longJson)

        // Blank callId throws.
        do {
            try await session.sendToolResult(callId: "  ", resultJson: "{}")
            XCTFail("expected throw")
        } catch {
            XCTAssertEqual(error as? RealtimeError, .argumentRequired("callId required"))
        }
        await session.dispose()

        var deltas: [String] = []
        for await e in events {
            if case let .transcriptDelta(_, delta, _) = e { deltas.append(delta) }
        }
        XCTAssertEqual(deltas.count, 1)
        // "[tool call-1: " + 60 'a' + "…" + "]"
        XCTAssertEqual(deltas[0], "[tool call-1: \(String(repeating: "a", count: 60))…]")
    }

    func testCancelResponseEmitsTurnComplete() async throws {
        let session = LoopbackRealtimeSession(config: RealtimeSessionConfig(model: "m"))
        let events = session.receiveEvents()
        try await session.cancelResponse()
        await session.dispose()

        var turns = 0
        for await e in events { if case .turnComplete = e { turns += 1 } }
        XCTAssertEqual(turns, 1)
    }

    func testEventsBufferedBeforeSubscription() async throws {
        // Write an event BEFORE receiveEvents() is iterated — the unbounded channel
        // must retain it (no lost message).
        let session = LoopbackRealtimeSession(config: RealtimeSessionConfig(model: "m"))
        try await session.cancelResponse() // emits turnComplete before we subscribe
        let events = session.receiveEvents()
        await session.dispose()

        var turns = 0
        for await e in events { if case .turnComplete = e { turns += 1 } }
        XCTAssertEqual(turns, 1)
    }

    // ── Null defaults ────────────────────────────────────────────────────────

    func testNullServiceThrowsOnStart() async {
        let svc = NullRealtimeService.instance
        XCTAssertEqual(svc.providerId, "null")
        XCTAssertFalse(svc.isConfigured)
        do {
            _ = try await svc.startSession(RealtimeSessionConfig(model: "m"))
            XCTFail("expected throw")
        } catch {
            guard case .noVendorRegistered = (error as? RealtimeError) else {
                return XCTFail("wrong error: \(error)")
            }
        }
    }

    func testNullSessionYieldsNothing() async throws {
        let session = NullRealtimeSession()
        XCTAssertEqual(session.sessionId, "null")
        // All sends are no-ops (must not throw).
        try await session.sendAudio(RealtimeAudioFrame(pcm: Data([1]), format: .pcm16k, offset: 0))
        try await session.sendText("x")
        try await session.sendToolResult(callId: "c", resultJson: "{}")
        try await session.cancelResponse()

        var audioCount = 0
        for await _ in session.receiveAudio() { audioCount += 1 }
        var eventCount = 0
        for await _ in session.receiveEvents() { eventCount += 1 }
        XCTAssertEqual(audioCount, 0)
        XCTAssertEqual(eventCount, 0)
        await session.dispose()
    }
}
