// InferencePreflightTests.swift

import XCTest
@testable import CircleAI

final class InferencePreflightTests: XCTestCase {

    private var dir: String!

    override func setUpWithError() throws {
        dir = NSTemporaryDirectory() + "sideload-" + UUID().uuidString
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(atPath: dir)
    }

    // MARK: - Resolver policy

    func testNoGoogleResolverIsUsed() {
        // De-Googled by policy: 8.8.8.8's absence here is a decision, not an
        // oversight, and a well-meaning "add a third fallback" would undo it.
        for e in NetworkPreflight.dohEndpoints {
            XCTAssertFalse(e.contains("8.8.8.8"), e)
            XCTAssertFalse(e.contains("8.8.4.4"), e)
            XCTAssertFalse(e.lowercased().contains("google"), e)
        }
    }

    func testEveryResolverIsAddressedByIpLiteral() {
        // The whole trick: 1.1.1.1 is reachable with a broken resolver precisely
        // because there is no name to look up. A hostname here would make the
        // bypass depend on the thing it exists to bypass.
        for e in NetworkPreflight.dohEndpoints {
            let host = URL(string: e)!.host!
            XCTAssertTrue(NetworkPreflight.isIpLiteral(host), "\(host) is not an IP literal")
        }
    }

    func testTheTwoResolversAreDifferentFailureDomains() {
        // Quad9 is second because it is a Swiss non-profit — a different
        // operator, not a second helping of the same one.
        XCTAssertEqual(NetworkPreflight.dohEndpoints.count, 2)
        XCTAssertTrue(NetworkPreflight.dohEndpoints[0].contains("1.1.1.1"))
        XCTAssertTrue(NetworkPreflight.dohEndpoints[1].contains("9.9.9.9"))
    }

    // MARK: - IP literals

    func testIpLiteralsAreRecognisedAndHostnamesAreNot() {
        XCTAssertTrue(NetworkPreflight.isIpLiteral("1.1.1.1"))
        XCTAssertTrue(NetworkPreflight.isIpLiteral("255.255.255.255"))
        XCTAssertTrue(NetworkPreflight.isIpLiteral("0.0.0.0"))

        // A hostname smuggled in where an address belongs would be handed back
        // to the resolver that is broken.
        XCTAssertFalse(NetworkPreflight.isIpLiteral("modelscope.cn"))
        XCTAssertFalse(NetworkPreflight.isIpLiteral("256.1.1.1"))
        XCTAssertFalse(NetworkPreflight.isIpLiteral("1.1.1"))
        XCTAssertFalse(NetworkPreflight.isIpLiteral("1.1.1.1.1"))
        XCTAssertFalse(NetworkPreflight.isIpLiteral(""))
        XCTAssertFalse(NetworkPreflight.isIpLiteral("1.1.1."))
    }

    // MARK: - Link layer

    private func preflight(status: Int = 200, location: String? = nil,
                           data: Data = Data(), linkUp: Bool = true,
                           system: [String] = [],
                           fail: (@Sendable (URLRequest) -> Error?)? = nil,
                           dohBody: Data? = nil) -> NetworkPreflight {
        NetworkPreflight(
            transport: { req in
                if let e = fail?(req) { throw e }
                if let dohBody, req.url?.host == "1.1.1.1" || req.url?.host == "9.9.9.9" {
                    return (dohBody, 200, nil)
                }
                return (data, status, location)
            },
            systemResolve: { _ in system },
            linkIsUp: { linkUp })
    }

    func testNoLinkIsReportedBeforeAnythingIsAttempted() async throws {
        // Cheapest check, and it distinguishes "no network at all" from
        // "network but broken", which have different remedies.
        let d = try await preflight(linkUp: false).check(target: URL(string: "https://x.com")!)
        XCTAssertEqual(d.fault, .noLink)
        XCTAssertTrue(d.remedy.contains("Wi-Fi"))
    }

    func testAReachableTargetIsHealthy() async throws {
        let d = try await preflight(status: 200).check(target: URL(string: "https://x.com")!)
        XCTAssertEqual(d.fault, .none)
        XCTAssertFalse(d.shouldBlockDownload)
    }

    func testItUsesHeadNotGet() async throws {
        // Reachability, not 433 MB of payload.
        let seen = MethodBox()
        let p = NetworkPreflight(transport: { req in
            seen.record(req.httpMethod)
            return (Data(), 200, nil)
        })
        _ = try await p.check(target: URL(string: "https://x.com")!)
        XCTAssertEqual(seen.methods, ["HEAD"])
    }

    // MARK: - Captive portals

