# grpc.py
#
# CircleAI.Networking.Grpc — gRPC-channel network transport module.
#
# Ported faithfully from the C# spec:
#   GrpcTransportCommons.cs -> GrpcChannelState (enum), GrpcChannelDescriptor,
#       GrpcRetryPolicy, GrpcCallSummary (records), GrpcRetryPolicies,
#       InMemoryGrpcCallMetrics
#   GrpcNetworkTransport.cs -> GrpcNetworkTransport (INetworkTransport over a
#       gRPC channel), IGrpcChannel (the injected channel seam)
#
# The real C# transport wraps a Grpc.Net.Client GrpcChannel. Here the channel is
# injected behind IGrpcChannel (in-memory, no sockets). As in C#,
# GrpcNetworkTransport is NOT a generic send path — SendAsync raises (callers use
# the channel directly for typed proto clients); ReceiveAsync yields nothing
# because gRPC streaming is protocol-specific.

from __future__ import annotations

import itertools
import math
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta
from enum import IntEnum
from typing import AsyncIterator, Dict, List, Optional, Sequence

from .interfaces import INetworkTransport
from .network_types import NetworkPayload, TransportKind


class GrpcSendNotSupportedError(RuntimeError):
    """Raised by :meth:`GrpcNetworkTransport.send_async`.

    Mirrors the C# ``NotSupportedException`` thrown by
    ``GrpcNetworkTransport.SendAsync`` — gRPC calls are protocol-specific, so
    callers use the underlying channel directly for typed proto clients rather
    than a generic send path.
    """


class GrpcChannelState(IntEnum):
    """Connectivity state of a gRPC channel.

    Ordinals match the C# ``enum GrpcChannelState { Idle, Connecting, Ready,
    TransientFailure, Shutdown }``.
    """

    IDLE = 0
    CONNECTING = 1
    READY = 2
    TRANSIENT_FAILURE = 3
    SHUTDOWN = 4


class GrpcConnectionState(IntEnum):
    """Lifecycle state of a managed gRPC connection, mirroring the connectivity
    states a channel steps through as reconnection is driven.

    Ordinals match the C# ``enum GrpcConnectionState { Idle, Connecting, Ready,
    TransientFailure, Shutdown }`` (a distinct enum from
    :class:`GrpcChannelState` in the C# source, with identical members).
    """

    IDLE = 0
    CONNECTING = 1
    READY = 2
    TRANSIENT_FAILURE = 3
    SHUTDOWN = 4


@dataclass(frozen=True, slots=True)
class GrpcChannelDescriptor:
    """Static configuration of a gRPC channel. Faithful port of the C# record.

    ``keep_alive_interval`` is seconds (the C# ``TimeSpan``).
    """

    target: str
    use_tls: bool
    max_receive_bytes: int
    max_send_bytes: int
    keep_alive_interval: float  # seconds


@dataclass(frozen=True, slots=True)
class GrpcRetryPolicy:
    """gRPC retry policy. Faithful port of the C# record.

    ``initial_backoff`` / ``max_backoff`` are seconds (the C# ``TimeSpan``).
    """

    max_attempts: int
    initial_backoff: float  # seconds
    max_backoff: float      # seconds
    multiplier: float
    retryable_status_codes: Sequence[str]


@dataclass(frozen=True, slots=True)
class GrpcCallSummary:
    """A completed-call telemetry row. Faithful port of the C# record.

    ``latency`` is seconds (the C# ``TimeSpan``).
    """

    method: str
    attempts: int
    latency: float  # seconds
    status_code: str
    at_utc: datetime


class GrpcRetryPolicies:
    """Canonical retry policies. Mirrors the C# static ``GrpcRetryPolicies``
    accessor properties (Default / Aggressive / NoRetry). Backoffs in seconds.
    """

    DEFAULT: GrpcRetryPolicy = GrpcRetryPolicy(
        3, 0.1, 2.0, 2.0, ("UNAVAILABLE", "DEADLINE_EXCEEDED")
    )
    AGGRESSIVE: GrpcRetryPolicy = GrpcRetryPolicy(
        6, 0.05, 5.0, 2.0,
        ("UNAVAILABLE", "DEADLINE_EXCEEDED", "RESOURCE_EXHAUSTED"),
    )
    NO_RETRY: GrpcRetryPolicy = GrpcRetryPolicy(1, 0.0, 0.0, 1.0, ())


