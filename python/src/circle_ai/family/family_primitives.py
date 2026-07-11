# family_primitives.py
#
# Port of CircleAI.Family FamilyPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Family vertical: family
# members, shared calendar events, shared expenses.
#
# C# ConcurrentDictionary stores map to plain dicts; the expense list is guarded
# by a single lock (mirroring the C# monitor lock). C# decimal money maps to
# decimal.Decimal, DateTimeOffset -> datetime, DateTime DateOfBirth -> datetime.
# `Members` orders by Name (ordinal). EventsForMember returns events whose
# MemberIds contain the member, ordered by AtUtc. TotalPaidBy/SpendByCategory sum
# expenses at/after `since`; SpendByCategory matches Category case-insensitively.
# An empty decimal sum is Decimal(0).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Dict, List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class FamilyMember:
    """Mirrors ``CircleAI.Family.FamilyMember`` — ``record(string MemberId,
    string Name, string Role, DateTime DateOfBirth)``.
    """

    member_id: str
    name: str
    role: str
    date_of_birth: datetime


@dataclass(frozen=True, slots=True)
class FamilyEvent:
    """Mirrors ``CircleAI.Family.FamilyEvent`` — ``record(string EventId,
    string Title, DateTimeOffset AtUtc, IReadOnlyList<string> MemberIds)``.
    """

    event_id: str
    title: str
    at_utc: datetime
    member_ids: Sequence[str]


@dataclass(frozen=True, slots=True)
class SharedExpense:
    """Mirrors ``CircleAI.Family.SharedExpense`` — ``record(string ExpenseId,
    string PaidById, decimal Amount, string Currency, string Category,
    DateTimeOffset AtUtc)``.
    """

    expense_id: str
    paid_by_id: str
    amount: Decimal
    currency: str
    category: str
    at_utc: datetime


class IFamilyBoard(ABC):
    """In-memory board for family members, events and shared expenses."""

    @abstractmethod
    def add(self, m: FamilyMember) -> None:
        ...

    @abstractmethod
    def get_member(self, id: str) -> Optional[FamilyMember]:
        ...

    @property
    @abstractmethod
    def members(self) -> List[FamilyMember]:
        ...

    @abstractmethod
    def schedule(self, e: FamilyEvent) -> None:
        ...

    @abstractmethod
    def events_for_member(self, member_id: str) -> List[FamilyEvent]:
        ...

    @abstractmethod
    def record(self, e: SharedExpense) -> None:
        ...

    @abstractmethod
    def total_paid_by(self, member_id: str, since: datetime) -> Decimal:
        ...

    @abstractmethod
    def spend_by_category(self, category: str, since: datetime) -> Decimal:
        ...


class InMemoryFamilyBoard(IFamilyBoard):
    """Thread-safe in-memory :class:`IFamilyBoard`."""

    def __init__(self) -> None:
        self._members: Dict[str, FamilyMember] = {}
        self._events: Dict[str, FamilyEvent] = {}
        self._expenses: List[SharedExpense] = []
        self._lock = threading.Lock()

    def add(self, m: FamilyMember) -> None:
        if m is None:
            raise ValueError("family member must not be None")
        with self._lock:
            self._members[m.member_id] = m

    def get_member(self, id: str) -> Optional[FamilyMember]:
        with self._lock:
            return self._members.get(id)

    @property
    def members(self) -> List[FamilyMember]:
        with self._lock:
            return sorted(self._members.values(), key=lambda m: m.name)

    def schedule(self, e: FamilyEvent) -> None:
        if e is None:
            raise ValueError("family event must not be None")
        with self._lock:
            self._events[e.event_id] = e

    def events_for_member(self, member_id: str) -> List[FamilyEvent]:
        with self._lock:
            rows = [e for e in self._events.values() if member_id in e.member_ids]
        return sorted(rows, key=lambda e: e.at_utc)

    def record(self, e: SharedExpense) -> None:
        if e is None:
            raise ValueError("shared expense must not be None")
        with self._lock:
            self._expenses.append(e)

    def total_paid_by(self, member_id: str, since: datetime) -> Decimal:
        with self._lock:
            return sum(
                (
                    e.amount
                    for e in self._expenses
                    if e.paid_by_id == member_id and e.at_utc >= since
                ),
                Decimal(0),
            )

    def spend_by_category(self, category: str, since: datetime) -> Decimal:
        with self._lock:
            return sum(
                (
                    e.amount
                    for e in self._expenses
                    if e.category.casefold() == category.casefold() and e.at_utc >= since
                ),
                Decimal(0),
            )
