// SurfaceHostsTests.swift

import XCTest
@testable import CircleAI

private final class StubSession: ICompanionSession, @unchecked Sendable {
    let sessionId: String
    let identityId: String
    let interface: InterfaceKind

    private let lock = NSLock()
    private var heard: [String] = []
    var reply = "a reply"
    var failWith: Error?

    init(sessionId: String = "s", identityId: String = "id",
         interface: InterfaceKind = .web) {
        self.sessionId = sessionId
        self.identityId = identityId
        self.interface = interface
    }

    var received: [String] { lock.lock(); defer { lock.unlock() }; return heard }

    func send(_ message: String) async throws -> String {
        if let failWith { throw failWith }
        lock.lock(); heard.append(message); lock.unlock()
        return reply
    }

    // The rest of the protocol, unused by these two hosts.
    func stream(_ message: String) -> AsyncStream<String> {
        AsyncStream { $0.finish() }
    }
    func agent(_ instruction: String) async throws -> String { try await send(instruction) }
    func getContext() -> CompanionContext {
        CompanionContext(identityId: identityId, displayName: identityId,
                         interface: interface, personaHints: "", affectSummary: "",
                         recentMemorySnippets: [], activeGoals: [])
    }
    func refreshContext() async throws {}
    var history: [CompanionTurn] { [] }
    func signalFeedback(positive: Bool, note: String?) async throws {}
    var proactiveEvents: AsyncStream<CompanionProactiveEvent> {
        AsyncStream { $0.finish() }
    }
}

private final class CountingFactory: ICompanionSessionFactory, @unchecked Sendable {
    private let lock = NSLock()
    private var made = 0
    var lastInterface: InterfaceKind?

    var createCount: Int { lock.lock(); defer { lock.unlock() }; return made }

    func create(identityId: String, interface: InterfaceKind) async throws -> ICompanionSession {
        lock.lock(); made += 1; lastInterface = interface; lock.unlock()
        return StubSession(identityId: identityId, interface: interface)
    }
}

final class SurfaceHostsTests: XCTestCase {

    // MARK: - Web

    func testTheSessionIsCreatedOnceAndReused() async throws {
        // A surface that builds a new session per request starts every message
        // with an empty conversation, which reads as an assistant with no
        // memory rather than as a wiring mistake.
        let factory = CountingFactory()
        let web = WebCompanionService(factory: factory)

        try await web.initialise(identityId: "u1")
        try await web.initialise(identityId: "u1")
        try await web.initialise(identityId: "u1")

        XCTAssertEqual(factory.createCount, 1)
    }

    func testTheWebSurfaceIsRequestedAsWeb() async throws {
        let factory = CountingFactory()
        try await WebCompanionService(factory: factory).initialise(identityId: "u1")
        XCTAssertEqual(factory.lastInterface, .web)
    }

    func testAskingForTheSessionBeforeInitialisingIsANamedFailure() {
        // Not optional: every caller would unwrap it, and the nil case has one
        // cause worth stating out loud.
        let web = WebCompanionService(factory: CountingFactory())
        XCTAssertFalse(web.isInitialised)
        XCTAssertThrowsError(try web.session()) {
            XCTAssertEqual($0 as? WebCompanionError, .notInitialised)
        }
    }

    func testTheSessionIsAvailableAfterInitialising() async throws {
        let web = WebCompanionService(factory: CountingFactory())
        try await web.initialise(identityId: "u1")
        XCTAssertTrue(web.isInitialised)
        XCTAssertEqual(try web.session().identityId, "u1")
    }

    func testClosingReleasesTheSession() async throws {
        let web = WebCompanionService(factory: CountingFactory())
        try await web.initialise(identityId: "u1")
        await web.close()
        XCTAssertFalse(web.isInitialised)
        XCTAssertThrowsError(try web.session())
    }

    // MARK: - IoT

    private func iot(_ session: StubSession,
                     tts: (any ITtsEngine)? = nil) -> IoTCompanionPipeline {
        IoTCompanionPipeline(session: session,
                             wakeWord: NullWakeWordDetector(),
                             transcriber: NullVoiceTranscriber(),
                             tts: tts)
    }

    func testAnUtteranceReachesTheSessionAndTheReplyIsSynthesised() async {
        let session = StubSession(interface: .iot)
        let tts = NullTtsEngine()
        let pipeline = iot(session, tts: tts)

        let audio = AudioBox()
        pipeline.onAudioReady = { audio.add($0) }

        await pipeline.handle(utterance: "turn on the light")

        XCTAssertEqual(session.received, ["turn on the light"])
        XCTAssertEqual(audio.all.count, 1)
    }

