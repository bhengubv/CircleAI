"use strict";
// tools.ts
//
// Tool definitions and bridge contract compatible with OpenAI/Qwen function-call schema.
// The IToolBridge routes tool calls to the appropriate API client.
Object.defineProperty(exports, "__esModule", { value: true });
exports.FacialMetricMatrix = exports.FaceExpressionClassification = exports.IToolBridge = void 0;
exports.toolFailure = toolFailure;
exports.toolSuccess = toolSuccess;
/** Convenience factory for a failed tool result. */
function toolFailure(toolName, error) {
    return { toolName, success: false, result: undefined, error };
}
/** Convenience factory for a successful tool result. */
function toolSuccess(toolName, result) {
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
class IToolBridge {
    /**
     * Returns the tools available through this bridge by querying the remote service.
     * The default implementation returns the synchronous availableTools list.
     */
    async getAvailableTools() {
        return this.availableTools;
    }
}
exports.IToolBridge = IToolBridge;
// ---------------------------------------------------------------------------
// FaceExpressionClassification
// ---------------------------------------------------------------------------
/** Broad facial expression classification derived from landmark geometry. */
var FaceExpressionClassification;
(function (FaceExpressionClassification) {
    /** No strong expression signal detected. */
    FaceExpressionClassification["NEUTRAL"] = "Neutral";
    /** Raised lip corners and cheek lift consistent with happiness. */
    FaceExpressionClassification["HAPPY"] = "Happy";
    /** Raised brows and open mouth consistent with surprise. */
    FaceExpressionClassification["SURPRISED"] = "Surprised";
    /** Furrowed brows and asymmetric lip geometry consistent with confusion. */
    FaceExpressionClassification["CONFUSED"] = "Confused";
    /**
     * Tense jaw, narrowed eyes, and brow compression consistent with stress.
     * Treated more urgently than CONFUSED by the affect mapper.
     */
    FaceExpressionClassification["STRESSED"] = "Stressed";
    /** Lowered brows, tightened lips consistent with anger or frustration. */
    FaceExpressionClassification["ANGRY"] = "Angry";
    /**
     * Expression could not be determined — low confidence detection or
     * occluded face. Callers should treat this as a no-op signal.
     */
    FaceExpressionClassification["UNKNOWN"] = "Unknown";
})(FaceExpressionClassification || (exports.FaceExpressionClassification = FaceExpressionClassification = {}));
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
class FacialMetricMatrix {
    /**
     * 68 facial landmark points stored as interleaved (x, y) pairs.
     * Length is always 136.
     */
    landmarks = new Float32Array(136);
    /** Bounding box of the detected face within the source frame. */
    boundingBox;
    /** The dominant facial expression inferred from landmark geometry. */
    expression = FaceExpressionClassification.UNKNOWN;
    /**
     * Detection confidence in [0.0, 1.0].
     * Detections below 0.5 should be treated as unreliable.
     * Detections below 0.3 should be discarded entirely.
     */
    confidenceScore = 0;
    /** UTC timestamp of the frame this matrix was extracted from. */
    capturedAt = new Date();
    /**
     * Returns the normalized (x, y) coordinate for landmark index i (0-based, 0–67).
     * @throws {RangeError} if i is not in [0, 67].
     */
    getLandmark(i) {
        if (i < 0 || i > 67) {
            throw new RangeError(`Landmark index must be in [0, 67], got ${i}`);
        }
        return [this.landmarks[i * 2], this.landmarks[i * 2 + 1]];
    }
}
exports.FacialMetricMatrix = FacialMetricMatrix;
