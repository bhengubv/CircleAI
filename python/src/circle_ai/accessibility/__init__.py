"""circle_ai.accessibility — port of the CircleAI.Accessibility assembly.

(3.3.0) Real domain types + in-memory board for the Accessibility vertical: user
accessibility profiles and derived adaptation hints — plus the static domain
context and the AccessibilityNeed enum. C# is the exact spec.

The C# ``AccessibilityCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .accessibility_domain_context import AccessibilityDomainContext
from .accessibility_primitives import (
    AccessibilityNeed,
    AdaptationHint,
    IAccessibilityBoard,
    InMemoryAccessibilityBoard,
    UserAccessibilityProfile,
)

__all__ = [
    "AccessibilityNeed",
    "UserAccessibilityProfile",
    "AdaptationHint",
    "IAccessibilityBoard",
    "InMemoryAccessibilityBoard",
    "AccessibilityDomainContext",
]
