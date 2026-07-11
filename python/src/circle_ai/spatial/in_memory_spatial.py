# in_memory_spatial.py
#
# Port of CircleAI.Spatial InMemorySpatial.cs (C# — the EXACT spec).
#
# (3.3.0) Deterministic spatial sources for tests + host fallbacks:
#   • InMemoryGeoTileSource — pre-seeded place gazetteer, returns a 1x1 PNG tile
#     (real bytes so MIME/format detection works) and substring place search.
#   • SyntheticRadarReadout — deterministic radar returns computed from the
#     coordinate seed. The C# uses System.Random(seed); .NET's Random algorithm
#     is NOT portable, so this port uses a small deterministic LCG seeded the
#     same way — same shape (3..7 returns, radial scatter, doppler/intensity in
#     the same ranges), deterministic across runs.
#   • SyntheticSkyTracker — fixed star/planet table, daily-rotation azimuth +
#     latitude visibility filter (byte-for-byte with the C# math).
#   • JsonScene3DRenderer — minimal valid GLTF 2.0 wrapping the script.

from __future__ import annotations

import json
import math
import threading
import uuid
from typing import Dict, List, Optional, Tuple

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

# 1x1 transparent PNG (identical bytes to the C# literal).
_PNG_BYTES = bytes(
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    ]
)


class _Lcg:
    """Deterministic RNG (32-bit LCG, glibc constants) standing in for the
    per-seed .NET ``System.Random``. Emits the same *shape* of output the C#
    synthetic radar consumes: an int in [0, n) and a double in [0, 1)."""

    def __init__(self, seed: int) -> None:
        self._state = seed & 0xFFFFFFFF

    def _next_u32(self) -> int:
        self._state = (1103515245 * self._state + 12345) & 0xFFFFFFFF
        return self._state

    def next_int(self, min_inclusive: int, max_exclusive: int) -> int:
        span = max_exclusive - min_inclusive
        if span <= 0:
            return min_inclusive
        return min_inclusive + (self._next_u32() % span)

    def next_double(self) -> float:
        return self._next_u32() / 4294967296.0


class InMemoryGeoTileSource(IGeoTileSource):
    """Deterministic :class:`IGeoTileSource`. Mirrors
    ``CircleAI.Spatial.InMemoryGeoTileSource``."""

    def __init__(self) -> None:
        self._places: Dict[str, LatLon] = {}
        self._lock = threading.Lock()
        self.register("Johannesburg", LatLon(-26.2041, 28.0473))
        self.register("Cape Town", LatLon(-33.9249, 18.4241))
        self.register("Pretoria", LatLon(-25.7479, 28.2293))
        self.register("Durban", LatLon(-29.8587, 31.0218))
        self.register("Lagos", LatLon(6.5244, 3.3792))
        self.register("Nairobi", LatLon(-1.2921, 36.8219))
        self.register("London", LatLon(51.5074, -0.1278))
        self.register("New York", LatLon(40.7128, -74.0060))

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def register(self, name: str, at: LatLon) -> None:
        if name is None or name.strip() == "":
            raise ValueError("name required")
        with self._lock:
            self._places[name] = at

    async def get_tile_async(
        self, z: int, x: int, y: int, ct: Optional[object] = None
    ) -> GeoTile:
        if z < 0 or x < 0 or y < 0:
            raise ValueError("z")
        return GeoTile(z, x, y, _PNG_BYTES, "image/png")

    async def search_places_async(
        self, query: str, top_k: int = 5, ct: Optional[object] = None
    ) -> List[LatLon]:
        if query is None:
            raise ValueError("query")
        if top_k <= 0:
            raise ValueError("topK")
        ql = query.lower()
        with self._lock:
            matches = [(k, v) for (k, v) in self._places.items() if ql in k.lower()]
        matches.sort(key=lambda kv: kv[0])  # OrderBy(Key) — ordinal
        return [v for (_k, v) in matches[:top_k]]


class SyntheticRadarReadout(IRadarReadout):
    """Deterministic :class:`IRadarReadout`. Mirrors
    ``CircleAI.Spatial.SyntheticRadarReadout``."""

    @property
    def backend_id(self) -> str:
        return "synthetic"

    async def get_current_reading_async(
        self, at: LatLon, range_km: float = 50, ct: Optional[object] = None
    ) -> RadarReading:
        if at is None:
            raise ValueError("at")
        if range_km <= 0:
            raise ValueError("rangeKm")
        seed = int(at.latitude * 1000) + int(at.longitude * 1000) + int(range_km * 10)
        rng = _Lcg((seed ^ (seed >> 32)) & 0xFFFFFFFF)
        count = 3 + rng.next_int(0, 5)
        rets: List[RadarReturn] = []
        for _ in range(count):
            d = rng.next_double() * range_km * 0.9
            ang = rng.next_double() * math.pi * 2
            lat = at.latitude + (math.cos(ang) * d) / 111.0
            lon = at.longitude + (math.sin(ang) * d) / 111.0
            rets.append(
                RadarReturn(
                    LatLon(lat, lon),
                    rng.next_double() * 60 - 30,
                    rng.next_double() * 60,
                )
            )
        return RadarReading(at, range_km, rets)


class SyntheticSkyTracker(ISkyTracker):
    """Deterministic :class:`ISkyTracker`. Mirrors
    ``CircleAI.Spatial.SyntheticSkyTracker``."""

    _BASE_OBJECTS: List[Tuple[str, float, float, float]] = [
        ("Sirius", 102.7, 35.0, -1.46),
        ("Polaris", 0.0, 51.5, 1.97),
        ("Vega", 88.0, 70.0, 0.03),
        ("Mars", 135.4, 22.0, 0.5),
        ("Jupiter", 180.5, 40.0, -2.0),
        ("Saturn", 210.0, 30.0, 0.4),
    ]

    @property
    def backend_id(self) -> str:
        return "synthetic"

    async def visible_async(
        self, at: LatLon, utc: datetime, ct: Optional[object] = None
    ) -> List[SkyObject]:
        if at is None:
            raise ValueError("at")
        # hours since midnight UTC (TimeOfDay.TotalHours).
        hours = utc.hour + utc.minute / 60.0 + utc.second / 3600.0 + utc.microsecond / 3.6e9
        rot = hours * 15.0
        hits: List[SkyObject] = []
        for (n, az, alt, mag) in self._BASE_OBJECTS:
            az2 = (az - rot + 360) % 360
            if alt - abs(at.latitude) > 0:
                hits.append(SkyObject(n, az2, alt, mag))
        return hits


class JsonScene3DRenderer(I3DSceneRenderer):
    """Minimal-GLTF :class:`I3DSceneRenderer`. Mirrors
    ``CircleAI.Spatial.JsonScene3DRenderer``."""

    @property
    def backend_id(self) -> str:
        return "json"

    async def render_async(
        self, scene_script: str, format: str = "gltf", ct: Optional[object] = None
    ) -> Scene3D:
        if scene_script is None:
            raise ValueError("sceneScript")
        if format is None or format.strip() == "":
            format = "gltf"
        scene_id = uuid.uuid4().hex
        # Minimal valid GLTF 2.0 wrapping the script as an extras blob. The
        # script is JSON-string-encoded (System.Text.Json.JsonSerializer.Serialize).
        script_json = json.dumps(scene_script)
        js = (
            '{"asset":{"version":"2.0","generator":'
            '"CircleAI.Spatial.JsonScene3DRenderer"},'
            '"scenes":[{"nodes":[]}],"scene":0,'
            '"extras":{"script":' + script_json + "}}"
        )
        return Scene3D(scene_id, js.encode("utf-8"), format)
