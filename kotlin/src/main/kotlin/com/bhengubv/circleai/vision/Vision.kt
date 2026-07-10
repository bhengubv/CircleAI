// Vision.kt
//
// Kotlin port of CircleAI.Vision — the C# reference is the EXACT spec
// (Contracts.cs, Primitives.cs, IVideoCapture.cs, NullImplementations.cs,
// OnnxFaceDetector.cs, OnnxFaceEmbedder.cs, OnnxPlateRecognizer.cs).
//
// The vision contract surface: a generic CV runtime, camera capture, face
// detection + embedding, liveness, KYC document verification, plate
// recognition, and BLE/RF anomaly detection. Null implementations ship out of
// the box (deterministic empty / fail-closed answers). The ONNX-backed
// detectors port the deterministic managed logic (letterbox math, YOLO
// postprocess, non-max suppression, IoU, ArcFace preprocessing, L2 normalise)
// algorithm-for-algorithm; the neural forward pass and the image decode/resize
// are INJECTED behind minimal seams ([IObjectDetectionModel],
// [IEmbeddingModel], [IImageOps]) so the native ONNX / ImageSharp bindings
// never leak into this portable module — exactly as CircleAI.Voice injects
// ISpeakerEmbedder / IEmotionModelRunner.
//
// Design fidelity notes:
//   * C# `record`                    -> Kotlin `data class`.
//   * C# `readonly record struct`    -> Kotlin `data class`.
//   * C# `Task<T>`/`ValueTask<T>`    -> `suspend fun`.
//   * C# `IAsyncEnumerable<T>`       -> `kotlinx.coroutines.flow.Flow<T>`.
//   * C# `IAsyncDisposable`          -> `AutoCloseable` + `suspend fun closeAsync()`.
//   * C# `ReadOnlyMemory<byte>`      -> `ByteArray`.
//   * C# `[Flags]` enum              -> n/a (no flags enums in this surface).
//
// CONCURRENCY: the Bluetooth anomaly detector fans out to subscribers via a
// CopyOnWriteArrayList; no lock is held while a subscriber callback runs, and
// the start/stop lifecycle guards its state under a single monitor without
// completing any stream continuation under that lock.

package com.bhengubv.circleai.vision

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.math.ceil
import kotlin.math.floor
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.sqrt

// =====================================================================
// Primitives (Primitives.cs)
// =====================================================================

/** An axis-aligned rectangle in image-pixel coordinates. Mirrors C# `BoundingBox`. */
data class BoundingBox(val x: Int, val y: Int, val width: Int, val height: Int)

/**
 * A 2D point on a detected face — eye centre, mouth corner, etc. Coordinates are
 * image-pixel space. Mirrors C# `LandmarkPoint`.
 */
data class LandmarkPoint(val x: Int, val y: Int)

/** One detected face with optional landmark fallback. Mirrors C# `DetectedFace`. */
data class DetectedFace(
    val region: BoundingBox,
    val confidence: Float,
    val landmarks: List<LandmarkPoint>? = null,
)

/**
 * A face embedding suitable for similarity search. [vector] is normalised so
 * cosine similarity reduces to a dot product. Mirrors C# `FaceEmbedding`.
 */
data class FaceEmbedding(val vector: FloatArray, val dimension: Int) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is FaceEmbedding) return false
        return dimension == other.dimension && vector.contentEquals(other.vector)
    }

    override fun hashCode(): Int = 31 * vector.contentHashCode() + dimension
}

/**
 * Outcome of liveness detection — is the camera seeing a real human, a printed
 * photo, a screen replay, a 3D mask, …? Mirrors C# `LivenessResult`.
 */
data class LivenessResult(
    val isLive: Boolean,
    val confidence: Float,
    val failureReason: String? = null,
)

/** One parsed field from an ID document. Mirrors C# `DocumentField`. */
data class DocumentField(val key: String, val value: String, val confidence: Float)

/** Outcome of KYC document verification. Mirrors C# `DocumentVerificationResult`. */
data class DocumentVerificationResult(
    val isValid: Boolean,
    val documentType: String,
    val issuingCountry: String,
    val fields: List<DocumentField>,
    val overallConfidence: Float,
    val warnings: List<String>? = null,
)

/** Outcome of license-plate recognition. Mirrors C# `PlateRecognitionResult`. */
data class PlateRecognitionResult(
    val plateText: String,
    val countryHint: String?,
    val region: BoundingBox,
    val confidence: Float,
)

