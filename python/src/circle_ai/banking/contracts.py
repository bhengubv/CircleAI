# contracts.py
#
# Port of CircleAI.Banking Contracts.cs (C# — the EXACT spec).
#
# (2.8.0) Banking contracts. Real backends 2.8.1.
#
# C# ValueTask<T> maps to async def -> T. C# records map to frozen slotted
# dataclasses. C# decimal (exact money) maps to decimal.Decimal.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import List, Optional


@dataclass(frozen=True, slots=True)
class Account:
    """Mirrors ``CircleAI.Banking.Account`` —
    ``record(string AccountId, string OwnerId, string Currency, decimal Balance)``.
    """

    account_id: str
    owner_id: str
    currency: str
    balance: Decimal


@dataclass(frozen=True, slots=True)
class LedgerEntry:
    """Mirrors ``CircleAI.Banking.LedgerEntry`` — ``record(string TxId,
    string AccountId, decimal Amount, string Memo, DateTimeOffset AtUtc)``.
    """

    tx_id: str
    account_id: str
    amount: Decimal
    memo: str
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class PaymentRequest:
    """Mirrors ``CircleAI.Banking.PaymentRequest`` — ``record(string FromAccount,
    string ToAccount, decimal Amount, string Currency, string Memo)``.
    """

    from_account: str
    to_account: str
    amount: Decimal
    currency: str
    memo: str


@dataclass(frozen=True, slots=True)
class PaymentResult:
    """Mirrors ``CircleAI.Banking.PaymentResult`` —
    ``record(string TxId, bool Accepted, string? FailureReason)``.
    """

    tx_id: str
    accepted: bool
    failure_reason: Optional[str]


class IAccountReader(ABC):
    """(2.8.0) Read-side of the banking contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def get_account_async(self, account_id: str, ct: Optional[object] = None) -> Optional[Account]:
        ...

    @abstractmethod
    async def list_for_owner_async(self, owner_id: str, ct: Optional[object] = None) -> List[Account]:
        ...


class ILedgerWriter(ABC):
    """(2.8.0) Append-only ledger contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def append_async(self, entry: LedgerEntry, ct: Optional[object] = None) -> LedgerEntry:
        ...

    @abstractmethod
    async def read_async(self, account_id: str, limit: int = 100, ct: Optional[object] = None) -> List[LedgerEntry]:
        ...


class IPaymentProcessor(ABC):
    """(2.8.0) Payment processor contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def process_async(self, req: PaymentRequest, ct: Optional[object] = None) -> PaymentResult:
        ...
