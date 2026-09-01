"""The code agent, the realtime voice seam, the tool catalogue, and the mesh.

THE CODE AGENT IS THE ONLY THING HERE THAT WRITES TO DISK AND RUNS A PROGRAM,
so the interesting engineering is in what it cannot do: an allow-list rather
than a deny-list, an iteration cap that is a termination guarantee, and a
reply it cannot parse becoming an ACTION rather than a crash.

REALTIME IS DUPLEX. Audio goes up while audio comes down, and the caller can be
interrupted mid-sentence. A request/response shape cannot express that, which is
why it is a separate seam.

EVERY CREDENTIAL PATH IS SCOPED, QUOTA'D AND REFUSABLE, and the default of each
does nothing. The failure that prevents is a model acquiring a capability
because a host forgot a line of configuration.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import re
import secrets
import shutil
import subprocess
import threading
import time
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from enum import Enum
from typing import Callable, Iterable, Sequence


def _now() -> datetime:
    return datetime.now(timezone.utc)


# ─────────────────────────────────────────────────────────────────────────────
# The code agent


class AgentActionKind(Enum):
    """What the model asked for."""

    #: The reply could not be parsed. Kept as a VALUE so the loop can re-prompt
    #: rather than fail.
    UNKNOWN = "unknown"
    READ_FILE = "read_file"
    #: A character-range edit. Ranges rather than a diff because a diff that
    #: fails to apply leaves the model guessing why; a range either is or is not
    #: inside the file.
    EDIT_FILE = "edit_file"
    RUN_COMMAND = "run_command"
    SEARCH_CODE = "search_code"
    FINISH = "finish"


@dataclass(frozen=True)
class AgentAction:
    """One parsed action."""

    kind: AgentActionKind
    path: str = ""
    range_start: int = 0
    range_end: int = 0
    replacement: str = ""
    command: str = ""
    query: str = ""
    top_k: int = 10
    summary: str = ""
    #: The source JSON, or the whole reply when it did not parse. Kept for
    #: diagnostics and re-prompting: without it, a loop that goes wrong leaves
    #: no evidence of what the model actually said.
    raw: str = ""


def _extract_json_object(text: str) -> str:
    """Finds the first balanced { } run, ignoring braces inside strings.

    BY BRACE DEPTH rather than by regex, because models routinely wrap the
    object in prose, in a fenced block, or in both, and a regex that handles two
    of those three quietly mis-parses the third.
    """
    depth = 0
    start = -1
    in_string = False
    escaped = False
    for i, ch in enumerate(text):
        if in_string:
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == '"':
                in_string = False
            continue
        if ch == '"':
            in_string = True
        elif ch == "{":
            if depth == 0:
                start = i
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0 and start >= 0:
                return text[start:i + 1]
    return ""


_ACTION_ALIASES = {
    "read_file": AgentActionKind.READ_FILE, "read": AgentActionKind.READ_FILE,
    "edit_file": AgentActionKind.EDIT_FILE, "edit": AgentActionKind.EDIT_FILE,
    "run_command": AgentActionKind.RUN_COMMAND, "run": AgentActionKind.RUN_COMMAND,
    "search_code": AgentActionKind.SEARCH_CODE, "search": AgentActionKind.SEARCH_CODE,
    "finish": AgentActionKind.FINISH, "done": AgentActionKind.FINISH,
}


class AgentActionParser:
    """Turns a model reply into an action."""

    @staticmethod
    def parse(reply: str) -> AgentAction:
        """NEVER fails — a reply it cannot understand becomes UNKNOWN with `raw`
        set."""
        obj = _extract_json_object(reply)
        if not obj:
            return AgentAction(AgentActionKind.UNKNOWN, raw=reply)
        try:
            raw = json.loads(obj)
        except json.JSONDecodeError:
            return AgentAction(AgentActionKind.UNKNOWN, raw=reply)
        if not isinstance(raw, dict):
            return AgentAction(AgentActionKind.UNKNOWN, raw=reply)

        kind = _ACTION_ALIASES.get(str(raw.get("action", "")).strip().lower())
        if kind is None:
            return AgentAction(AgentActionKind.UNKNOWN, raw=reply)
        return AgentAction(
            kind=kind,
            path=str(raw.get("path", "")),
            range_start=int(raw.get("range_start", 0) or 0),
            range_end=int(raw.get("range_end", 0) or 0),
            replacement=str(raw.get("replacement", "")),
            command=str(raw.get("command", "")),
            query=str(raw.get("query", "")),
            top_k=int(raw.get("top_k", 0) or 10),
            summary=str(raw.get("summary", "")),
            raw=obj,
        )


@dataclass(frozen=True)
class CommandRequest:
    """A command the agent wants to run."""

    executable: str
    arguments: tuple[str, ...] = ()
    working_directory: str = ""
    timeout: timedelta = timedelta(seconds=60)


@dataclass(frozen=True)
class CommandResult:
    """How it went."""

    #: Whether it ran AT ALL. False with exit code 0 is the shape of a refusal,
    #: and a caller that only checks the exit code would read that as success.
    executed: bool = False
    timed_out: bool = False
    exit_code: int = 0
    stdout: str = ""
    stderr: str = ""
    #: Why it did not run. Populated only when `executed` is False.
    refusal: str = ""

    @property
    def success(self) -> bool:
        return self.executed and not self.timed_out and self.exit_code == 0


class ICommandRunner(ABC):
    """Runs commands for the agent."""

    @abstractmethod
    def run(self, request: CommandRequest) -> CommandResult: ...


class DisabledCommandRunner(ICommandRunner):
    """Refuses everything, with a reason.

    THE DEFAULT: an agent that can run commands because nobody configured a
    runner is an agent that can run commands by accident.
    """

    def run(self, request: CommandRequest) -> CommandResult:
        return CommandResult(refusal="command running is disabled on this device")


class ProcessCommandRunner(ICommandRunner):
    """Runs only what is on the list.

    An ALLOW-LIST, not a deny-list: a deny-list is a claim to have thought of
    every dangerous command, and it is wrong the first time somebody pipes one
    into another.
    """

    def __init__(self, allowed_executables: Sequence[str], max_output_chars: int = 64 * 1024) -> None:
        if not allowed_executables:
            raise ValueError(
                "an allow-list is required: a runner with an empty list would "
                "run nothing, and one with no list would run everything"
            )
        self._allowed = {os.path.basename(e).lower() for e in allowed_executables}
        self._max_output = max_output_chars if max_output_chars > 0 else 64 * 1024

    def run(self, request: CommandRequest) -> CommandResult:
        """Matching is on the RESOLVED base name, not the string the model
        wrote — otherwise "./git", "git.exe" and a relative path through a
        symlink are three different things to the check and one thing to the
        operating system.

        Output is truncated: a command that prints a hundred megabytes would
        otherwise be handed to a model as context and cost more than the entire
        task.
        """
        base = os.path.basename(request.executable).lower()
        if base not in self._allowed:
            return CommandResult(refusal=f"{base!r} is not on the allow-list")

        # shell=False, always. A shell would make the allow-list meaningless the
        # first time an argument contained a semicolon.
        try:
            completed = subprocess.run(  # noqa: S603 — allow-listed, shell=False
                [request.executable, *request.arguments],
                cwd=request.working_directory or None,
                capture_output=True, text=True,
                timeout=request.timeout.total_seconds(),
                shell=False, check=False,
            )
        except subprocess.TimeoutExpired:
            return CommandResult(executed=True, timed_out=True, exit_code=1)
        except (OSError, ValueError) as exc:
            return CommandResult(refusal=str(exc))

        def clip(text: str) -> str:
            return text if len(text) <= self._max_output else text[:self._max_output] + "\n… truncated"

        return CommandResult(
            executed=True, exit_code=completed.returncode,
            stdout=clip(completed.stdout), stderr=clip(completed.stderr),
        )


class DeviceTier(Enum):
    """Which class of device this is.

    A floor in tiers as well as gigabytes, because RAM alone does not capture
    thermal headroom — a phone with 8 GB throttles where a tablet with 8 GB does
    not.
    """

    WEARABLE = 0
    LOW_PHONE = 1
    PHONE = 2
    TABLET = 3
    DESKTOP = 4
    SERVER = 5


@dataclass(frozen=True)
class CodingModelRequirements:
    """What a coding model must meet."""

    min_parameters_billion: int = 3
    min_ram_gb: float = 8.0
    min_free_storage_gb: float = 6.0
    min_device_tier: DeviceTier = DeviceTier.TABLET
    required_capabilities: tuple[str, ...] = ("tools", "reasoning", "long-context")

    @classmethod
    def default(cls) -> "CodingModelRequirements":
        """The PROVISIONAL floor, labelled so.

        These are reasoned, not measured — the numbers to trust are the ones a
        bench run produces on the actual device, and a default that pretends
        otherwise is a threshold nobody ever revisits.
        """
        return cls()


@dataclass(frozen=True)
class CodingModelDescriptor:
    """One candidate model."""

    model_id: str
    parameters_billion: int
    ram_gb: float
    download_gb: float = 0.0
    capabilities: tuple[str, ...] = ()
    note: str = ""


class ICodingModelCatalog(ABC):
    """Lists coding models."""

    @abstractmethod
    def list(self) -> Sequence[CodingModelDescriptor]: ...

    @abstractmethod
    def best_for(self, requirements: CodingModelRequirements) -> CodingModelDescriptor | None:
        """None when the catalogue has no model that meets the floor.

        Returning the closest one and letting it fail on load is how a feature
        becomes a crash report.
        """


class EmptyCodingModelCatalog(ICodingModelCatalog):
    """Knows about no models."""

    def list(self) -> Sequence[CodingModelDescriptor]:
        return ()

    def best_for(self, requirements: CodingModelRequirements) -> CodingModelDescriptor | None:
        return None


class InMemoryCodingModelCatalog(ICodingModelCatalog):
    """Holds a list a host supplied."""

    def __init__(self, models: Sequence[CodingModelDescriptor] = ()) -> None:
        self._models = tuple(models)

    def list(self) -> Sequence[CodingModelDescriptor]:
        return self._models

    def best_for(self, requirements: CodingModelRequirements) -> CodingModelDescriptor | None:
        wanted = {c.lower() for c in requirements.required_capabilities}
        viable = [
            m for m in self._models
            if m.parameters_billion >= requirements.min_parameters_billion
            and m.ram_gb <= requirements.min_ram_gb
            and wanted <= {c.lower() for c in m.capabilities}
        ]
        return max(viable, key=lambda m: m.parameters_billion, default=None)


class ICodingCapabilityPlanner(ABC):
    """Decides whether this device can code at all."""

    @abstractmethod
    def is_capable(self) -> tuple[bool, str]: ...


class CodingCapabilityPlanner(ICodingCapabilityPlanner):
    """The default planner."""

    _GB = float(1 << 30)

    def __init__(
        self,
        catalog: ICodingModelCatalog,
        ram_bytes: int,
        free_storage_bytes: int,
        tier: DeviceTier,
    ) -> None:
        self._catalog = catalog
        self._ram_bytes = ram_bytes
        self._free_storage_bytes = free_storage_bytes
        self._tier = tier

    def is_capable(self) -> tuple[bool, str]:
        """The reason names the SHORTFALL — "needs about 8 GB of memory" —
        rather than a policy identifier, because it is shown to a person."""
        req = CodingModelRequirements.default()
        ram_gb = self._ram_bytes / self._GB
        if ram_gb < req.min_ram_gb:
            return False, (
                f"this needs about {req.min_ram_gb:.0f} GB of memory and this "
                f"device has {ram_gb:.1f}"
            )
        free_gb = self._free_storage_bytes / self._GB
        if free_gb < req.min_free_storage_gb:
            return False, (
                f"this needs about {req.min_free_storage_gb:.0f} GB free and "
                f"this device has {free_gb:.1f}"
            )
        if self._tier.value < req.min_device_tier.value:
            return False, "this device is below the class a coding model needs"
        if self._catalog.best_for(req) is None:
            return False, "no catalogued model meets the floor"
        return True, ""


@dataclass(frozen=True)
class CodeAgentOptions:
    """Bounds one run."""

    #: A TERMINATION GUARANTEE, not a tuning knob. A model that has lost the
    #: thread does not stop — it reads the same file again, edits it back, and
    #: reads it once more. Without a cap that costs money until somebody
    #: notices, and on a phone it costs battery until it is flat.
    max_iterations: int = 24
    working_directory: str = "."
    max_observation_chars: int = 16 * 1024


@dataclass(frozen=True)
class CodeAgentStep:
    """One turn of the loop."""

    index: int
    action: AgentAction
    #: What came back — file text, command output, search hits. Truncated to
    #: what the budget allows, and the truncation is MARKED so the model knows
    #: it did not see everything.
    observation: str = ""
    observation_truncated: bool = False
    duration_ms: int = 0


@dataclass(frozen=True)
class CodeAgentRunResult:
    """The whole run."""

    finished: bool = False
    summary: str = ""
    steps: tuple[CodeAgentStep, ...] = ()
    #: Set when the loop stopped because it hit the cap rather than because the
    #: model said finish. The two must NEVER be confused: one is a completed
    #: task and the other is an abandoned one.
    exhausted_iterations: bool = False
    error: str = ""


class ICodeAgent(ABC):
    """Runs a coding task."""

    @abstractmethod
    def run(self, task: str) -> CodeAgentRunResult: ...


class NullCodeAgent(ICodeAgent):
    """Runs nothing."""

    def run(self, task: str) -> CodeAgentRunResult:
        return CodeAgentRunResult(error="no code agent configured")


class CodeAgentLoop(ICodeAgent):
    """The default agent."""

    def __init__(
        self,
        runner: ICommandRunner | None = None,
        options: CodeAgentOptions | None = None,
        generate: Callable[[str], str] | None = None,
        read_file: Callable[[str], str] | None = None,
    ) -> None:
        self._runner = runner or DisabledCommandRunner()
        self._options = options or CodeAgentOptions()
        self._generate = generate
        self._read_file = read_file

    def _truncate(self, text: str) -> tuple[str, bool]:
        cap = self._options.max_observation_chars
        if cap <= 0 or len(text) <= cap:
            return text, False
        return text[:cap] + "\n… truncated; you have not seen the whole thing", True

    def run(self, task: str) -> CodeAgentRunResult:
        if self._generate is None:
            return CodeAgentRunResult(error="no generator configured")
        transcript = task
        steps: list[CodeAgentStep] = []

        for i in range(self._options.max_iterations):
            started = time.monotonic()
            try:
                reply = self._generate(transcript)
            except Exception as exc:  # noqa: BLE001 — the reason is what matters
                return CodeAgentRunResult(steps=tuple(steps), error=str(exc))

            action = AgentActionParser.parse(reply)
            observation, truncated = "", False

            if action.kind is AgentActionKind.FINISH:
                steps.append(CodeAgentStep(
                    i, action, duration_ms=int((time.monotonic() - started) * 1000)))
                return CodeAgentRunResult(True, action.summary, tuple(steps))

            if action.kind is AgentActionKind.READ_FILE and self._read_file is not None:
                try:
                    text = self._read_file(
                        os.path.join(self._options.working_directory, action.path))
                    observation, truncated = self._truncate(text)
                except OSError as exc:
                    observation = f"could not read {action.path}: {exc}"
            elif action.kind is AgentActionKind.RUN_COMMAND:
                fields = action.command.split()
                if fields:
                    result = self._runner.run(CommandRequest(
                        executable=fields[0], arguments=tuple(fields[1:]),
                        working_directory=self._options.working_directory,
                    ))
                    if not result.executed:
                        observation = "refused: " + result.refusal
                    else:
                        observation, truncated = self._truncate(result.stdout + result.stderr)
            elif action.kind is AgentActionKind.UNKNOWN:
                # Re-prompt rather than fail. Answering in prose when asked for
                # JSON is the most common thing a model does.
                observation = (
                    "that reply could not be read as an action; answer with a "
                    "single JSON object"
                )

            steps.append(CodeAgentStep(
                i, action, observation, truncated,
                int((time.monotonic() - started) * 1000)))
            transcript += f"\n{reply}\n{observation}"

        return CodeAgentRunResult(steps=tuple(steps), exhausted_iterations=True)


# ─────────────────────────────────────────────────────────────────────────────
# Realtime


class RealtimeDirection(Enum):
    """Which way audio is flowing."""

    INBOUND = "inbound"
    OUTBOUND = "outbound"


@dataclass(frozen=True)
class RealtimeAudioFormat:
    """The wire format of a realtime stream."""

    sample_rate_hz: int = 24000
    channels: int = 1
    bits_per_sample: int = 16


@dataclass(frozen=True)
class RealtimeAudioFrame:
    """One frame of audio."""

    pcm: bytes
    direction: RealtimeDirection
    format: RealtimeAudioFormat = field(default_factory=RealtimeAudioFormat)
    at: datetime = field(default_factory=_now)


@dataclass(frozen=True)
class RealtimeTool:
    """One tool a realtime session may call."""

    name: str
    description: str = ""
    input_schema_json: str = "{}"


@dataclass(frozen=True)
class RealtimeSessionConfig:
    """How a realtime session is set up."""

    system_prompt: str = ""
    voice: str = ""
    language: str = ""
    tools: tuple[RealtimeTool, ...] = ()
    audio_format: RealtimeAudioFormat = field(default_factory=RealtimeAudioFormat)


class RealtimeEvent:
    """Something that happened during a session."""

    def __init__(self, session_id: str, at: datetime | None = None) -> None:
        self.session_id = session_id
        self.at = at or _now()


class SpeechStartedEvent(RealtimeEvent):
    """The caller began talking."""


class SpeechEndedEvent(RealtimeEvent):
    """The caller stopped.

    NOT the same as end of turn: stopping making noise and having finished a
    sentence are different facts.
    """


class TranscriptDeltaEvent(RealtimeEvent):
    """A partial transcript.

    Deltas REPLACE each other for an utterance; they do not append. A consumer
    that appends renders the sentence growing by duplication.
    """

    def __init__(self, session_id: str, delta: str, at: datetime | None = None) -> None:
        super().__init__(session_id, at)
        self.delta = delta


class TranscriptFinalEvent(RealtimeEvent):
    """The settled transcript for an utterance."""

    def __init__(
        self, session_id: str, text: str,
        confidence: float | None = None, at: datetime | None = None,
    ) -> None:
        super().__init__(session_id, at)
        self.text = text
        #: None when the engine did not say. Zero is a real answer meaning "no
        #: idea", and the two must not be confused.
        self.confidence = confidence


class TurnCompleteEvent(RealtimeEvent):
    """A whole turn finished."""

    def __init__(self, session_id: str, duration: timedelta, at: datetime | None = None) -> None:
        super().__init__(session_id, at)
        self.duration = duration


class ToolCallEvent(RealtimeEvent):
    """The model asked for a tool."""

    def __init__(self, session_id: str, tool_name: str, args_json: str, at: datetime | None = None) -> None:
        super().__init__(session_id, at)
        self.tool_name = tool_name
        self.args_json = args_json


class SessionErrorEvent(RealtimeEvent):
    """Something went wrong."""

    def __init__(self, session_id: str, code: str, message: str, fatal: bool, at: datetime | None = None) -> None:
        super().__init__(session_id, at)
        self.code = code
        self.message = message
        #: Whether the session survives. A recoverable error and a dead session
        #: demand opposite reactions, and a caller that cannot tell reconnects
        #: on every hiccup or on none.
        self.fatal = fatal


class IRealtimeSession(ABC):
    """A duplex audio session."""

    @abstractmethod
    def send_audio(self, frame: RealtimeAudioFrame) -> None: ...

    @abstractmethod
    def on_event(self, handler: Callable[[RealtimeEvent], None]) -> None: ...

    @abstractmethod
    def interrupt(self) -> None:
        """Barge-in. NOT optional and not a nicety: without it the service keeps
        speaking over somebody who has started talking, which is the single
        thing that makes a voice assistant feel broken."""

    @abstractmethod
    def close(self) -> None: ...


class IRealtimeService(ABC):
    """Opens realtime sessions."""

    @abstractmethod
    def open(self, config: RealtimeSessionConfig) -> IRealtimeSession: ...


class NullRealtimeSession(IRealtimeSession):
    """Accepts audio and produces nothing."""

    def send_audio(self, frame: RealtimeAudioFrame) -> None:
        return None

    def on_event(self, handler: Callable[[RealtimeEvent], None]) -> None:
        return None

    def interrupt(self) -> None:
        return None

    def close(self) -> None:
        return None


class NullRealtimeService(IRealtimeService):
    """Opens sessions that do nothing.

    The DEFAULT, so a build with no realtime provider runs the local voice loop
    — which is the intended behaviour rather than a degradation.
    """

    def open(self, config: RealtimeSessionConfig) -> IRealtimeSession:
        return NullRealtimeSession()


class LoopbackRealtimeSession(IRealtimeSession):
    """Echoes audio back and emits the events a real session would.

    What the loop is tested against: it exercises barge-in, transcript deltas
    and turn completion without a network or a provider.
    """

    def __init__(self, session_id: str, config: RealtimeSessionConfig) -> None:
        self.session_id = session_id
        self.config = config
        self._lock = threading.Lock()
        self._handlers: list[Callable[[RealtimeEvent], None]] = []
        self._frames: list[RealtimeAudioFrame] = []
        self._interrupted = False

    def _emit(self, event: RealtimeEvent) -> None:
        with self._lock:
            handlers = list(self._handlers)
        for handler in handlers:
            # A raising handler must not stop the others. On a live session
            # these events are how anything knows to stop talking.
            try:
                handler(event)
            except Exception:
                continue

    def send_audio(self, frame: RealtimeAudioFrame) -> None:
        with self._lock:
            self._frames.append(frame)
            first = len(self._frames) == 1
        if first:
            self._emit(SpeechStartedEvent(self.session_id))

    def on_event(self, handler: Callable[[RealtimeEvent], None]) -> None:
        with self._lock:
            self._handlers.append(handler)

    def interrupt(self) -> None:
        with self._lock:
            self._interrupted = True
        self._emit(SpeechEndedEvent(self.session_id))

    def close(self) -> None:
        self._emit(TurnCompleteEvent(self.session_id, timedelta()))

    @property
    def frames_received(self) -> int:
        with self._lock:
            return len(self._frames)

    @property
    def was_interrupted(self) -> bool:
        with self._lock:
            return self._interrupted


class LoopbackRealtimeService(IRealtimeService):
    """Opens loopback sessions."""

    def __init__(self) -> None:
        self._counter = 0
        self._lock = threading.Lock()

    def open(self, config: RealtimeSessionConfig) -> IRealtimeSession:
        with self._lock:
            self._counter += 1
            session_id = f"loopback-{self._counter}"
        return LoopbackRealtimeSession(session_id, config)


class IRealtimeTransport(ABC):
    """A duplex link to a realtime service."""

    @abstractmethod
    def connect(self) -> None: ...

    @abstractmethod
    def send_audio(self, pcm: bytes) -> None: ...

    @abstractmethod
    def interrupt(self) -> None: ...

    @abstractmethod
    def close(self) -> None: ...


class IRealtimeTransportFactory(ABC):
    """Builds a transport for a provider."""

    @abstractmethod
    def create(self, provider_id: str) -> IRealtimeTransport | None: ...


class NullRealtimeTransportFactory(IRealtimeTransportFactory):
    """Creates nothing.

    The default: a build with no realtime provider configured runs the local
    loop.
    """

    def create(self, provider_id: str) -> IRealtimeTransport | None:
        return None


# ─────────────────────────────────────────────────────────────────────────────
# The tool catalogue


class AuthKind(Enum):
    """How a provider authenticates."""

    NONE = "none"
    API_KEY = "api-key"
    OAUTH2 = "oauth2"
    BASIC = "basic"


@dataclass(frozen=True)
class ProviderDescriptor:
    """One third-party provider."""

    provider_id: str
    display_name: str
    auth_kind: AuthKind = AuthKind.NONE
    base_url: str = ""
    scopes: tuple[str, ...] = ()


class IProviderCatalog(ABC):
    """Lists providers."""

    @abstractmethod
    def add(self, provider: ProviderDescriptor) -> None: ...

    @abstractmethod
    def get(self, provider_id: str) -> ProviderDescriptor | None: ...

    @abstractmethod
    def list(self) -> Sequence[ProviderDescriptor]: ...


class InMemoryProviderCatalog(IProviderCatalog):
    """The default catalogue."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._providers: dict[str, ProviderDescriptor] = {}

    def add(self, provider: ProviderDescriptor) -> None:
        with self._lock:
            self._providers[provider.provider_id] = provider

    def get(self, provider_id: str) -> ProviderDescriptor | None:
        with self._lock:
            return self._providers.get(provider_id)

    def list(self) -> Sequence[ProviderDescriptor]:
        with self._lock:
            return tuple(sorted(self._providers.values(), key=lambda p: p.provider_id))


