# null_implementations.py
#
# Port of CircleAI.SDD NullImplementations.cs (C# — the EXACT spec).
#
# (2.7.0) Defaults — no-op store, always-invalid validator, empty scaffold. Each
# exposes a singleton `INSTANCE` mirroring the C# `static readonly ... Instance`.
# Empty-Guid ids -> str(uuid.UUID(int=0)).

from __future__ import annotations

import uuid
from typing import List, Optional

from .contracts import (
    ISpecToScaffold,
    ISpecificationStore,
    ISpecificationValidator,
    ScaffoldedProject,
    SpecValidationResult,
    Specification,
)

_EMPTY_GUID = str(uuid.UUID(int=0))


class NullSpecificationStore(ISpecificationStore):
    INSTANCE: "NullSpecificationStore"

    @property
    def backend_id(self) -> str:
        return "null"

    async def upsert_async(
        self, spec: Specification, ct: Optional[object] = None
    ) -> None:
        return None

    async def get_async(
        self, spec_id: str, ct: Optional[object] = None
    ) -> Optional[Specification]:
        return None

    async def list_async(
        self, ct: Optional[object] = None
    ) -> List[Specification]:
        return []


class NullSpecificationValidator(ISpecificationValidator):
    INSTANCE: "NullSpecificationValidator"

    @property
    def backend_id(self) -> str:
        return "null"

    async def validate_async(
        self, spec: Specification, ct: Optional[object] = None
    ) -> SpecValidationResult:
        return SpecValidationResult(False, ["No real validator wired."])


class NullSpecToScaffold(ISpecToScaffold):
    INSTANCE: "NullSpecToScaffold"

    @property
    def backend_id(self) -> str:
        return "null"

    async def scaffold_async(
        self, spec: Specification, target_language: str, ct: Optional[object] = None
    ) -> ScaffoldedProject:
        return ScaffoldedProject(_EMPTY_GUID, {})


NullSpecificationStore.INSTANCE = NullSpecificationStore()
NullSpecificationValidator.INSTANCE = NullSpecificationValidator()
NullSpecToScaffold.INSTANCE = NullSpecToScaffold()
