# in_memory_observability.py
#
# Port of CircleAI.Observability InMemoryObservability.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory metric sink (per-name sample lists), trace sink (spans
# per traceId, read ordered by StartUtc), and dashboard publisher (specs by id).
# C# ConcurrentDictionary + lock -> plain dicts guarded by a threading.Lock.

from __future__ import annotations

import threading
from typing import Dict, List, Optional

from .contracts import (
    DashboardSpec,
    IDashboardPublisher,
    IMetricSink,
    ITraceSink,
    MetricSample,
    TraceSpan,
)


class InMemoryMetricSink(IMetricSink):
    """Real in-memory :class:`IMetricSink`. Mirrors
    ``CircleAI.Observability.InMemoryMetricSink``."""

    def __init__(self) -> None:
        self._by_name: Dict[str, List[MetricSample]] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def emit_async(
        self, sample: MetricSample, ct: Optional[object] = None
    ) -> None:
        if sample is None:
            raise ValueError("sample")
        if sample.name is None or sample.name.strip() == "":
            raise ValueError("Name required")
        with self._lock:
            self._by_name.setdefault(sample.name, []).append(sample)

    def read(self, name: str) -> List[MetricSample]:
        with self._lock:
            got = self._by_name.get(name)
            return list(got) if got is not None else []

    @property
    def metric_names(self) -> List[str]:
        with self._lock:
            return sorted(self._by_name.keys())


class InMemoryTraceSink(ITraceSink):
    """Real in-memory :class:`ITraceSink`. Mirrors
    ``CircleAI.Observability.InMemoryTraceSink``."""

    def __init__(self) -> None:
        self._by_trace: Dict[str, List[TraceSpan]] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def emit_async(
        self, span: TraceSpan, ct: Optional[object] = None
    ) -> None:
        if span is None:
            raise ValueError("span")
        if span.trace_id is None or span.trace_id.strip() == "":
            raise ValueError("TraceId required")
        with self._lock:
            self._by_trace.setdefault(span.trace_id, []).append(span)

    def read(self, trace_id: str) -> List[TraceSpan]:
        with self._lock:
            got = self._by_trace.get(trace_id)
            if got is None:
                return []
            return sorted(got, key=lambda s: s.start_utc)


class InMemoryDashboardPublisher(IDashboardPublisher):
    """Real in-memory :class:`IDashboardPublisher`. Mirrors
    ``CircleAI.Observability.InMemoryDashboardPublisher``."""

    def __init__(self) -> None:
        self._specs: Dict[str, DashboardSpec] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def publish_async(
        self, spec: DashboardSpec, ct: Optional[object] = None
    ) -> None:
        if spec is None:
            raise ValueError("spec")
        if spec.dashboard_id is None or spec.dashboard_id.strip() == "":
            raise ValueError("DashboardId required")
        with self._lock:
            self._specs[spec.dashboard_id] = spec

    def get(self, dashboard_id: str) -> Optional[DashboardSpec]:
        with self._lock:
            return self._specs.get(dashboard_id)

    @property
    def all(self) -> List[DashboardSpec]:
        with self._lock:
            return sorted(self._specs.values(), key=lambda s: s.dashboard_id)