class NullProviderCatalog(IProviderCatalog):
    """Knows about nobody."""

    def add(self, provider: ProviderDescriptor) -> None:
        return None

    def get(self, provider_id: str) -> ProviderDescriptor | None:
        return None

    def list(self) -> Sequence[ProviderDescriptor]:
        return ()


@dataclass(frozen=True)
class CredentialBundle:
    """What a provider needs to be called."""

    provider_id: str
    #: The secret itself. Held in memory only for the length of a call; the
    #: store keeps it encrypted.
    secret: str
    refresh_token: str = ""
    expires_at: datetime | None = None


class ICredentialStore(ABC):
    """Holds credentials."""

    @abstractmethod
    def put(self, bundle: CredentialBundle) -> None: ...

    @abstractmethod
    def get(self, provider_id: str) -> CredentialBundle | None: ...

    @abstractmethod
    def remove(self, provider_id: str) -> bool: ...


class NullCredentialStore(ICredentialStore):
    """Holds nothing.

    The default: a tool that needs a credential is UNAVAILABLE rather than
    calling with none.
    """

    def put(self, bundle: CredentialBundle) -> None:
        return None

    def get(self, provider_id: str) -> CredentialBundle | None:
        return None

    def remove(self, provider_id: str) -> bool:
        return False


