// Vision.swift
//
// Port of the CircleAI.Vision contract surface (C# is the exact spec):
//   Primitives.cs        → BoundingBox, LandmarkPoint, DetectedFace, FaceEmbedding,
//                          LivenessResult, DocumentField, DocumentVerificationResult,
//                          PlateRecognitionResult, BluetoothAnomaly
//   IVideoCapture.cs     → VideoPixelFormat, VideoFrame, IVideoCapture, NullVideoCapture
//   Contracts.cs         → IComputerVisionRuntime, IFaceDetector, IFaceEmbedder,
//                          IFaceLivenessDetector, IDocumentVerifier, IPlateRecognizer,
//                          IBluetoothAnomalyDetector
//   NullImplementations.cs → NullComputerVisionRuntime, NullFaceDetector,
//                          NullFaceEmbedder, NullFaceLivenessDetector,
//                          NullDocumentVerifier, NullPlateRecognizer,
//                          NullBluetoothAnomalyDetector
//
// Real ONNX-backed detectors/embedder live in VisionOnnx.swift; the native
// tensor runtime + image decoder they need are injected behind protocols (the
// SDK never bakes in a native CV library — exactly as C# vendors compv/facex
// under native/<sdk>/ and the .NET build injects Microsoft.ML.OnnxRuntime).
//
// NAMING: Swift flattens the C# `CircleAI.Vision` namespace into the single
// `CircleAI` module. None of these type names collide with existing symbols
// (`MediaModality` in Multimodal.swift and `FaceBoundingBox` in Tools.swift are
// distinct names and are left untouched).
//
// C# `ReadOnlyMemory<byte>` maps to `Data`. `IAsyncEnumerable<T>` maps to
// `AsyncStream<T>`. `IAsyncDisposable` maps to a `dispose() async` method.
// The opaque `object?` CV-runtime image maps to `Any?`.

import Foundation

// =====================================================================
// Primitives (Primitives.cs)
// =====================================================================

/// An axis-aligned rectangle in image-pixel coordinates.
/// Port of `CircleAI.Vision.BoundingBox` (a `readonly record struct`).
public struct BoundingBox: Sendable, Equatable, Codable {
    public let x: Int
    public let y: Int
    public let width: Int
    public let height: Int

    public init(x: Int, y: Int, width: Int, height: Int) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }
}

/// A 2D point on a detected face — eye centre, mouth corner, etc.
/// Coordinates are image-pixel space. Port of `CircleAI.Vision.LandmarkPoint`.
public struct LandmarkPoint: Sendable, Equatable, Codable {
    public let x: Int
    public let y: Int

    public init(x: Int, y: Int) {
        self.x = x
        self.y = y
    }
}

/// One detected face with optional landmark fallback.
/// Port of `CircleAI.Vision.DetectedFace`.
public struct DetectedFace: Sendable, Equatable, Codable {
    public let region: BoundingBox
    public let confidence: Float
    public let landmarks: [LandmarkPoint]?

    public init(region: BoundingBox, confidence: Float, landmarks: [LandmarkPoint]? = nil) {
        self.region = region
        self.confidence = confidence
        self.landmarks = landmarks
    }
}

/// A face embedding suitable for similarity search. `vector` is normalised so
/// cosine similarity reduces to a dot product. Port of `CircleAI.Vision.FaceEmbedding`.
public struct FaceEmbedding: Sendable, Equatable, Codable {
    public let vector: [Float]
    public let dimension: Int

    public init(vector: [Float], dimension: Int) {
        self.vector = vector
        self.dimension = dimension
    }
}

/// Outcome of liveness detection — is the camera seeing a real human, a printed
/// photo, a screen replay, a 3D mask, …? Port of `CircleAI.Vision.LivenessResult`.
public struct LivenessResult: Sendable, Equatable, Codable {
    public let isLive: Bool
    public let confidence: Float
    public let failureReason: String?

