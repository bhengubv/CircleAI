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


# ---------------------------------------------------------------------------
# Dimension mismatch — the case the shared fixture does not cover.
#
# facex_biometric_vectors.json has six entries and every one pairs equal-length
# vectors, which is precisely why this went unnoticed for so long: there was no
# row that could fail. The ports do not agree on what belongs here — C#,
# Kotlin, TypeScript and HarmonyOS raise; Go and Swift return 0.0 — so these
# assert the majority behaviour, which is also C#'s (the reference), rather
# than settle it in the shared fixture unilaterally.
# ---------------------------------------------------------------------------


def test_cosine_similarity_refuses_mismatched_dimensions() -> None:
    with pytest.raises(ValueError, match="Embedding dimension mismatch"):
        cosine_similarity([1.0], [1.0, 0.5])


def test_cosine_similarity_does_not_score_a_truncated_prefix() -> None:
    """The regression itself: zip() silently truncated to the shorter vector.

    [1.0] against [1.0, 0.5] scored 0.894 — past the 0.85 default threshold —
    by comparing a 1-element embedding against the first element of a
    2-element one. Both argument orders truncated, so both are asserted.
    """
    with pytest.raises(ValueError):
        cosine_similarity([1.0], [1.0, 0.5])
    with pytest.raises(ValueError):
        cosine_similarity([1.0, 0.5], [1.0])


def test_is_match_refuses_a_profile_of_another_dimension() -> None:
    """A wrong-sized embedding is a model mismatch, not a failed match."""
    profile = BiometricProfile(
        identity_id="test",
        embedding_vector=[1.0, 0.5],
        match_threshold=DEFAULT_THRESHOLD,
    )
    with pytest.raises(ValueError, match="Embedding dimension mismatch"):
        is_match([1.0], profile)


def test_cosine_similarity_zero_and_empty_vectors() -> None:
    assert cosine_similarity([], []) == 0.0
    assert cosine_similarity([0.0, 0.0], [1.0, 0.0]) == 0.0


def test_cosine_similarity_stays_within_bounds() -> None:
    """Accumulated rounding must not push a self-comparison past 1.0."""
    v = [0.1234567] * 512
    assert -1.0 <= cosine_similarity(v, v) <= 1.0
