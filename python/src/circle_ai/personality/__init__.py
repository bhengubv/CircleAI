"""circle_ai.personality — port of the CircleAI.Personality assembly.

The user-DECLARED persona surface (distinct from the AI's learned
CircleAI.Memory.PersonaState):

  * Persona / FormalityRange / PrivacyLevel — the declared identity document.
  * IPersonaProvider — persist / export user-owned personas
    (JsonPersonaProvider on disk; InMemoryPersonaProvider for tests).
  * IPersonaConflictResolver — reconcile declared vs learned
    (DeclaredWinsResolver clamps learned formality into declared bounds;
     LearnedWinsResolver passes the declaration through).
  * PersonaPromptBuilder / build_system_hint — render a persona into a compact,
    prompt-injection-hardened system-prompt block.

C# is the exact spec.
"""
from __future__ import annotations

from .persona import FormalityRange, Persona, PrivacyLevel
from .persona_conflict_resolver import (
    DeclaredWinsResolver,
    IPersonaConflictResolver,
    LearnedWinsResolver,
)
from .persona_prompt_builder import PersonaPromptBuilder, build_system_hint
from .persona_provider import (
    InMemoryPersonaProvider,
    IPersonaProvider,
    JsonPersonaProvider,
    persona_from_json,
    persona_to_json,
)

__all__ = [
    "Persona",
    "FormalityRange",
    "PrivacyLevel",
    "IPersonaProvider",
    "JsonPersonaProvider",
    "InMemoryPersonaProvider",
    "persona_to_json",
    "persona_from_json",
    "IPersonaConflictResolver",
    "DeclaredWinsResolver",
    "LearnedWinsResolver",
    "PersonaPromptBuilder",
    "build_system_hint",
]