    public init(isLive: Bool, confidence: Float, failureReason: String? = nil) {
        self.isLive = isLive
        self.confidence = confidence
        self.failureReason = failureReason
    }
}

/// One parsed field from an ID document. Port of `CircleAI.Vision.DocumentField`.
public struct DocumentField: Sendable, Equatable, Codable {
    public let key: String
    public let value: String
    public let confidence: Float

    public init(key: String, value: String, confidence: Float) {
        self.key = key
        self.value = value
        self.confidence = confidence
    }
}

/// Outcome of KYC document verification.
/// Port of `CircleAI.Vision.DocumentVerificationResult`.
public struct DocumentVerificationResult: Sendable, Equatable, Codable {
    public let isValid: Bool
    public let documentType: String
    public let issuingCountry: String
    public let fields: [DocumentField]
    public let overallConfidence: Float
    public let warnings: [String]?

    public init(
        isValid: Bool,
        documentType: String,
        issuingCountry: String,
        fields: [DocumentField],
        overallConfidence: Float,
        warnings: [String]? = nil
    ) {
        self.isValid = isValid
        self.documentType = documentType
        self.issuingCountry = issuingCountry
        self.fields = fields
        self.overallConfidence = overallConfidence
        self.warnings = warnings
    }
}

/// Outcome of license-plate recognition.
/// Port of `CircleAI.Vision.PlateRecognitionResult`.
public struct PlateRecognitionResult: Sendable, Equatable, Codable {
    public let plateText: String
    public let countryHint: String?
    public let region: BoundingBox
    public let confidence: Float

    public init(plateText: String, countryHint: String?, region: BoundingBox, confidence: Float) {
        self.plateText = plateText
        self.countryHint = countryHint
        self.region = region
        self.confidence = confidence
    }
}

/// One observed BLE / RF anomaly. Severity 0-1; higher = more concerning.
/// Port of `CircleAI.Vision.BluetoothAnomaly`.
public struct BluetoothAnomaly: Sendable, Equatable, Codable {
    public let source: String
    public let kind: String
    public let severity: Float
    public let description: String
    public let observedAtUtc: Date

    public init(source: String, kind: String, severity: Float, description: String, observedAtUtc: Date) {
        self.source = source
        self.kind = kind
        self.severity = severity
        self.description = description
        self.observedAtUtc = observedAtUtc
    }
}

// =====================================================================
// Video capture (IVideoCapture.cs)
// =====================================================================

/// Pixel layout of a captured `VideoFrame`. Port of `CircleAI.Vision.VideoPixelFormat`.
public enum VideoPixelFormat: String, Sendable, Equatable, Codable {
    case yuv420 = "Yuv420"
    case nv21 = "Nv21"
    case rgba32 = "Rgba32"
    case bgr24 = "Bgr24"
    case jpeg = "Jpeg"
}

/// One raw camera frame with metadata. `ReadOnlyMemory<byte>` maps to `Data`.
/// Port of `CircleAI.Vision.VideoFrame`.
public struct VideoFrame: Sendable, Equatable {
    public let bytes: Data
    public let width: Int
    public let height: Int
    public let pixelFormat: VideoPixelFormat
    public let capturedAtUtc: Date
    public let rotationDegrees: Int?

    public init(
        bytes: Data,
        width: Int,
        height: Int,
        pixelFormat: VideoPixelFormat,
        capturedAtUtc: Date,
        rotationDegrees: Int? = nil
    ) {
        self.bytes = bytes
        self.width = width
        self.height = height
        self.pixelFormat = pixelFormat
        self.capturedAtUtc = capturedAtUtc
        self.rotationDegrees = rotationDegrees
    }
}

