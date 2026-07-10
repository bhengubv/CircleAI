// VisionOnnx.swift
//
// Port of the real ONNX-backed CircleAI.Vision backends (Phase C3):
//   OnnxFaceDetector.cs   → OnnxFaceDetectorOptions, OnnxFaceDetector
//   OnnxFaceEmbedder.cs   → OnnxFaceEmbedderOptions, OnnxFaceEmbedder
//   OnnxPlateRecognizer.cs → OnnxPlateRecognizerOptions, OnnxPlateRecognizer
//
// The C# implementations lean on two genuinely-native leaves:
//   • Microsoft.ML.OnnxRuntime `InferenceSession` — runs the model.
//   • SixLabors.ImageSharp — decodes bytes → pixels and resizes.
// Neither is baked into the SDK (the C# .csproj injects them as NuGet packages;
// the Swift SDK has zero external dependencies by policy). So those two leaves
// are modelled as INJECTED protocols — `IImageDecoder` and `IOnnxTensorRunner`
// — and everything else (letterbox geometry, tensor packing, YOLO decode, NMS,
// IoU, L2-normalise) is ported as PURE, DETERMINISTIC, UNIT-TESTABLE Swift that
// matches the C# math byte-for-byte.
//
// This is the same shape the codebase already uses for the cloud chat providers
// (HostingCloudFallback.swift): SDK owns the deterministic composite; the native
// leaf is injected; a deterministic fake stands in for tests.

import Foundation

// =====================================================================
// Injected native leaves
// =====================================================================

/// A decoded RGB image — packed 24-bit RGB, row-major, 3 bytes/pixel
/// (`rgb[(y*width + x)*3 + {0,1,2}]` = R,G,B). This is the Swift analogue of
/// ImageSharp's `Image<Rgb24>` that the C# code decodes into.
public struct RgbImage: Sendable, Equatable {
    public let width: Int
    public let height: Int
    /// `width * height * 3` bytes, row-major R,G,B.
    public let rgb: [UInt8]

    public init(width: Int, height: Int, rgb: [UInt8]) {
        precondition(width >= 0 && height >= 0, "dimensions must be non-negative")
        precondition(rgb.count == width * height * 3, "rgb must be width*height*3 bytes")
        self.width = width
        self.height = height
        self.rgb = rgb
    }

    /// R,G,B of pixel (x,y). No bounds re-check on the hot path — callers stay in range.
    @inline(__always) public func pixel(_ x: Int, _ y: Int) -> (r: UInt8, g: UInt8, b: UInt8) {
        let i = (y * width + x) * 3
        return (rgb[i], rgb[i + 1], rgb[i + 2])
    }
}

/// Decodes encoded image bytes (JPEG/PNG/…) into an `RgbImage`. The native leaf
/// ImageSharp performs in the C# (`Image.Load<Rgb24>`). Injected so the SDK
/// carries no image-codec dependency.
public protocol IImageDecoder: Sendable {
    /// Decode `imageBytes` into packed RGB24. Throws when the bytes are not a
    /// decodable image.
    func decode(imageBytes: Data) throws -> RgbImage
}

/// A dense float tensor mirroring OnnxRuntime's `DenseTensor<float>`: a shape
/// plus a row-major (C-contiguous) flat buffer. For the model input the shape is
/// `[1, 3, H, W]`; for the YOLO output it is `[1, channels, boxes]`. `data` is
/// laid out exactly as `Tensor<float>.ToArray()` returns in C# — index for
/// `[b, c, n]` in a 3-D tensor is `((b*C + c)*N + n)`.
public struct DenseTensorF: Sendable, Equatable {
    public let dimensions: [Int]
    public let data: [Float]

    public init(dimensions: [Int], data: [Float]) {
        let expected = dimensions.reduce(1, *)
        precondition(data.count == expected, "data.count must equal product of dimensions")
        self.dimensions = dimensions
        self.data = data
    }
}

