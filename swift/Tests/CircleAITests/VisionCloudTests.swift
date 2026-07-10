// VisionCloudTests.swift
//
// Verifies the CircleAI.Vision.Cloud image-generation ports (VisionCloud.swift):
//   • NullImageGenerator (empty, not-configured).
//   • OpenAiImageGenerator — request shaping (count clamp, size), url parsing,
//     fail-soft when unconfigured or on non-2xx.
//   • StabilityImageGenerator — loop-per-count, inline bytes, negative-prompt
//     inclusion, per-image failure skip.
//   • ImageGeneratorFallbackChain — skips unconfigured, returns first non-empty.
//   • LocalDeterministicImageGenerator — offline deterministic fake.
// The HTTP leaf is a deterministic in-test fake `IImageHttpTransport`.

import XCTest
@testable import CircleAI

final class VisionCloudTests: XCTestCase {

    // ── Deterministic HTTP transport fake ───────────────────────────────

    /// Records every call and returns scripted responses in order. A `nil`
    /// scripted response throws (drives transport-level failure). Thread-safe.
    final class FakeTransport: IImageHttpTransport, @unchecked Sendable {
        struct Call: Sendable {
            let kind: String        // "json" | "multipart"
            let path: String
            let headers: [String: String]
            let jsonBody: Data?
            let accept: String?
            let fields: [ImageHttpFormField]?
        }
        struct Boom: Error {}

        private let lock = NSLock()
        private var jsonResponses: [ImageHttpResponse]
        private var multipartResponses: [ImageHttpResponse]
        private var _calls: [Call] = []

        init(jsonResponses: [ImageHttpResponse] = [], multipartResponses: [ImageHttpResponse] = []) {
            self.jsonResponses = jsonResponses
            self.multipartResponses = multipartResponses
        }

        var calls: [Call] { lock.lock(); defer { lock.unlock() }; return _calls }

        func postJson(baseAddress: String, path: String, headers: [String: String], jsonBody: Data) async throws -> ImageHttpResponse {
            lock.lock()
            _calls.append(Call(kind: "json", path: path, headers: headers, jsonBody: jsonBody, accept: nil, fields: nil))
            let next = jsonResponses.isEmpty ? nil : jsonResponses.removeFirst()
            lock.unlock()
            guard let r = next else { throw Boom() }
            return r
        }

        func postMultipart(baseAddress: String, path: String, headers: [String: String], accept: String, fields: [ImageHttpFormField]) async throws -> ImageHttpResponse {
            lock.lock()
            _calls.append(Call(kind: "multipart", path: path, headers: headers, jsonBody: nil, accept: accept, fields: fields))
            let next = multipartResponses.isEmpty ? nil : multipartResponses.removeFirst()
            lock.unlock()
            guard let r = next else { throw Boom() }
            return r
        }
    }

    private let fixedClock: @Sendable () -> Date = { Date(timeIntervalSince1970: 1_700_000_000) }

    private func okJson(_ obj: [String: Any]) -> ImageHttpResponse {
        ImageHttpResponse(statusCode: 200, body: try! JSONSerialization.data(withJSONObject: obj))
    }

    // ── NullImageGenerator ──────────────────────────────────────────────

    func testNullImageGeneratorReturnsEmpty() async throws {
        let g = NullImageGenerator.instance
        XCTAssertEqual(g.generatorId, "null")
        XCTAssertFalse(g.isConfigured)
        XCTAssertEqual(g.displayLabel, "No image generator")
        let result = try await g.generate(request: ImageGenerationRequest(prompt: "cat"))
        XCTAssertTrue(result.isEmpty)
    }

    // ── OpenAiImageGenerator ────────────────────────────────────────────

    func testOpenAiNotConfiguredReturnsEmptyWithoutCallingTransport() async throws {
        let t = FakeTransport()
        let g = OpenAiImageGenerator(options: OpenAiImageOptions(apiKey: nil), transport: t, clock: fixedClock)
        XCTAssertFalse(g.isConfigured)
        XCTAssertEqual(g.generatorId, "openai-images")
        let result = try await g.generate(request: ImageGenerationRequest(prompt: "dog"))
        XCTAssertTrue(result.isEmpty)
        XCTAssertTrue(t.calls.isEmpty)     // fail-soft: no HTTP attempted
    }

