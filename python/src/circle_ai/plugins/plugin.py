# plugin.py
#
# Port of CircleAI.Plugins IPlugin.cs (C# — the EXACT spec).
#
# (3.2.0) Plugin contract surface: IPlugin, IPluginContext, IPluginEvents, the
# thread-safe default PluginEvents bus, and well-known event names. The C#
# ILogger the context exposes is host infrastructure — here it is an injected
# opaque object (any logger-like value or None).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from typing import Callable, Dict, List, Optional

# C# Action<object?> subscriber.
EventHandler = Callable[[Optional[object]], None]


class IPluginContext(ABC):
    """(3.2.0) Stable surface plugins are allowed to use."""

    @property
    @abstractmethod
    def workspace_path(self) -> Optional[str]:
        ...

    @property
    @abstractmethod
    def events(self) -> "IPluginEvents":
        ...

    @property
    @abstractmethod
    def logger(self) -> Optional[object]:
        ...


class IPlugin(ABC):
    """(3.2.0) Contract every CircleAI plugin implements."""

    @property
    @abstractmethod
    def id(self) -> str:
        ...

    @property
    @abstractmethod
    def display_name(self) -> str:
        ...

    @property
    @abstractmethod
    def version(self) -> str:
        ...

    @abstractmethod
    async def initialize_async(self, context: IPluginContext, cancellation_token: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def shutdown_async(self, cancellation_token: Optional[object] = None) -> None:
        ...


class IPluginEvents(ABC):
    """(3.2.0) String-keyed event bus."""

    @abstractmethod
    def subscribe(self, event_name: str, handler: EventHandler) -> object:
        """Subscribe; returns an unsubscribe handle (``dispose()``; context
        manager)."""
        ...

    @abstractmethod
    def raise_event(self, event_name: str, payload: Optional[object]) -> None:
        """Raise an event. Host-only API. (C# ``Raise``; renamed to avoid the
        Python ``raise`` keyword.)"""
        ...


class PluginEvents(IPluginEvents):
    """(3.2.0) Thread-safe default :class:`IPluginEvents`."""

    def __init__(self) -> None:
        self._handlers: Dict[str, List[EventHandler]] = {}
        self._lock = threading.Lock()

    def subscribe(self, event_name: str, handler: EventHandler) -> "PluginEvents._Subscription":
        if event_name is None or event_name == "":
            raise ValueError("eventName must not be null or empty")
        if handler is None:
            raise ValueError("handler must not be None")
        with self._lock:
            self._handlers.setdefault(event_name, []).append(handler)
        return PluginEvents._Subscription(self, event_name, handler)

    def raise_event(self, event_name: str, payload: Optional[object]) -> None:
        with self._lock:
            lst = self._handlers.get(event_name)
            if lst is None:
                return
            snapshot = list(lst)
        for h in snapshot:
            try:
                h(payload)
            except Exception:
                # An unhealthy plugin must not corrupt the host.
                pass

    def _unsubscribe(self, event_name: str, handler: EventHandler) -> None:
        with self._lock:
            lst = self._handlers.get(event_name)
            if lst is not None:
                try:
                    lst.remove(handler)
                except ValueError:
                    pass

    class _Subscription:
        def __init__(self, owner: "PluginEvents", name: str, handler: EventHandler) -> None:
            self._owner = owner
            self._name = name
            self._handler = handler
            self._disposed = False
            self._lock = threading.Lock()

        def dispose(self) -> None:
            with self._lock:
                if self._disposed:
                    return
                self._disposed = True
            self._owner._unsubscribe(self._name, self._handler)

        def __enter__(self) -> "PluginEvents._Subscription":
            return self

        def __exit__(self, *exc: object) -> None:
            self.dispose()


class PluginEventNames:
    """(3.2.0) Well-known event names."""

    WorkspaceLoaded = "workspace.loaded"
    ChatMessage = "chat.message"
    ModelLoaded = "model.loaded"
    ModelUnloaded = "model.unloaded"
