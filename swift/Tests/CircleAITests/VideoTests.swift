// VideoTests.swift
//
// Verifies the CircleAI.Video ports (Video.swift): fail-closed null generators,
// the thread-safe in-memory style catalogue, resolution presets, StyleId
// semantics, and the device-cannot-satisfy error path on a custom generator.

import XCTest
@testable import CircleAI

final class VideoTests: XCTestCase {

    // ── StyleId / VideoResolution primitives ────────────────────────────

    func testStyleIdDescriptionAndEquality() {
        let a = StyleId("noir-detective")
        XCTAssertEqual(a.value, "noir-detective")
        XCTAssertEqual(a.description, "noir-detective")
        XCTAssertEqual("\(a)", "noir-detective")
        XCTAssertEqual(a, StyleId("noir-detective"))
        XCTAssertNotEqual(a, StyleId("space-opera"))
    }

    func testStyleIdCodableRoundTrip() throws {
        let id = StyleId("pooh-1926")
        let data = try JSONEncoder().encode(id)
        XCTAssertEqual(try JSONDecoder().decode(StyleId.self, from: data), id)
    }

    func testVideoResolutionPresets() {
        XCTAssertEqual(VideoResolution.p480, VideoResolution(width: 720, height: 480))
        XCTAssertEqual(VideoResolution.p720, VideoResolution(width: 1280, height: 720))
        XCTAssertEqual(VideoResolution.p1080, VideoResolution(width: 1920, height: 1080))
    }

    // ── NullVideoGenerator ──────────────────────────────────────────────

    func testNullVideoGeneratorReturnsEmptyVideoEchoingResolution() async throws {
        let g = NullVideoGenerator.instance
        XCTAssertEqual(g.backendId, "null")
        let req = VideoGenerationRequest(prompt: "hi", duration: 5, resolution: .p720)
        let result = try await g.generate(request: req)
        XCTAssertTrue(result.videoBytes.isEmpty)
        XCTAssertEqual(result.mimeType, "video/mp4")
        XCTAssertEqual(result.duration, 0)
        XCTAssertEqual(result.frameCount, 0)
        XCTAssertEqual(result.resolution, .p720)   // echoes the request resolution
        XCTAssertEqual(result.backendId, "null")
    }

    // ── NullStyleScript ─────────────────────────────────────────────────

    func testNullStyleScriptEchoesSourceUnchanged() async throws {
        let s = NullStyleScript.instance
        XCTAssertEqual(s.backendId, "null")
        let req = StyleScriptRequest(sourceMessage: "call me back", style: StyleId("noir"))
        let result = try await s.rewrite(request: req)
        XCTAssertEqual(result.rewrittenText, "call me back")
        XCTAssertEqual(result.style, StyleId("noir"))
        XCTAssertNil(result.voicePersonaId)
        XCTAssertEqual(result.estimatedSpokenDuration, 0)
    }

    // ── InMemoryStyleReference ──────────────────────────────────────────

    private func makeStyle(_ id: String, name: String = "n") -> StyleReference {
        StyleReference(
            id: StyleId(id),
            displayName: name,
            shortDescription: "desc",
            attribution: StyleAttribution(source: "Public Domain", license: "CC0", url: nil),
            voicePersonaId: "voice-\(id)",
            frames: [StyleReferenceFrame(imageBytes: Data([1, 2, 3]), mimeType: "image/png", caption: "cap")])
    }

    func testInMemoryStyleReferenceRegisterGetList() async throws {
        let cat = InMemoryStyleReference()
        XCTAssertEqual(cat.backendId, "in-memory")

        try await cat.register(makeStyle("noir"))
        try await cat.register(makeStyle("space-opera"))

        let noir = try await cat.get(StyleId("noir"))
        XCTAssertNotNil(noir)
        XCTAssertEqual(noir?.voicePersonaId, "voice-noir")

        let all = try await cat.list()
        XCTAssertEqual(Set(all.map { $0.id.value }), ["noir", "space-opera"])
    }

