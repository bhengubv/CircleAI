# in_memory_connectors.py
#
# Port of CircleAI.Integration InMemoryIntegrationConnectors.cs (C# — the EXACT
# spec).
#
# Deterministic, dependency-free in-memory reference implementations of the
# integration connector contracts. These are the canonical offline/test doubles
# for ICalendarConnector / IEmailConnector / INewsSource / IWeatherProvider /
# IRoutingProvider / IHomeAutomationConnector — usable without any external
# provider, mirroring the InMemory* pattern every other package ships. The real
# provider bindings live in the network-backed connectors (see
# :mod:`circle_ai.integration.http`).
#
# The weather + routing math is deterministic and mirrors the C# formulas
# byte-for-byte: Python's ``round`` and C# ``Math.Round`` both use banker's
# rounding (round-half-to-even), so the rounded temperatures / distances match.
# C# ``DateTimeOffset.UnixEpoch`` maps to 1970-01-01T00:00:00+00:00.

from __future__ import annotations

import math
from datetime import datetime, timedelta, timezone
from typing import Dict, Iterable, List, Mapping, Optional

from .contracts import (
    CalendarEvent,
    EmailMessage,
    HaEntity,
    ICalendarConnector,
    IEmailConnector,
    IHomeAutomationConnector,
    INewsSource,
    IRoutingProvider,
    IWeatherProvider,
    NewsItem,
    RouteEstimate,
    WeatherSample,
)

# C# DateTimeOffset.UnixEpoch.
_UNIX_EPOCH: datetime = datetime(1970, 1, 1, tzinfo=timezone.utc)


def _max(a: int, b: int) -> int:
    """``max`` shadow-free helper mirroring C# ``Math.Max(0, max)`` — used
    because the connector methods bind a parameter named ``max`` that shadows the
    built-in.
    """
    return a if a > b else b


class InMemoryCalendarConnector(ICalendarConnector):
    """In-memory :class:`ICalendarConnector`: events are held in a map; listing
    returns those overlapping the window, ordered by start. Faithful port of the
    C# ``InMemoryCalendarConnector``.
    """

    def __init__(self) -> None:
        self._events: Dict[str, CalendarEvent] = {}

    @property
    def provider_id(self) -> str:
        return "in-memory"

    @property
    def is_configured(self) -> bool:
        return True

    async def list_events_async(
        self, from_utc: datetime, to_utc: datetime
    ) -> List[CalendarEvent]:
        matches = [
            e
            for e in self._events.values()
            if e.start_utc < to_utc and e.end_utc > from_utc
        ]
        return sorted(matches, key=lambda e: e.start_utc)

    async def create_event_async(self, ev: CalendarEvent) -> CalendarEvent:
        if ev is None:
            raise ValueError("event must not be None")
        self._events[ev.event_id] = ev
        return ev

    async def delete_event_async(self, calendar_id: str, event_id: str) -> None:
        self._events.pop(event_id, None)


class InMemoryEmailConnector(IEmailConnector):
    """In-memory :class:`IEmailConnector`: seeded with messages; unread + search
    read newest-first, :meth:`mark_read_async` flips the flag. Faithful port of
    the C# ``InMemoryEmailConnector``.
    """

    def __init__(self, seed: Optional[Iterable[EmailMessage]] = None) -> None:
        self._messages: Dict[str, EmailMessage] = {}
        if seed is not None:
            for m in seed:
                self._messages[m.message_id] = m

    @property
    def provider_id(self) -> str:
        return "in-memory"

    @property
    def is_configured(self) -> bool:
        return True

    async def list_unread_async(self, max: int) -> List[EmailMessage]:
        unread = [m for m in self._messages.values() if m.unread]
        unread.sort(key=lambda m: m.received_utc, reverse=True)
        return unread[: _max(0, max)]

    async def search_async(self, query: str, max: int) -> List[EmailMessage]:
        q = (query or "").casefold()
        matches = [
            m
            for m in self._messages.values()
            if q in m.subject.casefold() or q in m.body_text.casefold()
        ]
        matches.sort(key=lambda m: m.received_utc, reverse=True)
        return matches[: _max(0, max)]

    async def mark_read_async(self, message_id: str) -> None:
        m = self._messages.get(message_id)
        if m is not None:
            self._messages[message_id] = EmailMessage(
                m.message_id,
                m.from_,
                m.to,
                m.subject,
                m.body_text,
                m.received_utc,
                False,
                m.labels,
            )