    func testOpenAiParsesUrlsAndClampsCount() async throws {
        let body: [String: Any] = ["data": [["url": "https://img/1.png"], ["url": "https://img/2.png"]]]
        let t = FakeTransport(jsonResponses: [okJson(body)])
        let g = OpenAiImageGenerator(options: OpenAiImageOptions(apiKey: "sk-test", model: "dall-e-3"),
                                     transport: t, clock: fixedClock)
        XCTAssertTrue(g.isConfigured)
        let result = try await g.generate(request: ImageGenerationRequest(prompt: "sunset", size: 512, count: 99))
        XCTAssertEqual(result.count, 2)
        XCTAssertEqual(result[0].url, "https://img/1.png")
        XCTAssertEqual(result[0].mimeType, "image/png")
        XCTAssertNil(result[0].bytes)
        XCTAssertEqual(result[0].generatorId, "openai-images")
        XCTAssertEqual(result[0].generatedAtUtc, Date(timeIntervalSince1970: 1_700_000_000))

        // Request shaping: n clamped to 4, size "512x512", response_format url, path.
        XCTAssertEqual(t.calls.count, 1)
        let call = t.calls[0]
        XCTAssertEqual(call.path, "/v1/images/generations")
        XCTAssertEqual(call.headers["Authorization"], "Bearer sk-test")
        let sent = try JSONSerialization.jsonObject(with: call.jsonBody!) as! [String: Any]
        XCTAssertEqual(sent["n"] as? Int, 4)
        XCTAssertEqual(sent["size"] as? String, "512x512")
        XCTAssertEqual(sent["response_format"] as? String, "url")
        XCTAssertEqual(sent["model"] as? String, "dall-e-3")
        XCTAssertEqual(sent["prompt"] as? String, "sunset")
    }

    func testOpenAiFailSoftOnNon2xx() async throws {
        let t = FakeTransport(jsonResponses: [ImageHttpResponse(statusCode: 429, body: Data("rate limited".utf8))])
        let g = OpenAiImageGenerator(options: OpenAiImageOptions(apiKey: "sk-test"), transport: t, clock: fixedClock)
        let result = try await g.generate(request: ImageGenerationRequest(prompt: "x"))
        XCTAssertTrue(result.isEmpty)
    }

    func testOpenAiFailSoftWhenNoDataArray() async throws {
        let t = FakeTransport(jsonResponses: [okJson(["unexpected": true])])
        let g = OpenAiImageGenerator(options: OpenAiImageOptions(apiKey: "sk-test"), transport: t, clock: fixedClock)
        let result = try await g.generate(request: ImageGenerationRequest(prompt: "x"))
        XCTAssertTrue(result.isEmpty)
    }

    // ── StabilityImageGenerator ─────────────────────────────────────────

    func testStabilityLoopsPerCountAndReturnsBytes() async throws {
        let img1 = ImageHttpResponse(statusCode: 200, body: Data([1, 2, 3]))
        let img2 = ImageHttpResponse(statusCode: 200, body: Data([4, 5, 6]))
        let t = FakeTransport(multipartResponses: [img1, img2])
        let g = StabilityImageGenerator(
            options: StabilityImageOptions(apiKey: "st-test", model: "sd3.5-large", outputFormat: "png"),
            transport: t, clock: fixedClock)
        let result = try await g.generate(request: ImageGenerationRequest(prompt: "forest", count: 2))
        XCTAssertEqual(result.count, 2)
        XCTAssertEqual(result[0].bytes, Data([1, 2, 3]))
        XCTAssertEqual(result[1].bytes, Data([4, 5, 6]))
        XCTAssertEqual(result[0].mimeType, "image/png")
        XCTAssertNil(result[0].url)
        XCTAssertEqual(result[0].generatorId, "stability")
        XCTAssertEqual(t.calls.count, 2)   // one HTTP call per image
        XCTAssertEqual(t.calls[0].path, "/v2beta/stable-image/generate/sd3")
        XCTAssertEqual(t.calls[0].accept, "image/png")
    }

    func testStabilityIncludesNegativePromptField() async throws {
        let t = FakeTransport(multipartResponses: [ImageHttpResponse(statusCode: 200, body: Data([9]))])
        let g = StabilityImageGenerator(options: StabilityImageOptions(apiKey: "st"), transport: t, clock: fixedClock)
        _ = try await g.generate(request: ImageGenerationRequest(prompt: "city", negativePrompt: "blurry", count: 1))
        let fields = t.calls[0].fields!
        XCTAssertTrue(fields.contains(ImageHttpFormField(name: "prompt", value: "city")))
        XCTAssertTrue(fields.contains(ImageHttpFormField(name: "negative_prompt", value: "blurry")))
    }

    func testStabilityOmitsNegativePromptWhenEmpty() async throws {
        let t = FakeTransport(multipartResponses: [ImageHttpResponse(statusCode: 200, body: Data([9]))])
        let g = StabilityImageGenerator(options: StabilityImageOptions(apiKey: "st"), transport: t, clock: fixedClock)
        _ = try await g.generate(request: ImageGenerationRequest(prompt: "city", negativePrompt: "", count: 1))
        let fields = t.calls[0].fields!
        XCTAssertFalse(fields.contains { $0.name == "negative_prompt" })
    }