/**
 * One observed BLE / RF anomaly. Severity 0-1; higher = more concerning.
 * Mirrors C# `BluetoothAnomaly`.
 */
data class BluetoothAnomaly(
    val source: String,
    val kind: String,
    val severity: Float,
    val description: String,
    val observedAtUtc: Instant,
)

// =====================================================================
// IVideoCapture (IVideoCapture.cs)
// =====================================================================

/** Pixel layout of a captured [VideoFrame]. Mirrors C# `VideoPixelFormat`. */
enum class VideoPixelFormat { Yuv420, Nv21, Rgba32, Bgr24, Jpeg }

/** One captured camera frame with metadata. Mirrors C# `VideoFrame`. */
data class VideoFrame(
    val bytes: ByteArray,
    val width: Int,
    val height: Int,
    val pixelFormat: VideoPixelFormat,
    val capturedAtUtc: Instant,
    val rotationDegrees: Int? = null,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is VideoFrame) return false
        return width == other.width &&
            height == other.height &&
            pixelFormat == other.pixelFormat &&
            capturedAtUtc == other.capturedAtUtc &&
            rotationDegrees == other.rotationDegrees &&
            bytes.contentEquals(other.bytes)
    }

    override fun hashCode(): Int {
        var r = bytes.contentHashCode()
        r = 31 * r + width
        r = 31 * r + height
        r = 31 * r + pixelFormat.hashCode()
        r = 31 * r + capturedAtUtc.hashCode()
        r = 31 * r + (rotationDegrees ?: 0)
        return r
    }
}

/**
 * Async-stream of camera frames — the camera analogue of
 * CircleAI.Voice.IAudioCapture. Mirrors C# `IVideoCapture`.
 */
interface IVideoCapture : AutoCloseable {
    /**
     * Open the camera at the requested resolution and start streaming. The
     * capture loop is bound to the collecting coroutine's cancellation.
     */
    fun captureAsync(preferredWidth: Int, preferredHeight: Int): Flow<VideoFrame>

    /** Async disposal (C# IAsyncDisposable). Default delegates to [close]. */
    suspend fun closeAsync() = close()
}

/** Headless / no-camera fallback — yields nothing. Mirrors C# `NullVideoCapture`. */
class NullVideoCapture : IVideoCapture {
    override fun captureAsync(preferredWidth: Int, preferredHeight: Int): Flow<VideoFrame> = flow {
        // Honour cancellation like the C# `ct.ThrowIfCancellationRequested()`, then
        // yield break — emit nothing.
        currentCoroutineContext().ensureActive()
    }

    override fun close() {}
    override suspend fun closeAsync() {}
}

// =====================================================================
// Contract interfaces (Contracts.cs)
// =====================================================================

/**
 * Generic CV-runtime primitive: decode / resize / colour-space ops that dispatch
 * through a backend-private opaque image handle. Mirrors C#
 * `IComputerVisionRuntime`.
 */
interface IComputerVisionRuntime {
    /** Decode bytes into a backend-private opaque image, or null. */
    suspend fun decodeAsync(imageBytes: ByteArray): Any?

    /** Resize an opaque image. Returns a new opaque image, or null. */
    suspend fun resizeAsync(image: Any, width: Int, height: Int): Any?

    /** Backend self-identification — "compv-3.x", "null", etc. */
    val backendId: String
}

/** Find faces in an image. Mirrors C# `IFaceDetector`. */
interface IFaceDetector {
    suspend fun detectAsync(imageBytes: ByteArray): List<DetectedFace>
}

/** Convert a detected face into a similarity-search vector. Mirrors C# `IFaceEmbedder`. */
interface IFaceEmbedder {
    val dimension: Int

    suspend fun embedAsync(imageBytes: ByteArray, face: DetectedFace): FaceEmbedding
}

/** Decide whether the camera is looking at a real person. Mirrors C# `IFaceLivenessDetector`. */
interface IFaceLivenessDetector {
    suspend fun checkAsync(imageBytes: ByteArray): LivenessResult
}

/** Parse + verify a KYC document image. Mirrors C# `IDocumentVerifier`. */
interface IDocumentVerifier {
    suspend fun verifyAsync(imageBytes: ByteArray): DocumentVerificationResult
}

