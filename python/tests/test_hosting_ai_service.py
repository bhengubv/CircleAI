"""test_hosting_ai_service.py

Exercises the core hosting runtime — AIService (lifecycle, ask/chat/stream,
agentic tool loop, tool-call parsing, observer events, episodic + persona
writes), FallbackAIService (local-preferred / cloud-on-low-RAM), the in-memory
loopback endpoint + AIHttpClient, and InProcessEndpoint. Ports of
CircleAI.Hosting.{AIService, FallbackAIService, Endpoints}.
"""
from __future__ import annotations

from typing import List, Optional, Sequence

import pytest

from circle_ai.hosting import (
    AIApiClient,
    AIHttpClient,
    AIOptions,
    AIService,
    FallbackAIService,
    HttpLoopbackEndpoint,
    IAIObserver,
    IAvailableRamProbe,
    InProcessButlerApiTransport,
    InProcessEndpoint,
    parse_tool_call,
)
from circle_ai.inference import DeterministicChatGenerator, GenerationOptions
from circle_ai.models.models import ChatMessage
from circle_ai.tools.tool_types import IToolBridge, ToolInvocation, ToolResult


# ── fakes ─────────────────────────────────────────────────────────────────


class _ScriptedGenerator:
    """IChatGenerator returning a fixed reply (so agentic tool-call parsing is
    deterministic). Streams the reply in fixed chunks.
    """

    def __init__(self, reply: str) -> None:
        self.reply = reply
        self.calls = 0

    async def generate_async(
        self, messages: Sequence[ChatMessage], options: Optional[GenerationOptions] = None
    ) -> str:
        self.calls += 1
        return self.reply

    async def stream_async(
        self, messages: Sequence[ChatMessage], options: Optional[GenerationOptions] = None
    ):
        for i in range(0, len(self.reply), 3):
            yield self.reply[i : i + 3]


class _SequenceGenerator:
    """Returns a scripted list of replies in order (for the agentic loop)."""

    def __init__(self, replies: List[str]) -> None:
        self._replies = replies
        self.calls = 0

    async def generate_async(self, messages, options=None) -> str:
        r = self._replies[min(self.calls, len(self._replies) - 1)]
        self.calls += 1
        return r

    async def stream_async(self, messages, options=None):  # pragma: no cover
        yield self._replies[0]


class _EchoToolBridge(IToolBridge):
    def __init__(self) -> None:
        self.invocations: List[ToolInvocation] = []

    @property
    def available_tools(self):
        return []

    async def invoke_async(
        self, invocation: ToolInvocation, *, ct: Optional[object] = None
    ) -> ToolResult:
        self.invocations.append(invocation)
        return ToolResult(tool_name=invocation.tool_name, success=True, result={"ok": True})


class _RecordingObserver(IAIObserver):
    def __init__(self) -> None:
        self.chat_completed = 0
        self.stream_started = 0
        self.stream_completed = 0
        self.tool_invoked = 0
        self.started = 0
        self.stopped = 0
        self.model_fetching: List[tuple] = []

    async def on_started_async(self, ct=None) -> None:
        self.started += 1

    async def on_stopped_async(self, ct=None) -> None:
        self.stopped += 1

    async def on_chat_completed_async(self, event, ct=None) -> None:
        self.chat_completed += 1

    async def on_stream_started_async(self, event, ct=None) -> None:
        self.stream_started += 1

    async def on_stream_completed_async(self, event, ct=None) -> None:
        self.stream_completed += 1

    async def on_tool_invoked_async(self, event, ct=None) -> None:
        self.tool_invoked += 1

    async def on_model_fetching_async(self, model_id, auto_selected, ct=None) -> None:
        self.model_fetching.append((model_id, auto_selected))


def _opts(**kw) -> AIOptions:
    kw.setdefault("warm_on_start", False)
    return AIOptions(**kw)


# ── construction ────────────────────────────────────────────────────────────


def test_requires_generator_or_factory() -> None:
    with pytest.raises(ValueError):
        AIService(_opts())