    func testStabilitySkipsFailedImageAndKeepsGoing() async throws {
        // First image 500 (skipped), second 200 (kept).
        let t = FakeTransport(multipartResponses: [
            ImageHttpResponse(statusCode: 500, body: Data("err".utf8)),
            ImageHttpResponse(statusCode: 200, body: Data([7, 7])),
        ])
        let g = StabilityImageGenerator(options: StabilityImageOptions(apiKey: "st"), transport: t, clock: fixedClock)
        let result = try await g.generate(request: ImageGenerationRequest(prompt: "x", count: 2))
        XCTAssertEqual(result.count, 1)
        XCTAssertEqual(result[0].bytes, Data([7, 7]))
    }

    func testStabilityNotConfiguredReturnsEmpty() async throws {
        let t = FakeTransport()
        let g = StabilityImageGenerator(options: StabilityImageOptions(apiKey: "  "), transport: t, clock: fixedClock)
        XCTAssertFalse(g.isConfigured)
        let result = try await g.generate(request: ImageGenerationRequest(prompt: "x", count: 3))
        XCTAssertTrue(result.isEmpty)
        XCTAssertTrue(t.calls.isEmpty)
    }

    // ── ImageGeneratorFallbackChain ─────────────────────────────────────

    func testFallbackChainSkipsUnconfiguredAndReturnsFirstNonEmpty() async throws {
        let unconfigured = LocalDeterministicImageGenerator(generatorId: "a", isConfigured: false)
        let emptyButConfigured = ScriptedGenerator(id: "b", configured: true, output: [])
        let winner = LocalDeterministicImageGenerator(generatorId: "c", isConfigured: true, clock: fixedClock)
        let chain = ImageGeneratorFallbackChain([unconfigured, emptyButConfigured, winner])

        XCTAssertEqual(chain.generatorId, "fallback-chain")
        XCTAssertTrue(chain.isConfigured)
        let result = try await chain.generate(request: ImageGenerationRequest(prompt: "p", count: 1))
        XCTAssertEqual(result.count, 1)
        XCTAssertEqual(result[0].generatorId, "c")
    }

    func testFallbackChainEmptyWhenNothingConfigured() async throws {
        let chain = ImageGeneratorFallbackChain([
            LocalDeterministicImageGenerator(generatorId: "a", isConfigured: false),
            LocalDeterministicImageGenerator(generatorId: "b", isConfigured: false),
        ])
        XCTAssertFalse(chain.isConfigured)
        XCTAssertEqual(chain.statusMessage, "No configured generator in chain.")
        let result = try await chain.generate(request: ImageGenerationRequest(prompt: "p"))
        XCTAssertTrue(result.isEmpty)
    }

    func testFallbackChainStatusListsConfiguredIds() {
        let chain = ImageGeneratorFallbackChain([
            LocalDeterministicImageGenerator(generatorId: "openai-images", isConfigured: true),
            LocalDeterministicImageGenerator(generatorId: "stability", isConfigured: true),
        ])
        XCTAssertEqual(chain.statusMessage, "Ready · openai-images → stability")
    }

    // ── LocalDeterministicImageGenerator ────────────────────────────────

    func testLocalDeterministicProducesCountArtifacts() async throws {
        let g = LocalDeterministicImageGenerator(generatorId: "fake", clock: fixedClock)
        let result = try await g.generate(request: ImageGenerationRequest(prompt: "hello", count: 3))
        XCTAssertEqual(result.count, 3)
        XCTAssertEqual(result[0].bytes, Data("fake:hello:0".utf8))
        XCTAssertEqual(result[2].bytes, Data("fake:hello:2".utf8))
    }

    func testLocalDeterministicUnconfiguredReturnsEmpty() async throws {
        let g = LocalDeterministicImageGenerator(isConfigured: false)
        let result = try await g.generate(request: ImageGenerationRequest(prompt: "x", count: 2))
        XCTAssertTrue(result.isEmpty)
    }

    func testImageGeneratorIdsConstants() {
        XCTAssertEqual(ImageGeneratorIds.openAi, "openai-images")
        XCTAssertEqual(ImageGeneratorIds.stability, "stability")
    }

    // ── DTO round-trip ──────────────────────────────────────────────────

    func testImageGenerationRequestCodableRoundTrip() throws {
        let req = ImageGenerationRequest(prompt: "p", negativePrompt: "n", size: 768, count: 2, style: "vivid")
        let data = try JSONEncoder().encode(req)
        XCTAssertEqual(try JSONDecoder().decode(ImageGenerationRequest.self, from: data), req)
    }

    /// A configured generator that returns a fixed (possibly empty) artifact list.
    final class ScriptedGenerator: IImageGenerator, @unchecked Sendable {
        let generatorId: String
        let configured: Bool
        let output: [ImageArtifact]
        init(id: String, configured: Bool, output: [ImageArtifact]) {
            self.generatorId = id; self.configured = configured; self.output = output
        }
        var displayLabel: String { generatorId }
        var isConfigured: Bool { configured }
        var statusMessage: String { "scripted" }
        func generate(request: ImageGenerationRequest) async throws -> [ImageArtifact] { output }
    }
}
