// TelephonyCallEconomicsTests.swift

import XCTest
@testable import CircleAI

final class TelephonyCallEconomicsTests: XCTestCase {

    // MARK: - AMD

    /// One frame of PCM16 at 8 kHz. `loud` decides whether it reads as speech.
    private func frame(ms: Int, loud: Bool, rate: Int = 8000) -> [UInt8] {
        let samples = ms * rate / 1000
        var out = [UInt8](); out.reserveCapacity(samples * 2)
        for i in 0..<samples {
            let v: Int16 = loud
                ? Int16(truncatingIfNeeded: Int(sin(Double(i) * 0.3) * 12000))
                : 0
            let u = UInt16(bitPattern: v)
            out.append(UInt8(u & 0xFF)); out.append(UInt8(u >> 8))
        }
        return out
    }

    private func feed(_ d: AnsweringMachineDetector, ms: Int, loud: Bool, rate: Int = 8000) {
        var left = ms
        while left > 0 {
            let step = min(20, left)
            d.observe(pcmFrame: frame(ms: step, loud: loud, rate: rate), sampleRateHz: rate)
            left -= step
        }
    }

    func testAShortGreetingFollowedBySilenceIsAHuman() {
        // "Hello?" — under the ceiling and over the floor, then they wait.
        let d = AnsweringMachineDetector()
        feed(d, ms: 700, loud: true)
        feed(d, ms: 400, loud: false)
        XCTAssertEqual(d.currentVerdict, .human)
    }

    func testALongUnbrokenGreetingIsAMachine() {
        // "Hi, you've reached Thabo, I can't take your call right now…"
        let d = AnsweringMachineDetector()
        feed(d, ms: 2000, loud: true)
        XCTAssertEqual(d.currentVerdict, .answeringMachine)
    }

    func testItDecidesTheMachineBeforeTheRecordingEvenStops() {
        // Waiting for a 30-second greeting to finish is the same as not
        // detecting it: the caller is holding a live call open.
        let d = AnsweringMachineDetector()
        feed(d, ms: 1820, loud: true)
        XCTAssertEqual(d.currentVerdict, .answeringMachine)
    }

    func testACoughIsNotAGreeting() {
        // Under the floor: a click, line noise or a cough. Kept UNKNOWN so the
        // orchestrator waits rather than talking over somebody.
        let d = AnsweringMachineDetector()
        feed(d, ms: 120, loud: true)
        feed(d, ms: 400, loud: false)
        XCTAssertEqual(d.currentVerdict, .unknown)
    }

    func testNearSilenceForTheWholeWindowStaysUnknownRatherThanGuessing() {
        // Nobody spoke. Guessing "machine" here leaves a voicemail on a line a
        // person is holding to their ear.
        let d = AnsweringMachineDetector()
        feed(d, ms: 4000, loud: false)
        XCTAssertEqual(d.currentVerdict, .unknown)
    }

    func testAVerdictNeverFlipsOnceMade() {
        // Reversing halfway is the one behaviour the person on the other end
        // cannot make sense of.
        let d = AnsweringMachineDetector()
        feed(d, ms: 700, loud: true)
        feed(d, ms: 400, loud: false)
        XCTAssertEqual(d.currentVerdict, .human)

        feed(d, ms: 5000, loud: true)
        XCTAssertEqual(d.currentVerdict, .human)
    }

    func testABreathBetweenWordsDoesNotEndTheUtterance() {
        // 100 ms of quiet is under the 250 ms threshold, so this is ONE
        // utterance of 1900 ms — a machine, not two short human ones.
        let d = AnsweringMachineDetector()
        feed(d, ms: 900, loud: true)
        feed(d, ms: 100, loud: false)
        feed(d, ms: 1000, loud: true)
        XCTAssertEqual(d.currentVerdict, .answeringMachine)
    }

    func testResetClearsTheVerdictForTheNextCall() {
        let d = AnsweringMachineDetector()
        feed(d, ms: 2000, loud: true)
        XCTAssertEqual(d.currentVerdict, .answeringMachine)
        d.reset()
        XCTAssertEqual(d.currentVerdict, .unknown)
    }

