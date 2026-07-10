// VisionOnnxTests.swift
//
// Verifies the deterministic ONNX-backed vision ports (VisionOnnx.swift):
//   • VisionGeometry math (letterbox geometry, tensor packing, YOLO/plate
//     decode, NMS, IoU, clamp, L2-normalise) — the wire-critical algorithms
//     ported byte-for-byte from the C# backends.
//   • OnnxFaceDetector / OnnxFaceEmbedder / OnnxPlateRecognizer end-to-end with
//     deterministic injected image-decoder + tensor-runner fakes (the two native
//     leaves), including the fail-soft-on-inference-failure path.

import XCTest
@testable import CircleAI

final class VisionOnnxTests: XCTestCase {

    // ── Deterministic injected fakes (the two native leaves) ────────────

    /// Decoder that returns a fixed-size solid image, ignoring the bytes.
    struct FakeDecoder: IImageDecoder {
        let width: Int
        let height: Int
        let fill: UInt8
        func decode(imageBytes: Data) throws -> RgbImage {
            RgbImage(width: width, height: height,
                     rgb: [UInt8](repeating: fill, count: width * height * 3))
        }
    }

    /// Decoder that always throws — drives the "not a decodable image" path.
    struct ThrowingDecoder: IImageDecoder {
        struct Boom: Error {}
        func decode(imageBytes: Data) throws -> RgbImage { throw Boom() }
    }

    /// Runner that returns a canned output tensor, ignoring the input.
    struct FakeRunner: IOnnxTensorRunner {
        let output: DenseTensorF
        func run(input: DenseTensorF) async throws -> DenseTensorF { output }
    }

    /// Runner that always throws — drives the fail-soft catch in the detectors.
    struct ThrowingRunner: IOnnxTensorRunner {
        struct Boom: Error {}
        func run(input: DenseTensorF) async throws -> DenseTensorF { throw Boom() }
    }

    /// Runner that records the input it received so we can assert on the tensor
    /// packing / letterbox pipeline the detector fed it.
    final class CapturingRunner: IOnnxTensorRunner, @unchecked Sendable {
        private let lock = NSLock()
        private var _last: DenseTensorF?
        let output: DenseTensorF
        init(output: DenseTensorF) { self.output = output }
        var last: DenseTensorF? { lock.lock(); defer { lock.unlock() }; return _last }
        func run(input: DenseTensorF) async throws -> DenseTensorF {
            lock.lock(); _last = input; lock.unlock()
            return output
        }
    }

    /// Build a YOLO `[1, channels, boxes]` output tensor from a list of
    /// (cx, cy, w, h, score) boxes, laid out channel-major exactly like the C#
    /// `Tensor<float>.ToArray()` (index = c*boxes + n).
    private func yoloTensor(_ boxes: [(Float, Float, Float, Float, Float)], channels: Int = 5) -> DenseTensorF {
        let n = boxes.count
        var data = [Float](repeating: 0, count: channels * n)
        for (i, b) in boxes.enumerated() {
            data[0 * n + i] = b.0
            data[1 * n + i] = b.1
            data[2 * n + i] = b.2
            data[3 * n + i] = b.3
            data[4 * n + i] = b.4
        }
        return DenseTensorF(dimensions: [1, channels, n], data: data)
    }

    // ── Geometry: letterbox ─────────────────────────────────────────────

    func testLetterboxGeometry640x480() {
        let img = RgbImage(width: 640, height: 480, rgb: [UInt8](repeating: 10, count: 640 * 480 * 3))
        let lb = VisionGeometry.letterbox(img, inputSize: 640)
        XCTAssertEqual(lb.scale, 1.0)
        XCTAssertEqual(lb.padX, 0)
        XCTAssertEqual(lb.padY, 80)     // (640 - 480) / 2
        XCTAssertEqual(lb.image.width, 640)
        XCTAssertEqual(lb.image.height, 640)
        // Top padding rows are grey (114); interior copied from the source (10).
        XCTAssertEqual(lb.image.pixel(0, 0).r, 114)         // in pad band
        XCTAssertEqual(lb.image.pixel(320, 320).r, 10)      // in image band
    }

    func testLetterboxGeometryWideImage() {
        // 1000x500 into 640 → scale = 0.64, newW=640, newH=320, padX=0, padY=160.
        let img = RgbImage(width: 1000, height: 500, rgb: [UInt8](repeating: 7, count: 1000 * 500 * 3))
        let lb = VisionGeometry.letterbox(img, inputSize: 640)
        XCTAssertEqual(lb.scale, 0.64, accuracy: 1e-6)
        XCTAssertEqual(lb.padX, 0)
        XCTAssertEqual(lb.padY, 160)
    }

