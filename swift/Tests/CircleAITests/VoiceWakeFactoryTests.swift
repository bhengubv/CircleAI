// VoiceWakeFactoryTests.swift
//
// The factory's whole job is to make a choice nobody was making. So the tests
// are about the CHOICE, not about the detectors it would build.

import XCTest
@testable import CircleAI

final class VoiceWakeFactoryTests: XCTestCase {

    private var root: String!

    override func setUpWithError() throws {
        root = NSTemporaryDirectory() + "wakefactory-" + UUID().uuidString
        try FileManager.default.createDirectory(atPath: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(atPath: root)
    }

    private func bundle(_ name: String, files: [(String, Int)]) throws -> String {
        let dir = (root as NSString).appendingPathComponent(name)
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        for (f, size) in files {
            let path = (dir as NSString).appendingPathComponent(f)
            let sub = (path as NSString).deletingLastPathComponent
            try FileManager.default.createDirectory(atPath: sub, withIntermediateDirectories: true)
            try Data(repeating: 0, count: size).write(to: URL(fileURLWithPath: path))
        }
        return dir
    }

    // MARK: - Which engine

    func testAllThreeGraphsMeansTransducer() throws {
        let dir = try bundle("t", files: [
            ("encoder-epoch-99.onnx", 10), ("decoder-epoch-99.onnx", 10),
            ("joiner-epoch-99.onnx", 10), ("tokens.txt", 5),
        ])
        XCTAssertEqual(WakeWordFactory.engine(forBundleAt: dir), .zipformerTransducer)
    }

    func testOneGraphMeansClassifier() throws {
        let dir = try bundle("c", files: [("model.onnx", 10)])
        XCTAssertEqual(WakeWordFactory.engine(forBundleAt: dir), .singleGraphClassifier)
    }

    func testTwoOfThreeGraphsIsNotATransducer() throws {
        // A half-copied bundle must not be treated as a transducer, or it loads
        // an encoder and then fails deep inside with a confusing message.
        let dir = try bundle("half", files: [
            ("encoder.onnx", 10), ("decoder.onnx", 10),
        ])
        XCTAssertEqual(WakeWordFactory.engine(forBundleAt: dir), .singleGraphClassifier)
    }

    func testGraphsInSubdirectoriesStillCount() throws {
        let dir = try bundle("nested", files: [
            ("v2/encoder.onnx", 10), ("v2/decoder.onnx", 10), ("v2/joiner.onnx", 10),
        ])
        XCTAssertEqual(WakeWordFactory.engine(forBundleAt: dir), .zipformerTransducer)
    }

    func testMissingDirectoryIsTheClassifier() {
        // Not a crash: the caller gets a clear failure from the model lookup
        // rather than a confusing one from a transducer with no encoder.
        let missing = (root as NSString).appendingPathComponent("nope")
        XCTAssertEqual(WakeWordFactory.engine(forBundleAt: missing), .singleGraphClassifier)
        XCTAssertNil(WakeWordFactory.singleGraphModel(inBundleAt: missing))
    }

    func testEngineDetectionIgnoresCase() throws {
        let dir = try bundle("caps", files: [
            ("Encoder.ONNX", 10), ("DECODER.onnx", 10), ("Joiner.Onnx", 10),
        ])
        XCTAssertEqual(WakeWordFactory.engine(forBundleAt: dir), .zipformerTransducer)
    }

    // MARK: - Which file

    func testSmallestOnnxIsChosenNotTheFirst() throws {
        // A bundle can carry a spare or a quantised variant; picking by
        // directory order loads whichever the filesystem happened to hand back.
        let dir = try bundle("many", files: [
            ("aaa-big.onnx", 4096), ("zzz-small.onnx", 128), ("mid.onnx", 1024),
        ])
        let picked = WakeWordFactory.singleGraphModel(inBundleAt: dir)
        XCTAssertEqual((picked! as NSString).lastPathComponent, "zzz-small.onnx")
    }

    func testNoModelInBundleIsNilNotACrash() throws {
        let dir = try bundle("empty", files: [("readme.txt", 3)])
        XCTAssertNil(WakeWordFactory.singleGraphModel(inBundleAt: dir))
    }

    // MARK: - Thresholds

    func testTheTwoEnginesHaveDifferentDefaultThresholds() {
        // They score entirely different things: a transducer's mean acoustic
        // probability and a classifier's single output are not comparable.
        XCTAssertEqual(WakeWordFactory.defaultThreshold(for: .zipformerTransducer), 0.5)
        XCTAssertEqual(WakeWordFactory.defaultThreshold(for: .singleGraphClassifier), 0.7)
    }

    // MARK: - Which confirmer

    private func transcribeStub() -> @Sendable ([UInt8]) async throws -> String {
        { _ in "hey b" }
    }

    func testNoTranscriberMeansOnsetOnly() {
        let c = WakeWordFactory.confirmer(
            host: WakeHostCapabilities(totalRamBytes: 8_000_000_000, transcriberAvailable: true),
            calibration: WakeCalibration())
        XCTAssertTrue(c is UtteranceOnsetConfirmer)
    }

    func testTranscriberUnavailableMeansOnsetOnlyEvenWithRam() {
        let c = WakeWordFactory.confirmer(
            host: WakeHostCapabilities(totalRamBytes: 16_000_000_000, transcriberAvailable: false),
            calibration: WakeCalibration(),
            transcribe: transcribeStub())
        XCTAssertTrue(c is UtteranceOnsetConfirmer)
    }

    func testLowRamMeansOnsetOnly() {
        // Being throttled is worse than being slightly less precise.
        let c = WakeWordFactory.confirmer(
            host: WakeHostCapabilities(totalRamBytes: 3_000_000_000, transcriberAvailable: true),
            calibration: WakeCalibration(),
            transcribe: transcribeStub())
        XCTAssertTrue(c is UtteranceOnsetConfirmer)
    }

    func testEnoughRamAndATranscriberGivesBothInOrder() {
        let c = WakeWordFactory.confirmer(
            host: WakeHostCapabilities(totalRamBytes: 8_000_000_000, transcriberAvailable: true),
            calibration: WakeCalibration(),
            transcribe: transcribeStub())
        XCTAssertTrue(c is EitherConfirmer)
    }

    func testTheRamBoundaryIsInclusive() {
        let exactly = WakeWordFactory.transcriptConfirmerMinRam
        XCTAssertEqual(exactly, 4_000_000_000)

        let at = WakeWordFactory.confirmer(
            host: WakeHostCapabilities(totalRamBytes: exactly, transcriberAvailable: true),
            calibration: WakeCalibration(), transcribe: transcribeStub())
        XCTAssertTrue(at is EitherConfirmer)

        let below = WakeWordFactory.confirmer(
            host: WakeHostCapabilities(totalRamBytes: exactly - 1, transcriberAvailable: true),
            calibration: WakeCalibration(), transcribe: transcribeStub())
        XCTAssertTrue(below is UtteranceOnsetConfirmer)
    }

    func testCalibratedLeadInReachesTheOnsetConfirmer() {
        // The point of storing a calibration is that it is APPLIED. A value
        // that is loaded and then ignored is indistinguishable from no tuning.
        let c = WakeWordFactory.confirmer(
            host: WakeHostCapabilities(totalRamBytes: 1_000_000_000, transcriberAvailable: false),
            calibration: WakeCalibration(maxLeadInMs: 250))
        XCTAssertEqual((c as! UtteranceOnsetConfirmer).maxLeadInMs, 250)
    }

    func testNoCalibratedLeadInKeepsTheDefault() {
        let c = WakeWordFactory.confirmer(
            host: WakeHostCapabilities(totalRamBytes: 1_000_000_000, transcriberAvailable: false),
            calibration: WakeCalibration())
        XCTAssertEqual((c as! UtteranceOnsetConfirmer).maxLeadInMs,
                       UtteranceOnsetConfirmer().maxLeadInMs)
    }

    // MARK: - Calibration file

    func testDefaultCalibrationIsDefault() {
        XCTAssertTrue(WakeCalibration().isDefault)
        XCTAssertFalse(WakeCalibration(threshold: 0.6).isDefault)
        XCTAssertFalse(WakeCalibration(maxLeadInMs: 300).isDefault)
        // Counts alone are not tuning — they are what tuning is derived FROM.
        XCTAssertTrue(WakeCalibration(wakes: 12, vetoes: 3).isDefault)
    }

    func testCalibrationRoundTripsThroughAFile() {
        let path = (root as NSString).appendingPathComponent("cal/wake.json")
        let saved = WakeCalibration(threshold: 0.62, maxLeadInMs: 480, wakes: 9, vetoes: 2)
        saved.save(to: path)

        let loaded = WakeCalibration.load(from: path)
        XCTAssertEqual(loaded, saved)
        XCTAssertFalse(loaded.isDefault)
    }

    func testSaveCreatesTheDirectory() {
        let path = (root as NSString).appendingPathComponent("a/b/c/wake.json")
        WakeCalibration(threshold: 0.5).save(to: path)
        XCTAssertTrue(FileManager.default.fileExists(atPath: path))
    }

    func testMissingCalibrationFileIsADefaultNotAFailure() {
        // Losing it costs tuning, not function.
        let loaded = WakeCalibration.load(
            from: (root as NSString).appendingPathComponent("never-written.json"))
        XCTAssertTrue(loaded.isDefault)
        XCTAssertEqual(loaded.wakes, 0)
    }

    func testCorruptCalibrationFileIsADefaultNotACrash() {
        let path = (root as NSString).appendingPathComponent("junk.json")
        try? "{ this is not json".write(toFile: path, atomically: true, encoding: .utf8)
        XCTAssertTrue(WakeCalibration.load(from: path).isDefault)
    }

    // MARK: - Which language

    private let models = [
        WakeLanguages.Model(name: "hey-b-en-low", language: "en-US", quality: 1),
        WakeLanguages.Model(name: "hey-b-en-high", language: "en", quality: 5),
        WakeLanguages.Model(name: "hey-b-zu", language: "zu-ZA", quality: 3),
    ]

    func testANativeModelWinsAndSaysNothing() {
        let c = WakeLanguages.choose(from: models, languageCode: "zu-ZA")
        XCTAssertEqual(c.modelName, "hey-b-zu")
        XCTAssertTrue(c.isNative)
        XCTAssertEqual(c.note, "")
    }

    func testRegionDoesNotChangeWhichModelCanHearYou() {
        // en-ZA and en are the same language for this purpose.
        let c = WakeLanguages.choose(from: models, languageCode: "en_ZA")
        XCTAssertTrue(c.isNative)
        XCTAssertEqual(c.modelName, "hey-b-en-high")   // highest quality of the two
    }

    func testFallbackToEnglishSaysSo() {
        // Falling back silently leaves somebody repeating a phrase in their own
        // language at a device that is listening for it in English.
        let c = WakeLanguages.choose(from: models, languageCode: "xh")
        XCTAssertEqual(c.modelName, "hey-b-en-high")
        XCTAssertFalse(c.isNative)
        XCTAssertFalse(c.note.isEmpty)
        XCTAssertTrue(c.note.contains("English"))
    }

    func testNoEnglishEitherFallsBackToTheBestThereIs() {
        let onlyZulu = [WakeLanguages.Model(name: "hey-b-zu", language: "zu", quality: 3),
                        WakeLanguages.Model(name: "hey-b-zu-hi", language: "zu", quality: 8)]
        let c = WakeLanguages.choose(from: onlyZulu, languageCode: "fr")
        XCTAssertEqual(c.modelName, "hey-b-zu-hi")
        XCTAssertFalse(c.isNative)
        XCTAssertFalse(c.note.isEmpty)
    }

    func testNothingAvailableSaysItCannotListen() {
        let c = WakeLanguages.choose(from: [], languageCode: "en")
        XCTAssertNil(c.modelName)
        XCTAssertFalse(c.isNative)
        XCTAssertTrue(c.note.contains("No wake word"))
    }

    func testModelsWithNoLanguageNeverMatchNatively() {
        let unlabelled = [WakeLanguages.Model(name: "mystery", language: nil, quality: 9)]
        let c = WakeLanguages.choose(from: unlabelled, languageCode: "en")
        XCTAssertEqual(c.modelName, "mystery")
        XCTAssertFalse(c.isNative)
    }

    // MARK: - Zipformer config

    func testTheZipformerConfigDefaultsMatchTheEngine() {
        let c = ZipformerWakeConfig(bundleDirectory: "/models/wake")
        XCTAssertEqual(c.threshold, WakeWordFactory.defaultThreshold(for: .zipformerTransducer))
        XCTAssertNil(c.keywordsFile)
        XCTAssertNil(c.confirmer)
    }

    func testTheDebounceIsNotZero() {
        // The decoder emits a detection per frame while the phrase is still
        // under the microphone, so one spoken "Hey B" is several detections.
        // At zero the loop starts three or four conversations from one wake.
        XCTAssertGreaterThan(ZipformerWakeConfig(bundleDirectory: "/x").minIntervalBetweenFires, 0)
        XCTAssertEqual(ZipformerWakeConfig(bundleDirectory: "/x").minIntervalBetweenFires, 1.2)
    }

    func testTheConfigCarriesEveryChoiceThroughUnchanged() {
        let confirmer = AlwaysConfirm()
        let c = ZipformerWakeConfig(bundleDirectory: "/models/wake",
                                    keywordsFile: "/models/wake/keywords.txt",
                                    threshold: 0.62,
                                    confirmer: confirmer,
                                    minIntervalBetweenFires: 2)
        XCTAssertEqual(c.bundleDirectory, "/models/wake")
        XCTAssertEqual(c.keywordsFile, "/models/wake/keywords.txt")
        XCTAssertEqual(c.threshold, 0.62)
        XCTAssertTrue(c.confirmer === confirmer)
        XCTAssertEqual(c.minIntervalBetweenFires, 2)
    }

    func testBlankLanguageCodeIsNotAMatchForEverything() {
        // An empty wanted code must not silently match the unlabelled models.
        let c = WakeLanguages.choose(from: models, languageCode: "")
        XCTAssertFalse(c.isNative)
        XCTAssertEqual(c.modelName, "hey-b-en-high")
    }
}
