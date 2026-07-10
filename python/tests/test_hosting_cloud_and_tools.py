"""test_hosting_cloud_and_tools.py

Exercises CloudFallbackChain + BackupBrainOrchestrator (health/cool-down/retry),
the tool catalog (keyword scoring + provider import), the generative-UI JSON
render parser (strict validation + fallback), and the push/aether observer
bridges. Ports of CircleAI.Hosting.{CloudFallback, Tools, GenerativeUI,
PushAIObserver, AetherAIObserver}.
"""
from __future__ import annotations

import datetime as _dt
import json
from typing import List, Optional

import pytest

from circle_ai.hosting import (
    AetherAIObserver,
    AIChatEvent,
    BackupBrainOrchestrator,
    BackupBrainPolicy,
    BrainHealth,
    CloudFallbackChain,
    FakeConfigurableChatGenerator,
    ICircleAetherTransport,
    import_from_async,
    InMemoryToolCatalog,
    IPushNotificationSender,
    IToolProvider,
    JsonRenderParser,
    PushAIObserver,
    ToolDescriptor,
    UiCatalogs,
)

_UTC = _dt.timezone.utc


# ── CloudFallbackChain ──────────────────────────────────────────────────────


async def test_chain_uses_first_ready_generator() -> None:
    g1 = FakeConfigurableChatGenerator("primary", reply="from-primary")
    g2 = FakeConfigurableChatGenerator("backup", reply="from-backup")
    chain = CloudFallbackChain([g1, g2])
    assert await chain.generate_async([]) == "from-primary"


async def test_chain_skips_unconfigured_generator() -> None:
    g1 = FakeConfigurableChatGenerator("primary", configured=False)
    g2 = FakeConfigurableChatGenerator("backup", reply="backup-served")
    chain = CloudFallbackChain([g1, g2])
    assert await chain.generate_async([]) == "backup-served"


async def test_chain_falls_through_on_failure() -> None:
    g1 = FakeConfigurableChatGenerator("primary", fail=True)
    g2 = FakeConfigurableChatGenerator("backup", reply="recovered")
    chain = CloudFallbackChain([g1, g2])
    assert await chain.generate_async([]) == "recovered"


async def test_chain_all_unavailable_returns_sentinel() -> None:
    g1 = FakeConfigurableChatGenerator("a", configured=False)
    g2 = FakeConfigurableChatGenerator("b", configured=False)
    chain = CloudFallbackChain([g1, g2])
    out = await chain.generate_async([])
    assert "no configured generator" in out


async def test_chain_stream_skips_failsoft_and_uses_next() -> None:
    g1 = FakeConfigurableChatGenerator("a", configured=False)  # yields fail-soft frame
    g2 = FakeConfigurableChatGenerator("b", reply="streamed-ok", chunk_size=3)
    chain = CloudFallbackChain([g1, g2])
    chunks = [c async for c in chain.stream_async([])]
    assert "".join(chunks) == "streamed-ok"


# ── BackupBrainOrchestrator ─────────────────────────────────────────────────


def _clock_seq(times: List[_dt.datetime]):
    idx = {"i": 0}

    def _clk() -> _dt.datetime:
        t = times[min(idx["i"], len(times) - 1)]
        idx["i"] += 1
        return t

    return _clk


async def test_orchestrator_uses_primary_when_healthy() -> None:
    g1 = FakeConfigurableChatGenerator("primary", reply="p")
    g2 = FakeConfigurableChatGenerator("backup", reply="b")
    orch = BackupBrainOrchestrator([g1, g2])
    assert await orch.generate_async([]) == "p"


async def test_orchestrator_fails_over_to_backup() -> None:
    g1 = FakeConfigurableChatGenerator("primary", fail=True)
    g2 = FakeConfigurableChatGenerator("backup", reply="b")
    orch = BackupBrainOrchestrator([g1, g2])
    assert await orch.generate_async([]) == "b"


async def test_orchestrator_all_fail_returns_sentinel() -> None:
    g1 = FakeConfigurableChatGenerator("a", fail=True)
    g2 = FakeConfigurableChatGenerator("b", fail=True)
    orch = BackupBrainOrchestrator([g1, g2], BackupBrainPolicy(max_retries_per_turn=2))
    assert await orch.generate_async([]) == "[All brains failed.]"


async def test_orchestrator_marks_degraded_after_threshold() -> None:
    base = _dt.datetime(2026, 7, 8, 12, 0, tzinfo=_UTC)
    g1 = FakeConfigurableChatGenerator("primary", fail=True)
    g2 = FakeConfigurableChatGenerator("backup", reply="b")
    # threshold=2: primary must fail twice to become degraded.
    orch = BackupBrainOrchestrator(
        [g1, g2],
        BackupBrainPolicy(degraded_after_failures=2, max_retries_per_turn=3),
        clock=lambda: base,
    )
    await orch.generate_async([])  # primary fails once, backup serves
    await orch.generate_async([])  # primary fails again → now degraded
    statuses = {s.label: s.health for s in orch.statuses}
    assert statuses["primary · fake"] == BrainHealth.DEGRADED


