// MediaTests.swift
//
// Validates the CircleAI.Media port (Media.swift): MediaKind ordinals,
// MediaAsset Codable round-trip, InMemoryMediaLibrary (add/get/listByKind newest-
// first, title-substring search with topK + case-insensitivity + ordering + arg
// guards), MediaDomainContext constants, and the MediaCompanionAdapter decorator
// (domain-prompt prefixing + authoring helpers + forwarded surface) wired to a
// deterministic in-memory ICompanionSession fake.

import XCTest
@testable import CircleAI

final class MediaTests: XCTestCase {

    // ── MediaKind ────────────────────────────────────────────────────────────

    func testMediaKindOrdinals() {
        XCTAssertEqual(MediaKind.audio.rawValue, 0)
        XCTAssertEqual(MediaKind.video.rawValue, 1)
        XCTAssertEqual(MediaKind.image.rawValue, 2)
        XCTAssertEqual(MediaKind.allCases.count, 3)
    }

    // ── MediaAsset Codable ───────────────────────────────────────────────────

    func testMediaAssetCodableRoundTrip() throws {
        let a = MediaAsset(
            assetId: "a1", title: "Intro", kind: .video,
            duration: 12.5, bytes: 4096, mime: "video/mp4",
            createdAtUtc: Date(timeIntervalSince1970: 100))
        let data = try JSONEncoder().encode(a)
        let back = try JSONDecoder().decode(MediaAsset.self, from: data)
        XCTAssertEqual(a, back)
    }

    func testMediaAssetNilDurationRoundTrips() throws {
        let a = MediaAsset(
            assetId: "a2", title: "Photo", kind: .image,
            duration: nil, bytes: 10, mime: "image/png",
            createdAtUtc: Date(timeIntervalSince1970: 5))
        let back = try JSONDecoder().decode(MediaAsset.self, from: try JSONEncoder().encode(a))
        XCTAssertEqual(back.duration, nil)
        XCTAssertEqual(a, back)
    }

    // ── InMemoryMediaLibrary ─────────────────────────────────────────────────

    private func asset(_ id: String, _ title: String, _ kind: MediaKind, _ created: TimeInterval) -> MediaAsset {
        MediaAsset(assetId: id, title: title, kind: kind, duration: nil, bytes: 0, mime: "x",
                   createdAtUtc: Date(timeIntervalSince1970: created))
    }

    func testAddAndGet() throws {
        let lib = InMemoryMediaLibrary()
        XCTAssertNil(lib.get("missing"))
        let a = asset("a1", "Song", .audio, 1)
        try lib.add(a)
        XCTAssertEqual(lib.get("a1"), a)
    }

    func testAddReplacesSameId() throws {
        let lib = InMemoryMediaLibrary()
        try lib.add(asset("a1", "Old", .audio, 1))
        try lib.add(asset("a1", "New", .audio, 2))
        XCTAssertEqual(lib.get("a1")?.title, "New")
    }

    func testAddThrowsOnBlankAssetId() {
        let lib = InMemoryMediaLibrary()
        XCTAssertThrowsError(try lib.add(asset("   ", "T", .audio, 1))) { err in
            XCTAssertEqual(err as? MediaLibraryError, .assetIdRequired)
        }
    }

    func testListByKindNewestFirst() throws {
        let lib = InMemoryMediaLibrary()
        try lib.add(asset("v1", "Vid1", .video, 10))
        try lib.add(asset("v2", "Vid2", .video, 30))
        try lib.add(asset("v3", "Vid3", .video, 20))
        try lib.add(asset("a1", "Aud1", .audio, 99))
        let vids = lib.listByKind(.video)
        XCTAssertEqual(vids.map { $0.assetId }, ["v2", "v3", "v1"]) // OrderByDescending(CreatedAtUtc)
        XCTAssertTrue(vids.allSatisfy { $0.kind == .video })
        XCTAssertEqual(lib.listByKind(.image).count, 0)
    }

    func testSearchCaseInsensitiveSubstringNewestFirst() throws {
        let lib = InMemoryMediaLibrary()
        try lib.add(asset("1", "The Great Documentary", .video, 10))
        try lib.add(asset("2", "great expectations", .audio, 30))
        try lib.add(asset("3", "Unrelated", .image, 20))
        let hits = try lib.search("great")
        XCTAssertEqual(hits.map { $0.assetId }, ["2", "1"]) // newest-first
    }

    func testSearchTopKCap() throws {
        let lib = InMemoryMediaLibrary()
        for i in 0..<5 { try lib.add(asset("\(i)", "match \(i)", .audio, TimeInterval(i))) }
        let hits = try lib.search("match", topK: 3)
        XCTAssertEqual(hits.count, 3)
        // newest-first: 4, 3, 2
        XCTAssertEqual(hits.map { $0.assetId }, ["4", "3", "2"])
    }

    func testSearchDefaultTopKIs20() throws {
        let lib = InMemoryMediaLibrary()
        for i in 0..<25 { try lib.add(asset("\(i)", "m\(i)", .audio, TimeInterval(i))) }
        XCTAssertEqual(try lib.search("m").count, 20)
    }

    func testSearchThrowsOnNonPositiveTopK() {
        let lib = InMemoryMediaLibrary()
        XCTAssertThrowsError(try lib.search("x", topK: 0)) { err in
            XCTAssertEqual(err as? MediaLibraryError, .topKOutOfRange)
        }
        XCTAssertThrowsError(try lib.search("x", topK: -1)) { err in
            XCTAssertEqual(err as? MediaLibraryError, .topKOutOfRange)
        }
    }

    // ── MediaDomainContext ───────────────────────────────────────────────────

