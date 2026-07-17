"""test_neuron.py — the Python Neuron port.

Mirrors the C# CircleAI.Tests Neuron suite: the concierge decision table + gate,
the two-slot admission gate + eviction, the router-gated slot selection inside
AIService (specialist hot-load, generalist floor), and the NeuronNode facade.
"""
from __future__ import annotations

import os

from circle_ai.device.device_probe import DeviceTier
from circle_ai.hosting import (
    AIOptions,
    AIService,
    ChatTurn,
    HeuristicNeuronRouter,
    NeuronGate,
    NeuronNode,
    NullChatRuntime,
    Organ,
    ResidentSlotManager,
    RouteContext,
    RouteDecision,
    SlotOutcome,
)
from circle_ai.inference.inference import ChatCapability, ModelSelection


# ── test doubles ─────────────────────────────────────────────────────────────


class _FixedGen:
    """Minimal IChatGenerator that returns a fixed reply and records disposal."""

    def __init__(self, reply: str) -> None:
        self._reply = reply
        self.disposed = False

    async def generate_async(self, messages, options=None) -> str:
        return self._reply

    async def stream_async(self, messages, options=None):
        yield self._reply

    async def save_session_async(self, path: str) -> bool:
        with open(path, "w", encoding="utf-8") as fh:
            fh.write("circleai-session-marker\n")
        return True

    async def load_session_async(self, path: str) -> bool:
        if not os.path.isfile(path):
            return False
        with open(path, "r", encoding="utf-8") as fh:
            return fh.read().startswith("circleai-session-marker")

    def dispose(self) -> None:
        self.disposed = True


class _FixedRouter:
    def __init__(self, decision: RouteDecision) -> None:
        self._decision = decision

    def route(self, context: RouteContext) -> RouteDecision:
        return self._decision


class _FixedSelector:
    def __init__(self, selection: ModelSelection) -> None:
        self._selection = selection
        self.calls = 0

    def best_fit(self, probe, required: ChatCapability = ChatCapability.DEFAULT) -> ModelSelection:
        self.calls += 1
        return self._selection

    def all_candidates(self, probe):
        return [self._selection]


def _sel(model_id: str, estimated_bytes: int) -> ModelSelection:
    return ModelSelection(model_id, False, estimated_bytes, DeviceTier.DESKTOP)


# ── concierge router + gate ──────────────────────────────────────────────────


def test_plain_query_routes_to_generalist() -> None:
    d = HeuristicNeuronRouter().route(RouteContext("what's the weather today?"))
    assert d.organ == Organ.GENERALIST
    assert d.capability == ChatCapability.DEFAULT


def test_image_routes_to_vision_specialist() -> None:
    d = HeuristicNeuronRouter().route(RouteContext("what is this?", has_image=True))
    assert d.organ == Organ.SPECIALIST
    assert d.capability == ChatCapability.VISION


def test_reasoning_cue_routes_to_reasoning_specialist() -> None:
    d = HeuristicNeuronRouter().route(RouteContext("please debug this stack trace"))
    assert d.organ == Organ.SPECIALIST
    assert d.capability == ChatCapability.REASONING


def test_long_prompt_routes_to_long_context_specialist() -> None:
    d = HeuristicNeuronRouter(long_context_chars=50).route(RouteContext("x" * 60))
    assert d.organ == Organ.SPECIALIST
    assert d.capability == ChatCapability.LONG_CONTEXT


def test_gate_vetoes_specialist() -> None:
    router = HeuristicNeuronRouter(NeuronGate(allow_specialist=lambda q: False))
    d = router.route(RouteContext("solve this equation"))
    assert d.organ == Organ.GENERALIST


# ── resident slot manager ────────────────────────────────────────────────────


async def test_slot_admits_within_budget() -> None:
    mgr = ResidentSlotManager(1_000, lambda: 1_000_000)
    g = _FixedGen("S")
    a = await mgr.ensure_specialist_async(_sel("spec", 5_000), lambda mid: g)
    assert a.outcome == SlotOutcome.ADMITTED
    assert a.generator is g
    assert mgr.resident_specialist_model_id == "spec"


async def test_slot_denies_over_budget() -> None:
    mgr = ResidentSlotManager(900_000, lambda: 1_000_000)
    a = await mgr.ensure_specialist_async(_sel("spec", 500_000), lambda mid: _FixedGen("S"))
    assert a.outcome == SlotOutcome.INSUFFICIENT_RAM
    assert a.generator is None
    assert mgr.resident_specialist_model_id is None


