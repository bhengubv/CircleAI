"""Concierge router — port of CircleAI.Hosting.Neuron router + gate.

Per turn, decide whether the always-warm generalist answers or a
capability-matched specialist should. Cheap heuristics, no model inference.
"""
from __future__ import annotations

from dataclasses import dataclass
from enum import IntEnum
from typing import Callable, Optional, Protocol, runtime_checkable

from ...inference.inference import ChatCapability

__all__ = [
    "Organ",
    "RouteContext",
    "RouteDecision",
    "INeuronRouter",
    "NeuronGate",
    "HeuristicNeuronRouter",
]


class Organ(IntEnum):
    """Which organ answers a turn."""

    GENERALIST = 0
    SPECIALIST = 1


@dataclass(frozen=True)
class RouteContext:
    """Inputs the concierge classifies for a single turn."""

    query: str
    has_image: bool = False


@dataclass(frozen=True)
class RouteDecision:
    """The concierge's per-turn decision."""

    organ: Organ
    capability: ChatCapability
    reason: str

    @classmethod
    def generalist(cls, reason: str = "generalist") -> "RouteDecision":
        return cls(Organ.GENERALIST, ChatCapability.DEFAULT, reason)

    @classmethod
    def specialist(cls, capability: ChatCapability, reason: str) -> "RouteDecision":
        return cls(Organ.SPECIALIST, capability, reason)


@runtime_checkable
class INeuronRouter(Protocol):
    """The concierge's decision layer. Mirrors ``INeuronRouter``."""

    def route(self, context: RouteContext) -> RouteDecision: ...


class NeuronGate:
    """Guardrail checkpoint. An optional predicate can force a turn back to the
    generalist. ``None`` applies no veto — the honest default.
    """

    def __init__(self, allow_specialist: Optional[Callable[[str], bool]] = None) -> None:
        self._allow = allow_specialist

    def apply(self, decision: RouteDecision, context: RouteContext) -> RouteDecision:
        if (
            decision.organ == Organ.SPECIALIST
            and self._allow is not None
            and not self._allow(context.query)
        ):
            return RouteDecision.generalist("gate: specialist vetoed -> generalist")
        return decision


# Lowercase substrings that signal a turn wants an explicit reasoning model.
_REASONING_CUES = (
    "prove", "solve", "calculate", "derive", "algorithm", "complexity",
    "debug", "stack trace", "refactor", "regex", "step by step",
    "step-by-step", "theorem", "equation", "big-o", "big o",
)


class HeuristicNeuronRouter:
    """Default router: modality (image -> vision), length (long -> long-context),
    and reasoning cues (-> reasoning); everything else stays on the generalist.
    Mirrors ``HeuristicNeuronRouter``.
    """

    def __init__(
        self, gate: Optional[NeuronGate] = None, long_context_chars: int = 4000
    ) -> None:
        self._gate = gate or NeuronGate()
        self._long_context_chars = long_context_chars if long_context_chars > 0 else 4000

    def route(self, context: RouteContext) -> RouteDecision:
        if context is None:
            raise ValueError("context is required")
        return self._gate.apply(self._classify(context), context)

    def _classify(self, context: RouteContext) -> RouteDecision:
        # 1. An image attachment needs a vision model.
        if context.has_image:
            return RouteDecision.specialist(
                ChatCapability.VISION, "image attached -> vision specialist"
            )

        query = context.query or ""

        # 2. A very long prompt needs a long-context model.
        if len(query) >= self._long_context_chars:
            return RouteDecision.specialist(
                ChatCapability.LONG_CONTEXT,
                f"prompt length {len(query)} >= {self._long_context_chars} "
                f"-> long-context specialist",
            )

        # 3. Reasoning / coding cues want an explicit reasoning model.
        lower = query.lower()
        for cue in _REASONING_CUES:
            if cue in lower:
                return RouteDecision.specialist(
                    ChatCapability.REASONING, f"reasoning cue '{cue}' -> reasoning specialist"
                )

        # 4. Everything else: the always-warm generalist.
        return RouteDecision.generalist("no specialist cue -> generalist")
