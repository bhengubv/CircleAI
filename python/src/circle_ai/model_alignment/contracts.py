# contracts.py
#
# Port of CircleAI.ModelAlignment Contracts.cs (C# — the EXACT spec).
#
# (2.6.0) Model-alignment surface. Pattern-port of OBLITERATUS. Targeted
# abliteration lives behind contracts so a host can apply / revert it
# deliberately — and so we can refuse to publish abliterated weights.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class AlignmentProfile:
    """Mirrors ``CircleAI.ModelAlignment.AlignmentProfile`` —
    ``record(string ProfileId, string Description, IReadOnlyList<string>
    RefusalCategoriesRemoved, DateTimeOffset CreatedAtUtc, bool IsReversible)``.
    """

    profile_id: str
    description: str
    refusal_categories_removed: Sequence[str]
    created_at_utc: datetime
    is_reversible: bool


@dataclass(frozen=True, slots=True)
class AlignmentResult:
    """Mirrors ``CircleAI.ModelAlignment.AlignmentResult`` —
    ``record(string ProfileId, bool Success, string? FailureReason)``.
    """

    profile_id: str
    success: bool
    failure_reason: Optional[str]


class IAlignmentToolkit(ABC):
    """(2.6.0) Targeted abliteration toolkit. Apply / revert / list alignment
    profiles.
    """

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def apply_async(
        self, model_id: str, profile: AlignmentProfile, ct: Optional[object] = None
    ) -> AlignmentResult:
        ...

    @abstractmethod
    async def revert_async(
        self, model_id: str, profile_id: str, ct: Optional[object] = None
    ) -> AlignmentResult:
        ...

    @abstractmethod
    async def list_applied_async(
        self, model_id: str, ct: Optional[object] = None
    ) -> List[AlignmentProfile]:
        ...


class IAlignmentAuditor(ABC):
    """(2.6.0) Refuses to upload / publish weights that carry alignment deltas."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def assert_ok_to_publish_async(
        self, model_id: str, ct: Optional[object] = None
    ) -> None:
        """Raise / refuse if the model has applied alignment profiles and the
        action is "publish upstream".
        """
        ...
