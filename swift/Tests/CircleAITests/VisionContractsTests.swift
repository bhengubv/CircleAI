// VisionContractsTests.swift
//
// Verifies the CircleAI.Vision contract DTOs, fail-closed null defaults
// (Vision.swift): backend ids, empty/deterministic answers, the video-capture
// stream, and the Bluetooth anomaly subscription handle.

import XCTest
@testable import CircleAI

final class VisionContractsTests: XCTestCase {

    // ── Null runtime / detectors ────────────────────────────────────────

    func testNullComputerVisionRuntimeIsNoOp() async throws {
        let rt = NullComputerVisionRuntime.instance
        XCTAssertEqual(rt.backendId, "null")
        let decoded = await rt.decode(imageBytes: Data([1, 2, 3]))
        XCTAssertNil(decoded)
        let resized = await rt.resize(image: "anything", width: 10, height: 10)
        XCTAssertNil(resized)
    }

    func testNullFaceDetectorReturnsNoFaces() async throws {
        let d = NullFaceDetector.instance
        let faces = try await d.detect(imageBytes: Data([0xFF, 0xD8]))
        XCTAssertTrue(faces.isEmpty)
    }

    func testNullFaceEmbedderReturnsZeroVectorAtDimension() async throws {
        let e = NullFaceEmbedder(dimension: 8)
        XCTAssertEqual(e.dimension, 8)
        let face = DetectedFace(region: BoundingBox(x: 0, y: 0, width: 4, height: 4), confidence: 0.9)
        let emb = try await e.embed(imageBytes: Data([1, 2, 3]), face: face)
        XCTAssertEqual(emb.dimension, 8)
        XCTAssertEqual(emb.vector.count, 8)
        XCTAssertTrue(emb.vector.allSatisfy { $0 == 0 })
    }

    func testNullFaceEmbedderDefaultDimensionIs512() async throws {
        let e = NullFaceEmbedder()
        XCTAssertEqual(e.dimension, 512)
    }

    func testNullLivenessFailsClosed() async throws {
        let l = NullFaceLivenessDetector.instance
        let r = try await l.check(imageBytes: Data([1]))
        XCTAssertFalse(r.isLive)
        XCTAssertEqual(r.confidence, 0)
        XCTAssertEqual(r.failureReason, "no liveness backend registered")
    }

    func testNullDocumentVerifierFailsClosed() async throws {
        let v = NullDocumentVerifier.instance
        let r = try await v.verify(imageBytes: Data([1]))
        XCTAssertFalse(r.isValid)
        XCTAssertEqual(r.documentType, "unknown")
        XCTAssertEqual(r.issuingCountry, "unknown")
        XCTAssertTrue(r.fields.isEmpty)
        XCTAssertEqual(r.overallConfidence, 0)
        XCTAssertEqual(r.warnings, ["no document verifier backend registered"])
    }

    func testNullPlateRecognizerReturnsNoPlates() async throws {
        let p = NullPlateRecognizer.instance
        let r = try await p.recognize(imageBytes: Data([1]))
        XCTAssertTrue(r.isEmpty)
    }

    // ── Video capture stream ────────────────────────────────────────────

    func testNullVideoCaptureYieldsNothing() async throws {
        let cap = NullVideoCapture.instance
        var frames = 0
        for await _ in cap.capture(preferredWidth: 640, preferredHeight: 480) {
            frames += 1
        }
        XCTAssertEqual(frames, 0)
        await cap.dispose()
    }

    func testVideoFrameCarriesMetadata() {
        let now = Date()
        let f = VideoFrame(
            bytes: Data([1, 2, 3, 4]), width: 8, height: 6,
            pixelFormat: .nv21, capturedAtUtc: now, rotationDegrees: 90)
        XCTAssertEqual(f.width, 8)
        XCTAssertEqual(f.height, 6)
        XCTAssertEqual(f.pixelFormat, .nv21)
        XCTAssertEqual(f.rotationDegrees, 90)
        XCTAssertEqual(f.bytes.count, 4)
    }

    func testVideoPixelFormatRawValuesMatchCSharp() {
        XCTAssertEqual(VideoPixelFormat.yuv420.rawValue, "Yuv420")
        XCTAssertEqual(VideoPixelFormat.nv21.rawValue, "Nv21")
        XCTAssertEqual(VideoPixelFormat.rgba32.rawValue, "Rgba32")
        XCTAssertEqual(VideoPixelFormat.bgr24.rawValue, "Bgr24")
        XCTAssertEqual(VideoPixelFormat.jpeg.rawValue, "Jpeg")
    }

    // ── Bluetooth anomaly detector ──────────────────────────────────────

    /// Thread-safe flag for the (never-invoked) subscription callback.
    final class Flag: @unchecked Sendable {
        private let lock = NSLock()
        private var _fired = false
        func fire() { lock.lock(); _fired = true; lock.unlock() }
        var fired: Bool { lock.lock(); defer { lock.unlock() }; return _fired }
    }

    func testNullBluetoothAnomalyDetectorNeverFires() async throws {
        let d = NullBluetoothAnomalyDetector()
        XCTAssertEqual(d.backendId, "null")
        let flag = Flag()
        let sub = d.subscribe { _ in flag.fire() }
        try await d.start()
        try await d.stop()
        await d.dispose()
        sub.dispose()
        XCTAssertFalse(flag.fired)
    }

    func testBluetoothAnomalyDtoRoundTrips() throws {
        let a = BluetoothAnomaly(
            source: "radio0", kind: "spoof", severity: 0.7,
            description: "duplicate MAC", observedAtUtc: Date(timeIntervalSince1970: 1_700_000_000))
        let data = try JSONEncoder().encode(a)
        let back = try JSONDecoder().decode(BluetoothAnomaly.self, from: data)
        XCTAssertEqual(a, back)
    }

    // ── DTO Codable round-trips ─────────────────────────────────────────

    func testVisionDtosCodableRoundTrip() throws {
        let face = DetectedFace(
            region: BoundingBox(x: 1, y: 2, width: 30, height: 40),
            confidence: 0.88,
            landmarks: [LandmarkPoint(x: 5, y: 6), LandmarkPoint(x: 7, y: 8)])
        XCTAssertEqual(try roundTrip(face), face)

        let emb = FaceEmbedding(vector: [0.1, 0.2, 0.3], dimension: 3)
        XCTAssertEqual(try roundTrip(emb), emb)

        let plate = PlateRecognitionResult(
            plateText: "CA123456", countryHint: "ZA",
            region: BoundingBox(x: 0, y: 0, width: 100, height: 20), confidence: 0.95)
        XCTAssertEqual(try roundTrip(plate), plate)

        let doc = DocumentVerificationResult(
            isValid: true, documentType: "passport", issuingCountry: "ZA",
            fields: [DocumentField(key: "name", value: "Jane", confidence: 0.9)],
            overallConfidence: 0.9, warnings: nil)
        XCTAssertEqual(try roundTrip(doc), doc)
    }

    private func roundTrip<T: Codable & Equatable>(_ value: T) throws -> T {
        let data = try JSONEncoder().encode(value)
        return try JSONDecoder().decode(T.self, from: data)
    }
}
