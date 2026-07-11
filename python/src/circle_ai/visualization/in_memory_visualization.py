# in_memory_visualization.py
#
# Port of CircleAI.Visualization InMemoryVisualization.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory dashboard store + normalising API-doc builder + static
# site builder:
#   • InMemoryDashboardStore — thread-safe dict store.
#   • JsonApiDocBuilder — parse the OpenAPI JSON, extract info.title, derive a
#     kebab-case docId, re-serialise compact (WriteIndented=false) so downstream
#     sites get deterministic output.
#   • StaticSiteBuilder — render a multi-file static site from
#     {"pages":[{"path":"...","html":"..."}]}.

from __future__ import annotations

import json
import threading
import uuid
from typing import Dict, List, Optional

from .contracts import (
    ApiDoc,
    DashboardDefinition,
    GeneratedSite,
    IApiDocBuilder,
    IDashboardDefinitionStore,
    ISiteBuilder,
)


class InMemoryDashboardStore(IDashboardDefinitionStore):
    """Thread-safe in-memory :class:`IDashboardDefinitionStore`. Mirrors
    ``CircleAI.Visualization.InMemoryDashboardStore``."""

    def __init__(self) -> None:
        self._items: Dict[str, DashboardDefinition] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def upsert_async(
        self, d: DashboardDefinition, ct: Optional[object] = None
    ) -> None:
        if d is None:
            raise ValueError("d")
        if d.dashboard_id is None or d.dashboard_id.strip() == "":
            raise ValueError("DashboardId required")
        with self._lock:
            self._items[d.dashboard_id] = d

    async def get_async(
        self, id: str, ct: Optional[object] = None
    ) -> Optional[DashboardDefinition]:
        if id is None or id.strip() == "":
            raise ValueError("id required")
        with self._lock:
            return self._items.get(id)

    async def list_async(
        self, ct: Optional[object] = None
    ) -> List[DashboardDefinition]:
        with self._lock:
            return list(self._items.values())


class JsonApiDocBuilder(IApiDocBuilder):
    """Normalising :class:`IApiDocBuilder`. Mirrors
    ``CircleAI.Visualization.JsonApiDocBuilder``."""

    @property
    def backend_id(self) -> str:
        return "json-normaliser"

    async def build_async(
        self, open_api_spec: str, ct: Optional[object] = None
    ) -> ApiDoc:
        if open_api_spec is None or open_api_spec.strip() == "":
            raise ValueError("openApiSpec required")
        root = json.loads(open_api_spec)
        title = "API"
        if isinstance(root, dict):
            info = root.get("info")
            if isinstance(info, dict):
                t = info.get("title")
                title = t if isinstance(t, str) and t is not None else "API"
        doc_id = title.replace(" ", "-").lower()
        # WriteIndented=false — compact, key order preserved (json module keeps
        # insertion order, matching System.Text.Json's document order).
        canonical = json.dumps(root, separators=(",", ":"), ensure_ascii=False)
        return ApiDoc(doc_id, title, canonical)


class StaticSiteBuilder(ISiteBuilder):
    """Static-site :class:`ISiteBuilder`. Mirrors
    ``CircleAI.Visualization.StaticSiteBuilder``."""

    @property
    def backend_id(self) -> str:
        return "static"

    async def build_async(
        self, site_spec: str, ct: Optional[object] = None
    ) -> GeneratedSite:
        if site_spec is None or site_spec.strip() == "":
            raise ValueError("siteSpec required")
        root = json.loads(site_spec)
        files: Dict[str, bytes] = {}
        pages = root.get("pages") if isinstance(root, dict) else None
        if not isinstance(pages, list):
            raise ValueError("siteSpec must contain a pages[] array.")
        for page in pages:
            if not isinstance(page, dict):
                continue
            path = page.get("path")
            html = page.get("html")
            if not isinstance(path, str) or path.strip() == "" or not isinstance(html, str):
                continue
            files[path] = html.encode("utf-8")
        site_id = f"site-{uuid.uuid4().hex}"
        return GeneratedSite(site_id, files)