    func testARedirectToAnotherHostIsACaptivePortal() async throws {
        // The classic signature: the network answered for somebody else.
        let d = try await preflight(status: 302, location: "http://hotel-wifi.local/login")
            .check(target: URL(string: "https://modelscope.cn/x")!)
        XCTAssertEqual(d.fault, .captivePortal)
        XCTAssertTrue(d.detail.contains("hotel-wifi.local"))
        XCTAssertTrue(d.remedy.contains("sign in"))
    }

    func testACaptivePortalIsNotTransient() async throws {
        // Retrying redirects again until somebody signs in, and spinning on it
        // drains a battery for nothing.
        let d = try await preflight(status: 302, location: "http://portal/login")
            .check(target: URL(string: "https://x.com")!)
        XCTAssertFalse(d.isTransient)
    }

    func testARedirectToTheSameHostIsNotAPortal() async throws {
        // http -> https on the same host is ordinary, and calling it a captive
        // portal would tell somebody to go and sign in to nothing.
        let d = try await preflight(status: 301, location: "https://x.com/moved")
            .check(target: URL(string: "http://x.com/")!)
        XCTAssertNotEqual(d.fault, .captivePortal)
    }

    func testEveryRedirectStatusIsRecognised() {
        for code in [301, 302, 303, 307, 308] {
            XCTAssertTrue(NetworkPreflight.isRedirect(code), "\(code)")
        }
        for code in [200, 204, 400, 404, 500] {
            XCTAssertFalse(NetworkPreflight.isRedirect(code), "\(code)")
        }
    }

    func testAnErrorStatusIsClassifiedRatherThanCalledHealthy() async throws {
        let d = try await preflight(status: 503).check(target: URL(string: "https://x.com")!)
        XCTAssertEqual(d.fault, .httpError)
        XCTAssertTrue(d.isTransient)
    }

    // MARK: - The DNS bypass

    private let dohAnswer = Data("""
        {"Status":0,"Answer":[
          {"name":"modelscope.cn","type":5,"data":"cdn.modelscope.cn"},
          {"name":"cdn.modelscope.cn","type":1,"data":"47.246.1.2"}
        ]}
        """.utf8)

    private func dnsError() -> Error {
        NSError(domain: "Java.Net", code: 0, userInfo: [
            NSLocalizedDescriptionKey:
                "Java.Net.UnknownHostException: Unable to resolve host \"modelscope.cn\""])
    }

    func testADnsFailureThatTheBypassRecoversIsNotReportedAsFatal() async throws {
        // Reporting it would block a download that would have worked.
        let p = preflight(
            fail: { req in req.httpMethod == "HEAD" ? self.dnsError() : nil },
            dohBody: dohAnswer)

        let d = try await p.check(target: URL(string: "https://modelscope.cn/model")!)
        XCTAssertEqual(d.fault, .dnsFailure)
        XCTAssertTrue(d.detail.contains("47.246.1.2"))
        XCTAssertTrue(d.remedy.isEmpty, "nothing for the user to do — we routed around it")
    }

    func testADnsFailureTheBypassCannotRecoverKeepsItsRemedy() async throws {
        let p = preflight(fail: { _ in self.dnsError() })
        let d = try await p.check(target: URL(string: "https://modelscope.cn/model")!)
        XCTAssertEqual(d.fault, .dnsFailure)
        XCTAssertFalse(d.remedy.isEmpty, "now the user CAN try toggling Wi-Fi")
    }

    func testOnlyARecordsAreTakenFromTheDohAnswer() {
        // A CNAME is a NAME, not an address, and connecting to it would need the
        // resolver that just failed.
        XCTAssertEqual(NetworkPreflight.parseDohAnswer(dohAnswer), ["47.246.1.2"])
    }

    func testAnAnswerWithNoARecordsResolvesToNothing() {
        let onlyCname = Data("""
            {"Answer":[{"name":"x","type":5,"data":"y.example.com"}]}
            """.utf8)
        XCTAssertTrue(NetworkPreflight.parseDohAnswer(onlyCname).isEmpty)
        XCTAssertTrue(NetworkPreflight.parseDohAnswer(Data("{}".utf8)).isEmpty)
        XCTAssertTrue(NetworkPreflight.parseDohAnswer(Data("not json".utf8)).isEmpty)
    }

    func testAnARecordCarryingSomethingThatIsNotAnAddressIsRejected() {
        // A malicious or broken resolver answering with a hostname would send
        // the connection straight back through the resolver that is broken.
        let bad = Data("""
            {"Answer":[{"name":"x","type":1,"data":"evil.example.com"}]}
            """.utf8)
        XCTAssertTrue(NetworkPreflight.parseDohAnswer(bad).isEmpty)
    }

