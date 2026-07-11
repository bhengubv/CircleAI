# telemetry.py
#
# Port of CircleAI.Telephony Telemetry.cs (C# — the EXACT spec).
#
# (3.3.0) Trace spans for the voice loop. The C# uses .NET's
# System.Diagnostics.ActivitySource and the host wires an OpenTelemetry exporter
# to surface spans. The Python tree carries no opentelemetry dependency, so this
# is a faithful, dependency-free shim: the same stable SOURCE_NAME plus the same
# start_* / record_outcome surface. Each ``start_*`` returns an :class:`Activity`
# (never None here — the C# returns ``Activity?``, null only when no listener is
# attached; callers already null-check, and record_outcome tolerates None). The
# Activity is a context manager so ``with telemetry.start_turn(id) as span:``
# mirrors the C# ``using`` disposal of the returned Activity.

from __future__ import annotations

from enum import IntEnum
from typing import Dict, Optional


class ActivityStatusCode(IntEnum):
    """Mirrors ``System.Diagnostics.ActivityStatusCode``."""

    UNSET = 0
    OK = 1
    ERROR = 2


class ActivityKind(IntEnum):
    """Mirrors ``System.Diagnostics.ActivityKind`` (subset the voice loop uses)."""

    INTERNAL = 0
    SERVER = 1
    CLIENT = 2
    PRODUCER = 3
    CONSUMER = 4


class Activity:
    """(3.3.0) Minimal stand-in for ``System.Diagnostics.Activity``.

    Carries a name, kind, tag bag, and status. Supports the context-manager
    protocol so callers can ``with`` it (the C# disposes the Activity to end the
    span).
    """

    __slots__ = ("operation_name", "kind", "tags", "status", "status_description")

    def __init__(self, operation_name: str, kind: "ActivityKind" = ActivityKind.INTERNAL) -> None:
        self.operation_name = operation_name
        self.kind = kind
        self.tags: Dict[str, object] = {}
        self.status = ActivityStatusCode.UNSET
        self.status_description: Optional[str] = None

    def set_tag(self, key: str, value: object) -> "Activity":
        self.tags[key] = value
        return self

    def set_status(self, code: "ActivityStatusCode", description: Optional[str] = None) -> "Activity":
        self.status = code
        self.status_description = description
        return self

    def __enter__(self) -> "Activity":
        return self

    def __exit__(self, *exc_info: object) -> None:
        # End of span. Nothing to flush in the shim.
        return None


class VoiceLoopTelemetry:
    """(3.3.0) Public trace source for the voice loop. Mirrors the C#
    ``VoiceLoopTelemetry`` static class."""

    #: (3.3.0) ActivitySource name CircleAI uses for voice-loop spans.
    SOURCE_NAME = "CircleAI.Telephony.VoiceLoop"

    #: (3.3.0) Version the C# stamps on the ActivitySource.
    SOURCE_VERSION = "3.3.0"

    @staticmethod
    def start_turn(call_id: str) -> Activity:
        """(3.3.0) Start a span for one voice loop turn."""
        act = Activity("voice_loop.turn", ActivityKind.INTERNAL)
        act.set_tag("call.id", call_id)
        return act

    @staticmethod
    def start_asr(backend: str) -> Activity:
        """(3.3.0) Start a span around the STT stage."""
        act = Activity("voice_loop.asr", ActivityKind.CLIENT)
        act.set_tag("backend", backend)
        return act

    @staticmethod
    def start_llm(provider: str, model: str) -> Activity:
        """(3.3.0) Start a span around the LLM stage."""
        act = Activity("voice_loop.llm", ActivityKind.CLIENT)
        act.set_tag("provider", provider)
        act.set_tag("model", model)
        return act

    @staticmethod
    def start_tts(backend: str, voice_id: Optional[str] = None) -> Activity:
        """(3.3.0) Start a span around the TTS stage."""
        act = Activity("voice_loop.tts", ActivityKind.CLIENT)
        act.set_tag("backend", backend)
        act.set_tag("voice", voice_id)
        return act

    @staticmethod
    def record_outcome(
        activity: Optional[Activity], success: bool, error_reason: Optional[str] = None
    ) -> None:
        """(3.3.0) Tag a turn span with its outcome."""
        if activity is None:
            return
        activity.set_tag("outcome", "success" if success else "failure")
        if not success and error_reason is not None:
            activity.set_tag("error.message", error_reason)
            activity.set_status(ActivityStatusCode.ERROR, error_reason)
        elif success:
            activity.set_status(ActivityStatusCode.OK)
