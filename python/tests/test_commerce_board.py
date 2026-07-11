"""test_commerce_board.py — CircleAI.Commerce port.

Covers the domain records, InMemoryCommerceBoard (customer upsert, order place +
descending-time ordering, line items, status update, lifetime value as a Decimal
sum) and the static CommerceDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone
from decimal import Decimal

import pytest

from circle_ai import (
    CommerceCustomer,
    CommerceDomainContext,
    CommerceLineItem,
    CommerceOrder,
    ICommerceBoard,
    InMemoryCommerceBoard,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


def test_board_is_icommerceboard():
    assert isinstance(InMemoryCommerceBoard(), ICommerceBoard)


def test_add_and_get_customer_upserts():
    board = InMemoryCommerceBoard()
    assert board.get_customer("c1") is None
    board.add_customer(CommerceCustomer("c1", "Ann", "a@x.test", _at(0)))
    board.add_customer(CommerceCustomer("c1", "Anne", None, _at(0)))
    got = board.get_customer("c1")
    assert got is not None and got.name == "Anne" and got.email is None


def test_add_customer_none_raises():
    with pytest.raises(ValueError):
        InMemoryCommerceBoard().add_customer(None)  # type: ignore[arg-type]


def test_orders_for_descending_by_time():
    board = InMemoryCommerceBoard()
    board.place(CommerceOrder("o1", "c1", Decimal("10"), "ZAR", "new", _at(0)))
    board.place(CommerceOrder("o2", "c1", Decimal("20"), "ZAR", "new", _at(10)))
    board.place(CommerceOrder("ox", "other", Decimal("5"), "ZAR", "new", _at(5)))
    board.place(CommerceOrder("o3", "c1", Decimal("30"), "ZAR", "new", _at(5)))
    orders = board.orders_for("c1")
    assert [o.order_id for o in orders] == ["o2", "o3", "o1"]  # 10, 5, 0
    assert all(o.customer_id == "c1" for o in orders)


def test_place_none_raises():
    with pytest.raises(ValueError):
        InMemoryCommerceBoard().place(None)  # type: ignore[arg-type]


def test_lines_for_filters_by_order():
    board = InMemoryCommerceBoard()
    board.add_line(CommerceLineItem("l1", "o1", "SKU1", 2, Decimal("5.00")))
    board.add_line(CommerceLineItem("l2", "o1", "SKU2", 1, Decimal("7.50")))
    board.add_line(CommerceLineItem("l3", "o2", "SKU3", 3, Decimal("1.00")))
    lines = board.lines_for("o1")
    assert {l.line_id for l in lines} == {"l1", "l2"}


def test_add_line_none_raises():
    with pytest.raises(ValueError):
        InMemoryCommerceBoard().add_line(None)  # type: ignore[arg-type]


def test_update_status_replaces():
    board = InMemoryCommerceBoard()
    board.place(CommerceOrder("o1", "c1", Decimal("10"), "ZAR", "new", _at(0)))
    board.update_status("o1", "shipped")
    assert board.orders_for("c1")[0].status == "shipped"


def test_update_status_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryCommerceBoard().update_status("nope", "x")


def test_lifetime_value_sums_order_totals():
    board = InMemoryCommerceBoard()
    board.place(CommerceOrder("o1", "c1", Decimal("10.50"), "ZAR", "new", _at(0)))
    board.place(CommerceOrder("o2", "c1", Decimal("4.25"), "ZAR", "new", _at(1)))
    ltv = board.lifetime_value("c1")
    assert ltv == Decimal("14.75")
    assert isinstance(ltv, Decimal)
    assert board.lifetime_value("nobody") == Decimal(0)


def test_commerce_domain_context():
    assert CommerceDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Commerce]")
    assert "margin-aware" in CommerceDomainContext.SystemPromptSnippet
    assert list(CommerceDomainContext.ComplianceFlags) == [
        "POPIA",
        "Consumer_Protection_Act",
        "GDPR_aware",
    ]
    assert list(CommerceDomainContext.SuggestedTools) == [
        "inventory",
        "pricing_engine",
        "order_management",
        "analytics",
    ]
