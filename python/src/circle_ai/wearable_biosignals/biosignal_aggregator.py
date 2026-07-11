# biosignal_aggregator.py
#
# Port of CircleAI.Wearable.Biosignals BiosignalAggregator.cs (C# — the EXACT spec).
#
# Sliding-window aggregator: pulls samples from an IBiosignalSource and computes
# per-kind min/max/mean/count over a configurable time window.
#
# The C# implementation time-bounds the read with a linked CTS
# (``CancelAfter(window)``) so a never-completing source still yields a snapshot,
# and additionally breaks once wall-clock passes ``generatedAt + window``. This
# port bounds the whole read with :func:`asyncio.wait_for` (timeout = window),
# swallowing the resulting ``TimeoutError`` (the analogue of the C#
# ``catch (OperationCanceledException) when (...)``), and keeps the same
# ``MeasuredAt < cutoff`` skip and the ``UtcNow >= deadline`` early break.
#
# min/max/mean use a float32 accumulator to match the C# ``float`` fields
# (mean is (float)(sum/count) over a double sum).

from __future__ import annotations

import asyncio
import struct
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Dict, Mapping, Optional

from .biosignal_kind import BiosignalKind
from .biosignal_source import IBiosignalSource


def _f32(x: float) -> float:
    """Round a Python float to IEEE-754 single precision (a C# ``float``)."""
    return struct.unpack("<f", struct.pack("<f", x))[0]


@dataclass(frozen=True, slots=True)
class BiosignalStats:
    """Per-kind aggregate statistics over a sliding window. Mirrors
    ``CircleAI.Wearable.Biosignals.BiosignalStats`` — ``record(int SampleCount,
    float Min, float Max, float Mean)``.
    """

    sample_count: int
    min: float
    max: float
    mean: float


@dataclass(frozen=True, slots=True)
class BiosignalSnapshot:
    """A snapshot of biosignal aggregates across all observed kinds. Mirrors
    ``CircleAI.Wearable.Biosignals.BiosignalSnapshot`` — kinds with no samples in
    the window are absent from ``stats``.
    """

    stats: Mapping[BiosignalKind, BiosignalStats]
    generated_at: datetime


class _Accumulator:
    """Streaming min/max/mean over float32 values (C# private ``Accumulator``)."""

    __slots__ = ("_count", "_min", "_max", "_sum")

    def __init__(self) -> None:
        self._count = 0
        self._min = float("inf")
        self._max = float("-inf")
        self._sum = 0.0

    def add(self, v: float) -> None:
        self._count += 1
        if v < self._min:
            self._min = v
        if v > self._max:
            self._max = v
        self._sum += v

    def to_stats(self) -> BiosignalStats:
        mean = 0.0 if self._count == 0 else _f32(self._sum / self._count)
        return BiosignalStats(self._count, _f32(self._min), _f32(self._max), mean)


class BiosignalAggregator:
    """Sliding-window aggregator over an :class:`IBiosignalSource`. Mirrors
    ``CircleAI.Wearable.Biosignals.BiosignalAggregator``.
    """

    def __init__(self, source: IBiosignalSource) -> None:
        if source is None:
            raise ValueError("source must not be None")
        self._source = source

    async def snapshot_async(
        self, window: timedelta, cancellation_token: Optional[object] = None
    ) -> BiosignalSnapshot:
        """Consume samples until the source completes or elapsed time exceeds
        ``window``, then return a snapshot over the in-window samples (relative
        to UTC now at call time). Single-shot, not continuous. Mirrors
        ``SnapshotAsync``.
        """
        if window <= timedelta():
            raise ValueError("Window must be positive.")

        generated_at = datetime.now(timezone.utc)
        cutoff = generated_at - window
        deadline = generated_at + window
        accumulator: Dict[BiosignalKind, _Accumulator] = {}

        async def _consume() -> None:
            async for sample in self._source.stream_async(cancellation_token):
                if sample.measured_at < cutoff:
                    continue
                acc = accumulator.get(sample.kind)
                if acc is None:
                    acc = _Accumulator()
                    accumulator[sample.kind] = acc
                acc.add(sample.value)
                if datetime.now(timezone.utc) >= deadline:
                    break

        # Time-bound the read so a never-completing source still yields a
        # snapshot (C# linked-CTS CancelAfter(window)). Elapsed-before-complete
        # is expected — swallow it and fall through.
        try:
            await asyncio.wait_for(_consume(), timeout=window.total_seconds())
        except asyncio.TimeoutError:
            pass

        stats: Dict[BiosignalKind, BiosignalStats] = {
            kind: acc.to_stats() for kind, acc in accumulator.items()
        }
        return BiosignalSnapshot(stats, generated_at)