def test_rejects_none_options() -> None:
    with pytest.raises(ValueError):
        AIService(None, generator=DeterministicChatGenerator("m"))  # type: ignore[arg-type]


# ── lifecycle ────────────────────────────────────────────────────────────


async def test_start_sets_ready_and_fires_observer() -> None:
    obs = _RecordingObserver()
    svc = AIService(_opts(observer=obs), generator=DeterministicChatGenerator("m1"))
    assert svc.is_ready is False
    await svc.start_async()
    assert svc.is_ready is True
    assert obs.started == 1
    assert obs.model_fetching == [("", False)]  # model_id None → "" auto_selected False
    await svc.dispose_async()
    assert svc.is_ready is False


async def test_start_is_idempotent() -> None:
    obs = _RecordingObserver()
    svc = AIService(_opts(observer=obs), generator=DeterministicChatGenerator("m1"))
    await svc.start_async()
    await svc.start_async()
    assert obs.started == 1


async def test_generator_factory_builds_on_start() -> None:
    built = {"n": 0}

    def factory():
        built["n"] += 1
        return DeterministicChatGenerator("factory-model")

    svc = AIService(_opts(), generator_factory=factory)
    assert built["n"] == 0
    await svc.start_async()
    assert built["n"] == 1
    assert svc.is_ready is True


async def test_disposed_service_raises() -> None:
    svc = AIService(_opts(), generator=DeterministicChatGenerator("m1"))
    await svc.dispose_async()
    with pytest.raises(RuntimeError):
        await svc.start_async()


# ── ask / chat / stream ─────────────────────────────────────────────────


async def test_ask_returns_text_and_autostarts() -> None:
    svc = AIService(_opts(), generator=DeterministicChatGenerator("m1"))
    out = await svc.ask_async("hello")
    assert isinstance(out, str) and len(out) > 0
    assert svc.is_ready is True  # ask auto-started


async def test_ask_rejects_blank() -> None:
    svc = AIService(_opts(), generator=DeterministicChatGenerator("m1"))
    with pytest.raises(ValueError):
        await svc.ask_async("   ")


async def test_chat_fires_chat_completed() -> None:
    obs = _RecordingObserver()
    svc = AIService(_opts(observer=obs), generator=_ScriptedGenerator("hi there"))
    out = await svc.chat_async([ChatMessage("user", "yo")])
    assert out == "hi there"
    assert obs.chat_completed == 1


async def test_stream_yields_and_fires_stream_events() -> None:
    obs = _RecordingObserver()
    svc = AIService(_opts(observer=obs), generator=_ScriptedGenerator("abcdef"))
    chunks = [c async for c in svc.stream_async([ChatMessage("user", "go")])]
    assert "".join(chunks) == "abcdef"
    assert obs.stream_started == 1
    assert obs.stream_completed == 1


async def test_caller_system_message_is_preserved() -> None:
    # When the caller supplies a system message, the enriched prompt is NOT
    # injected — the messages pass through unchanged (verified via a capture gen).
    captured: List[Sequence[ChatMessage]] = []

    class _CaptureGen(_ScriptedGenerator):
        async def generate_async(self, messages, options=None) -> str:
            captured.append(list(messages))
            return "ok"

    svc = AIService(_opts(system_prompt="SP"), generator=_CaptureGen("ok"))
    await svc.chat_async([ChatMessage("system", "custom"), ChatMessage("user", "hi")])
    roles = [(m.role, m.content) for m in captured[0]]
    assert roles == [("system", "custom"), ("user", "hi")]


async def test_no_system_message_injects_enriched_prompt() -> None:
    captured: List[Sequence[ChatMessage]] = []

    class _CaptureGen(_ScriptedGenerator):
        async def generate_async(self, messages, options=None) -> str:
            captured.append(list(messages))
            return "ok"

    svc = AIService(_opts(system_prompt="SYSTEM-PROMPT"), generator=_CaptureGen("ok"))
    await svc.chat_async([ChatMessage("user", "hi")])
    assert captured[0][0].role == "system"
    assert "SYSTEM-PROMPT" in captured[0][0].content


# ── tool-call parsing ─────────────────────────────────────────────────────


