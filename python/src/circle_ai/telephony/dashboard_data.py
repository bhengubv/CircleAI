# dashboard_data.py
#
# Port of CircleAI.Telephony DashboardData.cs (C# — the EXACT spec).
#
# (3.3.0) Data model for the built-in voice-loop dashboard. Hosts can render this
# via any UI stack (Blazor, ASP.NET MVC, the existing BigBruh! console) — the
# contracts here describe what the dashboard needs to show: live calls, recent
# calls, agent health, cost spend, percentile latency.
#
# C# records -> frozen slotted dataclasses. C# Func<IReadOnlyList<T>> sync
# factories -> Callable[[], List[T]]. The composed data source's SnapshotAsync is
# async (ValueTask) even though the feeds are synchronous, matching the C#.

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta
from decimal import Decimal
from abc import ABC, abstractmethod
from typing import Callable, List, Optional

from .latency_tracker import LatencySnapshot
from .primitives import CallStatus


@dataclass(frozen=True, slots=True)
class LiveCallRow:
    """(3.3.0) One row in the live-calls panel."""

    call_id: str
    carrier: str
    from_: str
    to: str
    status: CallStatus
    started_at_utc: datetime
    duration: timedelta
    cost_so_far: Decimal


@dataclass(frozen=True, slots=True)
class RecentCallRow:
    """(3.3.0) One row in the recent-calls panel."""

    call_id: str
    carrier: str
    from_: str
    to: str
    final_status: CallStatus
    ended_at_utc: datetime
    duration: timedelta
    total_cost: Decimal


@dataclass(frozen=True, slots=True)
class AgentHealthRow:
    """(3.3.0) Agent health summary row.

    ``health``: "Healthy" / "Degraded" / "CoolingDown".
    """

    agent_label: str
    health: str
    consecutive_failures: int


@dataclass(frozen=True, slots=True)
class DashboardSummary:
    """(3.3.0) Top-of-page summary card."""

    live_call_count: int
    current_spend_usd: Decimal
    calls_last_24h: int
    pause_false_alarm_rate: float


@dataclass(frozen=True, slots=True)
class DashboardSnapshot:
    """(3.3.0) Full dashboard snapshot."""

    summary: DashboardSummary
    live_calls: List[LiveCallRow]
    recent_calls: List[RecentCallRow]
    agent_health: List[AgentHealthRow]
    latency_by_stage: List[LatencySnapshot]


class IDashboardDataSource(ABC):
    """(3.3.0) Dashboard data source: hosts compose live + recent + health +
    latency feeds."""

    @abstractmethod
    async def snapshot_async(self, *, ct: Optional[object] = None) -> DashboardSnapshot:
        ...


class DefaultDashboardDataSource(IDashboardDataSource):
    """(3.3.0) Default composed data source — pulls from supplied stores/services."""

    def __init__(
        self,
        live_calls: Callable[[], List[LiveCallRow]],
        recent_calls: Callable[[], List[RecentCallRow]],
        agent_health: Callable[[], List[AgentHealthRow]],
        latency: Callable[[], List[LatencySnapshot]],
        summary: Callable[[], DashboardSummary],
    ) -> None:
        if live_calls is None:
            raise ValueError("live_calls must not be None")
        if recent_calls is None:
            raise ValueError("recent_calls must not be None")
        if agent_health is None:
            raise ValueError("agent_health must not be None")
        if latency is None:
            raise ValueError("latency must not be None")
        if summary is None:
            raise ValueError("summary must not be None")
        self._live_calls = live_calls
        self._recent_calls = recent_calls
        self._agent_health = agent_health
        self._latency = latency
        self._summary = summary

    async def snapshot_async(self, *, ct: Optional[object] = None) -> DashboardSnapshot:
        return DashboardSnapshot(
            summary=self._summary(),
            live_calls=self._live_calls(),
            recent_calls=self._recent_calls(),
            agent_health=self._agent_health(),
            latency_by_stage=self._latency(),
        )
