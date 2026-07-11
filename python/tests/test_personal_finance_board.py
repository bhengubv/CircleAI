"""test_personal_finance_board.py — CircleAI.Personal.Finance port.

Covers the domain records, InMemoryPersonalFinanceBoard (account upsert, txn
record with balance mutation + unknown-account guard, month-scoped listing,
case-insensitive budget upsert ordered by Category, monthly summary with
in/out totals and by-category grouping) and the static
PersonalFinanceDomainContext. C# is the exact spec.

Imports Account from the subpackage to disambiguate from banking.Account (both
are re-exported at the root, personal-finance as PersonalFinanceAccount).
"""
from __future__ import annotations

from datetime import datetime, timezone
from decimal import Decimal

import pytest

from circle_ai import PersonalFinanceDomainContext
from circle_ai import PersonalFinanceAccount  # root alias for personal_finance.Account
from circle_ai.personal_finance import (
    Account,
    BudgetLine,
    FinanceTransaction,
    IPersonalFinanceBoard,
    InMemoryPersonalFinanceBoard,
    MonthSummary,
)


def _tx(tx_id, acct, amount, category, y=2026, m=1, d=5, note=None) -> FinanceTransaction:
    return FinanceTransaction(
        tx_id, acct, Decimal(amount), category, note, datetime(y, m, d, tzinfo=timezone.utc)
    )


def test_root_alias_matches_subpackage_type():
    assert PersonalFinanceAccount is Account


def test_board_is_ipersonalfinanceboard():
    assert isinstance(InMemoryPersonalFinanceBoard(), IPersonalFinanceBoard)


def test_upsert_and_get_account():
    board = InMemoryPersonalFinanceBoard()
    assert board.get_account("a1") is None
    board.upsert(Account("a1", "Cheque", Decimal("100.00"), "ZAR"))
    board.upsert(Account("a1", "Current", Decimal("200.00"), "ZAR"))
    got = board.get_account("a1")
    assert got is not None and got.name == "Current" and got.balance == Decimal("200.00")


def test_upsert_none_raises():
    with pytest.raises(ValueError):
        InMemoryPersonalFinanceBoard().upsert(None)  # type: ignore[arg-type]


def test_record_mutates_balance():
    board = InMemoryPersonalFinanceBoard()
    board.upsert(Account("a1", "Cheque", Decimal("100.00"), "ZAR"))
    board.record(_tx("t1", "a1", "-30.00", "food"))
    board.record(_tx("t2", "a1", "50.00", "salary"))
    assert board.get_account("a1").balance == Decimal("120.00")


def test_record_unknown_account_raises():
    board = InMemoryPersonalFinanceBoard()
    with pytest.raises(RuntimeError):
        board.record(_tx("t1", "missing", "10", "x"))


def test_record_none_raises():
    with pytest.raises(ValueError):
        InMemoryPersonalFinanceBoard().record(None)  # type: ignore[arg-type]


def test_list_for_month_is_scoped():
    board = InMemoryPersonalFinanceBoard()
    board.upsert(Account("a1", "Cheque", Decimal("0"), "ZAR"))
    board.record(_tx("jan1", "a1", "10", "x", m=1, d=5))
    board.record(_tx("jan2", "a1", "20", "x", m=1, d=20))
    board.record(_tx("feb", "a1", "30", "x", m=2, d=1))
    ids = {t.tx_id for t in board.list_for_month("a1", 2026, 1)}
    assert ids == {"jan1", "jan2"}


def test_budgets_case_insensitive_upsert_ordered_by_category():
    board = InMemoryPersonalFinanceBoard()
    board.set_budget(BudgetLine("Food", Decimal("500")))
    board.set_budget(BudgetLine("food", Decimal("750")))  # case-insensitive upsert
    board.set_budget(BudgetLine("Auto", Decimal("300")))
    budgets = board.budgets
    # 'food' upsert replaced 'Food'; ordered by Category (ordinal: 'Auto' < 'food').
    assert len(budgets) == 2
    assert [b.category for b in budgets] == ["Auto", "food"]
    assert {b.category: b.monthly_limit for b in budgets}["food"] == Decimal("750")


def test_set_budget_none_raises():
    with pytest.raises(ValueError):
        InMemoryPersonalFinanceBoard().set_budget(None)  # type: ignore[arg-type]


def test_summarise_totals_and_by_category():
    board = InMemoryPersonalFinanceBoard()
    board.upsert(Account("a1", "Cheque", Decimal("0"), "ZAR"))
    board.record(_tx("t1", "a1", "1000.00", "salary", m=3, d=1))
    board.record(_tx("t2", "a1", "-200.00", "food", m=3, d=2))
    board.record(_tx("t3", "a1", "-50.00", "food", m=3, d=3))
    board.record(_tx("t4", "a1", "-100.00", "transport", m=3, d=4))
    board.record(_tx("other_month", "a1", "-999.00", "food", m=4, d=1))
    s = board.summarise("a1", 2026, 3)
    assert isinstance(s, MonthSummary)
    assert s.year == 2026 and s.month == 3
    assert s.total_in == Decimal("1000.00")
    assert s.total_out == Decimal("350.00")  # 200 + 50 + 100
    assert s.by_category == {
        "salary": Decimal("1000.00"),
        "food": Decimal("-250.00"),
        "transport": Decimal("-100.00"),
    }


def test_summarise_empty_month():
    board = InMemoryPersonalFinanceBoard()
    board.upsert(Account("a1", "Cheque", Decimal("0"), "ZAR"))
    s = board.summarise("a1", 2026, 7)
    assert s.total_in == Decimal(0) and s.total_out == Decimal(0)
    assert s.by_category == {}


def test_personal_finance_domain_context():
    ctx = PersonalFinanceDomainContext
    assert ctx.SystemPromptSnippet.startswith("[DOMAIN: Personal.Finance]")
    assert "not advice" in ctx.SystemPromptSnippet
    assert list(ctx.ComplianceFlags) == ["FAIS_Act_37_2002", "NCA", "POPIA", "Not_Financial_Advice"]
    assert list(ctx.SuggestedTools) == ["budget_tracker", "spreadsheet", "calculator", "web_search"]
