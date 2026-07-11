"""test_integration_calendar.py

Verifies the CircleAI.Integration.Calendar port: the CalDAV connector (REPORT +
minimal ICS parse/build, all-day detection), and the Google / Microsoft Graph
connectors (token callback auth, event parsing, create round-trip). C# is spec.
"""
from __future__ import annotations

import json
from datetime import datetime, timezone

import pytest

from circle_ai.integration import (
    CalendarEvent,
    InMemoryHttpFetcher,
    HttpResponse,
)
from circle_ai.integration_calendar import (
    CalDavCalendarConnector,
    CalDavCalendarOptions,
    GoogleCalendarConnector,
    GoogleCalendarOptions,
    MsGraphCalendarConnector,
    MsGraphCalendarOptions,
)


def _token(value):
    async def _provider():
        return value

    return _provider


# -- CalDAV ----------------------------------------------------------------

_CALDAV_REPORT = """<?xml version="1.0"?>
<D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
  <D:response>
    <D:href>/cal/1.ics</D:href>
    <D:propstat><D:prop>
      <C:calendar-data>BEGIN:VCALENDAR
BEGIN:VEVENT
UID:evt-1
SUMMARY:Team Sync
DESCRIPTION:Weekly
LOCATION:Room 5
DTSTART:20240115T090000Z
DTEND:20240115T100000Z
END:VEVENT
END:VCALENDAR</C:calendar-data>
    </D:prop></D:propstat>
  </D:response>
</D:multistatus>
"""


def _caldav(f: InMemoryHttpFetcher) -> CalDavCalendarConnector:
    return CalDavCalendarConnector(
        CalDavCalendarOptions(
            "https://dav.example.com/cal/", "user", "pass"
        ),
        f,
    )


async def test_caldav_is_configured_and_provider_id() -> None:
    conn = _caldav(InMemoryHttpFetcher())
    assert conn.provider_id == "caldav"
    assert conn.is_configured is True
    assert (
        CalDavCalendarConnector(
            CalDavCalendarOptions("u", "", "p"), InMemoryHttpFetcher()
        ).is_configured
        is False
    )


async def test_caldav_list_events_parses_ics() -> None:
    f = InMemoryHttpFetcher().on_method("REPORT", HttpResponse(207, _CALDAV_REPORT))
    conn = _caldav(f)
    frm = datetime(2024, 1, 1, tzinfo=timezone.utc)
    to = datetime(2024, 1, 31, tzinfo=timezone.utc)
    events = await conn.list_events_async(frm, to)
    assert len(events) == 1
    e = events[0]
    assert e.event_id == "evt-1"
    assert e.title == "Team Sync"
    assert e.description == "Weekly"
    assert e.location == "Room 5"
    assert e.start_utc.isoformat() == "2024-01-15T09:00:00+00:00"
    assert e.end_utc.isoformat() == "2024-01-15T10:00:00+00:00"
    assert e.is_all_day is False
    # REPORT request carries Basic auth + Depth + time-range filter.
    req = f.last_request
    assert req.method == "REPORT"
    assert req.headers["Depth"] == "1"
    assert req.headers["Authorization"].startswith("Basic ")
    assert 'start="20240101T000000Z"' in req.body_text
    assert 'end="20240131T000000Z"' in req.body_text


async def test_caldav_all_day_detection() -> None:
    body = """<D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
<D:response><D:propstat><D:prop><C:calendar-data>BEGIN:VEVENT
UID:allday
SUMMARY:Holiday
DTSTART;VALUE=DATE:20240701
DTEND;VALUE=DATE:20240702
END:VEVENT</C:calendar-data></D:prop></D:propstat></D:response>
</D:multistatus>"""
    f = InMemoryHttpFetcher().on_method("REPORT", HttpResponse(207, body))
    conn = _caldav(f)
    events = await conn.list_events_async(
        datetime(2024, 1, 1, tzinfo=timezone.utc),
        datetime(2024, 12, 31, tzinfo=timezone.utc),
    )
    assert events[0].is_all_day is True
    assert events[0].description is None
    assert events[0].location is None


