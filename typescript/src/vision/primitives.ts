// vision/primitives.ts
//
// (2.2.0) Shared shapes used across the vision contract surface (Primitives.cs).
// Only the shapes the ported ONNX components need are exposed as first-class
// TS interfaces; the full KYC/liveness/bluetooth surface is carried for parity
// with the C# record set.

/** An axis-aligned rectangle in image-pixel coordinates. Mirrors C# `BoundingBox`. */
export interface BoundingBox {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

/** Constructs a {@link BoundingBox}. */
export function boundingBox(x: number, y: number, width: number, height: number): BoundingBox {
  return { x, y, width, height };
}

/** A 2D point on a detected face — eye centre, mouth corner, etc. Mirrors C# `LandmarkPoint`. */
export interface LandmarkPoint {
  readonly x: number;
  readonly y: number;
}

/** One detected face with optional landmark fallback. Mirrors C# `DetectedFace`. */
export interface DetectedFace {
  readonly region: BoundingBox;
  readonly confidence: number;
  readonly landmarks: readonly LandmarkPoint[] | null;
}

/** Constructs a {@link DetectedFace}. */
export function detectedFace(
  region: BoundingBox,
  confidence: number,
  landmarks: readonly LandmarkPoint[] | null = null,
): DetectedFace {
  return { region, confidence, landmarks };
}

/**
 * A face embedding suitable for similarity search. `vector` is normalised so
 * cosine similarity reduces to a dot product. Mirrors C# `FaceEmbedding`.
 */
export interface FaceEmbedding {
  readonly vector: Float32Array;
  readonly dimension: number;
}

/** Constructs a {@link FaceEmbedding}. */
export function faceEmbedding(vector: Float32Array, dimension: number): FaceEmbedding {
  return { vector, dimension };
}

/** Outcome of liveness detection. Mirrors C# `LivenessResult`. */
export interface LivenessResult {
  readonly isLive: boolean;
  readonly confidence: number;
  readonly failureReason: string | null;
}

/** One parsed field from an ID document. Mirrors C# `DocumentField`. */
export interface DocumentField {
  readonly key: string;
  readonly value: string;
  readonly confidence: number;
}

/** Outcome of KYC document verification. Mirrors C# `DocumentVerificationResult`. */
export interface DocumentVerificationResult {
  readonly isValid: boolean;
  readonly documentType: string;
  readonly issuingCountry: string;
  readonly fields: readonly DocumentField[];
  readonly overallConfidence: number;
  readonly warnings: readonly string[] | null;
}

/** Outcome of license-plate recognition. Mirrors C# `PlateRecognitionResult`. */
export interface PlateRecognitionResult {
  readonly plateText: string;
  readonly countryHint: string | null;
  readonly region: BoundingBox;
  readonly confidence: number;
}

/** Constructs a {@link PlateRecognitionResult}. */
export function plateRecognitionResult(
  plateText: string,
  countryHint: string | null,
  region: BoundingBox,
  confidence: number,
): PlateRecognitionResult {
  return { plateText, countryHint, region, confidence };
}

/** One observed BLE / RF anomaly. Severity 0-1; higher = more concerning. Mirrors C# `BluetoothAnomaly`. */
export interface BluetoothAnomaly {
  readonly source: string;
  readonly kind: string;
  readonly severity: number;
  readonly description: string;
  readonly observedAtUtc: Date;
}
