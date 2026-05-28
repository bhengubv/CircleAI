// tools/index.ts
// Tool definition/invocation/result types + FacialMetricMatrix.
// Ported from Circle.AI.Tools (C#).

// ─────────────────────────────────────────────────────────────────────────────
// Tool types
// ─────────────────────────────────────────────────────────────────────────────

/** Describes a single parameter accepted by a tool. */
export interface ToolParameter {
  /** JSON Schema primitive type: "string" | "number" | "boolean" | "object" | "array" */
  readonly type: string;
  readonly description: string;
  readonly enum?: string[];
}

/** Describes a tool the model can call. Compatible with OpenAI/Qwen function-call schema. */
export interface ToolDefinition {
  readonly name: string;
  readonly description: string;
  readonly parameters: Record<string, ToolParameter>;
  readonly requiredParameters: readonly string[];
}

/** A tool invocation emitted by the model. */
export interface ToolInvocation {
  readonly toolName: string;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  readonly arguments: Record<string, any>;
}

/** The result of executing a tool invocation. */
export interface ToolResult {
  readonly toolName: string;
  readonly success: boolean;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  readonly result?: any;
  readonly error?: string;
}

/** Convenience factory for a failed tool result. */
export function toolResultFailure(toolName: string, error: string): ToolResult {
  return { toolName, success: false, error };
}

/** Convenience factory for a successful tool result. */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function toolResultOk(toolName: string, result?: any): ToolResult {
  return { toolName, success: true, result };
}

// ─────────────────────────────────────────────────────────────────────────────
// FaceExpressionClassification enum
// ─────────────────────────────────────────────────────────────────────────────

/** Broad facial expression classification derived from landmark geometry. */
export enum FaceExpressionClassification {
  /** No strong expression signal detected. */
  NEUTRAL = "Neutral",
  /** Raised lip corners and cheek lift consistent with happiness. */
  HAPPY = "Happy",
  /** Raised brows and open mouth consistent with surprise. */
  SURPRISED = "Surprised",
  /** Furrowed brows and asymmetric lip geometry consistent with confusion. */
  CONFUSED = "Confused",
  /**
   * Tense jaw, narrowed eyes, and brow compression consistent with stress.
   * Treated more urgently than CONFUSED by the affect mapper.
   */
  STRESSED = "Stressed",
  /** Lowered brows, tightened lips consistent with anger or frustration. */
  ANGRY = "Angry",
  /**
   * Expression could not be determined — low confidence detection or
   * occluded face. Callers should treat this as a no-op signal.
   */
  UNKNOWN = "Unknown",
}

// ─────────────────────────────────────────────────────────────────────────────
// FaceBoundingBox
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Bounding box of a detected face in the source camera frame.
 * Each field is normalized to [0.0, 1.0] relative to frame dimensions.
 */
export interface FaceBoundingBox {
  readonly x: number;      // left edge fraction of frame width
  readonly y: number;      // top edge fraction of frame height
  readonly width: number;  // fraction of frame width
  readonly height: number; // fraction of frame height
}

// ─────────────────────────────────────────────────────────────────────────────
// FacialMetricMatrix
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The primary output type of the facex computer vision pipeline.
 * Contains 68 facial landmark points (stored as flat Float32Array[136]),
 * a face bounding box, a broad expression classification, and a detection
 * confidence score.
 *
 * Landmark coordinates: (x,y) pairs normalized to [0.0,1.0] relative to the
 * face bounding box. Use getLandmark(i) for safe indexed access.
 *
 * dlib 68-point index groups:
 *   0-16   jaw line
 *   17-21  right eyebrow
 *   22-26  left eyebrow
 *   27-30  nose bridge
 *   31-35  nose bottom
 *   36-41  right eye
 *   42-47  left eye
 *   48-59  outer lip
 *   60-67  inner lip
 */
export class FacialMetricMatrix {
  /**
   * 68 facial landmark points stored as interleaved (x, y) pairs.
   * Length is always 136. Each coordinate is normalized to [0.0, 1.0]
   * relative to the face boundingBox.
   */
  landmarks: Float32Array = new Float32Array(136);

  /** Bounding box of the detected face within the source frame. */
  boundingBox!: FaceBoundingBox;

  /** The dominant facial expression inferred from landmark geometry. */
  expression: FaceExpressionClassification = FaceExpressionClassification.UNKNOWN;

  /**
   * Detection confidence in [0.0, 1.0].
   * Detections below 0.5 should be treated as unreliable.
   * Detections below 0.3 should be discarded entirely.
   */
  confidenceScore: number = 0;

  /** UTC timestamp of the frame this matrix was extracted from. */
  capturedAt: Date = new Date();

  /**
   * Returns the normalized (x, y) coordinate for landmark index i (0-based, 0–67).
   * @throws {RangeError} if i is not in [0, 67].
   */
  getLandmark(i: number): [number, number] {
    if (i < 0 || i > 67) {
      throw new RangeError(`Landmark index must be in [0, 67], got ${i}`);
    }
    return [this.landmarks[i * 2], this.landmarks[i * 2 + 1]];
  }
}