    func testTunedThresholdsAreActuallyUsed() {
        // A market that has measured its own numbers must be able to say so.
        let d = AnsweringMachineDetector(options: AmdOptions(humanMaxFirstUtteranceMs: 500))
        feed(d, ms: 600, loud: true)
        XCTAssertEqual(d.currentVerdict, .answeringMachine,
                       "600 ms is over a 500 ms ceiling")
    }

    func testTheDefaultsAreTheDocumentedOnes() {
        let o = AmdOptions()
        XCTAssertEqual(o.humanMaxFirstUtterance, 1800)
        XCTAssertEqual(o.humanMinFirstUtterance, 300)
        XCTAssertEqual(o.maxObservationWindow, 3500)
        XCTAssertEqual(o.silenceFrameThreshold, 250)
    }

    func testPcmIsReadAsSignedNotUnsigned() {
        // Read unsigned, every negative sample becomes a large positive one and
        // silence reads as speech — the detector then calls every call a
        // machine, with nothing failing anywhere.
        var negative = [UInt8]()
        for _ in 0..<160 {
            let u = UInt16(bitPattern: Int16(-1))     // 0xFFFF
            negative.append(UInt8(u & 0xFF)); negative.append(UInt8(u >> 8))
        }
        XCTAssertFalse(AnsweringMachineDetector.frameHasSpeech(negative),
                       "-1 is silence; as unsigned 65535 it would be a shout")
    }

    func testABadSampleRateOrAnEmptyFrameIsIgnored() {
        let d = AnsweringMachineDetector()
        XCTAssertEqual(d.observe(pcmFrame: frame(ms: 100, loud: true), sampleRateHz: 0), .unknown)
        XCTAssertEqual(d.observe(pcmFrame: [1], sampleRateHz: 8000), .unknown)
    }

    // MARK: - Cost

    private let pricing = CallPricing(carrierPerMinute: 0.60, sttPerSecond: 0.004,
                                      ttsPerThousandChars: 0.015,
                                      llmInputPerKToken: 0.003, llmOutputPerKToken: 0.015)

    func testNothingUsedCostsNothing() {
        let c = CallCostCalculator(pricing: pricing)
        XCTAssertEqual(c.currentBreakdown().total, 0, accuracy: 1e-12)
    }

    func testEachAxisIsPricedOnItsOwnUnit() {
        let c = CallCostCalculator(pricing: pricing)
        c.addCarrierTime(120)                 // 2 minutes
        c.addSttTime(90)                      // 90 seconds
        c.addTtsCharacters(2000)              // 2k characters
        c.addLlmTokens(input: 4000, output: 1000)

        let b = c.currentBreakdown()
        XCTAssertEqual(b.carrier, 1.20, accuracy: 1e-9)
        XCTAssertEqual(b.stt, 0.36, accuracy: 1e-9)
        XCTAssertEqual(b.tts, 0.03, accuracy: 1e-9)
        XCTAssertEqual(b.llmInput, 0.012, accuracy: 1e-9)
        XCTAssertEqual(b.llmOutput, 0.015, accuracy: 1e-9)
        XCTAssertEqual(b.total, 1.617, accuracy: 1e-9)
    }

    func testTheTotalIsAlwaysTheSumOfTheParts() {
        // The breakdown exists so somebody can see WHERE the money went; a
        // total that does not add up makes the whole thing unusable.
        let c = CallCostCalculator(pricing: pricing)
        c.addCarrierTime(37.5)
        c.addSttTime(12.25)
        c.addTtsCharacters(937)
        c.addLlmTokens(input: 1234, output: 567)

        let b = c.currentBreakdown()
        XCTAssertEqual(b.total, b.carrier + b.stt + b.tts + b.llmInput + b.llmOutput,
                       accuracy: 1e-12)
    }

