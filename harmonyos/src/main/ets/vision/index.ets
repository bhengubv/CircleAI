/**
 * vision/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// vision/index.ts
//
// Barrel for the CircleAI.Vision port — the on-device vision stack's ported
// pieces: the contract surface + shared primitives, and the three ONNX-backed
// components (face detection, license-plate recognition, face embedding). C# is
// the exact spec.
//
// PLATFORM SEAMS. Two native dependencies are injected behind interfaces so the
// port is deterministic and needs no native library — matching the voice
// module's convention:
//   • ONNX Runtime (Microsoft.ML.OnnxRuntime) → IOnnxSession / OnnxSessionFactory
//     (onnx_backend.ts), used by all three components.
//   • Image codec (SixLabors.ImageSharp)      → ImageDecoder / Rgb24Image
//     (image.ts). Only bytes→RGB decode is injected; the letterbox-resize, crop,
//     tensor build, YOLO decode and NMS are ported one-to-one as pure TS.

// Shared primitives (records) used across the vision surface.
export {
  boundingBox,
  detectedFace,
  faceEmbedding,
  plateRecognitionResult,
} from "./primitives";
export type {
  BoundingBox,
  LandmarkPoint,
  DetectedFace,
  FaceEmbedding,
  LivenessResult,
  DocumentField,
  DocumentVerificationResult,
  PlateRecognitionResult,
  BluetoothAnomaly,
} from "./primitives";

// Contract interfaces.
export type {
  IComputerVisionRuntime,
  IFaceDetector,
  IFaceEmbedder,
  IFaceLivenessDetector,
  IDocumentVerifier,
  IPlateRecognizer,
} from "./contracts";

// ONNX-backend injection seam. NOTE: the seam types (`DenseTensor`,
// `floatTensor`, `IOnnxSession`, `OnnxSessionFactory`) intentionally mirror the
// voice module's ONNX seam by the same names. Voice is already `export *`'d at
// the package root, so re-exporting them here would collide under `export *`.
// They stay reachable via the `@bhengubv/circle-ai/.../vision` subpath; the
// three components consume them internally.

// Image codec seam + the pure pixel operations ported from the ImageSharp path.
export {
  rgb24Image,
  resizeNearest,
  letterboxResize,
  cropAndResize,
  toTensorRgb01,
  toTensorArcfaceBgr,
} from "./image";
export type { Rgb24Image, ImageDecoder, LetterboxResult } from "./image";

// Shared YOLO post-processing (IoU / NMS / box decode).
export { iou, nonMaxSuppression, decodeYoloBoxes } from "./yolo";
export type { ScoredBox } from "./yolo";

// The three ONNX-backed components.
export { OnnxFaceDetector, onnxFaceDetectorOptions } from "./onnx_face_detector";
export type { OnnxFaceDetectorOptions } from "./onnx_face_detector";
export { OnnxPlateRecognizer, onnxPlateRecognizerOptions } from "./onnx_plate_recognizer";
export type { OnnxPlateRecognizerOptions } from "./onnx_plate_recognizer";
export { OnnxFaceEmbedder, onnxFaceEmbedderOptions } from "./onnx_face_embedder";
export type { OnnxFaceEmbedderOptions } from "./onnx_face_embedder";
