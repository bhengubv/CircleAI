# null_implementations.py
#
# Port of CircleAI.AutonomousBiz NullImplementations.cs (C# — the EXACT spec).
#
# (3.0.0) Fail-closed autonomous-business defaults. The C# `static readonly
# Instance` singletons map to module-level singletons. NullTreasury returns a
# zero-balance snapshot at DateTimeOffset.MinValue; NullRevenueLoop hands back
# an empty disposable and an empty history; NullDecisionLog no-ops append.

from __future__ import annotations

from datetime import datetime, timezone
from decimal import Decimal
from typing import List, Optional

from .contracts import (
    AutonomousDecision,
    IDecisionLog,
    IRevenueLoop,
    ITreasury,
    RevenueEvent,
    RevenueHandler,
    TreasurySnapshot,
)

_MIN_UTC = datetime(1, 1, 1, tzinfo=timezone.utc)


class _EmptyDisposable:
    Instance: "_EmptyDisposable"

    def dispose(self) -> None:
        pass

    def __enter__(self) -> "_EmptyDisposable":
        return self

    def __exit__(self, *exc: object) -> None:
        pass


_EmptyDisposable.Instance = _EmptyDisposable()


class NullTreasury(ITreasury):
    Instance: "NullTreasury"

    @property
    def backend_id(self) -> str:
        return "null"

    async def get_snapshot_async(self, ct: Optional[object] = None) -> TreasurySnapshot:
        return TreasurySnapshot(Decimal(0), "ZAR", _MIN_UTC)


class NullRevenueLoop(IRevenueLoop):
    Instance: "NullRevenueLoop"

    @property
    def backend_id(self) -> str:
        return "null"

    def subscribe(self, handler: RevenueHandler) -> object:
        return _EmptyDisposable.Instance

    async def read_async(self, since: datetime, ct: Optional[object] = None) -> List[RevenueEvent]:
        return []


class NullDecisionLog(IDecisionLog):
    Instance: "NullDecisionLog"

    @property
    def backend_id(self) -> str:
        return "null"

    async def append_async(self, d: AutonomousDecision, ct: Optional[object] = None) -> None:
        return None

    async def read_async(self, limit: int = 100, ct: Optional[object] = None) -> List[AutonomousDecision]:
        return []


NullTreasury.Instance = NullTreasury()
NullRevenueLoop.Instance = NullRevenueLoop()
NullDecisionLog.Instance = NullDecisionLog()
