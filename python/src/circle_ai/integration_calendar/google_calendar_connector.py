# google_calendar_connector.py
#
# Port of CircleAI.Integration.Calendar/GoogleCalendarConnector.cs (C# — the
# EXACT spec).
#
# (Phase B1) Google Calendar v3 client using a host-supplied OAuth bearer token.
# The host owns the OAuth flow (web redirect, refresh, scope granting); this
# connector just lifts events through the v3 REST API.
#
# The C# takes an injected ``HttpClient`` (base address
# https://www.googleapis.com/calendar/v3/) and an ``AccessTokenProvider``
# callback; the Python port takes an injected :class:`IHttpFetcher`, builds
# absolute URLs against that base, and attaches the Bearer header per request.

from __future__ import annotations

from dataclasses import dataclass, replace
from datetime import datetime, timezone
from typing import Awaitable, Callable, List, Optional, Tuple
from urllib.parse import quote

from circle_ai.integration._util import (
    DATETIME_MIN,
    parse_date_only,
    parse_utc,
    to_iso_o,
)
from circle_ai.integration.contracts import CalendarEvent, ICalendarConnector
from circle_ai.integration.http import HttpRequest, IHttpFetcher

_BASE_URI = "https://www.googleapis.com/calendar/v3/"

AccessTokenProvider = Callable[[], Awaitable[Optional[str]]]


@dataclass(frozen=True, slots=True)
class GoogleCalendarOptions:
    """Mirrors ``CircleAI.Integration.Calendar.GoogleCalendarOptions`` —
    ``record(Func<CancellationToken, ValueTask<string?>> AccessTokenProvider,
    string CalendarId = "primary")``.

    ``access_token_provider`` is an async callable returning a fresh Bearer
    token (or ``None``).
    """

    access_token_provider: AccessTokenProvider
    calendar_id: str = "primary"


class GoogleCalendarConnector(ICalendarConnector):
    """Port of ``CircleAI.Integration.Calendar.GoogleCalendarConnector``."""

    def __init__(self, opts: GoogleCalendarOptions, http: IHttpFetcher) -> None:
        if opts is None:
            raise ValueError("opts must not be None")
        if http is None:
            raise ValueError("http must not be None")
        self._opts = opts
        self._http = http

    @property
    def provider_id(self) -> str:
        return "google-calendar"

    @property
    def is_configured(self) -> bool:
        return self._opts.access_token_provider is not None

    async def _ensure_auth(self) -> str:
        token = await self._opts.access_token_provider()
        if not (token and token.strip()):
            raise RuntimeError(
                "Google Calendar access token unavailable; refresh OAuth."
            )
        return f"Bearer {token}"

    async def list_events_async(
        self, from_utc: datetime, to_utc: datetime
    ) -> List[CalendarEvent]:
        auth = await self._ensure_auth()
        path = (
            f"calendars/{quote(self._opts.calendar_id, safe='')}/events"
            f"?timeMin={quote(to_iso_o(from_utc), safe='')}"
            f"&timeMax={quote(to_iso_o(to_utc), safe='')}"
            "&singleEvents=true&orderBy=startTime&maxResults=250"
        )
        resp = (
            await self._http.send_async(
                HttpRequest("GET", _BASE_URI + path, {"Authorization": auth})
            )
        ).ensure_success()
        root = resp.json()

        result: List[CalendarEvent] = []
        items = root.get("items") if isinstance(root, dict) else None
        if isinstance(items, list):
            for ev in items:
                if not isinstance(ev, dict):
                    continue
                if ev.get("status") == "cancelled":
                    continue
                start_utc, is_all_day = _parse_time(ev, "start")
                end_utc, _ = _parse_time(ev, "end")
                attendees: List[str] = []
                atts = ev.get("attendees")
                if isinstance(atts, list):
                    for a in atts:
                        if isinstance(a, dict) and "email" in a:
                            attendees.append(a.get("email") or "")
                result.append(
                    CalendarEvent(
                        event_id=ev.get("id") or "",
                        calendar_id=self._opts.calendar_id,
                        title=ev.get("summary") or "",
                        description=ev.get("description")
                        if "description" in ev
                        else None,
                        location=ev.get("location") if "location" in ev else None,
                        start_utc=start_utc,
                        end_utc=end_utc,
                        is_all_day=is_all_day,
                        attendees=attendees,
                    )
                )
        return result

    async def create_event_async(self, ev: CalendarEvent) -> CalendarEvent:
        if ev is None:
            raise ValueError("ev must not be None")
        auth = await self._ensure_auth()
        if ev.is_all_day:
            start = {"date": _utc_date(ev.start_utc)}
            end = {"date": _utc_date(ev.end_utc)}
        else:
            start = {"dateTime": to_iso_o(ev.start_utc), "timeZone": "UTC"}
            end = {"dateTime": to_iso_o(ev.end_utc), "timeZone": "UTC"}
        body = {
            "summary": ev.title,
            "description": ev.description,
            "location": ev.location,
            "start": start,
            "end": end,
            "attendees": [{"email": a} for a in ev.attendees],
        }
        resp = (
            await self._http.send_async(
                HttpRequest(
                    "POST",
                    _BASE_URI
                    + f"calendars/{quote(ev.calendar_id, safe='')}/events",
                    {"Authorization": auth},
                    body_json=body,
                )
            )
        ).ensure_success()
        root = resp.json()
        return replace(ev, event_id=root.get("id") or "")

    async def delete_event_async(self, calendar_id: str, event_id: str) -> None:
        if not (calendar_id and calendar_id.strip()):
            raise ValueError("calendarId required")
        if not (event_id and event_id.strip()):
            raise ValueError("eventId required")
        auth = await self._ensure_auth()
        resp = await self._http.send_async(
            HttpRequest(
                "DELETE",
                _BASE_URI
                + f"calendars/{quote(calendar_id, safe='')}/events/"
                + quote(event_id, safe=""),
                {"Authorization": auth},
            )
        )
        # NoContent (204) / Gone (410) are tolerated; anything else must be 2xx.
        if resp.status_code not in (204, 410):
            resp.ensure_success()


def _parse_time(parent: dict, prop: str) -> Tuple[datetime, bool]:
    """Mirror the C# ``ParseTime`` static helper — dateTime -> (utc, False),
    date -> (midnight-utc, True), else (MinValue, False).
    """
    node = parent.get(prop)
    if not isinstance(node, dict):
        return (DATETIME_MIN, False)
    dt = node.get("dateTime")
    if isinstance(dt, str):
        parsed = parse_utc(dt)
        if parsed != DATETIME_MIN:
            return (parsed, False)
    d = node.get("date")
    if isinstance(d, str):
        date = parse_date_only(d)
        if date is not None:
            return (date, True)
    return (DATETIME_MIN, False)


def _utc_date(dt: datetime) -> str:
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc).strftime("%Y-%m-%d")
