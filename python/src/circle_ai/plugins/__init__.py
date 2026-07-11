"""circle_ai.plugins — port of the CircleAI.Plugins assembly (contract surface).

(3.2.0) Plugin contract surface: the IPlugin / IPluginContext / IPluginEvents
contracts, the thread-safe default PluginEvents bus, well-known PluginEventNames,
the default PluginContext, and the permission-gated PermissionedPluginContext.
C# is the exact spec. (The assembly-loading PluginLoader / PluginRegistry /
PluginLifecycleService host layers depend on .NET AssemblyLoadContext +
IHostedService and are out of this in-memory unit's scope.)

Public surface:

  * IPlugin / IPluginContext / IPluginEvents              — contracts.
  * PluginEvents                                          — default event bus.
  * PluginEventNames                                      — well-known names.
  * PluginContext / PermissionedPluginContext            — context + gated wrapper.
"""
from __future__ import annotations

from .plugin import (
    EventHandler,
    IPlugin,
    IPluginContext,
    IPluginEvents,
    PluginEventNames,
    PluginEvents,
)
from .plugin_context import PermissionedPluginContext, PluginContext

__all__ = [
    "IPlugin",
    "IPluginContext",
    "IPluginEvents",
    "EventHandler",
    "PluginEvents",
    "PluginEventNames",
    "PluginContext",
    "PermissionedPluginContext",
]
