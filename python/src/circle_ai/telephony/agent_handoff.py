# agent_handoff.py
#
# Port of CircleAI.Telephony AgentHandoff.cs (C# — the EXACT spec).
#
# (3.3.0) Multi-agent handoff: swap the AI persona mid-call without dropping the
# carrier leg. Caller A is talking to "Reception"; the reception agent decides
# this is a billing question, hands off to "Billing" — same call, same audio
# stream, different system prompt and toolset.
#
# C# IReadOnlyDictionary AgentCatalog -> a fresh dict copy per read (mirrors the
# defensive copy the C# returns). C# ILogger -> stdlib logging. Case-insensitive
# ordinal agent-id keys -> casefold() keys. The BriefingSynthesiser TTS delegate
# comes from warm_transfer_orchestrator.

from __future__ import annotations

import logging
import threading
from dataclasses import dataclass
from datetime import timedelta
from abc import ABC, abstractmethod
from typing import Dict, Iterable, Optional

from .contracts import ICallSession
from .primitives import AudioFrame, CallMediaFormat
from .warm_transfer_orchestrator import BriefingSynthesiser

_logger = logging.getLogger("CircleAI.Telephony.AgentHandoff")


@dataclass(frozen=True, slots=True)
class CallAgent:
    """(3.3.0) One AI agent persona that can be handed control of a call.

    ``agent_id``: stable id ("reception" / "billing" / "tier2-support").
    ``display_name``: friendly name surfaced to logging + analytics.
    ``system_prompt``: persona instructions.
    ``greeting_text``: optional first sentence the agent says when it takes over.
    """

    agent_id: str
    display_name: str
    system_prompt: str
    greeting_text: Optional[str] = None


@dataclass(frozen=True, slots=True)
class HandoffResult:
    """(3.3.0) Outcome of a handoff attempt.

    Mirrors ``record(bool Succeeded, string? FailureReason, CallAgent? ActiveAgent)``.
    """

    succeeded: bool
    failure_reason: Optional[str]
    active_agent: Optional[CallAgent]


class IAgentHandoffOrchestrator(ABC):
    """(3.3.0) Drives mid-call agent handoff."""

    @property
    @abstractmethod
    def current_agent(self) -> Optional[CallAgent]:
        """The agent currently in control of the call."""

    @property
    @abstractmethod
    def agent_catalog(self) -> Dict[str, CallAgent]:
        """Available agents indexed by id."""

    @abstractmethod
    async def handoff_async(
        self,
        session: ICallSession,
        target_agent_id: str,
        tts: BriefingSynthesiser,
        *,
        ct: Optional[object] = None,
    ) -> HandoffResult:
        """Hand the call over to ``target_agent_id``; speaks the greeting via TTS."""

    @abstractmethod
    def register_agent(self, agent: CallAgent) -> None:
        """Register / replace an agent in the catalog at runtime."""

    @abstractmethod
    def set_initial_agent(self, agent_id: str) -> None:
        """Set the initial agent on a fresh call without TTS (no greeting)."""


class DefaultAgentHandoffOrchestrator(IAgentHandoffOrchestrator):
    """(3.3.0) Default in-memory orchestrator. Thread-safe via simple lock."""

    def __init__(
        self,
        seed: Optional[Iterable[CallAgent]] = None,
        logger: Optional[logging.Logger] = None,
    ) -> None:
        self._gate = threading.Lock()
        self._agents: Dict[str, CallAgent] = {}
        self._current: Optional[CallAgent] = None
        self._logger = logger if logger is not None else _logger
        if seed is not None:
            for agent in seed:
                self._agents[agent.agent_id.casefold()] = agent

    @property
    def current_agent(self) -> Optional[CallAgent]:
        with self._gate:
            return self._current

    @property
    def agent_catalog(self) -> Dict[str, CallAgent]:
        with self._gate:
            # Keyed by the original agent id (not casefolded) to mirror the C#
            # dictionary keyed on AgentId with an OrdinalIgnoreCase comparer.
            return {a.agent_id: a for a in self._agents.values()}

    def register_agent(self, agent: CallAgent) -> None:
        if agent is None:
            raise ValueError("agent must not be None")
        if not agent.agent_id or agent.agent_id.isspace():
            raise ValueError("AgentId is required.")
        with self._gate:
            self._agents[agent.agent_id.casefold()] = agent

    def set_initial_agent(self, agent_id: str) -> None:
        with self._gate:
            agent = self._agents.get(agent_id.casefold())
            if agent is None:
                raise RuntimeError(f"Agent '{agent_id}' is not registered.")
            self._current = agent

    async def handoff_async(
        self,
        session: ICallSession,
        target_agent_id: str,
        tts: BriefingSynthesiser,
        *,
        ct: Optional[object] = None,
    ) -> HandoffResult:
        if session is None:
            raise ValueError("session must not be None")
        if tts is None:
            raise ValueError("tts must not be None")
        if not target_agent_id or target_agent_id.isspace():
            with self._gate:
                current = self._current
            return HandoffResult(False, "targetAgentId is required", current)

        with self._gate:
            target = self._agents.get(target_agent_id.casefold())
            if target is None:
                return HandoffResult(False, f"Agent '{target_agent_id}' is not registered.", self._current)
            previous = self._current
            if previous is not None and previous.agent_id.casefold() == target.agent_id.casefold():
                return HandoffResult(True, None, previous)
            self._current = target

        self._logger.info(
            "Call %s handed off from %s to %s",
            session.info.call_id,
            previous.display_name if previous is not None else "(none)",
            target.display_name,
        )

        if target.greeting_text and not target.greeting_text.isspace():
            try:
                greeting_pcm = await tts(target.greeting_text, ct)
                if greeting_pcm:
                    await session.send_audio_async(
                        AudioFrame(greeting_pcm, CallMediaFormat.PCM24000, timedelta(0)), ct=ct
                    )
            except Exception as ex:
                self._logger.warning("Greeting playback failed during handoff to %s: %s", target.agent_id, ex)

        return HandoffResult(True, None, target)
