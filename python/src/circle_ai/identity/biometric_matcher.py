from __future__ import annotations

import math

from .biometric_profile import BiometricProfile


def cosine_similarity(a: list[float], b: list[float]) -> float:
    """Cosine similarity between two vectors.

    Uses a double-precision accumulator for cross-platform reproducibility.
    Must match C# BiometricMatcher exactly within 1e-5. No numpy — stdlib only.

    When both vectors are perfectly L2-normalised this reduces to their dot
    product.  For real-world embeddings that are only approximately normalised
    the full dot/|a||b| formula is used so the result is clamped to [-1, 1].
    """
    dot = sum(float(x) * float(y) for x, y in zip(a, b))
    mag_a = math.sqrt(sum(float(x) * float(x) for x in a))
    mag_b = math.sqrt(sum(float(y) * float(y) for y in b))
    if mag_a == 0.0 or mag_b == 0.0:
        return 0.0
    return float(dot / (mag_a * mag_b))


def is_match(live_embedding: list[float], profile: BiometricProfile) -> bool:
    """Return True if the live embedding matches the stored profile.

    Comparison is cosine_similarity >= profile.match_threshold (inclusive).
    """
    return cosine_similarity(live_embedding, profile.embedding_vector) >= profile.match_threshold
