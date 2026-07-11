# contracts.py
#
# Port of CircleAI.Visualization Contracts.cs (C# — the EXACT spec).
#
# (2.8.0) Visualization contracts: dashboard-definition / api-doc / generated-site
# records and the definition-store / api-doc-builder / site-builder interfaces.
#
# C# ValueTask/ValueTask<T> -> async def -> None/T. C# records -> frozen slotted
# dataclasses. ReadOnlyMemory<byte> -> bytes.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import List, Mapping, Optional


@dataclass(frozen=True, slots=True)
class DashboardDefinition:
    """Mirrors ``CircleAI.Visualization.DashboardDefinition`` — ``record(string
    DashboardId, string Title, string JsonSpec)``.
    """

    dashboard_id: str
    title: str
    json_spec: str


@dataclass(frozen=True, slots=True)
class ApiDoc:
    """Mirrors ``CircleAI.Visualization.ApiDoc`` — ``record(string DocId,
    string Title, string OpenApiJson)``.
    """

    doc_id: str
    title: str
    open_api_json: str


@dataclass(frozen=True, slots=True)
class GeneratedSite:
    """Mirrors ``CircleAI.Visualization.GeneratedSite`` — ``record(string SiteId,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files)``.
    """

    site_id: str
    files: Mapping[str, bytes]


class IDashboardDefinitionStore(ABC):
    """(2.8.0) Dashboard-definition store."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def upsert_async(
        self, d: DashboardDefinition, ct: Optional[object] = None
    ) -> None:
        ...

    @abstractmethod
    async def get_async(
        self, id: str, ct: Optional[object] = None
    ) -> Optional[DashboardDefinition]:
        ...

    @abstractmethod
    async def list_async(
        self, ct: Optional[object] = None
    ) -> List[DashboardDefinition]:
        ...


class IApiDocBuilder(ABC):
    """(2.8.0) API-doc builder."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def build_async(
        self, open_api_spec: str, ct: Optional[object] = None
    ) -> ApiDoc:
        ...


class ISiteBuilder(ABC):
    """(2.8.0) Static-site builder."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def build_async(
        self, site_spec: str, ct: Optional[object] = None
    ) -> GeneratedSite:
        ...
