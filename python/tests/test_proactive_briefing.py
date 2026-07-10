"""test_proactive_briefing.py

Verifies the ProactiveBriefingService ported from CircleAI.Companion
(ProactiveBriefingService.cs): next-fire scheduling, context assembly from
calendar/email/news/weather connectors, LLM summarisation with raw-context
fallback, notifier delivery, and skip-on-no-signals.
"""
from __future__ import annotations

from datetime import datetime, time, timedelta, timezone
from typing import List, Sequence

import pytest

from circle_ai.companion.proactive_briefing import (
    CalendarEvent,
    EmailMessage,
    IAIService,
    IBriefingNotifier,
    ICalendarConnector,
    IEmailConnector,
    INewsSource,
    IWeatherProvider,
    NewsItem,
    ProactiveBriefingOptions,
    ProactiveBriefingService,
    WeatherSample,
)
from circle_ai.models.models import ChatMessage


def _utc(y, mo, d, h, mi) -> datetime:
    return datetime(y, mo, d, h, mi, tzinfo=timezone.utc)


# ── fakes ───────────────────────────────────────────────────────────────────


class FakeCalendar(ICalendarConnector):
    def __init__(self, events: Sequence[CalendarEvent], configured: bool = True) -> None:
        self._events = events
        self._configured = configured

    @property
    def provider_id(self) -> str:
        return "google"

    @property
    def is_configured(self) -> bool:
        return self._configured

    async def list_events_async(self, from_utc, to_utc, *, ct=None):
        return list(self._events)


class FakeEmail(IEmailConnector):
    def __init__(self, unread: Sequence[EmailMessage]) -> None:
        self._unread = unread

    @property
    def provider_id(self) -> str:
        return "gmail"

    @property
    def is_configured(self) -> bool:
        return True

    async def list_unread_async(self, max, *, ct=None):
        return list(self._unread)[:max]


class FakeNews(INewsSource):
    def __init__(self, items: Sequence[NewsItem]) -> None:
        self._items = items

    @property
    def source_id(self) -> str:
        return "hn"

    @property
    def is_configured(self) -> bool:
        return True

    async def fetch_latest_async(self, max, *, ct=None):
        return list(self._items)[:max]


class FakeWeather(IWeatherProvider):
    def __init__(self, sample: WeatherSample) -> None:
        self._sample = sample

    @property
    def provider_id(self) -> str:
        return "owm"

    async def current_async(self, lat, lon, *, ct=None):
        return self._sample


class CapturingAI(IAIService):
    def __init__(self, reply: str) -> None:
        self.reply = reply
        self.last_messages: List[ChatMessage] = []

    async def chat_async(self, messages, *, ct=None) -> str:
        self.last_messages = list(messages)
        return self.reply


class FailingAI(IAIService):
    async def chat_async(self, messages, *, ct=None) -> str:
        raise RuntimeError("model down")


class CapturingNotifier(IBriefingNotifier):
    def __init__(self) -> None:
        self.deliveries: List[tuple] = []

    async def deliver_async(self, headline, body, address, *, ct=None) -> None:
        self.deliveries.append((headline, body, address))


# ── scheduling ──────────────────────────────────────────────────────────────


def test_time_until_next_fire_picks_soonest() -> None:
    opts = ProactiveBriefingOptions(fire_times_utc=(time(6, 30), time(18, 0)))
    svc = ProactiveBriefingService(opts)
    # At 05:00, next fire is 06:30 -> 90 minutes.
    gap = svc.time_until_next_fire(_utc(2026, 7, 8, 5, 0))
    assert gap == timedelta(minutes=90)


def test_time_until_next_fire_rolls_past_today() -> None:
    opts = ProactiveBriefingOptions(fire_times_utc=(time(6, 30),))
    svc = ProactiveBriefingService(opts)
    # At 07:00, 06:30 already passed -> next is tomorrow 06:30 (23.5h).
    gap = svc.time_until_next_fire(_utc(2026, 7, 8, 7, 0))
    assert gap == timedelta(hours=23, minutes=30)


def test_time_until_next_fire_avoids_double_fire_window() -> None:
    opts = ProactiveBriefingOptions(fire_times_utc=(time(6, 30),))
    svc = ProactiveBriefingService(opts)
    # At exactly 06:30, the +30s guard rolls it to tomorrow.
    gap = svc.time_until_next_fire(_utc(2026, 7, 8, 6, 30))
    assert gap > timedelta(hours=23)


def test_no_fire_times_defaults_to_one_hour() -> None:
    svc = ProactiveBriefingService(ProactiveBriefingOptions(fire_times_utc=()))
    assert svc.time_until_next_fire(_utc(2026, 7, 8, 5, 0)) == timedelta(hours=1)