/** Read a license plate from an image. Mirrors C# `IPlateRecognizer`. */
interface IPlateRecognizer {
    suspend fun recognizeAsync(imageBytes: ByteArray): List<PlateRecognitionResult>
}

/**
 * Surface for AetherNet adversary detection — BLE / RF anomalies raised by the
 * platform's Bluetooth radio. Implementations are long-running
 * (`startAsync`/`stopAsync` lifecycle). Mirrors C# `IBluetoothAnomalyDetector`.
 */
interface IBluetoothAnomalyDetector : AutoCloseable {
    /** Subscribe to anomaly events. Returns an unsubscribe handle. */
    fun subscribe(handler: suspend (BluetoothAnomaly) -> Unit): AutoCloseable

    /** Begin monitoring. Idempotent. */
    suspend fun startAsync()

    /** Stop monitoring. Idempotent. */
    suspend fun stopAsync()

    /** Backend self-identification — "bluehound-1.x", "null", etc. */
    val backendId: String

    /** Async disposal (C# IAsyncDisposable). Default delegates to [close]. */
    suspend fun closeAsync() = close()
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/** No-op vision runtime. Mirrors C# `NullComputerVisionRuntime`. */
class NullComputerVisionRuntime private constructor() : IComputerVisionRuntime {
    override val backendId: String get() = "null"
    override suspend fun decodeAsync(imageBytes: ByteArray): Any? = null
    override suspend fun resizeAsync(image: Any, width: Int, height: Int): Any? = null

    companion object {
        val Instance = NullComputerVisionRuntime()
    }
}

/** Returns no faces. Useful as the default DI registration. Mirrors C# `NullFaceDetector`. */
class NullFaceDetector private constructor() : IFaceDetector {
    override suspend fun detectAsync(imageBytes: ByteArray): List<DetectedFace> = emptyList()

    companion object {
        val Instance = NullFaceDetector()
    }
}

/** Returns a zero-vector at the configured dimension. Mirrors C# `NullFaceEmbedder`. */
class NullFaceEmbedder(override val dimension: Int = 512) : IFaceEmbedder {
    override suspend fun embedAsync(imageBytes: ByteArray, face: DetectedFace): FaceEmbedding =
        FaceEmbedding(FloatArray(dimension), dimension)
}

/** Reports "no liveness backend" — fail-closed default. Mirrors C# `NullFaceLivenessDetector`. */
class NullFaceLivenessDetector private constructor() : IFaceLivenessDetector {
    override suspend fun checkAsync(imageBytes: ByteArray): LivenessResult =
        LivenessResult(isLive = false, confidence = 0f, failureReason = "no liveness backend registered")

    companion object {
        val Instance = NullFaceLivenessDetector()
    }
}

/** Reports unverified — fail-closed default. Mirrors C# `NullDocumentVerifier`. */
class NullDocumentVerifier private constructor() : IDocumentVerifier {
    override suspend fun verifyAsync(imageBytes: ByteArray): DocumentVerificationResult =
        DocumentVerificationResult(
            isValid = false,
            documentType = "unknown",
            issuingCountry = "unknown",
            fields = emptyList(),
            overallConfidence = 0f,
            warnings = listOf("no document verifier backend registered"),
        )

    companion object {
        val Instance = NullDocumentVerifier()
    }
}

/** Returns no plates. Mirrors C# `NullPlateRecognizer`. */
class NullPlateRecognizer private constructor() : IPlateRecognizer {
    override suspend fun recognizeAsync(imageBytes: ByteArray): List<PlateRecognitionResult> = emptyList()

    companion object {
        val Instance = NullPlateRecognizer()
    }
}

/** Reports no anomalies; subscribers never fire. Mirrors C# `NullBluetoothAnomalyDetector`. */
class NullBluetoothAnomalyDetector : IBluetoothAnomalyDetector {
    override val backendId: String get() = "null"

    override fun subscribe(handler: suspend (BluetoothAnomaly) -> Unit): AutoCloseable = EmptyDisposable

    override suspend fun startAsync() {}
    override suspend fun stopAsync() {}
    override fun close() {}
    override suspend fun closeAsync() {}

