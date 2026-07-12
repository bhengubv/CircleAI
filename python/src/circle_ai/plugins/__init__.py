"""circle_ai.plugins — port of the CircleAI.Plugins assembly (contract surface).

(3.2.0) Plugin contract surface: the IPlugin / IPluginContext / IPluginEvents
contracts, the thread-safe default PluginEvents bus, well-known PluginEventNames,
the default PluginContext, and the permission-gated PermissionedPluginContext.
C# is the exact spec. (The assembly-loading PluginLoader / PluginLifecycleService
host layers depend on .NET AssemblyLoadContext + IHostedService and are out of
scope.) The JSON-persisted installed-plugin registry + marketplace catalog CRUD
IS portable and is included here.

Public surface:

  * IPlugin / IPluginContext / IPluginEvents              — contracts.
  * PluginEvents                                          — default event bus.
  * PluginEventNames                                      — well-known names.
  * PluginContext / PermissionedPluginContext            — context + gated wrapper.
  * RegisteredPlugin / PluginRegistry                    — JSON-persisted install/enable/permission CRUD.
  * MarketplaceEntry / PluginMarketplace                 — catalog list/search.
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
from .registry import (
    MarketplaceEntry,
    PluginMarketplace,
    PluginRegistry,
    RegisteredPlugin,
)

__all__ = [
    "IPlugin",
    "IPluginContext",
    "IPluginEvents",
    "EventHandler",
    "PluginEvents",
    "PluginEventNames",
    "PluginContext",
    "PermissionedPluginContext",
    "RegisteredPlugin",
    "PluginRegistry",
    "MarketplaceEntry",
    "PluginMarketplace",
]
