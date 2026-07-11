# null_implementations.py
#
# Port of CircleAI.Banking NullImplementations.cs (C# — the EXACT spec).
#
# (2.8.0) Fail-closed banking defaults. The C# `static readonly Instance`
# singleton maps to a module-level singleton created after the class body.
# Guid.Empty.ToString() renders the all-zero GUID in the default ("D") format
# — dashed — which is distinct from Guid.NewGuid().ToString("n").

from __future__ import annotations

from typing import List, Optional

from .contracts import (
    Account,
    IAccountReader,
    ILedgerWriter,
    IPaymentProcessor,
    LedgerEntry,
    PaymentRequest,
    PaymentResult,
)

_GUID_EMPTY = "00000000-0000-0000-0000-000000000000"


class NullAccountReader(IAccountReader):
    Instance: "NullAccountReader"

    @property
    def backend_id(self) -> str:
        return "null"

    async def get_account_async(self, id: str, ct: Optional[object] = None) -> Optional[Account]:
        return None

    async def list_for_owner_async(self, owner: str, ct: Optional[object] = None) -> List[Account]:
        return []


class NullLedgerWriter(ILedgerWriter):
    Instance: "NullLedgerWriter"

    @property
    def backend_id(self) -> str:
        return "null"

    async def append_async(self, e: LedgerEntry, ct: Optional[object] = None) -> LedgerEntry:
        return e

    async def read_async(self, acc: str, limit: int = 100, ct: Optional[object] = None) -> List[LedgerEntry]:
        return []


class NullPaymentProcessor(IPaymentProcessor):
    Instance: "NullPaymentProcessor"

    @property
    def backend_id(self) -> str:
        return "null"

    async def process_async(self, req: PaymentRequest, ct: Optional[object] = None) -> PaymentResult:
        return PaymentResult(_GUID_EMPTY, False, "NullPaymentProcessor.")


NullAccountReader.Instance = NullAccountReader()
NullLedgerWriter.Instance = NullLedgerWriter()
NullPaymentProcessor.Instance = NullPaymentProcessor()