async def test_orchestrator_cooldown_transitions_to_cooling_down() -> None:
    base = _dt.datetime(2026, 7, 8, 12, 0, tzinfo=_UTC)
    later = base + _dt.timedelta(seconds=31)  # past the 30s cool-down
    times = [base, base, base, later, later, later, later, later]
    g1 = FakeConfigurableChatGenerator("primary", fail=True)
    g2 = FakeConfigurableChatGenerator("backup", reply="b")
    orch = BackupBrainOrchestrator(
        [g1, g2],
        BackupBrainPolicy(degraded_after_failures=1, max_retries_per_turn=3),
        clock=_clock_seq(times),
    )
    await orch.generate_async([])  # primary fails → degraded (threshold 1)
    # After cool-down elapses, health reads CoolingDown (half-open).
    statuses = {s.label: s.health for s in orch.statuses}
    assert statuses["primary · fake"] == BrainHealth.COOLING_DOWN


async def test_orchestrator_rejects_empty_brains() -> None:
    with pytest.raises(ValueError):
        BackupBrainOrchestrator([])


# ── InMemoryToolCatalog ─────────────────────────────────────────────────────


def _tool(name: str, desc: str = "", provider: str = "local", tags=None) -> ToolDescriptor:
    return ToolDescriptor(name=name, description=desc, provider=provider, tags=tags)


async def test_catalog_upsert_get_count_remove() -> None:
    cat = InMemoryToolCatalog()
    await cat.upsert_async(_tool("search"))
    assert cat.count == 1
    assert (await cat.get_async("search")).name == "search"
    # Case-insensitive name → upserting "SEARCH" replaces.
    await cat.upsert_async(_tool("SEARCH", desc="v2"))
    assert cat.count == 1
    assert (await cat.get_async("search")).description == "v2"
    assert await cat.remove_async("search") is True
    assert await cat.remove_async("search") is False
    assert cat.count == 0


async def test_catalog_search_scores_name_over_desc() -> None:
    cat = InMemoryToolCatalog()
    await cat.upsert_async(_tool("weather", desc="unrelated"))
    await cat.upsert_async(_tool("misc", desc="check the weather forecast"))
    results = cat.search("weather")
    # name match (+5) ranks above description-only match (+2).
    assert results[0].name == "weather"
    assert {r.name for r in results} == {"weather", "misc"}


async def test_catalog_search_matches_tags() -> None:
    cat = InMemoryToolCatalog()
    await cat.upsert_async(_tool("t1", tags=["finance", "money"]))
    results = cat.search("finance")
    assert [r.name for r in results] == ["t1"]


async def test_catalog_search_empty_query_returns_nothing() -> None:
    cat = InMemoryToolCatalog()
    await cat.upsert_async(_tool("t"))
    assert cat.search("") == []
    assert cat.search("t", top_k=0) == []


async def test_catalog_list_by_provider() -> None:
    cat = InMemoryToolCatalog()
    await cat.upsert_async(_tool("a", provider="local"))
    await cat.upsert_async(_tool("b", provider="mcp"))
    assert [t.name for t in cat.list_by_provider("local")] == ["a"]
    assert [t.name for t in cat.list_by_provider("MCP")] == ["b"]  # case-insensitive


async def test_catalog_import_from_provider() -> None:
    class _Provider(IToolProvider):
        @property
        def provider_id(self) -> str:
            return "vendor"

        async def discover_async(self, ct: object = None):
            return [_tool("x", provider="vendor"), _tool("y", provider="vendor")]

        async def is_available_async(self, ct: object = None) -> bool:
            return True

    cat = InMemoryToolCatalog()
    n = await import_from_async(cat, _Provider())
    assert n == 2
    assert cat.count == 2


# ── JsonRenderParser ────────────────────────────────────────────────────────


def test_render_parser_parses_valid_component() -> None:
    js = json.dumps(
        {"kind": "textBlock", "properties": {"text": "hi", "markdown": True}}
    )
    comp = JsonRenderParser.parse(js, UiCatalogs.default)
    assert comp.kind == "textBlock"
    assert comp.properties["text"] == "hi"
    assert comp.properties["markdown"] is True


def test_render_parser_nested_children() -> None:
    js = json.dumps(
        {
            "kind": "card",
            "properties": {"title": "T"},
            "children": [{"kind": "textBlock", "properties": {"text": "body"}}],
        }
    )
    comp = JsonRenderParser.parse(js, UiCatalogs.default)
    assert comp.children is not None
    assert comp.children[0].kind == "textBlock"


