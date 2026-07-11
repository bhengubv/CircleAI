"""circle_ai.banking — port of the CircleAI.Banking assembly.

(2.8.0 contracts / 3.3.0 in-memory impl) Banking domain: accounts, ledger
entries, payment requests/results, and the reader/ledger/payment contracts,
with a concurrent in-memory bank (double-entry, balance-checked payments) and
fail-closed null defaults. C# is the exact spec.

Public surface:

  * Account / LedgerEntry / PaymentRequest / PaymentResult — domain records.
  * IAccountReader / ILedgerWriter / IPaymentProcessor     — backend contracts.
  * InMemoryBank                                           — shared concurrent store.
  * InMemoryAccountReader / InMemoryLedgerWriter / InMemoryPaymentProcessor.
  * NullAccountReader / NullLedgerWriter / NullPaymentProcessor — fail-closed defaults.
"""
from __future__ import annotations

from .contracts import (
    Account,
    IAccountReader,
    ILedgerWriter,
    IPaymentProcessor,
    LedgerEntry,
    PaymentRequest,
    PaymentResult,
)
from .in_memory_banking import (
    InMemoryAccountReader,
    InMemoryBank,
    InMemoryLedgerWriter,
    InMemoryPaymentProcessor,
)
from .null_implementations import (
    NullAccountReader,
    NullLedgerWriter,
    NullPaymentProcessor,
)

__all__ = [
    "Account",
    "LedgerEntry",
    "PaymentRequest",
    "PaymentResult",
    "IAccountReader",
    "ILedgerWriter",
    "IPaymentProcessor",
    "InMemoryBank",
    "InMemoryAccountReader",
    "InMemoryLedgerWriter",
    "InMemoryPaymentProcessor",
    "NullAccountReader",
    "NullLedgerWriter",
    "NullPaymentProcessor",
]
