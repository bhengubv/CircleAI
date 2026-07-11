# contracts.py
#
# Port of CircleAI.SDD Contracts.cs (C# — the EXACT spec).
#
# (2.7.0) Spec-Driven Development contracts (spec-kit pattern): specification /
# validation-result / scaffolded-project records and the store / validator /
# scaffolder interfaces.
#
# C# ValueTask/ValueTask<T> -> async def -> None/T. C# records -> frozen slotted
# dataclasses. ReadOnlyMemory<byte> -> bytes.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import List, Mapping, Optional, Sequence


@dataclass(frozen=True, slots=True)
class Specification:
    """Mirrors ``CircleAI.SDD.Specification`` — ``record(string SpecId,
    string Title, string Body, string? Schema,
    IReadOnlyDictionary<string,string>? Metadata = null)``.
    """

    spec_id: str
    title: str
    body: str
    schema: Optional[str]
    metadata: Optional[Mapping[str, str]] = None


@dataclass(frozen=True, slots=True)
class SpecValidationResult:
    """Mirrors ``CircleAI.SDD.SpecValidationResult`` — ``record(bool IsValid,
    IReadOnlyList<string> Errors)``.
    """

    is_valid: bool
    errors: Sequence[str]


@dataclass(frozen=True, slots=True)
class ScaffoldedProject:
    """Mirrors ``CircleAI.SDD.ScaffoldedProject`` — ``record(string ProjectId,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files)``.
    """

    project_id: str
    files: Mapping[str, bytes]


class ISpecificationStore(ABC):
    """(2.7.0) Persistent specification store."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def upsert_async(
        self, spec: Specification, ct: Optional[object] = None
    ) -> None:
        ...

    @abstractmethod
    async def get_async(
        self, spec_id: str, ct: Optional[object] = None
    ) -> Optional[Specification]:
        ...

    @abstractmethod
    async def list_async(
        self, ct: Optional[object] = None
    ) -> List[Specification]:
        ...


class ISpecificationValidator(ABC):
    """(2.7.0) Validate a specification (e.g. against a JSON Schema)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def validate_async(
        self, spec: Specification, ct: Optional[object] = None
    ) -> SpecValidationResult:
        ...


class ISpecToScaffold(ABC):
    """(2.7.0) Codegen hook — turn a spec into a scaffolded project."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def scaffold_async(
        self, spec: Specification, target_language: str, ct: Optional[object] = None
    ) -> ScaffoldedProject:
        ...
