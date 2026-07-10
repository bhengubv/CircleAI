// RealtimeCloudTests.swift
//
// Validates the CircleAI.Realtime.Cloud port (RealtimeCloud.swift): the Null
// transport factory (throws), options defaults, escapeDataString, the lenient
// cross-vendor parseEvent (every recognised OpenAI/Gemini shape + fallbacks +
// unknown→nil), the RealtimeWebSocketSession (outbound envelopes, binary audio
// framing, event parsing over a fake transport, dispose→close), and each vendor
// connector's isConfigured gating plus the exact endpoint + headers it hands the
// injected transport factory. Ultravox's two-step POST→WS is exercised with a
// fake HTTP leaf.

import XCTest
import Foundation
@testable import CircleAI

final class RealtimeCloudTests: XCTestCase {

    // ── Fakes ────────────────────────────────────────────────────────────────

    /// A deterministic in-memory transport. Text/binary frames it should deliver
    /// inbound are seeded up front; sent frames are recorded.
    private final class FakeTransport: IRealtimeTransport, @unchecked Sendable {
        private let lock = NSLock()
        private var sentText: [String] = []
        private var sentBinary: [Data] = []
        private var closed = false
        private var disposedFlag = false
        private let seededText: [String]
        private let seededBinary: [Data]

        init(inboundText: [String] = [], inboundBinary: [Data] = []) {
            self.seededText = inboundText
            self.seededBinary = inboundBinary
        }

        var isOpen: Bool { lock.lock(); defer { lock.unlock() }; return !closed }
        func recordedText() -> [String] { lock.lock(); defer { lock.unlock() }; return sentText }
        func recordedBinary() -> [Data] { lock.lock(); defer { lock.unlock() }; return sentBinary }
        func wasClosed() -> Bool { lock.lock(); defer { lock.unlock() }; return closed }
        func wasDisposed() -> Bool { lock.lock(); defer { lock.unlock() }; return disposedFlag }

        func sendText(_ text: String) async throws { lock.lock(); sentText.append(text); lock.unlock() }
        func sendBinary(_ bytes: Data) async throws { lock.lock(); sentBinary.append(bytes); lock.unlock() }

        func receiveText() -> AsyncStream<String> {
            AsyncStream { cont in
                for t in seededText { cont.yield(t) }
                cont.finish()
            }
        }
        func receiveBinary() -> AsyncStream<Data> {
            AsyncStream { cont in
                for b in seededBinary { cont.yield(b) }
                cont.finish()
            }
        }
        func close() async throws { lock.lock(); closed = true; lock.unlock() }
        func dispose() async { lock.lock(); disposedFlag = true; lock.unlock() }
    }

    /// Records the last connect() call and returns a fixed transport.
    private final class RecordingFactory: IRealtimeTransportFactory, @unchecked Sendable {
        private let lock = NSLock()
        private(set) var lastEndpoint: String?
        private(set) var lastHeaders: [String: String]?
        private let transport: FakeTransport

        init(transport: FakeTransport = FakeTransport()) { self.transport = transport }

        func connect(endpoint: String, headers: [String: String]?) async throws -> IRealtimeTransport {
            lock.lock(); lastEndpoint = endpoint; lastHeaders = headers; lock.unlock()
            return transport
        }
        func endpoint() -> String? { lock.lock(); defer { lock.unlock() }; return lastEndpoint }
        func headers() -> [String: String]? { lock.lock(); defer { lock.unlock() }; return lastHeaders }
    }

    /// Canned Ultravox HTTP leaf: returns a fixed status + body and records the POST.
    private final class FakeUltravoxHttp: IUltravoxHttpTransport, @unchecked Sendable {
        private let lock = NSLock()
        private let status: Int
        private let body: Data
        private(set) var lastBaseAddress: String?
        private(set) var lastPath: String?
        private(set) var lastHeaders: [String: String]?
        private(set) var lastJsonBody: Data?

        init(status: Int, body: Data) { self.status = status; self.body = body }

        func postJson(baseAddress: String, path: String, headers: [String: String], jsonBody: Data) async throws -> UltravoxHttpResponse {
            lock.lock()
            lastBaseAddress = baseAddress; lastPath = path; lastHeaders = headers; lastJsonBody = jsonBody
            lock.unlock()
            return UltravoxHttpResponse(statusCode: status, body: body)
        }
        func recordedBody() -> Data? { lock.lock(); defer { lock.unlock() }; return lastJsonBody }
        func recordedHeaders() -> [String: String]? { lock.lock(); defer { lock.unlock() }; return lastHeaders }
        func recordedPath() -> String? { lock.lock(); defer { lock.unlock() }; return lastPath }
    }

