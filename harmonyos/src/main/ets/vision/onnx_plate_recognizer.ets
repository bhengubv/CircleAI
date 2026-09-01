/**
 * vision/onnx_plate_recognizer
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// vision/onnx_plate_recognizer.ts
//
// (Phase C3) IPlateRecognizer backed by an ONNX detector model
// (OnnxPlateRecognizer.cs). Follows the same letterbox + YOLO postprocess
// pattern as OnnxFaceDetector but emits PlateRecognitionResult records. OCR for
// the plate text itself is a separate model stage; this component returns the
// bounding box only and leaves text empty for a downstream OCR pass.
//
// PLATFORM SEAMS: ONNX runtime via IOnnxSession / OnnxSessionFactory, image
// codec via ImageDecoder — the letterbox/decode/NMS logic is ported one-to-one.

import type { IPlateRecognizer } from "./contracts";
import { floatTensor, type IOnnxSession, type OnnxSessionFactory } from "./onnx_backend";
import { letterboxResize, toTensorRgb01, type ImageDecoder } from "./image";
import { plateRecognitionResult, type PlateRecognitionResult } from "./primitives";
import { decodeYoloBoxes, nonMaxSuppression } from "./yolo";

/** (Phase C3) Options for {@link OnnxPlateRecognizer}. Mirrors C# `OnnxPlateRecognizerOptions`. */
export interface OnnxPlateRecognizerOptions {
  readonly modelPath: string;
  readonly inputSize: number;
  readonly confidenceThreshold: number;
  readonly iouThreshold: number;
  readonly countryHint: string | null;
}

/** Builds {@link OnnxPlateRecognizerOptions} with the C# record defaults. */
export function onnxPlateRecognizerOptions(
  modelPath: string,
  overrides: Partial<Omit<OnnxPlateRecognizerOptions, "modelPath">> = {},
): OnnxPlateRecognizerOptions {
  if (isBlank(modelPath)) throw new Error("modelPath required");
  return {
    modelPath,
    inputSize: overrides.inputSize ?? 640,
    confidenceThreshold: overrides.confidenceThreshold ?? Math.fround(0.5),
    iouThreshold: overrides.iouThreshold ?? Math.fround(0.45),
    countryHint: overrides.countryHint ?? null,
  };
}

export class OnnxPlateRecognizer implements IPlateRecognizer {
  private readonly opts: OnnxPlateRecognizerOptions;
  private readonly sessionFactory: OnnxSessionFactory;
  private readonly decode: ImageDecoder;
  private session: IOnnxSession | null = null;
  private disposed = false;

  constructor(opts: OnnxPlateRecognizerOptions, sessionFactory: OnnxSessionFactory, decode: ImageDecoder) {
    if (opts == null) throw new Error("opts required");
    if (sessionFactory == null) throw new Error("sessionFactory required");
    if (decode == null) throw new Error("decode required");
    this.opts = opts;
    this.sessionFactory = sessionFactory;
    this.decode = decode;
  }

  async recognizeAsync(imageBytes: Uint8Array, signal?: AbortSignal): Promise<readonly PlateRecognitionResult[]> {
    if (this.disposed) throw new Error("OnnxPlateRecognizer is disposed");
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
      logDebug(`[OnnxPlateRecognizer] inference failed: ${message(ex)}`);
      return [];
    }

    const dims = output.dims;
    if (dims.length !== 3) return [];
    const boxes = dims[2];
    const hits = decodeYoloBoxes(
      output.data,
      boxes,
      origW,
      origH,
      padX,
      padY,
      scale,
      this.opts.confidenceThreshold,
      /* expandCeil */ false,
    );
    const kept = nonMaxSuppression(hits, this.opts.iouThreshold);
    return kept.map((k) =>
      plateRecognitionResult(
        "", // OCR pass is a separate model — left to a follow-up
        this.opts.countryHint,
        k.box,
        k.score,
      ),
    );
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
