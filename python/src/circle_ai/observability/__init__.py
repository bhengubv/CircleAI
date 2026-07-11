"""circle_ai.observability — port of the CircleAI.Observability assembly.

(2.7.0 contracts / 3.3.0 in-memory) Observability sinks: metric samples, trace
spans, dashboard specs, with real in-memory aggregating sinks and drop-all null
defaults. C# is the exact spec.
"""
from __future__ import annotations

from .contracts import (
    DashboardSpec,
    IDashboardPublisher,
    IMetricSink,
    ITraceSink,
    MetricSample,
    TraceSpan,
)
from .in_memory_observability import (
    InMemoryDashboardPublisher,
    InMemoryMetricSink,
    InMemoryTraceSink,
)
from .null_implementations import (
    NullDashboardPublisher,
    NullMetricSink,
    NullTraceSink,
)

__all__ = [
    "MetricSample",
    "TraceSpan",
    "DashboardSpec",
    "IMetricSink",
    "ITraceSink",
    "IDashboardPublisher",
    "InMemoryMetricSink",
    "InMemoryTraceSink",
    "InMemoryDashboardPublisher",
    "NullMetricSink",
    "NullTraceSink",
    "NullDashboardPublisher",
]
