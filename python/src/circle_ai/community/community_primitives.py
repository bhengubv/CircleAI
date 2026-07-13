# community_primitives.py
#
# Port of CircleAI.Community CommunityPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Community vertical:
# community groups, announcements, volunteer opportunities. C#
# ConcurrentDictionary -> dict; the announcements list is guarded by a single
# lock. DateTimeOffset -> datetime. GroupsForMember matches groups whose
# MemberIds contains the member; AnnouncementsFor returns the newest `limit`;
# Opportunities filters to those at/after UTC now, ordered by time.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Dict, List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class CommunityGroup:
    """Mirrors ``CircleAI.Community.CommunityGroup``."""

    group_id: str
    name: str
    purpose: str
    member_ids: Sequence[str]


@dataclass(frozen=True, slots=True)
class Announcement:
    """Mirrors ``CircleAI.Community.Announcement``."""

    announcement_id: str
    group_id: str
    title: str
    body: str
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class VolunteerOpportunity:
    """Mirrors ``CircleAI.Community.VolunteerOpportunity``."""

    opp_id: str
    group_id: str
    description: str
    volunteers_needed: int
    when_utc: datetime


class ICommunityBoard(ABC):
    """In-memory board for community groups, announcements and opportunities."""

    @abstractmethod
    def create(self, g: CommunityGroup) -> None:
        ...

    @abstractmethod
    def get_group(self, id: str) -> Optional[CommunityGroup]:
        ...

    @abstractmethod
    def groups_for_member(self, member_id: str) -> List[CommunityGroup]:
        ...

    @abstractmethod
    def post(self, a: Announcement) -> None:
        ...

    @abstractmethod
    def announcements_for(self, group_id: str, limit: int = 20) -> List[Announcement]:
        ...

    @abstractmethod
    def list(self, o: VolunteerOpportunity) -> None:
        ...

    @abstractmethod
    def opportunities(self) -> List[VolunteerOpportunity]:
        ...


class InMemoryCommunityBoard(ICommunityBoard):
    """Thread-safe in-memory :class:`ICommunityBoard`."""

    def __init__(self) -> None:
        self._groups: Dict[str, CommunityGroup] = {}
        self._annc: List[Announcement] = []
        self._opps: Dict[str, VolunteerOpportunity] = {}
        self._lock = threading.Lock()

    def create(self, g: CommunityGroup) -> None:
        if g is None:
            raise ValueError("community group must not be None")
        with self._lock:
            self._groups[g.group_id] = g

    def get_group(self, id: str) -> Optional[CommunityGroup]:
        with self._lock:
            return self._groups.get(id)

    def groups_for_member(self, member_id: str) -> List[CommunityGroup]:
        with self._lock:
            return [
                g for g in self._groups.values() if member_id in g.member_ids
            ]

    def post(self, a: Announcement) -> None:
        if a is None:
            raise ValueError("announcement must not be None")
        with self._lock:
            self._annc.append(a)

    def announcements_for(self, group_id: str, limit: int = 20) -> List[Announcement]:
        with self._lock:
            items = [a for a in self._annc if a.group_id == group_id]
        items.sort(key=lambda a: a.at_utc, reverse=True)
        return items[:limit]

    def list(self, o: VolunteerOpportunity) -> None:
        if o is None:
            raise ValueError("volunteer opportunity must not be None")
        with self._lock:
            self._opps[o.opp_id] = o

    def opportunities(self) -> List[VolunteerOpportunity]:
        now = datetime.now(timezone.utc)
        with self._lock:
            items = [o for o in self._opps.values() if o.when_utc >= now]
        items.sort(key=lambda o: o.when_utc)
        return items

    @property
    def group_count(self) -> int:
        """Number of community groups (C#: ``GroupCount``)."""
        with self._lock:
            return len(self._groups)

    def remove_group(self, group_id: str) -> bool:
        """Remove a group. Returns True if one was present (C#: ``RemoveGroup``)."""
        with self._lock:
            return self._groups.pop(group_id, None) is not None

    def add_member(self, group_id: str, member_id: str) -> bool:
        """Add ``member_id`` to a group. Returns False when the group is unknown
        or the member is already present (C#: ``AddMember``).
        """
        with self._lock:
            g = self._groups.get(group_id)
            if g is None:
                return False
            if member_id in g.member_ids:
                return False
            self._groups[group_id] = CommunityGroup(
                g.group_id,
                g.name,
                g.purpose,
                list(g.member_ids) + [member_id],
            )
            return True

    def remove_member(self, group_id: str, member_id: str) -> bool:
        """Remove ``member_id`` from a group. Returns False when the group is
        unknown or the member is not present (C#: ``RemoveMember``).
        """
        with self._lock:
            g = self._groups.get(group_id)
            if g is None:
                return False
            if member_id not in g.member_ids:
                return False
            self._groups[group_id] = CommunityGroup(
                g.group_id,
                g.name,
                g.purpose,
                [m for m in g.member_ids if m != member_id],
            )
            return True

    def opportunities_for_group(
        self, group_id: str
    ) -> List[VolunteerOpportunity]:
        """A group's volunteer opportunities (ordinal group match), earliest
        first (C#: ``OpportunitiesForGroup``).
        """
        with self._lock:
            matches = [
                o for o in self._opps.values() if o.group_id == group_id
            ]
        return sorted(matches, key=lambda o: o.when_utc)

    def total_volunteers_needed(self) -> int:
        """Total volunteers needed across every upcoming opportunity
        (C#: ``TotalVolunteersNeeded`` — sums the future-only ``Opportunities``).
        """
        return sum(o.volunteers_needed for o in self.opportunities())
