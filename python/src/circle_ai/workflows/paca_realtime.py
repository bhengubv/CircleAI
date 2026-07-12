# paca_realtime.py
#
# Port of CircleAI.Workflows PacaRealtime.cs (C# — the EXACT spec).
#
# (3.3.0) Realtime fan-out for paca workflows: pub/sub with permission-aware
# rooms, query-invalidation events, collaborative document editing, agent
# activity feed. The Socket.IO / Valkey transport is host-supplied via
# IRealtimeBroadcaster.
#
# The C# abstract record RealtimePacaEvent + sealed subrecords map to a frozen
# base dataclass + frozen subclasses. The permission check + broadcaster are
# host-supplied seams (a callable and an ABC). QueryInvalidation.KeysFor is the
# C# switch expression as an isinstance chain (subclasses before base).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Awaitable, Callable, Dict, List, Optional

from .conversations import ConversationStep


@dataclass(frozen=True, slots=True)
class RealtimePacaEvent:
    """(3.3.0) Realtime event union base."""

    project_id: str
    at: datetime


@dataclass(frozen=True, slots=True)
class TaskUpdatedEvent(RealtimePacaEvent):
    task_number: int = 0


@dataclass(frozen=True, slots=True)
class QueryInvalidationEvent(RealtimePacaEvent):
    query_key: str = ""


@dataclass(frozen=True, slots=True)
class DocCursorMoveEvent(RealtimePacaEvent):
    doc_id: str = ""
    member_id: str = ""
    cursor_offset: int = 0


@dataclass(frozen=True, slots=True)
class AgentActivityEvent(RealtimePacaEvent):
    agent_member_id: str = ""
    action: str = ""
    detail_json: str = ""


@dataclass(frozen=True, slots=True)
class ConversationStepEvent(RealtimePacaEvent):
    conversation_id: str = ""
    step: Optional[ConversationStep] = None


class IRealtimeBroadcaster(ABC):
    """(3.3.0) Host-supplied broadcaster (Socket.IO / Valkey Streams / etc.)."""

    @abstractmethod
    async def broadcast_async(
        self, room: str, ev: RealtimePacaEvent, ct: Optional[object] = None
    ) -> None:
        ...


# (3.3.0) Permission check delegate — return True if the member may join the room.
PermissionCheck = Callable[[str, str, Optional[object]], Awaitable[bool]]


class PacaRealtimeHub:
    """(3.3.0) Realtime hub: routes events into rooms, gates joins with a
    permission check."""

    def __init__(
        self, broadcaster: IRealtimeBroadcaster, permission: Optional[PermissionCheck] = None
    ) -> None:
        if broadcaster is None:
            raise ValueError("broadcaster must not be None")
        self._broadcaster = broadcaster
        if permission is not None:
            self._permission = permission
        else:
            async def _allow(_member_id: str, _room: str, _ct: Optional[object]) -> bool:
                return True

            self._permission = _allow
        self._members_by_room: Dict[str, Dict[str, int]] = {}
        self._lock = threading.Lock()

    async def join_async(self, member_id: str, room: str, ct: Optional[object] = None) -> bool:
        """(3.3.0) Member tries to join a room. Returns True if permission
        allowed."""
        if member_id is None:
            raise ValueError("member_id must not be None")
        if room is None:
            raise ValueError("room must not be None")
        if not await self._permission(member_id, room, ct):
            return False
        with self._lock:
            members = self._members_by_room.get(room)
            if members is None:
                members = {}
                self._members_by_room[room] = members
            members[member_id] = 1
        return True

    def leave(self, member_id: str, room: str) -> None:
        with self._lock:
            bucket = self._members_by_room.get(room)
            if bucket is not None:
                bucket.pop(member_id, None)

    def members(self, room: str) -> List[str]:
        with self._lock:
            bucket = self._members_by_room.get(room)
            return list(bucket.keys()) if bucket is not None else []

    async def publish_async(self, ev: RealtimePacaEvent, ct: Optional[object] = None) -> None:
        """(3.3.0) Publish an event to the project's main room."""
        if ev is None:
            raise ValueError("ev must not be None")
        await self._broadcaster.broadcast_async(f"project:{ev.project_id}", ev, ct)

    async def publish_to_doc_async(
        self, doc_id: str, ev: RealtimePacaEvent, ct: Optional[object] = None
    ) -> None:
        """(3.3.0) Publish to a doc collaboration sub-room."""
        await self._broadcaster.broadcast_async(f"doc:{doc_id}", ev, ct)


class QueryInvalidation:
    """(3.3.0) Helper that maps known events to query-invalidation keys for
    client UIs."""

    @staticmethod
    def keys_for(ev: RealtimePacaEvent) -> List[str]:
        # Order matters: check concrete subclasses before the base type, and
        # QueryInvalidationEvent must precede the RealtimePacaEvent catch-all
        # (which, in C#, has no default case for the base and returns empty).
        if isinstance(ev, TaskUpdatedEvent):
            return [f"tasks/{ev.project_id}", f"task/{ev.project_id}/{ev.task_number}"]
        if isinstance(ev, AgentActivityEvent):
            return [f"activity/{ev.project_id}", f"agent/{ev.agent_member_id}"]
        if isinstance(ev, ConversationStepEvent):
            return [f"conversation/{ev.conversation_id}", f"conversations/{ev.project_id}"]
        if isinstance(ev, DocCursorMoveEvent):
            return [f"doc/{ev.doc_id}/cursors"]
        if isinstance(ev, QueryInvalidationEvent):
            return [ev.query_key]
        return []
