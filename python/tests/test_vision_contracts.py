"""test_vision_contracts.py — CircleAI.Vision contract surface + implementations.

Covers the null (fail-closed) defaults, the camera-capture stream, the enum
ordinals + VideoFrame record, and the ONNX-backed face detector / face embedder /
plate recognizer over deterministic in-memory model runners (the load-bearing
YOLO box decode, letterbox back-projection, NMS, region clamp and L2 normalise are
all exercised). C# (CircleAI.Vision) is the reference.
"""
from __future__ import annotations

import math
from datetime import datetime, timezone

import pytest

from circle_ai.vision import (
    BoundingBox,
    DetectedFace,
    DocumentField,
    FaceDetectorModelOutput,
    FaceEmbedding,
    IFaceDetectorModelRunner,
    IFaceEmbedderModelRunner,
    IPlateModelRunner,
    LandmarkPoint,
    LivenessResult,
    NullBluetoothAnomalyDetector,
    NullComputerVisionRuntime,
    NullDocumentVerifier,
    NullFaceDetector,
    NullFaceEmbedder,
    NullFaceLivenessDetector,
    NullPlateRecognizer,
    NullVideoCapture,
    OnnxFaceDetector,
    OnnxFaceDetectorOptions,
    OnnxFaceEmbedder,
    OnnxFaceEmbedderOptions,
    OnnxPlateRecognizer,
    OnnxPlateRecognizerOptions,
    PlateModelOutput,
    VideoFrame,
    VideoPixelFormat,
)


# ── enum + records ──────────────────────────────────────────────────────────────


def test_video_pixel_format_ordinals_are_c_sharp_declaration_order():
    assert [m.value for m in VideoPixelFormat] == [0, 1, 2, 3, 4]
    assert VideoPixelFormat.YUV420 == 0
    assert VideoPixelFormat.JPEG == 4


def test_video_frame_defaults():
    now = datetime(2026, 7, 10, tzinfo=timezone.utc)
    f = VideoFrame(bytes=b"\x00\x01", width=64, height=48, pixel_format=VideoPixelFormat.RGBA32, captured_at_utc=now)
    assert f.rotation_degrees is None
    assert f.captured_at_utc == now
    # default captured_at is tz-aware UTC
    g = VideoFrame(b"", 1, 1, VideoPixelFormat.JPEG)
    assert g.captured_at_utc.tzinfo is timezone.utc


def test_detected_face_optional_landmarks_default_none():
    df = DetectedFace(BoundingBox(1, 2, 3, 4), 0.9)
    assert df.landmarks is None
    df2 = DetectedFace(BoundingBox(0, 0, 1, 1), 0.5, (LandmarkPoint(1, 1),))
    assert df2.landmarks == (LandmarkPoint(1, 1),)


# ── null implementations ─────────────────────────────────────────────────────────


async def test_null_video_capture_yields_nothing():
    cap = NullVideoCapture()
    frames = [f async for f in cap.capture_async(1280, 720)]
    assert frames == []
    await cap.dispose_async()
    # usable as an async context manager
    async with NullVideoCapture() as c:
        assert [x async for x in c.capture_async(640, 480)] == []


async def test_null_cv_runtime_returns_none():
    rt = NullComputerVisionRuntime.instance()
    assert rt.backend_id == "null"
    assert await rt.decode_async(b"\xff\xd8") is None
    assert await rt.resize_async(object(), 10, 10) is None


async def test_null_face_detector_returns_no_faces():
    d = NullFaceDetector.instance()
    assert await d.detect_async(b"\x00\x01") == ()


async def test_null_face_embedder_returns_zero_vector_at_dimension():
    e = NullFaceEmbedder(256)
    assert e.dimension == 256
    emb = await e.embed_async(b"\x00", DetectedFace(BoundingBox(0, 0, 1, 1), 1.0))
    assert emb.dimension == 256
    assert emb.vector == tuple(0.0 for _ in range(256))
    # default dimension is 512
    assert NullFaceEmbedder().dimension == 512