    func testDomainContextConstants() {
        XCTAssertTrue(MediaDomainContext.systemPromptSnippet.hasPrefix("[DOMAIN: Media]"))
        XCTAssertEqual(MediaDomainContext.complianceFlags,
                       ["ICASA", "BCCSA", "Copyright_Act_98_1978", "POPIA"])
        XCTAssertEqual(MediaDomainContext.suggestedTools,
                       ["content_planner", "analytics", "video_editor", "social_media_api"])
    }

    // ── MediaCompanionAdapter ────────────────────────────────────────────────

    /// A deterministic in-memory `ICompanionSession` recording what it received.
    private final class RecordingSession: ICompanionSession, @unchecked Sendable {
        let lock = NSLock()
        private(set) var lastSend: String?
        private(set) var lastAgent: String?
        private(set) var lastStream: String?

        var sessionId: String { "sess-1" }
        var identityId: String { "id-1" }
        var interface: InterfaceKind { .web }
        var history: [CompanionTurn] { [CompanionTurn(role: "user", content: "hi")] }

        func send(_ message: String) async throws -> String {
            lock.lock(); lastSend = message; lock.unlock()
            return "reply:\(message.count)"
        }
        func stream(_ message: String) -> AsyncStream<String> {
            lock.lock(); lastStream = message; lock.unlock()
            return AsyncStream { cont in cont.yield("tok"); cont.finish() }
        }
        func agent(_ instruction: String) async throws -> String {
            lock.lock(); lastAgent = instruction; lock.unlock()
            return "agent-done"
        }
        func getContext() -> CompanionContext {
            CompanionContext(identityId: "id-1", displayName: "Neo", interface: .web,
                             personaHints: "", affectSummary: "", recentMemorySnippets: [], activeGoals: [])
        }
        func refreshContext() async throws {}
        func signalFeedback(positive: Bool, note: String?) async throws {}
        var proactiveEvents: AsyncStream<CompanionProactiveEvent> { AsyncStream { $0.finish() } }

        // sync reads for the test
        func readSend() -> String? { lock.lock(); defer { lock.unlock() }; return lastSend }
        func readAgent() -> String? { lock.lock(); defer { lock.unlock() }; return lastAgent }
        func readStream() -> String? { lock.lock(); defer { lock.unlock() }; return lastStream }
    }

    func testAdapterForwardsIdentitySurface() {
        let inner = RecordingSession()
        let adapter = MediaCompanionAdapter(inner)
        XCTAssertEqual(adapter.sessionId, "sess-1")
        XCTAssertEqual(adapter.identityId, "id-1")
        XCTAssertEqual(adapter.interface, .web)
        XCTAssertEqual(adapter.history.count, 1)
        XCTAssertEqual(adapter.getContext().displayName, "Neo")
    }

    func testAdapterPrefixesDomainPromptOnSend() async throws {
        let inner = RecordingSession()
        let adapter = MediaCompanionAdapter(inner)
        _ = try await adapter.send("hello")
        let seen = inner.readSend()
        XCTAssertNotNil(seen)
        XCTAssertTrue(seen!.hasPrefix(MediaDomainContext.systemPromptSnippet))
        XCTAssertTrue(seen!.hasSuffix("\n\nhello"))
    }

    func testAdapterPrefixesDomainPromptOnAgentAndStream() async throws {
        let inner = RecordingSession()
        let adapter = MediaCompanionAdapter(inner)
        _ = try await adapter.agent("do it")
        XCTAssertEqual(inner.readAgent(), "\(MediaDomainContext.systemPromptSnippet)\n\ndo it")

        let stream = adapter.stream("go")
        var toks: [String] = []
        for await t in stream { toks.append(t) }
        XCTAssertEqual(toks, ["tok"])
        XCTAssertEqual(inner.readStream(), "\(MediaDomainContext.systemPromptSnippet)\n\ngo")
    }

    func testAuthoringHelpersRouteThroughAgentWithoutDomainPrefix() async throws {
        // The authoring helpers call inner.agent directly with their own prompt —
        // they do NOT go through the domain-prefix `enrich` (mirrors the C#).
        let inner = RecordingSession()
        let adapter = MediaCompanionAdapter(inner)

        _ = try await adapter.createContentBrief(topic: "AI", audience: "devs", platform: "YouTube")
        XCTAssertEqual(inner.readAgent(),
            "Create a detailed content brief for YouTube: Topic: AI. Target audience: devs. Include angle, key messages, SEO keywords, call to action, and production notes.")

        _ = try await adapter.analyseAudienceData("CTR 3%")
        XCTAssertEqual(inner.readAgent(),
            "Analyse this audience/analytics data and provide actionable content strategy recommendations:\nCTR 3%")

        _ = try await adapter.draftPressRelease(announcement: "Launch", audience: "press")
        XCTAssertEqual(inner.readAgent(),
            "Draft a press release on: Launch for press. AP style, inverted pyramid, quote from leadership, boilerplate.")

        _ = try await adapter.suggestThumbnailConcepts(videoTopic: "Rust", channelStyle: "minimal")
        XCTAssertEqual(inner.readAgent(),
            "Suggest 3 thumbnail concepts for a video on 'Rust' in minimal style. Hook, composition, text.")

        _ = try await adapter.structureNarrative(topic: "Space", format: "doc", durationMinutes: 8)
        XCTAssertEqual(inner.readAgent(),
            "Structure a 8-min doc on 'Space'. Hook, beats, payoff, CTA.")

        _ = try await adapter.writeCaption(mediaDescription: "sunset", platform: "IG", voice: "warm")
        XCTAssertEqual(inner.readAgent(),
            "Write a IG caption for: sunset. Voice: warm. Optimise for platform's algorithm + accessibility.")
    }
}
