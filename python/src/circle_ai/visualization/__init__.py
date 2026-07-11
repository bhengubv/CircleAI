"""circle_ai.visualization — port of the CircleAI.Visualization assembly.

(2.8.0 contracts / 3.3.0 in-memory) Visualization surface: dashboard
definitions, OpenAPI-doc normalisation, static-site generation — with real
in-memory backends and fail-safe null defaults. C# is the exact spec.
"""
from __future__ import annotations

from .contracts import (
    ApiDoc,
    DashboardDefinition,
    GeneratedSite,
    IApiDocBuilder,
    IDashboardDefinitionStore,
    ISiteBuilder,
)
from .in_memory_visualization import (
    InMemoryDashboardStore,
    JsonApiDocBuilder,
    StaticSiteBuilder,
)
from .null_implementations import (
    NullApiDocBuilder,
    NullDashboardDefinitionStore,
    NullSiteBuilder,
)

__all__ = [
    "DashboardDefinition",
    "ApiDoc",
    "GeneratedSite",
    "IDashboardDefinitionStore",
    "IApiDocBuilder",
    "ISiteBuilder",
    "InMemoryDashboardStore",
    "JsonApiDocBuilder",
    "StaticSiteBuilder",
    "NullDashboardDefinitionStore",
    "NullApiDocBuilder",
    "NullSiteBuilder",
]
