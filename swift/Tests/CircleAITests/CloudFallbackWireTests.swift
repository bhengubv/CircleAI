import XCTest
@testable import CircleAI

/// SSE framing, request shaping and delta extraction.
final class CloudFallbackWireTests: XCTestCase {

    private let convo = [
        ChatMessage(role: "system", content: "Be brief."),
        ChatMessage(role: "user", content: "hello"),
        ChatMessage(role: "assistant", content: "hi"),
    ]

    // MARK: - SSE

    func testOnlyDataLinesBecomeFrames() {
        let sse = """
        event: message_start
        data: {"a":1}
        : this is a comment

        data: {"b":2}
        """
        XCTAssertEqual(ServerSentEventsReader.frames(from: sse), ["{\"a\":1}", "{\"b\":2}"])
    }

    // The stream ends at [DONE] rather than waiting for the socket to close.
    func testDoneEndsTheStreamAndIsNotAFrame() {
        let sse = "data: {\"a\":1}\ndata: [DONE]\ndata: {\"never\":true}\n"
        XCTAssertEqual(ServerSentEventsReader.frames(from: sse), ["{\"a\":1}"])
    }

    func testLeadingSpaceAfterTheColonIsStripped() {
        XCTAssertEqual(ServerSentEventsReader.frames(from: "data:  {\"a\":1}"), ["{\"a\":1}"])
        XCTAssertEqual(ServerSentEventsReader.frames(from: "data:{\"a\":1}"), ["{\"a\":1}"])
    }

    func testCrlfStreamsFrameTheSame() {
        XCTAssertEqual(ServerSentEventsReader.frames(from: "data: a\r\ndata: b\r\n"), ["a", "b"])
    }

    func testAnEmptyStreamHasNoFrames() {
        XCTAssertTrue(ServerSentEventsReader.frames(from: "").isEmpty)
        XCTAssertTrue(ServerSentEventsReader.frames(from: "\n\n\n").isEmpty)
    }

    // MARK: - OpenAI shape

    func testTheOpenAiBodyStreamsAndKeepsSystemInTheList() throws {
        let body = CloudRequestBuilder.openAiBody(messages: convo, options: .openAi(apiKey: "k"))
        XCTAssertEqual(body["model"] as? String, "gpt-4o-mini")
        XCTAssertEqual(body["stream"] as? Bool, true)
        XCTAssertEqual(body["max_tokens"] as? Int, 1024)
        let messages = body["messages"] as! [[String: String]]
        XCTAssertEqual(messages.count, 3)
        XCTAssertEqual(messages[0]["role"], "system")
    }

    func testExplicitSamplingBeatsTheProviderDefault() {
        let body = CloudRequestBuilder.openAiBody(messages: convo, options: .openAi(apiKey: "k"),
                                                  temperature: 0.1, maxTokens: 32)
        XCTAssertEqual(body["temperature"] as? Float, 0.1)
        XCTAssertEqual(body["max_tokens"] as? Int, 32)
    }

    // MARK: - Anthropic shape

    // Leaving a system message in the list is rejected by the API, so it has to
    // come out into a top-level field.
    func testAnthropicLiftsSystemOutOfTheMessageList() {
        let body = CloudRequestBuilder.anthropicBody(messages: convo, options: .anthropic(apiKey: "k"))
        XCTAssertEqual(body["system"] as? String, "Be brief.")
        let messages = body["messages"] as! [[String: String]]
        XCTAssertEqual(messages.count, 2)
        XCTAssertFalse(messages.contains { $0["role"] == "system" })
    }

    func testSeveralSystemMessagesAreJoinedByABlankLine() {
        let two = [ChatMessage(role: "system", content: "One."),
                   ChatMessage(role: "system", content: "Two."),
                   ChatMessage(role: "user", content: "go")]
        let body = CloudRequestBuilder.anthropicBody(messages: two, options: .anthropic(apiKey: "k"))
        XCTAssertEqual(body["system"] as? String, "One.\n\nTwo.")
    }

    // An empty system field is not the same as no system field.
    func testNoSystemMessageMeansNoSystemKeyAtAll() {
        let body = CloudRequestBuilder.anthropicBody(
            messages: [ChatMessage(role: "user", content: "hi")], options: .anthropic(apiKey: "k"))
        XCTAssertNil(body["system"])
    }

