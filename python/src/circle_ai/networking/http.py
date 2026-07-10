# http.py
#
# CircleAI.Networking.Http — HttpClient-backed network transport module.
#
# Ported faithfully from the C# spec:
#   HttpTransportCommons.cs -> HttpEndpointDescriptor, HttpRequestSummary,
#       HttpCacheKey (records), HttpStatusFamily, InMemoryHttpRequestMetrics
#   HttpNetworkTransport.cs -> HttpNetworkTransport (INetworkTransport over
#       HttpClient), IHttpMessageSender (the injected HttpClient seam)
#
# The real C# transport wraps System.Net.Http.HttpClient. Here the send path is
# injected behind IHttpMessageSender (in-memory, no sockets). SendAsync POSTs
# payload data to {baseUrl}/messages/{destinationId} and retries up to 3 times
# with exponential backoff on transient failures — the algorithm is ported
# exactly. ReceiveAsync yields nothing (HTTP is request-response; use WebSocket
# or SSE for server push).

from __future__ import annotations

import asyncio
import statistics
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import (
    AsyncIterator,
    Awaitable,
    Callable,
    Dict,
    List,
    Mapping,
    Optional,
    Sequence,
)
from urllib.parse import quote

from .interfaces import INetworkTransport
from .network_types import NetworkPayload, TransportKind


class HttpStatusFamily:
    """HTTP status-family predicates. Faithful port of the C# static
    ``HttpStatusFamily`` (Is2xx / Is3xx / Is4xx / Is5xx / ShouldRetry).
    """

    @staticmethod
    def is_2xx(s: int) -> bool:
        return 200 <= s < 300

    @staticmethod
    def is_3xx(s: int) -> bool:
        return 300 <= s < 400

    @staticmethod
    def is_4xx(s: int) -> bool:
        return 400 <= s < 500

    @staticmethod
    def is_5xx(s: int) -> bool:
        return 500 <= s < 600

    @staticmethod
    def should_retry(s: int) -> bool:
        """408 / 425 / 429 / any 5xx (C#: ``ShouldRetry``)."""
        return s == 408 or s == 425 or s == 429 or HttpStatusFamily.is_5xx(s)


@dataclass(frozen=True, slots=True)
class HttpEndpointDescriptor:
    """Describes an HTTP endpoint. Faithful port of the C# record.

    ``default_headers`` may be ``None`` (the C#
    ``IReadOnlyDictionary<string,string>?``).
    """

    method: str
    base_uri: str
    path: str
    default_headers: Optional[Mapping[str, str]]


@dataclass(frozen=True, slots=True)
class HttpRequestSummary:
    """A completed-request telemetry row. Faithful port of the C# record.

    ``latency`` is seconds (the C# ``TimeSpan``).
    """

    endpoint_id: str
    status_code: int
    latency: float  # seconds
    response_bytes: int
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class HttpCacheKey:
    """A response-cache key. Faithful port of the C# record."""

    method: str
    full_uri: str
    accept_header: str


class InMemoryHttpRequestMetrics:
    """In-memory registry of endpoints + request telemetry. Faithful port of the
    C# ``InMemoryHttpRequestMetrics``.
    """

    def __init__(self) -> None:
        self._endpoints: Dict[str, HttpEndpointDescriptor] = {}
        self._requests: List[HttpRequestSummary] = []
        self._lock = threading.Lock()

    def register(self, id: str, d: HttpEndpointDescriptor) -> None:
        if d is None:
            raise ValueError("descriptor required")
        with self._lock:
            self._endpoints[id] = d

    def get_endpoint(self, id: str) -> Optional[HttpEndpointDescriptor]:
        with self._lock:
            return self._endpoints.get(id)

    def log(self, s: HttpRequestSummary) -> None:
        if s is None:
            raise ValueError("request summary required")
        with self._lock:
            self._requests.append(s)

    def recent_requests(self, limit: int = 100) -> Sequence[HttpRequestSummary]:
        """Most-recent requests first, capped at ``limit``
        (C#: ``OrderByDescending(AtUtc).Take(limit)``).
        """
        with self._lock:
            ordered = sorted(
                self._requests, key=lambda r: r.at_utc, reverse=True
            )
        return ordered[:limit]

    def avg_2xx_latency_ms(self, endpoint_id: str) -> float:
        """Mean latency (ms) over 2xx responses for ``endpoint_id``; 0.0 when
        none (C#: ``Avg2xxLatencyMs``). Latencies are stored in seconds, so
        multiply by 1000 to report milliseconds like the C# ``TotalMilliseconds``.
        """
        with self._lock:
            rows = [
                r.latency
                for r in self._requests
                if r.endpoint_id == endpoint_id
                and HttpStatusFamily.is_2xx(r.status_code)
            ]
        if not rows:
            return 0.0
        return statistics.fmean(rows) * 1000.0


class HttpRequestException(RuntimeError):
    """A failed HTTP request. The single-type analogue of .NET's
    ``System.Net.Http.HttpRequestException`` — raised BOTH for a transient
    network failure (the C# ``PostAsync`` throw) AND for a non-success status
    (the C# ``EnsureSuccessStatusCode`` throw). ``HttpNetworkTransport``
    catches this exact type to drive its backoff+retry loop, so both failure
    kinds share the same retry path exactly as in C#.

    ``status_code`` is set for a non-success-status failure, ``None`` for a
    network-level transient failure.
    """

    def __init__(self, message: str, *, status_code: Optional[int] = None) -> None:
        super().__init__(message)
        self.status_code = status_code