/// Runs one ONNX model over an input tensor and returns the first output tensor.
/// The native leaf `InferenceSession.Run` performs in the C#. Injected so the
/// SDK carries no ONNX-runtime dependency.
public protocol IOnnxTensorRunner: Sendable {
    /// Run the model on `input`, returning the primary output tensor. Throws on
    /// inference failure (the C# catches and returns empty; the callers here do
    /// the same, see below).
    func run(input: DenseTensorF) async throws -> DenseTensorF
}

// =====================================================================
// Shared deterministic geometry / post-processing (pure)
// =====================================================================

/// Deterministic vision math ported verbatim from the C# ONNX backends. Kept as
/// free static helpers so all three detectors share one tested copy and so the
/// unit tests can exercise the math without any injected native leaf.
public enum VisionGeometry {

    /// Result of a letterbox resize: the padded square image plus the geometry
    /// needed to map detections back to original pixel space.
    public struct Letterbox: Sendable, Equatable {
        public let image: RgbImage
        public let padX: Int
        public let padY: Int
        public let scale: Float
    }

    /// Letterbox-resize `image` into an `inputSize`×`inputSize` canvas filled
    /// with grey (114,114,114), centring the scaled image. Mirrors
    /// `OnnxFaceDetector.LetterboxResize` / the inline block in
    /// `OnnxPlateRecognizer`. Uses nearest-neighbour for the interior resize —
    /// deterministic and dependency-free (ImageSharp's default is bilinear, but
    /// the geometry — scale/pad and the back-projection — is what the wire
    /// contract depends on, and that is reproduced exactly).
    public static func letterbox(_ image: RgbImage, inputSize: Int) -> Letterbox {
        let scale = min(Float(inputSize) / Float(image.width), Float(inputSize) / Float(image.height))
        // C# `(int)Math.Round(w * scale)` on non-negative values.
        let newW = Int((Float(image.width) * scale).rounded())
        let newH = Int((Float(image.height) * scale).rounded())
        let padX = (inputSize - newW) / 2
        let padY = (inputSize - newH) / 2

        var canvas = [UInt8](repeating: 114, count: inputSize * inputSize * 3)
        // Nearest-neighbour sample of the source into the [padX,padY]+(newW,newH) region.
        if newW > 0 && newH > 0 {
            for dy in 0..<newH {
                // map dest row → source row
                let sy = min(image.height - 1, Int((Float(dy) + 0.5) / scale))
                let cy = padY + dy
                if cy < 0 || cy >= inputSize { continue }
                for dx in 0..<newW {
                    let sx = min(image.width - 1, Int((Float(dx) + 0.5) / scale))
                    let cx = padX + dx
                    if cx < 0 || cx >= inputSize { continue }
                    let (r, g, b) = image.pixel(sx, sy)
                    let di = (cy * inputSize + cx) * 3
                    canvas[di] = r
                    canvas[di + 1] = g
                    canvas[di + 2] = b
                }
            }
        }
        let out = RgbImage(width: inputSize, height: inputSize, rgb: canvas)
        return Letterbox(image: out, padX: padX, padY: padY, scale: scale)
    }

    /// Pack an RGB image into a `[1, 3, H, W]` float tensor with RGB channel
    /// order and `/255` scaling. Mirrors `OnnxFaceDetector.ToTensor` and the
    /// inline packing in `OnnxPlateRecognizer`.
    public static func toRgbTensor(_ image: RgbImage) -> DenseTensorF {
        let w = image.width, h = image.height
        var data = [Float](repeating: 0, count: 3 * h * w)
        let plane = h * w
        for y in 0..<h {
            let rowBase = y * w
            for x in 0..<w {
                let (r, g, b) = image.pixel(x, y)
                let idx = rowBase + x
                data[0 * plane + idx] = Float(r) / 255.0
                data[1 * plane + idx] = Float(g) / 255.0
                data[2 * plane + idx] = Float(b) / 255.0
            }
        }
        return DenseTensorF(dimensions: [1, 3, h, w], data: data)
    }