async def test_caldav_create_event_puts_ics() -> None:
    f = InMemoryHttpFetcher().on_method("PUT", HttpResponse(201, ""))
    conn = _caldav(f)
    ev = CalendarEvent(
        event_id="my-uid",
        calendar_id="cal",
        title="New, Event; test",
        description="desc",
        location="loc",
        start_utc=datetime(2024, 6, 1, 12, 0, tzinfo=timezone.utc),
        end_utc=datetime(2024, 6, 1, 13, 0, tzinfo=timezone.utc),
        is_all_day=False,
        attendees=(),
    )
    created = await conn.create_event_async(ev)
    assert created.event_id == "my-uid"
    req = f.last_request
    assert req.method == "PUT"
    assert req.url == "https://dav.example.com/cal/my-uid.ics"
    assert req.headers["If-None-Match"] == "*"
    assert "UID:my-uid" in req.body_text
    assert "DTSTART:20240601T120000Z" in req.body_text
    # comma / semicolon escaped per RFC 5545.
    assert "SUMMARY:New\\, Event\\; test" in req.body_text


async def test_caldav_delete_tolerates_404() -> None:
    f = InMemoryHttpFetcher().on_method("DELETE", HttpResponse(404, ""))
    conn = _caldav(f)
    await conn.delete_event_async("cal", "gone")  # must not raise
    assert f.last_request.url == "https://dav.example.com/cal/gone.ics"


async def test_caldav_delete_requires_event_id() -> None:
    conn = _caldav(InMemoryHttpFetcher())
    with pytest.raises(ValueError):
        await conn.delete_event_async("cal", "")


# -- Google ----------------------------------------------------------------


async def test_google_list_events_parses_items() -> None:
    payload = {
        "items": [
            {
                "id": "g1",
                "status": "confirmed",
                "summary": "Lunch",
                "description": "with team",
                "location": "Cafe",
                "start": {"dateTime": "2024-03-01T12:00:00Z"},
                "end": {"dateTime": "2024-03-01T13:00:00Z"},
                "attendees": [{"email": "a@x.com"}, {"email": "b@x.com"}],
            },
            {"id": "g2", "status": "cancelled"},  # skipped
            {
                "id": "g3",
                "summary": "All day",
                "start": {"date": "2024-03-02"},
                "end": {"date": "2024-03-03"},
            },
        ]
    }
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps(payload)))
    conn = GoogleCalendarConnector(GoogleCalendarOptions(_token("tok")), f)
    assert conn.provider_id == "google-calendar"
    assert conn.is_configured is True
    events = await conn.list_events_async(
        datetime(2024, 3, 1, tzinfo=timezone.utc),
        datetime(2024, 3, 31, tzinfo=timezone.utc),
    )
    assert [e.event_id for e in events] == ["g1", "g3"]
    assert events[0].title == "Lunch"
    assert list(events[0].attendees) == ["a@x.com", "b@x.com"]
    assert events[0].is_all_day is False
    assert events[1].is_all_day is True
    assert events[1].start_utc.isoformat() == "2024-03-02T00:00:00+00:00"
    assert f.last_request.headers["Authorization"] == "Bearer tok"


async def test_google_missing_token_raises() -> None:
    conn = GoogleCalendarConnector(GoogleCalendarOptions(_token(None)), InMemoryHttpFetcher())
    with pytest.raises(RuntimeError):
        await conn.list_events_async(
            datetime(2024, 1, 1, tzinfo=timezone.utc),
            datetime(2024, 1, 2, tzinfo=timezone.utc),
        )