    func testABlankUtteranceIsNotSent() async {
        let session = StubSession()
        await iot(session).handle(utterance: "   ")
        XCTAssertTrue(session.received.isEmpty)
    }

    func testAFailureIsReportedAndTheDeviceKeepsRunning() async {
        // An embedded device has no screen and nobody standing next to it: a
        // pipeline that dies on a bad utterance is a speaker that stops working
        // until somebody power-cycles it.
        struct Boom: Error {}
        let session = StubSession()
        session.failWith = Boom()
        let pipeline = iot(session)

        let faults = ErrorBox()
        pipeline.onFaulted = { faults.add($0) }

        await pipeline.handle(utterance: "hello")
        XCTAssertEqual(faults.count, 1)

        // And the next utterance still goes through.
        session.failWith = nil
        await pipeline.handle(utterance: "again")
        XCTAssertEqual(session.received, ["again"])
    }

    func testNoTtsMeansNoAudioButTheTurnStillHappens() async {
        let session = StubSession()
        let pipeline = iot(session, tts: nil)
        let audio = AudioBox()
        pipeline.onAudioReady = { audio.add($0) }

        await pipeline.handle(utterance: "hello")
        XCTAssertEqual(session.received, ["hello"])
        XCTAssertEqual(audio.all.count, 0)
    }

    func testABlankReplyIsNotSynthesised() async {
        let session = StubSession()
        session.reply = "  "
        let pipeline = iot(session, tts: NullTtsEngine())
        let audio = AudioBox()
        pipeline.onAudioReady = { audio.add($0) }

        await pipeline.handle(utterance: "hello")
        XCTAssertEqual(audio.all.count, 0)
    }

    func testClosingTwiceIsHarmless() async {
        let pipeline = iot(StubSession())
        await pipeline.close()
        await pipeline.close()
    }

    // MARK: - Identity

    private func identity(_ id: String, name: String? = nil) -> CircleIdentity {
        CircleIdentity(identityId: id, displayName: name ?? id, tier: .pseudonymous,
                       deviceIds: [], createdAt: Date(timeIntervalSince1970: 0),
                       lastSeenAt: Date(timeIntervalSince1970: 0))
    }

    private func device(_ deviceId: String, _ identityId: String) -> RegisteredDevice {
        RegisteredDevice(deviceId: deviceId, identityId: identityId, platform: "test",
                         registeredAt: Date(timeIntervalSince1970: 0),
                         lastActiveAt: Date(timeIntervalSince1970: 0))
    }

    func testAnIdentityRoundTrips() async throws {
        let s = InMemoryIdentityStore()
        try await s.save(identity("u1"))
        let got = try await s.get(identityId: "u1")
        XCTAssertEqual(got?.identityId, "u1")
    }

    func testAnUnknownIdentityIsNil() async throws {
        let got = try await InMemoryIdentityStore().get(identityId: "nobody")
        XCTAssertNil(got)
    }

    func testSavingTwiceReplaces() async throws {
        let s = InMemoryIdentityStore()
        try await s.save(identity("u1", name: "old"))
        try await s.save(identity("u1", name: "new"))
        let got = try await s.get(identityId: "u1")
        XCTAssertEqual(got?.displayName, "new")
    }

    func testDevicesAreListedPerIdentity() async throws {
        let s = InMemoryIdentityStore()
        try await s.registerDevice(device("d1", "u1"))
        try await s.registerDevice(device("d2", "u1"))
        try await s.registerDevice(device("d3", "u2"))

        let mine = try await s.getDevices(identityId: "u1")
        XCTAssertEqual(mine.map(\.deviceId), ["d1", "d2"])
    }

    func testTheDeviceListDoesNotReorderBetweenCalls() async throws {
        // A "your devices" screen that shuffles on every refresh looks broken
        // even though nothing changed.
        let s = InMemoryIdentityStore()
        for id in ["z", "m", "a", "q"] { try await s.registerDevice(device(id, "u1")) }

        let first = try await s.getDevices(identityId: "u1").map(\.deviceId)
        for _ in 0..<5 {
            let again = try await s.getDevices(identityId: "u1").map(\.deviceId)
            XCTAssertEqual(again, first)
        }
        XCTAssertEqual(first, ["a", "m", "q", "z"])
    }

