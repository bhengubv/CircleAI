# personal_finance_primitives.py
#
# Port of CircleAI.Personal.Finance PersonalFinancePrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for personal finance: accounts,
# transactions, budgets, simple monthly summary.
#
# The accounts ConcurrentDictionary is ordinal; the budgets ConcurrentDictionary
# is ORDINAL-IGNORE-CASE — so SetBudget upserts case-insensitively while the
# stored BudgetLine keeps its own Category casing. The transaction List<> and
# the balance mutation are guarded by a single lock. C# decimal (exact money)
# maps to decimal.Decimal. Budgets are ordered by Category (ordinal). Summarise
# groups by category preserving first-seen order (LINQ GroupBy is order-
# preserving, as is Python's dict).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, replace
from datetime import datetime
from decimal import Decimal
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class Account:
    """Mirrors ``CircleAI.Personal.Finance.Account`` —
    ``record(string AccountId, string Name, decimal Balance, string Currency)``.
    """

    account_id: str
    name: str
    balance: Decimal
    currency: str


@dataclass(frozen=True, slots=True)
class FinanceTransaction:
    """Mirrors ``CircleAI.Personal.Finance.FinanceTransaction`` —
    ``record(string TxId, string AccountId, decimal Amount, string Category,
    string? Note, DateTimeOffset AtUtc)``.
    """

    tx_id: str
    account_id: str
    amount: Decimal
    category: str
    note: Optional[str]
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class BudgetLine:
    """Mirrors ``CircleAI.Personal.Finance.BudgetLine`` —
    ``record(string Category, decimal MonthlyLimit)``.
    """

    category: str
    monthly_limit: Decimal


@dataclass(frozen=True, slots=True)
class MonthSummary:
    """Mirrors ``CircleAI.Personal.Finance.MonthSummary`` — ``record(int Year,
    int Month, decimal TotalIn, decimal TotalOut,
    IReadOnlyDictionary<string, decimal> ByCategory)``.
    """

    year: int
    month: int
    total_in: Decimal
    total_out: Decimal
    by_category: Dict[str, Decimal]


class IPersonalFinanceBoard(ABC):
    """In-memory board for accounts, transactions and budgets."""

    @abstractmethod
    def upsert(self, a: Account) -> None:
        ...

    @abstractmethod
    def get_account(self, id: str) -> Optional[Account]:
        ...

    @abstractmethod
    def record(self, t: FinanceTransaction) -> None:
        ...

    @abstractmethod
    def list_for_month(self, account_id: str, year: int, month: int) -> List[FinanceTransaction]:
        ...

    @abstractmethod
    def set_budget(self, b: BudgetLine) -> None:
        ...

    @property
    @abstractmethod
    def budgets(self) -> List[BudgetLine]:
        ...

    @abstractmethod
    def summarise(self, account_id: str, year: int, month: int) -> MonthSummary:
        ...


class InMemoryPersonalFinanceBoard(IPersonalFinanceBoard):
    """Thread-safe in-memory :class:`IPersonalFinanceBoard`."""

    def __init__(self) -> None:
        self._accounts: Dict[str, Account] = {}
        # Case-insensitive budget keys (StringComparer.OrdinalIgnoreCase):
        # keyed by casefold(category), value keeps the original casing.
        self._budgets: Dict[str, BudgetLine] = {}
        self._txns: List[FinanceTransaction] = []
        self._lock = threading.Lock()

    def upsert(self, a: Account) -> None:
        if a is None:
            raise ValueError("account must not be None")
        with self._lock:
            self._accounts[a.account_id] = a

    def get_account(self, id: str) -> Optional[Account]:
        with self._lock:
            return self._accounts.get(id)

    def record(self, t: FinanceTransaction) -> None:
        if t is None:
            raise ValueError("transaction must not be None")
        with self._lock:
            if t.account_id not in self._accounts:
                raise RuntimeError(f"Unknown account {t.account_id}")
            self._txns.append(t)
            a = self._accounts[t.account_id]
            self._accounts[t.account_id] = replace(a, balance=a.balance + t.amount)

    def list_for_month(self, account_id: str, year: int, month: int) -> List[FinanceTransaction]:
        with self._lock:
            return [
                t
                for t in self._txns
                if t.account_id == account_id and t.at_utc.year == year and t.at_utc.month == month
            ]

    def set_budget(self, b: BudgetLine) -> None:
        if b is None:
            raise ValueError("budget line must not be None")
        with self._lock:
            self._budgets[b.category.casefold()] = b

    @property
    def budgets(self) -> List[BudgetLine]:
        with self._lock:
            values = list(self._budgets.values())
        return sorted(values, key=lambda b: b.category)

    def summarise(self, account_id: str, year: int, month: int) -> MonthSummary:
        rows = self.list_for_month(account_id, year, month)
        by_cat: Dict[str, Decimal] = {}
        for t in rows:
            by_cat[t.category] = by_cat.get(t.category, Decimal(0)) + t.amount
        in_sum = sum((t.amount for t in rows if t.amount > 0), Decimal(0))
        out_sum = -sum((t.amount for t in rows if t.amount < 0), Decimal(0))
        return MonthSummary(year, month, in_sum, out_sum, by_cat)