    func testInMemoryStyleReferenceCaseInsensitiveKeying() async throws {
        let cat = InMemoryStyleReference()
        try await cat.register(makeStyle("Noir", name: "first"))
        // Re-register under a different-cased id → same key, overwrites.
        try await cat.register(makeStyle("NOIR", name: "second"))

        let byLower = try await cat.get(StyleId("noir"))
        XCTAssertEqual(byLower?.displayName, "second")
        let all = try await cat.list()
        XCTAssertEqual(all.count, 1)     // one logical entry
    }

    func testInMemoryStyleReferenceMissingReturnsNil() async throws {
        let cat = InMemoryStyleReference()
        let missing = try await cat.get(StyleId("does-not-exist"))
        XCTAssertNil(missing)
    }

    func testInMemoryStyleReferenceConcurrentRegistersAreSafe() async throws {
        let cat = InMemoryStyleReference()
        await withTaskGroup(of: Void.self) { group in
            for i in 0..<50 {
                group.addTask { try? await cat.register(self.makeStyle("style-\(i)")) }
            }
        }
        let all = try await cat.list()
        XCTAssertEqual(all.count, 50)
    }

    // ── Custom generator error path ─────────────────────────────────────

    /// Generator that refuses when the requested resolution exceeds a cap —
    /// exercises the `IVideoGenerator` "throws if the device cannot satisfy"
    /// contract with a real thrown `VideoGenerationError`.
    final class CappedVideoGenerator: IVideoGenerator, @unchecked Sendable {
        let maxHeight: Int
        init(maxHeight: Int) { self.maxHeight = maxHeight }
        var backendId: String { "capped" }
        func generate(request: VideoGenerationRequest) async throws -> VideoGenerationResult {
            if request.resolution.height > maxHeight {
                throw VideoGenerationError.deviceCannotSatisfyRequest(
                    "resolution \(request.resolution.height)p exceeds \(maxHeight)p")
            }
            let frames = Int((request.duration * Double(request.frameRate)).rounded())
            return VideoGenerationResult(
                videoBytes: Data("video".utf8), mimeType: "video/mp4",
                duration: request.duration, frameCount: frames,
                resolution: request.resolution, backendId: backendId)
        }
    }

    func testVideoGeneratorThrowsWhenDeviceCannotSatisfy() async throws {
        let g = CappedVideoGenerator(maxHeight: 720)
        do {
            _ = try await g.generate(request: VideoGenerationRequest(prompt: "x", duration: 3, resolution: .p1080))
            XCTFail("expected throw")
        } catch let VideoGenerationError.deviceCannotSatisfyRequest(msg) {
            XCTAssertTrue(msg.contains("1080"))
        }
    }

    func testVideoGeneratorSucceedsWithinCap() async throws {
        let g = CappedVideoGenerator(maxHeight: 1080)
        let result = try await g.generate(
            request: VideoGenerationRequest(prompt: "x", duration: 2, resolution: .p720, frameRate: 24))
        XCTAssertEqual(result.frameCount, 48)   // 2s * 24fps
        XCTAssertEqual(result.backendId, "capped")
        XCTAssertEqual(result.resolution, .p720)
    }

    // ── Request/result carriers ─────────────────────────────────────────

    func testVideoGenerationRequestCarriesOptionalGrounding() {
        let frame = StyleReferenceFrame(imageBytes: Data([1]), mimeType: "image/png")
        let audio = AudioTrack(audioPcm16Mono: Data([2, 3]), sampleRateHz: 16_000, duration: 1.5)
        let req = VideoGenerationRequest(
            prompt: "p", duration: 4, resolution: .p480, frameRate: 30,
            styleId: StyleId("noir"), referenceImage: frame, audioTrack: audio, seed: 42)
        XCTAssertEqual(req.frameRate, 30)
        XCTAssertEqual(req.styleId, StyleId("noir"))
        XCTAssertEqual(req.referenceImage?.mimeType, "image/png")
        XCTAssertEqual(req.audioTrack?.sampleRateHz, 16_000)
        XCTAssertEqual(req.seed, 42)
    }

    func testVideoGenerationRequestDefaults() {
        let req = VideoGenerationRequest(prompt: "p", duration: 1, resolution: .p720)
        XCTAssertEqual(req.frameRate, 24)   // C# default
        XCTAssertNil(req.styleId)
        XCTAssertNil(req.referenceImage)
        XCTAssertNil(req.audioTrack)
        XCTAssertNil(req.seed)
    }
}
