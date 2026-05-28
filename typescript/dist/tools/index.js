"use strict";
// tools/index.ts
// Tool definition/invocation/result types + FacialMetricMatrix.
// Ported from Circle.AI.Tools (C#).
Object.defineProperty(exports, "__esModule", { value: true });
exports.FacialMetricMatrix = exports.FaceExpressionClassification = void 0;
exports.toolResultFailure = toolResultFailure;
exports.toolResultOk = toolResultOk;
/** Convenience factory for a failed tool result. */
function toolResultFailure(toolName, error) {
    return { toolName, success: false, error };
}
/** Convenience factory for a successful tool result. */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function toolResultOk(toolName, result) {
    return { toolName, success: true, result };
}
// ─────────────────────────────────────────────────────────────────────────────
// FaceExpressionClassification enum
// ─────────────────────────────────────────────────────────────────────────────
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
class FacialMetricMatrix {
    /**
     * 68 facial landmark points stored as interleaved (x, y) pairs.
     * Length is always 136. Each coordinate is normalized to [0.0, 1.0]
     * relative to the face boundingBox.
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