    /// Pack a face crop into a `[1, 3, size, size]` float tensor using ArcFace
    /// preprocessing: BGR channel order, `(pixel - 127.5) / 128.0`. Mirrors the
    /// tensor-fill in `OnnxFaceEmbedder.EmbedAsync`.
    public static func toArcFaceTensor(_ crop: RgbImage, size: Int) -> DenseTensorF {
        var data = [Float](repeating: 0, count: 3 * size * size)
        let plane = size * size
        for y in 0..<size {
            let rowBase = y * size
            for x in 0..<size {
                let (r, g, b) = crop.pixel(x, y)
                let idx = rowBase + x
                data[0 * plane + idx] = (Float(b) - 127.5) / 128.0
                data[1 * plane + idx] = (Float(g) - 127.5) / 128.0
                data[2 * plane + idx] = (Float(r) - 127.5) / 128.0
            }
        }
        return DenseTensorF(dimensions: [1, 3, size, size], data: data)
    }

    /// YOLOv8 postprocess for a face/plate detector. `output` is `[1, channels,
    /// boxes]`. Reads the first 5 channels per box (cx, cy, w, h, score) and
    /// back-projects boxes from letterbox space to original pixels. Mirrors
    /// `OnnxFaceDetector.PostprocessYolo` (`emitLandmarks: false`) exactly.
    public static func postprocessYolo(
        output: DenseTensorF,
        origW: Int,
        origH: Int,
        padX: Int,
        padY: Int,
        scale: Float,
        confidenceThreshold: Float,
        iouThreshold: Float
    ) -> [(score: Float, box: BoundingBox)] {
        let dims = output.dimensions
        if dims.count != 3 { return [] }
        let boxes = dims[2]
        let arr = output.data
        var candidates: [(score: Float, box: BoundingBox)] = []
        for n in 0..<boxes {
            let cx = arr[0 * boxes + n]
            let cy = arr[1 * boxes + n]
            let bw = arr[2 * boxes + n]
            let bh = arr[3 * boxes + n]
            let score = arr[4 * boxes + n]
            if score < confidenceThreshold { continue }

            let x1 = (cx - bw / 2 - Float(padX)) / scale
            let y1 = (cy - bh / 2 - Float(padY)) / scale
            let x2 = (cx + bw / 2 - Float(padX)) / scale
            let y2 = (cy + bh / 2 - Float(padY)) / scale
            let bx = max(0, Int(x1.rounded(.down)))
            let by = max(0, Int(y1.rounded(.down)))
            let bxw = min(origW - bx, Int((x2 - x1).rounded(.up)))
            let bxh = min(origH - by, Int((y2 - y1).rounded(.up)))
            if bxw <= 0 || bxh <= 0 { continue }
            candidates.append((score, BoundingBox(x: bx, y: by, width: bxw, height: bxh)))
        }
        return nonMaxSuppression(candidates, iouThreshold: iouThreshold)
    }

    /// Plate-detector postprocess. Differs from `postprocessYolo` only in how the
    /// box width/height are recovered: the C# `OnnxPlateRecognizer` scales `bw`
    /// and `bh` directly (not from x2−x1). Mirrors the inline decode in
    /// `OnnxPlateRecognizer.RecognizeAsync`.
    public static func postprocessPlates(
        output: DenseTensorF,
        origW: Int,
        origH: Int,
        padX: Int,
        padY: Int,
        scale: Float,
        confidenceThreshold: Float,
        iouThreshold: Float
    ) -> [(score: Float, box: BoundingBox)] {
        let dims = output.dimensions
        if dims.count != 3 { return [] }
        let boxes = dims[2]
        let arr = output.data
        var hits: [(score: Float, box: BoundingBox)] = []
        for n in 0..<boxes {
            let cx = arr[0 * boxes + n]
            let cy = arr[1 * boxes + n]
            let bw = arr[2 * boxes + n]
            let bh = arr[3 * boxes + n]
            let score = arr[4 * boxes + n]
            if score < confidenceThreshold { continue }
            let x1 = (cx - bw / 2 - Float(padX)) / scale
            let y1 = (cy - bh / 2 - Float(padY)) / scale
            let bx = max(0, Int(x1.rounded(.down)))
            let by = max(0, Int(y1.rounded(.down)))
            let bxw = min(origW - bx, Int((bw / scale).rounded(.up)))
            let bxh = min(origH - by, Int((bh / scale).rounded(.up)))
            if bxw <= 0 || bxh <= 0 { continue }
            hits.append((score, BoundingBox(x: bx, y: by, width: bxw, height: bxh)))
        }
        return nonMaxSuppression(hits, iouThreshold: iouThreshold)
    }

