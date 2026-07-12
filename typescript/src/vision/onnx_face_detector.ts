// vision/onnx_face_detector.ts
//
// (Phase C3) Real IFaceDetector backed by an ONNX face detection model
// (OnnxFaceDetector.cs). Designed against YOLOv8-face / YOLOv5-face /
// RetinaFace family models — all share the same boxes+score(+landmarks) output
// shape. The model path + input dimensions are configurable so callers can plug
// in any trained model.
//
// PLATFORM SEAMS: the ONNX runtime is injected behind IOnnxSession /
// OnnxSessionFactory and the image codec behind ImageDecoder — the letterbox +
// NMS + tensor-build + YOLO decode logic is ported one-to-one and stays pure.

import type { IFaceDetector } from "./contracts.js";
import { floatTensor, type IOnnxSession, type OnnxSessionFactory } from "./onnx_backend.js";
import { letterboxResize, toTensorRgb01, type ImageDecoder } from "./image.js";
import { detectedFace, type DetectedFace } from "./primitives.js";
import { decodeYoloBoxes, nonMaxSuppression } from "./yolo.js";

/**
 * (Phase C3) Options for {@link OnnxFaceDetector}. Mirrors C# `OnnxFaceDetectorOptions`.
 * @param modelPath Path to a YOLO-family ONNX face-detection model.
 * @param inputSize Square input dimension (640 = YOLOv8 default).
 * @param confidenceThreshold Skip detections under this score (0..1).
 * @param iouThreshold NMS IoU cutoff (0..1).
 */
export interface OnnxFaceDetectorOptions {
  readonly modelPath: string;
  readonly inputSize: number;
  readonly confidenceThreshold: number;
  readonly iouThreshold: number;
}

/** Builds {@link OnnxFaceDetectorOptions} with the C# record defaults. */
export function onnxFaceDetectorOptions(
  modelPath: string,
  overrides: Partial<Omit<OnnxFaceDetectorOptions, "modelPath">> = {},
): OnnxFaceDetectorOptions {
  if (isBlank(modelPath)) throw new Error("modelPath required");
  return {
    modelPath,
    inputSize: overrides.inputSize ?? 640,
    confidenceThreshold: overrides.confidenceThreshold ?? Math.fround(0.5),
    iouThreshold: overrides.iouThreshold ?? Math.fround(0.45),
  };
}

export class OnnxFaceDetector implements IFaceDetector {
  private readonly opts: OnnxFaceDetectorOptions;
  private readonly sessionFactory: OnnxSessionFactory;
  private readonly decode: ImageDecoder;
  private session: IOnnxSession | null = null;
  private disposed = false;

  constructor(opts: OnnxFaceDetectorOptions, sessionFactory: OnnxSessionFactory, decode: ImageDecoder) {
    if (opts == null) throw new Error("opts required");
    if (sessionFactory == null) throw new Error("sessionFactory required");
    if (decode == null) throw new Error("decode required");
    this.opts = opts;
    this.sessionFactory = sessionFactory;
    this.decode = decode;
  }

  async detectAsync(imageBytes: Uint8Array, signal?: AbortSignal): Promise<readonly DetectedFace[]> {
    if (this.disposed) throw new Error("OnnxFaceDetector is disposed");
    if (signal?.aborted) throw abortError();
    if (imageBytes.length === 0) return [];

    const image = this.decode(imageBytes);
    const origW = image.width;
    const origH = image.height;

    const { canvas, padX, padY, scale } = letterboxResize(image, this.opts.inputSize);
    const tensorData = toTensorRgb01(canvas);
    const tensor = floatTensor(tensorData, [1, 3, this.opts.inputSize, this.opts.inputSize]);

    let output: { data: Float32Array; dims: readonly number[] };
    try {
      const session = this.ensureSession();
      const outputs = session.run({ [session.inputName]: tensor });
      output = outputs[session.outputName];
    } catch (ex) {
      logDebug(`[OnnxFaceDetector] inference failed: ${message(ex)}`);
      return [];
    }
    return this.postprocessYolo(output, origW, origH, padX, padY, scale);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.session?.dispose();
    this.session = null;
  }

  private ensureSession(): IOnnxSession {
    if (this.session !== null) return this.session;
    this.session = this.sessionFactory(this.opts.modelPath);
    return this.session;
  }

  /**
   * (Phase C3) YOLOv8 output layout: [1, 4+1+K, N] where K is class count. For
   * face models K = 1. We read the first 5 channels per box (cx, cy, w, h,
   * score) — enough to derive boxes. Mirrors C# `PostprocessYolo`.
   */
  private postprocessYolo(
    output: { data: Float32Array; dims: readonly number[] },
    origW: number,
    origH: number,
    padX: number,
    padY: number,
    scale: number,
  ): DetectedFace[] {
    const dims = output.dims;
    if (dims.length !== 3) return [];
    const boxes = dims[2];
    // output.data is laid out [batch, channel, box] flattened; index = c*boxes + n.
    const candidates = decodeYoloBoxes(
      output.data,
      boxes,
      origW,
      origH,
      padX,
      padY,
      scale,
      this.opts.confidenceThreshold,
      /* expandCeil */ true,
    );
    const kept = nonMaxSuppression(candidates, this.opts.iouThreshold);
    return kept.map((c) => detectedFace(c.box, c.score, null));
  }
}

function abortError(): Error {
  const e = new Error("The operation was aborted.");
  e.name = "AbortError";
  return e;
}

function message(ex: unknown): string {
  return ex instanceof Error ? ex.message : String(ex);
}

function logDebug(msg: string): void {
  if (typeof console !== "undefined" && console.debug) console.debug(msg);
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
