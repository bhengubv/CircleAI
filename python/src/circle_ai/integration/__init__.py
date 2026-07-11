"""circle_ai.integration — port of the CircleAI.Integration assembly.

(Phase B) Shared abstractions for the external-integration layer: calendar,
email, news/social, weather, routing and home-automation providers all
implement these contracts so the Companion can stitch a coherent "what's
happening" picture without coupling to specific providers. C# is the exact
spec (``Contracts.cs``).

The C# connectors take an injected ``HttpClient``; to port their JSON/XML/text
parsing faithfully with no real network, the Python connectors take an injected
:class:`IHttpFetcher` (see :mod:`circle_ai.integration.http`) whose in-memory
implementation replays deterministic responses.

Public surface:

  * CalendarEvent / EmailMessage / NewsItem / WeatherSample / RouteEstimate /
    HaEntity — domain records.
  * ICalendarConnector / IEmailConnector / INewsSource / IWeatherProvider /
    IRoutingProvider / IHomeAutomationConnector — provider interfaces.
  * IHttpFetcher / InMemoryHttpFetcher / HttpRequest / HttpResponse / HttpError —
    the injectable async HTTP abstraction.
  * DATETIME_MIN — C# ``DateTimeOffset.MinValue`` sentinel.
"""
from __future__ import annotations

from .contracts import (
    DATETIME_MIN,
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
from .http import (
    HttpError,
    HttpRequest,
    HttpResponse,
    IHttpFetcher,
    InMemoryHttpFetcher,
)

__all__ = [
    # records
    "CalendarEvent",
    "EmailMessage",
    "NewsItem",
    "WeatherSample",
    "RouteEstimate",
    "HaEntity",
    # interfaces
    "ICalendarConnector",
    "IEmailConnector",
    "INewsSource",
    "IWeatherProvider",
    "IRoutingProvider",
    "IHomeAutomationConnector",
    # http abstraction
    "IHttpFetcher",
    "InMemoryHttpFetcher",
    "HttpRequest",
    "HttpResponse",
    "HttpError",
    # sentinel
    "DATETIME_MIN",
]
