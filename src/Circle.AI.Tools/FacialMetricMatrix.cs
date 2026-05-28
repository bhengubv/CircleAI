// FacialMetricMatrix.cs
//
// Core cross-language output type of the facex computer vision pipeline.
// A single face detection: 68 dlib-convention landmark points, a normalized
// bounding box, an expression classification, and a confidence score.
//
// Landmark storage: flat float[136] (x0,y0, x1,y1, … x67,y67) for zero-copy
// interop with native facex buffers. Use GetLandmark(i) for index access.
//
// Coordinate convention: (x, y) pairs normalized to [0.0, 1.0] relative
// to the detected face bounding box, not the full frame.
//
// dlib 68-point index groups:
//   0-16   jaw line
//   17-21  right eyebrow
//   22-26  left eyebrow
//   27-30  nose bridge
//   31-35  nose bottom
//   36-41  right eye
//   42-47  left eye
//   48-59  outer lip
//   60-67  inner lip

using System;

namespace Circle.AI.Tools
{
    /// <summary>
    /// Bounding box of a detected face in the source camera frame,
    /// with each field normalized to [0.0, 1.0] relative to frame
    /// width and height respectively.
    /// </summary>
    public sealed record FaceBoundingBox(
        float X,       // left edge fraction of frame width
        float Y,       // top edge fraction of frame height
        float Width,   // fraction of frame width
        float Height); // fraction of frame height

    /// <summary>
    /// Broad facial expression classification derived from landmark geometry.
    /// Computed by the facex pipeline; callers should discard results where
    /// <see cref="FacialMetricMatrix.ConfidenceScore"/> is below 0.5.
    /// </summary>
    public enum FaceExpressionClassification
    {
        /// <summary>No strong expression signal detected.</summary>
        Neutral,

        /// <summary>Raised lip corners and cheek lift consistent with happiness.</summary>
        Happy,

        /// <summary>Raised brows and open mouth consistent with surprise.</summary>
        Surprised,

        /// <summary>Furrowed brows and asymmetric lip geometry consistent with confusion.</summary>
        Confused,

        /// <summary>
        /// Tense jaw, narrowed eyes, and brow compression consistent with stress.
        /// Treated more urgently than <see cref="Confused"/> by the affect mapper.
        /// </summary>
        Stressed,

        /// <summary>Lowered brows, tightened lips consistent with anger or frustration.</summary>
        Angry,

        /// <summary>
        /// Expression could not be determined — low confidence detection or
        /// occluded face. Callers should treat this as a no-op signal.
        /// </summary>
        Unknown,
    }

    /// <summary>
    /// The primary output type of the facex computer vision pipeline.
    /// Contains 68 facial landmark points, a face bounding box, a broad
    /// expression classification, and a detection confidence score.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Landmark coordinates are stored as a flat <c>float[136]</c> (x₀,y₀, x₁,y₁,
    /// …x₆₇,y₆₇) so the buffer can be passed to native facex routines without copying.
    /// Use <see cref="GetLandmark(int)"/> for safe indexed access.
    /// </para>
    /// <para>
    /// Delta computation (current frame minus previous frame) is the caller's
    /// responsibility — this type represents a single, stateless snapshot.
    /// </para>
    /// </remarks>
    public sealed class FacialMetricMatrix
    {
        // 68 points × 2 floats = 136 elements.
        private const int LandmarkFloats = 136;

        /// <summary>
        /// 68 facial landmark points stored as interleaved (x, y) pairs.
        /// Length is always 136. Each coordinate is normalized to [0.0, 1.0]
        /// relative to the face <see cref="BoundingBox"/>.
        /// </summary>
        public float[] Landmarks { get; init; } = new float[LandmarkFloats];

        /// <summary>
        /// Bounding box of the detected face within the source frame.
        /// All coordinates normalized to [0.0, 1.0] relative to frame dimensions.
        /// </summary>
        public required FaceBoundingBox BoundingBox { get; init; }

        /// <summary>
        /// The dominant facial expression inferred from landmark geometry.
        /// <see cref="FaceExpressionClassification.Unknown"/> when confidence is
        /// too low to classify reliably.
        /// </summary>
        public FaceExpressionClassification Expression { get; init; } =
            FaceExpressionClassification.Unknown;

        /// <summary>
        /// Detection confidence in [0.0, 1.0].
        /// Detections below 0.5 should be treated as unreliable.
        /// Detections below 0.3 should be discarded entirely.
        /// </summary>
        public float ConfidenceScore { get; init; }

        /// <summary>UTC timestamp of the frame this matrix was extracted from.</summary>
        public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Returns the normalized (x, y) coordinate for landmark index
        /// <paramref name="i"/> (0-based, 0–67).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="i"/> is not in [0, 67].
        /// </exception>
        public (float X, float Y) GetLandmark(int i)
        {
            if ((uint)i > 67u)
                throw new ArgumentOutOfRangeException(
                    nameof(i), i, "Landmark index must be in [0, 67].");

            return (Landmarks[i * 2], Landmarks[i * 2 + 1]);
        }
    }
}
