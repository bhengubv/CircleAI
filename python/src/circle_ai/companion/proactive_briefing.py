# companion/proactive_briefing.py
#
# The proactive-briefing hosted service. Ported from CircleAI.Companion
# (ProactiveBriefingService.cs) — the C# reference. It assembles a "what's
# happening" briefing from registered calendar / email / news / weather
# connectors, runs the result through an LLM for a friendly summary, and pushes
# the outcome through any registered notifier.
#
# The integration connector contracts it consumes (ICalendarConnector,
# IEmailConnector, INewsSource, IWeatherProvider and their records) mirror
# CircleAI.Integration.Contracts — reproduced here as the minimal surface the
# briefing service touches, so the service is faithfully portable without pulling
# the whole integration layer. The AI seam is an ``IAIService``-shaped object
# exposing ``chat_async(messages, ct=...) -> str``.
#
# Scheduling is the simplest possible cron — a list of UTC times-of-day at which
# the briefing fires (default 06:30 and 18:00 UTC). The loop is an asyncio task
# started/stopped by the ``IHostedService``-style start/stop methods.

from __future__ import annotations

import asyncio
import logging
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, time, timedelta, timezone
from typing import Iterable, List, Optional, Sequence

from ..models.models import ChatMessage

_LOG = logging.getLogger("circle_ai.companion.proactive_briefing")


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ── integration connector contracts (subset consumed by the briefing) ─────────
# These mirror CircleAI.Integration.Contracts. Only the members the briefing
# service reads are modelled.


@dataclass(frozen=True, slots=True)
class CalendarEvent:
    """Mirror of ``CircleAI.Integration.CalendarEvent`` (briefing-relevant fields)."""

    event_id: str
    calendar_id: str
    title: str
    description: Optional[str]
    location: Optional[str]
    start_utc: datetime
    end_utc: datetime
    is_all_day: bool
    attendees: Sequence[str]


class ICalendarConnector(ABC):
    """Mirror of ``CircleAI.Integration.ICalendarConnector`` (list surface)."""

    @property
    @abstractmethod
    def provider_id(self) -> str: ...

    @property
    @abstractmethod
    def is_configured(self) -> bool: ...

    @abstractmethod
    async def list_events_async(
        self, from_utc: datetime, to_utc: datetime, *, ct: Optional[object] = None
    ) -> Sequence[CalendarEvent]: ...


@dataclass(frozen=True, slots=True)
class EmailMessage:
    """Mirror of ``CircleAI.Integration.EmailMessage`` (briefing-relevant fields)."""

    message_id: str
    from_: str
    to: Sequence[str]
    subject: str
    body_text: str
    received_utc: datetime
    unread: bool
    labels: Sequence[str]


class IEmailConnector(ABC):
    """Mirror of ``CircleAI.Integration.IEmailConnector`` (unread surface)."""

    @property
    @abstractmethod
    def provider_id(self) -> str: ...

    @property
    @abstractmethod
    def is_configured(self) -> bool: ...

    @abstractmethod
    async def list_unread_async(
        self, max: int, *, ct: Optional[object] = None
    ) -> Sequence[EmailMessage]: ...


@dataclass(frozen=True, slots=True)
class NewsItem:
    """Mirror of ``CircleAI.Integration.NewsItem`` (briefing-relevant fields)."""

    item_id: str
    source_id: str
    title: str
    summary: str
    url: str
    published_utc: datetime
    tags: Sequence[str]


class INewsSource(ABC):
    """Mirror of ``CircleAI.Integration.INewsSource``."""

    @property
    @abstractmethod
    def source_id(self) -> str: ...

    @property
    @abstractmethod
    def is_configured(self) -> bool: ...

    @abstractmethod
    async def fetch_latest_async(
        self, max: int, *, ct: Optional[object] = None
    ) -> Sequence[NewsItem]: ...


@dataclass(frozen=True, slots=True)
class WeatherSample:
    """Mirror of ``CircleAI.Integration.WeatherSample`` (briefing-relevant fields)."""

    at_utc: datetime
    temp_c: float
    feels_like_c: float
    precip_mm: float
    wind_kph: float
    cloud_pct: int
    condition: str


class IWeatherProvider(ABC):
    """Mirror of ``CircleAI.Integration.IWeatherProvider`` (current surface)."""

    @property
    @abstractmethod
    def provider_id(self) -> str: ...

    @abstractmethod
    async def current_async(
        self, lat: float, lon: float, *, ct: Optional[object] = None
    ) -> WeatherSample: ...


# ── the LLM seam ──────────────────────────────────────────────────────────────


class IAIService(ABC):
    """The briefing's LLM seam — mirrors the ``CircleAI.Inference.IAIService`` surface it uses."""

    @abstractmethod
    async def chat_async(
        self, messages: Sequence[ChatMessage], *, ct: Optional[object] = None
    ) -> str: ...


