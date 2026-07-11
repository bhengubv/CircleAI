"""test_commerce_finance_board.py — CircleAI.Commerce.Finance port.

Covers the domain records, InMemoryInvoiceBoard (issue/get, payment recording,
tax-inclusive remaining balance, total outstanding across invoices, mark-overdue
with the not-Paid guard, overdue listing) and the static
CommerceFinanceDomainContext. C# is the exact spec.

The tax-inclusive billing mirrors the C# expression exactly:
    billed = Σ line.Amount * (decimal)(1 + line.TaxPct / 100.0)
15% VAT on 100.00 -> 115.00; 0% on 50.00 -> 50.00.
"""
from __future__ import annotations

from datetime import datetime, timezone
from decimal import Decimal

import pytest

from circle_ai import (
    CommerceFinanceDomainContext,
    FinancePayment,
    IInvoiceBoard,
    InMemoryInvoiceBoard,
    Invoice,
    InvoiceLine,
)


def _inv(invoice_id, due_day, lines, status="Sent") -> Invoice:
    return Invoice(
        invoice_id,
        "cust",
        datetime(2026, 1, 1),
        datetime(2026, 1, due_day),
        tuple(lines),
        "ZAR",
        status,
    )


def test_board_is_iinvoiceboard():
    assert isinstance(InMemoryInvoiceBoard(), IInvoiceBoard)


def test_issue_get_and_none_guard():
    board = InMemoryInvoiceBoard()
    assert board.get("i1") is None
    board.issue(_inv("i1", 31, [InvoiceLine("a", Decimal("10.00"), 0.0)]))
    assert board.get("i1").invoice_id == "i1"
    with pytest.raises(ValueError):
        board.issue(None)  # type: ignore[arg-type]


def test_remaining_on_applies_tax_and_subtracts_payments():
    board = InMemoryInvoiceBoard()
    board.issue(
        _inv("i1", 31, [InvoiceLine("svc", Decimal("100.00"), 15.0), InvoiceLine("goods", Decimal("50.00"), 0.0)])
    )
    # 100 * 1.15 + 50 * 1.00 = 165.00
    assert board.remaining_on("i1") == Decimal("165.00")
    board.record_payment(FinancePayment("p1", "i1", Decimal("65.00"), datetime(2026, 1, 10, tzinfo=timezone.utc)))
    assert board.remaining_on("i1") == Decimal("100.00")


def test_remaining_on_missing_invoice_is_zero():
    assert InMemoryInvoiceBoard().remaining_on("nope") == Decimal(0)


def test_record_payment_none_raises():
    with pytest.raises(ValueError):
        InMemoryInvoiceBoard().record_payment(None)  # type: ignore[arg-type]


def test_total_outstanding_sums_all_invoices():
    board = InMemoryInvoiceBoard()
    board.issue(_inv("i1", 31, [InvoiceLine("a", Decimal("100.00"), 0.0)]))
    board.issue(_inv("i2", 15, [InvoiceLine("b", Decimal("10.00"), 0.0)]))
    assert board.total_outstanding() == Decimal("110.00")


def test_mark_overdue_flags_past_due_unpaid_only():
    board = InMemoryInvoiceBoard()
    board.issue(_inv("future", 31, [InvoiceLine("a", Decimal("1.00"), 0.0)]))
    board.issue(_inv("past", 15, [InvoiceLine("b", Decimal("1.00"), 0.0)]))
    board.issue(_inv("paid", 10, [InvoiceLine("c", Decimal("1.00"), 0.0)], status="Paid"))
    board.mark_overdue(datetime(2026, 1, 20))
    assert board.get("future").status == "Sent"       # not yet due
    assert board.get("past").status == "Overdue"       # past due, unpaid
    assert board.get("paid").status == "Paid"          # past due but already paid


def test_overdue_lists_overdue_status_case_insensitively():
    board = InMemoryInvoiceBoard()
    board.issue(_inv("a", 15, [InvoiceLine("x", Decimal("1.00"), 0.0)], status="OVERDUE"))
    board.issue(_inv("b", 15, [InvoiceLine("y", Decimal("1.00"), 0.0)], status="Sent"))
    assert {i.invoice_id for i in board.overdue()} == {"a"}


def test_commerce_finance_domain_context():
    assert CommerceFinanceDomainContext.SystemPromptSnippet.startswith(
        "[DOMAIN: Commerce.Finance]"
    )
    assert "cash conversion cycle" in CommerceFinanceDomainContext.SystemPromptSnippet
    assert list(CommerceFinanceDomainContext.ComplianceFlags) == [
        "NCA_34_2005",
        "SARB_aware",
        "POPIA",
        "IFRS",
    ]
    assert list(CommerceFinanceDomainContext.SuggestedTools) == [
        "cash_flow_model",
        "spreadsheet",
        "web_search",
    ]
