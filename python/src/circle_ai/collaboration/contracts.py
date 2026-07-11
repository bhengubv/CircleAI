# contracts.py
#
# Port of CircleAI.Collaboration Contracts.cs (C# — the EXACT spec).
#
# (2.8.0) Collaboration contracts: channels, messages, presence. C# records map
# to frozen slotted dataclasses; C# ValueTask<T> maps to async def -> T.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import List, Optional


@dataclass(frozen=True, slots=True)
class Channel:
    """Mirrors ``CircleAI.Collaboration.Channel`` —
    ``record(string ChannelId, string Name, string TeamId)``."""

    channel_id: str
    name: str
    team_id: str


@dataclass(frozen=True, slots=True)
class Message:
    """Mirrors ``CircleAI.Collaboration.Message`` — ``record(string MessageId,
    string ChannelId, string AuthorId, string Body, DateTimeOffset AtUtc)``."""

    message_id: str
    channel_id: str
    author_id: str
    body: str
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class PresenceState:
    """Mirrors ``CircleAI.Collaboration.PresenceState`` —
    ``record(string UserId, bool Online, DateTimeOffset LastSeenUtc)``."""

    user_id: str
    online: bool
    last_seen_utc: datetime


class IChannelStore(ABC):
    """(2.8.0) Channel store contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def get_async(self, id: str, ct: Optional[object] = None) -> Optional[Channel]:
        ...

    @abstractmethod
    async def list_for_team_async(self, team_id: str, ct: Optional[object] = None) -> List[Channel]:
        ...


class IMessageStore(ABC):
    """(2.8.0) Message store contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def post_async(self, msg: Message, ct: Optional[object] = None) -> Message:
        ...

    @abstractmethod
    async def read_async(self, channel_id: str, limit: int = 100, ct: Optional[object] = None) -> List[Message]:
        ...


class IPresence(ABC):
    """(2.8.0) Presence contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def get_async(self, user_id: str, ct: Optional[object] = None) -> Optional[PresenceState]:
        ...