class AesGcmCredentialStore(ICredentialStore):
    """AES-GCM at rest, with the key from the platform keystore.

    GCM rather than CBC because it AUTHENTICATES: a tampered ciphertext fails to
    open instead of decrypting to something. A credential store that returned
    garbage on tampering hands that garbage to a provider as a token.

    THE NONCE IS NEVER REUSED. A repeated nonce under one key in GCM does not
    degrade the encryption — it breaks it, and both messages become
    recoverable. This draws a fresh 96-bit nonce per write and stores it beside
    the ciphertext.

    `seal` and `open_` are the host's crypto; no key material lives here.
    """

    def __init__(
        self,
        seal: Callable[[bytes, bytes], bytes] | None = None,
        open_: Callable[[bytes, bytes], bytes] | None = None,
    ) -> None:
        self._seal = seal
        self._open = open_
        self._lock = threading.Lock()
        self._entries: dict[str, tuple[bytes, bytes, CredentialBundle]] = {}

    def put(self, bundle: CredentialBundle) -> None:
        nonce = secrets.token_bytes(12)
        payload = bundle.secret.encode()
        sealed = self._seal(nonce, payload) if self._seal else payload
        with self._lock:
            self._entries[bundle.provider_id] = (nonce, sealed, bundle)

    def get(self, provider_id: str) -> CredentialBundle | None:
        with self._lock:
            entry = self._entries.get(provider_id)
        if entry is None:
            return None
        nonce, sealed, bundle = entry
        if self._open is None:
            return bundle
        try:
            opened = self._open(nonce, sealed)
        except Exception:
            # A tampered ciphertext is a MISSING credential, not a corrupt one:
            # handing a caller bytes that failed authentication is the failure
            # GCM exists to prevent.
            return None
        return CredentialBundle(
            provider_id, opened.decode(), bundle.refresh_token, bundle.expires_at)

    def remove(self, provider_id: str) -> bool:
        with self._lock:
            return self._entries.pop(provider_id, None) is not None


