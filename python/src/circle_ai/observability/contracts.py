# contracts.py
#
# Port of CircleAI.Observability Contracts.cs (C# — the EXACT spec).
#
# (2.7.0) Observability contracts: metric-sample / trace-span / dashboard-spec
# records and the metric-sink / trace-sink / dashboard-publisher interfaces.
#
# C# ValueTask -> async def -> None. C# records -> frozen slotted dataclasses.
# TimeSpan -> timedelta. DateTimeOffset -> datetime.
# IReadOnlyDictionary<string,string>? -> Optional[Mapping[str,str]].

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta
from typing import Mapping, Optional


@dataclass(frozen=True, slots=True)
class MetricSample:
    """Mirrors ``CircleAI.Observability.MetricSample`` — ``record(string Name,
    double Value, IReadOnlyDictionary<string,string>? Tags = null)``.
    """

    name: str
    value: float
    tags: Optional[Mapping[str, str]] = None


@dataclass(frozen=True, slots=True)
class TraceSpan:
    """Mirrors ``CircleAI.Observability.TraceSpan`` — ``record(string TraceId,
    string SpanId, string? ParentSpanId, string Name, DateTimeOffset StartUtc,
    TimeSpan Duration, IReadOnlyDictionary<string,string>? Attributes = null)``.
    """

    trace_id: str
    span_id: str
    parent_span_id: Optional[str]
    name: str
    start_utc: datetime
    duration: timedelta
    attributes: Optional[Mapping[str, str]] = None


@dataclass(frozen=True, slots=True)
class DashboardSpec:
    """Mirrors ``CircleAI.Observability.DashboardSpec`` — ``record(string
    DashboardId, string Title, string JsonBlob)``.
    """

    dashboard_id: str
    title: str
    json_blob: str


class IMetricSink(ABC):
    """(2.7.0) Metric sink — Prometheus / OTel."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def emit_async(
        self, sample: MetricSample, ct: Optional[object] = None
    ) -> None:
        ...


class ITraceSink(ABC):
    """(2.7.0) Trace sink — OTel."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def emit_async(
        self, span: TraceSpan, ct: Optional[object] = None
    ) -> None:
        ...


class IDashboardPublisher(ABC):
    """(2.7.0) Dashboard publisher — Grafana / claude-team-dashboard."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def publish_async(
        self, spec: DashboardSpec, ct: Optional[object] = None
    ) -> None:
        ...
