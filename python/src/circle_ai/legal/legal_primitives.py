# legal_primitives.py
#
# Port of CircleAI.Legal LegalPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Legal vertical: matters,
# contracts, deadlines, clause library.
#
# C# ConcurrentDictionary stores map to plain dicts guarded by a single lock.
# IReadOnlyList<string> record fields map to tuple[str, ...] (immutable). C#
# OrderBy / OrderByDescending are stable, as is Python's sorted(). Tag matching
# is ordinal-ignore-case, mirrored with str.casefold().

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, replace
from datetime import datetime
from typing import Dict, List, Optional, Sequence, Tuple


@dataclass(frozen=True, slots=True)
class Matter:
    """Mirrors ``CircleAI.Legal.Matter`` — ``record(string MatterId, string Title,
    string Jurisdiction, string Client, DateTimeOffset OpenedAtUtc, bool Open)``.
    """

    matter_id: str
    title: str
    jurisdiction: str
    client: str
    opened_at_utc: datetime
    open: bool


@dataclass(frozen=True, slots=True)
class Contract:
    """Mirrors ``CircleAI.Legal.Contract`` — ``record(string ContractId,
    string MatterId, string Title, DateTime EffectiveDate, DateTime? ExpiryDate,
    IReadOnlyList<string> Counterparties)``.
    """

    contract_id: str
    matter_id: str
    title: str
    effective_date: datetime
    expiry_date: Optional[datetime]
    counterparties: Tuple[str, ...]


@dataclass(frozen=True, slots=True)
class LegalDeadline:
    """Mirrors ``CircleAI.Legal.LegalDeadline`` — ``record(string DeadlineId,
    string MatterId, string Description, DateTime DueOn)``.
    """

    deadline_id: str
    matter_id: str
    description: str
    due_on: datetime


@dataclass(frozen=True, slots=True)
class Clause:
    """Mirrors ``CircleAI.Legal.Clause`` — ``record(string ClauseId, string Title,
    string Body, IReadOnlyList<string> Tags)``.
    """

    clause_id: str
    title: str
    body: str
    tags: Tuple[str, ...]


class ILegalBoard(ABC):
    """In-memory board for matters, contracts, deadlines and clauses."""

    @abstractmethod
    def open(self, m: Matter) -> None:
        ...

    @abstractmethod
    def close(self, matter_id: str) -> None:
        ...

    @abstractmethod
    def get_matter(self, id: str) -> Optional[Matter]:
        ...

    @property
    @abstractmethod
    def active_matters(self) -> List[Matter]:
        ...

    @abstractmethod
    def add_contract(self, c: Contract) -> None:
        ...

    @abstractmethod
    def contracts_expiring_before(self, date: datetime) -> List[Contract]:
        ...

    @abstractmethod
    def add(self, d: LegalDeadline) -> None:
        ...

    @abstractmethod
    def upcoming_deadlines(self, now: datetime) -> List[LegalDeadline]:
        ...

    @abstractmethod
    def add_clause(self, c: Clause) -> None:
        ...

    @abstractmethod
    def clauses_by_tag(self, tag: str) -> List[Clause]:
        ...


class InMemoryLegalBoard(ILegalBoard):
    """Thread-safe in-memory :class:`ILegalBoard`."""

    def __init__(self) -> None:
        self._matters: Dict[str, Matter] = {}
        self._contracts: Dict[str, Contract] = {}
        self._deadlines: Dict[str, LegalDeadline] = {}
        self._clauses: Dict[str, Clause] = {}
        self._lock = threading.Lock()

    def open(self, m: Matter) -> None:
        if m is None:
            raise ValueError("matter must not be None")
        with self._lock:
            self._matters[m.matter_id] = m

    def close(self, matter_id: str) -> None:
        with self._lock:
            m = self._matters.get(matter_id)
            if m is None:
                raise RuntimeError(f"Unknown matter {matter_id}")
            self._matters[matter_id] = replace(m, open=False)

    def get_matter(self, id: str) -> Optional[Matter]:
        with self._lock:
            return self._matters.get(id)

    @property
    def active_matters(self) -> List[Matter]:
        with self._lock:
            rows = [m for m in self._matters.values() if m.open]
        return sorted(rows, key=lambda m: m.opened_at_utc, reverse=True)

    def add_contract(self, c: Contract) -> None:
        if c is None:
            raise ValueError("contract must not be None")
        with self._lock:
            self._contracts[c.contract_id] = c

    def contracts_expiring_before(self, date: datetime) -> List[Contract]:
        with self._lock:
            rows = [
                c for c in self._contracts.values()
                if c.expiry_date is not None and c.expiry_date <= date
            ]
        return sorted(rows, key=lambda c: c.expiry_date)

    def add(self, d: LegalDeadline) -> None:
        if d is None:
            raise ValueError("deadline must not be None")
        with self._lock:
            self._deadlines[d.deadline_id] = d

    def upcoming_deadlines(self, now: datetime) -> List[LegalDeadline]:
        with self._lock:
            rows = [d for d in self._deadlines.values() if d.due_on >= now]
        return sorted(rows, key=lambda d: d.due_on)

    def add_clause(self, c: Clause) -> None:
        if c is None:
            raise ValueError("clause must not be None")
        with self._lock:
            self._clauses[c.clause_id] = c

    def clauses_by_tag(self, tag: str) -> List[Clause]:
        if tag is None or tag.strip() == "":
            raise ValueError("tag required")
        needle = tag.casefold()
        with self._lock:
            return [
                c for c in self._clauses.values()
                if any(t.casefold() == needle for t in c.tags)
            ]