    // ── Geometry: tensor packing ────────────────────────────────────────

    func testToRgbTensorLayoutAndScaling() {
        // 2x1 image: pixel(0,0)=(255,0,0), pixel(1,0)=(0,255,0).
        var rgb = [UInt8](repeating: 0, count: 2 * 1 * 3)
        rgb[0] = 255; rgb[1] = 0; rgb[2] = 0
        rgb[3] = 0;   rgb[4] = 255; rgb[5] = 0
        let img = RgbImage(width: 2, height: 1, rgb: rgb)
        let t = VisionGeometry.toRgbTensor(img)
        XCTAssertEqual(t.dimensions, [1, 3, 1, 2])
        let plane = 2 // h*w
        // R channel
        XCTAssertEqual(t.data[0 * plane + 0], 1.0, accuracy: 1e-6)
        XCTAssertEqual(t.data[0 * plane + 1], 0.0, accuracy: 1e-6)
        // G channel
        XCTAssertEqual(t.data[1 * plane + 0], 0.0, accuracy: 1e-6)
        XCTAssertEqual(t.data[1 * plane + 1], 1.0, accuracy: 1e-6)
        // B channel
        XCTAssertEqual(t.data[2 * plane + 0], 0.0, accuracy: 1e-6)
        XCTAssertEqual(t.data[2 * plane + 1], 0.0, accuracy: 1e-6)
    }

    func testToArcFaceTensorBgrOrderAndNormalisation() {
        // 1x1 image, pixel=(200,100,50). ArcFace: BGR, (v-127.5)/128.
        let img = RgbImage(width: 1, height: 1, rgb: [200, 100, 50])
        let t = VisionGeometry.toArcFaceTensor(img, size: 1)
        XCTAssertEqual(t.dimensions, [1, 3, 1, 1])
        // channel 0 = B = 50, channel 1 = G = 100, channel 2 = R = 200
        XCTAssertEqual(t.data[0], (50.0 - 127.5) / 128.0, accuracy: 1e-6)
        XCTAssertEqual(t.data[1], (100.0 - 127.5) / 128.0, accuracy: 1e-6)
        XCTAssertEqual(t.data[2], (200.0 - 127.5) / 128.0, accuracy: 1e-6)
    }

    // ── Geometry: YOLO decode / back-projection ─────────────────────────

    func testPostprocessYoloBackProjectsBox() {
        // 640x480 source: scale=1, padX=0, padY=80. Box cx=320,cy=320,w=100,h=100.
        // Expected: bx=270, by=190, w=100, h=80 (clamped by origH=480).
        let out = yoloTensor([(320, 320, 100, 100, 0.9)])
        let kept = VisionGeometry.postprocessYolo(
            output: out, origW: 640, origH: 480, padX: 0, padY: 80, scale: 1.0,
            confidenceThreshold: 0.5, iouThreshold: 0.45)
        XCTAssertEqual(kept.count, 1)
        XCTAssertEqual(kept[0].box, BoundingBox(x: 270, y: 190, width: 100, height: 80))
        XCTAssertEqual(kept[0].score, 0.9, accuracy: 1e-6)
    }

    func testPostprocessYoloDropsBelowThreshold() {
        let out = yoloTensor([(320, 320, 100, 100, 0.4)])
        let kept = VisionGeometry.postprocessYolo(
            output: out, origW: 640, origH: 480, padX: 0, padY: 80, scale: 1.0,
            confidenceThreshold: 0.5, iouThreshold: 0.45)
        XCTAssertTrue(kept.isEmpty)
    }

    func testPostprocessYoloRejectsNon3DTensor() {
        let out = DenseTensorF(dimensions: [1, 5], data: [1, 2, 3, 4, 5])
        let kept = VisionGeometry.postprocessYolo(
            output: out, origW: 100, origH: 100, padX: 0, padY: 0, scale: 1.0,
            confidenceThreshold: 0.5, iouThreshold: 0.45)
        XCTAssertTrue(kept.isEmpty)
    }

    func testPostprocessPlatesUsesDirectWidthHeight() {
        // Plate decode scales bw/bh directly (not x2-x1). 640x640, scale=1, no pad.
        // cx=100, cy=100, bw=200, bh=40 → x1=0, y1=80, bx=0, by=80, w=200, h=40.
        let out = yoloTensor([(100, 100, 200, 40, 0.8)])
        let kept = VisionGeometry.postprocessPlates(
            output: out, origW: 640, origH: 640, padX: 0, padY: 0, scale: 1.0,
            confidenceThreshold: 0.5, iouThreshold: 0.45)
        XCTAssertEqual(kept.count, 1)
        XCTAssertEqual(kept[0].box, BoundingBox(x: 0, y: 80, width: 200, height: 40))
    }