async def test_google_create_event_reads_back_id() -> None:
    f = InMemoryHttpFetcher().on_method("POST", HttpResponse(200, json.dumps({"id": "new-id"})))
    conn = GoogleCalendarConnector(GoogleCalendarOptions(_token("tok"), "primary"), f)
    ev = CalendarEvent(
        event_id="",
        calendar_id="primary",
        title="Meeting",
        description=None,
        location=None,
        start_utc=datetime(2024, 3, 1, 9, 0, tzinfo=timezone.utc),
        end_utc=datetime(2024, 3, 1, 10, 0, tzinfo=timezone.utc),
        is_all_day=False,
        attendees=("x@y.com",),
    )
    created = await conn.create_event_async(ev)
    assert created.event_id == "new-id"
    body = f.last_request.body_json
    assert body["summary"] == "Meeting"
    assert body["attendees"] == [{"email": "x@y.com"}]
    assert body["start"]["timeZone"] == "UTC"


async def test_google_delete_requires_ids() -> None:
    conn = GoogleCalendarConnector(GoogleCalendarOptions(_token("tok")), InMemoryHttpFetcher())
    with pytest.raises(ValueError):
        await conn.delete_event_async("", "e")
    with pytest.raises(ValueError):
        await conn.delete_event_async("c", "")


# -- Microsoft Graph -------------------------------------------------------


async def test_msgraph_list_events_parses_value() -> None:
    payload = {
        "value": [
            {
                "id": "m1",
                "subject": "Review",
                "bodyPreview": "quarterly",
                "isAllDay": False,
                "location": {"displayName": "HQ"},
                "start": {"dateTime": "2024-03-01T14:00:00"},
                "end": {"dateTime": "2024-03-01T15:00:00"},
                "attendees": [
                    {"emailAddress": {"address": "p@q.com"}},
                ],
            }
        ]
    }
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps(payload)))
    conn = MsGraphCalendarConnector(MsGraphCalendarOptions(_token("tok")), f)
    assert conn.provider_id == "ms-graph-calendar"
    events = await conn.list_events_async(
        datetime(2024, 3, 1, tzinfo=timezone.utc),
        datetime(2024, 3, 31, tzinfo=timezone.utc),
    )
    e = events[0]
    assert e.event_id == "m1"
    assert e.title == "Review"
    assert e.description == "quarterly"
    assert e.location == "HQ"
    assert list(e.attendees) == ["p@q.com"]
    # Graph dateTime has no offset; AssumeUniversal -> UTC.
    assert e.start_utc.isoformat() == "2024-03-01T14:00:00+00:00"


async def test_msgraph_create_reads_back_id() -> None:
    f = InMemoryHttpFetcher().on_method("POST", HttpResponse(201, json.dumps({"id": "graph-new"})))
    conn = MsGraphCalendarConnector(MsGraphCalendarOptions(_token("tok")), f)
    ev = CalendarEvent(
        event_id="",
        calendar_id="primary",
        title="Sync",
        description="agenda",
        location="Room",
        start_utc=datetime(2024, 3, 1, 9, 0, tzinfo=timezone.utc),
        end_utc=datetime(2024, 3, 1, 10, 0, tzinfo=timezone.utc),
        is_all_day=False,
        attendees=("a@b.com",),
    )
    created = await conn.create_event_async(ev)
    assert created.event_id == "graph-new"
    body = f.last_request.body_json
    assert body["subject"] == "Sync"
    assert body["body"] == {"contentType": "text", "content": "agenda"}
    assert body["attendees"][0]["emailAddress"]["address"] == "a@b.com"
    assert f.last_request.url.endswith("me/events")


async def test_msgraph_delete_tolerates_204() -> None:
    f = InMemoryHttpFetcher().on_method("DELETE", HttpResponse(204, ""))
    conn = MsGraphCalendarConnector(MsGraphCalendarOptions(_token("tok")), f)
    await conn.delete_event_async("primary", "m1")  # must not raise
    assert f.last_request.url.endswith("me/events/m1")