@dataclass(frozen=True, slots=True)
class GrpcReconnectPolicy:
    """Reconnection strategy for a managed gRPC channel: how many attempts to
    make and how to grow the backoff between them. Faithful port of the C#
    ``GrpcReconnectPolicy`` record.

    ``initial_backoff`` / ``max_backoff`` are seconds (the C# ``TimeSpan``).
    A sane :attr:`DEFAULT` is attached as a class attribute below.
    """

    max_attempts: int
    initial_backoff: float  # seconds
    backoff_multiplier: float
    max_backoff: float  # seconds

    def backoff_for(self, attempt: int) -> float:
        """Backoff (seconds) before a given 1-based ``attempt``:
        ``initial_backoff * backoff_multiplier ** (attempt - 1)``, capped at
        :attr:`max_backoff`. Attempt 1 returns :attr:`initial_backoff`
        (C#: ``BackoffFor``; overflow-safe — an infinite scaled value clamps to
        the cap).
        """
        if attempt < 1:
            raise ValueError("attempt is 1-based")
        scaled = self.initial_backoff * math.pow(
            self.backoff_multiplier, attempt - 1
        )
        cap = self.max_backoff
        if math.isinf(scaled) or scaled > cap:
            return self.max_backoff
        return scaled

    def should_retry(self, attempt: int) -> bool:
        """True when the 1-based ``attempt`` number is still within the retry
        budget (C#: ``ShouldRetry`` — ``attempt < MaxAttempts``).
        """
        return attempt < self.max_attempts


# C# static ``GrpcReconnectPolicy.Default`` — 5 attempts, 200ms growing x2 up to
# a 30s ceiling. Attached as a class attribute so callers use
# ``GrpcReconnectPolicy.DEFAULT``.
GrpcReconnectPolicy.DEFAULT = GrpcReconnectPolicy(  # type: ignore[attr-defined]
    5, 0.2, 2.0, 30.0
)


class GrpcDeadline:
    """Deadline math for gRPC calls: turns a relative timeout into the absolute
    UTC instant a call must complete by, and reports remaining time against a
    clock. Faithful port of the C# static ``GrpcDeadline`` class.

    Timeouts / remaining values are :class:`datetime.timedelta` (the C#
    ``TimeSpan``); ``now`` / deadlines are :class:`datetime`.
    """

    @staticmethod
    def from_timeout(timeout: timedelta, now_utc: datetime) -> datetime:
        """Absolute deadline for a call started at ``now_utc`` with the given
        ``timeout`` (C#: ``FromTimeout``).
        """
        if timeout < timedelta(0):
            raise ValueError("timeout must not be negative")
        return now_utc + timeout

    @staticmethod
    def remaining(deadline_utc: datetime, now_utc: datetime) -> timedelta:
        """Time left before ``deadline_utc``, clamped to zero once passed
        (C#: ``Remaining``).
        """
        left = deadline_utc - now_utc
        return left if left > timedelta(0) else timedelta(0)

    @staticmethod
    def is_expired(deadline_utc: datetime, now_utc: datetime) -> bool:
        """True once ``now_utc`` has reached or passed ``deadline_utc``
        (C#: ``IsExpired`` — ``nowUtc >= deadlineUtc``).
        """
        return now_utc >= deadline_utc