class InMemoryNewsSource(INewsSource):
    """In-memory :class:`INewsSource`: seeded items, newest-first. Faithful port
    of the C# ``InMemoryNewsSource``.
    """

    def __init__(self, seed: Optional[Iterable[NewsItem]] = None) -> None:
        self._items: Dict[str, NewsItem] = {}
        if seed is not None:
            for i in seed:
                self._items[i.item_id] = i

    @property
    def source_id(self) -> str:
        return "in-memory"

    @property
    def is_configured(self) -> bool:
        return True

    async def fetch_latest_async(self, max: int) -> List[NewsItem]:
        items = sorted(
            self._items.values(), key=lambda i: i.published_utc, reverse=True
        )
        return items[: _max(0, max)]


class InMemoryWeatherProvider(IWeatherProvider):
    """In-memory :class:`IWeatherProvider`: deterministic pseudo-weather derived
    from coordinates + hour (no randomness, reproducible across platforms).
    Faithful port of the C# ``InMemoryWeatherProvider``.
    """

    @property
    def provider_id(self) -> str:
        return "in-memory"

    async def current_async(self, lat: float, lon: float) -> WeatherSample:
        return self._sample(lat, lon, 0)

    async def hourly_async(
        self, lat: float, lon: float, hours: int
    ) -> List[WeatherSample]:
        return [
            self._sample(lat, lon, h) for h in range(_max(0, hours))
        ]

    @staticmethod
    def _sample(lat: float, lon: float, hour_offset: int) -> WeatherSample:
        temp_c = round(15.0 + 10.0 * math.cos((lat + hour_offset) * math.pi / 12.0), 2)
        return WeatherSample(
            _UNIX_EPOCH + timedelta(hours=hour_offset),
            temp_c,
            round(temp_c - 1.5, 2),
            0.0,
            12.0,
            40,
            "Clear",
        )


class InMemoryRoutingProvider(IRoutingProvider):
    """In-memory :class:`IRoutingProvider`: great-circle distance and a
    mode-based speed give a deterministic estimate with a 2-point polyline.
    Faithful port of the C# ``InMemoryRoutingProvider``.
    """

    @property
    def provider_id(self) -> str:
        return "in-memory"

    async def route_async(
        self,
        from_lat: float,
        from_lon: float,
        to_lat: float,
        to_lon: float,
        mode: str = "car",
    ) -> RouteEstimate:
        km = self._haversine(from_lat, from_lon, to_lat, to_lon)
        kph = {"walk": 5.0, "bike": 18.0, "transit": 30.0}.get(mode, 60.0)
        dur = timedelta(hours=0 if kph <= 0 else km / kph)
        return RouteEstimate(
            round(km, 3),
            dur,
            [(from_lat, from_lon), (to_lat, to_lon)],
        )

    @staticmethod
    def _haversine(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
        r = 6371.0
        d_lat = (lat2 - lat1) * math.pi / 180.0
        d_lon = (lon2 - lon1) * math.pi / 180.0
        a = (
            math.sin(d_lat / 2) * math.sin(d_lat / 2)
            + math.cos(lat1 * math.pi / 180.0)
            * math.cos(lat2 * math.pi / 180.0)
            * math.sin(d_lon / 2)
            * math.sin(d_lon / 2)
        )
        return r * 2 * math.atan2(math.sqrt(a), math.sqrt(1 - a))


class InMemoryHomeAutomationConnector(IHomeAutomationConnector):
    """In-memory :class:`IHomeAutomationConnector`: seeded entities;
    ``turn_on`` / ``turn_off`` / ``toggle`` deterministically mutate
    matching-domain entity state. Faithful port of the C#
    ``InMemoryHomeAutomationConnector``.
    """

    def __init__(self, seed: Optional[Iterable[HaEntity]] = None) -> None:
        self._entities: Dict[str, HaEntity] = {}
        if seed is not None:
            for e in seed:
                self._entities[e.entity_id] = e

    @property
    def provider_id(self) -> str:
        return "in-memory"

    @property
    def is_configured(self) -> bool:
        return True

    async def list_entities_async(self) -> List[HaEntity]:
        return sorted(self._entities.values(), key=lambda e: e.entity_id)

    async def call_service_async(
        self,
        domain: str,
        service: str,
        data: Optional[Mapping[str, object]],
    ) -> None:
        target = domain.casefold()
        matches = [
            e for e in self._entities.values() if e.domain.casefold() == target
        ]
        for e in matches:
            if service == "turn_on":
                new_state = "on"
            elif service == "turn_off":
                new_state = "off"
            elif service == "toggle":
                new_state = "off" if e.state == "on" else "on"
            else:
                new_state = e.state
            self._entities[e.entity_id] = HaEntity(
                e.entity_id,
                e.friendly_name,
                e.domain,
                new_state,
                e.attributes,
            )


__all__ = [
    "InMemoryCalendarConnector",
    "InMemoryEmailConnector",
    "InMemoryNewsSource",
    "InMemoryWeatherProvider",
    "InMemoryRoutingProvider",
    "InMemoryHomeAutomationConnector",
]
