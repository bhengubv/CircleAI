# accessibility_primitives.py
#
# Port of CircleAI.Accessibility AccessibilityPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Accessibility vertical:
# user accessibility profiles and derived adaptation hints. C#
# ConcurrentDictionary -> dict. HintsFor emits, in this exact order:
#   contrast=high (if HighContrast), motion=reduced (if ReducedMotion),
#   aria=verbose (if ScreenReader), text-scale=<F2> (if TextScale > 1),
#   then one need=<Name> per need (C# ``AccessibilityNeed.ToString()`` — the
#   PascalCase member name, e.g. "Visual").
# Returns an empty list when the user has no saved profile. The text-scale value
# is formatted like C# ``TextScale.ToString("F2")`` -> two decimal places.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from enum import IntEnum
from typing import Dict, List, Optional, Sequence


class AccessibilityNeed(IntEnum):
    """Mirrors ``CircleAI.Accessibility.AccessibilityNeed``. Stable ordinals.

    :attr:`cs_name` returns the C# ``ToString()`` (PascalCase) form, which is
    what the board embeds in ``need=<...>`` adaptation hints.
    """

    VISUAL = 0
    HEARING = 1
    MOTOR = 2
    COGNITIVE = 3
    SPEECH = 4

    @property
    def cs_name(self) -> str:
        return _NEED_CS_NAMES[self]


_NEED_CS_NAMES: Dict[AccessibilityNeed, str] = {
    AccessibilityNeed.VISUAL: "Visual",
    AccessibilityNeed.HEARING: "Hearing",
    AccessibilityNeed.MOTOR: "Motor",
    AccessibilityNeed.COGNITIVE: "Cognitive",
    AccessibilityNeed.SPEECH: "Speech",
}


@dataclass(frozen=True, slots=True)
class UserAccessibilityProfile:
    """Mirrors ``CircleAI.Accessibility.UserAccessibilityProfile``."""

    user_id: str
    needs: Sequence[AccessibilityNeed]
    text_scale: float
    high_contrast: bool
    reduced_motion: bool
    screen_reader: bool


@dataclass(frozen=True, slots=True)
class AdaptationHint:
    """Mirrors ``CircleAI.Accessibility.AdaptationHint`` — ``record(string Kind,
    string Value)``.
    """

    kind: str
    value: str


class IAccessibilityBoard(ABC):
    """In-memory board for accessibility profiles + derived adaptation hints."""

    @abstractmethod
    def set_profile(self, p: UserAccessibilityProfile) -> None:
        ...

    @abstractmethod
    def get_profile(self, user_id: str) -> Optional[UserAccessibilityProfile]:
        ...

    @abstractmethod
    def hints_for(self, user_id: str) -> List[AdaptationHint]:
        ...


class InMemoryAccessibilityBoard(IAccessibilityBoard):
    """Thread-safe in-memory :class:`IAccessibilityBoard`."""

    def __init__(self) -> None:
        self._profiles: Dict[str, UserAccessibilityProfile] = {}
        self._lock = threading.Lock()

    def set_profile(self, p: UserAccessibilityProfile) -> None:
        if p is None:
            raise ValueError("profile must not be None")
        with self._lock:
            self._profiles[p.user_id] = p

    def get_profile(self, user_id: str) -> Optional[UserAccessibilityProfile]:
        with self._lock:
            return self._profiles.get(user_id)

    def hints_for(self, user_id: str) -> List[AdaptationHint]:
        with self._lock:
            p = self._profiles.get(user_id)
            if p is None:
                return []
            hints: List[AdaptationHint] = []
            if p.high_contrast:
                hints.append(AdaptationHint("contrast", "high"))
            if p.reduced_motion:
                hints.append(AdaptationHint("motion", "reduced"))
            if p.screen_reader:
                hints.append(AdaptationHint("aria", "verbose"))
            if p.text_scale > 1:
                hints.append(AdaptationHint("text-scale", f"{p.text_scale:.2f}"))
            for n in p.needs:
                hints.append(AdaptationHint("need", AccessibilityNeed(n).cs_name))
            return hints