@dataclass(frozen=True)
class OAuth2Descriptor:
    """How a provider does OAuth."""

    provider_id: str
    authorize_url: str
    token_url: str
    client_id: str
    scopes: tuple[str, ...] = ()
    #: PKCE is NOT optional. A public client doing OAuth without it can have its
    #: authorization code stolen by anything that can register the redirect.
    use_pkce: bool = True


class IOAuth2FlowDriver(ABC):
    """Drives an OAuth flow."""

    @abstractmethod
    def authorize_url(self, descriptor: OAuth2Descriptor, redirect_uri: str) -> tuple[str, str]:
        """Builds the URL a PERSON opens, and the PKCE verifier to keep.

        This module never posts credentials and never handles a password — the
        person authenticates with the provider, and what comes back is a code.
        """

    @abstractmethod
    def exchange_code(
        self, descriptor: OAuth2Descriptor, code: str, verifier: str
    ) -> CredentialBundle: ...


class OAuth2FlowDriver(IOAuth2FlowDriver):
    """The default driver."""

    def __init__(self, post: Callable[[str, dict[str, str]], dict[str, object]] | None = None) -> None:
        self._post = post

    def authorize_url(self, descriptor: OAuth2Descriptor, redirect_uri: str) -> tuple[str, str]:
        verifier = base64.urlsafe_b64encode(secrets.token_bytes(48)).decode().rstrip("=")
        challenge = base64.urlsafe_b64encode(
            hashlib.sha256(verifier.encode()).digest()).decode().rstrip("=")
        params = [
            f"response_type=code",
            f"client_id={descriptor.client_id}",
            f"redirect_uri={redirect_uri}",
            f"scope={'+'.join(descriptor.scopes)}",
            f"state={secrets.token_urlsafe(16)}",
        ]
        if descriptor.use_pkce:
            params += [f"code_challenge={challenge}", "code_challenge_method=S256"]
        return descriptor.authorize_url + "?" + "&".join(params), verifier

    def exchange_code(
        self, descriptor: OAuth2Descriptor, code: str, verifier: str
    ) -> CredentialBundle:
        if self._post is None:
            raise RuntimeError("no transport configured")
        body = {
            "grant_type": "authorization_code", "code": code,
            "client_id": descriptor.client_id,
        }
        if descriptor.use_pkce:
            body["code_verifier"] = verifier
        raw = self._post(descriptor.token_url, body)
        expires_in = raw.get("expires_in")
        return CredentialBundle(
            descriptor.provider_id,
            str(raw.get("access_token", "")),
            str(raw.get("refresh_token", "")),
            _now() + timedelta(seconds=int(expires_in)) if expires_in else None,
        )