/// Async-stream of camera frames. The C# contract is
/// `IAsyncEnumerable<VideoFrame> CaptureAsync(...)` plus `IAsyncDisposable`.
/// `IAsyncEnumerable<VideoFrame>` maps to `AsyncStream<VideoFrame>`; cancellation
/// is honoured by the producer (it finishes the stream when the owning task is
/// cancelled). Port of `CircleAI.Vision.IVideoCapture`.
public protocol IVideoCapture: AnyObject, Sendable {
    /// Open the camera at the requested resolution and start streaming. The
    /// capture loop terminates when the consuming task is cancelled (mirrors the
    /// C# `CancellationToken` bound to the enumerator).
    func capture(preferredWidth: Int, preferredHeight: Int) -> AsyncStream<VideoFrame>

    /// Async dispose (mirrors `IAsyncDisposable`).
    func dispose() async
}

/// Headless / no-camera fallback — yields nothing. Port of
/// `CircleAI.Vision.NullVideoCapture`.
public final class NullVideoCapture: IVideoCapture, @unchecked Sendable {
    public static let instance = NullVideoCapture()
    public init() {}

    public func capture(preferredWidth: Int, preferredHeight: Int) -> AsyncStream<VideoFrame> {
        // C# `yield break` after honouring cancellation → an immediately-finished
        // stream that produces no frames.
        AsyncStream { continuation in
            continuation.finish()
        }
    }

    public func dispose() async {}
}

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/// Generic CV-runtime primitive. Consumers that need basic image decoding /
/// resize / colour-space ops dispatch through this surface. The backend-private
/// opaque image is `object?` in C# → `Any?` in Swift. Port of
/// `CircleAI.Vision.IComputerVisionRuntime`.
public protocol IComputerVisionRuntime: AnyObject {
    /// Decode bytes into a backend-private opaque image.
    func decode(imageBytes: Data) async -> Any?

    /// Resize an opaque image. Returns a new opaque image.
    func resize(image: Any, width: Int, height: Int) async -> Any?

    /// Backend self-identification — "compv-3.x", "null", etc.
    var backendId: String { get }
}

/// Find faces in an image. Port of `CircleAI.Vision.IFaceDetector`.
public protocol IFaceDetector: AnyObject, Sendable {
    func detect(imageBytes: Data) async throws -> [DetectedFace]
}

/// Convert a detected face into a similarity-search vector. Port of
/// `CircleAI.Vision.IFaceEmbedder`.
public protocol IFaceEmbedder: AnyObject, Sendable {
    var dimension: Int { get }

    func embed(imageBytes: Data, face: DetectedFace) async throws -> FaceEmbedding
}

/// Decide whether the camera is looking at a real person. Port of
/// `CircleAI.Vision.IFaceLivenessDetector`.
public protocol IFaceLivenessDetector: AnyObject, Sendable {
    func check(imageBytes: Data) async throws -> LivenessResult
}

/// Parse + verify a KYC document image. Port of `CircleAI.Vision.IDocumentVerifier`.
public protocol IDocumentVerifier: AnyObject, Sendable {
    func verify(imageBytes: Data) async throws -> DocumentVerificationResult
}

/// Read a license plate from an image. Port of `CircleAI.Vision.IPlateRecognizer`.
public protocol IPlateRecognizer: AnyObject, Sendable {
    func recognize(imageBytes: Data) async throws -> [PlateRecognitionResult]
}

/// Handle returned by `IBluetoothAnomalyDetector.subscribe`; disposing
/// unsubscribes. Port of the `IDisposable` the C# `Subscribe` returns.
public protocol IBluetoothAnomalySubscription: AnyObject, Sendable {
    func dispose()
}

/// Surface for AetherNet adversary detection — BLE / RF anomalies raised by the
/// platform's Bluetooth radio. Implementations are long-running
/// (`start`/`stop` lifecycle). Port of `CircleAI.Vision.IBluetoothAnomalyDetector`
/// (which extends `IAsyncDisposable` → `dispose() async`).
public protocol IBluetoothAnomalyDetector: AnyObject, Sendable {
    /// Subscribe to anomaly events. Returns an unsubscribe handle.
    func subscribe(_ handler: @escaping @Sendable (BluetoothAnomaly) async -> Void) -> IBluetoothAnomalySubscription

