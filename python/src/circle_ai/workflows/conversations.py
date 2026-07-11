# conversations.py
#
# Port of CircleAI.Workflows PacaConversations.cs (C# — the EXACT spec).
#
# (3.3.0) Conversation state machine + host-supplied executor contract. The
# actual Docker/OpenHands integration is host-supplied via IConversationExecutor;
# this module owns the state machine, per-conversation step history, and the
# lifecycle events.
#
# The C# StartAsync flips Queued -> Running, links the outer token with a
# per-conversation CancellationTokenSource, invokes the executor's RunAsync with
# an onStep callback, and lands on Finished / Stopped / Failed. Stop() cancels
# that CTS. The port models the stop signal with a _CancelToken the executor can
# poll (`is_cancellation_requested`); when the executor raises CancelledError
# while a stop was requested, the conversation lands on Stopped — matching the
# C# `catch (OperationCanceledException) when (cts.IsCancellationRequested)`.

from __future__ import annotations

import asyncio
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, replace
from datetime import datetime, timezone
from enum import IntEnum
from typing import Callable, Dict, List, Optional


class ConversationState(IntEnum):
    """Mirrors ``CircleAI.Workflows.ConversationState`` (declaration order)."""

    Queued = 0
    Running = 1
    Finished = 2
    Failed = 3
    Stopped = 4


@dataclass(frozen=True, slots=True)
class AgentConversation:
    """Mirrors ``CircleAI.Workflows.AgentConversation`` — one conversation
    between a human + an agent (or multiple agents)."""

    id: str
    project_id: str
    agent_member_id: str
    human_member_id: Optional[str]
    opening_prompt: str
    state: ConversationState
    queued_at_utc: datetime
    started_at_utc: Optional[datetime]
    finished_at_utc: Optional[datetime]
    result_json: Optional[str]
    failure_reason: Optional[str]


@dataclass(frozen=True, slots=True)
class ConversationStep:
    """Mirrors ``CircleAI.Workflows.ConversationStep`` — one executed step.
    Speaker is ``"user"`` / ``"agent"`` / ``"tool"``."""

    conversation_id: str
    order: int
    speaker: str
    content_json: str
    at: datetime


@dataclass(frozen=True, slots=True)
class ConversationPermissions:
    """Mirrors ``CircleAI.Workflows.ConversationPermissions`` — permission flag
    set required to run risky actions."""

    allow_clone_repos: bool
    allow_create_pr: bool


class _CancelToken:
    """Cooperative stop signal handed to the executor (mirrors the linked
    CancellationToken). The executor should poll ``is_cancellation_requested``
    and raise :class:`asyncio.CancelledError` (or return) when set."""

    def __init__(self) -> None:
        self._event = asyncio.Event()

    def cancel(self) -> None:
        self._event.set()

    @property
    def is_cancellation_requested(self) -> bool:
        return self._event.is_set()

    async def wait(self) -> None:
        await self._event.wait()


class IConversationExecutor(ABC):
    """(3.3.0) Host-supplied executor — invokes the OpenHands SDK / Docker
    container per conversation. Implement :meth:`run_async`; emit
    ConversationStep events via the ``on_step`` callback as work progresses."""

    @abstractmethod
    async def run_async(
        self,
        conversation: AgentConversation,
        permissions: ConversationPermissions,
        on_step: Callable[[ConversationStep], None],
        ct: _CancelToken,
    ) -> None:
        ...


class PacaConversationRuntime:
    """(3.3.0) Conversation registry + state machine."""

    def __init__(
        self,
        executor: IConversationExecutor,
        clock: Optional[Callable[[], datetime]] = None,
    ) -> None:
        if executor is None:
            raise ValueError("executor must not be None")
        self._executor = executor
        self._clock = clock if clock is not None else (lambda: datetime.now(timezone.utc))
        self._conversations: Dict[str, AgentConversation] = {}
        self._steps: Dict[str, List[ConversationStep]] = {}
        self._step_locks: Dict[str, threading.Lock] = {}
        self._running: Dict[str, _CancelToken] = {}
        self._lock = threading.Lock()

    def queue(
        self,
        id: str,
        project_id: str,
        agent_member_id: str,
        opening_prompt: str,
        human_member_id: Optional[str] = None,
    ) -> AgentConversation:
        c = AgentConversation(
            id=id,
            project_id=project_id,
            agent_member_id=agent_member_id,
            human_member_id=human_member_id,
            opening_prompt=opening_prompt or "",
            state=ConversationState.Queued,
            queued_at_utc=self._clock(),
            started_at_utc=None,
            finished_at_utc=None,
            result_json=None,
            failure_reason=None,
        )
        with self._lock:
            if id in self._conversations:
                raise RuntimeError(f"Conversation '{id}' already exists.")
            self._conversations[id] = c
            self._steps[id] = []
            self._step_locks[id] = threading.Lock()
        return c

    def get(self, id: str) -> Optional[AgentConversation]:
        with self._lock:
            return self._conversations.get(id)

    def steps(self, id: str) -> List[ConversationStep]:
        with self._lock:
            lst = self._steps.get(id)
            lock = self._step_locks.get(id)
        if lst is None or lock is None:
            return []
        with lock:
            return list(lst)

    async def start_async(
        self, id: str, permissions: ConversationPermissions, outer_ct: Optional[_CancelToken] = None
    ) -> None:
        """(3.3.0) Begin executing the conversation."""
        with self._lock:
            current = self._conversations.get(id)
            if current is None or current.state != ConversationState.Queued:
                raise RuntimeError(f"Conversation '{id}' is not in Queued state.")
            started = replace(current, state=ConversationState.Running, started_at_utc=self._clock())
            self._conversations[id] = started
            step_lock = self._step_locks[id]
            step_list = self._steps[id]

        cts = _CancelToken()
        with self._lock:
            self._running[id] = cts

        def _on_step(step: ConversationStep) -> None:
            with step_lock:
                step_list.append(step)

        try:
            await self._executor.run_async(started, permissions, _on_step, cts)
            final = replace(
                started,
                state=ConversationState.Finished,
                finished_at_utc=self._clock(),
                result_json="{}",
            )
        except asyncio.CancelledError:
            if cts.is_cancellation_requested:
                final = replace(started, state=ConversationState.Stopped, finished_at_utc=self._clock())
            else:
                # Cancellation not requested via Stop() — propagate.
                with self._lock:
                    self._running.pop(id, None)
                raise
        except Exception as ex:  # noqa: BLE001 — mirror C# catch (Exception ex)
            final = replace(
                started,
                state=ConversationState.Failed,
                finished_at_utc=self._clock(),
                failure_reason=str(ex),
            )
        with self._lock:
            self._conversations[id] = final
            self._running.pop(id, None)

    def stop(self, id: str) -> None:
        """(3.3.0) Stop a running conversation from the UI."""
        with self._lock:
            cts = self._running.get(id)
        if cts is not None:
            cts.cancel()


# Public alias for the cooperative stop token handed to executors.
ConversationCancelToken = _CancelToken