async def test_null_liveness_is_fail_closed():
    lv = NullFaceLivenessDetector.instance()
    res = await lv.check_async(b"\x00")
    assert res == LivenessResult(is_live=False, confidence=0.0, failure_reason="no liveness backend registered")


async def test_null_document_verifier_is_fail_closed():
    v = NullDocumentVerifier.instance()
    res = await v.verify_async(b"\x00")
    assert res.is_valid is False
    assert res.document_type == "unknown"
    assert res.issuing_country == "unknown"
    assert res.fields == ()
    assert res.overall_confidence == 0.0
    assert res.warnings == ("no document verifier backend registered",)


async def test_null_plate_recognizer_returns_no_plates():
    p = NullPlateRecognizer.instance()
    assert await p.recognize_async(b"\x00\x01") == ()


async def test_null_bluetooth_anomaly_detector_never_fires():
    fired = []

    async def handler(a):  # BluetoothAnomalyHandler
        fired.append(a)

    det = NullBluetoothAnomalyDetector()
    assert det.backend_id == "null"
    sub = det.subscribe(handler)
    await det.start_async()
    await det.stop_async()
    sub.dispose()
    await det.dispose_async()
    assert fired == []


# ── ONNX face detector over an in-memory runner ─────────────────────────────────


def _yolo_output(origw, origh, input_size, boxes):
    """Build a [1, 5, N] flat tensor from letterbox-space (cx,cy,w,h,score) boxes."""
    n = len(boxes)
    channels = 5
    data = [0.0] * (channels * n)
    for i, (cx, cy, w, h, score) in enumerate(boxes):
        data[0 * n + i] = cx
        data[1 * n + i] = cy
        data[2 * n + i] = w
        data[3 * n + i] = h
        data[4 * n + i] = score
    return FaceDetectorModelOutput(
        data=data, channels=channels, boxes=n, original_width=origw, original_height=origh
    )


def _as_plate_output(out):
    """Re-wrap a face-detector output tensor as a PlateModelOutput (same shape;
    PlateModelOutput is the aliased FaceDetectorModelOutput record)."""
    return PlateModelOutput(
        data=out.data,
        channels=out.channels,
        boxes=out.boxes,
        original_width=out.original_width,
        original_height=out.original_height,
    )


class _FixedFaceRunner(IFaceDetectorModelRunner):
    def __init__(self, output):
        self._output = output
        self.calls = 0

    def run(self, image_bytes, input_size):
        self.calls += 1
        return self._output


async def test_onnx_face_detector_empty_bytes_short_circuits():
    runner = _FixedFaceRunner(None)
    det = OnnxFaceDetector(OnnxFaceDetectorOptions(model_path="x.onnx"), runner)
    assert await det.detect_async(b"") == ()
    assert runner.calls == 0  # never touches the runner on empty input


async def test_onnx_face_detector_back_projects_and_thresholds():
    # 100x100 image, no letterbox padding (square) -> input_size 100, scale 1, pad 0.
    # One box centred at (50,50) size 20x20, score 0.9; one below threshold at 0.1.
    out = _yolo_output(
        100, 100, 100,
        [(50.0, 50.0, 20.0, 20.0, 0.9), (10.0, 10.0, 8.0, 8.0, 0.1)],
    )
    det = OnnxFaceDetector(
        OnnxFaceDetectorOptions(model_path="m.onnx", input_size=100, confidence_threshold=0.5),
        _FixedFaceRunner(out),
    )
    faces = await det.detect_async(b"\x89PNG")
    assert len(faces) == 1
    f = faces[0]
    assert f.confidence == pytest.approx(0.9)
    # x1 = 50 - 10 - 0 = 40 (scale 1); w = ceil((60)-(40)) = 20.
    assert f.region == BoundingBox(40, 40, 20, 20)
    assert f.landmarks is None


