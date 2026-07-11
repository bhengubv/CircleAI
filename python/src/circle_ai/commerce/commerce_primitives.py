# commerce_primitives.py
#
# Port of CircleAI.Commerce CommercePrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Commerce vertical:
# customers, orders, line items, lifetime value.
#
# C# ConcurrentDictionary maps to plain dicts; the line-item List<> is guarded
# by a single lock. C# decimal (exact money) maps to decimal.Decimal. C#
# OrderByDescending is stable, as is Python's sorted(). LifetimeValue sums
# Decimals; an empty sum is Decimal(0) to match C#'s decimal Sum default.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, replace
from datetime import datetime
from decimal import Decimal
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class CommerceCustomer:
    """Mirrors ``CircleAI.Commerce.CommerceCustomer`` — ``record(string CustomerId,
    string Name, string? Email, DateTimeOffset CreatedUtc)``.
    """

    customer_id: str
    name: str
    email: Optional[str]
    created_utc: datetime


@dataclass(frozen=True, slots=True)
class CommerceOrder:
    """Mirrors ``CircleAI.Commerce.CommerceOrder`` — ``record(string OrderId,
    string CustomerId, decimal Total, string Currency, string Status,
    DateTimeOffset AtUtc)``.
    """

    order_id: str
    customer_id: str
    total: Decimal
    currency: str
    status: str
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class CommerceLineItem:
    """Mirrors ``CircleAI.Commerce.CommerceLineItem`` — ``record(string LineId,
    string OrderId, string Sku, int Quantity, decimal UnitPrice)``.
    """

    line_id: str
    order_id: str
    sku: str
    quantity: int
    unit_price: Decimal


class ICommerceBoard(ABC):
    """In-memory board for customers, orders and line items."""

    @abstractmethod
    def add_customer(self, c: CommerceCustomer) -> None:
        ...

    @abstractmethod
    def get_customer(self, id: str) -> Optional[CommerceCustomer]:
        ...

    @abstractmethod
    def place(self, o: CommerceOrder) -> None:
        ...

    @abstractmethod
    def add_line(self, l: CommerceLineItem) -> None:
        ...

    @abstractmethod
    def update_status(self, order_id: str, status: str) -> None:
        ...

    @abstractmethod
    def orders_for(self, customer_id: str) -> List[CommerceOrder]:
        ...

    @abstractmethod
    def lines_for(self, order_id: str) -> List[CommerceLineItem]:
        ...

    @abstractmethod
    def lifetime_value(self, customer_id: str) -> Decimal:
        ...


class InMemoryCommerceBoard(ICommerceBoard):
    """Thread-safe in-memory :class:`ICommerceBoard`."""

    def __init__(self) -> None:
        self._customers: Dict[str, CommerceCustomer] = {}
        self._orders: Dict[str, CommerceOrder] = {}
        self._lines: List[CommerceLineItem] = []
        self._lock = threading.Lock()

    def add_customer(self, c: CommerceCustomer) -> None:
        if c is None:
            raise ValueError("customer must not be None")
        with self._lock:
            self._customers[c.customer_id] = c

    def get_customer(self, id: str) -> Optional[CommerceCustomer]:
        with self._lock:
            return self._customers.get(id)

    def place(self, o: CommerceOrder) -> None:
        if o is None:
            raise ValueError("order must not be None")
        with self._lock:
            self._orders[o.order_id] = o

    def add_line(self, l: CommerceLineItem) -> None:
        if l is None:
            raise ValueError("line item must not be None")
        with self._lock:
            self._lines.append(l)

    def update_status(self, order_id: str, status: str) -> None:
        with self._lock:
            o = self._orders.get(order_id)
            if o is None:
                raise RuntimeError(f"Unknown order {order_id}")
            self._orders[order_id] = replace(o, status=status)

    def orders_for(self, customer_id: str) -> List[CommerceOrder]:
        with self._lock:
            rows = [o for o in self._orders.values() if o.customer_id == customer_id]
        return sorted(rows, key=lambda o: o.at_utc, reverse=True)

    def lines_for(self, order_id: str) -> List[CommerceLineItem]:
        with self._lock:
            return [l for l in self._lines if l.order_id == order_id]

    def lifetime_value(self, customer_id: str) -> Decimal:
        return sum((o.total for o in self.orders_for(customer_id)), Decimal(0))