    // ── Geometry: NMS + IoU ─────────────────────────────────────────────

    func testIouExactValues() {
        let a = BoundingBox(x: 0, y: 0, width: 10, height: 10)
        let b = BoundingBox(x: 0, y: 0, width: 10, height: 10)
        XCTAssertEqual(VisionGeometry.iou(a, b), 1.0, accuracy: 1e-6)

        let c = BoundingBox(x: 100, y: 100, width: 10, height: 10)
        XCTAssertEqual(VisionGeometry.iou(a, c), 0.0, accuracy: 1e-6)

        // Half-overlap: a=[0,0,10,10], d=[5,0,10,10] → inter=5*10=50, union=200-50=150.
        let d = BoundingBox(x: 5, y: 0, width: 10, height: 10)
        XCTAssertEqual(VisionGeometry.iou(a, d), 50.0 / 150.0, accuracy: 1e-6)
    }

    func testNonMaxSuppressionKeepsHighestSuppressesOverlap() {
        let big = BoundingBox(x: 0, y: 0, width: 100, height: 100)
        let overlap = BoundingBox(x: 5, y: 5, width: 100, height: 100)   // high IoU with big
        let far = BoundingBox(x: 500, y: 500, width: 50, height: 50)     // disjoint
        let kept = VisionGeometry.nonMaxSuppression(
            [(0.6, big), (0.9, overlap), (0.7, far)], iouThreshold: 0.45)
        // overlap (0.9) kept first, big suppressed by it, far survives.
        XCTAssertEqual(kept.count, 2)
        XCTAssertEqual(kept[0].box, overlap)
        XCTAssertEqual(kept[1].box, far)
    }

    // ── Geometry: clamp + L2 ────────────────────────────────────────────

    func testClampRegionInsideBounds() {
        let r = VisionGeometry.clampRegion(
            BoundingBox(x: -5, y: -5, width: 1000, height: 1000), imageWidth: 100, imageHeight: 80)
        XCTAssertEqual(r.x, 0)
        XCTAssertEqual(r.y, 0)
        XCTAssertEqual(r.width, 100)    // clamp(1000, 1, 100 - 0)
        XCTAssertEqual(r.height, 80)
    }

    func testL2NormaliseUnitLength() {
        let v: [Float] = [3, 4]     // norm 5 → [0.6, 0.8]
        let n = VisionGeometry.l2Normalise(v)
        XCTAssertEqual(n[0], 0.6, accuracy: 1e-6)
        XCTAssertEqual(n[1], 0.8, accuracy: 1e-6)
        let mag = (n[0] * n[0] + n[1] * n[1]).squareRoot()
        XCTAssertEqual(mag, 1.0, accuracy: 1e-6)
    }

    func testL2NormaliseZeroVectorIsNoOp() {
        let v: [Float] = [0, 0, 0]
        XCTAssertEqual(VisionGeometry.l2Normalise(v), v)
    }

    // ── End-to-end detectors with injected fakes ────────────────────────

    func testOnnxFaceDetectorEndToEnd() async throws {
        let opts = OnnxFaceDetectorOptions(modelPath: "/models/face.onnx")
        let runner = FakeRunner(output: yoloTensor([(320, 320, 100, 100, 0.9)]))
        let det = OnnxFaceDetector(
            opts: opts,
            decoder: FakeDecoder(width: 640, height: 480, fill: 50),
            runner: runner)
        let faces = try await det.detect(imageBytes: Data([0xFF, 0xD8, 0xFF]))
        XCTAssertEqual(faces.count, 1)
        XCTAssertEqual(faces[0].region, BoundingBox(x: 270, y: 190, width: 100, height: 80))
        XCTAssertEqual(faces[0].confidence, 0.9, accuracy: 1e-6)
        XCTAssertNil(faces[0].landmarks)
    }

    func testOnnxFaceDetectorEmptyBytesShortCircuits() async throws {
        let det = OnnxFaceDetector(
            opts: OnnxFaceDetectorOptions(modelPath: "x"),
            decoder: ThrowingDecoder(),       // must not be reached
            runner: ThrowingRunner())
        let faces = try await det.detect(imageBytes: Data())
        XCTAssertTrue(faces.isEmpty)
    }

    func testOnnxFaceDetectorFailSoftOnInferenceError() async throws {
        let det = OnnxFaceDetector(
            opts: OnnxFaceDetectorOptions(modelPath: "x"),
            decoder: FakeDecoder(width: 640, height: 480, fill: 0),
            runner: ThrowingRunner())
        let faces = try await det.detect(imageBytes: Data([1, 2, 3]))
        XCTAssertTrue(faces.isEmpty)     // C# catch → empty
    }

