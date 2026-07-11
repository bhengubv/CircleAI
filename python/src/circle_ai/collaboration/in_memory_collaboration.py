# in_memory_collaboration.py
#
# Port of CircleAI.Collaboration InMemoryCollaboration.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory channel/message/presence stores. Messages kept
# per-channel; presence has online + last-seen timestamps.
#
# C# StringComparer.Ordinal dictionaries map to plain dicts. The C# `_lock`
# monitor guarding the per-channel message lists maps to threading.Lock.
# ArgumentException on whitespace input maps to ValueError.

from __future__ import annotations

import threading
from typing import Dict, List, Optional

from .contracts import Channel, IChannelStore, IMessageStore, IPresence, Message, PresenceState


def _require(value: str, name: str) -> None:
    if value is None or value.strip() == "":
        raise ValueError(f"{name} required")


class InMemoryChannelStore(IChannelStore):
    def __init__(self) -> None:
        self._items: Dict[str, Channel] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def upsert(self, c: Channel) -> None:
        if c is None:
            raise ValueError("c must not be None")
        with self._lock:
            self._items[c.channel_id] = c

    async def get_async(self, id: str, ct: Optional[object] = None) -> Optional[Channel]:
        _require(id, "id")
        with self._lock:
            return self._items.get(id)

    async def list_for_team_async(self, team_id: str, ct: Optional[object] = None) -> List[Channel]:
        _require(team_id, "teamId")
        with self._lock:
            matches = [c for c in self._items.values() if c.team_id == team_id]
        return sorted(matches, key=lambda c: c.name)


class InMemoryMessageStore(IMessageStore):
    def __init__(self) -> None:
        self._by_channel: Dict[str, List[Message]] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def post_async(self, msg: Message, ct: Optional[object] = None) -> Message:
        if msg is None:
            raise ValueError("msg must not be None")
        _require(msg.channel_id, "ChannelId")
        with self._lock:
            self._by_channel.setdefault(msg.channel_id, []).append(msg)
        return msg

    async def read_async(self, channel_id: str, limit: int = 100, ct: Optional[object] = None) -> List[Message]:
        _require(channel_id, "channelId")
        with self._lock:
            lst = self._by_channel.get(channel_id)
            if lst is None:
                return []
            ordered = sorted(lst, key=lambda m: m.at_utc, reverse=True)
            return ordered[:limit]


class InMemoryPresence(IPresence):
    def __init__(self) -> None:
        self._states: Dict[str, PresenceState] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def set(self, s: PresenceState) -> None:
        if s is None:
            raise ValueError("s must not be None")
        with self._lock:
            self._states[s.user_id] = s

    async def get_async(self, user_id: str, ct: Optional[object] = None) -> Optional[PresenceState]:
        _require(user_id, "userId")
        with self._lock:
            return self._states.get(user_id)
