from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Optional


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass
class BiometricProfile:
    """A stored facial biometric profile for an identity.

    embedding_vector must be L2-normalised before storage.
    match_threshold is the minimum cosine similarity for a positive match.
    """

    identity_id: str
    embedding_vector: list[float]   # L2-normalised, NOT a hash
    match_threshold: float = 0.85   # cosine similarity threshold
    enrolled_at: datetime = field(default_factory=_utc_now)
    last_match_at: Optional[datetime] = None

    @property
    def embedding_dimension(self) -> int:
        """Dimensionality of the stored embedding vector."""
        return len(self.embedding_vector)