    // ── NullRealtimeTransportFactory ─────────────────────────────────────────

    func testNullTransportFactoryThrows() async {
        do {
            _ = try await NullRealtimeTransportFactory.instance.connect(endpoint: "wss://x", headers: nil)
            XCTFail("expected throw")
        } catch {
            guard case .noVendorRegistered = (error as? RealtimeError) else {
                return XCTFail("wrong error: \(error)")
            }
        }
    }

    // ── Options defaults ─────────────────────────────────────────────────────

    func testOptionsDefaults() {
        XCTAssertEqual(OpenAiRealtimeOptions().webSocketEndpoint, "wss://api.openai.com/v1/realtime")
        XCTAssertEqual(OpenAiRealtimeOptions().defaultModel, "gpt-4o-realtime-preview-2024-12-17")
        XCTAssertEqual(OpenAiRealtimeOptions().betaHeader, "realtime=v1")
        XCTAssertEqual(GeminiLiveOptions().defaultModel, "models/gemini-2.0-flash-exp")
        XCTAssertEqual(NovaSonicOptions().region, "us-east-1")
        XCTAssertEqual(NovaSonicOptions().defaultModel, "amazon.nova-sonic-v1:0")
        XCTAssertEqual(ElevenLabsConvOptions().webSocketEndpoint, "wss://api.elevenlabs.io/v1/convai/conversation")
        XCTAssertEqual(UltravoxOptions().apiEndpoint, "https://api.ultravox.ai")
        XCTAssertEqual(UltravoxOptions().defaultModel, "fixie-ai/ultravox-70B")
        XCTAssertEqual(UltravoxOptions().defaultVoice, "Mark")
    }

    // ── escapeDataString ─────────────────────────────────────────────────────

    func testEscapeDataString() {
        // Unreserved set stays; reserved chars escape (matches Uri.EscapeDataString).
        XCTAssertEqual(escapeDataString("abcXYZ012-._~"), "abcXYZ012-._~")
        XCTAssertEqual(escapeDataString("a b"), "a%20b")
        XCTAssertEqual(escapeDataString("models/gemini-2.0-flash-exp"), "models%2Fgemini-2.0-flash-exp")
        XCTAssertEqual(escapeDataString("fixie-ai/ultravox-70B"), "fixie-ai%2Fultravox-70B")
        XCTAssertEqual(escapeDataString("a=b&c"), "a%3Db%26c")
    }

    // ── parseEvent ───────────────────────────────────────────────────────────

    func testParseEventBlankAndUnknownReturnNil() {
        XCTAssertNil(RealtimeWebSocketSession.parseEvent(""))
        XCTAssertNil(RealtimeWebSocketSession.parseEvent("   "))
        XCTAssertNil(RealtimeWebSocketSession.parseEvent("not json"))
        XCTAssertNil(RealtimeWebSocketSession.parseEvent("{\"type\":\"something.unknown\"}"))
        XCTAssertNil(RealtimeWebSocketSession.parseEvent("{\"no_type\":1}"))
    }

    func testParseEventOpenAiSpeechAndTranscripts() {
        if case .speechStarted = RealtimeWebSocketSession.parseEvent("{\"type\":\"input_audio_buffer.speech_started\"}")! {} else { XCTFail() }
        if case .speechStarted = RealtimeWebSocketSession.parseEvent("{\"type\":\"speech_started\"}")! {} else { XCTFail() }
        if case .speechEnded = RealtimeWebSocketSession.parseEvent("{\"type\":\"input_audio_buffer.speech_stopped\"}")! {} else { XCTFail() }
        if case .speechEnded = RealtimeWebSocketSession.parseEvent("{\"type\":\"speech_stopped\"}")! {} else { XCTFail() }

        // Inbound transcript delta.
        if case let .transcriptDelta(_, delta, dir) =
            RealtimeWebSocketSession.parseEvent("{\"type\":\"transcript.delta\",\"delta\":\"hi\"}")! {
            XCTAssertEqual(delta, "hi"); XCTAssertEqual(dir, .inbound)
        } else { XCTFail() }

        // Inbound final: prefers "transcript", falls back to "text".
        if case let .transcriptFinal(_, text, dir) =
            RealtimeWebSocketSession.parseEvent("{\"type\":\"transcript.final\",\"transcript\":\"done\"}")! {
            XCTAssertEqual(text, "done"); XCTAssertEqual(dir, .inbound)
        } else { XCTFail() }
        if case let .transcriptFinal(_, text, _) =
            RealtimeWebSocketSession.parseEvent("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"text\":\"viaText\"}")! {
            XCTAssertEqual(text, "viaText")
        } else { XCTFail() }

        // Outbound transcript delta + done.
        if case let .transcriptDelta(_, delta, dir) =
            RealtimeWebSocketSession.parseEvent("{\"type\":\"response.audio_transcript.delta\",\"delta\":\"out\"}")! {
            XCTAssertEqual(delta, "out"); XCTAssertEqual(dir, .outbound)
        } else { XCTFail() }
        if case let .transcriptFinal(_, text, dir) =
            RealtimeWebSocketSession.parseEvent("{\"type\":\"response.audio_transcript.done\",\"transcript\":\"final\"}")! {
            XCTAssertEqual(text, "final"); XCTAssertEqual(dir, .outbound)
        } else { XCTFail() }
    }