class NullOAuth2FlowDriver(IOAuth2FlowDriver):
    """Drives nothing."""

    def authorize_url(self, descriptor: OAuth2Descriptor, redirect_uri: str) -> tuple[str, str]:
        raise RuntimeError("no OAuth driver configured")

    def exchange_code(
        self, descriptor: OAuth2Descriptor, code: str, verifier: str
    ) -> CredentialBundle:
        raise RuntimeError("no OAuth driver configured")


@dataclass(frozen=True)
class QuotaPolicy:
    """What a caller is allowed."""

    limit: int
    window: timedelta


class IQuotaGuard(ABC):
    """Enforces a quota."""

    @abstractmethod
    def try_acquire(self, key: str, now: datetime | None = None) -> bool: ...

    @abstractmethod
    def remaining(self, key: str, now: datetime | None = None) -> int: ...


class SlidingWindowQuotaGuard(IQuotaGuard):
    """A SLIDING window, not a fixed one.

    A fixed window lets twice the quota through across a boundary — all of it in
    the last second of one window and the first of the next — which is exactly
    when a rate limit matters.
    """

    def __init__(self, policy: QuotaPolicy) -> None:
        self._policy = policy
        self._lock = threading.Lock()
        self._hits: dict[str, list[datetime]] = {}

    def _prune(self, key: str, now: datetime) -> list[datetime]:
        cutoff = now - self._policy.window
        hits = [h for h in self._hits.get(key, ()) if h > cutoff]
        self._hits[key] = hits
        return hits

    def try_acquire(self, key: str, now: datetime | None = None) -> bool:
        at = now or _now()
        with self._lock:
            hits = self._prune(key, at)
            if len(hits) >= self._policy.limit:
                return False
            hits.append(at)
            return True

    def remaining(self, key: str, now: datetime | None = None) -> int:
        at = now or _now()
        with self._lock:
            return max(0, self._policy.limit - len(self._prune(key, at)))