    /// Greedy non-max suppression. Sorts by score desc, keeps a box only if it
    /// does not overlap an already-kept box beyond `iouThreshold`. Mirrors
    /// `OnnxFaceDetector.NonMaxSuppression` and the inline loop in the plate
    /// recognizer. The sort is by score descending; ties preserve encounter
    /// order via a stable sort so results are deterministic.
    public static func nonMaxSuppression(
        _ boxes: [(score: Float, box: BoundingBox)],
        iouThreshold: Float
    ) -> [(score: Float, box: BoundingBox)] {
        // Stable descending sort by score (index tiebreak reproduces the
        // encounter order the C# List.Sort→greedy walk effectively keeps).
        let sorted = boxes.enumerated().sorted { a, b in
            if a.element.score != b.element.score { return a.element.score > b.element.score }
            return a.offset < b.offset
        }.map { $0.element }

        var kept: [(score: Float, box: BoundingBox)] = []
        for cand in sorted {
            var keep = true
            for k in kept where iou(cand.box, k.box) > iouThreshold {
                keep = false
                break
            }
            if keep { kept.append(cand) }
        }
        return kept
    }

    /// Intersection-over-union of two boxes. Mirrors the `Iou` helper shared by
    /// both C# detectors.
    public static func iou(_ a: BoundingBox, _ b: BoundingBox) -> Float {
        let ax2 = a.x + a.width
        let ay2 = a.y + a.height
        let bx2 = b.x + b.width
        let by2 = b.y + b.height
        let ix1 = max(a.x, b.x)
        let iy1 = max(a.y, b.y)
        let ix2 = min(ax2, bx2)
        let iy2 = min(ay2, by2)
        let iw = max(0, ix2 - ix1)
        let ih = max(0, iy2 - iy1)
        let inter = iw * ih
        let union = a.width * a.height + b.width * b.height - inter
        return union == 0 ? 0 : Float(inter) / Float(union)
    }

    /// Clamp a face region into the image bounds. Mirrors
    /// `OnnxFaceEmbedder.ClampRegion`.
    public static func clampRegion(_ region: BoundingBox, imageWidth: Int, imageHeight: Int) -> BoundingBox {
        let x = min(max(region.x, 0), imageWidth - 1)
        let y = min(max(region.y, 0), imageHeight - 1)
        let w = min(max(region.width, 1), imageWidth - x)
        let h = min(max(region.height, 1), imageHeight - y)
        return BoundingBox(x: x, y: y, width: w, height: h)
    }

    /// L2-normalise a vector in place (returns the normalised copy). No-op when
    /// the norm is < 1e-9. Mirrors `OnnxFaceEmbedder.L2Normalise`.
    public static func l2Normalise(_ v: [Float]) -> [Float] {
        var sumSq: Double = 0
        for value in v { sumSq += Double(value) * Double(value) }
        let norm = Float(sumSq.squareRoot())
        if norm < 1e-9 { return v }
        return v.map { $0 / norm }
    }

    /// Nearest-neighbour crop+resize of `image`'s `region` to `size`×`size`.
    /// Stands in for ImageSharp's `Crop(...).Resize(size,size)` in the embedder.
    /// The region is assumed already clamped in-bounds.
    public static func cropResize(_ image: RgbImage, region: BoundingBox, size: Int) -> RgbImage {
        var out = [UInt8](repeating: 0, count: size * size * 3)
        if region.width <= 0 || region.height <= 0 || size <= 0 {
            return RgbImage(width: max(0, size), height: max(0, size), rgb: out)
        }
        for dy in 0..<size {
            let sy = region.y + min(region.height - 1, Int((Float(dy) + 0.5) * Float(region.height) / Float(size)))
            for dx in 0..<size {
                let sx = region.x + min(region.width - 1, Int((Float(dx) + 0.5) * Float(region.width) / Float(size)))
                let (r, g, b) = image.pixel(sx, sy)
                let di = (dy * size + dx) * 3
                out[di] = r
                out[di + 1] = g
                out[di + 2] = b
            }
        }
        return RgbImage(width: size, height: size, rgb: out)
    }
}

