# persona_conflict_resolver.py
#
# Port of CircleAI.Personality IPersonaConflictResolver.cs (C# — the EXACT spec).
#
# Bridges the declared Persona (this package) and the learned PersonaState
# (CircleAI.Memory). Decides which wins when they disagree.
#
#   • IPersonaConflictResolver — the contract.
#   • DeclaredWinsResolver — declared bounds are hard limits; learned formality is
#     clamped into the declared FormalityRange (privacy-respecting default).
#   • LearnedWinsResolver — passes the declared persona through (identity / taboos
#     / values stay intact; formality/locale applied separately by the caller).
#
# C# ``declared with { Formality = range }`` -> dataclasses.replace. Resolvers are
# deterministic and never mutate either input.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import replace

from ..memory.persona_state import PersonaState
from .persona import FormalityRange, Persona


class IPersonaConflictResolver(ABC):
    """Reconciles a declared :class:`Persona` with a learned
    :class:`PersonaState`. Mirrors ``CircleAI.Personality.IPersonaConflictResolver``."""

    @abstractmethod
    def resolve(self, declared: Persona, learned: PersonaState) -> Persona:
        ...


class DeclaredWinsResolver(IPersonaConflictResolver):
    """Declared-wins resolver. Mirrors
    ``CircleAI.Personality.DeclaredWinsResolver``."""

    def resolve(self, declared: Persona, learned: PersonaState) -> Persona:
        if declared is None:
            raise ValueError("declared")
        if learned is None:
            raise ValueError("learned")

        clamped = self._clamp_formality(learned.formality, declared.formality)
        if clamped == learned.formality:
            # Learned was within bounds — no adjustment to surface.
            return declared

        # Learned drifted outside declared bounds — surface the clamped value by
        # replacing the floor or ceiling so future projections respect it.
        if clamped == "casual":
            rng = FormalityRange("casual", declared.formality.ceiling)
        elif clamped == "formal":
            rng = FormalityRange(declared.formality.floor, "formal")
        else:
            rng = declared.formality

        return replace(declared, formality=rng)

    @staticmethod
    def _clamp_formality(learned: str, rng: FormalityRange) -> str:
        learned_rank = DeclaredWinsResolver._rank(learned)
        floor_rank = DeclaredWinsResolver._rank(rng.floor)
        ceiling_rank = DeclaredWinsResolver._rank(rng.ceiling)
        # If declared range is inverted, treat declared as fixed at floor.
        if floor_rank > ceiling_rank:
            return rng.floor
        if learned_rank < floor_rank:
            return rng.floor
        if learned_rank > ceiling_rank:
            return rng.ceiling
        return learned

    @staticmethod
    def _rank(formality: str) -> int:
        if formality == "casual":
            return 0
        if formality == "neutral":
            return 1
        if formality == "formal":
            return 2
        return 1  # unknown values rank as neutral


class LearnedWinsResolver(IPersonaConflictResolver):
    """Learned-wins resolver. Mirrors
    ``CircleAI.Personality.LearnedWinsResolver``."""

    def resolve(self, declared: Persona, learned: PersonaState) -> Persona:
        if declared is None:
            raise ValueError("declared")
        if learned is None:
            raise ValueError("learned")
        # Pass through — identity, taboos, values stay intact; the caller applies
        # the learned formality/locale/verbosity separately.
        return declared
