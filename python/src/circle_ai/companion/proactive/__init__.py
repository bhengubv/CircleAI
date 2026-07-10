# circle_ai.companion.proactive — proactive scheduling substrate.
#
# Ported from CircleAI.Companion.Proactive — the C# reference project. Cron-driven
# scheduling of opaque tasks, split into a source (what tasks exist), a runner
# (how to execute one), and a scheduler (when they fire).

from __future__ import annotations

from .contracts import (
    IProactiveScheduler,
    IProactiveTaskRunner,
    IProactiveTaskSource,
)
from .cron_expression import CronExpression
from .null_implementations import (
    DelegateProactiveTaskRunner,
    InMemoryProactiveTaskSource,
    NullProactiveTaskRunner,
    NullProactiveTaskSource,
)
from .primitives import (
    ProactiveTask,
    ProactiveTaskLoadError,
    ProactiveTaskRunResult,
    ProactiveTrigger,
)
from .scheduler import ProactiveScheduler

__all__ = [
    # primitives
    "ProactiveTrigger",
    "ProactiveTask",
    "ProactiveTaskRunResult",
    "ProactiveTaskLoadError",
    # cron
    "CronExpression",
    # contracts
    "IProactiveTaskSource",
    "IProactiveTaskRunner",
    "IProactiveScheduler",
    # implementations
    "ProactiveScheduler",
    "NullProactiveTaskSource",
    "NullProactiveTaskRunner",
    "InMemoryProactiveTaskSource",
    "DelegateProactiveTaskRunner",
]