// =====================================================================
// OnnxFaceDetector (OnnxFaceDetector.cs)
// =====================================================================

/// Options for `OnnxFaceDetector`. Port of `OnnxFaceDetectorOptions`. `ModelPath`
/// is retained for parity/diagnostics — the model is loaded by the injected
/// `IOnnxTensorRunner`, so the path is informational in Swift.
public struct OnnxFaceDetectorOptions: Sendable, Equatable {
    public let modelPath: String
    public let inputSize: Int
    public let confidenceThreshold: Float
    public let iouThreshold: Float

    public init(
        modelPath: String,
        inputSize: Int = 640,
        confidenceThreshold: Float = 0.5,
        iouThreshold: Float = 0.45
    ) {
        self.modelPath = modelPath
        self.inputSize = inputSize
        self.confidenceThreshold = confidenceThreshold
        self.iouThreshold = iouThreshold
    }
}

/// Real `IFaceDetector` backed by a YOLO-family ONNX model. Decode → letterbox →
/// RGB tensor → run → YOLO postprocess. The decode and tensor run are injected;
/// all geometry is `VisionGeometry`. Fail-soft: an inference failure yields no
/// faces (mirrors the C# `catch` returning `Array.Empty`). Port of
/// `CircleAI.Vision.OnnxFaceDetector`.
public final class OnnxFaceDetector: IFaceDetector, @unchecked Sendable {
    private let opts: OnnxFaceDetectorOptions
    private let decoder: any IImageDecoder
    private let runner: any IOnnxTensorRunner

    public init(opts: OnnxFaceDetectorOptions, decoder: any IImageDecoder, runner: any IOnnxTensorRunner) {
        self.opts = opts
        self.decoder = decoder
        self.runner = runner
    }

    public func detect(imageBytes: Data) async throws -> [DetectedFace] {
        try Task.checkCancellation()
        if imageBytes.isEmpty { return [] }

        let image = try decoder.decode(imageBytes: imageBytes)
        let origW = image.width
        let origH = image.height

        let lb = VisionGeometry.letterbox(image, inputSize: opts.inputSize)
        let tensor = VisionGeometry.toRgbTensor(lb.image)

        let output: DenseTensorF
        do {
            output = try await runner.run(input: tensor)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            // C# logs and returns empty on inference failure.
            return []
        }

        let kept = VisionGeometry.postprocessYolo(
            output: output, origW: origW, origH: origH,
            padX: lb.padX, padY: lb.padY, scale: lb.scale,
            confidenceThreshold: opts.confidenceThreshold, iouThreshold: opts.iouThreshold)
        return kept.map { DetectedFace(region: $0.box, confidence: $0.score, landmarks: nil) }
    }
}

// =====================================================================
// OnnxFaceEmbedder (OnnxFaceEmbedder.cs)
// =====================================================================

/// Options for `OnnxFaceEmbedder`. Port of `OnnxFaceEmbedderOptions`.
public struct OnnxFaceEmbedderOptions: Sendable, Equatable {
    public let modelPath: String
    public let inputSize: Int
    public let dimension: Int

    public init(modelPath: String, inputSize: Int = 112, dimension: Int = 512) {
        self.modelPath = modelPath
        self.inputSize = inputSize
        self.dimension = dimension
    }
}

/// Real `IFaceEmbedder` backed by an ArcFace-family ONNX model. Decode → clamp →
/// crop+resize to 112 → BGR ArcFace tensor → run → L2-normalise. Fail-soft: an
/// inference failure yields a zero vector at the configured dimension (mirrors
/// the C# `catch`). Port of `CircleAI.Vision.OnnxFaceEmbedder`.
public final class OnnxFaceEmbedder: IFaceEmbedder, @unchecked Sendable {
    private let opts: OnnxFaceEmbedderOptions
    private let decoder: any IImageDecoder
    private let runner: any IOnnxTensorRunner