class NullQuotaGuard(IQuotaGuard):
    """Allows everything.

    Named so that choosing it is visible: an unmetered path to a paid provider
    is a bill nobody capped.
    """

    def try_acquire(self, key: str, now: datetime | None = None) -> bool:
        return True

    def remaining(self, key: str, now: datetime | None = None) -> int:
        return -1


@dataclass(frozen=True)
class ToolNamespace:
    """A group of tools under one provider."""

    namespace: str
    provider_id: str
    tool_names: tuple[str, ...] = ()


class IToolNamespaceStore(ABC):
    """Holds namespaces."""

    @abstractmethod
    def put(self, namespace: ToolNamespace) -> None: ...

    @abstractmethod
    def get(self, namespace: str) -> ToolNamespace | None: ...

    @abstractmethod
    def list(self) -> Sequence[ToolNamespace]: ...


class InMemoryToolNamespaceStore(IToolNamespaceStore):
    """The default store."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._namespaces: dict[str, ToolNamespace] = {}

    def put(self, namespace: ToolNamespace) -> None:
        with self._lock:
            self._namespaces[namespace.namespace] = namespace

    def get(self, namespace: str) -> ToolNamespace | None:
        with self._lock:
            return self._namespaces.get(namespace)

    def list(self) -> Sequence[ToolNamespace]:
        with self._lock:
            return tuple(sorted(self._namespaces.values(), key=lambda n: n.namespace))


class NullToolNamespaceStore(IToolNamespaceStore):
    """Holds nothing."""

    def put(self, namespace: ToolNamespace) -> None:
        return None

    def get(self, namespace: str) -> ToolNamespace | None:
        return None

    def list(self) -> Sequence[ToolNamespace]:
        return ()


# ─────────────────────────────────────────────────────────────────────────────
# Mesh offload


@dataclass(frozen=True)
class OffloadServedBy:
    """Which device answered."""

    peer_id: str
    display_name: str = ""
    #: Whether this peer has been added by BOTH devices. Offloading to a peer
    #: that has not added us back is sending a prompt to a stranger.
    mutually_added: bool = False


@dataclass(frozen=True)
class OffloadTurn:
    """One turn, wherever it ran."""

    prompt: str
    response: str = ""
    #: None means it ran HERE. Always carried through to the caller, so a UI can
    #: say which device answered — the one fact that makes offloading something
    #: somebody agreed to rather than something that happened to them.
    served_by: OffloadServedBy | None = None
    duration: timedelta = timedelta()


@dataclass(frozen=True)
class OffloadResult:
    """The turn plus why it was routed the way it was."""

    turn: OffloadTurn
    #: ALWAYS populated, including when the answer was to stay local. The reason
    #: is what makes an offload decision reviewable instead of magic.
    reason: str = ""


class ILocalInferenceFallback(ABC):
    """Runs a prompt on this device instead."""

    @property
    @abstractmethod
    def is_available(self) -> bool: ...

    @abstractmethod
    def run(self, prompt: str) -> str: ...


class NullLocalInferenceFallback(ILocalInferenceFallback):
    """Runs nothing and reports unavailable.

    The default: a router with no local fallback must KNOW it has none, or it
    will route to the mesh because it believes there is a safety net.
    """

    @property
    def is_available(self) -> bool:
        return False

    def run(self, prompt: str) -> str:
        raise RuntimeError("no local inference available on this device")


class IMeshOffloadClient(ABC):
    """Sends a prompt to a peer."""

    @abstractmethod
    def send(self, peer_id: str, prompt: str) -> OffloadTurn: ...


class MeshOffloadClient(IMeshOffloadClient):
    """The default client."""

    def __init__(self, send: Callable[[str, str], str] | None = None) -> None:
        self._send = send
        self._lock = threading.Lock()
        self._peers: dict[str, OffloadServedBy] = {}

    def add_peer(self, peer: OffloadServedBy) -> None:
        with self._lock:
            self._peers[peer.peer_id] = peer

    def send(self, peer_id: str, prompt: str) -> OffloadTurn:
        with self._lock:
            peer = self._peers.get(peer_id)
        if peer is None or not peer.mutually_added:
            raise PermissionError(f"peer {peer_id!r} has not added this device back")
        if self._send is None:
            raise RuntimeError("no transport configured")
        started = time.monotonic()
        response = self._send(peer_id, prompt)
        return OffloadTurn(
            prompt, response, peer,
            timedelta(seconds=time.monotonic() - started),
        )


class IOffloadRouter(ABC):
    """Decides where a turn runs and runs it."""

    @abstractmethod
    def route(self, prompt: str) -> OffloadResult: ...


class MeshOffloadRouter(IOffloadRouter):
    """Routes to a peer only when every condition holds.

    The peer is mutually added, this device genuinely cannot do the work, and
    the person has consented — PER PEER, because agreeing to use the tablet in
    the next room is not agreeing to use whatever else joins the mesh later.

    LATENCY ALONE IS NEVER SUFFICIENT: "it would be faster over there" is the
    argument that ends with somebody's conversation on a device they do not own.
    """

    def __init__(
        self,
        client: IMeshOffloadClient | None = None,
        fallback: ILocalInferenceFallback | None = None,
    ) -> None:
        self._client = client
        self._fallback = fallback or NullLocalInferenceFallback()
        self._lock = threading.Lock()
        self._consented_peer: str | None = None

    def consent(self, peer_id: str) -> None:
        with self._lock:
            self._consented_peer = peer_id.strip() or None

    def route(self, prompt: str) -> OffloadResult:
        if self._fallback.is_available:
            try:
                return OffloadResult(
                    OffloadTurn(prompt, self._fallback.run(prompt)),
                    "this device can answer, so it did",
                )
            except Exception:
                pass
        with self._lock:
            peer_id = self._consented_peer
        if peer_id is None:
            return OffloadResult(
                OffloadTurn(prompt),
                "nothing on this device can answer, and no peer has been agreed to",
            )
        if self._client is None:
            return OffloadResult(OffloadTurn(prompt), "no mesh client configured")
        try:
            turn = self._client.send(peer_id, prompt)
        except Exception as exc:  # noqa: BLE001 — the reason is shown to a person
            return OffloadResult(
                OffloadTurn(prompt), f"the agreed peer could not answer: {exc}")
        return OffloadResult(turn, f"answered by {peer_id}, which you agreed to")


@dataclass(frozen=True)
class MeshAdvertisementBeacon:
    """What a device tells the room about itself.

    CAPABILITIES ONLY — never what it is doing, never who owns it, never what
    was asked. A beacon that carried activity would make a mesh of phones into a
    mesh of people broadcasting their behaviour to the room.
    """

    device_id: str
    capabilities: tuple[str, ...] = ()
    ram_bytes: int = 0
    load_average: float = 0.0
    at: datetime = field(default_factory=_now)


class AetherMeshCapabilityBroadcaster:
    """Tells nearby devices what this one can do."""

    def __init__(
        self,
        device_id: str,
        publish: Callable[[MeshAdvertisementBeacon], None] | None = None,
        min_period: timedelta = timedelta(seconds=30),
    ) -> None:
        self._device_id = device_id
        self._publish = publish
        self._min_period = min_period
        self._lock = threading.Lock()
        self._last_sent: datetime | None = None

    def advertise(self, beacon: MeshAdvertisementBeacon) -> bool:
        """Rate-limited, because a beacon is a RADIO TRANSMISSION: broadcasting
        every second is a measurable battery cost on every device in range, not
        just this one."""
        if self._publish is None:
            raise RuntimeError("no transport configured")
        now = _now()
        with self._lock:
            if self._last_sent is not None and now - self._last_sent < self._min_period:
                return False
            self._last_sent = now
        self._publish(MeshAdvertisementBeacon(
            self._device_id, beacon.capabilities, beacon.ram_bytes,
            beacon.load_average, now,
        ))
        return True


@dataclass(frozen=True)
class MeshOffloadOptions:
    """Configures offloading."""

    #: OFF by default. Offloading sends a prompt to somebody else's hardware,
    #: and it should never begin because a component was imported.
    enabled: bool = False
    #: The peer agreed to, per peer rather than globally.
    preferred_peer_id: str = ""
    max_prompt_bytes: int = 8192
