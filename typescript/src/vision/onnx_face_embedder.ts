// vision/onnx_face_embedder.ts
//
// (Phase C3) Real IFaceEmbedder backed by an ArcFace-family ONNX model
// (OnnxFaceEmbedder.cs). Input: 112x112 BGR float32 (typical ArcFace
// preprocessing). Output: 512-D vector, re-L2-normalised so cosine == dot.
//
// PLATFORM SEAMS: ONNX runtime via IOnnxSession / OnnxSessionFactory, image
// codec via ImageDecoder — the crop + BGR mean-subtraction + L2-normalise logic
// is ported one-to-one.

import type { IFaceEmbedder } from "./contracts.js";
import { floatTensor, type IOnnxSession, type OnnxSessionFactory } from "./onnx_backend.js";
import { cropAndResize, toTensorArcfaceBgr, type ImageDecoder } from "./image.js";
import { faceEmbedding, type BoundingBox, type DetectedFace, type FaceEmbedding } from "./primitives.js";

/**
 * (Phase C3) Options for {@link OnnxFaceEmbedder}. Mirrors C# `OnnxFaceEmbedderOptions`.
 * @param modelPath Path to an ArcFace-family ONNX model.
 * @param inputSize Square input dimension (112 = ArcFace default).
 * @param dimension Output embedding dimension (typically 512).
 */
export interface OnnxFaceEmbedderOptions {
  readonly modelPath: string;
  readonly inputSize: number;
  readonly dimension: number;
}

/** Builds {@link OnnxFaceEmbedderOptions} with the C# record defaults. */
export function onnxFaceEmbedderOptions(
  modelPath: string,
  overrides: Partial<Omit<OnnxFaceEmbedderOptions, "modelPath">> = {},
): OnnxFaceEmbedderOptions {
  if (isBlank(modelPath)) throw new Error("modelPath required");
  return {
    modelPath,
    inputSize: overrides.inputSize ?? 112,
    dimension: overrides.dimension ?? 512,
  };
}

export class OnnxFaceEmbedder implements IFaceEmbedder {
  private readonly opts: OnnxFaceEmbedderOptions;
  private readonly sessionFactory: OnnxSessionFactory;
  private readonly decode: ImageDecoder;
  private session: IOnnxSession | null = null;
  private disposed = false;

  constructor(opts: OnnxFaceEmbedderOptions, sessionFactory: OnnxSessionFactory, decode: ImageDecoder) {
    if (opts == null) throw new Error("opts required");
    if (sessionFactory == null) throw new Error("sessionFactory required");
    if (decode == null) throw new Error("decode required");
    this.opts = opts;
    this.sessionFactory = sessionFactory;
    this.decode = decode;
  }

  get dimension(): number {
    return this.opts.dimension;
  }

  async embedAsync(imageBytes: Uint8Array, face: DetectedFace, signal?: AbortSignal): Promise<FaceEmbedding> {
    if (this.disposed) throw new Error("OnnxFaceEmbedder is disposed");
    if (face == null) throw new Error("face required");
    if (signal?.aborted) throw abortError();

    const image = this.decode(imageBytes);
    const region = clampRegion(face.region, image.width, image.height);
    const crop = cropAndResize(image, region.x, region.y, region.width, region.height, this.opts.inputSize);

    const tensorData = toTensorArcfaceBgr(crop);
    const tensor = floatTensor(tensorData, [1, 3, this.opts.inputSize, this.opts.inputSize]);

    let raw: Float32Array;
    try {
      const session = this.ensureSession();
      const outputs = session.run({ [session.inputName]: tensor });
      raw = outputs[session.outputName].data.slice();
    } catch (ex) {
      logDebug(`[OnnxFaceEmbedder] inference failed: ${message(ex)}`);
      return faceEmbedding(new Float32Array(this.opts.dimension), this.opts.dimension);
    }
    l2Normalise(raw);
    return faceEmbedding(raw, raw.length);
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

/** Clamp a region into image bounds with a minimum 1px extent. Mirrors C# `ClampRegion`. */
function clampRegion(region: BoundingBox, imageWidth: number, imageHeight: number): BoundingBox {
  const x = clamp(region.x, 0, imageWidth - 1);
  const y = clamp(region.y, 0, imageHeight - 1);
  const w = clamp(region.width, 1, imageWidth - x);
  const h = clamp(region.height, 1, imageHeight - y);
  return { x, y, width: w, height: h };
}

function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v;
}

/** L2-normalise in place (double accumulate → float32 norm). Mirrors C# `L2Normalise`. */
function l2Normalise(v: Float32Array): void {
  let sumSq = 0;
  for (let i = 0; i < v.length; i++) sumSq += v[i] * v[i];
  const norm = Math.fround(Math.sqrt(sumSq));
  if (norm < 1e-9) return;
  for (let i = 0; i < v.length; i++) v[i] = Math.fround(v[i] / norm);
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