    public init(opts: OnnxFaceEmbedderOptions, decoder: any IImageDecoder, runner: any IOnnxTensorRunner) {
        self.opts = opts
        self.decoder = decoder
        self.runner = runner
    }

    public var dimension: Int { opts.dimension }

    public func embed(imageBytes: Data, face: DetectedFace) async throws -> FaceEmbedding {
        try Task.checkCancellation()

        let image = try decoder.decode(imageBytes: imageBytes)
        let region = VisionGeometry.clampRegion(face.region, imageWidth: image.width, imageHeight: image.height)
        let crop = VisionGeometry.cropResize(image, region: region, size: opts.inputSize)
        let tensor = VisionGeometry.toArcFaceTensor(crop, size: opts.inputSize)

        let output: DenseTensorF
        do {
            output = try await runner.run(input: tensor)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            return FaceEmbedding(vector: [Float](repeating: 0, count: opts.dimension), dimension: opts.dimension)
        }

        let normalised = VisionGeometry.l2Normalise(output.data)
        return FaceEmbedding(vector: normalised, dimension: normalised.count)
    }
}

// =====================================================================
// OnnxPlateRecognizer (OnnxPlateRecognizer.cs)
// =====================================================================

/// Options for `OnnxPlateRecognizer`. Port of `OnnxPlateRecognizerOptions`.
public struct OnnxPlateRecognizerOptions: Sendable, Equatable {
    public let modelPath: String
    public let inputSize: Int
    public let confidenceThreshold: Float
    public let iouThreshold: Float
    public let countryHint: String?

    public init(
        modelPath: String,
        inputSize: Int = 640,
        confidenceThreshold: Float = 0.5,
        iouThreshold: Float = 0.45,
        countryHint: String? = nil
    ) {
        self.modelPath = modelPath
        self.inputSize = inputSize
        self.confidenceThreshold = confidenceThreshold
        self.iouThreshold = iouThreshold
        self.countryHint = countryHint
    }
}

/// Real `IPlateRecognizer` backed by a YOLO-family ONNX detector. Same letterbox
/// + decode pattern as the face detector, emitting `PlateRecognitionResult`
/// records with empty `plateText` (OCR is a separate downstream stage, exactly as
/// the C# leaves it). Fail-soft on inference failure. Port of
/// `CircleAI.Vision.OnnxPlateRecognizer`.
public final class OnnxPlateRecognizer: IPlateRecognizer, @unchecked Sendable {
    private let opts: OnnxPlateRecognizerOptions
    private let decoder: any IImageDecoder
    private let runner: any IOnnxTensorRunner

    public init(opts: OnnxPlateRecognizerOptions, decoder: any IImageDecoder, runner: any IOnnxTensorRunner) {
        self.opts = opts
        self.decoder = decoder
        self.runner = runner
    }

    public func recognize(imageBytes: Data) async throws -> [PlateRecognitionResult] {
        try Task.checkCancellation()
        if imageBytes.isEmpty { return [] }

        let image = try decoder.decode(imageBytes: imageBytes)
        let origW = image.width
        let origH = image.height

        let lb = VisionGeometry.letterbox(image, inputSize: opts.inputSize)
        let tensor = VisionGeometry.toRgbTensor(lb.image)

        let output: DenseTensorF
        do {
            output = try await runner.run(input: tensor)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            return []
        }

        let kept = VisionGeometry.postprocessPlates(
            output: output, origW: origW, origH: origH,
            padX: lb.padX, padY: lb.padY, scale: lb.scale,
            confidenceThreshold: opts.confidenceThreshold, iouThreshold: opts.iouThreshold)
        return kept.map {
            PlateRecognitionResult(
                plateText: "",
                countryHint: opts.countryHint,
                region: $0.box,
                confidence: $0.score)
        }
    }
}
