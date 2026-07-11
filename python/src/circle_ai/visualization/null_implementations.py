# null_implementations.py
#
# Port of CircleAI.Visualization NullImplementations.cs (C# — the EXACT spec).
#
# (2.8.0) Fail-safe defaults. Each exposes a singleton `INSTANCE` mirroring the
# C# `static readonly ... Instance`. Empty-Guid ids -> str(uuid.UUID(int=0)).

from __future__ import annotations

import uuid
from typing import List, Optional

from .contracts import (
    ApiDoc,
    DashboardDefinition,
    GeneratedSite,
    IApiDocBuilder,
    IDashboardDefinitionStore,
    ISiteBuilder,
)

_EMPTY_GUID = str(uuid.UUID(int=0))


class NullDashboardDefinitionStore(IDashboardDefinitionStore):
    INSTANCE: "NullDashboardDefinitionStore"

    @property
    def backend_id(self) -> str:
        return "null"

    async def upsert_async(
        self, d: DashboardDefinition, ct: Optional[object] = None
    ) -> None:
        return None

    async def get_async(
        self, id: str, ct: Optional[object] = None
    ) -> Optional[DashboardDefinition]:
        return None

    async def list_async(
        self, ct: Optional[object] = None
    ) -> List[DashboardDefinition]:
        return []


class NullApiDocBuilder(IApiDocBuilder):
    INSTANCE: "NullApiDocBuilder"

    @property
    def backend_id(self) -> str:
        return "null"

    async def build_async(
        self, open_api_spec: str, ct: Optional[object] = None
    ) -> ApiDoc:
        return ApiDoc(_EMPTY_GUID, "", "{}")


class NullSiteBuilder(ISiteBuilder):
    INSTANCE: "NullSiteBuilder"

    @property
    def backend_id(self) -> str:
        return "null"

    async def build_async(
        self, site_spec: str, ct: Optional[object] = None
    ) -> GeneratedSite:
        return GeneratedSite(_EMPTY_GUID, {})


NullDashboardDefinitionStore.INSTANCE = NullDashboardDefinitionStore()
NullApiDocBuilder.INSTANCE = NullApiDocBuilder()
NullSiteBuilder.INSTANCE = NullSiteBuilder()
