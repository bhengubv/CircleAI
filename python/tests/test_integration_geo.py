"""test_integration_geo.py

Verifies the CircleAI.Integration.Geo port: the Open-Meteo weather provider
(current + hourly, WMO decode, m/s -> km/h) and the OSRM routing provider
(profile mapping, GeoJSON polyline, metres/seconds conversion). C# is the spec.
"""
from __future__ import annotations

import json
from datetime import timedelta, timezone

import pytest

from circle_ai.integration import InMemoryHttpFetcher, HttpResponse
from circle_ai.integration_geo import (
    OpenMeteoWeatherProvider,
    OsrmOptions,
    OsrmRoutingProvider,
)


def _weather_fetcher(payload: dict, needle: str) -> InMemoryHttpFetcher:
    f = InMemoryHttpFetcher()
    f.on_url_contains(needle, HttpResponse(200, json.dumps(payload)))
    return f


async def test_current_maps_all_fields_and_converts_wind() -> None:
    payload = {
        "current": {
            "time": "2024-01-02T15:00",
            "temperature_2m": 12.5,
            "apparent_temperature": 10.0,
            "precipitation": 0.2,
            "wind_speed_10m": 5.0,
            "cloud_cover": 40,
            "weather_code": 2,
        }
    }
    provider = OpenMeteoWeatherProvider(_weather_fetcher(payload, "current="))
    assert provider.provider_id == "open-meteo"
    s = await provider.current_async(-33.9249, 18.4241)
    assert s.temp_c == 12.5
    assert s.feels_like_c == 10.0
    assert s.precip_mm == 0.2
    assert s.wind_kph == pytest.approx(18.0)  # 5 m/s * 3.6
    assert s.cloud_pct == 40
    assert s.condition == "partly cloudy"
    assert s.at_utc.tzinfo is not None
    assert s.at_utc == s.at_utc.astimezone(timezone.utc)


async def test_current_request_uses_invariant_coordinates() -> None:
    provider = OpenMeteoWeatherProvider(
        _weather_fetcher(
            {
                "current": {
                    "time": "2024-01-02T15:00",
                    "temperature_2m": 1,
                    "apparent_temperature": 1,
                    "precipitation": 0,
                    "wind_speed_10m": 0,
                    "cloud_cover": 0,
                    "weather_code": 0,
                }
            },
            "current=",
        )
    )
    f = provider._http  # type: ignore[attr-defined]
    await provider.current_async(-33.5, 18.0)
    url = f.last_request.url
    assert "latitude=-33.5" in url
    assert "longitude=18" in url  # integral longitude renders without ".0"


async def test_hourly_takes_min_of_length_and_hours() -> None:
    payload = {
        "hourly": {
            "time": ["2024-01-01T00:00", "2024-01-01T01:00", "2024-01-01T02:00"],
            "temperature_2m": [1.0, 2.0, 3.0],
            "apparent_temperature": [0.5, 1.5, 2.5],
            "precipitation": [0.0, 0.1, 0.0],
            "wind_speed_10m": [1.0, 2.0, 3.0],
            "cloud_cover": [10, 20, 30],
            "weather_code": [0, 61, 95],
        }
    }
    provider = OpenMeteoWeatherProvider(_weather_fetcher(payload, "hourly="))
    out = await provider.hourly_async(1.0, 2.0, 2)
    assert len(out) == 2  # min(3 entries, 2 hours)
    assert out[0].condition == "clear sky"
    assert out[1].condition == "rain"
    assert out[1].wind_kph == pytest.approx(7.2)


async def test_hourly_rejects_out_of_range_hours() -> None:
    provider = OpenMeteoWeatherProvider(InMemoryHttpFetcher())
    with pytest.raises(ValueError):
        await provider.hourly_async(1.0, 2.0, 0)
    with pytest.raises(ValueError):
        await provider.hourly_async(1.0, 2.0, 169)


async def test_wmo_decode_covers_all_branches() -> None:
    from circle_ai.integration_geo.open_meteo_weather_provider import _wmo_decode

    assert _wmo_decode(0) == "clear sky"
    assert _wmo_decode(3) == "partly cloudy"
    assert _wmo_decode(48) == "fog"
    assert _wmo_decode(55) == "drizzle"
    assert _wmo_decode(57) == "freezing drizzle"
    assert _wmo_decode(65) == "rain"
    assert _wmo_decode(67) == "freezing rain"
    assert _wmo_decode(75) == "snow"
    assert _wmo_decode(77) == "snow grains"
    assert _wmo_decode(82) == "rain showers"
    assert _wmo_decode(86) == "snow showers"
    assert _wmo_decode(95) == "thunderstorm"
    assert _wmo_decode(99) == "thunderstorm with hail"
    assert _wmo_decode(12345) == "unknown"


async def test_osrm_route_parses_and_maps_profile() -> None:
    payload = {
        "code": "Ok",
        "routes": [
            {
                "distance": 1500.0,
                "duration": 300.0,
                "geometry": {
                    "coordinates": [[18.4, -33.9], [18.5, -34.0]],
                },
            }
        ],
    }
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps(payload)))
    provider = OsrmRoutingProvider(f)
    assert provider.provider_id == "osrm"
    est = await provider.route_async(-33.9, 18.4, -34.0, 18.5, "walk")
    assert est.distance_km == pytest.approx(1.5)
    assert est.duration == timedelta(seconds=300)
    # GeoJSON is [lon, lat]; RouteEstimate polyline is (lat, lon).
    assert est.polyline == [(-33.9, 18.4), (-34.0, 18.5)]
    assert "/foot/" in f.last_request.url  # walk -> foot profile


async def test_osrm_profile_defaults_to_driving() -> None:
    payload = {"code": "Ok", "routes": [{"distance": 0.0, "duration": 0.0}]}
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps(payload)))
    provider = OsrmRoutingProvider(f, OsrmOptions(host="https://osrm.example.com/"))
    await provider.route_async(0, 0, 1, 1, "car")
    # Trailing slash trimmed, unknown mode -> driving.
    assert "https://osrm.example.com/route/v1/driving/" in f.last_request.url


async def test_osrm_bike_alias() -> None:
    payload = {"code": "Ok", "routes": [{"distance": 0.0, "duration": 0.0}]}
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps(payload)))
    provider = OsrmRoutingProvider(f)
    await provider.route_async(0, 0, 1, 1, "bicycle")
    assert "/bike/" in f.last_request.url


async def test_osrm_non_ok_code_raises() -> None:
    payload = {"code": "NoRoute", "routes": []}
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps(payload)))
    provider = OsrmRoutingProvider(f)
    with pytest.raises(RuntimeError):
        await provider.route_async(0, 0, 1, 1)