    // MARK: - Gemini shape

    // Gemini has no assistant role - it is called model.
    func testGeminiRenamesAssistantToModel() {
        let body = CloudRequestBuilder.geminiBody(messages: convo, options: .gemini(apiKey: "k"))
        let contents = body["contents"] as! [[String: Any]]
        XCTAssertEqual(contents.count, 2)
        XCTAssertEqual(contents[0]["role"] as? String, "user")
        XCTAssertEqual(contents[1]["role"] as? String, "model")
    }

    func testGeminiWrapsEveryMessageInParts() {
        let body = CloudRequestBuilder.geminiBody(messages: convo, options: .gemini(apiKey: "k"))
        let contents = body["contents"] as! [[String: Any]]
        let parts = contents[0]["parts"] as! [[String: String]]
        XCTAssertEqual(parts[0]["text"], "hello")
    }

    func testGeminiPutsSystemInItsOwnInstruction() {
        let body = CloudRequestBuilder.geminiBody(messages: convo, options: .gemini(apiKey: "k"))
        let sys = body["systemInstruction"] as! [String: Any]
        let parts = sys["parts"] as! [[String: String]]
        XCTAssertEqual(parts[0]["text"], "Be brief.")
    }

    func testGeminiUsesMaxOutputTokensNotMaxTokens() {
        let body = CloudRequestBuilder.geminiBody(messages: convo, options: .gemini(apiKey: "k"))
        let cfg = body["generationConfig"] as! [String: Any]
        XCTAssertEqual(cfg["maxOutputTokens"] as? Int, 1024)
        XCTAssertNil(body["max_tokens"])
    }

    // A model id with a slash in it would otherwise address another route.
    func testGeminiPercentEncodesTheModelAndTheKeyIntoThePath() {
        var opts = CloudChatOptions.gemini(apiKey: "abc/def")
        opts.model = "models/gemini-2.0-flash"
        let path = CloudRequestBuilder.geminiPath(options: opts)
        XCTAssertTrue(path.contains("models%2Fgemini-2.0-flash:streamGenerateContent"))
        XCTAssertTrue(path.contains("key=abc%2Fdef"))
        XCTAssertTrue(path.contains("alt=sse"))
    }

    // MARK: - Paths and headers

    // Groq serves the OpenAI API under a different path; getting this wrong
    // produces a 404, not a message.
    func testGroqUsesItsOwnOpenAiPath() {
        XCTAssertEqual(CloudProvider.groq.chatCompletionsPath, "/openai/v1/chat/completions")
        XCTAssertEqual(CloudProvider.openAi.chatCompletionsPath, "/v1/chat/completions")
        XCTAssertEqual(CloudProvider.deepSeek.chatCompletionsPath, "/v1/chat/completions")
        XCTAssertEqual(CloudProvider.anthropic.chatCompletionsPath, "/v1/messages")
    }

    // Three providers, three conventions for carrying the key.
    func testEachProviderCarriesItsKeyItsOwnWay() {
        let openAi = CloudRequestBuilder.headers(for: .openAi, options: .openAi(apiKey: "sk-1"))
        XCTAssertEqual(openAi["Authorization"], "Bearer sk-1")

        let anthropic = CloudRequestBuilder.headers(for: .anthropic, options: .anthropic(apiKey: "sk-2"))
        XCTAssertEqual(anthropic["x-api-key"], "sk-2")
        XCTAssertEqual(anthropic["anthropic-version"], "2023-06-01")
        XCTAssertNil(anthropic["Authorization"])

        // Gemini carries it in the query string, so no auth header at all.
        let gemini = CloudRequestBuilder.headers(for: .gemini, options: .gemini(apiKey: "sk-3"))
        XCTAssertNil(gemini["Authorization"])
        XCTAssertNil(gemini["x-api-key"])
    }

    func testTheWireShapesAreAssignedCorrectly() {
        XCTAssertEqual(CloudProvider.anthropic.wireShape, .anthropic)
        XCTAssertEqual(CloudProvider.gemini.wireShape, .gemini)
        for p: CloudProvider in [.openAi, .groq, .cerebras, .together, .deepSeek] {
            XCTAssertEqual(p.wireShape, .openAiCompatible, "\(p.rawValue)")
        }
    }
}
