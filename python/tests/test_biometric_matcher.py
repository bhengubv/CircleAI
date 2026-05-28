"""test_biometric_matcher.py

Validates cosine_similarity() and is_match() against all vectors in
fixtures/facex_biometric_vectors.json.
"""
from __future__ import annotations

import json
import pathlib

import pytest

from circle_ai.identity.biometric_matcher import cosine_similarity, is_match
from circle_ai.identity.biometric_profile import BiometricProfile

FIXTURES_DIR = pathlib.Path(__file__).parent.parent.parent / "fixtures"


def _load_fixture() -> dict:
    with open(FIXTURES_DIR / "facex_biometric_vectors.json", encoding="utf-8") as f:
        return json.load(f)


FIXTURE = _load_fixture()
COSINE_VECTORS = FIXTURE["cosine_similarity_vectors"]
DEFAULT_THRESHOLD = float(FIXTURE["match_threshold_default"])


@pytest.mark.parametrize("entry", COSINE_VECTORS, ids=[e["id"] for e in COSINE_VECTORS])
def test_cosine_similarity(entry: dict) -> None:
    """Validate cosine_similarity output against the fixture's exact values.

    Every entry in fixtures/facex_biometric_vectors.json provides the
    mathematically-precise expected_similarity (verified against the C#
    reference implementation). All ports must agree within the stated
    tolerance — 1e-5 for unit vectors, 1e-4 for 4-element embeddings.
    """
    a = [float(v) for v in entry["a"]]
    b = [float(v) for v in entry["b"]]
    expected = float(entry["expected_similarity"])
    fixture_tolerance = float(entry.get("tolerance", 1e-5))

    result = cosine_similarity(a, b)

    assert abs(result - expected) <= fixture_tolerance, (
        f"[{entry['id']}] cosine_similarity mismatch: got {result}, "
        f"expected {expected} (tolerance {fixture_tolerance})"
    )


@pytest.mark.parametrize(
    "entry",
    [e for e in COSINE_VECTORS if "expected_is_match_at_threshold_0_85" in e],
    ids=[e["id"] for e in COSINE_VECTORS if "expected_is_match_at_threshold_0_85" in e],
)
def test_is_match(entry: dict) -> None:
    a = [float(v) for v in entry["a"]]
    b = [float(v) for v in entry["b"]]
    expected_match = bool(entry["expected_is_match_at_threshold_0_85"])

    profile = BiometricProfile(
        identity_id="test",
        embedding_vector=b,
        match_threshold=DEFAULT_THRESHOLD,
    )

    result = is_match(a, profile)
    assert result == expected_match, (
        f"[{entry['id']}] is_match mismatch: got {result}, expected {expected_match}"
    )
