"""test_anomaly_signal.py

Validates AnomalySignal.create() confidence-clamping against all clamp
vectors in fixtures/anomaly_signal_schema.json.
"""
from __future__ import annotations

import json
import pathlib
from datetime import datetime, timezone
from uuid import UUID

import pytest

from circle_ai.security import AnomalySignal, ThreatVector

FIXTURES_DIR = pathlib.Path(__file__).parent.parent.parent / "fixtures"
EPSILON = 1e-9


def _load_clamp_vectors() -> list[dict]:
    with open(FIXTURES_DIR / "anomaly_signal_schema.json", encoding="utf-8") as f:
        data = json.load(f)
    return data["clamp_vectors"]


VECTORS = _load_clamp_vectors()


@pytest.mark.parametrize("vector", VECTORS, ids=[v["id"] for v in VECTORS])
def test_confidence_clamp(vector: dict) -> None:
    """create() must clamp confidence to [0.0, 1.0] for all clamp vectors."""
    before_utc = datetime.now(timezone.utc)

    signal = AnomalySignal.create(
        vector=ThreatVector.MEMORY_ANOMALY,
        confidence=float(vector["input_confidence"]),
        affected_module="Circle.AI.Test",
        description=f"clamp vector {vector['id']}",
    )

    after_utc = datetime.now(timezone.utc)

    expected = float(vector["expected_confidence"])
    assert abs(signal.confidence - expected) <= EPSILON, (
        f"[{vector['id']}] confidence mismatch: "
        f"got {signal.confidence}, expected {expected}"
    )

    # Factory contract: stamps fresh UUID, current UTC time, and the
    # supplied vector + module + description survive intact.
    assert isinstance(signal.id, UUID)
    assert signal.id.version == 4
    assert signal.vector is ThreatVector.MEMORY_ANOMALY
    assert signal.affected_module == "Circle.AI.Test"
    assert signal.description == f"clamp vector {vector['id']}"
    assert signal.evidence == {}
    assert before_utc <= signal.detected_at <= after_utc
