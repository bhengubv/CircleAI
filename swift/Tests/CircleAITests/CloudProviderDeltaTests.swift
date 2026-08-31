import XCTest
@testable import CircleAI

/// Reading text back out of each vendor stream, and the configured/not state.
final class CloudProviderDeltaTests: XCTestCase {

    func testOpenAiDeltasAreRead() {
        let sse = """
        data: {"choices":[{"delta":{"role":"assistant"}}]}
        data: {"choices":[{"delta":{"content":"Hello"}}]}
        data: {"choices":[{"delta":{"content":" world"}}]}
        data: [DONE]
        """
        XCTAssertEqual(CloudDeltaReader.deltas(fromStream: sse, shape: .openAiCompatible),
                       ["Hello", " world"])
    }

    // message_start, ping and message_stop arrive on the same stream and carry
    // no words - only content_block_delta does.
    func testOnlyAnthropicContentBlockDeltasCarryText() {
        let sse = """
        data: {"type":"message_start","message":{"id":"m1"}}
        data: {"type":"ping"}
        data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"Hi"}}
        data: {"type":"content_block_delta","delta":{"type":"text_delta","text":" there"}}
        data: {"type":"message_stop"}
        """
        XCTAssertEqual(CloudDeltaReader.deltas(fromStream: sse, shape: .anthropic), ["Hi", " there"])
    }

    func testGeminiDeltasAreReadOutOfCandidates() {
        let sse = """
        data: {"candidates":[{"content":{"parts":[{"text":"Sawubona"}],"role":"model"}}]}
        data: {"candidates":[{"content":{"parts":[{"text":" Nandi"}],"role":"model"}}]}
        """
        XCTAssertEqual(CloudDeltaReader.deltas(fromStream: sse, shape: .gemini),
                       ["Sawubona", " Nandi"])
    }

    // A keepalive or a usage report is not an error, it just carries no words.
    func testAFrameWithNoTextIsSkippedRatherThanFailing() {
        XCTAssertNil(CloudDeltaReader.delta(from: "{\"usage\":{\"total\":9}}", shape: .openAiCompatible))
        XCTAssertNil(CloudDeltaReader.delta(from: "{\"choices\":[]}", shape: .openAiCompatible))
        XCTAssertNil(CloudDeltaReader.delta(from: "not json", shape: .openAiCompatible))
        XCTAssertNil(CloudDeltaReader.delta(from: "{\"candidates\":[]}", shape: .gemini))
        XCTAssertNil(CloudDeltaReader.delta(from: "{\"type\":\"ping\"}", shape: .anthropic))
    }

    func testAnEmptyDeltaStringIsTreatedAsNoDelta() {
        XCTAssertNil(CloudDeltaReader.delta(from: "{\"choices\":[{\"delta\":{\"content\":\"\"}}]}",
                                            shape: .openAiCompatible))
    }

    // Each reader must read ONLY its own shape - a Gemini frame is not an
    // OpenAI frame with different names.
    func testEachReaderIgnoresTheOtherShapes() {
        let openAiFrame = "{\"choices\":[{\"delta\":{\"content\":\"x\"}}]}"
        XCTAssertNil(CloudDeltaReader.delta(from: openAiFrame, shape: .anthropic))
        XCTAssertNil(CloudDeltaReader.delta(from: openAiFrame, shape: .gemini))
    }

    // MARK: - Configured or not

    func testAGeneratorWithNoKeyIsNotConfiguredAndSaysSo() {
        let g = CloudChatGenerator(provider: .openAi, options: .openAi(apiKey: nil))
        XCTAssertFalse(g.isConfigured)
        XCTAssertEqual(g.statusMessage, "OpenAI API key not configured.")
        XCTAssertEqual(g.id, "openai")
    }

    func testABlankKeyIsNoKey() {
        XCTAssertFalse(CloudChatGenerator(provider: .groq, options: .groq(apiKey: "   ")).isConfigured)
    }

    func testAConfiguredGeneratorNamesItsModel() {
        let g = CloudChatGenerator(provider: .anthropic, options: .anthropic(apiKey: "sk-1"))
        XCTAssertTrue(g.isConfigured)
        XCTAssertEqual(g.statusMessage, "Ready \u{B7} claude-3-5-sonnet-latest")
        XCTAssertEqual(g.engineLabel, "Anthropic \u{B7} claude-3-5-sonnet-latest")
    }

    // Fail soft: a status frame, not an exception, so the chain can move past
    // it and a UI can show the reason.
    func testAnUnconfiguredGeneratorYieldsItsStatusAndStops() async {
        let g = CloudChatGenerator(provider: .openAi, options: .openAi(apiKey: nil))
        var frames: [String] = []
        for await f in g.stream(messages: [], options: nil) { frames.append(f) }
        XCTAssertEqual(frames, ["[OpenAI API key not configured.]"])
    }

    // A configured provider with no transport wired must still not throw.
    func testAConfiguredGeneratorWithNoTransportFailsSoftToo() async {
        let g = CloudChatGenerator(provider: .openAi, options: .openAi(apiKey: "sk-1"))
        var frames: [String] = []
        for await f in g.stream(messages: [], options: nil) { frames.append(f) }
        XCTAssertEqual(frames.count, 1)
        XCTAssertTrue(frames[0].hasPrefix("["))
    }

    // MARK: - Through a transport

    private struct StubTransport: ICloudChatTransport {
        let status: Int
        let text: String
        func post(baseAddress: String, path: String, headers: [String: String],
                  body: Data) async throws -> (status: Int, text: String) {
            (status, text)
        }
    }

    func testASuccessfulStreamIsAssembledIntoWords() async {
        let sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Sawu\"}}]}\n"
                + "data: {\"choices\":[{\"delta\":{\"content\":\"bona\"}}]}\n"
                + "data: [DONE]\n"
        let g = CloudChatGenerator(provider: .openAi, options: .openAi(apiKey: "sk-1"),
                                   transport: StubTransport(status: 200, text: sse))
        let answer = try? await g.generate(messages: [ChatMessage(role: "user", content: "hi")],
                                           options: nil)
        XCTAssertEqual(answer, "Sawubona")
    }

    // An HTTP error is reported as one readable frame, truncated - not thrown,
    // and not a wall of HTML.
    func testAnHttpErrorBecomesOneTruncatedFrame() async {
        let g = CloudChatGenerator(provider: .openAi, options: .openAi(apiKey: "sk-1"),
                                   transport: StubTransport(status: 429,
                                                            text: String(repeating: "x", count: 1000)))
        var frames: [String] = []
        for await f in g.stream(messages: [], options: nil) { frames.append(f) }
        XCTAssertEqual(frames.count, 1)
        XCTAssertTrue(frames[0].hasPrefix("[openai error 429: "))
        XCTAssertTrue(frames[0].hasSuffix("\u{2026}]"))
        XCTAssertLessThan(frames[0].count, 300)
    }
}
