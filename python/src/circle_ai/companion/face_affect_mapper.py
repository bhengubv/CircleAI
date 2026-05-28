from __future__ import annotations

from datetime import datetime, timezone

from ..memory.affect_state import AffectState
from ..tools.facial_metric_matrix import FaceExpressionClassification, FacialMetricMatrix


def apply(matrix: FacialMetricMatrix, affect: AffectState) -> None:
    """Map a FaceExpressionClassification to AffectState mutations.

    No-op when confidence_score < 0.5. All field updates are clamped to [0.0, 1.0].
    """
    if matrix.confidence_score < 0.5:
        return

    match matrix.expression:
        case FaceExpressionClassification.HAPPY:
            affect.engagement = min(1.0, affect.engagement + 0.03)
            affect.energy = min(1.0, affect.energy + 0.02)
        case FaceExpressionClassification.SURPRISED:
            affect.curiosity = min(1.0, affect.curiosity + 0.04)
        case FaceExpressionClassification.CONFUSED:
            affect.uncertainty = min(1.0, affect.uncertainty + 0.05)
        case FaceExpressionClassification.STRESSED:
            affect.uncertainty = min(1.0, affect.uncertainty + 0.08)
            affect.energy = max(0.0, affect.energy - 0.05)
        case FaceExpressionClassification.ANGRY:
            affect.engagement = max(0.0, affect.engagement - 0.04)
            affect.rapport = max(0.0, affect.rapport - 0.02)
        case _:
            # NEUTRAL, UNKNOWN — no mutations
            pass

    affect.last_updated_utc = datetime.now(timezone.utc)
