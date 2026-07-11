"""test_retail_board.py — CircleAI.Retail port.

Covers the domain records, InMemoryRetailBoard (product upsert, stock set/get and
decrement on sale, unknown-SKU rejection, same-day revenue, top-sellers ranking
with topK guard) and the static RetailDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone
from decimal import Decimal

import pytest

from circle_ai import (
    IRetailBoard,
    InMemoryRetailBoard,
    Product,
    RetailDomainContext,
    Sale,
    StockLevel,
)

_NOW = datetime(2026, 6, 1, 12, 0, tzinfo=timezone.utc)


def test_board_is_iretailboard():
    assert isinstance(InMemoryRetailBoard(), IRetailBoard)


def test_add_product_and_stock():
    board = InMemoryRetailBoard()
    assert board.get_product("s1") is None
    board.add_product(Product("s1", "Widget", Decimal("9.99"), "ZAR", "gadgets"))
    board.set_stock(StockLevel("s1", 10))
    assert board.get_product("s1").name == "Widget"
    assert board.stock("s1") == 10
    assert board.stock("unknown") == 0


def test_add_product_none_raises():
    with pytest.raises(ValueError):
        InMemoryRetailBoard().add_product(None)  # type: ignore[arg-type]


def test_record_sale_decrements_stock():
    board = InMemoryRetailBoard()
    board.add_product(Product("s1", "Widget", Decimal("10"), "ZAR", None))
    board.set_stock(StockLevel("s1", 5))
    board.record_sale(Sale("x1", "s1", 2, Decimal("10"), _NOW))
    assert board.stock("s1") == 3


def test_record_sale_unknown_sku_raises():
    board = InMemoryRetailBoard()
    with pytest.raises(RuntimeError):
        board.record_sale(Sale("x1", "nope", 1, Decimal("1"), _NOW))


def test_revenue_today_sums_same_calendar_date():
    board = InMemoryRetailBoard()
    board.add_product(Product("s1", "Widget", Decimal("10"), "ZAR", None))
    board.record_sale(Sale("x1", "s1", 2, Decimal("10.00"), _NOW))
    board.record_sale(Sale("x2", "s1", 1, Decimal("5.50"), _NOW + timedelta(hours=1)))
    board.record_sale(Sale("x3", "s1", 9, Decimal("99"), _NOW - timedelta(days=1)))
    assert board.revenue_today(_NOW) == Decimal("25.50")


def test_top_sellers_since_ranks_by_units():
    board = InMemoryRetailBoard()
    for sku in ("a", "b", "c"):
        board.add_product(Product(sku, sku.upper(), Decimal("1"), "ZAR", None))
    board.record_sale(Sale("1", "a", 3, Decimal("1"), _NOW))
    board.record_sale(Sale("2", "b", 7, Decimal("1"), _NOW))
    board.record_sale(Sale("3", "a", 2, Decimal("1"), _NOW))
    board.record_sale(Sale("4", "c", 1, Decimal("1"), _NOW - timedelta(days=2)))
    top = board.top_sellers_since(_NOW - timedelta(hours=1), top_k=2)
    assert top == [("b", 7), ("a", 5)]


def test_top_sellers_since_bad_topk_raises():
    with pytest.raises(ValueError):
        InMemoryRetailBoard().top_sellers_since(_NOW, top_k=0)


def test_retail_domain_context():
    assert RetailDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Retail]")
    assert list(RetailDomainContext.ComplianceFlags) == [
        "Consumer_Protection_Act",
        "POPIA",
        "Labour_Relations_Act",
    ]
    assert list(RetailDomainContext.SuggestedTools) == [
        "pos_system",
        "inventory",
        "analytics",
        "promotions_engine",
    ]
