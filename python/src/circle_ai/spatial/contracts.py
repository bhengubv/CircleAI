# contracts.py
#
# Port of CircleAI.Spatial Contracts.cs (C# — the EXACT spec).
#
# (2.5.0) Spatial / geo contract surface: map tiles + place search, radar
# readout, visible-sky tracking, and 3D-scene rendering.
#
# C# ValueTask<T> -> async def -> T. C# records -> frozen slotted dataclasses.
# ReadOnlyMemory<byte> -> bytes. DateTimeOffset -> datetime.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class LatLon:
    """Mirrors ``CircleAI.Spatial.LatLon`` — ``record(double Latitude,
    double Longitude)``.
    """

    latitude: float
    longitude: float


@dataclass(frozen=True, slots=True)
class GeoTile:
    """Mirrors ``CircleAI.Spatial.GeoTile`` — ``record(int Z, int X, int Y,
    ReadOnlyMemory<byte> ImageBytes, string MimeType)``.
    """

    z: int
    x: int
    y: int
    image_bytes: bytes
    mime_type: str


@dataclass(frozen=True, slots=True)
class RadarReturn:
    """Mirrors ``CircleAI.Spatial.RadarReturn`` — ``record(LatLon Position,
    double DopplerKmh, double IntensityDbz)``.
    """

    position: LatLon
    doppler_kmh: float
    intensity_dbz: float


@dataclass(frozen=True, slots=True)
class RadarReading:
    """Mirrors ``CircleAI.Spatial.RadarReading`` — ``record(LatLon Centre,
    double RangeKm, IReadOnlyList<RadarReturn> Returns)``.
    """

    centre: LatLon
    range_km: float
    returns: Sequence[RadarReturn]


@dataclass(frozen=True, slots=True)
class SkyObject:
    """Mirrors ``CircleAI.Spatial.SkyObject`` — ``record(string Name,
    double AzimuthDeg, double AltitudeDeg, double MagnitudeApparent)``.
    """

    name: str
    azimuth_deg: float
    altitude_deg: float
    magnitude_apparent: float


@dataclass(frozen=True, slots=True)
class Scene3D:
    """Mirrors ``CircleAI.Spatial.Scene3D`` — ``record(string SceneId,
    ReadOnlyMemory<byte> Encoded, string Format)``.
    """

    scene_id: str
    encoded: bytes
    format: str


class IGeoTileSource(ABC):
    """(2.5.0) Map-tile source (deck.gl / cesium pattern)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def get_tile_async(
        self, z: int, x: int, y: int, ct: Optional[object] = None
    ) -> GeoTile:
        ...

    @abstractmethod
    async def search_places_async(
        self, query: str, top_k: int = 5, ct: Optional[object] = None
    ) -> List[LatLon]:
        ...


class IRadarReadout(ABC):
    """(2.5.0) Weather / surveillance radar (RADAR pattern)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def get_current_reading_async(
        self, at: LatLon, range_km: float = 50, ct: Optional[object] = None
    ) -> RadarReading:
        ...


class ISkyTracker(ABC):
    """(2.5.0) Visible-sky tracking (skylight pattern)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def visible_async(
        self, at: LatLon, utc: datetime, ct: Optional[object] = None
    ) -> List[SkyObject]:
        ...


class I3DSceneRenderer(ABC):
    """(2.5.0) 3D-scene rendering hook (flame / anime pattern)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def render_async(
        self, scene_script: str, format: str = "gltf", ct: Optional[object] = None
    ) -> Scene3D:
        ...
