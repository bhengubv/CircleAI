"""circle_ai.model_alignment — port of the CircleAI.ModelAlignment assembly.

(2.6.0/3.3.0) Model-alignment surface (pattern-port of OBLITERATUS). Targeted
abliteration lives behind contracts so a host can apply / revert it deliberately
— and so we can refuse to publish abliterated weights (C# is the exact spec).

Public surface:

  * AlignmentProfile  — (profile_id, description, refusal_categories_removed,
                        created_at_utc, is_reversible).
  * AlignmentResult   — (profile_id, success, failure_reason).
  * IAlignmentToolkit — apply / revert / list alignment profiles.
  * IAlignmentAuditor — refuse to publish weights carrying alignment deltas.
  * InMemoryAlignmentToolkit    — real toolkit; refuses non-reversible profiles.
  * RefuseAlignedPublishAuditor — refuses publish when any profile is applied.
  * NullAlignmentToolkit / NullAlignmentAuditor — fail-closed defaults.
"""
from __future__ import annotations

from .contracts import (
    AlignmentProfile,
    AlignmentResult,
    IAlignmentAuditor,
    IAlignmentToolkit,
)
from .in_memory_model_alignment import (
    InMemoryAlignmentToolkit,
    RefuseAlignedPublishAuditor,
)
from .null_implementations import (
    NullAlignmentAuditor,
    NullAlignmentToolkit,
)

__all__ = [
    "AlignmentProfile",
    "AlignmentResult",
    "IAlignmentToolkit",
    "IAlignmentAuditor",
    "InMemoryAlignmentToolkit",
    "RefuseAlignedPublishAuditor",
    "NullAlignmentToolkit",
    "NullAlignmentAuditor",
]
