# null_implementations.py
#
# Port of CircleAI.Observability NullImplementations.cs (C# — the EXACT spec).
#
# (2.7.0) Drop-all defaults — signals go nowhere. Each exposes a singleton
# `INSTANCE` mirroring the C# `static readonly ... Instance`.

from __future__ import annotations

from typing import Optional

from .contracts import (
    DashboardSpec,
    IDashboardPublisher,
    IMetricSink,
    ITraceSink,
    MetricSample,
    TraceSpan,
)


class NullMetricSink(IMetricSink):
    INSTANCE: "NullMetricSink"

    @property
    def backend_id(self) -> str:
        return "null"

    async def emit_async(
        self, sample: MetricSample, ct: Optional[object] = None
    ) -> None:
        return None


class NullTraceSink(ITraceSink):
    INSTANCE: "NullTraceSink"

    @property
    def backend_id(self) -> str:
        return "null"

    async def emit_async(
        self, span: TraceSpan, ct: Optional[object] = None
    ) -> None:
        return None


class NullDashboardPublisher(IDashboardPublisher):
    INSTANCE: "NullDashboardPublisher"

    @property
    def backend_id(self) -> str:
        return "null"

    async def publish_async(
        self, spec: DashboardSpec, ct: Optional[object] = None
    ) -> None:
        return None


NullMetricSink.INSTANCE = NullMetricSink()
NullTraceSink.INSTANCE = NullTraceSink()
NullDashboardPublisher.INSTANCE = NullDashboardPublisher()