async def test_slot_already_resident_does_not_rebuild() -> None:
    mgr = ResidentSlotManager(0, lambda: 1_000_000)
    builds: list[str] = []

    def build(mid: str) -> _FixedGen:
        builds.append(mid)
        return _FixedGen("S")

    await mgr.ensure_specialist_async(_sel("spec", 1), build)
    second = await mgr.ensure_specialist_async(_sel("spec", 1), build)
    assert second.outcome == SlotOutcome.ALREADY_RESIDENT
    assert len(builds) == 1


async def test_slot_swap_evicts_incumbent() -> None:
    mgr = ResidentSlotManager(0, lambda: 1_000_000)
    a, b = _FixedGen("A"), _FixedGen("B")
    await mgr.ensure_specialist_async(_sel("A", 1), lambda mid: a)
    await mgr.ensure_specialist_async(_sel("B", 1), lambda mid: b)
    assert a.disposed is True
    assert b.disposed is False
    assert mgr.resident_specialist_model_id == "B"


async def test_slot_build_failure_leaves_slot_empty() -> None:
    mgr = ResidentSlotManager(0, lambda: 1_000_000)

    def boom(mid: str):
        raise RuntimeError("boom")

    a = await mgr.ensure_specialist_async(_sel("spec", 1), boom)
    assert a.outcome == SlotOutcome.BUILD_FAILED
    assert mgr.resident_specialist_model_id is None


async def test_slot_evict_disposes_and_empties() -> None:
    mgr = ResidentSlotManager(0, lambda: 1_000_000)
    g = _FixedGen("S")
    await mgr.ensure_specialist_async(_sel("spec", 1), lambda mid: g)
    await mgr.evict_specialist_async()
    assert g.disposed is True
    assert mgr.resident_specialist_model_id is None


# ── AIService two-slot residency ─────────────────────────────────────────────


async def test_router_none_uses_generalist() -> None:
    svc = AIService(AIOptions(model_id="gen", warm_on_start=False), generator=_FixedGen("GEN"))
    await svc.start_async()
    assert await svc.ask_async("solve this equation") == "GEN"  # reasoning cue, no router


async def test_router_hot_loads_specialist() -> None:
    gen, spec = _FixedGen("GEN"), _FixedGen("SPEC")
    opts = AIOptions(
        model_id="gen",
        warm_on_start=False,
        router=_FixedRouter(RouteDecision.specialist(ChatCapability.REASONING, "t")),
        model_selector=_FixedSelector(_sel("spec-model", 1_024)),
        specialist_factory=lambda mid: spec,
    )
    svc = AIService(opts, generator=gen)
    await svc.start_async()
    assert await svc.ask_async("anything") == "SPEC"


async def test_best_fit_equals_generalist_uses_generalist() -> None:
    gen, spec = _FixedGen("GEN"), _FixedGen("SPEC")
    opts = AIOptions(
        model_id="gen-model",
        warm_on_start=False,
        router=_FixedRouter(RouteDecision.specialist(ChatCapability.REASONING, "t")),
        model_selector=_FixedSelector(_sel("gen-model", 1_024)),
        specialist_factory=lambda mid: spec,
    )
    svc = AIService(opts, generator=gen)
    await svc.start_async()
    assert await svc.ask_async("anything") == "GEN"


async def test_session_round_trip(tmp_path) -> None:
    svc = AIService(AIOptions(model_id="gen", warm_on_start=False), generator=_FixedGen("GEN"))
    await svc.start_async()
    snap = str(tmp_path / "active.session")
    assert await svc.save_session_async(snap) is True
    assert await svc.load_session_async(snap) is True


# ── NeuronNode facade + NullChatRuntime ──────────────────────────────────────


async def test_neuron_node_over_brain(tmp_path) -> None:
    svc = AIService(AIOptions(model_id="qwen-x", warm_on_start=False), generator=_FixedGen("hello"))
    node = NeuronNode(svc)

    assert node.id == "circleai-neuron"
    assert node.is_ready is False
    assert node.status_message == "loading model…"

    await svc.start_async()
    assert node.is_ready is True
    assert node.status_message == "ready"
    assert "qwen-x" in node.engine_label

    chunks = [c async for c in node.stream_async([ChatTurn("user", "hi")])]
    assert "".join(chunks) == "hello"

    snap = str(tmp_path / "active.session")
    assert await node.save_session_async(snap) is True
    assert await node.load_session_async(snap) is True
    assert node.session_snapshot_path


async def test_null_chat_runtime_yields_offline_status() -> None:
    null = NullChatRuntime()
    assert null.is_ready is False
    chunks = [c async for c in null.stream_async([ChatTurn("user", "hi")])]
    assert "No chat engine" in "".join(chunks)
