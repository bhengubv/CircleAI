// Tools.kt
//
// Kotlin port of Circle.AI.Tools portable layer.
//
// Covers:
//   ToolParameter               — one parameter of a tool definition
//   ToolDefinition              — describes a tool the model can call
//   ToolInvocation              — a model-generated tool call request
//   ToolResult                  — outcome of executing a tool call
//   FaceExpressionClassification — face expression categories from facex sensor
//   FaceBoundingBox             — normalized bounding box for a detected face
//   FacialMetricMatrix          — full facial metric data from a face sensor frame
//   IToolBridge                 — bridge between the local LLM and the TGN APIs

package com.bhengubv.circleai.tools

import java.time.Instant

// ---------------------------------------------------------------------------
// ToolParameter
// ---------------------------------------------------------------------------

/**
 * Describes one parameter of a [ToolDefinition].
 * [type] is one of: "string", "number", "boolean", "object", "array".
 */
data class ToolParameter(
    /** JSON Schema primitive type: "string", "number", "boolean", "object", "array". */
    val type: String,
    val description: String,
    /** Optional enumeration of allowed values. */
    val enum: Array<String>? = null
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ToolParameter) return false
        return type == other.type &&
            description == other.description &&
            (enum == null && other.enum == null ||
             enum != null && other.enum != null && enum.contentEquals(other.enum))
    }

    override fun hashCode(): Int {
        var result = type.hashCode()
        result = 31 * result + description.hashCode()
        result = 31 * result + (enum?.contentHashCode() ?: 0)
        return result
    }
}

// ---------------------------------------------------------------------------
// ToolDefinition
// ---------------------------------------------------------------------------

/**
 * Describes a tool the model can call.
 * Compatible with OpenAI/Qwen function-call schema.
 */
data class ToolDefinition(
    val name: String,
    val description: String,
    val parameters: Map<String, ToolParameter>,
    val requiredParameters: List<String>
)

// ---------------------------------------------------------------------------
// ToolInvocation
// ---------------------------------------------------------------------------

/** A model-generated tool call request. */
data class ToolInvocation(
    val toolName: String,
    val arguments: Map<String, Any?>
)

// ---------------------------------------------------------------------------
// ToolResult
// ---------------------------------------------------------------------------

/** Outcome of executing a tool call. */
data class ToolResult(
    val toolName: String,
    val success: Boolean,
    val result: Any? = null,
    val error: String? = null
) {
    companion object {
        /** Convenience factory for a failed tool result. */
        fun failure(toolName: String, error: String): ToolResult =
            ToolResult(toolName = toolName, success = false, error = error)

        /** Convenience factory for a successful tool result. */
        fun ok(toolName: String, result: Any? = null): ToolResult =
            ToolResult(toolName = toolName, success = true, result = result)
    }
}

// ---------------------------------------------------------------------------
// FaceExpressionClassification
// ---------------------------------------------------------------------------

/**
 * Classification of a facial expression detected by the facex sensor.
 * Used by [FacialMetricMatrix] and [com.bhengubv.circleai.companion.FaceAffectMapper].
 */
enum class FaceExpressionClassification {
    /** No distinct expression detected. */
    Neutral,
    /** Smiling, positive affect. */
    Happy,
    /** Downcast, negative affect. */
    Sad,
    /** Eyes wide, mouth open — unexpected stimulus. */
    Surprised,
    /** Furrowed brow, head tilt — processing difficulty. */
    Confused,
    /** Tense expression, shallow breathing signs. */
    Stressed,
    /** Clenched jaw, narrowed eyes — frustration signal. */
    Angry,
    /** Detection succeeded but expression could not be classified. */
    Unknown,
}

// ---------------------------------------------------------------------------
// FaceBoundingBox
// ---------------------------------------------------------------------------

/**
 * Normalized bounding box for a detected face.
 * All coordinates are in [0.0, 1.0] relative to the frame dimensions.
 */
data class FaceBoundingBox(
    /** Normalized x coordinate of the top-left corner. */
    val x: Float,
    /** Normalized y coordinate of the top-left corner. */
    val y: Float,
    /** Normalized width of the bounding box. */
    val width: Float,
    /** Normalized height of the bounding box. */
    val height: Float
)

// ---------------------------------------------------------------------------
// FacialMetricMatrix
// ---------------------------------------------------------------------------

/**
 * Full facial metric data from a single face-sensor frame.
 *
 * [landmarks] is a flat FloatArray of 136 values representing 68 (x, y) landmark
 * pairs, each normalized to [0.0, 1.0] relative to the face bounding box.
 */
class FacialMetricMatrix(
    /** 68 landmark (x, y) pairs stored flat: [x0, y0, x1, y1, …, x67, y67]. */
    val landmarks: FloatArray,
    /** Bounding box of the detected face in the frame. */
    val boundingBox: FaceBoundingBox,
    /** Classified expression. */
    val expression: FaceExpressionClassification,
    /** Confidence of the expression classification, 0.0–1.0. */
    val confidenceScore: Float,
    /** UTC instant when this frame was captured. */
    val capturedAt: Instant = Instant.now()
) {
    init {
        require(landmarks.size == 136) {
            "landmarks must have exactly 136 elements (68 x,y pairs), got ${landmarks.size}"
        }
    }

    /**
     * Returns the [i]-th landmark as an (x, y) pair.
     * [i] must be in [0, 67].
     */
    fun getLandmark(i: Int): Pair<Float, Float> {
        require(i in 0..67) { "Landmark index must be in [0, 67], got $i" }
        return Pair(landmarks[i * 2], landmarks[i * 2 + 1])
    }
}

// ---------------------------------------------------------------------------
// IToolBridge
// ---------------------------------------------------------------------------

/**
 * Bridge between the local LLM and the TheGeekNetwork APIs.
 */
interface IToolBridge {
    /** The list of tools available through this bridge. */
    val availableTools: List<ToolDefinition>

    /** Executes the given [invocation] and returns its result. */
    suspend fun invokeAsync(invocation: ToolInvocation): ToolResult

    /**
     * Returns the tools available through this bridge by querying the remote service.
     * The default implementation returns the synchronous [availableTools] list.
     */
    suspend fun getAvailableToolsAsync(): List<ToolDefinition> = availableTools
}
