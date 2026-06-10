"""IAIObserver — port of CircleAI.Hosting.IAIObserver.

Hosts implement this Protocol to receive lifecycle + inference events
from the SDK without modifying it. Methods are async, mirroring the C#
ValueTask default-method pattern.

All methods have default no-op implementations on a base class so
hosts only need to override what they care about.
"""
from __future__ import annotations

from typing import Optional, Protocol, runtime_checkable

from ..models.models import ChatResponse, UpgradeInfo


@runtime_checkable
class IAIObserver(Protocol):
    """Observer for AIService lifecycle + inference events."""

    async def on_started_async(self) -> None: ...
    async def on_stopped_async(self) -> None: ...
    async def on_chat_completed_async(self, response: ChatResponse) -> None: ...
    async def on_stream_started_async(self, model_id: str) -> None: ...
    async def on_stream_completed_async(self, model_id: str, token_count: int) -> None: ...
    async def on_tool_invoked_async(
        self, tool_name: str, success: bool
    ) -> None: ...

    async def on_model_fetching_async(
        self, model_id: str, auto_selected: bool
    ) -> None: ...

    async def on_upgrade_available_async(self, upgrade: UpgradeInfo) -> None: ...


class AIObserverBase:
    """No-op base class implementing every IAIObserver method.

    Python Protocols don't carry default implementations, so this base
    class is the practical equivalent of the C# default-interface-method
    pattern. Subclass and override only what you care about.
    """

    async def on_started_async(self) -> None:
        return None

    async def on_stopped_async(self) -> None:
        return None

    async def on_chat_completed_async(self, response: ChatResponse) -> None:
        return None

    async def on_stream_started_async(self, model_id: str) -> None:
        return None

    async def on_stream_completed_async(
        self, model_id: str, token_count: int
    ) -> None:
        return None

    async def on_tool_invoked_async(self, tool_name: str, success: bool) -> None:
        return None

    async def on_model_fetching_async(
        self, model_id: str, auto_selected: bool
    ) -> None:
        return None

    async def on_upgrade_available_async(self, upgrade: UpgradeInfo) -> None:
        return None
