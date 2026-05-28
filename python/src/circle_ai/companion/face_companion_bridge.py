from __future__ import annotations

from datetime import datetime, timezone
from typing import Optional

from ..memory.affect_state import AffectState
from ..tools.facial_metric_matrix import FaceExpressionClassification, FacialMetricMatrix
from .companion_types import CompanionProactiveEvent, InterfaceKind
from . import face_affect_mapper

CONFUSION_THRESHOLD = 0.70

_PROACTIVE_EXPRESSIONS = {
    FaceExpressionClassification.CONFUSED,
    FaceExpressionClassification.STRESSED,
}


def observe(
    matrix: FacialMetricMatrix,
    affect: AffectState,
    session_id: str,
    identity_id: str,
    surface: InterfaceKind,
) -> Optional[CompanionProactiveEvent]:
    """Apply face expression to affect state and optionally emit a proactive event.

    Returns a CompanionProactiveEvent when:
      - affect.uncertainty >= CONFUSION_THRESHOLD (0.70) after applying the matrix, AND
      - the expression is CONFUSED or STRESSED.

    Returns None otherwise (including when confidence is too low).
    """
    face_affect_mapper.apply(matrix, affect)

    if (
        affect.uncertainty >= CONFUSION_THRESHOLD
        and matrix.expression in _PROACTIVE_EXPRESSIONS
        and matrix.confidence_score >= 0.5
    ):
        return CompanionProactiveEvent(
            session_id=session_id,
            identity_id=identity_id,
            interface=surface,
            message="It looks like you might be confused or stressed. Can I help clarify something?",
            trigger_name="face_confusion_detected",
            generated_at=datetime.now(timezone.utc),
        )

    return None