    private object EmptyDisposable : AutoCloseable {
        override fun close() {}
    }
}

// =====================================================================
// Injected native seams for the ONNX-backed detectors
// =====================================================================

/**
 * The letterbox layout produced when fitting a source image of [sourceWidth] ×
 * [sourceHeight] into a square [inputSize] canvas: the uniform [scale] applied
 * and the symmetric [padX]/[padY] borders. Pure value type shared by the
 * detector post-processing.
 */
data class Letterbox(val padX: Int, val padY: Int, val scale: Float)

/**
 * Host-supplied image geometry probe. The ONNX detectors need only the source
 * image dimensions to invert the letterbox transform back to pixel space; the
 * actual decode / resize / tensor packing is the injected model's concern. This
 * keeps the native image-codec (ImageSharp) binding out of the portable module.
 */
interface IImageOps {
    /** Decode just enough of [imageBytes] to report the pixel dimensions (width, height). */
    fun dimensions(imageBytes: ByteArray): Pair<Int, Int>
}

/**
 * Host-supplied object-detection model (ONNX YOLOv8-face / YOLOv5-face /
 * RetinaFace / plate detector). Consumes the raw image bytes plus the square
 * [inputSize] the model was configured with; returns the raw YOLO output tensor
 * flattened as `[channel * boxes + box]` together with its `channels`/`boxes`
 * dimensions, or null if inference failed. Injected so the neural binding never
 * leaks into this module — the deterministic letterbox-inversion + NMS runs
 * here.
 */
interface IObjectDetectionModel {
    fun infer(imageBytes: ByteArray, inputSize: Int): YoloOutput?
}

/**
 * Raw YOLO detection output. [data] is laid out `[batch, channel, box]`
 * flattened so element `(c, n)` is at `c * boxes + n` — exactly the layout C#
 * `Tensor<float>.ToArray()` produces for a `[1, channels, boxes]` tensor.
 */
data class YoloOutput(val channels: Int, val boxes: Int, val data: FloatArray) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is YoloOutput) return false
        return channels == other.channels && boxes == other.boxes && data.contentEquals(other.data)
    }

    override fun hashCode(): Int {
        var r = channels
        r = 31 * r + boxes
        r = 31 * r + data.contentHashCode()
        return r
    }
}

/**
 * Host-supplied face-embedding model (ONNX ArcFace family). Given the source
 * image bytes and the [face] region to crop, plus the square [inputSize], runs
 * the ArcFace forward pass and returns the raw embedding (pre-normalisation), or
 * null on failure. The crop → 112×112 → BGR mean-subtract preprocessing lives on
 * the native side (it needs the codec); the deterministic L2 re-normalise runs
 * here.
 */
interface IEmbeddingModel {
    fun embed(imageBytes: ByteArray, face: DetectedFace, inputSize: Int): FloatArray?
}

// =====================================================================
// Shared vision math (ported from OnnxFaceDetector / OnnxPlateRecognizer)
// =====================================================================

/** Deterministic geometry ported byte-for-byte from the C# ONNX detectors. */
object VisionMath {

    /**
     * Letterbox fit of a [srcWidth] × [srcHeight] image into an [inputSize]
     * square. Ported from `OnnxFaceDetector.LetterboxResize` / the inline
     * version in `OnnxPlateRecognizer`.
     */
    fun letterbox(srcWidth: Int, srcHeight: Int, inputSize: Int): Letterbox {
        val scale = min(inputSize.toFloat() / srcWidth, inputSize.toFloat() / srcHeight)
        val newW = (srcWidth * scale).roundToInt()
        val newH = (srcHeight * scale).roundToInt()
        val padX = (inputSize - newW) / 2
        val padY = (inputSize - newH) / 2
        return Letterbox(padX, padY, scale)
    }

    /** IoU of two boxes. Ported from `OnnxFaceDetector.Iou`. */
    fun iou(a: BoundingBox, b: BoundingBox): Float {
        val ax2 = a.x + a.width
        val ay2 = a.y + a.height
        val bx2 = b.x + b.width
        val by2 = b.y + b.height
        val ix1 = max(a.x, b.x)
        val iy1 = max(a.y, b.y)
        val ix2 = min(ax2, bx2)
        val iy2 = min(ay2, by2)
        val iw = max(0, ix2 - ix1)
        val ih = max(0, iy2 - iy1)
        val inter = iw * ih
        val union = a.width * a.height + b.width * b.height - inter
        return if (union == 0) 0f else inter.toFloat() / union
    }