    func testTheReverseLookupFindsTheOwner() async throws {
        let s = InMemoryIdentityStore()
        try await s.save(identity("u1"))
        try await s.registerDevice(device("d1", "u1"))
        let owner = try await s.getByDevice(deviceId: "d1")
        XCTAssertEqual(owner?.identityId, "u1")
    }

    func testADeviceWhoseOwnerWasNeverSavedIsNil() async throws {
        // The device row exists, the person does not, and pretending otherwise
        // puts an empty name on a screen.
        let s = InMemoryIdentityStore()
        try await s.registerDevice(device("d1", "ghost"))
        let owner = try await s.getByDevice(deviceId: "d1")
        XCTAssertNil(owner)
    }

    func testAnUnknownDeviceIsNil() async throws {
        let owner = try await InMemoryIdentityStore().getByDevice(deviceId: "d9")
        XCTAssertNil(owner)
    }

    func testRegisteringTheSameDeviceTwiceReplaces() async throws {
        let s = InMemoryIdentityStore()
        try await s.registerDevice(device("d1", "u1"))
        try await s.registerDevice(device("d1", "u2"))
        let hoisted1 = try await s.getDevices(identityId: "u1").isEmpty
        XCTAssertTrue(hoisted1)
        let hoisted2 = try await s.getDevices(identityId: "u2").count
        XCTAssertEqual(hoisted2, 1)
    }

    // MARK: - Null media library

    func testTheNullLibraryFindsNothingAndSaysSo() async throws {
        let l = NullMediaLibrary.instance
        XCTAssertEqual(l.backendId, "null")
        let hoisted3 = try await l.get("anything")
        XCTAssertNil(hoisted3)
        let hoisted4 = try await l.search("anything", topK: 10).isEmpty
        XCTAssertTrue(hoisted4)
        let hoisted5 = try await l.search("anything").isEmpty
        XCTAssertTrue(hoisted5)
    }

    // MARK: - Hosting options

    func testEnrichmentDefaultsToAlwaysBecauseSilenceIsWorse() {
        // Before this existed a host that set its own system prompt silently
        // lost persona, device context and recall. That presents as "the
        // assistant forgot", which nobody debugs as a dropped feature.
        XCTAssertEqual(AIOptions().systemPromptEnrichment, .always)
        XCTAssertEqual(SystemPromptEnrichment.allCases.count, 2)
    }

    func testEnrichmentCanBeTurnedDownForFullControl() {
        let o = AIOptions(systemPromptEnrichment: .onlyWhenAbsent)
        XCTAssertEqual(o.systemPromptEnrichment, .onlyWhenAbsent)
    }

    func testVoiceOptionDefaultsAreTheSafeOnes() {
        let v = VoiceOptions()
        XCTAssertEqual(v.wakeWord, "hey b")
        XCTAssertEqual(v.sampleRateHz, 16_000, "every catalogued model was trained here")
        XCTAssertFalse(v.autoStart, "a library does not open a microphone by itself")
        XCTAssertEqual(v.ttsBackend, "null")
        XCTAssertEqual(v.endOfSpeechSilenceMs, 800)
    }

    func testTheWakeWordIsNormalisedSoTwoHostsAgree() {
        // "Hey B" and "hey b" are the same configuration of the same thing.
        XCTAssertEqual(VoiceOptions(wakeWord: "Hey B").wakeWord, "hey b")
        XCTAssertEqual(VoiceOptions(wakeWord: "HEY B"), VoiceOptions(wakeWord: "hey b"))
    }

    func testVoiceOptionsRoundTrip() throws {
        let v = VoiceOptions(wakeWord: "circle", sampleRateHz: 8000, autoStart: true,
                             ttsBackend: "onnx", endOfSpeechSilenceMs: 500)
        let back = try JSONDecoder().decode(VoiceOptions.self, from: try JSONEncoder().encode(v))
        XCTAssertEqual(back, v)
    }
}

private final class AudioBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [TtsSynthesisResult] = []
    func add(_ r: TtsSynthesisResult) { lock.lock(); stored.append(r); lock.unlock() }
    var all: [TtsSynthesisResult] { lock.lock(); defer { lock.unlock() }; return stored }
}

private final class ErrorBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [Error] = []
    func add(_ e: Error) { lock.lock(); stored.append(e); lock.unlock() }
    var count: Int { lock.lock(); defer { lock.unlock() }; return stored.count }
}
