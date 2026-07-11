# plugin_context.py
#
# Port of CircleAI.Plugins PluginContext.cs (C# — the EXACT spec).
#
# (3.2.0) Default IPluginContext + permission-gated wrapper. The workspace path
# is a Func<string?> accessor; PermissionedPluginContext gates the workspace by
# workspace.read/workspace.write and events by events.subscribe, substituting a
# drop-on-the-floor SilentEvents bus when the subscribe permission is absent.
# Permission names are matched case-insensitively (StringComparer.OrdinalIgnoreCase).

from __future__ import annotations

from typing import Callable, Iterable, Optional

from .plugin import EventHandler, IPluginContext, IPluginEvents


class PluginContext(IPluginContext):
    """(3.2.0) Default :class:`IPluginContext`."""

    def __init__(
        self,
        workspace_path_accessor: Optional[Callable[[], Optional[str]]],
        events: IPluginEvents,
        logger: Optional[object],
    ) -> None:
        self._workspace_path = workspace_path_accessor if workspace_path_accessor is not None else (lambda: None)
        if events is None:
            raise ValueError("events must not be None")
        self._events = events
        # The C# ctor requires a non-null ILogger; here the logger is an opaque
        # injected value and may legitimately be a null-logger sentinel, so we
        # accept None.
        self._logger = logger

    @property
    def workspace_path(self) -> Optional[str]:
        return self._workspace_path()

    @property
    def events(self) -> IPluginEvents:
        return self._events

    @property
    def logger(self) -> Optional[object]:
        return self._logger


class _SilentEvents(IPluginEvents):
    """Drop-on-the-floor event bus for permission-denied plugins."""

    def subscribe(self, event_name: str, handler: EventHandler) -> object:
        return _NoopDisposable.Instance

    def raise_event(self, event_name: str, payload: Optional[object]) -> None:
        pass


class _NoopDisposable:
    Instance: "_NoopDisposable"

    def dispose(self) -> None:
        pass

    def __enter__(self) -> "_NoopDisposable":
        return self

    def __exit__(self, *exc: object) -> None:
        pass


_NoopDisposable.Instance = _NoopDisposable()


class PermissionedPluginContext(IPluginContext):
    """(3.2.0) Wraps an inner context and gates capabilities by a granted-
    permission set."""

    class Permissions:
        WorkspaceRead = "workspace.read"
        WorkspaceWrite = "workspace.write"
        EventsSubscribe = "events.subscribe"

    def __init__(self, inner: IPluginContext, granted_permissions: Optional[Iterable[str]]) -> None:
        if inner is None:
            raise ValueError("inner must not be None")
        self._inner = inner
        # Case-insensitive granted set (StringComparer.OrdinalIgnoreCase).
        self._granted = {p.casefold() for p in (granted_permissions or [])}
        self._events = (
            self._inner.events
            if PermissionedPluginContext.Permissions.EventsSubscribe.casefold() in self._granted
            else _SilentEvents()
        )

    @property
    def workspace_path(self) -> Optional[str]:
        if (
            PermissionedPluginContext.Permissions.WorkspaceRead.casefold() in self._granted
            or PermissionedPluginContext.Permissions.WorkspaceWrite.casefold() in self._granted
        ):
            return self._inner.workspace_path
        return None

    @property
    def events(self) -> IPluginEvents:
        return self._events

    @property
    def logger(self) -> Optional[object]:
        return self._inner.logger
