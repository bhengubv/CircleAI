/**
 * vision/contracts
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// vision/contracts.ts
//
// (2.2.0) The CircleAI.Vision contract surface (Contracts.cs). The ported ONNX
// components implement IFaceDetector / IFaceEmbedder / IPlateRecognizer; the
// remaining contracts are carried for parity with the C# interface set.
//
// Type mappings: ReadOnlyMemory<byte> → Uint8Array; IReadOnlyList<T> →
// readonly T[]; ValueTask → Promise.

import type {
  DetectedFace,
  DocumentVerificationResult,
  FaceEmbedding,
  LivenessResult,
  PlateRecognitionResult,
} from "./primitives";

/**
 * (2.2.0) Generic CV-runtime primitive. Consumers that need basic image
 * decoding / resize / colour-space ops dispatch through this surface. Mirrors
 * C# `IComputerVisionRuntime`.
 */
export interface IComputerVisionRuntime {
  /** Decode bytes into a backend-private opaque image. */
  decodeAsync(imageBytes: Uint8Array, signal?: AbortSignal): Promise<unknown>;
  /** Resize an opaque image. Returns a new opaque image. */
  resizeAsync(image: unknown, width: number, height: number, signal?: AbortSignal): Promise<unknown>;
  /** Backend self-identification — "compv-3.x", "null", etc. */
  readonly backendId: string;
}

/** (2.2.0) Find faces in an image. Mirrors C# `IFaceDetector`. */
export interface IFaceDetector {
  detectAsync(imageBytes: Uint8Array, signal?: AbortSignal): Promise<readonly DetectedFace[]>;
}

/** (2.2.0) Convert a detected face into a similarity-search vector. Mirrors C# `IFaceEmbedder`. */
export interface IFaceEmbedder {
  readonly dimension: number;
  embedAsync(imageBytes: Uint8Array, face: DetectedFace, signal?: AbortSignal): Promise<FaceEmbedding>;
}

/** (2.2.0) Decide whether the camera is looking at a real person. Mirrors C# `IFaceLivenessDetector`. */
export interface IFaceLivenessDetector {
  checkAsync(imageBytes: Uint8Array, signal?: AbortSignal): Promise<LivenessResult>;
}

/** (2.2.0) Parse + verify a KYC document image. Mirrors C# `IDocumentVerifier`. */
export interface IDocumentVerifier {
  verifyAsync(imageBytes: Uint8Array, signal?: AbortSignal): Promise<DocumentVerificationResult>;
}

/** (2.2.0) Read a license plate from an image. Mirrors C# `IPlateRecognizer`. */
export interface IPlateRecognizer {
  recognizeAsync(imageBytes: Uint8Array, signal?: AbortSignal): Promise<readonly PlateRecognitionResult[]>;
}