# Back-compat aliases: transient (network) vs status failures are the same
# .NET type; these names read at call sites and are both ``HttpRequestException``.
HttpTransientError = HttpRequestException
HttpRequestFailedError = HttpRequestException


class IHttpMessageSender(ABC):
    """The injected HTTP send seam (replaces ``HttpClient.PostAsync`` +
    ``EnsureSuccessStatusCode``).

    Implementations POST ``body`` to ``url`` with ``headers`` and return the
    HTTP status code, or raise :class:`HttpRequestException` for a transient
    network failure (the C# ``HttpRequestException`` from ``PostAsync``).
    """

    @abstractmethod
    async def post_async(
        self,
        url: str,
        body: bytes,
        headers: Mapping[str, str],
        *,
        ct: Optional[object] = None,
    ) -> int:
        """POST and return the response status code."""
        ...


class HttpNetworkTransport(INetworkTransport):
    """`INetworkTransport` backed by an injected HTTP sender. Faithful port of
    the C# ``HttpNetworkTransport``.

    ``is_available`` is always True (HTTP is assumed reachable if configured).
    ``send_async`` POSTs payload data to ``{base_url}/messages/{destination_id}``
    (or ``{base_url}/messages`` for a broadcast), retrying up to 3 times with
    exponential backoff on transient failures — exactly mirroring the C# retry
    loop. ``receive_async`` yields nothing (HTTP pull model).
    """

    def __init__(
        self,
        sender: IHttpMessageSender,
        base_url: str,
        *,
        backoff_base_seconds: float = 1.0,
        sleep: Optional[Callable[[float], Awaitable[None]]] = None,
    ) -> None:
        if sender is None:
            raise ValueError("sender required")
        if base_url is None or base_url.strip() == "":
            raise ValueError("base_url required")
        self._sender = sender
        self._base_url = base_url.rstrip("/")
        # Backoff base (seconds) for Task.Delay(2^attempt); injectable so tests
        # need not wait real seconds while the escalation stays faithful.
        self._backoff_base = backoff_base_seconds
        self._sleep = sleep or asyncio.sleep
        self._running = False

    @property
    def kind(self) -> TransportKind:
        return TransportKind.HTTP

    @property
    def is_available(self) -> bool:
        # Assume HTTP always available if configured (matches C#).
        return True

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        self._running = True

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        self._running = False

    async def send_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        if payload is None:
            raise ValueError("payload required")
        dest = payload.destination_id
        if dest:
            url = f"{self._base_url}/messages/{quote(dest, safe='')}"
        else:
            url = f"{self._base_url}/messages"

        headers = {
            "Content-Type": payload.content_type,
            "X-Payload-Id": payload.id,
            "X-Payload-Priority": payload.priority.name.capitalize(),
        }

        # Up to 3 attempts; retry the first two failures, exactly as the C#
        # `catch (HttpRequestException) when (attempt < 2)` guard. Both a
        # transient network failure (PostAsync throw) and a non-success status
        # (EnsureSuccessStatusCode throw) are HttpRequestException, so both
        # travel this same retry path.
        for attempt in range(3):
            try:
                status = await self._sender.post_async(
                    url, payload.data, headers, ct=ct
                )
                if not HttpStatusFamily.is_2xx(status):
                    # EnsureSuccessStatusCode-equivalent.
                    raise HttpRequestException(
                        f"HTTP {status} for {url}", status_code=status
                    )
                return
            except HttpRequestException:
                if attempt < 2:
                    await self._sleep(self._backoff_base * (2 ** attempt))
                    continue
                raise

    def receive_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[NetworkPayload]:
        # HTTP is request-response; polling receive is intentionally not provided
        # (use WebSocket/SSE). Yields nothing, matching the C# `yield break`.
        async def _empty() -> AsyncIterator[NetworkPayload]:
            return
            yield  # pragma: no cover  (makes this an async generator)

        return _empty()


class InMemoryHttpMessageSender(IHttpMessageSender):
    """A working, deterministic :class:`IHttpMessageSender`.

    Records every POST and returns a configurable status code (200 by default).
    A per-URL script of statuses can be queued to drive the retry path: a queued
    :class:`HttpRequestException` (or the ``TRANSIENT`` sentinel) raises to
    trigger a backoff+retry; a queued int is returned as the status code.
    """

    #: Sentinel a caller can enqueue to force one transient failure.
    TRANSIENT = object()

    def __init__(self, *, default_status: int = 200) -> None:
        self._default_status = default_status
        self._script: Dict[str, List[object]] = {}
        self._posts: List[tuple] = []
        self._lock = threading.Lock()

    def script(self, url: str, *responses: object) -> "InMemoryHttpMessageSender":
        """Queue a sequence of responses for ``url`` (status ints and/or the
        :attr:`TRANSIENT` sentinel), consumed one per POST. Returns self.
        """
        with self._lock:
            self._script.setdefault(url, []).extend(responses)
        return self

    @property
    def posts(self) -> Sequence[tuple]:
        """Every (url, body, headers) POST recorded, in order."""
        with self._lock:
            return list(self._posts)

    @property
    def post_count(self) -> int:
        with self._lock:
            return len(self._posts)

    async def post_async(
        self,
        url: str,
        body: bytes,
        headers: Mapping[str, str],
        *,
        ct: Optional[object] = None,
    ) -> int:
        with self._lock:
            self._posts.append((url, bytes(body), dict(headers)))
            queued = self._script.get(url)
            response: object = (
                queued.pop(0) if queued else self._default_status
            )
        if isinstance(response, HttpRequestException):
            raise response
        if response is self.TRANSIENT:
            raise HttpRequestException(f"transient failure for {url}")
        return int(response)  # type: ignore[arg-type]
