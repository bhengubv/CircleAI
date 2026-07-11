# caldav_calendar_connector.py
#
# Port of CircleAI.Integration.Calendar/CalDavCalendarConnector.cs (C# — the
# EXACT spec).
#
# (Phase B1) Generic CalDAV connector — covers iCloud, Fastmail, Posteo,
# Nextcloud, ownCloud, every other CalDAV server. Authenticates via HTTP Basic
# (or app-specific password) and uses the standard CalDAV REPORT verb to fetch
# events in a time range.
#
# This is a deliberately small, dependency-free CalDAV client (no recurrence
# expansion / ACL / etag semantics) — sufficient for the Companion's read-mostly
# workload, matching the C#.
#
# The C# takes an injected ``HttpClient`` (Basic auth header set from user/pass);
# the Python port takes an injected :class:`IHttpFetcher` and attaches the same
# Basic header per request. XML is served as response text and parsed with
# :mod:`xml.etree.ElementTree`; the minimal ICS parser mirrors the C# regexes.

from __future__ import annotations

import base64
import re
import uuid
from dataclasses import dataclass, replace
from datetime import datetime, timezone
from typing import Iterator, List, Optional
from xml.etree import ElementTree as ET

from circle_ai.integration._util import parse_ics_time
from circle_ai.integration.contracts import (
    DATETIME_MIN,
    CalendarEvent,
    ICalendarConnector,
)
from circle_ai.integration.http import HttpRequest, IHttpFetcher

_CALDAV_NS = "{urn:ietf:params:xml:ns:caldav}"
_RX_EVENT = re.compile(r"BEGIN:VEVENT(?P<body>.*?)END:VEVENT", re.DOTALL)


@dataclass(frozen=True, slots=True)
class CalDavCalendarOptions:
    """Mirrors ``CircleAI.Integration.Calendar.CalDavCalendarOptions`` —
    ``record(Uri CalendarUri, string Username, string Password)``.

    ``calendar_uri`` is the full URL of the calendar collection (a plain
    ``str``).
    """

    calendar_uri: str
    username: str
    password: str


class CalDavCalendarConnector(ICalendarConnector):
    """Port of ``CircleAI.Integration.Calendar.CalDavCalendarConnector``."""

    def __init__(self, opts: CalDavCalendarOptions, http: IHttpFetcher) -> None:
        if opts is None:
            raise ValueError("opts must not be None")
        if http is None:
            raise ValueError("http must not be None")
        self._opts = opts
        self._http = http
        creds = base64.b64encode(
            f"{opts.username}:{opts.password}".encode("utf-8")
        ).decode("ascii")
        self._auth = f"Basic {creds}"

    @property
    def provider_id(self) -> str:
        return "caldav"

    @property
    def is_configured(self) -> bool:
        return bool(self._opts.username and self._opts.username.strip()) and bool(
            self._opts.password and self._opts.password.strip()
        )

    async def list_events_async(
        self, from_utc: datetime, to_utc: datetime
    ) -> List[CalendarEvent]:
        # CalDAV REPORT with a time-range filter (start/end in yyyyMMddTHHmmssZ).
        f = _caldav_stamp(from_utc)
        t = _caldav_stamp(to_utc)
        xml = (
            '<?xml version="1.0" encoding="utf-8" ?>\n'
            '<C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">\n'
            "  <D:prop>\n"
            "    <D:getetag/>\n"
            "    <C:calendar-data/>\n"
            "  </D:prop>\n"
            "  <C:filter>\n"
            '    <C:comp-filter name="VCALENDAR">\n'
            '      <C:comp-filter name="VEVENT">\n'
            f'        <C:time-range start="{f}" end="{t}"/>\n'
            "      </C:comp-filter>\n"
            "    </C:comp-filter>\n"
            "  </C:filter>\n"
            "</C:calendar-query>"
        )
        headers = {"Authorization": self._auth, "Depth": "1"}
        resp = (
            await self._http.send_async(
                HttpRequest("REPORT", self._opts.calendar_uri, headers, body_text=xml)
            )
        ).ensure_success()

        result: List[CalendarEvent] = []
        root = ET.fromstring(resp.text)
        for cal_data in root.iter(_CALDAV_NS + "calendar-data"):
            for ev in _parse_ics(cal_data.text or "", self._opts.calendar_uri):
                result.append(ev)
        return result

    async def create_event_async(self, ev: CalendarEvent) -> CalendarEvent:
        if ev is None:
            raise ValueError("ev must not be None")
        uid = (
            ev.event_id
            if (ev.event_id and ev.event_id.strip())
            else uuid.uuid4().hex
        )
        ics = _build_ics(replace(ev, event_id=uid))
        target_uri = _combine(self._opts.calendar_uri, uid + ".ics")
        headers = {"Authorization": self._auth, "If-None-Match": "*"}
        resp = await self._http.send_async(
            HttpRequest("PUT", target_uri, headers, body_text=ics)
        )
        resp.ensure_success()
        return replace(ev, event_id=uid)

    async def delete_event_async(self, calendar_id: str, event_id: str) -> None:
        if not (event_id and event_id.strip()):
            raise ValueError("eventId required")
        target_uri = _combine(self._opts.calendar_uri, event_id + ".ics")
        headers = {"Authorization": self._auth}
        resp = await self._http.send_async(HttpRequest("DELETE", target_uri, headers))
        # NoContent / OK / NotFound are tolerated; anything else must be 2xx.
        if resp.status_code not in (204, 200, 404):
            resp.ensure_success()