    /// Begin monitoring. Idempotent.
    func start() async throws

    /// Stop monitoring. Idempotent.
    func stop() async throws

    /// Backend self-identification — "bluehound-1.x", "null", etc.
    var backendId: String { get }

    /// Async dispose (mirrors `IAsyncDisposable`).
    func dispose() async
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/// No-op vision runtime. Port of `CircleAI.Vision.NullComputerVisionRuntime`.
public final class NullComputerVisionRuntime: IComputerVisionRuntime, @unchecked Sendable {
    public static let instance = NullComputerVisionRuntime()
    public init() {}

    public var backendId: String { "null" }
    public func decode(imageBytes: Data) async -> Any? { nil }
    public func resize(image: Any, width: Int, height: Int) async -> Any? { nil }
}

/// Returns no faces. Useful as the default registration. Port of
/// `CircleAI.Vision.NullFaceDetector`.
public final class NullFaceDetector: IFaceDetector, @unchecked Sendable {
    public static let instance = NullFaceDetector()
    public init() {}

    public func detect(imageBytes: Data) async throws -> [DetectedFace] { [] }
}

/// Returns a zero-vector at the configured dimension. Port of
/// `CircleAI.Vision.NullFaceEmbedder`.
public final class NullFaceEmbedder: IFaceEmbedder, @unchecked Sendable {
    public let dimension: Int

    public init(dimension: Int = 512) {
        self.dimension = dimension
    }

    public func embed(imageBytes: Data, face: DetectedFace) async throws -> FaceEmbedding {
        FaceEmbedding(vector: [Float](repeating: 0, count: dimension), dimension: dimension)
    }
}

/// Reports "no liveness backend" — fail-closed default. Port of
/// `CircleAI.Vision.NullFaceLivenessDetector`.
public final class NullFaceLivenessDetector: IFaceLivenessDetector, @unchecked Sendable {
    public static let instance = NullFaceLivenessDetector()
    public init() {}

    public func check(imageBytes: Data) async throws -> LivenessResult {
        LivenessResult(isLive: false, confidence: 0, failureReason: "no liveness backend registered")
    }
}

/// Reports unverified — fail-closed default. Port of
/// `CircleAI.Vision.NullDocumentVerifier`.
public final class NullDocumentVerifier: IDocumentVerifier, @unchecked Sendable {
    public static let instance = NullDocumentVerifier()
    public init() {}

    public func verify(imageBytes: Data) async throws -> DocumentVerificationResult {
        DocumentVerificationResult(
            isValid: false,
            documentType: "unknown",
            issuingCountry: "unknown",
            fields: [],
            overallConfidence: 0,
            warnings: ["no document verifier backend registered"])
    }
}

/// Returns no plates. Port of `CircleAI.Vision.NullPlateRecognizer`.
public final class NullPlateRecognizer: IPlateRecognizer, @unchecked Sendable {
    public static let instance = NullPlateRecognizer()
    public init() {}

    public func recognize(imageBytes: Data) async throws -> [PlateRecognitionResult] { [] }
}

/// Reports no anomalies; subscribers never fire. Port of
/// `CircleAI.Vision.NullBluetoothAnomalyDetector`.
public final class NullBluetoothAnomalyDetector: IBluetoothAnomalyDetector, @unchecked Sendable {
    public init() {}

    public var backendId: String { "null" }

    public func subscribe(_ handler: @escaping @Sendable (BluetoothAnomaly) async -> Void) -> IBluetoothAnomalySubscription {
        EmptyBluetoothAnomalySubscription.instance
    }
    public func start() async throws {}
    public func stop() async throws {}
    public func dispose() async {}

    /// Port of the private `EmptyDisposable`.
    public final class EmptyBluetoothAnomalySubscription: IBluetoothAnomalySubscription, @unchecked Sendable {
        public static let instance = EmptyBluetoothAnomalySubscription()
        public func dispose() {}
    }
}
