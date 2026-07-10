"""IProactiveReasoningService + ProactiveReasoningService — ports of the
CircleAI.Hosting proactive-reasoning engine.

B!'s ability to initiate contact rather than merely respond. Evaluates a
prioritised list of :class:`ITriggerCondition` instances and, when the first
one fires, calls :meth:`IAIService.ask_async` to generate a warm, goal-aware
check-in message, then notifies subscribers via ``on_proactive_message_ready``.

Mirrors ``IProactiveReasoningService``, ``ProactiveMessageEventArgs``, and
``ProactiveReasoningService``.
"""
from __future__ import annotations

import datetime as _dt
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Callable, List, Optional, Sequence

from ..memory.affect_state import AffectState
from ..memory.goal import Goal
from .triggers import ITriggerCondition, ProactiveContext

__all__ = [
    "ProactiveMessageEventArgs",
    "IProactiveReasoningService",
    "ProactiveReasoningService",
]

_UTC = _dt.timezone.utc

# Subscriber callback: receives the generated proactive-message event.
ProactiveMessageHandler = Callable[["ProactiveMessageEventArgs"], None]


@dataclass(frozen=True, slots=True)
class ProactiveMessageEventArgs:
    """Emitted when B! generates a proactive message. Mirrors
    ``ProactiveMessageEventArgs``.
    """

    user_id: str
    message: str
    trigger_name: str
    generated_utc: _dt.datetime


class IProactiveReasoningService(ABC):
    """Evaluates trigger conditions and, when any fires, generates a proactive
    check-in message unprompted. Mirrors ``IProactiveReasoningService``.
    """

    @abstractmethod
    async def check_async(self, user_id: str, ct: object = None) -> None:
        """Evaluate all triggers and, when any fires, generate a message and
        raise ``on_proactive_message_ready``.
        """
        ...

    @abstractmethod
    def add_proactive_message_handler(self, handler: ProactiveMessageHandler) -> None:
        """Subscribe to proactive-message notifications."""
        ...


class ProactiveReasoningService(IProactiveReasoningService):
    """Default :class:`IProactiveReasoningService`. Evaluates triggers in order
    and fires only the first that matches. Mirrors ``ProactiveReasoningService``.
    """

    __slots__ = ("_butler", "_goal_store", "_affect_store", "_triggers", "_handlers")

    def __init__(
        self,
        butler,
        goal_store=None,
        affect_store=None,
        triggers: Optional[Sequence[ITriggerCondition]] = None,
    ) -> None:
        if butler is None:
            raise ValueError("butler is required")
        if triggers is None:
            raise ValueError("triggers is required")
        self._butler = butler
        self._goal_store = goal_store
        self._affect_store = affect_store
        self._triggers: List[ITriggerCondition] = list(triggers)
        self._handlers: List[ProactiveMessageHandler] = []

    def add_proactive_message_handler(self, handler: ProactiveMessageHandler) -> None:
        if handler is None:
            raise ValueError("handler is required")
        self._handlers.append(handler)

    def remove_proactive_message_handler(self, handler: ProactiveMessageHandler) -> None:
        try:
            self._handlers.remove(handler)
        except ValueError:
            pass

    async def check_async(self, user_id: str, ct: object = None) -> None:
        if user_id is None or not user_id.strip():
            raise ValueError("user_id is required")

        if len(self._triggers) == 0:
            return

        # 1. Load affect state.
        affect: Optional[AffectState] = None
        if self._affect_store is not None:
            try:
                affect = await self._affect_store.load_async(user_id)
            except Exception:  # noqa: BLE001 - affect load is non-fatal
                affect = None

        # 2. Load active goals.
        active_goals: List[Goal] = []
        if self._goal_store is not None:
            try:
                active_goals = list(await self._goal_store.get_active_async(user_id))
            except Exception:  # noqa: BLE001 - goal load is non-fatal
                active_goals = []

        # 3. Build context snapshot.
        now = _dt.datetime.now(_UTC)
        if affect is not None:
            time_since_last = now - _as_utc(affect.last_updated_utc)
        else:
            time_since_last = _dt.timedelta(0)

        context = ProactiveContext(
            user_id=user_id,
            now_utc=now,
            time_since_last_interaction=time_since_last,
            affect_state=affect,
            active_goals=active_goals,
        )

        # 4. Check triggers in order — fire only the first.
        for trigger in self._triggers:
            try:
                met = await trigger.is_met_async(context)
            except Exception:  # noqa: BLE001 - a throwing trigger is skipped
                continue

            if not met:
                continue

            # 5. Build a proactive prompt.
            prompt = _build_proactive_prompt(user_id, time_since_last, active_goals)

            # 6. Generate the message.
            try:
                message = await self._butler.ask_async(prompt)
            except Exception:  # noqa: BLE001 - generation failure aborts this check
                return

            # 7. Raise the event.
            args = ProactiveMessageEventArgs(
                user_id=user_id,
                message=message,
                trigger_name=trigger.name,
                generated_utc=_dt.datetime.now(_UTC),
            )
            for handler in list(self._handlers):
                try:
                    handler(args)
                except Exception:  # noqa: BLE001 - handler errors are non-fatal
                    pass

            # Only fire one trigger per call.
            return


def _build_proactive_prompt(
    user_id: str,
    time_since_last_interaction: _dt.timedelta,
    active_goals: Sequence[Goal],
) -> str:
    """Mirror the C# ``BuildProactivePrompt`` string construction byte-for-byte."""
    parts: List[str] = []
    parts.append("You are B!. ")

    total_minutes = time_since_last_interaction.total_seconds() / 60.0
    if total_minutes > 5:
        hours = int(time_since_last_interaction.total_seconds() // 3600)
        minutes = int(total_minutes % 60)
        if hours > 0:
            plural = "" if hours == 1 else "s"
            parts.append(
                f"The user has been away for approximately {hours} hour{plural}. "
            )
        else:
            plural = "" if minutes == 1 else "s"
            parts.append(
                f"The user has been away for approximately {minutes} minute{plural}. "
            )

    count = len(active_goals)
    if count > 0:
        plural = "" if count == 1 else "s"
        parts.append(f"They have {count} active goal{plural}: ")
        for i, goal in enumerate(active_goals):
            parts.append('"')
            parts.append(goal.title)
            parts.append('"')
            if i < count - 1:
                parts.append(", ")
        parts.append(". ")

    parts.append("Generate a brief, friendly check-in message (1-2 sentences). ")
    parts.append("Be warm, specific to their goals if you know them, and not intrusive.")

    return "".join(parts)


def _as_utc(dt: _dt.datetime) -> _dt.datetime:
    if dt.tzinfo is None:
        return dt.replace(tzinfo=_UTC)
    return dt.astimezone(_UTC)
