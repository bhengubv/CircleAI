# contracts.py
#
# Port of CircleAI.CRM Contracts.cs (C# — the EXACT spec).
#
# (2.8.0) CRM contracts. Real in-memory backends 3.3.0.
#
# C# ValueTask/ValueTask<T> maps to async def -> None/T. C# records map to frozen
# slotted dataclasses. C# decimal (exact money) maps to decimal.Decimal,
# DateTimeOffset -> datetime. The optional CancellationToken is carried as an
# opt-in `ct` argument (ignored by the in-memory backends).

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import List, Optional


@dataclass(frozen=True, slots=True)
class Contact:
    """Mirrors ``CircleAI.CRM.Contact`` — ``record(string ContactId,
    string FullName, string? Email, string? Phone, string? CompanyId)``.
    """

    contact_id: str
    full_name: str
    email: Optional[str]
    phone: Optional[str]
    company_id: Optional[str]


@dataclass(frozen=True, slots=True)
class Company:
    """Mirrors ``CircleAI.CRM.Company`` — ``record(string CompanyId,
    string Name, string? Industry)``.
    """

    company_id: str
    name: str
    industry: Optional[str]


@dataclass(frozen=True, slots=True)
class Deal:
    """Mirrors ``CircleAI.CRM.Deal`` — ``record(string DealId, string CompanyId,
    string Name, decimal Value, string Currency, string Stage)``.
    """

    deal_id: str
    company_id: str
    name: str
    value: Decimal
    currency: str
    stage: str


@dataclass(frozen=True, slots=True)
class Activity:
    """Mirrors ``CircleAI.CRM.Activity`` — ``record(string ActivityId,
    string ContactId, string Kind, string Body, DateTimeOffset AtUtc)``.
    """

    activity_id: str
    contact_id: str
    kind: str
    body: str
    at_utc: datetime


class IContactStore(ABC):
    """(2.8.0) Contact store contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def upsert_async(self, c: Contact, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def get_async(self, id: str, ct: Optional[object] = None) -> Optional[Contact]:
        ...

    @abstractmethod
    async def search_async(
        self, query: str, top_k: int = 20, ct: Optional[object] = None
    ) -> List[Contact]:
        ...


class IDealPipeline(ABC):
    """(2.8.0) Deal pipeline contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def upsert_async(self, d: Deal, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def get_async(self, id: str, ct: Optional[object] = None) -> Optional[Deal]:
        ...

    @abstractmethod
    async def list_by_stage_async(
        self, stage: str, ct: Optional[object] = None
    ) -> List[Deal]:
        ...


class IActivityLog(ABC):
    """(2.8.0) Per-contact activity log contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def append_async(self, a: Activity, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def read_for_contact_async(
        self, contact_id: str, limit: int = 100, ct: Optional[object] = None
    ) -> List[Activity]:
        ...
