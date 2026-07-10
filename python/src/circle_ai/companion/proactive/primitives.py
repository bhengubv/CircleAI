# companion/proactive/primitives.py
#
# Shared shapes for the proactive scheduling surface. Ported from
# CircleAI.Companion.Proactive (Primitives.cs) — the C# reference.
#
# A ``ProactiveTask`` is opaque to the substrate — its ``payload`` is whatever the
# consumer's ``IProactiveTaskRunner`` knows how to execute. The substrate never
# inspects it.

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional


@dataclass(frozen=True, slots=True)
class ProactiveTrigger:
    """How a task fires. Exactly one of ``cron`` / ``on_event`` / ``manual`` is set.

    Mirrors ``CircleAI.Companion.Proactive.ProactiveTrigger``.

    * ``cron``     — 5-field cron expression (see :class:`CronExpression`).
    * ``on_event`` — event name (e.g. "note-saved", "task-created").
    * ``manual``   — ``True`` if the task only fires when explicitly invoked.
    """

    cron: Optional[str] = None
    on_event: Optional[str] = None
    manual: bool = False


@dataclass(frozen=True, slots=True)
class ProactiveTask:
    """One scheduled task — opaque from the substrate's perspective.

    Mirrors ``CircleAI.Companion.Proactive.ProactiveTask``.

    * ``id``             — unique task id within its source (used for last-run tracking).
    * ``trigger``        — cron / event / manual trigger.
    * ``payload``        — consumer-owned object; the substrate never inspects it.
    * ``source_context`` — optional context tag so multi-tenant sources keep
                            per-context last-run state separate.
    """

    id: str
    trigger: ProactiveTrigger
    payload: object
    source_context: Optional[str] = None


@dataclass(frozen=True, slots=True)
class ProactiveTaskRunResult:
    """One run outcome — success or failure with a message.

    Mirrors ``CircleAI.Companion.Proactive.ProactiveTaskRunResult``.
    """

    task_id: str
    success: bool
    failure_message: Optional[str] = None


@dataclass(frozen=True, slots=True)
class ProactiveTaskLoadError:
    """One parse failure surfaced through the source.

    Mirrors ``CircleAI.Companion.Proactive.ProactiveTaskLoadError``.
    """

    task_id: str
    message: str
    source_context: Optional[str] = None


__all__ = [
    "ProactiveTrigger",
    "ProactiveTask",
    "ProactiveTaskRunResult",
    "ProactiveTaskLoadError",
]
