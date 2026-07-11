# speech_lifecycle_events.py
#
# Port of CircleAI.Telephony SpeechLifecycleEvents.cs (C# — the EXACT spec).
#
# (3.3.0) Lifecycle events for every speaking moment in a call:
# caller-speech-started, transcript-final, agent-thinking,
# agent-speaking-started, agent-speaking-finished, plus errors. Apps subscribe
# for analytics, UX (waveform animations), or audit.
#
# C# record inheritance (SpeechLifecycleEvent -> concrete records) maps to a
# frozen-dataclass hierarchy. C# generic Subscribe<TEvent>(Action<TEvent>) has
# no runtime-generic Python equivalent, so subscribe takes the event ``type``
# explicitly; Publish walks the class MRO (like the C# BaseType walk) so a
# SpeechLifecycleEvent subscriber receives every concrete type. The Type ->
# handler-bucket map is guarded by a lock to match the ConcurrentDictionary +
# Interlocked handle allocation.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta
from typing import Callable, Dict, Type

from .disposable import IDisposable, _ActionDisposable


@dataclass(frozen=True, slots=True)
class SpeechLifecycleEvent:
    """(3.3.0) Discriminator base for the union of lifecycle events.

    Mirrors ``abstract record SpeechLifecycleEvent(string CallId, DateTimeOffset At)``.
    """

    call_id: str
    at: datetime


@dataclass(frozen=True, slots=True)
class CallerSpeechStartedEvent(SpeechLifecycleEvent):
    pass


@dataclass(frozen=True, slots=True)
class CallerSpeechEndedEvent(SpeechLifecycleEvent):
    pass


@dataclass(frozen=True, slots=True)
class TranscriptInterimEvent(SpeechLifecycleEvent):
    text: str


@dataclass(frozen=True, slots=True)
class TranscriptFinalEventV2(SpeechLifecycleEvent):
    """Mirrors ``TranscriptFinalEvent_v2``."""

    text: str


@dataclass(frozen=True, slots=True)
class AgentThinkingEvent(SpeechLifecycleEvent):
    pass


@dataclass(frozen=True, slots=True)
class AgentSpeakingStartedEvent(SpeechLifecycleEvent):
    pass


@dataclass(frozen=True, slots=True)
class AgentSpeakingFinishedEvent(SpeechLifecycleEvent):
    spoken_duration: timedelta


@dataclass(frozen=True, slots=True)
class SpeechErrorEvent(SpeechLifecycleEvent):
    stage: str
    message: str


class ISpeechSubscription(IDisposable):
    """(3.3.0) Subscription handle. Mirrors ``ISpeechSubscription : IDisposable``."""


class ISpeechLifecycleBus(ABC):
    """(3.3.0) Speech lifecycle pub/sub."""

    @abstractmethod
    def subscribe(
        self,
        event_type: Type[SpeechLifecycleEvent],
        handler: Callable[[SpeechLifecycleEvent], None],
    ) -> ISpeechSubscription:
        """(3.3.0) Subscribe to a specific event type. Use
        :class:`SpeechLifecycleEvent` for all."""

    @abstractmethod
    def publish(self, ev: SpeechLifecycleEvent) -> None:
        """(3.3.0) Publish one event. All matching subscribers are invoked
        synchronously."""


class _SubHandle(ISpeechSubscription):
    __slots__ = ("_inner",)

    def __init__(self, dispose_action: Callable[[], None]) -> None:
        self._inner = _ActionDisposable(dispose_action)

    def dispose(self) -> None:
        self._inner.dispose()


class InMemorySpeechLifecycleBus(ISpeechLifecycleBus):
    """(3.3.0) Default in-memory bus."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._subscribers: Dict[Type[SpeechLifecycleEvent], Dict[int, Callable]] = {}
        self._next_handle = 0

    def subscribe(
        self,
        event_type: Type[SpeechLifecycleEvent],
        handler: Callable[[SpeechLifecycleEvent], None],
    ) -> ISpeechSubscription:
        if handler is None:
            raise ValueError("handler must not be None")
        with self._lock:
            bucket = self._subscribers.get(event_type)
            if bucket is None:
                bucket = {}
                self._subscribers[event_type] = bucket
            self._next_handle += 1
            handle_id = self._next_handle
            bucket[handle_id] = handler

        def _remove() -> None:
            with self._lock:
                b = self._subscribers.get(event_type)
                if b is not None:
                    b.pop(handle_id, None)

        return _SubHandle(_remove)

    def publish(self, ev: SpeechLifecycleEvent) -> None:
        if ev is None:
            raise ValueError("ev must not be None")
        # Walk the class hierarchy so a SpeechLifecycleEvent subscriber receives
        # every concrete type. Stop at (and exclude) object, mirroring the C#
        # ``t != typeof(object)`` guard.
        for t in type(ev).__mro__:
            if t is object:
                break
            with self._lock:
                bucket = self._subscribers.get(t)
                handlers = list(bucket.values()) if bucket is not None else []
            for handler in handlers:
                handler(ev)