async def test_onnx_face_detector_letterbox_back_projection():
    # 200x100 image, input_size 100. scale = min(100/200, 100/100) = 0.5.
    # newW=100, newH=50, padX=0, padY=25. Box at letterbox (50,50) size 50x25.
    out = _yolo_output(200, 100, 100, [(50.0, 50.0, 50.0, 25.0, 0.8)])
    det = OnnxFaceDetector(
        OnnxFaceDetectorOptions(model_path="m.onnx", input_size=100, confidence_threshold=0.5),
        _FixedFaceRunner(out),
    )
    faces = await det.detect_async(b"img")
    assert len(faces) == 1
    # x1 = (50 - 25 - 0)/0.5 = 50; y1 = (50 - 12.5 - 25)/0.5 = 25; -> floor.
    r = faces[0].region
    assert r.x == 50
    assert r.y == 25
    # width = ceil(x2 - x1); x2=(50+25-0)/0.5=150 -> ceil(100)=100, clamped to 200-50=150 -> 100.
    assert r.width == 100
    assert r.height == 50


async def test_onnx_face_detector_nms_drops_overlap():
    # Two heavily-overlapping high-score boxes -> NMS keeps the top-scoring one.
    out = _yolo_output(
        100, 100, 100,
        [(50.0, 50.0, 40.0, 40.0, 0.9), (52.0, 52.0, 40.0, 40.0, 0.8)],
    )
    det = OnnxFaceDetector(
        OnnxFaceDetectorOptions(model_path="m.onnx", input_size=100, confidence_threshold=0.5, iou_threshold=0.45),
        _FixedFaceRunner(out),
    )
    faces = await det.detect_async(b"img")
    assert len(faces) == 1
    assert faces[0].confidence == pytest.approx(0.9)


async def test_onnx_face_detector_runner_exception_degrades_to_empty():
    class Boom(IFaceDetectorModelRunner):
        def run(self, image_bytes, input_size):
            raise RuntimeError("session blew up")

    det = OnnxFaceDetector(OnnxFaceDetectorOptions(model_path="m.onnx"), Boom())
    assert await det.detect_async(b"img") == ()


def test_onnx_face_detector_requires_model_file_when_asked():
    with pytest.raises(FileNotFoundError):
        OnnxFaceDetector(
            OnnxFaceDetectorOptions(model_path="does-not-exist.onnx"),
            _FixedFaceRunner(None),
            require_model_file=True,
        )


# ── ONNX face embedder over an in-memory runner ─────────────────────────────────


class _FixedEmbRunner(IFaceEmbedderModelRunner):
    def __init__(self, dims, raw):
        self._dims = dims
        self._raw = raw
        self.last_region = None

    def decode_dimensions(self, image_bytes):
        return self._dims

    def run(self, image_bytes, region, input_size):
        self.last_region = region
        return self._raw


async def test_onnx_face_embedder_l2_normalises_output():
    runner = _FixedEmbRunner((100, 100), [3.0, 4.0])  # norm 5 -> (0.6, 0.8)
    emb = OnnxFaceEmbedder(OnnxFaceEmbedderOptions(model_path="a.onnx", dimension=2), runner)
    assert emb.dimension == 2
    res = await emb.embed_async(b"img", DetectedFace(BoundingBox(10, 10, 20, 20), 0.9))
    assert res.dimension == 2
    assert res.vector[0] == pytest.approx(0.6)
    assert res.vector[1] == pytest.approx(0.8)
    # unit length
    assert math.sqrt(sum(v * v for v in res.vector)) == pytest.approx(1.0)


async def test_onnx_face_embedder_clamps_region_into_bounds():
    runner = _FixedEmbRunner((50, 40), [1.0])
    emb = OnnxFaceEmbedder(OnnxFaceEmbedderOptions(model_path="a.onnx", dimension=1), runner)
    # Region hangs off the right/bottom edge; clamp keeps it inside 50x40.
    await emb.embed_async(b"img", DetectedFace(BoundingBox(45, 38, 100, 100), 1.0))
    reg = runner.last_region
    assert reg.x == 45 and reg.y == 38
    assert reg.width == 50 - 45  # 5
    assert reg.height == 40 - 38  # 2


