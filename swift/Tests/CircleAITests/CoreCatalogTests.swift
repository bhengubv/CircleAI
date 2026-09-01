// CoreCatalogTests.swift

import XCTest
@testable import CircleAI

final class CoreCatalogTests: XCTestCase {

    private var dir: String!

    override func setUpWithError() throws {
        dir = NSTemporaryDirectory() + "core-" + UUID().uuidString
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(atPath: dir)
        EmbeddedVoiceConfigs.resourceDirectory = nil
        DeviceProbe.platformMemoryProbe = nil
        CircleAIDiagnostics.metricSink = nil
    }

    // MARK: - Enum wire values

    func testModalityValuesAreStableOnTheWire() {
        // These are persisted in a registry, so the numbers are the contract.
        XCTAssertEqual(ModelModality.chat.rawValue, 0)
        XCTAssertEqual(ModelModality.asr.rawValue, 1)
        XCTAssertEqual(ModelModality.tts.rawValue, 2)
        XCTAssertEqual(ModelModality.phonemizer.rawValue, 9)
        XCTAssertEqual(ModelModality.allCases.count, 10)
    }

    func testSourceValuesAreStableOnTheWire() {
        XCTAssertEqual(ModelSource.modelScope.rawValue, 0)
        XCTAssertEqual(ModelSource.huggingFace.rawValue, 1)
        // A bucket is a SEPARATE case, not a URL detail: a 401 from a bucket we
        // hold no token for is not the same problem as a 404 from a repo.
        XCTAssertEqual(ModelSource.huggingFaceBucket.rawValue, 2)
        XCTAssertEqual(ModelSource.gitHubRelease.rawValue, 3)
        XCTAssertEqual(ModelSource.allCases.count, 4)
    }

    func testDownloadPhaseCoversTheNonTransferWork() {
        // A 433 MB bundle spends real time hashing and retrying, and without a
        // phase those look identical to a stall.
        XCTAssertEqual(DownloadPhase.downloading.rawValue, 0)
        XCTAssertEqual(DownloadPhase.allCases.count, 6)
        XCTAssertTrue(DownloadPhase.allCases.contains(.verifying))
        XCTAssertTrue(DownloadPhase.allCases.contains(.retrying))
        XCTAssertTrue(DownloadPhase.allCases.contains(.cached))
    }

    func testEveryCatalogEnumRoundTripsThroughJson() throws {
        let enc = JSONEncoder(), dec = JSONDecoder()
        for m in ModelModality.allCases {
            XCTAssertEqual(try dec.decode(ModelModality.self, from: try enc.encode(m)), m)
        }
        for s in ModelSource.allCases {
            XCTAssertEqual(try dec.decode(ModelSource.self, from: try enc.encode(s)), s)
        }
        for p in DownloadPhase.allCases {
            XCTAssertEqual(try dec.decode(DownloadPhase.self, from: try enc.encode(p)), p)
        }
    }

    // MARK: - Model paths

    func testTheDefaultSitsUnderTheRootAndIsNotTheRoot() {
        // Two owners of one fact is how a 523 MB model got downloaded twice onto
        // a phone with 890 MB of app data.
        XCTAssertTrue(ModelPaths.default.hasPrefix(ModelPaths.root))
        XCTAssertNotEqual(ModelPaths.default, ModelPaths.root)
        XCTAssertTrue(ModelPaths.default.hasSuffix("CircleAI/Models"))
    }

    func testTheRootIsAbsoluteAndNotTheWorkingDirectory() {
        // A relative path here puts a 400 MB download wherever the process
        // happened to be started from.
        XCTAssertTrue(ModelPaths.root.hasPrefix("/"))
    }

    func testResolveCreatesTheDirectory() {
        let target = (dir as NSString).appendingPathComponent("a/b/models")
        XCTAssertEqual(ModelPaths.resolve(target), target)
        XCTAssertTrue(FileManager.default.fileExists(atPath: target))
    }

    func testABlankRequestIsTheDefaultNotTheCurrentDirectory() {
        XCTAssertEqual(ModelPaths.resolve(nil), ModelPaths.default)
        XCTAssertEqual(ModelPaths.resolve(""), ModelPaths.default)
        XCTAssertEqual(ModelPaths.resolve("   "), ModelPaths.default)
    }

    func testResolveIsIdempotent() {
        let target = (dir as NSString).appendingPathComponent("models")
        XCTAssertEqual(ModelPaths.resolve(target), ModelPaths.resolve(target))
    }

    // MARK: - Verification level

