# ms_graph_calendar_connector.py
#
# Port of CircleAI.Integration.Calendar/MsGraphCalendarConnector.cs (C# — the
# EXACT spec).
#
# (Phase B1) Microsoft Graph 1.0 client for Outlook / Microsoft 365 calendar.
# Same shape as the Google connector — host supplies access tokens via callback.
#
# The C# takes an injected ``HttpClient`` (base address
# https://graph.microsoft.com/v1.0/) and an ``AccessTokenProvider`` callback;
# the Python port takes an injected :class:`IHttpFetcher`, builds absolute URLs
# against that base, and attaches the Bearer header per request.

from __future__ import annotations

from dataclasses import dataclass, replace
from datetime import datetime, timezone
from typing import Awaitable, Callable, List, Optional
from urllib.parse import quote

from circle_ai.integration._util import DATETIME_MIN, parse_utc, to_iso_o
from circle_ai.integration.contracts import CalendarEvent, ICalendarConnector
from circle_ai.integration.http import HttpRequest, IHttpFetcher

_BASE_URI = "https://graph.microsoft.com/v1.0/"

AccessTokenProvider = Callable[[], Awaitable[Optional[str]]]


@dataclass(frozen=True, slots=True)
class MsGraphCalendarOptions:
    """Mirrors ``CircleAI.Integration.Calendar.MsGraphCalendarOptions`` —
    ``record(Func<CancellationToken, ValueTask<string?>> AccessTokenProvider,
    string CalendarId = "primary")``.
    """

    access_token_provider: AccessTokenProvider
    calendar_id: str = "primary"


class MsGraphCalendarConnector(ICalendarConnector):
    """Port of ``CircleAI.Integration.Calendar.MsGraphCalendarConnector``."""

    def __init__(self, opts: MsGraphCalendarOptions, http: IHttpFetcher) -> None:
        if opts is None:
            raise ValueError("opts must not be None")
        if http is None:
            raise ValueError("http must not be None")
        self._opts = opts
        self._http = http

    @property
    def provider_id(self) -> str:
        return "ms-graph-calendar"

    @property
    def is_configured(self) -> bool:
        return self._opts.access_token_provider is not None

    async def _ensure_auth(self) -> str:
        token = await self._opts.access_token_provider()
        if not (token and token.strip()):
            raise RuntimeError(
                "Microsoft Graph access token unavailable; refresh OAuth."
            )
        return f"Bearer {token}"

    async def list_events_async(
        self, from_utc: datetime, to_utc: datetime
    ) -> List[CalendarEvent]:
        auth = await self._ensure_auth()
        path = (
            "me/calendar/calendarView"
            f"?startDateTime={quote(to_iso_o(from_utc), safe='')}"
            f"&endDateTime={quote(to_iso_o(to_utc), safe='')}"
            "&$top=250&$orderby=start/dateTime"
        )
        resp = (
            await self._http.send_async(
                HttpRequest("GET", _BASE_URI + path, {"Authorization": auth})
            )
        ).ensure_success()
        root = resp.json()

        result: List[CalendarEvent] = []
        arr = root.get("value") if isinstance(root, dict) else None
        if isinstance(arr, list):
            for ev in arr:
                if not isinstance(ev, dict):
                    continue
                attendees: List[str] = []
                atts = ev.get("attendees")
                if isinstance(atts, list):
                    for a in atts:
                        if not isinstance(a, dict):
                            continue
                        ea = a.get("emailAddress")
                        if isinstance(ea, dict) and "address" in ea:
                            attendees.append(ea.get("address") or "")
                start_utc = _parse_graph_time(ev, "start")
                end_utc = _parse_graph_time(ev, "end")
                all_day = bool(ev.get("isAllDay")) if "isAllDay" in ev else False
                location = None
                loc = ev.get("location")
                if isinstance(loc, dict) and "displayName" in loc:
                    location = loc.get("displayName")
                result.append(
                    CalendarEvent(
                        event_id=ev.get("id") or "",
                        calendar_id=self._opts.calendar_id,
                        title=ev.get("subject") or "",
                        description=ev.get("bodyPreview")
                        if "bodyPreview" in ev
                        else None,
                        location=location,
                        start_utc=start_utc,
                        end_utc=end_utc,
                        is_all_day=all_day,
                        attendees=attendees,
                    )
                )
        return result

    async def create_event_async(self, ev: CalendarEvent) -> CalendarEvent:
        if ev is None:
            raise ValueError("ev must not be None")
        auth = await self._ensure_auth()
        body = {
            "subject": ev.title,
            "body": {"contentType": "text", "content": ev.description or ""},
            "start": {"dateTime": _utc_iso(ev.start_utc), "timeZone": "UTC"},
            "end": {"dateTime": _utc_iso(ev.end_utc), "timeZone": "UTC"},
            "isAllDay": ev.is_all_day,
            "location": {"displayName": ev.location or ""},
            "attendees": [
                {"emailAddress": {"address": a}, "type": "required"}
                for a in ev.attendees
            ],
        }
        resp = (
            await self._http.send_async(
                HttpRequest(
                    "POST", _BASE_URI + "me/events", {"Authorization": auth},
                    body_json=body,
                )
            )
        ).ensure_success()
        root = resp.json()
        return replace(ev, event_id=root.get("id") or "")

    async def delete_event_async(self, calendar_id: str, event_id: str) -> None:
        if not (event_id and event_id.strip()):
            raise ValueError("eventId required")
        auth = await self._ensure_auth()
        resp = await self._http.send_async(
            HttpRequest(
                "DELETE",
                _BASE_URI + "me/events/" + quote(event_id, safe=""),
                {"Authorization": auth},
            )
        )
        # NoContent (204) is tolerated; anything else must be 2xx.
        if resp.status_code != 204:
            resp.ensure_success()


def _parse_graph_time(parent: dict, prop: str) -> datetime:
    """Mirror the C# ``ParseGraphTime`` helper — read node.dateTime, assume UTC."""
    node = parent.get(prop)
    if not isinstance(node, dict):
        return DATETIME_MIN
    dt = node.get("dateTime")
    if not dt:
        return DATETIME_MIN
    return parse_utc(dt)


def _utc_iso(dt: datetime) -> str:
    """C# ``ev.StartUtc.UtcDateTime.ToString("O")`` — the UTC wall-clock, ISO."""
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    naive = dt.astimezone(timezone.utc).replace(tzinfo=None)
    return naive.isoformat()
