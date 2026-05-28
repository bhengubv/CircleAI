// tools.ts
//
// Tool definitions and bridge contract compatible with OpenAI/Qwen function-call schema.
// The IToolBridge routes tool calls to the appropriate API client.

// ---------------------------------------------------------------------------
// Tool definition types
// ---------------------------------------------------------------------------

/**
 * A single parameter descriptor for a ToolDefinition.
 * type is one of: "string" | "number" | "boolean" | "object" | "array"
 */
export interface ToolParameter {
  /** JSON Schema type: "string", "number", "boolean", "object", "array". */
  readonly type: string;
  readonly description: string;
  /** Allowed enum values (for string parameters). */
  readonly enum?: readonly string[];
}

/**
 * Describes a tool the model can call.
 * Compatible with OpenAI/Qwen function-call schema.
 */
export interface ToolDefinition {
  readonly name: string;
  readonly description: string;
  readonly parameters: Record<string, ToolParameter>;
  readonly requiredParameters: readonly string[];
}

// ---------------------------------------------------------------------------
// Invocation and result types
// ---------------------------------------------------------------------------

/** A tool call produced by the model. */
export interface ToolInvocation {
  readonly toolName:  string;
  readonly arguments: Record<string, unknown>;
}

/** The outcome of executing a ToolInvocation. */
export interface ToolResult {
  readonly toolName: string;
  readonly success:  boolean;
  readonly result:   unknown;
  readonly error:    string | null;
}

/** Convenience factory for a failed tool result. */
export function toolFailure(toolName: string, error: string): ToolResult {
  return { toolName, success: false, result: undefined, error };
}

/** Convenience factory for a successful tool result. */
export function toolSuccess(toolName: string, result?: unknown): ToolResult {
  return { toolName, success: true, result: result ?? null, error: null };
}

// ---------------------------------------------------------------------------
// IToolBridge
// ---------------------------------------------------------------------------

/**
 * Bridge between the local LLM and the TheGeekNetwork APIs.
 * Implementations route tool calls to the appropriate API client
 * (HTTP, in-process service, etc.).
 */
export abstract class IToolBridge {
  /** The synchronously-known list of available tools. */
  abstract readonly availableTools: readonly ToolDefinition[];

  /** Execute a single tool invocation. */
  abstract invoke(invocation: ToolInvocation): Promise<ToolResult>;

  /**
   * Returns the tools available through this bridge by querying the remote service.
   * The default implementation returns the synchronous availableTools list.
   */
  async getAvailableTools(): Promise<readonly ToolDefinition[]> {
    return this.availableTools;
  }
}

// ---------------------------------------------------------------------------
// FaceExpressionClassification
// ---------------------------------------------------------------------------

/** Broad facial expression classification derived from landmark geometry. */
export enum FaceExpressionClassification {
  /** No strong expression signal detected. */
  NEUTRAL   = 'Neutral',
  /** Raised lip corners and cheek lift consistent with happiness. */
  HAPPY     = 'Happy',
  /** Raised brows and open mouth consistent with surprise. */
  SURPRISED = 'Surprised',
  /** Furrowed brows and asymmetric lip geometry consistent with confusion. */
  CONFUSED  = 'Confused',
  /**
   * Tense jaw, narrowed eyes, and brow compression consistent with stress.
   * Treated more urgently than CONFUSED by the affect mapper.
   */
  STRESSED  = 'Stressed',
  /** Lowered brows, tightened lips consistent with anger or frustration. */
  ANGRY     = 'Angry',
  /**
   * Expression could not be determined — low confidence detection or
   * occluded face. Callers should treat this as a no-op signal.
   */
  UNKNOWN   = 'Unknown',
}

// ---------------------------------------------------------------------------
// FaceBoundingBox
// ---------------------------------------------------------------------------

/**
 * Bounding box of a detected face in the source camera frame.
 * Each field is normalized to [0.0, 1.0] relative to frame dimensions.
 */
export interface FaceBoundingBox {
  readonly x:      number; // left edge fraction of frame width
  readonly y:      number; // top edge fraction of frame height
  readonly width:  number; // fraction of frame width
  readonly height: number; // fraction of frame height
}

// ---------------------------------------------------------------------------
// FacialMetricMatrix
// ---------------------------------------------------------------------------

/**
 * The primary output type of the facex computer vision pipeline.
 * Contains 68 facial landmark points (stored as flat Float32Array[136]),
 * a face bounding box, a broad expression classification, and a detection
 * confidence score.
 *
 * Landmark coordinates: (x,y) pairs normalized to [0.0,1.0] relative to the
 * face bounding box. Use getLandmark(i) for safe indexed access.
 */
export class FacialMetricMatrix {
  /**
   * 68 facial landmark points stored as interleaved (x, y) pairs.
   * Length is always 136.
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