    func testVerificationLevelsAreOrdered() {
        // "It compiles" and "a byte crossed a wire" are different claims, and the
        // order is what lets a caller ask for at least one of them.
        XCTAssertLessThan(VerificationLevel.reference.rawValue,
                          VerificationLevel.wireProven.rawValue)
        XCTAssertLessThan(VerificationLevel.wireProven.rawValue,
                          VerificationLevel.productionDeployed.rawValue)
    }

    private struct Proven: CircleAIVerificationStatus {
        static let verificationStatus = VerificationLevel.wireProven
        static let verificationNotes: String? = "P30, 2026-08"
    }

    private struct Unproven: CircleAIVerificationStatus {
        static let verificationStatus = VerificationLevel.reference
    }

    func testATypeCanStateHowFarItHasBeenProven() {
        XCTAssertEqual(Proven.verificationStatus, .wireProven)
        XCTAssertEqual(Proven.verificationNotes, "P30, 2026-08")
    }

    func testNotesAreOptionalAndDefaultToNothingClaimed() {
        XCTAssertEqual(Unproven.verificationStatus, .reference)
        XCTAssertNil(Unproven.verificationNotes)
    }

    // MARK: - Diagnostics

    private final class Sink: ICircleAIMetricSink, @unchecked Sendable {
        private let lock = NSLock()
        private(set) var counts: [(String, Int64, [String: String])] = []
        private(set) var timings: [(String, Double, [String: String])] = []

        func count(_ name: String, by amount: Int64, tags: [String: String]) {
            lock.lock(); counts.append((name, amount, tags)); lock.unlock()
        }
        func record(_ name: String, milliseconds: Double, tags: [String: String]) {
            lock.lock(); timings.append((name, milliseconds, tags)); lock.unlock()
        }
    }

    func testNothingIsRecordedUntilAHostAsksForIt() {
        // A package with no dependencies must not start measuring on its own.
        XCTAssertNil(CircleAIDiagnostics.metricSink)
        CircleAIDiagnostics.count(CircleAIDiagnostics.operationsTotal)   // must not crash
        let op = CircleAIDiagnostics.startOperation(component: "x", operation: "y")
        op.finish()
        XCTAssertTrue(op.isFinished)
    }

    func testAFinishedOperationReportsItsDurationAndOutcome() {
        let sink = Sink()
        CircleAIDiagnostics.metricSink = sink

        let op = CircleAIDiagnostics.startOperation(component: "voice", operation: "synthesise")
        op.finish(outcome: CircleAIDiagnostics.Outcomes.rateLimited)

        XCTAssertEqual(sink.timings.count, 1)
        XCTAssertEqual(sink.timings[0].0, "circleai.operation.duration")
        XCTAssertEqual(sink.counts.count, 1)
        XCTAssertEqual(sink.counts[0].0, "circleai.operations.total")
        XCTAssertEqual(sink.counts[0].2["circleai.component"], "voice")
        XCTAssertEqual(sink.counts[0].2["circleai.operation"], "synthesise")
        XCTAssertEqual(sink.counts[0].2["circleai.outcome"], "rate_limited")
    }

    func testFinishingTwiceDoesNotDoubleCount() {
        // A caller that finishes in both a success path and a defer is normal.
        let sink = Sink()
        CircleAIDiagnostics.metricSink = sink
        let op = CircleAIDiagnostics.startOperation(component: "a", operation: "b")
        op.finish()
        op.finish()
        XCTAssertEqual(sink.counts.count, 1)
    }

    func testAnAbandonedOperationReportsNothingRatherThanSuccess() {
        // A span that ends on deinit files every abandoned operation as a
        // success, which is exactly backwards.
        let sink = Sink()
        CircleAIDiagnostics.metricSink = sink
        do { _ = CircleAIDiagnostics.startOperation(component: "a", operation: "b") }
        XCTAssertTrue(sink.counts.isEmpty)
    }

    func testTheInstrumentNamesMatchTheDashboardsExactly() {
        // A dashboard is built on these strings; renaming one silently splits a
        // metric in two.
        XCTAssertEqual(CircleAIDiagnostics.operationsTotal, "circleai.operations.total")
        XCTAssertEqual(CircleAIDiagnostics.operationDurationMs, "circleai.operation.duration")
        XCTAssertEqual(CircleAIDiagnostics.anomalySignalsTotal, "circleai.anomaly.signals.total")
        XCTAssertEqual(CircleAIDiagnostics.inferenceRequestsTotal,
                       "circleai.inference.requests.total")
        XCTAssertEqual(CircleAIDiagnostics.activitySourceName, "CircleAI")
        XCTAssertEqual(CircleAIDiagnostics.meterName, "CircleAI")
    }

