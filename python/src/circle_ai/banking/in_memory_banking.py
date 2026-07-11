# in_memory_banking.py
#
# Port of CircleAI.Banking InMemoryBanking.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory banking primitives: account store, ledger writer,
# payment processor with balance checks + double-entry bookkeeping (debit
# source, credit destination). Hosts that need durability swap in a
# database-backed implementation behind the same contract.
#
# The C# `_txLock` is a monitor lock, which is re-entrant: ProcessPayment holds
# it and then calls Append, which re-acquires it. Python's threading.Lock is
# NOT re-entrant, so we use threading.RLock to preserve the exact semantics.
# C# Guid.NewGuid().ToString("n") (32 lowercase hex, no dashes) maps to
# uuid.uuid4().hex.

from __future__ import annotations

import threading
import uuid
from datetime import datetime, timezone
from decimal import Decimal
from typing import Dict, List, Optional

from .contracts import (
    Account,
    IAccountReader,
    ILedgerWriter,
    IPaymentProcessor,
    LedgerEntry,
    PaymentRequest,
    PaymentResult,
)


def _new_txid() -> str:
    return uuid.uuid4().hex


class InMemoryBank:
    """(3.3.0) Concurrent in-memory bank shared by reader/ledger/payment."""

    def __init__(self) -> None:
        self._accounts: Dict[str, Account] = {}
        self._ledger: Dict[str, List[LedgerEntry]] = {}
        # Re-entrant to mirror the re-entrant C# monitor lock (ProcessPayment ->
        # Append re-acquires the same lock).
        self._tx_lock = threading.RLock()

    def seed_account(self, account: Account) -> None:
        if account is None:
            raise ValueError("account must not be None")
        with self._tx_lock:
            self._accounts[account.account_id] = account

    def get(self, id: str) -> Optional[Account]:
        with self._tx_lock:
            return self._accounts.get(id)

    def list_for_owner(self, owner_id: str) -> List[Account]:
        with self._tx_lock:
            return [a for a in self._accounts.values() if a.owner_id == owner_id]

    def append(self, entry: LedgerEntry) -> LedgerEntry:
        if entry is None:
            raise ValueError("entry must not be None")
        with self._tx_lock:
            acct = self._accounts.get(entry.account_id)
            if acct is None:
                raise RuntimeError(f"Unknown account {entry.account_id}")
            self._accounts[entry.account_id] = Account(
                acct.account_id, acct.owner_id, acct.currency, acct.balance + entry.amount
            )
            self._ledger.setdefault(entry.account_id, []).append(entry)
            return entry

    def read(self, account_id: str, limit: int) -> List[LedgerEntry]:
        with self._tx_lock:
            lst = self._ledger.get(account_id)
            if lst is None:
                return []
            ordered = sorted(lst, key=lambda e: e.at_utc, reverse=True)
            return ordered[:limit]

    def process_payment(self, req: PaymentRequest) -> PaymentResult:
        if req is None:
            raise ValueError("req must not be None")
        if req.amount <= 0:
            return PaymentResult(_new_txid(), False, "Amount must be positive")
        with self._tx_lock:
            src = self._accounts.get(req.from_account)
            if src is None:
                return PaymentResult(_new_txid(), False, "Unknown source account")
            dst = self._accounts.get(req.to_account)
            if dst is None:
                return PaymentResult(_new_txid(), False, "Unknown destination account")
            if src.currency.casefold() != req.currency.casefold() or dst.currency.casefold() != req.currency.casefold():
                return PaymentResult(_new_txid(), False, "Currency mismatch")
            if src.balance < req.amount:
                return PaymentResult(_new_txid(), False, "Insufficient funds")

            tx_id = _new_txid()
            now = datetime.now(timezone.utc)
            self.append(LedgerEntry(tx_id, req.from_account, -req.amount, f"To {req.to_account}: {req.memo}", now))
            self.append(LedgerEntry(tx_id, req.to_account, req.amount, f"From {req.from_account}: {req.memo}", now))
            return PaymentResult(tx_id, True, None)


class InMemoryAccountReader(IAccountReader):
    def __init__(self, bank: InMemoryBank) -> None:
        if bank is None:
            raise ValueError("bank must not be None")
        self._bank = bank

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def get_account_async(self, id: str, ct: Optional[object] = None) -> Optional[Account]:
        return self._bank.get(id)

    async def list_for_owner_async(self, owner: str, ct: Optional[object] = None) -> List[Account]:
        return self._bank.list_for_owner(owner)


class InMemoryLedgerWriter(ILedgerWriter):
    def __init__(self, bank: InMemoryBank) -> None:
        if bank is None:
            raise ValueError("bank must not be None")
        self._bank = bank

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def append_async(self, e: LedgerEntry, ct: Optional[object] = None) -> LedgerEntry:
        return self._bank.append(e)

    async def read_async(self, acc: str, limit: int = 100, ct: Optional[object] = None) -> List[LedgerEntry]:
        return self._bank.read(acc, limit)


class InMemoryPaymentProcessor(IPaymentProcessor):
    def __init__(self, bank: InMemoryBank) -> None:
        if bank is None:
            raise ValueError("bank must not be None")
        self._bank = bank

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def process_async(self, req: PaymentRequest, ct: Optional[object] = None) -> PaymentResult:
        return self._bank.process_payment(req)