    func testTheSecondResolverIsTriedWhenTheFirstFails() async throws {
        let tried = HostBox()
        let p = NetworkPreflight(transport: { req in
            let host = req.url?.host ?? ""
            tried.record(host)
            if host == "1.1.1.1" { throw NSError(domain: "t", code: 1) }
            return (self.dohAnswer, 200, nil)
        })
        let out = await p.resolveViaDoh(host: "modelscope.cn")
        XCTAssertEqual(out, ["47.246.1.2"])
        XCTAssertEqual(tried.hosts, ["1.1.1.1", "9.9.9.9"])
    }

    func testAnAddressIsNotHandedToAResolverAtAll() async {
        // Asking a broken resolver about an IP literal is how a working
        // connection gets blocked by a broken one.
        let p = NetworkPreflight(transport: { _ in
            XCTFail("must not reach the network")
            return (Data(), 200, nil)
        })
        let out = await p.resolve(host: "1.1.1.1")
        XCTAssertEqual(out, ["1.1.1.1"])
    }

    func testTheSystemResolverIsTheFastPathAndTheBypassIsNotUsedWhenItWorks() async {
        let p = NetworkPreflight(transport: { _ in
            XCTFail("must not fall through to DoH")
            return (Data(), 200, nil)
        }, systemResolve: { _ in ["10.0.0.1"] })
        let out = await p.resolve(host: "modelscope.cn")
        XCTAssertEqual(out, ["10.0.0.1"])
    }

    func testABlankHostResolvesToNothingRatherThanQueryingForIt() async {
        let p = preflight()
        let out = await p.resolve(host: "   ")
        XCTAssertTrue(out.isEmpty)
    }

    // MARK: - Side-loading

    private func write(_ name: String, _ body: String) throws -> String {
        let path = (dir as NSString).appendingPathComponent(name)
        let parent = (path as NSString).deletingLastPathComponent
        try FileManager.default.createDirectory(atPath: parent, withIntermediateDirectories: true)
        try body.write(toFile: path, atomically: true, encoding: .utf8)
        return path
    }

    private func sha(_ path: String) -> String {
        SideloadedBundleImporter.sha256Hex(ofFileAt: path)!
    }

    private func importer(_ files: [BundleFile]?, root: String) -> SideloadedBundleImporter {
        SideloadedBundleImporter(storageRoot: root, lookup: { _ in files })
    }

    func testAModelNotInTheCatalogueIsRefusedRatherThanTrusted() throws {
        // There is nothing to check it against, and trusting it is exactly the
        // "run somebody else's weights" case this exists to prevent.
        let r = importer(nil, root: dir + "/store")
            .import(modelName: "mystery", from: dir)
        XCTAssertEqual(r.outcome, .unknown)
        XCTAssertFalse(r.usable)
    }

    func testAVerifiedCopyIsImported() throws {
        let src = dir + "/incoming"
        try FileManager.default.createDirectory(atPath: src, withIntermediateDirectories: true)
        let f = try write("incoming/encoder.onnx", "weights")

        let files = [BundleFile(name: "kws/encoder.onnx", sha256: sha(f),
                                sizeBytes: Int64("weights".utf8.count))]
        let store = dir + "/store"
        let r = importer(files, root: store).import(modelName: "kws", from: src)

        XCTAssertEqual(r.outcome, .imported)
        XCTAssertTrue(r.usable)
        XCTAssertEqual(r.files, 1)
        XCTAssertTrue(FileManager.default.fileExists(atPath: store + "/kws/kws/encoder.onnx"))
    }

    func testTheLeafNameIsMatchedBecauseNobodyKeepsThePath() throws {
        // The published name is repo-relative; somebody copying a folder across
        // keeps the file names and rarely the directory structure.
        let src = dir + "/incoming"
        let f = try write("incoming/encoder.onnx", "weights")
        let files = [BundleFile(name: "deeply/nested/path/encoder.onnx",
                                sha256: sha(f), sizeBytes: 7)]
        XCTAssertEqual(importer(files, root: dir + "/store")
            .import(modelName: "kws", from: src).outcome, .imported)
    }

    func testAWrongHashIsRefusedAndSaysItMayNotBeOurs() throws {
        // The whole security story for this path.
        let src = dir + "/incoming"
        _ = try write("incoming/encoder.onnx", "tampered")
        let files = [BundleFile(name: "encoder.onnx",
                                sha256: String(repeating: "a", count: 64), sizeBytes: 0)]
        let r = importer(files, root: dir + "/store").import(modelName: "kws", from: src)
        XCTAssertEqual(r.outcome, .corrupt)
        XCTAssertTrue(r.detail.contains("may not be ours"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: dir + "/store/kws"))
    }