def _caldav_stamp(dt: datetime) -> str:
    """C# ``{fromUtc:yyyyMMddTHHmmssZ}`` — format a UTC instant for the filter."""
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def _combine(base: str, rel: str) -> str:
    """Mirror ``new Uri(baseUri, rel)`` for a collection URI ending in '/'."""
    if base.endswith("/"):
        return base + rel
    # Uri combine replaces the last path segment if base has no trailing slash.
    idx = base.rfind("/")
    if idx < 0:
        return base + "/" + rel
    return base[: idx + 1] + rel


def _parse_ics(ics: str, calendar_id: str) -> Iterator[CalendarEvent]:
    if not (ics and ics.strip()):
        return
    for m in _RX_EVENT.finditer(ics):
        body = m.group("body")

        def get(key: str) -> str:
            line = re.search(
                rf"(?m)^{re.escape(key)}(?:;[^:]*)?:(.*)$", body
            )
            return line.group(1).strip() if line else ""

        def time(key: str) -> datetime:
            v = get(key)
            if not v:
                return DATETIME_MIN
            return parse_ics_time(v)

        uid = get("UID")
        title = get("SUMMARY")
        desc = get("DESCRIPTION")
        loc = get("LOCATION")
        start_utc = time("DTSTART")
        end_utc = time("DTEND")
        is_all_day = (
            start_utc != DATETIME_MIN
            and _time_of_day_zero(start_utc)
            and _time_of_day_zero(end_utc)
        )
        yield CalendarEvent(
            event_id=uid,
            calendar_id=calendar_id,
            title=title,
            description=None if not desc else desc,
            location=None if not loc else loc,
            start_utc=start_utc,
            end_utc=end_utc,
            is_all_day=is_all_day,
            attendees=(),
        )


def _time_of_day_zero(dt: datetime) -> bool:
    return dt.hour == 0 and dt.minute == 0 and dt.second == 0 and dt.microsecond == 0


def _build_ics(ev: CalendarEvent) -> str:
    dt_stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    dt_start = _to_ics_utc(ev.start_utc)
    dt_end = _to_ics_utc(ev.end_utc)
    lines = [
        "BEGIN:VCALENDAR",
        "VERSION:2.0",
        "PRODID:-//CircleAI//Calendar//EN",
        "BEGIN:VEVENT",
        f"UID:{ev.event_id}",
        f"DTSTAMP:{dt_stamp}",
        f"DTSTART:{dt_start}",
        f"DTEND:{dt_end}",
        f"SUMMARY:{_escape(ev.title)}",
    ]
    if ev.description:
        lines.append(f"DESCRIPTION:{_escape(ev.description)}")
    if ev.location:
        lines.append(f"LOCATION:{_escape(ev.location)}")
    lines.append("END:VEVENT")
    lines.append("END:VCALENDAR")
    # C# StringBuilder.AppendLine uses Environment.NewLine; join with "\r\n" plus
    # a trailing terminator to mirror ``AppendLine`` after the last line.
    return "\r\n".join(lines) + "\r\n"


def _to_ics_utc(dt: datetime) -> str:
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def _escape(s: str) -> str:
    return (
        s.replace("\\", "\\\\")
        .replace("\n", "\\n")
        .replace(",", "\\,")
        .replace(";", "\\;")
    )