async def test_onnx_face_embedder_runner_none_returns_zero_vector():
    class NoneRunner(IFaceEmbedderModelRunner):
        def decode_dimensions(self, image_bytes):
            return (10, 10)

        def run(self, image_bytes, region, input_size):
            return None

    emb = OnnxFaceEmbedder(OnnxFaceEmbedderOptions(model_path="a.onnx", dimension=4), NoneRunner())
    res = await emb.embed_async(b"img", DetectedFace(BoundingBox(0, 0, 1, 1), 1.0))
    assert res.vector == (0.0, 0.0, 0.0, 0.0)


async def test_onnx_face_embedder_exception_returns_zero_vector():
    class Boom(IFaceEmbedderModelRunner):
        def decode_dimensions(self, image_bytes):
            raise RuntimeError("decode fail")

        def run(self, image_bytes, region, input_size):
            return [1.0]

    emb = OnnxFaceEmbedder(OnnxFaceEmbedderOptions(model_path="a.onnx", dimension=3), Boom())
    res = await emb.embed_async(b"img", DetectedFace(BoundingBox(0, 0, 1, 1), 1.0))
    assert res.vector == (0.0, 0.0, 0.0)


async def test_onnx_face_embedder_zero_norm_stays_zero():
    runner = _FixedEmbRunner((10, 10), [0.0, 0.0, 0.0])
    emb = OnnxFaceEmbedder(OnnxFaceEmbedderOptions(model_path="a.onnx", dimension=3), runner)
    res = await emb.embed_async(b"img", DetectedFace(BoundingBox(0, 0, 1, 1), 1.0))
    assert res.vector == (0.0, 0.0, 0.0)  # L2 no-op below 1e-9


# ── ONNX plate recognizer over an in-memory runner ──────────────────────────────


class _FixedPlateRunner(IPlateModelRunner):
    def __init__(self, output):
        self._output = output

    def run(self, image_bytes, input_size):
        return self._output


async def test_onnx_plate_recognizer_empty_bytes():
    rec = OnnxPlateRecognizer(OnnxPlateRecognizerOptions(model_path="p.onnx"), _FixedPlateRunner(None))
    assert await rec.recognize_async(b"") == ()


async def test_onnx_plate_recognizer_back_projects_width_from_bw_over_scale():
    # 100x100 square, input_size 100 -> scale 1, pad 0. Box (50,50) 30x10, score .7.
    out = _yolo_output(100, 100, 100, [(50.0, 50.0, 30.0, 10.0, 0.7)])
    rec = OnnxPlateRecognizer(
        OnnxPlateRecognizerOptions(model_path="p.onnx", input_size=100, confidence_threshold=0.5, country_hint="ZA"),
        _FixedPlateRunner(_as_plate_output(out)),
    )
    plates = await rec.recognize_async(b"img")
    assert len(plates) == 1
    p = plates[0]
    assert p.plate_text == ""  # OCR is a separate stage
    assert p.country_hint == "ZA"
    assert p.confidence == pytest.approx(0.7)
    # x1 = 50 - 15 = 35; width = ceil(bw/scale) = ceil(30) = 30.
    assert p.region == BoundingBox(35, 45, 30, 10)


async def test_onnx_plate_recognizer_thresholds_and_nms():
    out = _yolo_output(
        100, 100, 100,
        [(50.0, 50.0, 40.0, 20.0, 0.9), (51.0, 50.0, 40.0, 20.0, 0.85), (5.0, 5.0, 4.0, 4.0, 0.2)],
    )
    rec = OnnxPlateRecognizer(
        OnnxPlateRecognizerOptions(model_path="p.onnx", input_size=100, confidence_threshold=0.5, iou_threshold=0.45),
        _FixedPlateRunner(_as_plate_output(out)),
    )
    plates = await rec.recognize_async(b"img")
    assert len(plates) == 1  # low-score dropped by threshold, overlap dropped by NMS
    assert plates[0].confidence == pytest.approx(0.9)


async def test_onnx_plate_recognizer_runner_exception_degrades_to_empty():
    class Boom(IPlateModelRunner):
        def run(self, image_bytes, input_size):
            raise RuntimeError("boom")

    rec = OnnxPlateRecognizer(OnnxPlateRecognizerOptions(model_path="p.onnx"), Boom())
    assert await rec.recognize_async(b"img") == ()
