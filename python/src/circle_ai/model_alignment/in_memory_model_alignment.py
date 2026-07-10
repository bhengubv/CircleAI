# in_memory_model_alignment.py
#
# Port of CircleAI.ModelAlignment InMemoryModelAlignment.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory alignment toolkit + auditor. ApplyAsync only allows
# reversible profiles (matches our "no permanent abliteration" licence stance);
# the auditor REFUSES to publish any model that has applied alignment profiles.
# Hosts that need different policy can swap auditors.
#
# The C# ConcurrentDictionary + object _lock maps to a plain dict guarded by a
# threading.Lock — every mutation/read here already runs under _lock, matching
# the C# critical sections. C# InvalidOperationException -> RuntimeError;
# ArgumentException -> ValueError.

from __future__ import annotations

import threading
from typing import Dict, List, Optional

from .contracts import (
    AlignmentProfile,
    AlignmentResult,
    IAlignmentAuditor,
    IAlignmentToolkit,
)


def _is_null_or_whitespace(s: Optional[str]) -> bool:
    return s is None or s.strip() == ""


class InMemoryAlignmentToolkit(IAlignmentToolkit):
    """In-memory alignment toolkit. Refuses non-reversible profiles."""

    def __init__(self) -> None:
        self._by_model: Dict[str, List[AlignmentProfile]] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def apply_async(
        self, model_id: str, profile: AlignmentProfile, ct: Optional[object] = None
    ) -> AlignmentResult:
        if _is_null_or_whitespace(model_id):
            raise ValueError("modelId required")
        if profile is None:
            raise ValueError("profile must not be None")
        if not profile.is_reversible:
            return AlignmentResult(
                profile.profile_id,
                False,
                "Non-reversible alignment refused by InMemoryAlignmentToolkit",
            )

        with self._lock:
            self._by_model.setdefault(model_id, []).append(profile)
        return AlignmentResult(profile.profile_id, True, None)

    async def revert_async(
        self, model_id: str, profile_id: str, ct: Optional[object] = None
    ) -> AlignmentResult:
        if _is_null_or_whitespace(model_id):
            raise ValueError("modelId required")
        if _is_null_or_whitespace(profile_id):
            raise ValueError("profileId required")
        with self._lock:
            lst = self._by_model.get(model_id)
            if lst is None:
                return AlignmentResult(profile_id, False, "Unknown model")
            before = len(lst)
            lst[:] = [p for p in lst if p.profile_id != profile_id]
            removed = before - len(lst)
            return (
                AlignmentResult(profile_id, True, None)
                if removed > 0
                else AlignmentResult(profile_id, False, "Profile not applied to this model")
            )

    async def list_applied_async(
        self, model_id: str, ct: Optional[object] = None
    ) -> List[AlignmentProfile]:
        if _is_null_or_whitespace(model_id):
            raise ValueError("modelId required")
        with self._lock:
            lst = self._by_model.get(model_id)
            if lst is None:
                return []
            return list(lst)


class RefuseAlignedPublishAuditor(IAlignmentAuditor):
    """(3.3.0) Refuses to publish weights that carry alignment deltas. Wired by
    default.
    """

    def __init__(self, toolkit: IAlignmentToolkit) -> None:
        if toolkit is None:
            raise ValueError("toolkit must not be None")
        self._toolkit = toolkit

    @property
    def backend_id(self) -> str:
        return "refuse-aligned"

    async def assert_ok_to_publish_async(
        self, model_id: str, ct: Optional[object] = None
    ) -> None:
        if _is_null_or_whitespace(model_id):
            raise ValueError("modelId required")
        applied = await self._toolkit.list_applied_async(model_id, ct)
        if len(applied) > 0:
            raise RuntimeError(
                f"Cannot publish '{model_id}': {len(applied)} alignment profile(s) applied — "
                f"this would distribute weights with safety modifications."
            )
