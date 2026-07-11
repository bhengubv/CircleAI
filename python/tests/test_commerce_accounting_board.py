"""test_commerce_accounting_board.py — CircleAI.Commerce.Accounting port.

Covers the domain records, InMemoryAccountingBoard (post with non-negative
guard, tax define/get, account balance and per-period sum as debit − credit,
period-scoped entries ascending, net profit) and the static
CommerceAccountingDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime
from decimal import Decimal

import pytest

from circle_ai import (
    AccountingEntry,
    CommerceAccountingDomainContext,
    IAccountingBoard,
    InMemoryAccountingBoard,
    Period,
    TaxRate,
)


def _e(entry_id, y, m, d, code, debit, credit, memo="") -> AccountingEntry:
    return AccountingEntry(entry_id, datetime(y, m, d), code, Decimal(debit), Decimal(credit), memo)


def test_board_is_iaccountingboard():
    assert isinstance(InMemoryAccountingBoard(), IAccountingBoard)


def test_post_none_raises():
    with pytest.raises(ValueError):
        InMemoryAccountingBoard().post(None)  # type: ignore[arg-type]


def test_post_negative_amount_raises():
    board = InMemoryAccountingBoard()
    with pytest.raises(ValueError):
        board.post(_e("x", 2026, 1, 1, "4000", "-1", "0"))
    with pytest.raises(ValueError):
        board.post(_e("x", 2026, 1, 1, "4000", "0", "-1"))


def test_define_and_get_tax():
    board = InMemoryAccountingBoard()
    assert board.get_tax("VAT") is None
    board.define_tax(TaxRate("VAT", 15.0))
    assert board.get_tax("VAT").percentage == 15.0
    board.define_tax(TaxRate("VAT", 14.0))  # upsert
    assert board.get_tax("VAT").percentage == 14.0


def test_define_tax_none_raises():
    with pytest.raises(ValueError):
        InMemoryAccountingBoard().define_tax(None)  # type: ignore[arg-type]


def test_account_balance_is_debit_minus_credit():
    board = InMemoryAccountingBoard()
    board.post(_e("e1", 2026, 1, 5, "1000", "100", "0"))
    board.post(_e("e2", 2026, 1, 6, "1000", "0", "30"))
    board.post(_e("e3", 2026, 2, 6, "1000", "10", "0"))
    board.post(_e("e4", 2026, 1, 6, "2000", "999", "0"))
    assert board.account_balance("1000") == Decimal("80")  # 100 - 30 + 10
    assert board.account_balance("nope") == Decimal(0)


def test_sum_is_period_scoped():
    board = InMemoryAccountingBoard()
    board.post(_e("e1", 2026, 1, 5, "1000", "100", "0"))
    board.post(_e("e2", 2026, 1, 20, "1000", "0", "30"))
    board.post(_e("e3", 2026, 2, 6, "1000", "10", "0"))
    assert board.sum("1000", Period(2026, 1)) == Decimal("70")  # 100 - 30
    assert board.sum("1000", Period(2026, 2)) == Decimal("10")
    assert board.sum("1000", Period(2025, 1)) == Decimal(0)


def test_for_account_period_scoped_and_ascending():
    board = InMemoryAccountingBoard()
    board.post(_e("late", 2026, 1, 20, "1000", "1", "0"))
    board.post(_e("early", 2026, 1, 5, "1000", "1", "0"))
    board.post(_e("other_month", 2026, 2, 5, "1000", "1", "0"))
    board.post(_e("other_acct", 2026, 1, 6, "2000", "1", "0"))
    rows = board.for_account("1000", Period(2026, 1))
    assert [r.entry_id for r in rows] == ["early", "late"]


def test_net_profit():
    board = InMemoryAccountingBoard()
    board.post(_e("rev", 2026, 3, 1, "4000", "1000", "0"))
    board.post(_e("exp", 2026, 3, 2, "5000", "300", "0"))
    board.post(_e("rev_other_month", 2026, 4, 1, "4000", "500", "0"))
    assert board.net_profit(Period(2026, 3), "4000", "5000") == Decimal("700")


def test_accounting_domain_context():
    assert CommerceAccountingDomainContext.SystemPromptSnippet.startswith(
        "[DOMAIN: Commerce.Accounting]"
    )
    assert "15%" in CommerceAccountingDomainContext.SystemPromptSnippet
    assert list(CommerceAccountingDomainContext.ComplianceFlags) == [
        "IFRS",
        "SARS",
        "Companies_Act_71_2008",
        "VAT_Act",
    ]
    assert list(CommerceAccountingDomainContext.SuggestedTools) == [
        "accounting_software",
        "spreadsheet",
        "document_editor",
    ]
