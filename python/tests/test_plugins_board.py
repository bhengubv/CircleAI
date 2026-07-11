"""test_plugins_board.py — CircleAI.Plugins port.

Covers the PluginEvents bus (subscribe/raise/unsubscribe, exception isolation,
well-known names), the default PluginContext (workspace accessor + events +
logger), and the PermissionedPluginContext gating (workspace by
read/write, events by subscribe -> SilentEvents otherwise). C# is the exact spec.
"""
from __future__ import annotations

import pytest

from circle_ai.plugins import (
    IPluginContext,
    IPluginEvents,
    PermissionedPluginContext,
    PluginContext,
    PluginEventNames,
    PluginEvents,
)


def test_event_names():
    assert PluginEventNames.WorkspaceLoaded == "workspace.loaded"
    assert PluginEventNames.ChatMessage == "chat.message"
    assert PluginEventNames.ModelLoaded == "model.loaded"
    assert PluginEventNames.ModelUnloaded == "model.unloaded"


def test_events_subscribe_raise_unsubscribe():
    ev = PluginEvents()
    assert isinstance(ev, IPluginEvents)
    seen: list = []
    sub = ev.subscribe("topic", lambda p: seen.append(p))
    ev.raise_event("topic", 42)
    assert seen == [42]
    ev.raise_event("other", "x")  # no subscriber -> ignored
    assert seen == [42]
    sub.dispose()
    ev.raise_event("topic", 99)
    assert seen == [42]  # unsubscribed
    sub.dispose()  # idempotent


def test_events_subscribe_guards():
    ev = PluginEvents()
    with pytest.raises(ValueError):
        ev.subscribe("", lambda p: None)
    with pytest.raises(ValueError):
        ev.subscribe("t", None)  # type: ignore[arg-type]


def test_events_isolate_thrown_exceptions():
    ev = PluginEvents()
    seen: list = []

    def bad(p):
        raise RuntimeError("boom")

    ev.subscribe("t", bad)
    ev.subscribe("t", lambda p: seen.append(p))
    ev.raise_event("t", "ok")  # bad handler must not stop the good one
    assert seen == ["ok"]


def test_plugin_context_exposes_workspace_events_logger():
    ev = PluginEvents()
    logger = object()
    ctx = PluginContext(lambda: "/ws", ev, logger)
    assert isinstance(ctx, IPluginContext)
    assert ctx.workspace_path == "/ws"
    assert ctx.events is ev
    assert ctx.logger is logger


def test_plugin_context_null_accessor_yields_none():
    ctx = PluginContext(None, PluginEvents(), None)
    assert ctx.workspace_path is None


def test_plugin_context_requires_events():
    with pytest.raises(ValueError):
        PluginContext(lambda: None, None, None)  # type: ignore[arg-type]


def test_permissioned_context_gates_workspace():
    inner = PluginContext(lambda: "/ws", PluginEvents(), None)
    denied = PermissionedPluginContext(inner, [])
    assert denied.workspace_path is None  # no read/write perm
    read = PermissionedPluginContext(inner, [PermissionedPluginContext.Permissions.WorkspaceRead])
    assert read.workspace_path == "/ws"
    write = PermissionedPluginContext(inner, ["WORKSPACE.WRITE"])  # case-insensitive
    assert write.workspace_path == "/ws"


def test_permissioned_context_gates_events():
    real = PluginEvents()
    inner = PluginContext(lambda: None, real, None)

    granted = PermissionedPluginContext(inner, [PermissionedPluginContext.Permissions.EventsSubscribe])
    assert granted.events is real  # real bus passed through

    denied = PermissionedPluginContext(inner, [])
    seen: list = []
    denied.events.subscribe("t", lambda p: seen.append(p))
    denied.events.raise_event("t", 1)
    assert seen == []  # silent bus swallows everything


def test_permissioned_context_requires_inner():
    with pytest.raises(ValueError):
        PermissionedPluginContext(None, [])  # type: ignore[arg-type]
