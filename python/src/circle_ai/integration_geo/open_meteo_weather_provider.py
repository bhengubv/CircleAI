# open_meteo_weather_provider.py
#
# Port of CircleAI.Integration.Geo/OpenMeteoWeatherProvider.cs (C# — the EXACT
# spec).
#
# (Phase B4) Open-Meteo free, no-API-key weather provider. Returns current
# conditions + hourly forecast in plain JSON.
#
# The C# takes an injected ``HttpClient``; the Python port takes an injected
# :class:`IHttpFetcher` and parses the identical JSON shape. Wind is converted
# m/s -> km/h (``* 3.6``) exactly as the C#.

from __future__ import annotations

from typing import List

from circle_ai.integration._util import parse_utc
from circle_ai.integration.contracts import IWeatherProvider, WeatherSample
from circle_ai.integration.http import HttpRequest, IHttpFetcher


def _invariant(x: float) -> str:
    """C# ``double.ToString(CultureInfo.InvariantCulture)`` — '.' decimal, no
    thousands separators, no trailing ``.0`` for integers.
    """
    if x == int(x):
        return str(int(x))
    return repr(x)


def _wmo_decode(code: int) -> str:
    """(Phase B4) Decode WMO weather code (Open-Meteo standard). Mirrors the C#
    ``WmoDecode`` switch expression exactly.
    """
    if code == 0:
        return "clear sky"
    if code in (1, 2, 3):
        return "partly cloudy"
    if code in (45, 48):
        return "fog"
    if code in (51, 53, 55):
        return "drizzle"
    if code in (56, 57):
        return "freezing drizzle"
    if code in (61, 63, 65):
        return "rain"
    if code in (66, 67):
        return "freezing rain"
    if code in (71, 73, 75):
        return "snow"
    if code == 77:
        return "snow grains"
    if code in (80, 81, 82):
        return "rain showers"
    if code in (85, 86):
        return "snow showers"
    if code == 95:
        return "thunderstorm"
    if code in (96, 99):
        return "thunderstorm with hail"
    return "unknown"


class OpenMeteoWeatherProvider(IWeatherProvider):
    """Port of ``CircleAI.Integration.Geo.OpenMeteoWeatherProvider``."""

    def __init__(self, http: IHttpFetcher) -> None:
        if http is None:
            raise ValueError("http must not be None")
        self._http = http

    @property
    def provider_id(self) -> str:
        return "open-meteo"

    async def current_async(self, lat: float, lon: float) -> WeatherSample:
        url = (
            "https://api.open-meteo.com/v1/forecast"
            f"?latitude={_invariant(lat)}&longitude={_invariant(lon)}"
            "&current=temperature_2m,apparent_temperature,precipitation,"
            "wind_speed_10m,cloud_cover,weather_code"
        )
        resp = (await self._http.send_async(HttpRequest("GET", url))).ensure_success()
        root = resp.json()
        cur = root["current"]
        ts = cur.get("time")
        return WeatherSample(
            at_utc=parse_utc(ts),
            temp_c=float(cur["temperature_2m"]),
            feels_like_c=float(cur["apparent_temperature"]),
            precip_mm=float(cur["precipitation"]),
            wind_kph=float(cur["wind_speed_10m"]) * 3.6,  # m/s -> km/h
            cloud_pct=int(cur["cloud_cover"]),
            condition=_wmo_decode(int(cur["weather_code"])),
        )

    async def hourly_async(
        self, lat: float, lon: float, hours: int
    ) -> List[WeatherSample]:
        if hours <= 0 or hours > 168:
            raise ValueError("hours out of range (1..168)")
        url = (
            "https://api.open-meteo.com/v1/forecast"
            f"?latitude={_invariant(lat)}&longitude={_invariant(lon)}"
            "&hourly=temperature_2m,apparent_temperature,precipitation,"
            "wind_speed_10m,cloud_cover,weather_code"
            f"&forecast_hours={hours}"
        )
        resp = (await self._http.send_async(HttpRequest("GET", url))).ensure_success()
        root = resp.json()
        h = root["hourly"]
        time = h["time"]
        temp = h["temperature_2m"]
        feel = h["apparent_temperature"]
        prec = h["precipitation"]
        wind = h["wind_speed_10m"]
        cld = h["cloud_cover"]
        code = h["weather_code"]
        n = min(len(time), hours)
        result: List[WeatherSample] = []
        for i in range(n):
            result.append(
                WeatherSample(
                    at_utc=parse_utc(time[i]),
                    temp_c=float(temp[i]),
                    feels_like_c=float(feel[i]),
                    precip_mm=float(prec[i]),
                    wind_kph=float(wind[i]) * 3.6,
                    cloud_pct=int(cld[i]),
                    condition=_wmo_decode(int(code[i])),
                )
            )
        return result
