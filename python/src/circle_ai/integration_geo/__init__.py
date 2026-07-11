"""circle_ai.integration_geo — port of the CircleAI.Integration.Geo assembly.

(Phase B4) Weather + routing providers over the free Open-Meteo and OSRM APIs.
C# is the exact spec. The C# connectors take an injected ``HttpClient``; the
Python ports take an injected :class:`~circle_ai.integration.http.IHttpFetcher`
and parse the identical JSON so no real network is needed.

Public surface:

  * OpenMeteoWeatherProvider — WMO-coded current + hourly weather.
  * OsrmRoutingProvider / OsrmOptions — driving/bike/foot route estimates.
"""
from __future__ import annotations

from .open_meteo_weather_provider import OpenMeteoWeatherProvider
from .osrm_routing_provider import OsrmOptions, OsrmRoutingProvider

__all__ = [
    "OpenMeteoWeatherProvider",
    "OsrmRoutingProvider",
    "OsrmOptions",
]
