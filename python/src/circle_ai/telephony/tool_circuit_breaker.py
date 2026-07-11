# tool_circuit_breaker.py
#
# Port of CircleAI.Telephony ToolCircuitBreaker.cs (C# — the EXACT spec).
#
# (3.3.0) Per-tool circuit breaker + timeout wrapper around any
# IToolCallRegistry. Three states: Closed (normal), Open (failing — reject
# immediately), HalfOpen (one trial allowed). Each tool has its own breaker
# state — a broken billing API doesn't cut off the order-lookup API.
#
# C# CancellationTokenSource(timeout) + linked token -> asyncio.wait_for around
# the inner invoke; a wait_for TimeoutError is the C# OperationCanceledException
# "timed out" branch. C# Func<DateTimeOffset> clock -> Callable[[], datetime]
# (default: datetime.now(timezone.utc)). C# ConcurrentDictionary + Interlocked ->
# dicts + a lock; the breaker entry's failure counter is guarded so the
# increment/threshold check stays atomic.

from __future__ import annotations

import asyncio
import threading
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from enum import IntEnum
from typing import Callable, Dict, List, Optional

from .tool_calling import (
    IToolCallRegistry,
    LocalToolHandler,
    ToolDefinition,
    ToolInvocation,
    ToolResult,
)


@dataclass(frozen=True, slots=True)
class ToolCallPolicy:
    """(3.3.0) Per-tool timeout + breaker thresholds. Mirrors ``ToolCallPolicy``.

    ``timeout``: wall-clock ceiling for the call (default 5 s).
    ``failure_threshold``: consecutive failures that trip the breaker (default 3).
    ``open_duration``: how long the breaker stays open before half-opening
    (default 30 s).
    """

    timeout: Optional[timedelta] = None
    failure_threshold: int = 3
    open_duration: Optional[timedelta] = None

    @property
    def timeout_or_default(self) -> timedelta:
        return self.timeout if self.timeout is not None else timedelta(seconds=5)

    @property
    def open_duration_or_default(self) -> timedelta:
        return self.open_duration if self.open_duration is not None else timedelta(seconds=30)


class ToolBreakerState(IntEnum):
    """(3.3.0) Breaker state."""

    CLOSED = 0
    OPEN = 1
    HALF_OPEN = 2


class _BreakerEntry:
    __slots__ = ("_lock", "_consecutive_failures", "_opened_at", "_is_open")

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._consecutive_failures = 0
        self._opened_at = datetime.fromtimestamp(0, tz=timezone.utc)
        self._is_open = False

    def current_state(self, now: datetime, open_duration: timedelta) -> ToolBreakerState:
        with self._lock:
            if not self._is_open:
                return ToolBreakerState.CLOSED
            if now - self._opened_at >= open_duration:
                return ToolBreakerState.HALF_OPEN
            return ToolBreakerState.OPEN

    def record_success(self) -> None:
        with self._lock:
            self._consecutive_failures = 0
            self._is_open = False

    def record_failure(self, threshold: int, now: datetime) -> None:
        with self._lock:
            self._consecutive_failures += 1
            if self._consecutive_failures >= threshold:
                self._is_open = True
                self._opened_at = now


class CircuitBreakerToolRegistry(IToolCallRegistry):
    """(3.3.0) Decorates an :class:`IToolCallRegistry` with per-tool timeouts and
    a circuit breaker. Pass a clock for deterministic tests."""

    def __init__(
        self,
        inner: IToolCallRegistry,
        default_policy: Optional[ToolCallPolicy] = None,
        clock: Optional[Callable[[], datetime]] = None,
    ) -> None:
        if inner is None:
            raise ValueError("inner must not be None")
        self._inner = inner
        self._default_policy = default_policy if default_policy is not None else ToolCallPolicy()
        self._clock = clock if clock is not None else (lambda: datetime.now(timezone.utc))
        self._lock = threading.Lock()
        self._policies: Dict[str, ToolCallPolicy] = {}
        self._breakers: Dict[str, _BreakerEntry] = {}

    def set_policy(self, tool_name: str, policy: ToolCallPolicy) -> None:
        """Override the policy for a specific tool."""
        if policy is None:
            raise ValueError("policy must not be None")
        with self._lock:
            self._policies[tool_name.casefold()] = policy

    def get_state(self, tool_name: str) -> ToolBreakerState:
        """Inspect the current breaker state for a tool."""
        with self._lock:
            entry = self._breakers.get(tool_name.casefold())
        if entry is None:
            return ToolBreakerState.CLOSED
        return entry.current_state(self._clock(), self._get_policy(tool_name).open_duration_or_default)

    @property
    def definitions(self) -> List[ToolDefinition]:
        return self._inner.definitions

    def register_local(self, definition: ToolDefinition, handler: LocalToolHandler) -> None:
        self._inner.register_local(definition, handler)

    def register_webhook(self, definition: ToolDefinition, webhook: str) -> None:
        self._inner.register_webhook(definition, webhook)

    async def invoke_async(self, invocation: ToolInvocation, *, ct: Optional[object] = None) -> ToolResult:
        if invocation is None:
            raise ValueError("invocation must not be None")
        policy = self._get_policy(invocation.tool_name)
        with self._lock:
            entry = self._breakers.get(invocation.tool_name.casefold())
            if entry is None:
                entry = _BreakerEntry()
                self._breakers[invocation.tool_name.casefold()] = entry

        state = entry.current_state(self._clock(), policy.open_duration_or_default)
        if state == ToolBreakerState.OPEN:
            return ToolResult(
                invocation.call_id,
                False,
                "{}",
                f"Tool '{invocation.tool_name}' is circuit-broken; retry after the breaker resets.",
            )

        timeout_seconds = policy.timeout_or_default.total_seconds()
        try:
            result = await asyncio.wait_for(
                self._inner.invoke_async(invocation, ct=ct), timeout=timeout_seconds
            )
            if result.succeeded:
                entry.record_success()
            else:
                entry.record_failure(policy.failure_threshold, self._clock())
            return result
        except asyncio.TimeoutError:
            entry.record_failure(policy.failure_threshold, self._clock())
            return ToolResult(
                invocation.call_id,
                False,
                "{}",
                f"Tool '{invocation.tool_name}' timed out after "
                f"{policy.timeout_or_default.total_seconds() * 1000} ms.",
            )
        except Exception as ex:
            entry.record_failure(policy.failure_threshold, self._clock())
            return ToolResult(invocation.call_id, False, "{}", str(ex))

    def _get_policy(self, tool_name: str) -> ToolCallPolicy:
        with self._lock:
            policy = self._policies.get(tool_name.casefold())
        return policy if policy is not None else self._default_policy
