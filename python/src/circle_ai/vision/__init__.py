"""circle_ai.vision — port of the CircleAI.Vision assembly (C# is the exact spec).

The detection-side computer-vision contract surface for B! Butler and AetherNet:
camera capture, a generic CV runtime, face detection / embedding / liveness, KYC
document verification, license-plate recognition and BLE/RF anomaly detection.
Deterministic null (fail-closed) implementations ship out of the box; the real
native backends (compv, facex, FaceLivenessDetection-SDK, KYC-Documents-Verif-SDK,
ultimateALPR-SDK, Bluehound, and the ONNX YOLO/ArcFace models) are injected behind
the ``I*ModelRunner`` seams — no real image codec or ONNX runtime is required to
exercise the ported algorithms.

Public surface:

  * Primitives (records):
      BoundingBox, LandmarkPoint, DetectedFace, FaceEmbedding, LivenessResult,
      DocumentField, DocumentVerificationResult, PlateRecognitionResult,
      BluetoothAnomaly.
  * Contracts + camera capture:
      VideoPixelFormat, VideoFrame, IVideoCapture, IComputerVisionRuntime,
      IFaceDetector, IFaceEmbedder, IFaceLivenessDetector, IDocumentVerifier,
      IPlateRecognizer, IBluetoothAnomalyDetector, BluetoothAnomalyHandler,
      IDisposable.
  * Null (fail-closed) implementations:
      NullVideoCapture, NullComputerVisionRuntime, NullFaceDetector,
      NullFaceEmbedder, NullFaceLivenessDetector, NullDocumentVerifier,
      NullPlateRecognizer, NullBluetoothAnomalyDetector.
  * ONNX-backed implementations + injected native seams:
      OnnxFaceDetector, OnnxFaceDetectorOptions, IFaceDetectorModelRunner,
      FaceDetectorModelOutput,
      OnnxFaceEmbedder, OnnxFaceEmbedderOptions, IFaceEmbedderModelRunner,
      OnnxPlateRecognizer, OnnxPlateRecognizerOptions, IPlateModelRunner,
      PlateModelOutput.
"""
from __future__ import annotations

from .contracts import (
    BluetoothAnomalyHandler,
    IBluetoothAnomalyDetector,
    IComputerVisionRuntime,
    IDisposable,
    IDocumentVerifier,
    IFaceDetector,
    IFaceEmbedder,
    IFaceLivenessDetector,
    IPlateRecognizer,
    IVideoCapture,
    VideoFrame,
    VideoPixelFormat,
)
from .null_implementations import (
    NullBluetoothAnomalyDetector,
    NullComputerVisionRuntime,
    NullDocumentVerifier,
    NullFaceDetector,
    NullFaceEmbedder,
    NullFaceLivenessDetector,
    NullPlateRecognizer,
    NullVideoCapture,
)
from .onnx_face_detector import (
    FaceDetectorModelOutput,
    IFaceDetectorModelRunner,
    OnnxFaceDetector,
    OnnxFaceDetectorOptions,
)
from .onnx_face_embedder import (
    IFaceEmbedderModelRunner,
    OnnxFaceEmbedder,
    OnnxFaceEmbedderOptions,
)
from .onnx_plate_recognizer import (
    IPlateModelRunner,
    OnnxPlateRecognizer,
    OnnxPlateRecognizerOptions,
    PlateModelOutput,
)
from .primitives import (
    BluetoothAnomaly,
    BoundingBox,
    DetectedFace,
    DocumentField,
    DocumentVerificationResult,
    FaceEmbedding,
    LandmarkPoint,
    LivenessResult,
    PlateRecognitionResult,
)

__all__ = [
    # primitives
    "BoundingBox",
    "LandmarkPoint",
    "DetectedFace",
    "FaceEmbedding",
    "LivenessResult",
    "DocumentField",
    "DocumentVerificationResult",
    "PlateRecognitionResult",
    "BluetoothAnomaly",
    # contracts + camera capture
    "VideoPixelFormat",
    "VideoFrame",
    "IVideoCapture",
    "IComputerVisionRuntime",
    "IFaceDetector",
    "IFaceEmbedder",
    "IFaceLivenessDetector",
    "IDocumentVerifier",
    "IPlateRecognizer",
    "IBluetoothAnomalyDetector",
    "BluetoothAnomalyHandler",
    "IDisposable",
    # null implementations
    "NullVideoCapture",
    "NullComputerVisionRuntime",
    "NullFaceDetector",
    "NullFaceEmbedder",
    "NullFaceLivenessDetector",
    "NullDocumentVerifier",
    "NullPlateRecognizer",
    "NullBluetoothAnomalyDetector",
    # ONNX implementations + seams
    "OnnxFaceDetector",
    "OnnxFaceDetectorOptions",
    "IFaceDetectorModelRunner",
    "FaceDetectorModelOutput",
    "OnnxFaceEmbedder",
    "OnnxFaceEmbedderOptions",
    "IFaceEmbedderModelRunner",
    "OnnxPlateRecognizer",
    "OnnxPlateRecognizerOptions",
    "IPlateModelRunner",
    "PlateModelOutput",
]