    func testParseEventToolCallCarriesRawArgs() {
        let json = "{\"type\":\"response.function_call_arguments.done\",\"call_id\":\"c1\",\"name\":\"lookup\",\"arguments\":{\"q\":\"weather\"}}"
        guard case let .toolCall(_, callId, toolName, args) = RealtimeWebSocketSession.parseEvent(json)! else {
            return XCTFail()
        }
        XCTAssertEqual(callId, "c1")
        XCTAssertEqual(toolName, "lookup")
        // GetRawText analogue: the arguments object re-serialised (sorted keys).
        XCTAssertEqual(args, "{\"q\":\"weather\"}")
    }

    func testParseEventToolCallMissingArgsDefaultsToEmptyObject() {
        let json = "{\"type\":\"tool.call\",\"call_id\":\"c\",\"name\":\"n\"}"
        guard case let .toolCall(_, _, _, args) = RealtimeWebSocketSession.parseEvent(json)! else {
            return XCTFail()
        }
        XCTAssertEqual(args, "{}")
    }

    func testParseEventDoneAndError() {
        if case .turnComplete = RealtimeWebSocketSession.parseEvent("{\"type\":\"response.done\"}")! {} else { XCTFail() }
        if case .turnComplete = RealtimeWebSocketSession.parseEvent("{\"type\":\"turn.complete\"}")! {} else { XCTFail() }

        // Error with nested message.
        if case let .sessionError(_, message) =
            RealtimeWebSocketSession.parseEvent("{\"type\":\"error\",\"error\":{\"message\":\"boom\"}}")! {
            XCTAssertEqual(message, "boom")
        } else { XCTFail() }
        // Error without message falls back to the whole json.
        let raw = "{\"type\":\"error\"}"
        if case let .sessionError(_, message) = RealtimeWebSocketSession.parseEvent(raw)! {
            XCTAssertEqual(message, raw)
        } else { XCTFail() }
    }

    func testParseEventGeminiServerContent() {
        // turnComplete = true.
        if case .turnComplete =
            RealtimeWebSocketSession.parseEvent("{\"serverContent\":{\"turnComplete\":true}}")! {} else { XCTFail() }
        // modelTurn parts text → outbound delta.
        let json = "{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"text\":\"hi from gemini\"}]}}}"
        if case let .transcriptDelta(_, delta, dir) = RealtimeWebSocketSession.parseEvent(json)! {
            XCTAssertEqual(delta, "hi from gemini"); XCTAssertEqual(dir, .outbound)
        } else { XCTFail() }
    }

    // ── RealtimeWebSocketSession over a fake transport ───────────────────────

    func testSessionSessionIdIs32Hex() {
        let s = RealtimeWebSocketSession(transport: FakeTransport(), config: RealtimeSessionConfig(model: "m"), providerId: "p")
        XCTAssertEqual(s.sessionId.count, 32)
        XCTAssertNil(s.sessionId.rangeOfCharacter(from: CharacterSet(charactersIn: "0123456789abcdef").inverted))
    }