    func testAClockThatGoesBackwardsIsNotARefund() {
        // On a phone a clock does go backwards.
        let c = CallCostCalculator(pricing: pricing)
        c.addCarrierTime(60)
        c.addCarrierTime(-600)
        c.addSttTime(-10)
        c.addTtsCharacters(-500)
        c.addLlmTokens(input: -1000, output: -1000)

        let b = c.currentBreakdown()
        XCTAssertEqual(b.carrier, 0.60, accuracy: 1e-9)
        XCTAssertEqual(b.stt, 0, accuracy: 1e-12)
        XCTAssertEqual(b.tts, 0, accuracy: 1e-12)
        XCTAssertGreaterThanOrEqual(b.total, 0)
    }

    func testUsageAccumulatesRatherThanReplacing() {
        let c = CallCostCalculator(pricing: pricing)
        for _ in 0..<10 { c.addTtsCharacters(100) }
        XCTAssertEqual(c.currentBreakdown().tts, 0.015, accuracy: 1e-9)
    }

    func testSmallFractionsAreNotLostAcrossALongCall() {
        // A fraction of a cent per second across ten minutes is real money, so
        // nothing is rounded on the way IN.
        let c = CallCostCalculator(pricing: CallPricing(
            carrierPerMinute: 0, sttPerSecond: 0.0001, ttsPerThousandChars: 0,
            llmInputPerKToken: 0, llmOutputPerKToken: 0))
        for _ in 0..<600 { c.addSttTime(1) }
        XCTAssertEqual(c.currentBreakdown().stt, 0.06, accuracy: 1e-9)
    }

    func testResetClearsEveryAxis() {
        let c = CallCostCalculator(pricing: pricing)
        c.addCarrierTime(120); c.addLlmTokens(input: 100, output: 100)
        c.reset()
        XCTAssertEqual(c.currentBreakdown().total, 0, accuracy: 1e-12)
    }

    func testFreePricingIsFree() {
        let c = CallCostCalculator(pricing: .free)
        c.addCarrierTime(3600); c.addTtsCharacters(1_000_000)
        XCTAssertEqual(c.currentBreakdown().total, 0, accuracy: 1e-12)
    }

    // MARK: - Tunnels

    func testTheNullTunnelRefusesRatherThanHandingBackLocalhost() async {
        // A carrier posting a webhook to localhost reaches ITSELF, and the call
        // simply never gets a reply.
        let t = NullLocalDevTunnel.instance
        XCTAssertFalse(t.isAvailable)
        do {
            _ = try await t.publicUrl(localPort: 5000)
            XCTFail("must not hand back a URL")
        } catch {
            XCTAssertEqual(error as? LocalDevTunnelError, .notConfigured)
        }
    }

    func testAStaticTunnelReturnsWhatWasPinned() async throws {
        let t = try StaticLocalDevTunnel(publicUrl: URL(string: "https://demo.example.com")!)
        XCTAssertEqual(t.providerId, "static")
        XCTAssertTrue(t.isAvailable)
        let u = try await t.publicUrl(localPort: 5000)
        XCTAssertEqual(u.absoluteString, "https://demo.example.com")
    }

    func testARelativeUrlIsRefusedAtConstructionNotDuringACall() {
        // Discovered at first use means discovered during a live call.
        XCTAssertThrowsError(try StaticLocalDevTunnel(publicUrl: URL(string: "/webhook")!)) {
            guard case .notAbsolute = ($0 as? LocalDevTunnelError) else {
                return XCTFail("expected notAbsolute")
            }
        }
    }

    func testTheResolvedTunnelsPassThePortThrough() async throws {
        // The port is the whole input; a resolver that never sees it opens a
        // tunnel to the wrong process.
        let seen = PortBox()
        let cf = CloudflareTunnel { port in
            seen.record(port)
            return URL(string: "https://\(port).trycloudflare.com")!
        }
        XCTAssertEqual(cf.providerId, "cloudflare")
        let cfUrl = try await cf.publicUrl(localPort: 7331)
        XCTAssertEqual(cfUrl.absoluteString, "https://7331.trycloudflare.com")
        XCTAssertEqual(seen.ports, [7331])

        let ng = NgrokTunnel { port in URL(string: "https://\(port).ngrok.io")! }
        XCTAssertEqual(ng.providerId, "ngrok")
        let ngUrl = try await ng.publicUrl(localPort: 22)
        XCTAssertEqual(ngUrl.absoluteString, "https://22.ngrok.io")
    }