    func testAWrongSizeIsCaughtBeforeTheHashIsEvenComputed() throws {
        // Free, and it catches the overwhelmingly common failure — a copy that
        // stopped part-way — without reading 400 MB to find out.
        let src = dir + "/incoming"
        _ = try write("incoming/encoder.onnx", "short")
        let files = [BundleFile(name: "encoder.onnx",
                                sha256: String(repeating: "a", count: 64), sizeBytes: 999_999)]
        let r = importer(files, root: dir + "/store").import(modelName: "kws", from: src)
        XCTAssertEqual(r.outcome, .corrupt)
        XCTAssertTrue(r.detail.contains("wrong size"))
        XCTAssertTrue(r.detail.contains("incomplete"))
    }

    func testAMissingFileNamesTheOneThatIsMissing() throws {
        let src = dir + "/incoming"
        _ = try write("incoming/encoder.onnx", "weights")
        let files = [
            BundleFile(name: "encoder.onnx", sha256: "", sizeBytes: 0),
            BundleFile(name: "decoder.onnx", sha256: "", sizeBytes: 0),
        ]
        let r = importer(files, root: dir + "/store").import(modelName: "kws", from: src)
        XCTAssertEqual(r.outcome, .notFound)
        XCTAssertTrue(r.detail.contains("decoder.onnx"))
    }

    func testAMissingFolderIsNotFoundNotACrash() {
        let files = [BundleFile(name: "x", sha256: "", sizeBytes: 0)]
        let r = importer(files, root: dir + "/store")
            .import(modelName: "kws", from: dir + "/nowhere")
        XCTAssertEqual(r.outcome, .notFound)
    }

    func testImportingTwiceReportsAlreadyInstalledAndIsStillUsable() throws {
        // A caller that treats only `imported` as success re-imports every launch.
        let src = dir + "/incoming"
        let f = try write("incoming/encoder.onnx", "weights")
        let files = [BundleFile(name: "encoder.onnx", sha256: sha(f), sizeBytes: 7)]
        let imp = importer(files, root: dir + "/store")

        XCTAssertEqual(imp.import(modelName: "kws", from: src).outcome, .imported)
        let again = imp.import(modelName: "kws", from: src)
        XCTAssertEqual(again.outcome, .alreadyInstalled)
        XCTAssertTrue(again.usable)
    }

    func testTheSourceFolderIsCopiedNotConsumed() throws {
        // It may be shared storage somebody wants to pass to the next phone, and
        // consuming it would make installing on one device destroy the copy for
        // everyone else.
        let src = dir + "/incoming"
        let f = try write("incoming/encoder.onnx", "weights")
        let files = [BundleFile(name: "encoder.onnx", sha256: sha(f), sizeBytes: 7)]

        _ = importer(files, root: dir + "/store").import(modelName: "kws", from: src)
        XCTAssertTrue(FileManager.default.fileExists(atPath: f), "the original must survive")
    }

    func testAnEmptyHashSkipsVerificationButStillChecksSize() throws {
        // Some catalogue entries carry no hash; those still get the size check
        // rather than being waved through entirely.
        let src = dir + "/incoming"
        _ = try write("incoming/encoder.onnx", "weights")
        let files = [BundleFile(name: "encoder.onnx", sha256: "  ", sizeBytes: 7)]
        XCTAssertEqual(importer(files, root: dir + "/store")
            .import(modelName: "kws", from: src).outcome, .imported)
    }

    func testHashingIsStableAndMatchesAKnownValue() throws {
        // Streamed in chunks rather than read whole — a 900 MB model read into
        // memory to hash it is the allocation a low-end phone cannot make.
        let f = try write("abc.txt", "abc")
        XCTAssertEqual(sha(f),
                       "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
    }

    func testEverySideloadOutcomeIsAccountedFor() {
        // usable must be true for exactly the two that mean "the model is there".
        let usable: Set<SideloadOutcome> = [.imported, .alreadyInstalled]
        for o in SideloadOutcome.allCases {
            XCTAssertEqual(SideloadResult(outcome: o, detail: "").usable, usable.contains(o),
                           "\(o)")
        }
    }
}

private final class MethodBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [String?] = []
    func record(_ m: String?) { lock.lock(); stored.append(m); lock.unlock() }
    var methods: [String?] { lock.lock(); defer { lock.unlock() }; return stored }
}

private final class HostBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [String] = []
    func record(_ h: String) { lock.lock(); stored.append(h); lock.unlock() }
    var hosts: [String] { lock.lock(); defer { lock.unlock() }; return stored }
}
