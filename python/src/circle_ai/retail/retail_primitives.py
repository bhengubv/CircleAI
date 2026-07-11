# retail_primitives.py
#
# Port of CircleAI.Retail RetailPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory store for the Retail vertical:
# products, stock levels, sales, daily summary.
#
# C# ConcurrentDictionary stores map to plain dicts; the sales list is guarded
# by a single lock (mirroring the C# monitor lock). C# decimal Price/UnitPrice
# maps to decimal.Decimal, DateTimeOffset -> datetime. RevenueToday compares by
# calendar date (``AtUtc.Date == now.Date``). TopSellersSince returns a list of
# (sku, sold) tuples ordered by units sold descending, limited to topK; topK<=0
# raises. RecordSale rejects an unknown SKU and decrements stock.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Dict, List, Optional, Tuple


@dataclass(frozen=True, slots=True)
class Product:
    """Mirrors ``CircleAI.Retail.Product`` — ``record(string Sku, string Name,
    decimal Price, string Currency, string? Category)``.
    """

    sku: str
    name: str
    price: Decimal
    currency: str
    category: Optional[str]


@dataclass(frozen=True, slots=True)
class StockLevel:
    """Mirrors ``CircleAI.Retail.StockLevel`` — ``record(string Sku, int Quantity)``."""

    sku: str
    quantity: int


@dataclass(frozen=True, slots=True)
class Sale:
    """Mirrors ``CircleAI.Retail.Sale`` — ``record(string SaleId, string Sku,
    int Quantity, decimal UnitPrice, DateTimeOffset AtUtc)``.
    """

    sale_id: str
    sku: str
    quantity: int
    unit_price: Decimal
    at_utc: datetime


class IRetailBoard(ABC):
    """In-memory board for products, stock and sales."""

    @abstractmethod
    def add_product(self, p: Product) -> None:
        ...

    @abstractmethod
    def get_product(self, sku: str) -> Optional[Product]:
        ...

    @abstractmethod
    def set_stock(self, l: StockLevel) -> None:
        ...

    @abstractmethod
    def stock(self, sku: str) -> int:
        ...

    @abstractmethod
    def record_sale(self, s: Sale) -> None:
        ...

    @abstractmethod
    def revenue_today(self, now: datetime) -> Decimal:
        ...

    @abstractmethod
    def top_sellers_since(
        self, since: datetime, top_k: int = 5
    ) -> List[Tuple[str, int]]:
        ...


class InMemoryRetailBoard(IRetailBoard):
    """Thread-safe in-memory :class:`IRetailBoard`."""

    def __init__(self) -> None:
        self._products: Dict[str, Product] = {}
        self._stock: Dict[str, int] = {}
        self._sales: List[Sale] = []
        self._lock = threading.Lock()

    def add_product(self, p: Product) -> None:
        if p is None:
            raise ValueError("product must not be None")
        with self._lock:
            self._products[p.sku] = p

    def get_product(self, sku: str) -> Optional[Product]:
        with self._lock:
            return self._products.get(sku)

    def set_stock(self, l: StockLevel) -> None:
        if l is None:
            raise ValueError("stock level must not be None")
        with self._lock:
            self._stock[l.sku] = l.quantity

    def stock(self, sku: str) -> int:
        with self._lock:
            return self._stock.get(sku, 0)

    def record_sale(self, s: Sale) -> None:
        if s is None:
            raise ValueError("sale must not be None")
        with self._lock:
            if s.sku not in self._products:
                raise RuntimeError(f"Unknown SKU {s.sku}")
            self._sales.append(s)
            self._stock[s.sku] = self._stock.get(s.sku, 0) - s.quantity

    def revenue_today(self, now: datetime) -> Decimal:
        with self._lock:
            return sum(
                (s.unit_price * s.quantity for s in self._sales if s.at_utc.date() == now.date()),
                Decimal(0),
            )

    def top_sellers_since(
        self, since: datetime, top_k: int = 5
    ) -> List[Tuple[str, int]]:
        if top_k <= 0:
            raise ValueError("top_k")
        with self._lock:
            totals: Dict[str, int] = {}
            for s in self._sales:
                if s.at_utc >= since:
                    totals[s.sku] = totals.get(s.sku, 0) + s.quantity
        # OrderByDescending(sold).Take(topK); ties keep first-seen order, which
        # for a dict is insertion order = first sale seen for that SKU.
        ranked = sorted(totals.items(), key=lambda kv: kv[1], reverse=True)
        return [(sku, sold) for sku, sold in ranked[:top_k]]