    func testTheOutcomeVocabularyIsClosedAndLowercase() {
        // "failed", "error" and "err" in three components make a chart nobody
        // can read.
        XCTAssertEqual(CircleAIDiagnostics.Outcomes.all.count, 6)
        for o in CircleAIDiagnostics.Outcomes.all {
            XCTAssertEqual(o, o.lowercased())
            XCTAssertFalse(o.contains(" "))
        }
        XCTAssertEqual(CircleAIDiagnostics.Outcomes.rateLimited, "rate_limited")
    }

    // MARK: - Embedded voice configs

    private func writeSidecar(_ voice: String, _ file: String, _ body: String) throws {
        let d = (dir as NSString).appendingPathComponent(voice)
        try FileManager.default.createDirectory(atPath: d, withIntermediateDirectories: true)
        try body.write(toFile: (d as NSString).appendingPathComponent(file),
                       atomically: true, encoding: .utf8)
    }

    func testASidecarIsFoundByItsBundleRelativeName() throws {
        try writeSidecar("mms-swh", "model.onnx.json", "{\"a\":1}")
        EmbeddedVoiceConfigs.resourceDirectory = dir

        let bytes = EmbeddedVoiceConfigs.bytes(forBundleFile: "mms-swh/model.onnx.json")
        XCTAssertEqual(bytes.map { String(decoding: $0, as: UTF8.self) }, "{\"a\":1}")
    }

    func testABackslashNameFindsTheSameFile() throws {
        // A bundle manifest written on Windows names the same file a different
        // way, and a miss here falls through to downloading a file that 404s.
        try writeSidecar("mms-swh", "model.onnx.json", "{}")
        EmbeddedVoiceConfigs.resourceDirectory = dir
        XCTAssertNotNil(EmbeddedVoiceConfigs.bytes(forBundleFile: "mms-swh\\model.onnx.json"))
    }

    func testBothCompanionFilesAreCarried() throws {
        try writeSidecar("mms-zul", "model.onnx.json", "{}")
        try writeSidecar("mms-zul", "language_ids.json", "{}")
        EmbeddedVoiceConfigs.resourceDirectory = dir

        XCTAssertEqual(EmbeddedVoiceConfigs.names,
                       ["mms-zul/language_ids.json", "mms-zul/model.onnx.json"])
        XCTAssertEqual(EmbeddedVoiceConfigs.voices, ["mms-zul"])
    }

    func testAnUnrelatedFileIsNotCarried() throws {
        try writeSidecar("mms-swh", "model.onnx", "not a sidecar")
        try writeSidecar("mms-swh", "tokens.txt", "not a sidecar")
        EmbeddedVoiceConfigs.resourceDirectory = dir
        XCTAssertTrue(EmbeddedVoiceConfigs.names.isEmpty)
    }

    func testAnUnknownNameIsNilRatherThanAnEmptyFile() throws {
        try writeSidecar("mms-swh", "model.onnx.json", "{}")
        EmbeddedVoiceConfigs.resourceDirectory = dir
        XCTAssertNil(EmbeddedVoiceConfigs.bytes(forBundleFile: "mms-nso/model.onnx.json"))
        XCTAssertNil(EmbeddedVoiceConfigs.bytes(forBundleFile: nil))
        XCTAssertNil(EmbeddedVoiceConfigs.bytes(forBundleFile: "   "))
    }

    func testPointingSomewhereElseRebuildsTheMap() throws {
        try writeSidecar("mms-swh", "model.onnx.json", "{}")
        EmbeddedVoiceConfigs.resourceDirectory = dir
        XCTAssertEqual(EmbeddedVoiceConfigs.voices, ["mms-swh"])

        let other = NSTemporaryDirectory() + "core2-" + UUID().uuidString
        try FileManager.default.createDirectory(atPath: other + "/mms-nso",
                                                withIntermediateDirectories: true)
        try "{}".write(toFile: other + "/mms-nso/model.onnx.json",
                       atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(atPath: other) }

        EmbeddedVoiceConfigs.resourceDirectory = other
        XCTAssertEqual(EmbeddedVoiceConfigs.voices, ["mms-nso"])
    }

    // MARK: - RAM provenance

    func testAnExplicitFigureIsNotOverwrittenByThePlatformHook() {
        // A test that states a number must keep it, whatever hardware is running
        // the test.
        DeviceProbe.platformMemoryProbe = { PlatformMemory(ramAvailableBytes: 999) }
        let r = DeviceProbe.measuredSnapshot(ramAvailableBytes: 8_000_000_000)
        XCTAssertEqual(r.source, .explicit)
        XCTAssertEqual(r.probe.ramAvailableBytes, 8_000_000_000)
    }

