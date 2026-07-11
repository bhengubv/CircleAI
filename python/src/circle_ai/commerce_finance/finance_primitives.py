# finance_primitives.py
#
# Port of CircleAI.Commerce.Finance FinancePrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Commerce.Finance vertical:
# invoices, invoice lines, payments, overdue tracking, outstanding balances.
#
# C# ConcurrentDictionary maps to a plain dict; the payments List<> is guarded
# by a single lock. C# decimal (exact money) maps to decimal.Decimal.
#
# RemainingOn mirrors the C# expression exactly:
#     billed = Σ line.Amount * (decimal)(1 + line.TaxPct / 100.0)
# In C# the `1 + TaxPct/100.0` term is computed in `double`, then cast to
# `decimal`. The faithful Python equivalent of `(decimal)(double)` is
# `Decimal(str(x))` — the shortest round-trippable decimal — NOT `Decimal(x)`,
# which would expand the binary float (e.g. 1.15 -> 1.14999…). Status
# comparisons are ordinal-ignore-case (str.casefold()).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, replace
from datetime import datetime
from decimal import Decimal
from typing import Dict, List, Optional, Sequence, Tuple


@dataclass(frozen=True, slots=True)
class InvoiceLine:
    """Mirrors ``CircleAI.Commerce.Finance.InvoiceLine`` —
    ``record(string Description, decimal Amount, double TaxPct)``.
    """

    description: str
    amount: Decimal
    tax_pct: float


@dataclass(frozen=True, slots=True)
class Invoice:
    """Mirrors ``CircleAI.Commerce.Finance.Invoice`` — ``record(string InvoiceId,
    string CustomerId, DateTime IssueDate, DateTime DueDate,
    IReadOnlyList<InvoiceLine> Lines, string Currency, string Status)``.
    """

    invoice_id: str
    customer_id: str
    issue_date: datetime
    due_date: datetime
    lines: Tuple[InvoiceLine, ...]
    currency: str
    status: str


@dataclass(frozen=True, slots=True)
class FinancePayment:
    """Mirrors ``CircleAI.Commerce.Finance.FinancePayment`` —
    ``record(string PaymentId, string InvoiceId, decimal Amount,
    DateTimeOffset AtUtc)``.
    """

    payment_id: str
    invoice_id: str
    amount: Decimal
    at_utc: datetime


class IInvoiceBoard(ABC):
    """In-memory board for invoices and payments."""

    @abstractmethod
    def issue(self, i: Invoice) -> None:
        ...

    @abstractmethod
    def get(self, invoice_id: str) -> Optional[Invoice]:
        ...

    @abstractmethod
    def record_payment(self, p: FinancePayment) -> None:
        ...

    @abstractmethod
    def mark_overdue(self, as_of: datetime) -> None:
        ...

    @abstractmethod
    def remaining_on(self, invoice_id: str) -> Decimal:
        ...

    @abstractmethod
    def total_outstanding(self) -> Decimal:
        ...

    @abstractmethod
    def overdue(self) -> List[Invoice]:
        ...


class InMemoryInvoiceBoard(IInvoiceBoard):
    """Thread-safe in-memory :class:`IInvoiceBoard`."""

    def __init__(self) -> None:
        self._invoices: Dict[str, Invoice] = {}
        self._payments: List[FinancePayment] = []
        self._lock = threading.Lock()

    def issue(self, i: Invoice) -> None:
        if i is None:
            raise ValueError("invoice must not be None")
        with self._lock:
            self._invoices[i.invoice_id] = i

    def get(self, invoice_id: str) -> Optional[Invoice]:
        with self._lock:
            return self._invoices.get(invoice_id)

    def record_payment(self, p: FinancePayment) -> None:
        if p is None:
            raise ValueError("payment must not be None")
        with self._lock:
            self._payments.append(p)

    def mark_overdue(self, as_of: datetime) -> None:
        with self._lock:
            targets = [
                i
                for i in self._invoices.values()
                if i.due_date < as_of and i.status.casefold() != "paid".casefold()
            ]
            for i in targets:
                self._invoices[i.invoice_id] = replace(i, status="Overdue")

    def remaining_on(self, invoice_id: str) -> Decimal:
        with self._lock:
            inv = self._invoices.get(invoice_id)
            if inv is None:
                return Decimal(0)
            billed = sum(
                (l.amount * Decimal(str(1 + l.tax_pct / 100.0)) for l in inv.lines),
                Decimal(0),
            )
            paid = sum(
                (p.amount for p in self._payments if p.invoice_id == invoice_id),
                Decimal(0),
            )
            return billed - paid

    def total_outstanding(self) -> Decimal:
        with self._lock:
            ids = list(self._invoices.keys())
        return sum((self.remaining_on(i) for i in ids), Decimal(0))

    def overdue(self) -> List[Invoice]:
        with self._lock:
            return [
                i for i in self._invoices.values()
                if i.status.casefold() == "overdue".casefold()
            ]
