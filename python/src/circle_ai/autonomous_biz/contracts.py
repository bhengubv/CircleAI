# contracts.py
#
# Port of CircleAI.AutonomousBiz Contracts.cs (C# — the EXACT spec).
#
# (3.0.0) Autonomous business contracts: treasury, revenue loop, decision log.
# C# records map to frozen slotted dataclasses; C# decimal maps to Decimal;
# C# IDisposable subscription tokens map to objects with a close()/__enter__.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Awaitable, Callable, List, Optional


@dataclass(frozen=True, slots=True)
class TreasurySnapshot:
    """Mirrors ``CircleAI.AutonomousBiz.TreasurySnapshot`` —
    ``record(decimal Balance, string Currency, DateTimeOffset AtUtc)``."""

    balance: Decimal
    currency: str
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class RevenueEvent:
    """Mirrors ``CircleAI.AutonomousBiz.RevenueEvent`` — ``record(string EventId,
    decimal Amount, string Currency, string Source, DateTimeOffset AtUtc)``."""

    event_id: str
    amount: Decimal
    currency: str
    source: str
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class AutonomousDecision:
    """Mirrors ``CircleAI.AutonomousBiz.AutonomousDecision`` —
    ``record(string DecisionId, string Rationale, string ChosenAction,
    DateTimeOffset AtUtc)``."""

    decision_id: str
    rationale: str
    chosen_action: str
    at_utc: datetime


# C# Func<RevenueEvent, ValueTask> handler.
RevenueHandler = Callable[[RevenueEvent], Awaitable[None]]


class ITreasury(ABC):
    """(3.0.0) Treasury snapshot contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def get_snapshot_async(self, ct: Optional[object] = None) -> TreasurySnapshot:
        ...


class IRevenueLoop(ABC):
    """(3.0.0) Revenue-event pub/sub with kept history."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    def subscribe(self, handler: RevenueHandler) -> object:
        """Returns an IDisposable-style token (has a ``dispose()`` method and is
        a context manager)."""
        ...

    @abstractmethod
    async def read_async(self, since: datetime, ct: Optional[object] = None) -> List[RevenueEvent]:
        ...


class IDecisionLog(ABC):
    """(3.0.0) Append-only decision log."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def append_async(self, d: AutonomousDecision, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def read_async(self, limit: int = 100, ct: Optional[object] = None) -> List[AutonomousDecision]:
        ...