    func testSessionOutboundEnvelopes() async throws {
        let transport = FakeTransport()
        let s = RealtimeWebSocketSession(transport: transport, config: RealtimeSessionConfig(model: "m"), providerId: "openai-realtime")
        try await s.sendText("hello")
        try await s.sendToolResult(callId: "c1", resultJson: "{\"ok\":true}")
        try await s.cancelResponse()

        let sent = transport.recordedText()
        XCTAssertEqual(sent.count, 3)
        // Sorted-key JSON — assert the decoded shape rather than byte order.
        func obj(_ s: String) -> [String: String] {
            (try? JSONSerialization.jsonObject(with: Data(s.utf8)) as? [String: String]) ?? [:]
        }
        XCTAssertEqual(obj(sent[0]), ["type": "user.text", "provider": "openai-realtime", "text": "hello"])
        XCTAssertEqual(obj(sent[1]), ["type": "tool.result", "provider": "openai-realtime", "call_id": "c1", "result_json": "{\"ok\":true}"])
        XCTAssertEqual(obj(sent[2]), ["type": "response.cancel", "provider": "openai-realtime"])
    }

    func testSessionSendAudioForwardsBinary() async throws {
        let transport = FakeTransport()
        let s = RealtimeWebSocketSession(transport: transport, config: RealtimeSessionConfig(model: "m"), providerId: "p")
        try await s.sendAudio(RealtimeAudioFrame(pcm: Data([9, 8, 7]), format: .pcm16k, offset: 0))
        XCTAssertEqual(transport.recordedBinary(), [Data([9, 8, 7])])
    }

    func testSessionReceiveAudioFramesInConfigFormat() async throws {
        let transport = FakeTransport(inboundBinary: [Data([1, 2]), Data([3, 4])])
        let s = RealtimeWebSocketSession(transport: transport, config: RealtimeSessionConfig(model: "m", audioFormat: .mulaw8k), providerId: "p")
        var frames: [RealtimeAudioFrame] = []
        for await f in s.receiveAudio() { frames.append(f) }
        XCTAssertEqual(frames.map { $0.pcm }, [Data([1, 2]), Data([3, 4])])
        XCTAssertTrue(frames.allSatisfy { $0.format == .mulaw8k && $0.offset == 0 })
    }

    func testSessionReceiveEventsParsesAndSkipsBadFrames() async throws {
        let transport = FakeTransport(inboundText: [
            "{\"type\":\"speech_started\"}",
            "garbage-not-json",                    // skipped
            "{\"type\":\"response.done\"}",
        ])
        let s = RealtimeWebSocketSession(transport: transport, config: RealtimeSessionConfig(model: "m"), providerId: "p")
        var kinds: [String] = []
        for await e in s.receiveEvents() {
            switch e {
            case .speechStarted: kinds.append("start")
            case .turnComplete: kinds.append("turn")
            default: kinds.append("other")
            }
        }
        XCTAssertEqual(kinds, ["start", "turn"]) // bad frame dropped
    }

    func testSessionDisposeClosesAndDisposesTransport() async throws {
        let transport = FakeTransport()
        let s = RealtimeWebSocketSession(transport: transport, config: RealtimeSessionConfig(model: "m"), providerId: "p")
        await s.dispose()
        XCTAssertTrue(transport.wasClosed())
        XCTAssertTrue(transport.wasDisposed())
    }

    // ── OpenAI connector ─────────────────────────────────────────────────────

    func testOpenAiNotConfiguredThrows() async {
        let svc = OpenAiRealtimeService(options: OpenAiRealtimeOptions(apiKey: nil))
        XCTAssertEqual(svc.providerId, "openai-realtime")
        XCTAssertFalse(svc.isConfigured)
        do { _ = try await svc.startSession(RealtimeSessionConfig(model: "m")); XCTFail() }
        catch { guard case .notConfigured = (error as? RealtimeError) else { return XCTFail() } }
    }

    func testOpenAiEndpointAndHeaders() async throws {
        let factory = RecordingFactory()
        let svc = OpenAiRealtimeService(options: OpenAiRealtimeOptions(apiKey: "sk-123"), transports: factory)
        XCTAssertTrue(svc.isConfigured)
        _ = try await svc.startSession(RealtimeSessionConfig(model: "gpt-4o-realtime-preview-2024-12-17"))
        XCTAssertEqual(factory.endpoint(),
            "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-12-17")
        XCTAssertEqual(factory.headers()?["Authorization"], "Bearer sk-123")
        XCTAssertEqual(factory.headers()?["OpenAI-Beta"], "realtime=v1")
    }

