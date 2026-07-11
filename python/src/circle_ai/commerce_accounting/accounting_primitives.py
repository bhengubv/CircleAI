# accounting_primitives.py
#
# Port of CircleAI.Commerce.Accounting AccountingPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Commerce.Accounting
# vertical: double-entry ledger, tax rates, per-period sums, net profit.
#
# The entry List<> and tax ConcurrentDictionary map to a plain list + dict
# guarded by a single lock. C# decimal (exact money) maps to decimal.Decimal.
# Balances / sums are debit − credit. C# OrderBy is stable, as is Python's
# sorted(). Empty decimal Sum() is Decimal(0).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class AccountingEntry:
    """Mirrors ``CircleAI.Commerce.Accounting.AccountingEntry`` —
    ``record(string EntryId, DateTime AtUtc, string AccountCode,
    decimal DebitAmount, decimal CreditAmount, string Memo)``.
    """

    entry_id: str
    at_utc: datetime
    account_code: str
    debit_amount: Decimal
    credit_amount: Decimal
    memo: str


@dataclass(frozen=True, slots=True)
class TaxRate:
    """Mirrors ``CircleAI.Commerce.Accounting.TaxRate`` —
    ``record(string Code, double Percentage)``.
    """

    code: str
    percentage: float


@dataclass(frozen=True, slots=True)
class Period:
    """Mirrors ``CircleAI.Commerce.Accounting.Period`` —
    ``record(int Year, int Month)``.
    """

    year: int
    month: int


class IAccountingBoard(ABC):
    """In-memory double-entry accounting board."""

    @abstractmethod
    def post(self, e: AccountingEntry) -> None:
        ...

    @abstractmethod
    def define_tax(self, r: TaxRate) -> None:
        ...

    @abstractmethod
    def get_tax(self, code: str) -> Optional[TaxRate]:
        ...

    @abstractmethod
    def account_balance(self, account_code: str) -> Decimal:
        ...

    @abstractmethod
    def sum(self, account_code: str, p: Period) -> Decimal:
        ...

    @abstractmethod
    def for_account(self, account_code: str, p: Period) -> List[AccountingEntry]:
        ...

    @abstractmethod
    def net_profit(self, p: Period, revenue_account: str, expense_account: str) -> Decimal:
        ...


class InMemoryAccountingBoard(IAccountingBoard):
    """Thread-safe in-memory :class:`IAccountingBoard`."""

    def __init__(self) -> None:
        self._entries: List[AccountingEntry] = []
        self._tax: Dict[str, TaxRate] = {}
        self._lock = threading.Lock()

    def post(self, e: AccountingEntry) -> None:
        if e is None:
            raise ValueError("entry must not be None")
        if e.debit_amount < 0 or e.credit_amount < 0:
            raise ValueError("amounts must be non-negative")
        with self._lock:
            self._entries.append(e)

    def define_tax(self, r: TaxRate) -> None:
        if r is None:
            raise ValueError("tax rate must not be None")
        with self._lock:
            self._tax[r.code] = r

    def get_tax(self, code: str) -> Optional[TaxRate]:
        with self._lock:
            return self._tax.get(code)

    def account_balance(self, account_code: str) -> Decimal:
        with self._lock:
            return sum(
                (e.debit_amount - e.credit_amount for e in self._entries if e.account_code == account_code),
                Decimal(0),
            )

    def sum(self, account_code: str, p: Period) -> Decimal:
        with self._lock:
            return sum(
                (
                    e.debit_amount - e.credit_amount
                    for e in self._entries
                    if e.account_code == account_code and e.at_utc.year == p.year and e.at_utc.month == p.month
                ),
                Decimal(0),
            )

    def for_account(self, account_code: str, p: Period) -> List[AccountingEntry]:
        with self._lock:
            rows = [
                e
                for e in self._entries
                if e.account_code == account_code and e.at_utc.year == p.year and e.at_utc.month == p.month
            ]
        return sorted(rows, key=lambda e: e.at_utc)

    def net_profit(self, p: Period, revenue_account: str, expense_account: str) -> Decimal:
        return self.sum(revenue_account, p) - self.sum(expense_account, p)
