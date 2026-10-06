// FaceCompanionBridge.cs
//
// Bridges a FacialMetricMatrix observation into the Companion layer:
//   1. Applies affect mutations via FaceAffectMapper
//   2. Checks post-mutation AffectState against confusion/stress thresholds
//   3. Returns a CompanionProactiveEvent when a threshold is crossed
//
// The platform host is responsible for acting on the returned event —
// slowing UI transitions, switching layouts, or having B! speak a message.
// This layer does not own the UI.

using System;
using CircleAI.Memory;
using CircleAI.Tools;

namespace CircleAI.Companion
{
    /// <summary>
    /// Bridges the facex vision pipeline into the Companion event model.
    /// Applies affect mutations from a face observation and surfaces a
    /// <see cref="CompanionProactiveEvent"/> when confusion or stress crosses
    /// a threshold. The platform host handles all UI responses.
    /// </summary>
    public static class FaceCompanionBridge
    {
        /// <summary>
        /// <see cref="AffectState.Uncertainty"/> level at or above which a
        /// proactive companion message is triggered, provided the observed
        /// expression is also <see cref="FaceExpressionClassification.Confused"/>
        /// or <see cref="FaceExpressionClassification.Stressed"/>.
        /// Default: 0.70.
        /// </summary>
        public const float ConfusionThreshold = 0.70f;

        /// <summary>
        /// Apply a face observation to the affect state and optionally surface
        /// a proactive companion event.
        /// </summary>
        /// <param name="matrix">
        /// The <see cref="FacialMetricMatrix"/> output for the current frame.
        /// </param>
        /// <param name="affect">
        /// The user's current <see cref="AffectState"/>. Mutated in place by
        /// <see cref="FaceAffectMapper.Apply"/>.
        /// </param>
        /// <param name="sessionId">Companion session identifier.</param>
        /// <param name="identityId">Circle identity identifier for the user.</param>
        /// <param name="surface">
        /// The current interface surface. Determines how the host should render
        /// the proactive message (speak aloud, push notification, inline card).
        /// </param>
        /// <returns>
        /// A <see cref="CompanionProactiveEvent"/> with trigger name
        /// <c>face.confusion_detected</c> when confusion or stress was observed
        /// above <see cref="ConfusionThreshold"/>; otherwise <c>null</c>.
        /// </returns>
        public static CompanionProactiveEvent? Observe(
            FacialMetricMatrix matrix,
            AffectState affect,
            string sessionId,
            string identityId,
            InterfaceKind surface)
        {
            ArgumentNullException.ThrowIfNull(matrix);
            ArgumentNullException.ThrowIfNull(affect);
            ArgumentNullException.ThrowIfNull(sessionId);
            ArgumentNullException.ThrowIfNull(identityId);

            // Step 1: mutate affect from observed expression.
            FaceAffectMapper.Apply(matrix, affect);

            // Step 2: check if the post-mutation state crosses the threshold.
            // Both conditions must hold — a high Uncertainty score alone (from
            // prior interactions) does not trigger a face-driven proactive event.
            bool thresholdCrossed =
                affect.Uncertainty >= ConfusionThreshold
                && matrix.Expression is FaceExpressionClassification.Confused
                                     or FaceExpressionClassification.Stressed;

            if (!thresholdCrossed) return null;

            // Step 3: surface the proactive event for the platform host to act on.
            return new CompanionProactiveEvent(
                SessionId  : sessionId,
                IdentityId : identityId,
                Interface  : surface,
                Message    :
                    "I notice you might be finding this a bit tricky. " +
                    "Would you like me to slow down or explain it differently?",
                TriggerName : "face.confusion_detected",
                GeneratedAt : DateTimeOffset.UtcNow);
        }
    }
}