    func testOpenAiFallsBackToDefaultModelOnBlank() async throws {
        let factory = RecordingFactory()
        let svc = OpenAiRealtimeService(options: OpenAiRealtimeOptions(apiKey: "k", defaultModel: "def-model"), transports: factory)
        _ = try await svc.startSession(RealtimeSessionConfig(model: "   "))
        XCTAssertEqual(factory.endpoint(), "wss://api.openai.com/v1/realtime?model=def-model")
    }

    // ── Gemini connector ─────────────────────────────────────────────────────

    func testGeminiEndpointEscapesKeyAndNoHeaders() async throws {
        let factory = RecordingFactory()
        let svc = GeminiLiveService(options: GeminiLiveOptions(apiKey: "a/b c"), transports: factory)
        XCTAssertEqual(svc.providerId, "gemini-live")
        _ = try await svc.startSession(RealtimeSessionConfig(model: "m"))
        XCTAssertEqual(factory.endpoint(),
            "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=a%2Fb%20c")
        XCTAssertNil(factory.headers())
    }

    func testGeminiNotConfigured() async {
        let svc = GeminiLiveService(options: GeminiLiveOptions(apiKey: "  "))
        XCTAssertFalse(svc.isConfigured)
        do { _ = try await svc.startSession(RealtimeSessionConfig(model: "m")); XCTFail() }
        catch { guard case .notConfigured = (error as? RealtimeError) else { return XCTFail() } }
    }

    // ── Nova Sonic connector ─────────────────────────────────────────────────

    func testNovaSonicRequiresBothKeys() {
        XCTAssertFalse(NovaSonicService(options: NovaSonicOptions(accessKeyId: "a", secretAccessKey: nil)).isConfigured)
        XCTAssertFalse(NovaSonicService(options: NovaSonicOptions(accessKeyId: nil, secretAccessKey: "s")).isConfigured)
        XCTAssertTrue(NovaSonicService(options: NovaSonicOptions(accessKeyId: "a", secretAccessKey: "s")).isConfigured)
    }

    func testNovaSonicEndpointAndHeaders() async throws {
        let factory = RecordingFactory()
        let svc = NovaSonicService(
            options: NovaSonicOptions(region: "eu-west-1", accessKeyId: "AK", secretAccessKey: "SK", sessionToken: "TOK"),
            transports: factory)
        XCTAssertEqual(svc.providerId, "aws-nova-sonic")
        _ = try await svc.startSession(RealtimeSessionConfig(model: "amazon.nova-sonic-v1:0"))
        XCTAssertEqual(factory.endpoint(),
            "wss://bedrock-runtime.eu-west-1.amazonaws.com/model/amazon.nova-sonic-v1%3A0/invoke-with-bidirectional-stream")
        XCTAssertEqual(factory.headers()?["X-Amz-Access-Key"], "AK")
        XCTAssertEqual(factory.headers()?["X-Amz-Secret-Key"], "SK")
        XCTAssertEqual(factory.headers()?["X-Amz-Region"], "eu-west-1")
        XCTAssertEqual(factory.headers()?["X-Amz-Security-Token"], "TOK")
    }

    func testNovaSonicOmitsSecurityTokenWhenBlank() async throws {
        let factory = RecordingFactory()
        let svc = NovaSonicService(
            options: NovaSonicOptions(accessKeyId: "AK", secretAccessKey: "SK", sessionToken: nil),
            transports: factory)
        _ = try await svc.startSession(RealtimeSessionConfig(model: "m"))
        XCTAssertNil(factory.headers()?["X-Amz-Security-Token"])
    }

    // ── ElevenLabs connector ─────────────────────────────────────────────────

    func testElevenLabsRequiresKeyAndAgent() {
        XCTAssertFalse(ElevenLabsConvService(options: ElevenLabsConvOptions(apiKey: "k", agentId: nil)).isConfigured)
        XCTAssertFalse(ElevenLabsConvService(options: ElevenLabsConvOptions(apiKey: nil, agentId: "a")).isConfigured)
        XCTAssertTrue(ElevenLabsConvService(options: ElevenLabsConvOptions(apiKey: "k", agentId: "a")).isConfigured)
    }

    func testElevenLabsEndpointAndHeader() async throws {
        let factory = RecordingFactory()
        let svc = ElevenLabsConvService(options: ElevenLabsConvOptions(apiKey: "xi-key", agentId: "agent 42"), transports: factory)
        XCTAssertEqual(svc.providerId, "elevenlabs-conv")
        _ = try await svc.startSession(RealtimeSessionConfig(model: "m"))
        XCTAssertEqual(factory.endpoint(),
            "wss://api.elevenlabs.io/v1/convai/conversation?agent_id=agent%2042")
        XCTAssertEqual(factory.headers()?["xi-api-key"], "xi-key")
    }