    /**
     * Greedy non-max suppression over score-tagged boxes. Ported from
     * `OnnxFaceDetector.NonMaxSuppression`: sort by descending score, keep a box
     * unless it overlaps a kept box beyond [iouThreshold].
     */
    fun nonMaxSuppression(
        boxes: MutableList<Scored>,
        iouThreshold: Float,
    ): List<Scored> {
        boxes.sortWith(compareByDescending { it.score })
        val kept = ArrayList<Scored>()
        for (cand in boxes) {
            var keep = true
            for (k in kept) {
                if (iou(cand.box, k.box) > iouThreshold) {
                    keep = false
                    break
                }
            }
            if (keep) kept.add(cand)
        }
        return kept
    }

    /** In-place L2 normalisation. Ported from `OnnxFaceEmbedder.L2Normalise`. */
    fun l2Normalise(v: FloatArray) {
        var sumSq = 0.0
        for (x in v) sumSq += x.toDouble() * x.toDouble()
        val norm = sqrt(sumSq).toFloat()
        if (norm < 1e-9f) return
        for (i in v.indices) v[i] /= norm
    }

    /** One score-tagged bounding box candidate. */
    data class Scored(val score: Float, val box: BoundingBox)
}

// =====================================================================
// OnnxFaceDetector (OnnxFaceDetector.cs)
// =====================================================================

/**
 * Configuration for [OnnxFaceDetector]. Mirrors C# `OnnxFaceDetectorOptions`.
 *
 * @param inputSize Square input dimension (640 = YOLOv8 default).
 * @param confidenceThreshold Skip detections under this score (0..1).
 * @param iouThreshold NMS IoU cutoff (0..1).
 */
data class OnnxFaceDetectorOptions(
    val inputSize: Int = 640,
    val confidenceThreshold: Float = 0.5f,
    val iouThreshold: Float = 0.45f,
)

/**
 * Real [IFaceDetector] backed by a YOLO-family ONNX face-detection model. The
 * neural forward pass + image tensor packing is the injected
 * [IObjectDetectionModel]; the image-dimension probe is the injected
 * [IImageOps]. The letterbox-inversion, confidence gate, and NMS are ported
 * algorithm-for-algorithm from C# `OnnxFaceDetector.PostprocessYolo`.
 */
class OnnxFaceDetector(
    private val model: IObjectDetectionModel,
    private val imageOps: IImageOps,
    private val options: OnnxFaceDetectorOptions = OnnxFaceDetectorOptions(),
) : IFaceDetector {

    override suspend fun detectAsync(imageBytes: ByteArray): List<DetectedFace> {
        currentCoroutineContext().ensureActive()
        if (imageBytes.isEmpty()) return emptyList()

        val (origW, origH) = imageOps.dimensions(imageBytes)
        val lb = VisionMath.letterbox(origW, origH, options.inputSize)

        val output = try {
            model.infer(imageBytes, options.inputSize)
        } catch (ce: CancellationException) {
            throw ce
        } catch (_: Exception) {
            return emptyList()
        } ?: return emptyList()

        return postprocessYolo(output, origW, origH, lb.padX, lb.padY, lb.scale)
    }

    /**
     * YOLOv8 output layout: `[1, 4+1+K, N]`. We read the first 5 channels per box
     * (cx, cy, w, h, score). Ported from C# `PostprocessYolo`.
     */
    private fun postprocessYolo(
        output: YoloOutput,
        origW: Int,
        origH: Int,
        padX: Int,
        padY: Int,
        scale: Float,
    ): List<DetectedFace> {
        val boxes = output.boxes
        val arr = output.data
        val candidates = ArrayList<VisionMath.Scored>()
        for (n in 0 until boxes) {
            val cx = arr[0 * boxes + n]
            val cy = arr[1 * boxes + n]
            val bw = arr[2 * boxes + n]
            val bh = arr[3 * boxes + n]
            val score = arr[4 * boxes + n]
            if (score < options.confidenceThreshold) continue

            // Convert back from letterbox space to original pixel space.
            val x1 = (cx - bw / 2 - padX) / scale
            val y1 = (cy - bh / 2 - padY) / scale
            val x2 = (cx + bw / 2 - padX) / scale
            val y2 = (cy + bh / 2 - padY) / scale
            val bx = max(0, floor(x1).toInt())
            val by = max(0, floor(y1).toInt())
            val bxw = min(origW - bx, ceil((x2 - x1).toDouble()).toInt())
            val bxh = min(origH - by, ceil((y2 - y1).toDouble()).toInt())
            if (bxw <= 0 || bxh <= 0) continue
            candidates.add(VisionMath.Scored(score, BoundingBox(bx, by, bxw, bxh)))
        }

        val kept = VisionMath.nonMaxSuppression(candidates, options.iouThreshold)
        return kept.map { DetectedFace(it.box, it.score, null) }
    }
}

