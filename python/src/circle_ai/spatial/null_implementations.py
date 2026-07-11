# null_implementations.py
#
# Port of CircleAI.Spatial NullImplementations.cs (C# — the EXACT spec).
#
# (2.5.0) Fail-safe defaults for the Spatial pack. Each exposes a singleton
# `INSTANCE` mirroring the C# `static readonly ... Instance`. The empty-Guid
# scene id is "00000000-0000-0000-0000-000000000000" (str(uuid.UUID(int=0))).

from __future__ import annotations

import uuid
from datetime import datetime
from typing import List, Optional

from .contracts import (
    GeoTile,
    I3DSceneRenderer,
    IGeoTileSource,
    IRadarReadout,
    ISkyTracker,
    LatLon,
    RadarReading,
    Scene3D,
    SkyObject,
)

_EMPTY_GUID = str(uuid.UUID(int=0))


class NullGeoTileSource(IGeoTileSource):
    INSTANCE: "NullGeoTileSource"

    @property
    def backend_id(self) -> str:
        return "null"

    async def get_tile_async(
        self, z: int, x: int, y: int, ct: Optional[object] = None
    ) -> GeoTile:
        return GeoTile(z, x, y, b"", "image/png")

    async def search_places_async(
        self, query: str, top_k: int = 5, ct: Optional[object] = None
    ) -> List[LatLon]:
        return []


class NullRadarReadout(IRadarReadout):
    INSTANCE: "NullRadarReadout"

    @property
    def backend_id(self) -> str:
        return "null"

    async def get_current_reading_async(
        self, at: LatLon, range_km: float = 50, ct: Optional[object] = None
    ) -> RadarReading:
        return RadarReading(at, range_km, [])


class NullSkyTracker(ISkyTracker):
    INSTANCE: "NullSkyTracker"

    @property
    def backend_id(self) -> str:
        return "null"

    async def visible_async(
        self, at: LatLon, utc: datetime, ct: Optional[object] = None
    ) -> List[SkyObject]:
        return []


class Null3DSceneRenderer(I3DSceneRenderer):
    INSTANCE: "Null3DSceneRenderer"

    @property
    def backend_id(self) -> str:
        return "null"

    async def render_async(
        self, scene_script: str, format: str = "gltf", ct: Optional[object] = None
    ) -> Scene3D:
        return Scene3D(_EMPTY_GUID, b"", format)


NullGeoTileSource.INSTANCE = NullGeoTileSource()
NullRadarReadout.INSTANCE = NullRadarReadout()
NullSkyTracker.INSTANCE = NullSkyTracker()
Null3DSceneRenderer.INSTANCE = Null3DSceneRenderer()