# ── fire_once assembly + summarise + deliver ────────────────────────────────


async def test_fire_assembles_context_and_delivers_summary() -> None:
    cal = FakeCalendar([
        CalendarEvent("e1", "c", "Standup", None, "Room 1", _utc(2026, 7, 8, 9, 0),
                      _utc(2026, 7, 8, 9, 30), False, []),
    ])
    email = FakeEmail([EmailMessage("m1", "boss@x.com", ["me"], "Budget", "…",
                                    _utc(2026, 7, 8, 8, 0), True, [])])
    news = FakeNews([NewsItem("n1", "hn", "Big story", "…", "http://x", _utc(2026, 7, 8, 7, 0), [])])
    ai = CapturingAI("Good morning! You have a standup at 9.")
    notifier = CapturingNotifier()
    opts = ProactiveBriefingOptions(headline="Brief", delivery_address="+27123")

    svc = ProactiveBriefingService(
        opts, calendars=[cal], emails=[email], news=[news], notifiers=[notifier], ai=ai
    )
    await svc.fire_once_async()

    assert len(notifier.deliveries) == 1
    headline, body, address = notifier.deliveries[0]
    assert headline == "Brief"
    assert address == "+27123"
    assert body == "Good morning! You have a standup at 9."
    # The prompt handed to the LLM should carry the assembled sections.
    prompt = ai.last_messages[0].content
    assert "### Calendar (google)" in prompt
    assert "Standup" in prompt
    assert "### Unread email (gmail)" in prompt
    assert "boss@x.com: Budget" in prompt
    assert "### News (hn)" in prompt


async def test_fire_falls_back_to_raw_context_when_ai_fails() -> None:
    news = FakeNews([NewsItem("n1", "hn", "Headline", "…", "http://x", _utc(2026, 7, 8, 7, 0), [])])
    notifier = CapturingNotifier()
    svc = ProactiveBriefingService(
        ProactiveBriefingOptions(), news=[news], notifiers=[notifier], ai=FailingAI()
    )
    await svc.fire_once_async()
    _, body, _ = notifier.deliveries[0]
    assert "### News (hn)" in body
    assert "Headline" in body


async def test_fire_without_ai_sends_raw_context() -> None:
    news = FakeNews([NewsItem("n1", "hn", "Headline", "…", "http://x", _utc(2026, 7, 8, 7, 0), [])])
    notifier = CapturingNotifier()
    svc = ProactiveBriefingService(
        ProactiveBriefingOptions(), news=[news], notifiers=[notifier]
    )
    await svc.fire_once_async()
    _, body, _ = notifier.deliveries[0]
    assert "Headline" in body


async def test_fire_skips_when_no_signals() -> None:
    notifier = CapturingNotifier()
    svc = ProactiveBriefingService(ProactiveBriefingOptions(), notifiers=[notifier])
    await svc.fire_once_async()
    assert notifier.deliveries == []


async def test_fire_includes_weather_when_located() -> None:
    weather = FakeWeather(WeatherSample(_utc(2026, 7, 8, 6, 0), 12.4, 10.6, 0.0, 8.0, 20, "Cloudy"))
    notifier = CapturingNotifier()
    opts = ProactiveBriefingOptions(latitude=-29.85, longitude=31.02)
    svc = ProactiveBriefingService(opts, weather=weather, notifiers=[notifier])
    await svc.fire_once_async()
    _, body, _ = notifier.deliveries[0]
    assert "### Weather (owm)" in body
    # F0 rounding: 12.4 -> 12, feels 10.6 -> 11, wind 8.0 -> 8.
    assert "12°C Cloudy, feels 11°C, wind 8 km/h" in body


async def test_unconfigured_connectors_skipped() -> None:
    cal = FakeCalendar([
        CalendarEvent("e1", "c", "X", None, None, _utc(2026, 7, 8, 9, 0),
                      _utc(2026, 7, 8, 9, 30), False, [])
    ], configured=False)
    notifier = CapturingNotifier()
    svc = ProactiveBriefingService(ProactiveBriefingOptions(), calendars=[cal], notifiers=[notifier])
    await svc.fire_once_async()
    assert notifier.deliveries == []  # nothing configured -> no signals


# ── lifecycle ───────────────────────────────────────────────────────────────


async def test_start_stop_idempotent() -> None:
    svc = ProactiveBriefingService(ProactiveBriefingOptions())
    await svc.start_async()
    await svc.start_async()  # no-op
    await svc.stop_async()
    await svc.stop_async()  # no-op


def test_rejects_none_options() -> None:
    with pytest.raises(ValueError):
        ProactiveBriefingService(None)  # type: ignore[arg-type]
