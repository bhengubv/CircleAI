"""circle_ai.sdd — port of the CircleAI.SDD assembly.

(2.7.0 contracts / 3.3.0 in-memory) Spec-Driven Development surface (spec-kit
pattern): a specification store, a JSON-shape validator, a hello-world
spec-to-scaffold codegen hook (C# / TypeScript / Python), and fail-safe null
defaults. C# is the exact spec.
"""
from __future__ import annotations

from .contracts import (
    ISpecToScaffold,
    ISpecificationStore,
    ISpecificationValidator,
    ScaffoldedProject,
    SpecValidationResult,
    Specification,
)
from .in_memory_sdd import (
    HelloWorldSpecToScaffold,
    InMemorySpecificationStore,
    JsonShapeSpecificationValidator,
)
from .null_implementations import (
    NullSpecToScaffold,
    NullSpecificationStore,
    NullSpecificationValidator,
)

__all__ = [
    "Specification",
    "SpecValidationResult",
    "ScaffoldedProject",
    "ISpecificationStore",
    "ISpecificationValidator",
    "ISpecToScaffold",
    "InMemorySpecificationStore",
    "JsonShapeSpecificationValidator",
    "HelloWorldSpecToScaffold",
    "NullSpecificationStore",
    "NullSpecificationValidator",
    "NullSpecToScaffold",
]
