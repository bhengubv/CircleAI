# contracts.py
#
# Port of CircleAI.Integration/Contracts.cs (C# — the EXACT spec).
#
# (Phase B) Shared abstractions for the external-integration layer.
# Calendar, email, news, weather and home-automation providers all
# implement these so the Companion's ProactiveBriefingService can stitch
# a coherent "what's happening" picture without coupling to specific
# providers.
#
# C# ``DateTimeOffset`` maps to a timezone-aware :class:`datetime` (always
# normalised to UTC by the connectors). ``DateTimeOffset.MinValue`` maps to
# :data:`DATETIME_MIN` (0001-01-01T00:00:00+00:00). C# ``TimeSpan`` maps to
# :class:`datetime.timedelta`. ``Uri`` maps to ``str`` (Python has no first-
# class URI type; ``about:blank`` sentinels are preserved verbatim).
#
# ``ValueTask<T>`` async methods map to ``async def`` coroutines.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from typing import List, Mapping, Optional, Sequence, Tuple

# C# DateTimeOffset.MinValue == 0001-01-01T00:00:00+00:00
DATETIME_MIN: datetime = datetime.min.replace(tzinfo=timezone.utc)


# -- Calendar --------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class CalendarEvent:
    """Mirrors ``CircleAI.Integration.CalendarEvent`` — ``record(string EventId,
    string CalendarId, string Title, string? Description, string? Location,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, bool IsAllDay,
    IReadOnlyList<string> Attendees)``.
    """

    event_id: str
    calendar_id: str
    title: str
    description: Optional[str]
    location: Optional[str]
    start_utc: datetime
    end_utc: datetime
    is_all_day: bool
    attendees: Sequence[str] = field(default_factory=tuple)


class ICalendarConnector(ABC):
    """Mirrors ``CircleAI.Integration.ICalendarConnector``."""

    @property
    @abstractmethod
    def provider_id(self) -> str:
        ...

    @property
    @abstractmethod
    def is_configured(self) -> bool:
        ...

    @abstractmethod
    async def list_events_async(
        self, from_utc: datetime, to_utc: datetime
    ) -> List[CalendarEvent]:
        ...

    @abstractmethod
    async def create_event_async(self, ev: CalendarEvent) -> CalendarEvent:
        ...

    @abstractmethod
    async def delete_event_async(self, calendar_id: str, event_id: str) -> None:
        ...


# -- Email -----------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class EmailMessage:
    """Mirrors ``CircleAI.Integration.EmailMessage`` — ``record(string MessageId,
    string From, IReadOnlyList<string> To, string Subject, string BodyText,
    DateTimeOffset ReceivedUtc, bool Unread, IReadOnlyList<string> Labels)``.
    """

    message_id: str
    from_: str
    to: Sequence[str]
    subject: str
    body_text: str
    received_utc: datetime
    unread: bool
    labels: Sequence[str] = field(default_factory=tuple)


class IEmailConnector(ABC):
    """Mirrors ``CircleAI.Integration.IEmailConnector``."""

    @property
    @abstractmethod
    def provider_id(self) -> str:
        ...

    @property
    @abstractmethod
    def is_configured(self) -> bool:
        ...

    @abstractmethod
    async def list_unread_async(self, max: int) -> List[EmailMessage]:
        ...

    @abstractmethod
    async def search_async(self, query: str, max: int) -> List[EmailMessage]:
        ...

    @abstractmethod
    async def mark_read_async(self, message_id: str) -> None:
        ...


# -- News + social feeds ---------------------------------------------------


@dataclass(frozen=True, slots=True)
class NewsItem:
    """Mirrors ``CircleAI.Integration.NewsItem`` — ``record(string ItemId,
    string SourceId, string Title, string Summary, Uri Url,
    DateTimeOffset PublishedUtc, IReadOnlyList<string> Tags)``.

    ``Url`` is a plain ``str`` (C# ``Uri``).
    """

    item_id: str
    source_id: str
    title: str
    summary: str
    url: str
    published_utc: datetime
    tags: Sequence[str] = field(default_factory=tuple)


class INewsSource(ABC):
    """Mirrors ``CircleAI.Integration.INewsSource``."""

    @property
    @abstractmethod
    def source_id(self) -> str:
        ...

    @property
    @abstractmethod
    def is_configured(self) -> bool:
        ...

    @abstractmethod
    async def fetch_latest_async(self, max: int) -> List[NewsItem]:
        ...


# -- Weather ---------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class WeatherSample:
    """Mirrors ``CircleAI.Integration.WeatherSample`` — ``record(DateTimeOffset
    AtUtc, double TempC, double FeelsLikeC, double PrecipMm, double WindKph,
    int CloudPct, string Condition)``.
    """

    at_utc: datetime
    temp_c: float
    feels_like_c: float
    precip_mm: float
    wind_kph: float
    cloud_pct: int
    condition: str


class IWeatherProvider(ABC):
    """Mirrors ``CircleAI.Integration.IWeatherProvider``."""

    @property
    @abstractmethod
    def provider_id(self) -> str:
        ...

    @abstractmethod
    async def current_async(self, lat: float, lon: float) -> WeatherSample:
        ...

    @abstractmethod
    async def hourly_async(
        self, lat: float, lon: float, hours: int
    ) -> List[WeatherSample]:
        ...


# -- Routing / traffic -----------------------------------------------------


@dataclass(frozen=True, slots=True)
class RouteEstimate:
    """Mirrors ``CircleAI.Integration.RouteEstimate`` — ``record(double
    DistanceKm, TimeSpan Duration, IReadOnlyList<(double Lat, double Lon)>
    Polyline)``.

    ``Duration`` is a :class:`datetime.timedelta` (C# ``TimeSpan``). Each
    polyline point is a ``(lat, lon)`` tuple of floats.
    """

    distance_km: float
    duration: timedelta
    polyline: Sequence[Tuple[float, float]] = field(default_factory=tuple)


class IRoutingProvider(ABC):
    """Mirrors ``CircleAI.Integration.IRoutingProvider``."""

    @property
    @abstractmethod
    def provider_id(self) -> str:
        ...

    @abstractmethod
    async def route_async(
        self,
        from_lat: float,
        from_lon: float,
        to_lat: float,
        to_lon: float,
        mode: str = "car",
    ) -> RouteEstimate:
        ...


# -- Home automation -------------------------------------------------------


@dataclass(frozen=True, slots=True)
class HaEntity:
    """Mirrors ``CircleAI.Integration.HaEntity`` — ``record(string EntityId,
    string FriendlyName, string Domain, string State,
    IReadOnlyDictionary<string, string> Attributes)``.
    """

    entity_id: str
    friendly_name: str
    domain: str
    state: str
    attributes: Mapping[str, str] = field(default_factory=dict)


class IHomeAutomationConnector(ABC):
    """Mirrors ``CircleAI.Integration.IHomeAutomationConnector``."""

    @property
    @abstractmethod
    def provider_id(self) -> str:
        ...

    @property
    @abstractmethod
    def is_configured(self) -> bool:
        ...

    @abstractmethod
    async def list_entities_async(self) -> List[HaEntity]:
        ...

    @abstractmethod
    async def call_service_async(
        self,
        domain: str,
        service: str,
        data: Optional[Mapping[str, object]],
    ) -> None:
        ...
