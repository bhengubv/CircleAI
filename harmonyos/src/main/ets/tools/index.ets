// tools/index.ets — Tool definitions and FacialMetricMatrix for Circle AI
// ArkTS port: no `any`, Float32Array for landmark data, explicit types.

// ---------------------------------------------------------------------------
// Face expression classification
// ---------------------------------------------------------------------------

/**
 * Classification of the dominant facial expression detected in a frame.
 */
export enum FaceExpressionClassification {
  Neutral   = 0,
  Happy     = 1,
  Sad       = 2,
  Surprised = 3,
  Confused  = 4,
  Stressed  = 5,
  Angry     = 6,
  Unknown   = 7,
}

/**
 * Axis-aligned bounding box of the detected face, in pixel coordinates.
 */
export interface FaceBoundingBox {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * All outputs from one face analysis pass.
 * landmarks: 68 (x,y) pairs stored flat as Float32Array[136],
 * normalised to [0.0, 1.0] relative to the face bounding box.
 */
export interface FacialMetricMatrix {
  landmarks: Float32Array;             // length 136, 68 (x,y) pairs
  boundingBox: FaceBoundingBox;
  expression: FaceExpressionClassification;
  confidenceScore: number;             // 0.0..1.0
  capturedAt: number;                  // unix ms
}

/**
 * Returns the (x, y) coordinates of landmark point i (0-indexed, 0..67).
 */
export function getLandmark(m: FacialMetricMatrix, i: number): [number, number] {
  return [m.landmarks[i * 2], m.landmarks[i * 2 + 1]];
}

// ---------------------------------------------------------------------------
// Tool definition types
// ---------------------------------------------------------------------------

/**
 * A single parameter descriptor for a ToolDefinition.
 */
export interface ToolParameter {
  name: string;
  type: string;           // "string" | "number" | "boolean" | "object" | "array"
  description: string;
  required: boolean;
}

/**
 * Describes a tool the model can call.
 * Compatible with OpenAI/Qwen function-call schema.
 */
export interface ToolDefinition {
  name: string;
  description: string;
  parameters: ToolParameter[];
}

/**
 * A tool call produced by the model.
 */
export interface ToolInvocation {
  toolName: string;
  arguments: Record<string, Object>;
  invocationId: string;
}

/**
 * The outcome of executing a ToolInvocation.
 */
export interface ToolResult {
  invocationId: string;
  result: Object;
  isError: boolean;
  errorMessage?: string;
}