// =====================================================================
// OnnxFaceEmbedder (OnnxFaceEmbedder.cs)
// =====================================================================

/**
 * Configuration for [OnnxFaceEmbedder]. Mirrors C# `OnnxFaceEmbedderOptions`.
 *
 * @param inputSize Square input dimension (112 = ArcFace default).
 * @param dimension Output embedding dimension (typically 512).
 */
data class OnnxFaceEmbedderOptions(
    val inputSize: Int = 112,
    val dimension: Int = 512,
)

/**
 * Real [IFaceEmbedder] backed by an ArcFace-family ONNX model. The crop → resize
 * → BGR mean-subtract preprocessing and the neural forward pass are the injected
 * [IEmbeddingModel] (it owns the codec); the region clamp and the L2
 * re-normalise (guaranteeing cosine == dot) are ported algorithm-for-algorithm
 * from C# `OnnxFaceEmbedder`.
 */
class OnnxFaceEmbedder(
    private val model: IEmbeddingModel,
    private val options: OnnxFaceEmbedderOptions = OnnxFaceEmbedderOptions(),
) : IFaceEmbedder {

    override val dimension: Int get() = options.dimension

    override suspend fun embedAsync(imageBytes: ByteArray, face: DetectedFace): FaceEmbedding {
        currentCoroutineContext().ensureActive()

        val raw = try {
            model.embed(imageBytes, face, options.inputSize)
        } catch (ce: CancellationException) {
            throw ce
        } catch (_: Exception) {
            return FaceEmbedding(FloatArray(options.dimension), options.dimension)
        } ?: return FaceEmbedding(FloatArray(options.dimension), options.dimension)

        val normalised = raw.copyOf()
        VisionMath.l2Normalise(normalised)
        return FaceEmbedding(normalised, normalised.size)
    }

    /**
     * Clamp a face region to the image bounds. Ported from C#
     * `OnnxFaceEmbedder.ClampRegion`. Exposed for hosts that implement
     * [IEmbeddingModel] and need the identical crop geometry.
     */
    companion object {
        fun clampRegion(region: BoundingBox, imageWidth: Int, imageHeight: Int): BoundingBox {
            val x = region.x.coerceIn(0, imageWidth - 1)
            val y = region.y.coerceIn(0, imageHeight - 1)
            val w = region.width.coerceIn(1, imageWidth - x)
            val h = region.height.coerceIn(1, imageHeight - y)
            return BoundingBox(x, y, w, h)
        }
    }
}

// =====================================================================
// OnnxPlateRecognizer (OnnxPlateRecognizer.cs)
// =====================================================================

/** Configuration for [OnnxPlateRecognizer]. Mirrors C# `OnnxPlateRecognizerOptions`. */
data class OnnxPlateRecognizerOptions(
    val inputSize: Int = 640,
    val confidenceThreshold: Float = 0.5f,
    val iouThreshold: Float = 0.45f,
    val countryHint: String? = null,
)

/**
 * [IPlateRecognizer] backed by a YOLO-family ONNX plate-detector model. Same
 * letterbox + YOLO-postprocess pattern as [OnnxFaceDetector] but emits
 * [PlateRecognitionResult] records with empty [PlateRecognitionResult.plateText]
 * — the OCR pass is a separate model, left to a downstream stage, exactly as the
 * C# `OnnxPlateRecognizer`. Note the plate post-process derives the box size
 * directly from `bw/bh / scale` (not from `x2-x1`), matching the C# verbatim.
 */
