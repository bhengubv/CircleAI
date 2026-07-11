"""circle_ai.autonomous_biz — port of the CircleAI.AutonomousBiz assembly.

(3.0.0 contracts / 3.3.0 in-memory impl) Autonomous-business domain: treasury
snapshots, a revenue-event fan-out loop with kept history, and an append-only
decision log, plus fail-closed null defaults. C# is the exact spec.

Public surface:

  * TreasurySnapshot / RevenueEvent / AutonomousDecision  — domain records.
  * ITreasury / IRevenueLoop / IDecisionLog               — backend contracts.
  * InMemoryTreasury / InMemoryRevenueLoop / InMemoryDecisionLog.
  * NullTreasury / NullRevenueLoop / NullDecisionLog      — fail-closed defaults.
"""
from __future__ import annotations

from .contracts import (
    AutonomousDecision,
    IDecisionLog,
    IRevenueLoop,
    ITreasury,
    RevenueEvent,
    RevenueHandler,
    TreasurySnapshot,
)
from .in_memory_autonomous_biz import (
    InMemoryDecisionLog,
    InMemoryRevenueLoop,
    InMemoryTreasury,
)
from .null_implementations import (
    NullDecisionLog,
    NullRevenueLoop,
    NullTreasury,
)

__all__ = [
    "TreasurySnapshot",
    "RevenueEvent",
    "AutonomousDecision",
    "RevenueHandler",
    "ITreasury",
    "IRevenueLoop",
    "IDecisionLog",
    "InMemoryTreasury",
    "InMemoryRevenueLoop",
    "InMemoryDecisionLog",
    "NullTreasury",
    "NullRevenueLoop",
    "NullDecisionLog",
]