def test_parse_tool_call_name_and_arguments() -> None:
    resp = 'prefix <tool_call>{"name": "search", "arguments": {"q": "cats"}}</tool_call> suffix'
    inv = parse_tool_call(resp)
    assert inv is not None
    assert inv.tool_name == "search"
    assert inv.arguments["q"] == "cats"


def test_parse_tool_call_tool_name_spelling() -> None:
    inv = parse_tool_call('<tool_call>{"tool_name": "x"}</tool_call>')
    assert inv is not None and inv.tool_name == "x"


def test_parse_tool_call_none_when_absent() -> None:
    assert parse_tool_call("just text") is None
    assert parse_tool_call("") is None
    assert parse_tool_call("<tool_call>not json</tool_call>") is None


def test_parse_tool_call_non_string_arg_becomes_raw_json() -> None:
    inv = parse_tool_call('<tool_call>{"name":"f","arguments":{"n":5,"b":true}}</tool_call>')
    assert inv is not None
    # C#: non-string values keep their raw JSON text.
    assert inv.arguments["n"] == "5"
    assert inv.arguments["b"] == "true"


# ── invoke tool ─────────────────────────────────────────────────────────


async def test_invoke_tool_without_bridge_returns_failure() -> None:
    obs = _RecordingObserver()
    svc = AIService(_opts(observer=obs), generator=DeterministicChatGenerator("m"))
    res = await svc.invoke_tool_async(ToolInvocation(tool_name="t", arguments={}))
    assert res.success is False
    assert "No tool bridge" in res.error
    assert obs.tool_invoked == 1


async def test_invoke_tool_with_bridge_succeeds() -> None:
    bridge = _EchoToolBridge()
    svc = AIService(_opts(tool_bridge=bridge), generator=DeterministicChatGenerator("m"))
    res = await svc.invoke_tool_async(ToolInvocation(tool_name="echo", arguments={"a": 1}))
    assert res.success is True
    assert len(bridge.invocations) == 1


# ── agentic loop ─────────────────────────────────────────────────────────


async def test_agentic_executes_tool_then_returns_plain_text() -> None:
    bridge = _EchoToolBridge()
    # 1st reply asks for a tool; 2nd reply is plain text → loop ends.
    gen = _SequenceGenerator(
        ['<tool_call>{"name": "echo", "arguments": {"x": 1}}</tool_call>', "final answer"]
    )
    svc = AIService(
        _opts(tool_bridge=bridge, agentic_max_iterations=5), generator=gen
    )
    out = await svc.agentic_chat_async("do a thing")
    assert out == "final answer"
    assert len(bridge.invocations) == 1
    assert bridge.invocations[0].tool_name == "echo"


async def test_agentic_stops_at_max_iterations() -> None:
    bridge = _EchoToolBridge()
    # Always asks for a tool → loop bounded by max_iterations.
    gen = _SequenceGenerator(['<tool_call>{"name": "echo"}</tool_call>'])
    svc = AIService(_opts(tool_bridge=bridge, agentic_max_iterations=3), generator=gen)
    out = await svc.agentic_chat_async("loop")
    assert gen.calls == 3
    assert len(bridge.invocations) == 3


async def test_agentic_without_bridge_degrades_gracefully() -> None:
    gen = _SequenceGenerator(
        ['<tool_call>{"name": "x"}</tool_call>', "answer without tools"]
    )
    svc = AIService(_opts(agentic_max_iterations=5), generator=gen)  # no bridge
    out = await svc.agentic_chat_async("go")
    assert out == "answer without tools"


async def test_agentic_rejects_blank_prompt() -> None:
    svc = AIService(_opts(), generator=DeterministicChatGenerator("m"))
    with pytest.raises(ValueError):
        await svc.agentic_chat_async("")


# ── FallbackAIService ─────────────────────────────────────────────────────


class _FixedRamProbe(IAvailableRamProbe):
    def __init__(self, bytes_available: int) -> None:
        self._b = bytes_available

    def available_ram_bytes(self) -> int:
        return self._b


def _local() -> AIService:
    return AIService(_opts(), generator=_ScriptedGenerator("local-reply"))