class InMemoryGrpcCallMetrics:
    """In-memory registry of channels, states, and call telemetry. Faithful
    port of the C# ``InMemoryGrpcCallMetrics``.
    """

    def __init__(self) -> None:
        self._channels: Dict[str, GrpcChannelDescriptor] = {}
        self._states: Dict[str, GrpcChannelState] = {}
        self._calls: List[GrpcCallSummary] = []
        self._lock = threading.Lock()
        self._seq = itertools.count(1)

    def register_channel(self, id: str, d: GrpcChannelDescriptor) -> None:
        if d is None:
            raise ValueError("descriptor required")
        with self._lock:
            self._channels[id] = d

    def get_channel(self, id: str) -> Optional[GrpcChannelDescriptor]:
        with self._lock:
            return self._channels.get(id)

    def set_state(self, id: str, s: GrpcChannelState) -> None:
        with self._lock:
            self._states[id] = s

    def state(self, id: str) -> GrpcChannelState:
        with self._lock:
            return self._states.get(id, GrpcChannelState.IDLE)

    def log_call(self, c: GrpcCallSummary) -> str:
        """Record a call and return a monotonic ``grpc-<n>`` id
        (C#: ``Interlocked.Increment``).
        """
        if c is None:
            raise ValueError("call summary required")
        with self._lock:
            self._calls.append(c)
            n = next(self._seq)
        return f"grpc-{n}"

    def recent_calls(self, limit: int = 50) -> Sequence[GrpcCallSummary]:
        """Most-recent calls first, capped at ``limit``
        (C#: ``OrderByDescending(AtUtc).Take(limit)``).
        """
        with self._lock:
            ordered = sorted(
                self._calls, key=lambda c: c.at_utc, reverse=True
            )
        return ordered[:limit]


class IGrpcChannel(ABC):
    """The injected gRPC channel seam (replaces the real
    ``Grpc.Net.Client.GrpcChannel``). A typed proto client is created against
    this channel by the consuming application; this transport only manages its
    lifecycle.
    """

    @property
    @abstractmethod
    def target(self) -> str:
        """The channel target address."""
        ...

    @property
    @abstractmethod
    def state(self) -> GrpcChannelState:
        """Current channel connectivity state."""
        ...

    @abstractmethod
    def dispose(self) -> None:
        """Release the channel (the C# ``GrpcChannel.Dispose``)."""
        ...


class InMemoryGrpcChannel(IGrpcChannel):
    """A working, deterministic :class:`IGrpcChannel`. Tracks target + state and
    transitions to :attr:`GrpcChannelState.SHUTDOWN` on :meth:`dispose`.
    """

    def __init__(
        self,
        target: str,
        *,
        state: GrpcChannelState = GrpcChannelState.IDLE,
    ) -> None:
        if target is None or target.strip() == "":
            raise ValueError("target required")
        self._target = target
        self._state = state
        self._disposed = False

    @property
    def target(self) -> str:
        return self._target

    @property
    def state(self) -> GrpcChannelState:
        return self._state

    def set_state(self, s: GrpcChannelState) -> None:
        if not self._disposed:
            self._state = s

    @property
    def is_disposed(self) -> bool:
        return self._disposed

    def dispose(self) -> None:
        self._disposed = True
        self._state = GrpcChannelState.SHUTDOWN


class GrpcNetworkTransport(INetworkTransport):
    """`INetworkTransport` backed by a gRPC channel. Faithful port of the C#
    ``GrpcNetworkTransport``.

    Manages channel lifecycle; the wire protocol (proto service) is defined by
    the consuming application, which uses :attr:`channel` for typed proto
    clients. ``send_async`` is intentionally not a generic send path — it raises
    :class:`GrpcSendNotSupportedError` (the C# ``NotSupportedException``).
    ``receive_async`` yields nothing (gRPC streaming is protocol-specific).
    """

    def __init__(self, channel: IGrpcChannel) -> None:
        if channel is None:
            raise ValueError("channel required")
        self._channel = channel
        self._running = False

    @property
    def kind(self) -> TransportKind:
        return TransportKind.GRPC

    @property
    def is_available(self) -> bool:
        return self._running

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        self._running = True

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        self._running = False

    async def send_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        raise GrpcSendNotSupportedError(
            "Use the gRPC channel directly for typed proto clients. "
            "GrpcNetworkTransport.send_async is not a generic send path."
        )

    def receive_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[NetworkPayload]:
        async def _empty() -> AsyncIterator[NetworkPayload]:
            return
            yield  # pragma: no cover  (makes this an async generator)

        return _empty()

    @property
    def channel(self) -> IGrpcChannel:
        """The underlying channel for typed gRPC client creation."""
        return self._channel

    def dispose(self) -> None:
        """Dispose the underlying channel (the C# ``Dispose``)."""
        self._channel.dispose()
