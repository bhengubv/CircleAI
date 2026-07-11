"""circle_ai.spatial — port of the CircleAI.Spatial assembly.

(2.5.0 contracts / 3.3.0 in-memory) Spatial / geo surface: map tiles + place
search, radar readout, visible-sky tracking, 3D-scene rendering — with
deterministic synthetic backends and fail-safe null defaults. C# is the exact
spec.
"""
from __future__ import annotations

from .contracts import (
    GeoTile,
    I3DSceneRenderer,
    IGeoTileSource,
    IRadarReadout,
    ISkyTracker,
    LatLon,
    RadarReading,
    RadarReturn,
    Scene3D,
    SkyObject,
)
from .in_memory_spatial import (
    InMemoryGeoTileSource,
    JsonScene3DRenderer,
    SyntheticRadarReadout,
    SyntheticSkyTracker,
)
from .null_implementations import (
    Null3DSceneRenderer,
    NullGeoTileSource,
    NullRadarReadout,
    NullSkyTracker,
)

__all__ = [
    "LatLon",
    "GeoTile",
    "RadarReturn",
    "RadarReading",
    "SkyObject",
    "Scene3D",
    "IGeoTileSource",
    "IRadarReadout",
    "ISkyTracker",
    "I3DSceneRenderer",
    "InMemoryGeoTileSource",
    "SyntheticRadarReadout",
    "SyntheticSkyTracker",
    "JsonScene3DRenderer",
    "NullGeoTileSource",
    "NullRadarReadout",
    "NullSkyTracker",
    "Null3DSceneRenderer",
]