    func testThePlatformHookIsUsedWhenNobodyStatedAFigure() {
        DeviceProbe.platformMemoryProbe = {
            PlatformMemory(ramAvailableBytes: 3_000_000_000,
                           storageFreeBytes: 10_000_000_000,
                           ramTotalBytes: 4_000_000_000)
        }
        let r = DeviceProbe.measuredSnapshot()
        XCTAssertEqual(r.source, .platformMeasured)
        XCTAssertEqual(r.probe.ramAvailableBytes, 3_000_000_000)
        XCTAssertEqual(r.probe.storageFreeBytes, 10_000_000_000)
        XCTAssertNil(r.warning)
    }

    func testTheTotalIsUsedWhenTheHookKnowsOnlyThat() {
        DeviceProbe.platformMemoryProbe = { PlatformMemory(ramTotalBytes: 4_000_000_000) }
        let r = DeviceProbe.measuredSnapshot()
        XCTAssertEqual(r.source, .platformMeasured)
        XCTAssertEqual(r.probe.ramAvailableBytes, 4_000_000_000)
    }

    func testNoHookAndNoFigureIsAGuessAndSaysSo() {
        DeviceProbe.platformMemoryProbe = nil
        XCTAssertEqual(DeviceProbe.measuredSnapshot().source, .heuristic)
    }

    func testAGuessThatLooksLikeAHeapLimitWarnsInPlainLanguage() {
        // The actual signature of the bug: an inferred figure too small for any
        // real device, which is what a mobile head that never set the hook gives.
        let probe = DeviceProbe(ramAvailableBytes: 100 * 1024 * 1024,
                                storageFreeBytes: 0, cpuCores: 8)
        let w = probe.measurementWarning(source: .heuristic)
        XCTAssertNotNil(w)
        XCTAssertTrue(w!.contains("was not measured"))
        XCTAssertTrue(w!.contains("platformMemoryProbe"))
        XCTAssertTrue(w!.contains("100 MB"))
    }

    func testAGuessOnADesktopIsNotWarnedAbout() {
        // The heuristic is perfectly good where it returns GB-scale numbers, and
        // warning there would be noise nobody reads.
        let probe = DeviceProbe(ramAvailableBytes: 16_000_000_000,
                                storageFreeBytes: 0, cpuCores: 8)
        XCTAssertNil(probe.measurementWarning(source: .heuristic))
    }

    func testAMeasuredFigureIsNeverWarnedAboutEvenWhenSmall() {
        // A real 256 MB device is a real 256 MB device. The warning is about
        // provenance, not size.
        let probe = DeviceProbe(ramAvailableBytes: 256 * 1024 * 1024,
                                storageFreeBytes: 0, cpuCores: 2)
        XCTAssertNil(probe.measurementWarning(source: .platformMeasured))
        XCTAssertNil(probe.measurementWarning(source: .explicit))
    }

    func testASmallMeasuredPhoneStillClassifiesAsAPhoneNotAWearable() {
        // The whole point of the hook: a 3 GB phone was reading as a wearable
        // and every model came back as not fitting.
        DeviceProbe.platformMemoryProbe = { PlatformMemory(ramAvailableBytes: 3_000_000_000) }
        XCTAssertEqual(DeviceProbe.measuredSnapshot().probe.classify(), .tablet)
    }

    // MARK: - System device context

    func testItAnswersWhatTheRuntimeKnows() {
        let c = SystemInfoDeviceContext(activeAppId: "com.bhengubv.aether")
        XCTAssertEqual(c.activeAppId, "com.bhengubv.aether")
        XCTAssertEqual(c.locale, Locale.current.identifier)
        XCTAssertEqual(c.timeZoneId, TimeZone.current.identifier)
        XCTAssertNotNil(c.localTime)
    }

    func testEverythingItCannotKnowIsNilNotZero() {
        // A zero battery level and an unknown battery level are different facts,
        // and reporting 0% tells the assistant the phone is about to die.
        let c = SystemInfoDeviceContext()
        XCTAssertNil(c.batteryLevel)
        XCTAssertNil(c.isCharging)
        XCTAssertNil(c.latitude)
        XCTAssertNil(c.longitude)
        XCTAssertNil(c.networkType)
        XCTAssertNil(c.cpuUsagePercent)
        XCTAssertNil(c.thermalState)
        XCTAssertNil(c.storageFreeBytes)
        XCTAssertNil(c.availableMemoryBytes)
    }

    func testInteractionMovesTheLastActiveStamp() {
        let c = SystemInfoDeviceContext()
        let first = c.lastActiveUtc!
        Thread.sleep(forTimeInterval: 0.01)
        c.recordInteraction()
        XCTAssertGreaterThan(c.lastActiveUtc!, first)
    }
}
