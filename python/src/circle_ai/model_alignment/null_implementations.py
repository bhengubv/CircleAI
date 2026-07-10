# null_implementations.py
#
# Port of CircleAI.ModelAlignment NullImplementations.cs (C# — the EXACT spec).
#
# (2.6.0) Fail-closed defaults — null toolkit refuses to apply anything; null
# auditor always asserts ok-to-publish (since nothing was applied). The C#
# `static readonly Instance` singletons map to module-level singletons exposed
# as class attributes.

from __future__ import annotations

from typing import List, Optional

from .contracts import (
    AlignmentProfile,
    AlignmentResult,
    IAlignmentAuditor,
    IAlignmentToolkit,
)


class NullAlignmentToolkit(IAlignmentToolkit):
    """Fail-closed alignment toolkit — refuses to apply or revert anything."""

    Instance: "NullAlignmentToolkit"

    @property
    def backend_id(self) -> str:
        return "null"

    async def apply_async(
        self, model_id: str, profile: AlignmentProfile, ct: Optional[object] = None
    ) -> AlignmentResult:
        return AlignmentResult(
            profile_id=profile.profile_id,
            success=False,
            failure_reason="NullAlignmentToolkit: no real backend wired.",
        )

    async def revert_async(
        self, model_id: str, profile_id: str, ct: Optional[object] = None
    ) -> AlignmentResult:
        return AlignmentResult(
            profile_id=profile_id,
            success=False,
            failure_reason="NullAlignmentToolkit: nothing to revert.",
        )

    async def list_applied_async(
        self, model_id: str, ct: Optional[object] = None
    ) -> List[AlignmentProfile]:
        return []


class NullAlignmentAuditor(IAlignmentAuditor):
    """No-op auditor — always asserts ok-to-publish (nothing was applied)."""

    Instance: "NullAlignmentAuditor"

    @property
    def backend_id(self) -> str:
        return "null"

    async def assert_ok_to_publish_async(
        self, model_id: str, ct: Optional[object] = None
    ) -> None:
        return None


# `static readonly Instance` singletons (see C# NullImplementations.cs).
NullAlignmentToolkit.Instance = NullAlignmentToolkit()
NullAlignmentAuditor.Instance = NullAlignmentAuditor()
