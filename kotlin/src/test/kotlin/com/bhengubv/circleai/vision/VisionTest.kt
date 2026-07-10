// VisionTest.kt
//
// Verifies the CircleAI.Vision Kotlin port against the C# reference: the null
// implementations' fail-closed / empty contracts, the deterministic vision math
// (letterbox, IoU, NMS, L2 normalise) ported from the ONNX detectors, the
// OnnxFaceDetector / OnnxFaceEmbedder / OnnxPlateRecognizer post-processing over
// injected model seams, the NullVideoCapture empty stream, and the in-memory
// Bluetooth anomaly fan-out lifecycle.

package com.bhengubv.circleai.vision

import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.time.Instant
import java.util.concurrent.atomic.AtomicInteger
import kotlin.math.abs
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class VisionTest {

    // ── Fakes for the injected native seams ────────────────────────────

    /** Reports fixed image dimensions. */
    private class FakeImageOps(val w: Int, val h: Int) : IImageOps {
        override fun dimensions(imageBytes: ByteArray): Pair<Int, Int> = w to h
    }

    /** Emits a preset YOLO output regardless of input. */
    private class FakeDetModel(private val out: YoloOutput?) : IObjectDetectionModel {
        override fun infer(imageBytes: ByteArray, inputSize: Int): YoloOutput? = out
    }

    /** Emits a preset raw embedding regardless of input. */
    private class FakeEmbModel(private val vec: FloatArray?) : IEmbeddingModel {
        override fun embed(imageBytes: ByteArray, face: DetectedFace, inputSize: Int): FloatArray? = vec
    }

    /**
     * Build a `[1, channels, boxes]`-flattened YOLO tensor from per-box
     * (cx, cy, w, h, score) rows, laid out `[channel * boxes + box]`.
     */
    private fun yolo(vararg rows: FloatArray): YoloOutput {
        val boxes = rows.size
        val channels = 5
        val data = FloatArray(channels * boxes)
        for (n in rows.indices) {
            val r = rows[n]
            for (c in 0 until channels) data[c * boxes + n] = r[c]
        }
        return YoloOutput(channels, boxes, data)
    }

    // ── Null implementations ───────────────────────────────────────────

    @Test
    fun `null face detector returns no faces`() = runBlocking {
        assertTrue(NullFaceDetector.Instance.detectAsync(ByteArray(4)).isEmpty())
    }

    @Test
    fun `null face embedder returns zero vector at dimension`() = runBlocking {
        val e = NullFaceEmbedder(256)
        assertEquals(256, e.dimension)
        val emb = e.embedAsync(ByteArray(0), DetectedFace(BoundingBox(0, 0, 1, 1), 1f))
        assertEquals(256, emb.dimension)
        assertEquals(256, emb.vector.size)
        assertTrue(emb.vector.all { it == 0f })
    }

    @Test
    fun `null liveness detector fails closed`() = runBlocking {
        val r = NullFaceLivenessDetector.Instance.checkAsync(ByteArray(0))
        assertFalse(r.isLive)
        assertEquals(0f, r.confidence)
        assertEquals("no liveness backend registered", r.failureReason)
    }

    @Test
    fun `null document verifier reports unverified with warning`() = runBlocking {
        val r = NullDocumentVerifier.Instance.verifyAsync(ByteArray(0))
        assertFalse(r.isValid)
        assertEquals("unknown", r.documentType)
        assertEquals("unknown", r.issuingCountry)
        assertTrue(r.fields.isEmpty())
        assertEquals(0f, r.overallConfidence)
        assertEquals(listOf("no document verifier backend registered"), r.warnings)
    }

    @Test
    fun `null plate recognizer returns no plates`() = runBlocking {
        assertTrue(NullPlateRecognizer.Instance.recognizeAsync(ByteArray(4)).isEmpty())
    }

    @Test
    fun `null computer vision runtime is a no-op`() = runBlocking {
        val rt = NullComputerVisionRuntime.Instance
        assertEquals("null", rt.backendId)
        assertNull(rt.decodeAsync(ByteArray(4)))
        assertNull(rt.resizeAsync(Any(), 10, 10))
    }

    @Test
    fun `null video capture yields nothing`() = runBlocking {
        val cap = NullVideoCapture()
        assertTrue(cap.captureAsync(1280, 720).toList().isEmpty())
        cap.closeAsync()
    }

    // ── Vision math ────────────────────────────────────────────────────

    @Test
    fun `letterbox centers a landscape image`() {
        // 200x100 into 640: scale = min(3.2, 6.4) = 3.2, newW=640, newH=320, padY=160.
        val lb = VisionMath.letterbox(200, 100, 640)
        assertEquals(3.2f, lb.scale)
        assertEquals(0, lb.padX)
        assertEquals(160, lb.padY)
    }

    @Test
    fun `iou of identical boxes is one and disjoint is zero`() {
        val a = BoundingBox(0, 0, 10, 10)
        assertEquals(1f, VisionMath.iou(a, a))
        assertEquals(0f, VisionMath.iou(a, BoundingBox(100, 100, 10, 10)))
    }

    @Test
    fun `iou half overlap`() {
        // a=[0,0,10,10], b=[5,0,10,10]: inter=5*10=50, union=100+100-50=150 => 1/3.
        val v = VisionMath.iou(BoundingBox(0, 0, 10, 10), BoundingBox(5, 0, 10, 10))
        assertTrue(abs(v - (1f / 3f)) < 1e-6f)
    }

    @Test
    fun `nms keeps the highest score and drops overlaps`() {
        val boxes = mutableListOf(
            VisionMath.Scored(0.6f, BoundingBox(0, 0, 10, 10)),
            VisionMath.Scored(0.9f, BoundingBox(1, 1, 10, 10)), // overlaps the first heavily
            VisionMath.Scored(0.8f, BoundingBox(100, 100, 10, 10)), // disjoint
        )
        val kept = VisionMath.nonMaxSuppression(boxes, 0.45f)
        // Highest (0.9) kept, its overlap (0.6) suppressed, disjoint (0.8) kept.
        assertEquals(2, kept.size)
        assertEquals(0.9f, kept[0].score)
        assertEquals(0.8f, kept[1].score)
    }

    @Test
    fun `l2 normalise yields unit length and leaves zero vector untouched`() {
        val v = floatArrayOf(3f, 4f)
        VisionMath.l2Normalise(v)
        assertTrue(abs(v[0] - 0.6f) < 1e-6f)
        assertTrue(abs(v[1] - 0.8f) < 1e-6f)

        val z = floatArrayOf(0f, 0f, 0f)
        VisionMath.l2Normalise(z)
        assertTrue(z.all { it == 0f })
    }

    // ── OnnxFaceDetector ───────────────────────────────────────────────

    @Test
    fun `face detector inverts letterbox and applies confidence gate`() = runBlocking {
        // Identity letterbox: 640x640 image -> scale 1, no pad.
        val det = OnnxFaceDetector(
            FakeDetModel(
                yolo(
                    floatArrayOf(100f, 100f, 40f, 40f, 0.9f), // box centered (100,100) 40x40
                    floatArrayOf(300f, 300f, 20f, 20f, 0.3f), // below default 0.5 threshold -> dropped
                ),
            ),
            FakeImageOps(640, 640),
        )
        val faces = det.detectAsync(ByteArray(8))
        assertEquals(1, faces.size)
        val f = faces[0]
        // x1 = 100 - 20 = 80, y1 = 80, w = 40, h = 40.
        assertEquals(BoundingBox(80, 80, 40, 40), f.region)
        assertEquals(0.9f, f.confidence)
        assertNull(f.landmarks)
    }

    @Test
    fun `face detector returns empty on empty bytes and on null inference`() = runBlocking {
        val det = OnnxFaceDetector(FakeDetModel(yolo(floatArrayOf(1f, 1f, 1f, 1f, 1f))), FakeImageOps(640, 640))
        assertTrue(det.detectAsync(ByteArray(0)).isEmpty())

        val detNull = OnnxFaceDetector(FakeDetModel(null), FakeImageOps(640, 640))
        assertTrue(detNull.detectAsync(ByteArray(8)).isEmpty())
    }

    @Test
    fun `face detector suppresses overlapping detections`() = runBlocking {
        val det = OnnxFaceDetector(
            FakeDetModel(
                yolo(
                    floatArrayOf(100f, 100f, 40f, 40f, 0.9f),
                    floatArrayOf(102f, 102f, 40f, 40f, 0.7f), // heavy overlap -> suppressed
                ),
            ),
            FakeImageOps(640, 640),
        )
        val faces = det.detectAsync(ByteArray(8))
        assertEquals(1, faces.size)
        assertEquals(0.9f, faces[0].confidence)
    }

    // ── OnnxFaceEmbedder ───────────────────────────────────────────────

    @Test
    fun `face embedder renormalises the raw model output`() = runBlocking {
        val emb = OnnxFaceEmbedder(
            FakeEmbModel(floatArrayOf(3f, 4f, 0f)),
            OnnxFaceEmbedderOptions(dimension = 3),
        )
        assertEquals(3, emb.dimension)
        val result = emb.embedAsync(ByteArray(8), DetectedFace(BoundingBox(0, 0, 10, 10), 1f))
        assertEquals(3, result.dimension)
        assertTrue(abs(result.vector[0] - 0.6f) < 1e-6f)
        assertTrue(abs(result.vector[1] - 0.8f) < 1e-6f)
        // Unit length overall.
        val len = kotlin.math.sqrt(result.vector.fold(0.0) { a, x -> a + x * x })
        assertTrue(abs(len - 1.0) < 1e-6)
    }

    @Test
    fun `face embedder returns zero vector when model yields null`() = runBlocking {
        val emb = OnnxFaceEmbedder(FakeEmbModel(null), OnnxFaceEmbedderOptions(dimension = 4))
        val result = emb.embedAsync(ByteArray(8), DetectedFace(BoundingBox(0, 0, 1, 1), 1f))
        assertEquals(4, result.dimension)
        assertTrue(result.vector.all { it == 0f })
    }

    @Test
    fun `clamp region keeps the crop inside the image`() {
        val clamped = OnnxFaceEmbedder.clampRegion(BoundingBox(-5, -5, 1000, 1000), 100, 80)
        assertEquals(0, clamped.x)
        assertEquals(0, clamped.y)
        assertEquals(100, clamped.width)
        assertEquals(80, clamped.height)
    }

    // ── OnnxPlateRecognizer ────────────────────────────────────────────

    @Test
    fun `plate recognizer emits boxes with empty text and the country hint`() = runBlocking {
        val rec = OnnxPlateRecognizer(
            FakeDetModel(yolo(floatArrayOf(200f, 150f, 60f, 20f, 0.8f))),
            FakeImageOps(640, 640),
            OnnxPlateRecognizerOptions(countryHint = "ZA"),
        )
        val plates = rec.recognizeAsync(ByteArray(8))
        assertEquals(1, plates.size)
        val p = plates[0]
        assertEquals("", p.plateText)
        assertEquals("ZA", p.countryHint)
        assertEquals(0.8f, p.confidence)
        // Plate box size derives from bw/bh directly (C# verbatim): x1=170, w=60, h=20.
        assertEquals(BoundingBox(170, 140, 60, 20), p.region)
    }

    @Test
    fun `plate recognizer returns empty on empty bytes`() = runBlocking {
        val rec = OnnxPlateRecognizer(FakeDetModel(yolo(floatArrayOf(1f, 1f, 1f, 1f, 1f))), FakeImageOps(640, 640))
        assertTrue(rec.recognizeAsync(ByteArray(0)).isEmpty())
    }

    // ── Bluetooth anomaly detector ─────────────────────────────────────

    @Test
    fun `null bluetooth detector never fires and reports null backend`() = runBlocking {
        val d = NullBluetoothAnomalyDetector()
        assertEquals("null", d.backendId)
        d.startAsync()
        val fired = AtomicInteger(0)
        d.subscribe { fired.incrementAndGet() }
        d.stopAsync()
        d.closeAsync()
        assertEquals(0, fired.get())
    }

    @Test
    fun `in-memory bluetooth detector fans out only while monitoring`() = runBlocking {
        val d = InMemoryBluetoothAnomalyDetector()
        assertEquals("in-memory", d.backendId)
        assertFalse(d.isMonitoring)

        val seen = ArrayList<BluetoothAnomaly>()
        val sub = d.subscribe { seen.add(it) }

        val anomaly = BluetoothAnomaly("radio", "spoof", 0.7f, "cloned beacon", Instant.now())

        // Not started yet -> dropped.
        d.raiseAsync(anomaly)
        assertTrue(seen.isEmpty())

        d.startAsync()
        assertTrue(d.isMonitoring)
        d.raiseAsync(anomaly)
        assertEquals(1, seen.size)
        assertEquals("spoof", seen[0].kind)

        // Unsubscribe -> no more deliveries.
        sub.close()
        d.raiseAsync(anomaly)
        assertEquals(1, seen.size)

        d.stopAsync()
        assertFalse(d.isMonitoring)
        d.closeAsync()
    }

    @Test
    fun `in-memory bluetooth detector delivers to multiple subscribers in order`() = runBlocking {
        val d = InMemoryBluetoothAnomalyDetector()
        val order = ArrayList<String>()
        d.subscribe { order.add("a") }
        d.subscribe { order.add("b") }
        d.startAsync()
        d.raiseAsync(BluetoothAnomaly("s", "k", 0.1f, "d", Instant.now()))
        assertEquals(listOf("a", "b"), order)
        d.closeAsync()
    }

    // ── Records ────────────────────────────────────────────────────────

    @Test
    fun `face embedding and video frame value equality respects array contents`() {
        val a = FaceEmbedding(floatArrayOf(1f, 2f), 2)
        val b = FaceEmbedding(floatArrayOf(1f, 2f), 2)
        assertEquals(a, b)
        assertEquals(a.hashCode(), b.hashCode())

        val t = Instant.parse("2026-07-10T00:00:00Z")
        val f1 = VideoFrame(byteArrayOf(1, 2, 3), 640, 480, VideoPixelFormat.Nv21, t)
        val f2 = VideoFrame(byteArrayOf(1, 2, 3), 640, 480, VideoPixelFormat.Nv21, t)
        assertEquals(f1, f2)
        assertNotNull(f1.copy(rotationDegrees = 90))
    }
}
