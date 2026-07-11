# voice_loop_as_tool.py
#
# Port of CircleAI.Telephony VoiceLoopAsTool.cs (C# — the EXACT spec).
#
# (3.3.0) Expose the CircleAI voice loop as a tool an external agent framework
# (LangGraph, OpenAI Agents, CrewAI) can call. The framework hands us a number
# to call + a script, we drive the call to completion, return a structured
# result.
#
# C# CancellationTokenSource(maxDuration) + linked token -> asyncio.wait_for
# around the host runner; a wait_for TimeoutError is the C# "timed out" branch.
# C# Func<VoiceLoopToolRequest, CancellationToken, Task<VoiceLoopToolResult>>
# runner -> an async Callable. The static Descriptor is a module-level
# ToolDefinition mirroring the C# ``Descriptor`` property.

from __future__ import annotations

import asyncio
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import timedelta
from typing import Awaitable, Callable, Optional

from .tool_calling import ToolDefinition


@dataclass(frozen=True, slots=True)
class VoiceLoopToolRequest:
    """(3.3.0) Request to make one outbound voice call as a tool invocation.

    Mirrors ``record(string ToNumber, string Goal, string? ContextJson, string?
    SystemPrompt, TimeSpan? MaxDuration)``.
    """

    to_number: str
    goal: str
    context_json: Optional[str] = None
    system_prompt: Optional[str] = None
    max_duration: Optional[timedelta] = None


@dataclass(frozen=True, slots=True)
class VoiceLoopToolResult:
    """(3.3.0) Result of the call returned to the calling agent.

    Mirrors ``record(bool GoalAchieved, string Summary, string CallId, TimeSpan
    Duration, string Transcript, string? StructuredOutputJson)``.
    """

    goal_achieved: bool
    summary: str
    call_id: str
    duration: timedelta
    transcript: str
    structured_output_json: Optional[str]


class IVoiceLoopTool(ABC):
    """(3.3.0) Voice-loop-as-a-tool surface."""

    @abstractmethod
    async def invoke_async(
        self, request: VoiceLoopToolRequest, *, ct: Optional[object] = None
    ) -> VoiceLoopToolResult:
        """(3.3.0) Make the call and report back."""


VoiceLoopRunner = Callable[[VoiceLoopToolRequest, Optional[object]], Awaitable[VoiceLoopToolResult]]


class VoiceLoopAsTool(IVoiceLoopTool):
    """(3.3.0) Driver that delegates the actual call to a host-supplied runner."""

    def __init__(
        self,
        runner: VoiceLoopRunner,
        default_max_duration: Optional[timedelta] = None,
    ) -> None:
        if runner is None:
            raise ValueError("runner must not be None")
        self._runner = runner
        self._default_max_duration = (
            default_max_duration if default_max_duration is not None else timedelta(minutes=5)
        )

    async def invoke_async(
        self, request: VoiceLoopToolRequest, *, ct: Optional[object] = None
    ) -> VoiceLoopToolResult:
        if request is None:
            raise ValueError("request must not be None")
        if not request.to_number or request.to_number.isspace():
            raise ValueError("ToNumber is required.")
        if not request.goal or request.goal.isspace():
            raise ValueError("Goal is required.")

        max_duration = request.max_duration if request.max_duration is not None else self._default_max_duration
        try:
            return await asyncio.wait_for(
                self._runner(request, ct), timeout=max_duration.total_seconds()
            )
        except asyncio.TimeoutError:
            minutes = max_duration.total_seconds() / 60.0
            return VoiceLoopToolResult(
                goal_achieved=False,
                summary=f"Call timed out after {minutes:.1f} minutes.",
                call_id="",
                duration=max_duration,
                transcript="",
                structured_output_json=None,
            )

    #: (3.3.0) Tool descriptor for use with :class:`IToolCallRegistry`.
    Descriptor: ToolDefinition


VoiceLoopAsTool.Descriptor = ToolDefinition(
    name="make_voice_call",
    description=(
        "Place an outbound phone call and follow the supplied goal/script. "
        "Returns whether the goal was achieved."
    ),
    arguments_json_schema="""{
          "type": "object",
          "properties": {
            "to_number":     { "type": "string", "description": "E.164 destination." },
            "goal":          { "type": "string" },
            "context_json":  { "type": "string", "nullable": true },
            "system_prompt": { "type": "string", "nullable": true },
            "max_duration_seconds": { "type": "integer", "nullable": true }
          },
          "required": ["to_number", "goal"]
        }""",
)