def test_render_parser_strict_rejects_unknown_kind() -> None:
    js = json.dumps({"kind": "nope", "properties": {}})
    with pytest.raises(ValueError):
        JsonRenderParser.parse(js, UiCatalogs.default, strict=True)


def test_render_parser_lenient_unknown_kind_becomes_textblock() -> None:
    js = json.dumps({"kind": "nope", "properties": {}})
    comp = JsonRenderParser.parse(js, UiCatalogs.default, strict=False)
    assert comp.kind == "textBlock"
    assert "unknown kind 'nope'" in comp.properties["text"]


def test_render_parser_strict_rejects_undeclared_property() -> None:
    js = json.dumps({"kind": "button", "properties": {"label": "x", "bogus": 1}})
    with pytest.raises(ValueError):
        JsonRenderParser.parse(js, UiCatalogs.default, strict=True)


def test_render_parser_rejects_missing_kind() -> None:
    with pytest.raises(ValueError):
        JsonRenderParser.parse(json.dumps({"properties": {}}), UiCatalogs.default)


def test_render_parser_rejects_empty_input() -> None:
    with pytest.raises(ValueError):
        JsonRenderParser.parse("", UiCatalogs.default)


def test_render_parser_number_coercion() -> None:
    # C# ToManaged: whole numbers -> int64, fractional -> double.
    js = json.dumps({"kind": "textBlock", "properties": {"text": "t"}})
    # Use a catalog that allows arbitrary props to test number coercion directly
    from circle_ai.hosting import UiCatalogEntry

    cat = [UiCatalogEntry("n", "num", {"whole": "number", "frac": "number"})]
    comp = JsonRenderParser.parse(
        json.dumps({"kind": "n", "properties": {"whole": 3, "frac": 2.5}}), cat
    )
    assert comp.properties["whole"] == 3 and isinstance(comp.properties["whole"], int)
    assert comp.properties["frac"] == 2.5


def test_render_parser_describe_catalog_for_prompt() -> None:
    text = JsonRenderParser.describe_catalog_for_prompt(UiCatalogs.default)
    assert "Allowed kinds:" in text
    assert "- card —" in text
    assert text.endswith("\n")


# ── observers ───────────────────────────────────────────────────────────────


class _RecordingSender(IPushNotificationSender):
    def __init__(self) -> None:
        self.sent: List[tuple] = []

    async def send_async(self, device_token: str, title: str, body: str, ct: object = None) -> None:
        self.sent.append((device_token, title, body))


def _chat_event(response: str) -> AIChatEvent:
    import uuid

    return AIChatEvent(uuid.uuid4(), [], response, _dt.timedelta(0), _dt.datetime.now(_UTC))


async def test_push_observer_sends_response() -> None:
    sender = _RecordingSender()
    obs = PushAIObserver(sender, "device-1")
    await obs.on_chat_completed_async(_chat_event("hello world"))
    assert sender.sent == [("device-1", "B!", "hello world")]


async def test_push_observer_truncates_long_body() -> None:
    sender = _RecordingSender()
    obs = PushAIObserver(sender, "d")
    long = "x" * 250
    await obs.on_chat_completed_async(_chat_event(long))
    _, _, body = sender.sent[0]
    assert body == "x" * 100 + "…"


async def test_push_observer_on_error() -> None:
    sender = _RecordingSender()
    obs = PushAIObserver(sender, "d")
    await obs.on_error(RuntimeError("boom"))
    assert sender.sent[0][1] == "B! Error"
    assert "boom" in sender.sent[0][2]


def test_push_observer_rejects_blank_token() -> None:
    with pytest.raises(ValueError):
        PushAIObserver(_RecordingSender(), "")


class _RecordingTransport(ICircleAetherTransport):
    def __init__(self) -> None:
        self.published: List[tuple] = []

    async def publish_async(self, topic: str, payload: bytes, ct: object = None) -> None:
        self.published.append((topic, payload))


async def test_aether_observer_publishes_response() -> None:
    transport = _RecordingTransport()
    obs = AetherAIObserver(transport)
    await obs.on_chat_completed_async(_chat_event("mesh-reply"))
    topic, payload = transport.published[0]
    assert topic == "butler/response"
    assert json.loads(payload.decode("utf-8"))["response"] == "mesh-reply"


async def test_aether_observer_publishes_error() -> None:
    transport = _RecordingTransport()
    obs = AetherAIObserver(transport)
    await obs.on_error(ValueError("bad"))
    topic, payload = transport.published[0]
    assert topic == "butler/error"
    parsed = json.loads(payload.decode("utf-8"))
    assert parsed["error"] == "ValueError"
    assert parsed["message"] == "bad"