    // MARK: - MCP import

    private func mcpResponse(_ tools: String) -> Data {
        Data("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"tools\":[\(tools)]}}".utf8)
    }

    func testToolsAreParsedWithTheirSchemas() {
        let body = mcpResponse("""
            {"name":"search","description":"Find things","inputSchema":{"type":"object"}},
            {"name":"send","description":"Send a thing","inputSchema":{"type":"object"}}
            """)
        let out = HttpMcpToolImporter.parse(body, prefix: nil)
        XCTAssertEqual(out.map(\.name), ["search", "send"])
        XCTAssertTrue(out[0].inputJsonSchema.contains("object"))
    }

    func testThePrefixKeepsTwoServersFromColliding() {
        // Two MCP servers both offering "search" is normal, and the prefix also
        // lets somebody reading a transcript see which one answered.
        let body = mcpResponse("{\"name\":\"search\",\"description\":\"\"}")
        let out = HttpMcpToolImporter.parse(body, prefix: "crm_")
        XCTAssertEqual(out[0].name, "crm_search")
        XCTAssertEqual(out[0].originalName, "search", "the remote still knows it as 'search'")
    }

    func testAToolWithNoNameIsSkippedNotRegisteredUnderNothing() {
        // An empty name would shadow the next tool registered.
        let body = mcpResponse("""
            {"name":"","description":"nameless"},
            {"description":"no name at all"},
            {"name":"   "},
            {"name":"real","description":"fine"}
            """)
        XCTAssertEqual(HttpMcpToolImporter.parse(body, prefix: nil).map(\.name), ["real"])
    }

    func testAMissingSchemaBecomesAnEmptyObjectNotNothing() {
        let body = mcpResponse("{\"name\":\"x\",\"description\":\"y\"}")
        XCTAssertEqual(HttpMcpToolImporter.parse(body, prefix: nil)[0].inputJsonSchema, "{}")
    }

    func testGarbageAndUnexpectedShapesParseToNothing() {
        XCTAssertTrue(HttpMcpToolImporter.parse(Data("not json".utf8), prefix: nil).isEmpty)
        XCTAssertTrue(HttpMcpToolImporter.parse(Data("{}".utf8), prefix: nil).isEmpty)
        XCTAssertTrue(HttpMcpToolImporter.parse(
            Data("{\"result\":{\"tools\":\"nope\"}}".utf8), prefix: nil).isEmpty)
    }

    func testTheForwardingUrlCarriesTheRemoteToolName() {
        let base = URL(string: "https://mcp.example.com/rpc")!
        let u = HttpMcpToolImporter.appendQuery(base, key: "remote_tool", value: "search files")
        XCTAssertTrue(u.absoluteString.hasPrefix("https://mcp.example.com/rpc?"))
        XCTAssertTrue(u.absoluteString.contains("remote_tool=search%20files"))
    }

    func testAnExistingQueryIsPreservedNotReplaced() {
        let base = URL(string: "https://mcp.example.com/rpc?v=2")!
        let u = HttpMcpToolImporter.appendQuery(base, key: "remote_tool", value: "x")
        XCTAssertTrue(u.absoluteString.contains("v=2"))
        XCTAssertTrue(u.absoluteString.contains("remote_tool=x"))
    }

    func testAToolServerThatIsDownDoesNotTakeTheCallDown() async throws {
        // The call proceeds with whatever tools it already had.
        let importer = HttpMcpToolImporter(send: { _ in (Data(), 503) })
        let registry = RecordingRegistry()
        let out = try await importer.import(
            into: registry,
            from: McpServerConfig(serverEndpoint: URL(string: "https://mcp.example.com")!))
        XCTAssertTrue(out.isEmpty)
        XCTAssertTrue(registry.registered.isEmpty)
    }