    // ── Ultravox connector ───────────────────────────────────────────────────

    func testUltravoxNotConfigured() async {
        let http = FakeUltravoxHttp(status: 200, body: Data())
        let svc = UltravoxService(http: http, options: UltravoxOptions(apiKey: nil))
        XCTAssertEqual(svc.providerId, "ultravox")
        XCTAssertFalse(svc.isConfigured)
        do { _ = try await svc.startSession(RealtimeSessionConfig(model: "m")); XCTFail() }
        catch { guard case .notConfigured = (error as? RealtimeError) else { return XCTFail() } }
    }

    func testUltravoxTwoStepPostThenConnectJoinUrl() async throws {
        let joinBody = Data("{\"joinUrl\":\"wss://join.ultravox.ai/abc\"}".utf8)
        let http = FakeUltravoxHttp(status: 200, body: joinBody)
        let factory = RecordingFactory()
        let svc = UltravoxService(http: http, options: UltravoxOptions(apiKey: "uv-key"), transports: factory)

        let session = try await svc.startSession(
            RealtimeSessionConfig(model: "custom-model", voiceId: "Ada", systemPrompt: "be terse"))

        // HTTP POST shape.
        XCTAssertEqual(http.recordedPath(), "/api/calls")
        XCTAssertEqual(http.recordedHeaders()?["X-API-Key"], "uv-key")
        let body = try XCTUnwrap(http.recordedBody())
        let root = try XCTUnwrap(try JSONSerialization.jsonObject(with: body) as? [String: Any])
        XCTAssertEqual(root["model"] as? String, "custom-model")
        XCTAssertEqual(root["voice"] as? String, "Ada")
        XCTAssertEqual(root["systemPrompt"] as? String, "be terse")
        let medium = try XCTUnwrap(root["medium"] as? [String: Any])
        let ws = try XCTUnwrap(medium["serverWebSocket"] as? [String: Any])
        XCTAssertEqual(ws["inputSampleRate"] as? Int, 16000)
        XCTAssertEqual(ws["outputSampleRate"] as? Int, 24000)

        // Then WS connect to the returned joinUrl.
        XCTAssertEqual(factory.endpoint(), "wss://join.ultravox.ai/abc")
        XCTAssertNil(factory.headers())
        XCTAssertEqual(session.sessionId.count, 32) // RealtimeWebSocketSession
    }

    func testUltravoxUsesDefaultsWhenModelOrVoiceBlank() async throws {
        let http = FakeUltravoxHttp(status: 200, body: Data("{\"joinUrl\":\"wss://j\"}".utf8))
        let factory = RecordingFactory()
        let svc = UltravoxService(http: http,
                                  options: UltravoxOptions(apiKey: "k", defaultModel: "dm", defaultVoice: "dv"),
                                  transports: factory)
        _ = try await svc.startSession(RealtimeSessionConfig(model: "  ", voiceId: nil))
        let root = try XCTUnwrap(try JSONSerialization.jsonObject(with: try XCTUnwrap(http.recordedBody())) as? [String: Any])
        XCTAssertEqual(root["model"] as? String, "dm")
        XCTAssertEqual(root["voice"] as? String, "dv")
        XCTAssertNil(root["systemPrompt"]) // omitted when nil
    }

    func testUltravoxThrowsOnHttpFailure() async {
        let http = FakeUltravoxHttp(status: 500, body: Data())
        let svc = UltravoxService(http: http, options: UltravoxOptions(apiKey: "k"))
        do { _ = try await svc.startSession(RealtimeSessionConfig(model: "m")); XCTFail() }
        catch { guard case .badVendorResponse = (error as? RealtimeError) else { return XCTFail() } }
    }

    func testUltravoxThrowsWhenNoJoinUrl() async {
        let http = FakeUltravoxHttp(status: 200, body: Data("{\"other\":1}".utf8))
        let svc = UltravoxService(http: http, options: UltravoxOptions(apiKey: "k"))
        do { _ = try await svc.startSession(RealtimeSessionConfig(model: "m")); XCTFail() }
        catch { guard case .badVendorResponse = (error as? RealtimeError) else { return XCTFail() } }
    }
}