# ── options + notifier ────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class ProactiveBriefingOptions:
    """Configuration knobs for :class:`ProactiveBriefingService`.

    Mirrors ``CircleAI.Companion.ProactiveBriefingOptions``. ``fire_times_utc`` is
    a list of times-of-day (default 06:30 and 18:00 UTC).
    """

    fire_times_utc: Sequence[time] = field(
        default_factory=lambda: (time(6, 30, 0), time(18, 0, 0))
    )
    latitude: Optional[float] = None
    longitude: Optional[float] = None
    headline: str = "Your briefing"
    delivery_address: Optional[str] = None


class IBriefingNotifier(ABC):
    """Pluggable notifier — hosts wire WhatsApp, Telegram, SMS, push, etc.

    Mirrors ``CircleAI.Companion.IBriefingNotifier``.
    """

    @abstractmethod
    async def deliver_async(
        self,
        headline: str,
        body: str,
        address: Optional[str],
        *,
        ct: Optional[object] = None,
    ) -> None: ...


def _time_of_day_seconds(t: time) -> float:
    return t.hour * 3600 + t.minute * 60 + t.second + t.microsecond / 1_000_000.0


class ProactiveBriefingService:
    """Scheduled service that assembles, summarises, and delivers a briefing.

    Mirrors ``CircleAI.Companion.ProactiveBriefingService`` (an
    ``IHostedService`` + ``IAsyncDisposable``). ``start_async`` launches the loop;
    ``stop_async`` cancels it; ``fire_once_async`` performs one assemble → LLM →
    deliver pass. ``time_until_next_fire`` is exposed (internal in C#) for tests.
    """

    def __init__(
        self,
        opts: ProactiveBriefingOptions,
        *,
        calendars: Optional[Iterable[ICalendarConnector]] = None,
        emails: Optional[Iterable[IEmailConnector]] = None,
        news: Optional[Iterable[INewsSource]] = None,
        weather: Optional[IWeatherProvider] = None,
        notifiers: Optional[Iterable[IBriefingNotifier]] = None,
        ai: Optional[IAIService] = None,
        now_provider=None,
    ) -> None:
        if opts is None:
            raise ValueError("opts required")
        self._opts = opts
        self._calendars: List[ICalendarConnector] = list(calendars) if calendars else []
        self._emails: List[IEmailConnector] = list(emails) if emails else []
        self._news: List[INewsSource] = list(news) if news else []
        self._weather = weather
        self._notifiers: List[IBriefingNotifier] = list(notifiers) if notifiers else []
        self._ai = ai
        self._now = now_provider or _utc_now
        self._task: Optional[asyncio.Task] = None
        self._cancel: Optional[asyncio.Event] = None

    # ── hosted-service lifecycle ─────────────────────────────────────────────

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        if self._task is not None:
            return
        self._cancel = asyncio.Event()
        self._task = asyncio.ensure_future(self._loop_async(self._cancel))
        _LOG.info(
            "[ProactiveBriefingService] started with %d fire-time(s).",
            len(self._opts.fire_times_utc),
        )

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        if self._cancel is None:
            return
        self._cancel.set()
        task = self._task
        if task is not None:
            task.cancel()
            try:
                await task
            except asyncio.CancelledError:
                pass  # expected
        self._task = None
        self._cancel = None
        _LOG.info("[ProactiveBriefingService] stopped.")

    async def dispose_async(self) -> None:
        await self.stop_async()

    async def __aenter__(self) -> "ProactiveBriefingService":
        await self.start_async()
        return self

    async def __aexit__(self, *exc) -> None:
        await self.dispose_async()

    async def _loop_async(self, cancel: asyncio.Event) -> None:
        try:
            while not cancel.is_set():
                sleep = self.time_until_next_fire(self._now())
                try:
                    await asyncio.wait_for(cancel.wait(), timeout=sleep.total_seconds())
                    return  # cancelled
                except asyncio.TimeoutError:
                    pass  # slept the full interval -> fire
                try:
                    await self.fire_once_async()
                except Exception:  # noqa: BLE001 — matches C#: log + keep looping
                    _LOG.warning("[ProactiveBriefingService] fire failed", exc_info=True)
        except asyncio.CancelledError:
            return

    # ── scheduling ───────────────────────────────────────────────────────────

    def time_until_next_fire(self, now: datetime) -> timedelta:
        """Time until the next configured fire moment. Always > 30 s to avoid double-fires.

        Mirrors ``CircleAI.Companion.ProactiveBriefingService.TimeUntilNextFire``.
        """
        if len(self._opts.fire_times_utc) == 0:
            return timedelta(hours=1)
        if now.tzinfo is None:
            now = now.replace(tzinfo=timezone.utc)
        now = now.astimezone(timezone.utc)
        today_base = datetime(now.year, now.month, now.day, tzinfo=timezone.utc)
        best: Optional[timedelta] = None
        for tod in self._opts.fire_times_utc:
            candidate = today_base + timedelta(seconds=_time_of_day_seconds(tod))
            if candidate <= now + timedelta(seconds=30):
                candidate = candidate + timedelta(days=1)
            gap = candidate - now
            if best is None or gap < best:
                best = gap
        return best if best is not None else timedelta(hours=1)

    # ── the fire ─────────────────────────────────────────────────────────────

    async def fire_once_async(self, *, ct: Optional[object] = None) -> None:
        """Assemble the briefing context, summarise via the LLM, deliver.

        Mirrors ``CircleAI.Companion.ProactiveBriefingService.FireOnceAsync``.
        """
        ctx_parts: List[str] = []
        now = self._now()

        # Calendar — next 24 hours.
        for cal in [c for c in self._calendars if c.is_configured]:
            try:
                events = await cal.list_events_async(now, now + timedelta(hours=24), ct=ct)
                if len(events) > 0:
                    ctx_parts.append(f"### Calendar ({cal.provider_id})")
                    ordered = sorted(events, key=lambda e: e.start_utc)[:8]
                    for e in ordered:
                        loc = "" if not e.location else " @ " + e.location
                        ctx_parts.append(f"- {_local_hm(e.start_utc)} {e.title}{loc}")
            except Exception:  # noqa: BLE001
                _LOG.debug("[briefing] calendar %s skipped", cal.provider_id, exc_info=True)

        # Email — unread.
        for em in [c for c in self._emails if c.is_configured]:
            try:
                unread = await em.list_unread_async(5, ct=ct)
                if len(unread) > 0:
                    ctx_parts.append(f"### Unread email ({em.provider_id})")
                    for m in unread:
                        ctx_parts.append(f"- {m.from_}: {m.subject}")
            except Exception:  # noqa: BLE001
                _LOG.debug("[briefing] email %s skipped", em.provider_id, exc_info=True)

        # News — latest from each source.
        for src in [s for s in self._news if s.is_configured]:
            try:
                items = await src.fetch_latest_async(5, ct=ct)
                if len(items) > 0:
                    ctx_parts.append(f"### News ({src.source_id})")
                    for i in items:
                        ctx_parts.append(f"- {i.title}")
            except Exception:  # noqa: BLE001
                _LOG.debug("[briefing] news %s skipped", src.source_id, exc_info=True)

        # Weather — if location configured.
        if self._weather is not None and self._opts.latitude is not None and self._opts.longitude is not None:
            try:
                w = await self._weather.current_async(
                    self._opts.latitude, self._opts.longitude, ct=ct
                )
                ctx_parts.append(f"### Weather ({self._weather.provider_id})")
                ctx_parts.append(
                    f"- {_f0(w.temp_c)}°C {w.condition}, feels {_f0(w.feels_like_c)}°C, "
                    f"wind {_f0(w.wind_kph)} km/h"
                )
            except Exception:  # noqa: BLE001
                _LOG.debug("[briefing] weather skipped", exc_info=True)

        if len(ctx_parts) == 0:
            _LOG.debug("[ProactiveBriefingService] no signals; skipping fire")
            return

        context = "\n".join(ctx_parts)
        prompt = (
            "Summarise the user's morning briefing in 80 words or less. Warm but "
            "factual. End with the one thing they should do first today.\n\n" + context
        )

        if self._ai is not None:
            try:
                summary = await self._ai.chat_async([ChatMessage("user", prompt)], ct=ct)
            except Exception:  # noqa: BLE001
                _LOG.warning(
                    "[briefing] AI summarisation failed; sending raw context", exc_info=True
                )
                summary = context
        else:
            summary = context

        for notifier in self._notifiers:
            try:
                await notifier.deliver_async(
                    self._opts.headline, summary, self._opts.delivery_address, ct=ct
                )
            except Exception:  # noqa: BLE001
                _LOG.warning("[briefing] notifier failed", exc_info=True)


def _local_hm(dt: datetime) -> str:
    """``StartUtc.ToLocalTime():HH:mm`` — local wall-clock hour:minute."""
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    local = dt.astimezone()
    return f"{local.hour:02d}:{local.minute:02d}"


def _f0(x: float) -> str:
    """``{x:F0}`` — .NET Core 3.0+ formats floats with round-half-to-even."""
    return str(int(_bankers_round(x)))


def _bankers_round(x: float) -> float:
    import math

    floor = math.floor(x)
    diff = x - floor
    if diff < 0.5:
        return float(floor)
    if diff > 0.5:
        return float(floor + 1)
    # exactly .5 -> round to even
    return float(floor if floor % 2 == 0 else floor + 1)


__all__ = [
    "CalendarEvent",
    "ICalendarConnector",
    "EmailMessage",
    "IEmailConnector",
    "NewsItem",
    "INewsSource",
    "WeatherSample",
    "IWeatherProvider",
    "IAIService",
    "ProactiveBriefingOptions",
    "IBriefingNotifier",
    "ProactiveBriefingService",
]
