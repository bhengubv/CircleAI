# null_implementations.py
#
# Port of CircleAI.Collaboration NullImplementations.cs (C# — the EXACT spec).
#
# (2.8.0) Fail-closed collaboration defaults. The C# `static readonly Instance`
# singleton maps to a module-level singleton set after each class body.

from __future__ import annotations

from typing import List, Optional

from .contracts import Channel, IChannelStore, IMessageStore, IPresence, Message, PresenceState


class NullChannelStore(IChannelStore):
    Instance: "NullChannelStore"

    @property
    def backend_id(self) -> str:
        return "null"

    async def get_async(self, id: str, ct: Optional[object] = None) -> Optional[Channel]:
        return None

    async def list_for_team_async(self, team_id: str, ct: Optional[object] = None) -> List[Channel]:
        return []


class NullMessageStore(IMessageStore):
    Instance: "NullMessageStore"

    @property
    def backend_id(self) -> str:
        return "null"

    async def post_async(self, m: Message, ct: Optional[object] = None) -> Message:
        return m

    async def read_async(self, ch: str, limit: int = 100, ct: Optional[object] = None) -> List[Message]:
        return []


class NullPresence(IPresence):
    Instance: "NullPresence"

    @property
    def backend_id(self) -> str:
        return "null"

    async def get_async(self, user_id: str, ct: Optional[object] = None) -> Optional[PresenceState]:
        return None


NullChannelStore.Instance = NullChannelStore()
NullMessageStore.Instance = NullMessageStore()
NullPresence.Instance = NullPresence()
