"""test_integration_contracts.py

Verifies the CircleAI.Integration core port: the domain records, the injectable
async HTTP abstraction (routing, status semantics, request recording), and the
DateTimeOffset.MinValue sentinel. C# is the spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.integration import (
    DATETIME_MIN,
    CalendarEvent,
    EmailMessage,
    HaEntity,
    HttpError,
    HttpRequest,
    HttpResponse,
    InMemoryHttpFetcher,
    NewsItem,
    RouteEstimate,
    WeatherSample,
)


def test_datetime_min_matches_csharp_minvalue() -> None:
    assert DATETIME_MIN == datetime.min.replace(tzinfo=timezone.utc)
    assert DATETIME_MIN.isoformat() == "0001-01-01T00:00:00+00:00"


def test_records_are_frozen() -> None:
    ev = CalendarEvent(
        "id", "cal", "t", None, None, DATETIME_MIN, DATETIME_MIN, False, ()
    )
    with pytest.raises(Exception):
        ev.title = "changed"  # type: ignore[misc]


def test_records_construct() -> None:
    assert EmailMessage("m", "f", ("t",), "s", "b", DATETIME_MIN, True, ()).from_ == "f"
    assert NewsItem("i", "s", "t", "sum", "about:blank", DATETIME_MIN, ()).url == "about:blank"
    assert WeatherSample(DATETIME_MIN, 1.0, 1.0, 0.0, 0.0, 0, "clear sky").temp_c == 1.0
    est = RouteEstimate(1.5, timedelta(seconds=60), [(1.0, 2.0)])
    assert est.duration == timedelta(seconds=60)
    assert HaEntity("e", "n", "d", "on", {"k": "v"}).attributes["k"] == "v"


def test_http_response_status_semantics() -> None:
    assert HttpResponse(200).is_success is True
    assert HttpResponse(299).is_success is True
    assert HttpResponse(204).is_success is True
    assert HttpResponse(300).is_success is False
    assert HttpResponse(404).is_success is False
    HttpResponse(200).ensure_success()  # no raise
    with pytest.raises(HttpError):
        HttpResponse(500).ensure_success()


def test_http_response_json_parse() -> None:
    assert HttpResponse(200, '{"a": 1}').json() == {"a": 1}


async def test_fetcher_routes_first_match_and_records() -> None:
    f = InMemoryHttpFetcher()
    f.on_url_contains("/a", HttpResponse(200, "A"))
    f.on_url_contains("/b", HttpResponse(201, "B"))
    r1 = await f.send_async(HttpRequest("GET", "http://x/a"))
    r2 = await f.send_async(HttpRequest("POST", "http://x/b"))
    assert (r1.status_code, r1.text) == (200, "A")
    assert (r2.status_code, r2.text) == (201, "B")
    assert len(f.requests) == 2
    assert f.last_request.method == "POST"


async def test_fetcher_default_is_404() -> None:
    f = InMemoryHttpFetcher()
    r = await f.send_async(HttpRequest("GET", "http://x/missing"))
    assert r.status_code == 404


async def test_fetcher_custom_default() -> None:
    f = InMemoryHttpFetcher(default=HttpResponse(503, "down"))
    r = await f.send_async(HttpRequest("GET", "http://x/anything"))
    assert r.status_code == 503


async def test_fetcher_method_filter() -> None:
    f = InMemoryHttpFetcher()
    f.on_url_contains("/x", HttpResponse(200, "get"), method="GET")
    got = await f.send_async(HttpRequest("GET", "http://h/x"))
    missed = await f.send_async(HttpRequest("POST", "http://h/x"))
    assert got.status_code == 200
    assert missed.status_code == 404  # method mismatch falls through