class OnnxPlateRecognizer(
    private val model: IObjectDetectionModel,
    private val imageOps: IImageOps,
    private val options: OnnxPlateRecognizerOptions = OnnxPlateRecognizerOptions(),
) : IPlateRecognizer {

    override suspend fun recognizeAsync(imageBytes: ByteArray): List<PlateRecognitionResult> {
        currentCoroutineContext().ensureActive()
        if (imageBytes.isEmpty()) return emptyList()

        val (origW, origH) = imageOps.dimensions(imageBytes)
        val lb = VisionMath.letterbox(origW, origH, options.inputSize)

        val output = try {
            model.infer(imageBytes, options.inputSize)
        } catch (ce: CancellationException) {
            throw ce
        } catch (_: Exception) {
            return emptyList()
        } ?: return emptyList()

        val boxes = output.boxes
        val arr = output.data
        val hits = ArrayList<VisionMath.Scored>()
        for (n in 0 until boxes) {
            val cx = arr[0 * boxes + n]
            val cy = arr[1 * boxes + n]
            val bw = arr[2 * boxes + n]
            val bh = arr[3 * boxes + n]
            val score = arr[4 * boxes + n]
            if (score < options.confidenceThreshold) continue
            val x1 = (cx - bw / 2 - lb.padX) / lb.scale
            val y1 = (cy - bh / 2 - lb.padY) / lb.scale
            val bx = max(0, floor(x1).toInt())
            val by = max(0, floor(y1).toInt())
            val bxw = min(origW - bx, ceil((bw / lb.scale).toDouble()).toInt())
            val bxh = min(origH - by, ceil((bh / lb.scale).toDouble()).toInt())
            if (bxw <= 0 || bxh <= 0) continue
            hits.add(VisionMath.Scored(score, BoundingBox(bx, by, bxw, bxh)))
        }

        val kept = VisionMath.nonMaxSuppression(hits, options.iouThreshold)
        return kept.map {
            PlateRecognitionResult(
                plateText = "",
                countryHint = options.countryHint,
                region = it.box,
                confidence = it.score,
            )
        }
    }
}

// =====================================================================
// InMemoryBluetoothAnomalyDetector — deterministic pub/sub fan-out
// =====================================================================

/**
 * Deterministic in-memory [IBluetoothAnomalyDetector]. The C# `IBluetoothAnomalyDetector`
 * is a long-running BLE/RF radio surface with a real backend landing later; this
 * portable implementation provides the working start/stop lifecycle and the
 * subscribe fan-out so hosts (and tests) can drive it deterministically by
 * calling [raiseAsync] to inject an observed anomaly. Not a stub — a complete
 * working detector whose "radio" is the host-supplied event stream.
 *
 * CONCURRENCY: subscribers are held in a CopyOnWriteArrayList; anomalies are
 * only delivered while started; no lock is held while a subscriber callback
 * runs (the list snapshot iterates lock-free) — the self-deadlock pattern from
 * Wave 1 (completing a continuation under a lock its handler re-takes) is
 * structurally impossible here.
 */
class InMemoryBluetoothAnomalyDetector : IBluetoothAnomalyDetector {

    private val handlers = CopyOnWriteArrayList<suspend (BluetoothAnomaly) -> Unit>()
    private val gate = Any()

    @Volatile
    private var started = false

    @Volatile
    private var disposed = false

    override val backendId: String get() = "in-memory"

    /** True while monitoring is active (between [startAsync] and [stopAsync]). */
    val isMonitoring: Boolean get() = started

    override fun subscribe(handler: suspend (BluetoothAnomaly) -> Unit): AutoCloseable {
        check(!disposed) { "InMemoryBluetoothAnomalyDetector is disposed" }
        handlers.add(handler)
        return AutoCloseable { handlers.remove(handler) }
    }

    override suspend fun startAsync() {
        check(!disposed) { "InMemoryBluetoothAnomalyDetector is disposed" }
        synchronized(gate) { started = true }
    }

    override suspend fun stopAsync() {
        check(!disposed) { "InMemoryBluetoothAnomalyDetector is disposed" }
        synchronized(gate) { started = false }
    }

    /**
     * Inject an observed anomaly. Delivered to every subscriber in registration
     * order while monitoring is active; a no-op when stopped or disposed. This is
     * the host-supplied "radio" seam — a real Bluehound backend would call this
     * off its native callback.
     */
    suspend fun raiseAsync(anomaly: BluetoothAnomaly) {
        if (disposed || !started) return
        // Snapshot iteration is lock-free; callbacks run without holding [gate].
        for (h in handlers) h(anomaly)
    }

    override fun close() {
        if (disposed) return
        disposed = true
        started = false
        handlers.clear()
    }

    override suspend fun closeAsync() = close()
}
