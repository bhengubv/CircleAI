"""test_banking_board.py — CircleAI.Banking port.

Covers the domain records, InMemoryBank (seed/get/list, append + balance
mutation, descending-time read with limit, double-entry payment processing with
positive-amount / unknown-account / currency / insufficient-funds guards), the
in-memory reader/ledger/processor adapters, and the fail-closed null defaults.
C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone
from decimal import Decimal

import pytest

from circle_ai.banking import (
    Account,
    IAccountReader,
    ILedgerWriter,
    InMemoryAccountReader,
    InMemoryBank,
    InMemoryLedgerWriter,
    InMemoryPaymentProcessor,
    IPaymentProcessor,
    LedgerEntry,
    NullAccountReader,
    NullLedgerWriter,
    NullPaymentProcessor,
    PaymentRequest,
    PaymentResult,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)
_HEX32 = set("0123456789abcdef")


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


def _seed(bank: InMemoryBank) -> None:
    bank.seed_account(Account("acc-a", "owner-1", "ZAR", Decimal("100.00")))
    bank.seed_account(Account("acc-b", "owner-1", "ZAR", Decimal("0.00")))
    bank.seed_account(Account("acc-usd", "owner-2", "USD", Decimal("50.00")))


def test_records_are_frozen():
    a = Account("a", "o", "ZAR", Decimal("1"))
    with pytest.raises(Exception):
        a.balance = Decimal("2")  # type: ignore[misc]


def test_seed_get_and_list_for_owner():
    bank = InMemoryBank()
    _seed(bank)
    assert bank.get("acc-a").balance == Decimal("100.00")
    assert bank.get("missing") is None
    owners = {a.account_id for a in bank.list_for_owner("owner-1")}
    assert owners == {"acc-a", "acc-b"}


def test_seed_none_raises():
    with pytest.raises(ValueError):
        InMemoryBank().seed_account(None)  # type: ignore[arg-type]


def test_append_mutates_balance_and_records_entry():
    bank = InMemoryBank()
    _seed(bank)
    bank.append(LedgerEntry("t1", "acc-a", Decimal("-25.00"), "coffee", _at(0)))
    assert bank.get("acc-a").balance == Decimal("75.00")
    entries = bank.read("acc-a", 100)
    assert len(entries) == 1 and entries[0].memo == "coffee"


def test_append_unknown_account_raises():
    bank = InMemoryBank()
    with pytest.raises(RuntimeError):
        bank.append(LedgerEntry("t", "nope", Decimal("1"), "m", _at(0)))


def test_read_orders_descending_and_limits():
    bank = InMemoryBank()
    _seed(bank)
    for i in range(5):
        bank.append(LedgerEntry(f"t{i}", "acc-a", Decimal("1"), f"m{i}", _at(i)))
    recent = bank.read("acc-a", 3)
    assert [e.memo for e in recent] == ["m4", "m3", "m2"]  # newest first
    assert bank.read("acc-b", 100) == []  # no entries


async def test_payment_double_entry_and_balances():
    bank = InMemoryBank()
    _seed(bank)
    proc = InMemoryPaymentProcessor(bank)
    res = await proc.process_async(PaymentRequest("acc-a", "acc-b", Decimal("40.00"), "ZAR", "rent"))
    assert isinstance(res, PaymentResult)
    assert res.accepted is True and res.failure_reason is None
    assert len(res.tx_id) == 32 and set(res.tx_id) <= _HEX32
    assert bank.get("acc-a").balance == Decimal("60.00")
    assert bank.get("acc-b").balance == Decimal("40.00")
    # Both legs share the tx id.
    a_entries = bank.read("acc-a", 100)
    b_entries = bank.read("acc-b", 100)
    assert a_entries[0].tx_id == res.tx_id == b_entries[0].tx_id
    assert a_entries[0].amount == Decimal("-40.00")
    assert b_entries[0].amount == Decimal("40.00")
    assert a_entries[0].memo == "To acc-b: rent"
    assert b_entries[0].memo == "From acc-a: rent"


def test_payment_positive_amount_guard():
    bank = InMemoryBank()
    _seed(bank)
    res = bank.process_payment(PaymentRequest("acc-a", "acc-b", Decimal("0"), "ZAR", "x"))
    assert res.accepted is False and res.failure_reason == "Amount must be positive"


def test_payment_unknown_accounts():
    bank = InMemoryBank()
    _seed(bank)
    assert bank.process_payment(
        PaymentRequest("nope", "acc-b", Decimal("1"), "ZAR", "x")
    ).failure_reason == "Unknown source account"
    assert bank.process_payment(
        PaymentRequest("acc-a", "nope", Decimal("1"), "ZAR", "x")
    ).failure_reason == "Unknown destination account"


def test_payment_currency_mismatch():
    bank = InMemoryBank()
    _seed(bank)
    res = bank.process_payment(PaymentRequest("acc-a", "acc-b", Decimal("1"), "USD", "x"))
    assert res.failure_reason == "Currency mismatch"


def test_payment_insufficient_funds():
    bank = InMemoryBank()
    _seed(bank)
    res = bank.process_payment(PaymentRequest("acc-a", "acc-b", Decimal("1000.00"), "ZAR", "x"))
    assert res.failure_reason == "Insufficient funds"


async def test_in_memory_reader_and_ledger_adapters():
    bank = InMemoryBank()
    _seed(bank)
    reader = InMemoryAccountReader(bank)
    writer = InMemoryLedgerWriter(bank)
    assert isinstance(reader, IAccountReader) and isinstance(writer, ILedgerWriter)
    assert reader.backend_id == "in-memory" and writer.backend_id == "in-memory"
    acc = await reader.get_account_async("acc-a")
    assert acc is not None and acc.account_id == "acc-a"
    owned = await reader.list_for_owner_async("owner-1")
    assert {a.account_id for a in owned} == {"acc-a", "acc-b"}
    e = await writer.append_async(LedgerEntry("tt", "acc-a", Decimal("5"), "m", _at(0)))
    assert e.tx_id == "tt"
    assert (await writer.read_async("acc-a", 10))[0].tx_id == "tt"


async def test_null_implementations_fail_closed():
    r = NullAccountReader.Instance
    w = NullLedgerWriter.Instance
    p = NullPaymentProcessor.Instance
    assert r.backend_id == "null" and w.backend_id == "null" and p.backend_id == "null"
    assert await r.get_account_async("x") is None
    assert await r.list_for_owner_async("x") == []
    entry = LedgerEntry("t", "a", Decimal("1"), "m", _at(0))
    assert (await w.append_async(entry)) is entry
    assert await w.read_async("a") == []
    res = await p.process_async(PaymentRequest("a", "b", Decimal("1"), "ZAR", "m"))
    assert res.accepted is False
    assert res.failure_reason == "NullPaymentProcessor."
    assert res.tx_id == "00000000-0000-0000-0000-000000000000"


def test_null_singletons_are_stable():
    assert NullAccountReader.Instance is NullAccountReader.Instance
    assert isinstance(NullPaymentProcessor.Instance, IPaymentProcessor)
