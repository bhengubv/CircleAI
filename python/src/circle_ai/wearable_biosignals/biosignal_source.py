# biosignal_source.py
#
# Port of CircleAI.Wearable.Biosignals IBiosignalSource.cs + NullBiosignalSource.cs
# + RecordedBiosignalSource.cs (C# — the EXACT spec).
#
# A streaming source of biosignal samples — backed by a wearable device, a
# platform health API, or a simulator/recording. C# ``IAsyncEnumerable<T>``
# streaming maps to an ``async def ... yield`` async generator. C#
# ``BiosignalKind[] SupportedKinds`` maps to a ``list``/tuple property.

from __future__ import annotations

import asyncio
from abc import ABC, abstractmethod
from datetime import timedelta
from typing import AsyncIterator, List, Optional, Sequence

from .biosignal_kind import BiosignalKind
from .biosignal_sample import BiosignalSample


class IBiosignalSource(ABC):
    """A streaming source of biosignal samples. Mirrors
    ``CircleAI.Wearable.Biosignals.IBiosignalSource``.
    """

    @property
    @abstractmethod
    def supported_kinds(self) -> Sequence[BiosignalKind]:
        """The kinds this source can emit. May be empty (the null source)."""
        ...

    @abstractmethod
    def stream_async(
        self, cancellation_token: Optional[object] = None
    ) -> AsyncIterator[BiosignalSample]:
        """Stream biosignal samples until cancelled or the device disconnects.

        Returns an async iterator (C# ``IAsyncEnumerable<BiosignalSample>``).
        """
        ...

    @abstractmethod
    async def is_supported_async(
        self, kind: BiosignalKind, cancellation_token: Optional[object] = None
    ) -> bool:
        """Whether this source can produce samples of ``kind``."""
        ...


class NullBiosignalSource(IBiosignalSource):
    """A biosignal source that supports nothing and emits nothing. Mirrors
    ``CircleAI.Wearable.Biosignals.NullBiosignalSource`` — the "no wearable
    connected" reference case.
    """

    @property
    def supported_kinds(self) -> Sequence[BiosignalKind]:
        return ()

    async def is_supported_async(
        self, kind: BiosignalKind, cancellation_token: Optional[object] = None
    ) -> bool:
        return False

    async def stream_async(  # type: ignore[override]
        self, cancellation_token: Optional[object] = None
    ) -> AsyncIterator[BiosignalSample]:
        # Yield nothing. The await keeps the method genuinely async and honours
        # the cancellation token in case callers test for it.
        await asyncio.sleep(0)
        return
        yield  # pragma: no cover - makes this an async generator


class RecordedBiosignalSource(IBiosignalSource):
    """Replays a recorded biosignal stream. Mirrors
    ``CircleAI.Wearable.Biosignals.RecordedBiosignalSource`` — useful for tests,
    training data, and host integration when no live wearable is connected.

    ``replay_delay`` (a :class:`datetime.timedelta`, default zero) inserts an
    ``asyncio.sleep`` before each sample; a cancelled token raises
    :class:`asyncio.CancelledError` (the C# ``ThrowIfCancellationRequested`` /
    ``Task.Delay`` cancellation).
    """

    def __init__(
        self,
        samples: Sequence[BiosignalSample],
        replay_delay: Optional[timedelta] = None,
    ) -> None:
        if samples is None:
            raise ValueError("samples must not be None")
        self._samples: List[BiosignalSample] = list(samples)
        self._replay_delay = replay_delay if replay_delay is not None else timedelta()
        # Distinct kinds seen in the recording (C# HashSet<BiosignalKind>).
        seen: List[BiosignalKind] = []
        seen_set = set()
        for s in self._samples:
            if s.kind not in seen_set:
                seen_set.add(s.kind)
                seen.append(s.kind)
        self._kinds: List[BiosignalKind] = seen

    @property
    def supported_kinds(self) -> Sequence[BiosignalKind]:
        return tuple(self._kinds)

    async def is_supported_async(
        self, kind: BiosignalKind, cancellation_token: Optional[object] = None
    ) -> bool:
        return kind in self._kinds

    async def stream_async(  # type: ignore[override]
        self, cancellation_token: Optional[object] = None
    ) -> AsyncIterator[BiosignalSample]:
        delay = self._replay_delay.total_seconds()
        for s in self._samples:
            if delay > 0:
                await asyncio.sleep(delay)
            yield s