def _cloud() -> AIApiClient:
    # In-process transport forwarding to a second local service = deterministic "cloud".
    cloud_svc = AIService(_opts(), generator=_ScriptedGenerator("cloud-reply"))
    return AIApiClient(InProcessButlerApiTransport(cloud_svc))


async def test_fallback_uses_local_when_ram_sufficient() -> None:
    fb = FallbackAIService(
        _local(), _cloud(), ram_threshold_bytes=1000, ram_probe=_FixedRamProbe(5000)
    )
    await fb.start_async()
    assert await fb.ask_async("hi") == "local-reply"
    await fb.dispose_async()


async def test_fallback_uses_cloud_when_ram_below_threshold() -> None:
    fb = FallbackAIService(
        _local(), _cloud(), ram_threshold_bytes=10_000, ram_probe=_FixedRamProbe(500)
    )
    await fb.start_async()
    assert await fb.ask_async("hi") == "cloud-reply"
    await fb.dispose_async()


async def test_fallback_requires_start_before_use() -> None:
    fb = FallbackAIService(
        _local(), _cloud(), ram_threshold_bytes=1, ram_probe=_FixedRamProbe(5)
    )
    with pytest.raises(RuntimeError):
        await fb.ask_async("hi")


async def test_fallback_falls_to_cloud_when_local_start_throws() -> None:
    class _ThrowingLocal(AIService):
        async def start_async(self, ct: object = None) -> None:
            raise RuntimeError("local boom")

    local = _ThrowingLocal(_opts(), generator=_ScriptedGenerator("never"))
    fb = FallbackAIService(local, _cloud(), ram_threshold_bytes=1, ram_probe=_FixedRamProbe(999))
    await fb.start_async()
    assert await fb.ask_async("hi") == "cloud-reply"


# ── endpoints ─────────────────────────────────────────────────────────────


async def test_inprocess_endpoint_exposes_service() -> None:
    svc = AIService(_opts(), generator=_ScriptedGenerator("x"))
    ep = InProcessEndpoint()
    await ep.start_async(svc)
    assert ep.service_accessor is svc
    await ep.stop_async()
    assert ep.service_accessor is None


async def test_loopback_endpoint_ask_chat_roundtrip() -> None:
    svc = AIService(_opts(), generator=_ScriptedGenerator("reply-42"))
    opts = _opts()
    ep = HttpLoopbackEndpoint(opts)
    await ep.start_async(svc)
    assert ep.token is not None
    client = AIHttpClient(ep, ep.token)
    assert await client.ask_async("q") == "reply-42"
    assert await client.chat_async([ChatMessage("user", "q")]) == "reply-42"
    await ep.dispose_async()


async def test_loopback_endpoint_stream_roundtrip() -> None:
    svc = AIService(_opts(), generator=_ScriptedGenerator("streamme"))
    ep = HttpLoopbackEndpoint(_opts())
    await ep.start_async(svc)
    client = AIHttpClient(ep, ep.token)
    chunks = [c async for c in client.stream_async([ChatMessage("user", "q")])]
    assert "".join(chunks) == "streamme"
    await ep.dispose_async()


async def test_loopback_endpoint_rejects_bad_token() -> None:
    svc = AIService(_opts(), generator=_ScriptedGenerator("x"))
    ep = HttpLoopbackEndpoint(_opts())
    await ep.start_async(svc)
    bad = AIHttpClient(ep, "wrong-token")
    with pytest.raises(RuntimeError):
        await bad.ask_async("q")
    await ep.dispose_async()


async def test_loopback_endpoint_tool_route() -> None:
    bridge = _EchoToolBridge()
    svc = AIService(_opts(tool_bridge=bridge), generator=_ScriptedGenerator("x"))
    ep = HttpLoopbackEndpoint(_opts())
    await ep.start_async(svc)
    client = AIHttpClient(ep, ep.token)
    res = await client.invoke_tool_async(ToolInvocation(tool_name="echo", arguments={"a": 1}))
    assert res.success is True
    assert res.tool_name == "echo"
    await ep.dispose_async()