    func testASuccessfulImportRegistersEachToolAsAWebhook() async throws {
        let body = mcpResponse("""
            {"name":"search","description":"Find","inputSchema":{"type":"object"}}
            """)
        let importer = HttpMcpToolImporter(send: { _ in (body, 200) })
        let registry = RecordingRegistry()

        let out = try await importer.import(
            into: registry,
            from: McpServerConfig(serverEndpoint: URL(string: "https://mcp.example.com/rpc")!,
                                  toolNamePrefix: "crm_"))

        XCTAssertEqual(out.map(\.name), ["crm_search"])
        XCTAssertEqual(registry.registered.count, 1)
        XCTAssertTrue(registry.registered[0].1.absoluteString.contains("remote_tool=search"))
    }

    func testTheAuthorizationHeaderIsSentWhenThereIsOne() async throws {
        let seen = HeaderBox()
        let importer = HttpMcpToolImporter(send: { req in
            seen.record(req.value(forHTTPHeaderField: "Authorization"))
            return (self.mcpResponse(""), 200)
        })
        _ = try await importer.import(
            into: RecordingRegistry(),
            from: McpServerConfig(serverEndpoint: URL(string: "https://mcp.example.com")!,
                                  authorizationHeader: "Bearer abc"))
        XCTAssertEqual(seen.values, ["Bearer abc"])
    }

    func testABlankAuthorizationHeaderIsNotSentAsEmpty() async throws {
        let seen = HeaderBox()
        let importer = HttpMcpToolImporter(send: { req in
            seen.record(req.value(forHTTPHeaderField: "Authorization"))
            return (self.mcpResponse(""), 200)
        })
        _ = try await importer.import(
            into: RecordingRegistry(),
            from: McpServerConfig(serverEndpoint: URL(string: "https://mcp.example.com")!,
                                  authorizationHeader: "   "))
        XCTAssertEqual(seen.values, [nil])
    }

    func testOneUnregisterableToolDoesNotLoseTheRest() async throws {
        // A duplicate name is the usual cause, and losing a whole server's
        // catalogue over one collision is a bad trade.
        let body = mcpResponse("""
            {"name":"bad","description":""},
            {"name":"good","description":""}
            """)
        let importer = HttpMcpToolImporter(send: { _ in (body, 200) })
        let registry = RecordingRegistry(rejecting: "bad")

        let out = try await importer.import(
            into: registry,
            from: McpServerConfig(serverEndpoint: URL(string: "https://mcp.example.com")!))
        XCTAssertEqual(out.map(\.name), ["good"])
    }
}

// MARK: - Test doubles

private final class PortBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [Int] = []
    func record(_ p: Int) { lock.lock(); stored.append(p); lock.unlock() }
    var ports: [Int] { lock.lock(); defer { lock.unlock() }; return stored }
}

private final class HeaderBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [String?] = []
    func record(_ v: String?) { lock.lock(); stored.append(v); lock.unlock() }
    var values: [String?] { lock.lock(); defer { lock.unlock() }; return stored }
}

private struct RegistryRefused: Error {}

private final class RecordingRegistry: IToolCallRegistry, @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [(TelephonyToolDefinition, URL)] = []
    private let reject: String?

    init(rejecting: String? = nil) { self.reject = rejecting }

    var registered: [(TelephonyToolDefinition, URL)] {
        lock.lock(); defer { lock.unlock() }
        return stored
    }

    var definitions: [TelephonyToolDefinition] { registered.map(\.0) }

    func registerLocal(_ definition: TelephonyToolDefinition,
                       handler: @escaping TelephonyLocalToolHandler) throws {}

    func registerWebhook(_ definition: TelephonyToolDefinition, webhook: URL) throws {
        if let reject, definition.name == reject { throw RegistryRefused() }
        lock.lock(); stored.append((definition, webhook)); lock.unlock()
    }

    func invoke(_ invocation: TelephonyToolInvocation) async -> TelephonyToolResult {
        TelephonyToolResult(callId: invocation.callId, succeeded: true, resultJson: "{}")
    }
}
