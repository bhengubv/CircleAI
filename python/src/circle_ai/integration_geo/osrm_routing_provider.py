# osrm_routing_provider.py
#
# Port of CircleAI.Integration.Geo/OsrmRoutingProvider.cs (C# — the EXACT spec).
#
# (Phase B4) Open Source Routing Machine (OSRM) HTTP client. Default host is the
# public OSRM demo server; production hosts should run their own instance.
#
# The C# takes an injected ``HttpClient``; the Python port takes an injected
# :class:`IHttpFetcher` and parses the identical GeoJSON shape.

from __future__ import annotations

from dataclasses import dataclass
from datetime import timedelta
from typing import List, Tuple

from circle_ai.integration.contracts import IRoutingProvider, RouteEstimate
from circle_ai.integration.http import HttpRequest, IHttpFetcher


def _invariant(x: float) -> str:
    """C# ``double.ToString(CultureInfo.InvariantCulture)``."""
    if x == int(x):
        return str(int(x))
    return repr(x)


@dataclass(frozen=True, slots=True)
class OsrmOptions:
    """Mirrors ``CircleAI.Integration.Geo.OsrmOptions`` — ``record(string
    Host = "https://router.project-osrm.org")``.
    """

    host: str = "https://router.project-osrm.org"


class OsrmRoutingProvider(IRoutingProvider):
    """Port of ``CircleAI.Integration.Geo.OsrmRoutingProvider``."""

    def __init__(self, http: IHttpFetcher, opts: OsrmOptions | None = None) -> None:
        if http is None:
            raise ValueError("http must not be None")
        self._opts = opts if opts is not None else OsrmOptions()
        self._http = http

    @property
    def provider_id(self) -> str:
        return "osrm"

    async def route_async(
        self,
        from_lat: float,
        from_lon: float,
        to_lat: float,
        to_lon: float,
        mode: str = "car",
    ) -> RouteEstimate:
        if mode in ("bike", "bicycle"):
            profile = "bike"
        elif mode in ("foot", "walk"):
            profile = "foot"
        else:
            profile = "driving"
        url = (
            f"{self._opts.host.rstrip('/')}/route/v1/{profile}/"
            f"{_invariant(from_lon)},{_invariant(from_lat)};"
            f"{_invariant(to_lon)},{_invariant(to_lat)}"
            "?overview=full&geometries=geojson"
        )
        resp = (await self._http.send_async(HttpRequest("GET", url))).ensure_success()
        root = resp.json()

        code = root.get("code")
        if code != "Ok":
            raise RuntimeError(f"OSRM returned code={code}")

        route = root["routes"][0]
        dist = float(route["distance"])  # metres
        dur = float(route["duration"])  # seconds
        poly: List[Tuple[float, float]] = []
        geom = route.get("geometry")
        coords = geom.get("coordinates") if isinstance(geom, dict) else None
        if isinstance(coords, list):
            for pt in coords:
                if not isinstance(pt, list) or len(pt) < 2:
                    continue
                poly.append((float(pt[1]), float(pt[0])))
        return RouteEstimate(
            distance_km=dist / 1000.0,
            duration=timedelta(seconds=dur),
            polyline=poly,
        )