    func testOnnxFaceDetectorFeedsLetterboxedTensor() async throws {
        let capturing = CapturingRunner(output: yoloTensor([]))
        let det = OnnxFaceDetector(
            opts: OnnxFaceDetectorOptions(modelPath: "x", inputSize: 640),
            decoder: FakeDecoder(width: 640, height: 480, fill: 0),
            runner: capturing)
        _ = try await det.detect(imageBytes: Data([1]))
        // Input tensor must be [1,3,640,640].
        XCTAssertEqual(capturing.last?.dimensions, [1, 3, 640, 640])
    }

    func testOnnxFaceEmbedderEndToEndNormalises() async throws {
        // Runner emits a raw [3,4] non-normalised vector; embedder L2-normalises.
        let raw: [Float] = [3, 4, 0, 0]   // norm 5
        let runner = FakeRunner(output: DenseTensorF(dimensions: [1, 4], data: raw))
        let emb = OnnxFaceEmbedder(
            opts: OnnxFaceEmbedderOptions(modelPath: "/models/arcface.onnx", inputSize: 112, dimension: 4),
            decoder: FakeDecoder(width: 200, height: 200, fill: 128),
            runner: runner)
        let face = DetectedFace(region: BoundingBox(x: 10, y: 10, width: 50, height: 50), confidence: 0.9)
        let out = try await emb.embed(imageBytes: Data([1, 2, 3]), face: face)
        XCTAssertEqual(out.dimension, 4)
        XCTAssertEqual(out.vector[0], 0.6, accuracy: 1e-6)
        XCTAssertEqual(out.vector[1], 0.8, accuracy: 1e-6)
        XCTAssertEqual(out.vector[2], 0.0, accuracy: 1e-6)
    }

    func testOnnxFaceEmbedderFailSoftReturnsZeroVector() async throws {
        let emb = OnnxFaceEmbedder(
            opts: OnnxFaceEmbedderOptions(modelPath: "x", inputSize: 112, dimension: 6),
            decoder: FakeDecoder(width: 100, height: 100, fill: 0),
            runner: ThrowingRunner())
        let face = DetectedFace(region: BoundingBox(x: 0, y: 0, width: 10, height: 10), confidence: 0.5)
        let out = try await emb.embed(imageBytes: Data([1]), face: face)
        XCTAssertEqual(out.dimension, 6)
        XCTAssertTrue(out.vector.allSatisfy { $0 == 0 })
    }

    func testOnnxFaceEmbedderReportsDimension() {
        let emb = OnnxFaceEmbedder(
            opts: OnnxFaceEmbedderOptions(modelPath: "x", dimension: 256),
            decoder: FakeDecoder(width: 1, height: 1, fill: 0),
            runner: FakeRunner(output: DenseTensorF(dimensions: [1, 1], data: [0])))
        XCTAssertEqual(emb.dimension, 256)
    }

    func testOnnxPlateRecognizerEndToEnd() async throws {
        let runner = FakeRunner(output: yoloTensor([(100, 100, 200, 40, 0.8)]))
        let rec = OnnxPlateRecognizer(
            opts: OnnxPlateRecognizerOptions(modelPath: "/models/plate.onnx", inputSize: 640, countryHint: "ZA"),
            decoder: FakeDecoder(width: 640, height: 640, fill: 0),
            runner: runner)
        let plates = try await rec.recognize(imageBytes: Data([1, 2, 3]))
        XCTAssertEqual(plates.count, 1)
        XCTAssertEqual(plates[0].plateText, "")     // OCR is a downstream stage
        XCTAssertEqual(plates[0].countryHint, "ZA")
        XCTAssertEqual(plates[0].region, BoundingBox(x: 0, y: 80, width: 200, height: 40))
        XCTAssertEqual(plates[0].confidence, 0.8, accuracy: 1e-6)
    }

    func testOnnxPlateRecognizerFailSoftOnInferenceError() async throws {
        let rec = OnnxPlateRecognizer(
            opts: OnnxPlateRecognizerOptions(modelPath: "x"),
            decoder: FakeDecoder(width: 640, height: 640, fill: 0),
            runner: ThrowingRunner())
        let plates = try await rec.recognize(imageBytes: Data([1]))
        XCTAssertTrue(plates.isEmpty)
    }

    func testOnnxPlateRecognizerEmptyBytesShortCircuits() async throws {
        let rec = OnnxPlateRecognizer(
            opts: OnnxPlateRecognizerOptions(modelPath: "x"),
            decoder: ThrowingDecoder(),
            runner: ThrowingRunner())
        let plates = try await rec.recognize(imageBytes: Data())
        XCTAssertTrue(plates.isEmpty)
    }
}
